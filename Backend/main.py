"""
Object Spawning backend -- Stage 2/3/4/5/6.

Exposes /parse-intent, which turns a raw voice transcript into the small structured
JSON schema the Unity client already understands (create a shape, generate a novel mesh,
or edit an existing object -- resize/recolor/move/rotate/duplicate/delete), using Gemini's
function-calling to keep the LLM's output contained to a schema we control. Which object
an edit action applies to is never part of this schema -- that's resolved client-side
(pointing, else last object touched), not something the LLM is asked to figure out from
the transcript. Stage 5's spatial relations (on/next_to) work the same way: the LLM only
names the relation and the referenced object's TYPE, never a specific instance -- resolving
which actual object that means (the most recent of that type) is also client-side.

Stage 6 adds /generate-mesh and /generation-status/{job_id}, wrapping Tripo3D's
text-to-3D task API (single-stage -- texture is generated in the same task, unlike some
competitors' separate preview/refine split) behind a job id the Unity client polls. Job
state advances lazily, whenever a poll request happens to notice the task finished -- no
background scheduler or thread needed, since the client is already polling on its own
cadence.
"""
import logging
import os
import time

import requests
from dotenv import load_dotenv
from fastapi import FastAPI, HTTPException
from pydantic import BaseModel
from google import genai
from google.genai import types

load_dotenv()

logging.basicConfig(level=logging.INFO)
logger = logging.getLogger("object-spawning-backend")

# Free-tier eligible, fast, cheap -- this is a lightweight extraction task, and
# latency compounds with the STT round trip already in front of it. Using the
# "-latest" alias so this keeps pointing at Google's current lite model as they rotate.
MODEL = "gemini-flash-lite-latest"

API_KEY = os.environ.get("GEMINI_API_KEY")
if not API_KEY:
    logger.warning("GEMINI_API_KEY is not set; /parse-intent will fail until it is.")

client = genai.Client(api_key=API_KEY) if API_KEY else None

TRIPO_API_KEY = os.environ.get("TRIPO_API_KEY")
if not TRIPO_API_KEY:
    logger.warning("TRIPO_API_KEY is not set; /generate-mesh will fail until it is.")

TRIPO_BASE_URL = "https://openapi.tripo3d.ai/v3"

# In-memory job store -- fine for a single-user hobby project with no need to survive a
# backend restart. Keyed by Tripo's own task_id, so there's no separate id to invent.
generation_jobs: dict[str, dict] = {}

app = FastAPI(title="Object Spawning Backend")

COMMAND_FUNCTION = types.FunctionDeclaration(
    name="handle_command",
    description="Extract the spawn or edit command the user wants performed, if any.",
    parameters=types.Schema(
        type="OBJECT",
        properties={
            "recognized": types.Schema(
                type="BOOLEAN",
                description="True if the transcript describes a spawn or edit command. "
                             "False if it doesn't describe either.",
            ),
            "action": types.Schema(
                type="STRING",
                enum=["create", "generate", "resize", "recolor", "move", "rotate", "duplicate", "delete"],
                description="What to do. 'create' spawns a new object from the fixed shape "
                             "library -- set shape (and optionally color/size). 'generate' "
                             "requests a real generated 3D mesh for something that ISN'T in the "
                             "shape library -- set prompt instead of shape. The other actions "
                             "edit whatever object the user is currently pointing at, or the "
                             "last one they created or touched if they aren't pointing at "
                             "anything -- do NOT try to figure out which object from the "
                             "transcript's wording, that's resolved elsewhere. Default to "
                             "'create' if omitted.",
            ),
            "shape": types.Schema(
                type="STRING",
                enum=["cube", "sphere", "cylinder", "table", "shelf", "lamp", "crate", "chair"],
                description="Only for action=create. The object requested. Map related words: "
                             "box/block->cube, ball/orb/globe->sphere, tube/pipe/can->cylinder, "
                             "desk->table, bookshelf/bookcase->shelf, lamp/lantern->lamp, "
                             "container->crate, seat->chair. Note 'box' means the cube "
                             "primitive, not crate -- only the word 'crate' itself maps to crate. "
                             "If the requested object doesn't reasonably map to any of these, "
                             "use action=generate with 'prompt' instead -- don't force a mismatch.",
            ),
            "prompt": types.Schema(
                type="STRING",
                description="Only for action=generate. A short, clean text-to-3D description of "
                             "the object (e.g. 'a stone gargoyle statue', 'a medieval sword', "
                             "'a potted cactus'), derived from the user's phrase with filler "
                             "words stripped ('spawn', 'give me a', 'can you make'). Max ~500 "
                             "characters.",
            ),
            "color": types.Schema(
                type="STRING",
                enum=["red", "green", "blue", "yellow", "white", "black",
                      "orange", "purple", "pink", "gray", "brown", "cyan"],
                description="For action=create or action=recolor. The closest matching color "
                             "name from this list, even if the user's wording differs (e.g. "
                             "'grey'->gray, 'violet'->purple, 'crimson'->red, 'sky blue'->blue, "
                             "'wood'/'wooden'->brown). Omit entirely if no color was mentioned.",
            ),
            "size": types.Schema(
                type="STRING",
                enum=["small", "medium", "large"],
                description="Only for action=create. Omit entirely if no size was mentioned; "
                             "the client defaults to medium.",
            ),
            "size_delta": types.Schema(
                type="STRING",
                enum=["bigger", "smaller"],
                description="Only for action=resize. Whether to grow or shrink the target. "
                             "Defaults to bigger if omitted.",
            ),
            "relation": types.Schema(
                type="STRING",
                enum=["on", "next_to", "on_ground"],
                description="For action=create OR action=move. Set when the user describes where "
                             "to place the object. 'on' and 'next_to' are relative to an EXISTING "
                             "OBJECT (e.g. 'put a lamp ON the table' -> on, 'move it NEXT TO the "
                             "shelf' -> next_to) -- also set reference_shape for these two. "
                             "'on_ground' is for the floor/ground itself ('put it on the ground', "
                             "'move it to the floor') -- the floor isn't a spawnable object, so "
                             "leave reference_shape unset for on_ground. Omit relation entirely if "
                             "no placement was mentioned -- for create the client places the object "
                             "in front of the user by default, for move it uses the same default.",
            ),
            "reference_shape": types.Schema(
                type="STRING",
                enum=["cube", "sphere", "cylinder", "table", "shelf", "lamp", "crate", "chair"],
                description="Only for relation=on or relation=next_to. The type of the existing "
                             "object being referenced (e.g. 'table' in 'put a lamp on the table', "
                             "'move it onto the table'). If several objects of that type exist, the "
                             "client resolves which specific one -- do not try to disambiguate "
                             "instances yourself. Leave unset for relation=on_ground (the ground "
                             "isn't a spawnable shape).",
            ),
        },
        required=["recognized"],
    ),
)

SYSTEM_PROMPT = (
    "You extract structured commands from short voice transcripts for a VR object-spawning app. "
    "Always call the handle_command function exactly once, with no other text.\n"
    "For create commands (spawn a table, make a red cube, give me a big crate), set action=create "
    "and shape (plus color/size if mentioned). The shape library is ONLY: cube, sphere, cylinder, "
    "table, shelf, lamp, crate, chair (plus their listed synonyms). If the requested object doesn't "
    "reasonably match any of those (a gargoyle, a dragon, a sword, a car, a plant -- anything "
    "genuinely different), set action=generate and prompt instead of shape -- do not force it into "
    "the nearest shape. If the user describes where to place it relative to an existing object "
    "('put a lamp on the table', 'place a crate next to the shelf'), also set relation and "
    "reference_shape (this applies to create only, not generate -- generated objects always spawn "
    "at the default position). If they mention the ground/floor instead ('put it on the ground', "
    "'place a crate on the floor'), set relation=on_ground and leave reference_shape unset.\n"
    "For edit commands about an existing object ('make it bigger', 'turn it red', 'rotate it', "
    "'duplicate that', 'delete it', 'move it here'), set action to resize/recolor/rotate/duplicate/"
    "delete/move as appropriate. Leave shape unset for edit actions -- which object it applies to "
    "is resolved elsewhere, not from the transcript's wording. If a move command describes where to "
    "move it ('move it onto the table', 'move it next to the shelf', 'move it to the ground'), also "
    "set relation (and reference_shape for on/next_to) exactly as you would for create.\n"
    "If the transcript doesn't describe either a create, generate, or edit command, set recognized "
    "to false and omit the other fields."
)


class ParseIntentRequest(BaseModel):
    transcript: str


@app.post("/parse-intent")
def parse_intent(request: ParseIntentRequest):
    if client is None:
        raise HTTPException(status_code=500, detail="GEMINI_API_KEY is not configured on the server.")

    if not request.transcript or not request.transcript.strip():
        return {"recognized": False}

    start = time.monotonic()
    try:
        response = client.models.generate_content(
            model=MODEL,
            contents=request.transcript,
            config=types.GenerateContentConfig(
                system_instruction=SYSTEM_PROMPT,
                tools=[types.Tool(function_declarations=[COMMAND_FUNCTION])],
                tool_config=types.ToolConfig(
                    function_calling_config=types.FunctionCallingConfig(
                        mode="ANY",
                        allowed_function_names=["handle_command"],
                    )
                ),
            ),
        )
    except Exception as exc:
        logger.exception("Gemini API call failed")
        raise HTTPException(status_code=502, detail=f"LLM request failed: {exc}") from exc

    elapsed_ms = (time.monotonic() - start) * 1000

    for part in response.candidates[0].content.parts:
        if part.function_call and part.function_call.name == "handle_command":
            args = dict(part.function_call.args)
            logger.info("(%.0fms) transcript=%r -> %r", elapsed_ms, request.transcript, args)
            return args

    raise HTTPException(status_code=502, detail="LLM did not return a function call.")


def _tripo_headers():
    return {"Authorization": f"Bearer {TRIPO_API_KEY}", "Content-Type": "application/json"}


def _create_tripo_task(prompt: str) -> str:
    resp = requests.post(
        f"{TRIPO_BASE_URL}/generation/text-to-model",
        headers=_tripo_headers(),
        json={"prompt": prompt, "texture": True},
        timeout=15,
    )
    if not resp.ok:
        logger.error("Tripo3D create-task returned %d: %s", resp.status_code, resp.text)
    resp.raise_for_status()
    body = resp.json()
    if body.get("code") not in (0, None):
        raise requests.RequestException(f"Tripo3D returned error code {body.get('code')}: {body}")
    return body["data"]["task_id"]


def _get_tripo_task(task_id: str) -> dict:
    resp = requests.get(f"{TRIPO_BASE_URL}/task/{task_id}", headers=_tripo_headers(), timeout=15)
    resp.raise_for_status()
    return resp.json()["data"]


_TRIPO_TERMINAL_FAILURE_STATUSES = {"failed", "cancelled", "banned"}


class GenerateMeshRequest(BaseModel):
    prompt: str


@app.post("/generate-mesh")
def generate_mesh(request: GenerateMeshRequest):
    if not TRIPO_API_KEY:
        raise HTTPException(status_code=500, detail="TRIPO_API_KEY is not configured on the server.")

    prompt = (request.prompt or "").strip()
    if not prompt:
        raise HTTPException(status_code=400, detail="prompt must not be empty.")

    try:
        task_id = _create_tripo_task(prompt)
    except requests.RequestException as exc:
        logger.exception("Tripo3D task creation failed")
        raise HTTPException(status_code=502, detail=f"Mesh generation request failed: {exc}") from exc

    generation_jobs[task_id] = {
        "stage": "pending",
        "prompt": prompt,
        "final_glb_url": None,
        "error": None,
    }
    logger.info("Started mesh generation job %s for prompt=%r", task_id, prompt)
    return {"job_id": task_id}


@app.get("/generation-status/{job_id}")
def generation_status(job_id: str):
    job = generation_jobs.get(job_id)
    if job is None:
        raise HTTPException(status_code=404, detail="Unknown job id.")

    # Terminal states are cached, not re-polled -- avoids burning Tripo3D API calls (and
    # matters more here since result URLs expire 5 minutes after the task completes, so we
    # only want to fetch the fresh status once right as it finishes, not repeatedly after).
    if job["stage"] in ("done", "failed"):
        return _job_response(job)

    try:
        task = _get_tripo_task(job_id)
    except requests.RequestException as exc:
        logger.exception("Polling Tripo3D task failed")
        job["stage"] = "failed"
        job["error"] = f"Status check failed: {exc}"
        return _job_response(job)

    status = task.get("status")
    if status == "success":
        job["final_glb_url"] = (task.get("output") or {}).get("model_url")
        job["stage"] = "done"
        logger.info("Mesh generation job %s complete.", job_id)
    elif status in _TRIPO_TERMINAL_FAILURE_STATUSES:
        job["stage"] = "failed"
        job["error"] = f"Generation {status}."
    # else queued/running -- stay in "pending" stage, client keeps polling

    return _job_response(job)


def _job_response(job: dict) -> dict:
    return {
        "stage": job["stage"],
        "final_glb_url": job["final_glb_url"],
        "error": job["error"],
    }


@app.get("/health")
def health():
    return {"status": "ok", "configured": client is not None, "tripo_configured": TRIPO_API_KEY is not None}

"""
Object Spawning backend -- Stage 2/3/4/5/6/7/8.

Exposes /parse-intent, which turns a raw voice transcript into the small structured
JSON schema the Unity client already understands (create a shape, generate a novel mesh,
or edit an existing object -- resize/recolor/retexture/move/rotate/duplicate/delete/
adjust_lighting), using Gemini's
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

Stage 7 adds the retexture action -- "make it look like rusted metal" -- mapping loose
phrasing onto a small curated PBR material library the Unity client owns (color/metallic/
smoothness presets, no texture assets involved). Same pattern as recolor: the LLM only
names which preset, Unity applies it.

Stage 8 adds a "light" create shape (a real point light) and the adjust_lighting action for
the scene's own ambient/directional light -- reuses resize's size_delta/resize_multiplier
fields for brightness and recolor's color field for tint rather than inventing new ones,
since "how much brighter/dimmer" and "what tint" are the same shape of question resize/
recolor already answer. There used to also be a purely decorative "lamp" shape distinct from
"light", but headset testing showed the LLM reliably conflated the two regardless of prompt
wording, so lamp was removed rather than chasing an unreliable disambiguation.
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
                enum=["create", "generate", "resize", "recolor", "move", "rotate", "duplicate",
                      "delete", "clear", "retexture", "adjust_lighting"],
                description="What to do. 'create' spawns a new object from the fixed shape "
                             "library -- set shape (and optionally color/size). 'generate' "
                             "requests a real generated 3D mesh for something that ISN'T in the "
                             "shape library -- set prompt instead of shape. 'clear' wipes every "
                             "spawned object and resets the room ('clear the room', 'clear "
                             "everything', 'reset', 'start over') -- takes no other fields. "
                             "'retexture' changes an existing object's surface material (NOT its "
                             "solid color -- see the 'material' field) -- set material. "
                             "'adjust_lighting' changes the SCENE's own ambient/directional light "
                             "(not a specific object) -- only for phrases that clearly refer to "
                             "the room/lighting/ambiance/environment as a whole ('make the room "
                             "brighter', 'dim the lighting', 'make the lighting feel warm'), using "
                             "size_delta/resize_multiplier for brightness and color for tint, "
                             "exactly like resize/recolor's own fields. A bare 'make it brighter' "
                             "with no scene reference is NOT this -- that's action=resize against "
                             "whatever object is currently targeted. The remaining actions (resize/"
                             "recolor/move/rotate/duplicate/delete/retexture) edit whatever object "
                             "the user is currently pointing at, or the last one they created or "
                             "touched if they aren't pointing at anything -- do NOT try to figure "
                             "out which object from the transcript's wording, that's resolved "
                             "elsewhere. Default to 'create' if omitted.",
            ),
            "shape": types.Schema(
                type="STRING",
                enum=["cube", "sphere", "cylinder", "table", "shelf", "crate", "chair",
                      "stool", "bench", "sofa", "light"],
                description="Only for action=create. The object requested. Map related words: "
                             "box/block->cube, ball/orb/globe->sphere, tube/pipe/can->cylinder, "
                             "desk->table, bookshelf/bookcase->shelf, container->crate, seat->chair, "
                             "couch->sofa, lamp/lantern/bulb/lightbulb/light source/point light-> "
                             "light (there is no separate decorative-lamp shape -- any lamp/light/ "
                             "bulb request maps to this one 'light' entry, which is a real light "
                             "source, not just a decorative object). Also note 'box' means the cube "
                             "primitive, not crate -- only the word 'crate' itself maps to crate. If "
                             "the requested object doesn't reasonably map to any of these, use "
                             "action=generate with 'prompt' instead -- don't force a mismatch.",
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
                description="For action=create, action=recolor, or action=adjust_lighting (the "
                             "scene's own ambient/directional light tint, e.g. 'make the lighting "
                             "feel warm/blue'). The closest matching color name from this list, "
                             "even if the user's wording differs (e.g. 'grey'->gray, 'violet'-> "
                             "purple, 'crimson'->red, 'sky blue'->blue, 'warm'->orange, 'cool'-> "
                             "blue -- the last two only for adjust_lighting, not recolor, since "
                             "'warm'/'cool' describe lighting mood, not an object's own color). "
                             "Omit entirely if no color was mentioned. IMPORTANT for action=recolor "
                             "specifically: this field is ONLY for a plain, material-free color word "
                             "('turn it red', 'make it blue', 'change its color to green'). If the "
                             "word instead names (or closely synonyms) one of the material "
                             "field's own curated entries -- including ones that also happen to read "
                             "as a color, like 'gold', 'silver', 'chrome', 'wood', 'wooden' -- that "
                             "is ALWAYS action=retexture with material set, never action=recolor, "
                             "even though the word alone could loosely describe a color too. "
                             "'make it gold' and 'make it wooden' are retexture (material=gold, "
                             "material=wood), not recolor.",
            ),
            "material": types.Schema(
                type="STRING",
                enum=["wood", "metal", "rusted_metal", "gold", "chrome", "stone", "concrete",
                      "marble", "brick", "plastic", "rubber", "fabric", "leather"],
                description="Only for action=retexture. The closest matching surface material "
                             "from this curated list, even if the user's wording differs -- "
                             "'wooden'/'oak'/'timber'->wood, 'steel'/'metallic'/'iron'->metal, "
                             "'rusty'/'rusted'/'corroded'->rusted_metal, 'golden'->gold, "
                             "'silver'/'mirror'/'polished silver'/'chrome-plated'->chrome, "
                             "'rock'/'granite'->stone, 'cement'->concrete, 'cloth'/'canvas'/"
                             "'textile'->fabric. This takes priority over the color field whenever "
                             "a word could be read as either -- 'gold'/'silver'/'wood'/'wooden' name "
                             "a material first, a color only as an afterthought. If the request "
                             "doesn't reasonably match any of these (a genuinely different material, "
                             "or a vague 'shinier'/'rougher' texture tweak with no named material), "
                             "set recognized to false instead of forcing a mismatch -- there is no "
                             "generic texture-generation fallback yet.",
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
                description="For action=resize (whether to grow or shrink the target) OR "
                             "action=adjust_lighting (whether to brighten or dim the scene's "
                             "lighting -- map 'brighter'/'brighten'/'lighter' to bigger, 'dimmer'/"
                             "'dim'/'darker' to smaller). Defaults to bigger if omitted. For "
                             "adjust_lighting specifically, leave this unset entirely when the "
                             "phrase is a pure color/mood change with no brightness word ('make "
                             "the lighting feel warm') -- don't invent a brightness change that "
                             "wasn't asked for.",
            ),
            "resize_multiplier": types.Schema(
                type="NUMBER",
                description="Paired with size_delta for EITHER action=resize or "
                             "action=adjust_lighting, and only when the user gives a SPECIFIC "
                             "factor ('make it 10 times bigger', 'make it 3x the size', 'shrink "
                             "it by half' -> 2, 'make it a quarter of the size' -> 4, 'double it' "
                             "-> 2, 'triple it' -> 3, 'make the room twice as bright' -> 2). Always "
                             "a positive number expressing the factor itself, never pre-negated or "
                             "inverted -- the client applies it as a straight multiply for bigger "
                             "and a divide for smaller, so 'make it 2x smaller' means half size "
                             "(set resize_multiplier=2, size_delta=smaller), not literally "
                             "multiplied by 2. Leave unset for a plain 'bigger'/'smaller'/"
                             "'brighter'/'dimmer' with no specific amount -- the client applies its "
                             "own default single-step change in that case, don't guess a number "
                             "yourself.",
            ),
            "relation": types.Schema(
                type="STRING",
                enum=["on", "next_to", "on_ground"],
                description="For action=create OR action=move. Set when the user describes where "
                             "to place the object RELATIVE TO ANOTHER OBJECT OR THE FLOOR, not a "
                             "specific distance -- for a specific distance+direction on a move "
                             "command ('move it 3 meters to the left'), use distance_meters and "
                             "direction instead, and leave this unset. 'on' and 'next_to' are "
                             "relative to an EXISTING OBJECT (e.g. 'put a light ON the table' -> "
                             "on, 'move it NEXT TO the shelf' -> next_to) -- also set "
                             "reference_shape for these two. 'on_ground' is for the floor/ground "
                             "itself ('put it on the ground', 'move it to the floor') -- the "
                             "floor isn't a spawnable object, so leave reference_shape unset for "
                             "on_ground. Omit relation entirely if no placement was mentioned -- "
                             "for create the client places the object in front of the user by "
                             "default, for move it uses the same default.",
            ),
            "reference_shape": types.Schema(
                type="STRING",
                enum=["cube", "sphere", "cylinder", "table", "shelf", "crate", "chair",
                      "stool", "bench", "sofa", "light"],
                description="Only for relation=on or relation=next_to. The type of the existing "
                             "object being referenced (e.g. 'table' in 'put a light on the table', "
                             "'move it onto the table'). If several objects of that type exist, the "
                             "client resolves which specific one -- do not try to disambiguate "
                             "instances yourself. Leave unset for relation=on_ground (the ground "
                             "isn't a spawnable shape).",
            ),
            "direction": types.Schema(
                type="STRING",
                enum=["left", "right", "forward", "backward", "up", "down"],
                description="Only for action=move, when the user names a direction to move the "
                             "object -- WITH or WITHOUT a specific distance ('move it left' -> "
                             "set direction=left with no distance_meters; 'move it 3 meters to "
                             "the left' -> set both). The client applies a sensible default nudge "
                             "distance when none is given, so set this whenever a direction word "
                             "is present -- don't withhold it just because there's no number. "
                             "Direction is relative to the PLAYER'S own current facing direction "
                             "(their left/right/forward/backward), never the object's own "
                             "orientation. Map: 'ahead'/'in front' -> forward, 'behind' -> "
                             "backward, 'above'/'higher' -> up, 'below'/'lower' -> down. Leave "
                             "unset and use relation/reference_shape instead for placement "
                             "relative to another object or the floor ('move it onto the "
                             "table'), or leave everything unset for a plain 'move it here' with "
                             "no details.",
            ),
            "distance_meters": types.Schema(
                type="NUMBER",
                description="Only for action=move, paired with direction, and only when the user "
                             "gives a SPECIFIC distance (e.g. 'move it 3 meters to the left', "
                             "'move it back half a meter', 'shift it up 2 meters'). Leave unset "
                             "if they name a direction with no distance ('move it left') -- the "
                             "client fills in a default in that case, don't guess a number "
                             "yourself.",
            ),
            "degrees": types.Schema(
                type="NUMBER",
                description="Only for action=rotate, when the user gives a SPECIFIC rotation "
                             "amount. Positive rotates clockwise viewed from above, negative "
                             "counterclockwise -- if they say 'to the left' or "
                             "'counterclockwise', make the value negative (e.g. 'rotate it 90 "
                             "degrees to the left' -> -90). 'turn it around' or 'flip it' (no "
                             "number given) means a half turn -> 180. Leave unset entirely for a "
                             "plain 'rotate it'/'turn it' with no amount given -- the client "
                             "applies its own default single-press rotation in that case.",
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
    "table, shelf, crate, chair, stool, bench, sofa, light (plus their listed synonyms). If the "
    "requested object doesn't reasonably match any of those (a gargoyle, a dragon, a sword, a car, "
    "a plant -- anything genuinely different), set action=generate and prompt instead of shape -- "
    "do not force it into the nearest shape. If the user describes where to place it relative to an "
    "existing object ('put a light on the table', 'place a crate next to the shelf'), also set "
    "relation and reference_shape (this applies to create only, not generate -- generated objects "
    "always spawn at the default position). If they mention the ground/floor instead ('put it on "
    "the ground', 'place a crate on the floor'), set relation=on_ground and leave reference_shape "
    "unset.\n"
    "For edit commands about an existing object ('make it bigger', 'turn it red', 'rotate it', "
    "'duplicate that', 'delete it', 'move it here'), set action to resize/recolor/rotate/duplicate/"
    "delete/move as appropriate. Leave shape unset for edit actions -- which object it applies to "
    "is resolved elsewhere, not from the transcript's wording. Resize also covers plain 'brighter'/"
    "'dimmer'/'dim' with no scene reference ('make it brighter', 'dim it') -- these are resize too "
    "(size_delta=bigger/smaller respectively), NOT adjust_lighting, since there's no room/lighting/"
    "scene word; the client resolves whatever object that ends up targeting client-side, including "
    "the case where it's a light. For resize: if the user gives a "
    "specific factor ('make it 10 times bigger', 'shrink it by half', 'double it'), also set "
    "resize_multiplier per that field's own sign/rounding convention (always positive, always the "
    "factor itself); otherwise leave it unset for the client's own default single-step change. "
    "For move: if the user names a "
    "direction ('move it left', 'move it 3 meters to the left', 'shift it back half a meter'), "
    "set direction -- and distance_meters too if they gave a specific number, otherwise leave "
    "distance_meters unset (the client applies its own default nudge distance). If instead they "
    "describe where to move it relative to another object or the floor ('move it onto the "
    "table', 'move it next to the shelf', 'move it to the ground'), set relation (and "
    "reference_shape for on/next_to) exactly as you would for create. A move command should set "
    "EITHER direction (with or without distance_meters) OR relation, never both -- if the phrase "
    "gives neither, leave all four unset for the default in-front-of-user move. For rotate: if a "
    "specific amount is given "
    "('rotate it 180 degrees', 'turn it 45 degrees to the left', 'flip it around'), set degrees "
    "per that field's own sign convention; otherwise leave degrees unset.\n"
    "For 'clear the room', 'clear everything', 'reset', 'start over' (wiping every spawned "
    "object), set action=clear and no other fields.\n"
    "For changing an existing object's surface material ('make it look like rusted metal', "
    "'turn it to stone', 'make it wood', 'give it a marble finish'), set action=retexture and "
    "material to the closest match from the material field's own curated list. This is distinct "
    "from recolor: a plain, material-free color word alone ('make it red', 'make it blue') is "
    "recolor, while a named material is retexture -- even one-word phrases. Material wins "
    "whenever a word is ambiguous between the two: 'make it gold', 'make it silver', 'make it "
    "wood'/'wooden' are retexture (material=gold/chrome/wood), NOT recolor, even though gold/"
    "silver/wood could each loosely describe a color too -- naming a material takes priority "
    "over any color reading of the same word. If the material doesn't reasonably match the "
    "curated list, set recognized to false rather than guessing the nearest one.\n"
    "For changing the SCENE's own ambient/directional light -- not a specific object -- ('make "
    "the room brighter', 'dim the lighting', 'brighten the scene', 'make the lighting feel warm', "
    "'change the ambiance to blue'), set action=adjust_lighting using size_delta/resize_multiplier "
    "for brightness (exactly like resize's own fields, including the same multiplier convention) "
    "and color for tint (exactly like recolor's own field, including 'warm'->orange, 'cool'->blue "
    "for mood words that only make sense as lighting, not an object's color). Only use "
    "adjust_lighting when the phrase clearly references the room/lighting/ambiance/environment/"
    "scene as a whole -- a bare 'make it brighter' or 'dim it' with no such reference is action="
    "resize instead, targeting whatever object is currently selected (this covers the case where "
    "that object happens to be a spawned light itself, which is resolved client-side, not by you). "
    "Set only the fields the phrase actually asks for -- 'make the lighting feel warm' should set "
    "color alone and leave size_delta unset, not invent a brightness change that wasn't requested.\n"
    "If the transcript doesn't describe either a create, generate, edit, clear, retexture, or "
    "adjust_lighting command, set recognized to false and omit the other fields."
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


TRIPO_MODEL = "v3.1-20260211"


def _create_tripo_task(prompt: str) -> str:
    resp = requests.post(
        f"{TRIPO_BASE_URL}/generation/text-to-model",
        headers=_tripo_headers(),
        json={"prompt": prompt, "texture": True, "model": TRIPO_MODEL},
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
    resp = requests.get(f"{TRIPO_BASE_URL}/tasks/{task_id}", headers=_tripo_headers(), timeout=15)
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
        "progress": 0,
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
    job["progress"] = task.get("progress", job["progress"])
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
        "progress": job["progress"],
        "final_glb_url": job["final_glb_url"],
        "error": job["error"],
    }


@app.get("/health")
def health():
    return {"status": "ok", "configured": client is not None, "tripo_configured": TRIPO_API_KEY is not None}

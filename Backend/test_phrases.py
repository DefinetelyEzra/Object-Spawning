"""
Ad-hoc validation script for Stage 2/3/4/5/6/7's test criteria: run a batch of varied phrasings
against the running local backend, check the JSON is well-formed and semantically
correct, and report round-trip latency. Not a pytest suite -- just a quick harness.

Usage: run the backend (uvicorn main:app), then `python test_phrases.py`.
"""
import time
import urllib.request
import json

URL = "http://127.0.0.1:8000/parse-intent"

# (transcript, expected_action, expected_shape, expected_color, expected_size,
#  expected_size_delta, expected_relation, expected_reference_shape, expects_prompt,
#  expected_material)
# expected_action is None for transcripts that should NOT be recognized as a command at all.
# Unset fields for a given action/relation are left as None and skipped. expects_prompt is
# True only for Stage 6 generate cases -- the LLM's exact prompt wording isn't pinned, just
# that action=generate came back with a non-empty prompt. expected_material is Stage 7's
# retexture-only field, appended at the end to avoid reshuffling every existing case above.
CASES = [
    # Roadmap's own canonical examples
    ("spawn a cube", "create", "cube", None, None, None, None, None, False, None),
    ("make a sphere red", "create", "sphere", "red", None, None, None, None, False, None),
    ("big cylinder", "create", "cylinder", None, "large", None, None, None, False, None),
    ("give me something round and blue, kind of small", "create", "sphere", "blue", "small", None, None, None, False, None),
    # Straightforward, varied shape/color/size combos
    ("spawn a red sphere", "create", "sphere", "red", None, None, None, None, False, None),
    ("I want a small green cube", "create", "cube", "green", "small", None, None, None, False, None),
    ("create a large yellow cylinder", "create", "cylinder", "yellow", "large", None, None, None, False, None),
    ("give me a tiny purple ball", "create", "sphere", "purple", "small", None, None, None, False, None),
    ("drop a big orange box", "create", "cube", "orange", "large", None, None, None, False, None),
    ("place a white cylinder here", "create", "cylinder", "white", None, None, None, None, False, None),
    ("black cube please", "create", "cube", "black", None, None, None, None, False, None),
    # Synonyms the parser should map correctly
    ("spawn a box", "create", "cube", None, None, None, None, None, False, None),
    ("give me a ball", "create", "sphere", None, None, None, None, None, False, None),
    ("I need a tube", "create", "cylinder", None, None, None, None, None, False, None),
    ("make me an orb", "create", "sphere", None, None, None, None, None, False, None),
    # No color / no size specified
    ("spawn a sphere", "create", "sphere", None, None, None, None, None, False, None),
    ("give me a cylinder", "create", "cylinder", None, None, None, None, None, False, None),
    # Loosely phrased / conversational
    ("can you make me a little blue cube", "create", "cube", "blue", "small", None, None, None, False, None),
    ("I'd like a huge red ball please", "create", "sphere", "red", "large", None, None, None, False, None),
    ("how about a tiny green cylinder", "create", "cylinder", "green", "small", None, None, None, False, None),
    ("something big and purple, a cube maybe", "create", "cube", "purple", "large", None, None, None, False, None),
    ("just give me a plain sphere", "create", "sphere", None, None, None, None, None, False, None),
    ("could you spawn a massive yellow box", "create", "cube", "yellow", "large", None, None, None, False, None),
    ("a small round thing, blue", "create", "sphere", "blue", "small", None, None, None, False, None),
    ("gimme a cylinder, make it orange", "create", "cylinder", "orange", None, None, None, None, False, None),
    # Reordered / unusual phrasing
    ("red, small, a cube", "create", "cube", "red", "small", None, None, None, False, None),
    ("the color should be green and the shape a sphere", "create", "sphere", "green", None, None, None, None, False, None),
    ("cylinder shaped, large, white", "create", "cylinder", "white", "large", None, None, None, False, None),
    # Should NOT recognize a spawn or edit command
    ("what's the weather like today", None, None, None, None, None, None, None, False, None),
    ("hello there", None, None, None, None, None, None, None, False, None),
    # Stage 3: procedurally-composed objects
    ("make me a small wooden table", "create", "table", "brown", "small", None, None, None, False, None),
    ("give me a desk", "create", "table", None, None, None, None, None, False, None),
    ("spawn a bookshelf", "create", "shelf", None, None, None, None, None, False, None),
    ("I want a large shelf", "create", "shelf", None, "large", None, None, None, False, None),
    ("give me a crate", "create", "crate", None, None, None, None, None, False, None),
    ("spawn a big crate", "create", "crate", None, "large", None, None, None, False, None),
    ("make me a chair", "create", "chair", None, None, None, None, None, False, None),
    ("I need a black chair", "create", "chair", "black", None, None, None, None, False, None),
    # "box" should still mean the cube primitive, not crate
    ("spawn a box", "create", "cube", None, None, None, None, None, False, None),
    # Stage 4: edit commands -- shape/size must stay unset; which object it applies to
    # is resolved client-side, never by the LLM from the transcript's wording.
    ("make it bigger", "resize", None, None, None, "bigger", None, None, False, None),
    ("that's too small, make it larger", "resize", None, None, None, "bigger", None, None, False, None),
    ("make it smaller", "resize", None, None, None, "smaller", None, None, False, None),
    ("shrink it a bit", "resize", None, None, None, "smaller", None, None, False, None),
    # resize_multiplier isn't formally asserted below (same as distance_meters/degrees above --
    # check the printed raw result for the actual value), just confirming action/size_delta still
    # resolve correctly when a specific factor is mentioned.
    ("make it 10 times bigger", "resize", None, None, None, "bigger", None, None, False, None),
    ("shrink it by half", "resize", None, None, None, "smaller", None, None, False, None),
    ("double it", "resize", None, None, None, "bigger", None, None, False, None),
    ("turn it red", "recolor", None, "red", None, None, None, None, False, None),
    ("make it blue", "recolor", None, "blue", None, None, None, None, False, None),
    ("change its color to green", "recolor", None, "green", None, None, None, None, False, None),
    ("move it here", "move", None, None, None, None, None, None, False, None),
    ("bring it closer", "move", None, None, None, None, None, None, False, None),
    # Move + spatial relation -- same relation/reference_shape schema as create
    ("move it onto the table", "move", None, None, None, None, "on", "table", False, None),
    ("move it next to the shelf", "move", None, None, None, None, "next_to", "shelf", False, None),
    ("move it to the ground", "move", None, None, None, None, "on_ground", None, False, None),
    ("rotate it", "rotate", None, None, None, None, None, None, False, None),
    ("turn it around", "rotate", None, None, None, None, None, None, False, None),
    ("duplicate that", "duplicate", None, None, None, None, None, None, False, None),
    ("copy it", "duplicate", None, None, None, None, None, None, False, None),
    ("delete that", "delete", None, None, None, None, None, None, False, None),
    ("remove it", "delete", None, None, None, None, None, None, False, None),
    ("get rid of that", "delete", None, None, None, None, None, None, False, None),
    # Edit verb should win over an incidental shape noun in the same phrase
    ("make the table bigger", "resize", None, None, None, "bigger", None, None, False, None),
    ("delete the chair", "delete", None, None, None, None, None, None, False, None),
    # Stage 5: spatial relations -- the LLM only names the relation and the reference's TYPE,
    # never a specific instance (resolving which actual object that means is client-side).
    ("put a light on the table", "create", "light", None, None, None, "on", "table", False, None),
    ("place a crate next to the shelf", "create", "crate", None, None, None, "next_to", "shelf", False, None),
    ("spawn a small red cube on the crate", "create", "cube", "red", "small", None, "on", "crate", False, None),
    ("put a chair next to the table", "create", "chair", None, None, None, "next_to", "table", False, None),
    # The floor/ground isn't a spawnable object -- reference_shape should stay unset
    ("put a cube on the ground", "create", "cube", None, None, None, "on_ground", None, False, None),
    ("place a crate on the floor", "create", "crate", None, None, None, "on_ground", None, False, None),
    # No spatial relation mentioned -- relation/reference_shape should stay unset
    ("spawn a cube", "create", "cube", None, None, None, None, None, False, None),
    # Stage 6: generate -- anything outside the fixed shape library should come back as
    # action=generate with a prompt, not be forced into the nearest shape match.
    ("spawn a stone gargoyle statue", "generate", None, None, None, None, None, None, True, None),
    ("give me a medieval sword", "generate", None, None, None, None, None, None, True, None),
    ("can you make me a small dragon figurine", "generate", None, None, None, None, None, None, True, None),
    ("I want a potted cactus", "generate", None, None, None, None, None, None, True, None),
    ("generate a vintage car", "generate", None, None, None, None, None, None, True, None),
    # Still a plain create -- a shape-library word shouldn't get pushed into generate
    ("spawn a cube", "create", "cube", None, None, None, None, None, False, None),
    # Stage 7: retexture -- named-material phrasing should map to the curated preset list,
    # never a solid color, and never force a mismatch onto the nearest preset.
    ("make it look like rusted metal", "retexture", None, None, None, None, None, None, False, "rusted_metal"),
    ("turn it to stone", "retexture", None, None, None, None, None, None, False, "stone"),
    ("give it a marble finish", "retexture", None, None, None, None, None, None, False, "marble"),
    ("make it wood", "retexture", None, None, None, None, None, None, False, "wood"),
    ("make it look metallic", "retexture", None, None, None, None, None, None, False, "metal"),
    ("make it gold", "retexture", None, None, None, None, None, None, False, "gold"),
    ("give it a chrome finish", "retexture", None, None, None, None, None, None, False, "chrome"),
    ("make it concrete", "retexture", None, None, None, None, None, None, False, "concrete"),
    ("make it brick", "retexture", None, None, None, None, None, None, False, "brick"),
    ("make it plastic", "retexture", None, None, None, None, None, None, False, "plastic"),
    ("make it rubber", "retexture", None, None, None, None, None, None, False, "rubber"),
    ("make it fabric", "retexture", None, None, None, None, None, None, False, "fabric"),
    ("make it leather", "retexture", None, None, None, None, None, None, False, "leather"),
    # Material wins whenever a word is ambiguous between color and material -- confirmed against
    # a real headset test where "make it gold" (line above) was initially (incorrectly) parsed
    # as recolor before the color/material field descriptions were tightened.
    ("make it silver", "retexture", None, None, None, None, None, None, False, "chrome"),
    # Recolor/retexture disambiguation -- a plain color word alone should stay recolor, not
    # get pulled into retexture just because a material also happens to be describable by color.
    ("turn it red", "recolor", None, "red", None, None, None, None, False, None),
    ("make it blue", "recolor", None, "blue", None, None, None, None, False, None),
    # Stage 8: "light" is a real point-light shape -- "lamp" was removed after headset testing
    # showed the LLM reliably conflated it with light regardless of prompt wording, so both
    # words should now map to the same "light" shape.
    ("spawn a light", "create", "light", None, None, None, None, None, False, None),
    ("give me a bright light", "create", "light", None, None, None, None, None, False, None),
    ("spawn a lamp", "create", "light", None, None, None, None, None, False, None),
    # adjust_lighting -- reuses size_delta/color, only when the phrase clearly refers to the
    # scene/room/lighting as a whole, not a specific object.
    ("make the room brighter", "adjust_lighting", None, None, None, "bigger", None, None, False, None),
    ("dim the lighting", "adjust_lighting", None, None, None, "smaller", None, None, False, None),
    ("brighten the scene", "adjust_lighting", None, None, None, "bigger", None, None, False, None),
    ("make the lighting feel warm", "adjust_lighting", None, "orange", None, None, None, None, False, None),
    ("change the ambiance to blue", "adjust_lighting", None, "blue", None, None, None, None, False, None),
    # Disambiguation -- a bare "make it brighter" with no scene reference must stay a plain
    # per-object resize, never adjust_lighting.
    ("make it brighter", "resize", None, None, None, "bigger", None, None, False, None),
    ("dim it", "resize", None, None, None, "smaller", None, None, False, None),
    # Stage 9: undo -- no other fields.
    ("undo that", "undo", None, None, None, None, None, None, False, None),
    ("undo", "undo", None, None, None, None, None, None, False, None),
    ("undo the last thing", "undo", None, None, None, None, None, None, False, None),
    # Stage 9: reroll_style -- a vague "different style" ask with no material named, distinct
    # from retexture above (which requires a specific material).
    ("try a different style", "reroll_style", None, None, None, None, None, None, False, None),
    ("try something else", "reroll_style", None, None, None, None, None, None, False, None),
    ("give it a different look", "reroll_style", None, None, None, None, None, None, False, None),
]


def run():
    passed = 0
    failed = 0
    total_latency = 0.0

    for (transcript, exp_action, exp_shape, exp_color, exp_size,
         exp_size_delta, exp_relation, exp_reference_shape, expects_prompt, exp_material) in CASES:
        time.sleep(4.5)  # stay under the free tier's 15 requests/minute
        body = json.dumps({"transcript": transcript}).encode("utf-8")
        req = urllib.request.Request(
            URL, data=body, headers={"Content-Type": "application/json"}, method="POST"
        )
        start = time.monotonic()
        try:
            with urllib.request.urlopen(req, timeout=15) as resp:
                result = json.loads(resp.read().decode("utf-8"))
        except Exception as e:
            print(f"FAIL  {transcript!r} -> request error: {e}")
            failed += 1
            continue
        elapsed_ms = (time.monotonic() - start) * 1000
        total_latency += elapsed_ms

        recognized = result.get("recognized", False)
        action = result.get("action", "create" if recognized else None)
        shape = result.get("shape")
        prompt = result.get("prompt")
        color = result.get("color")
        size = result.get("size")
        size_delta = result.get("size_delta")
        relation = result.get("relation")
        reference_shape = result.get("reference_shape")
        material = result.get("material")

        if exp_action is None:
            ok = not recognized
        else:
            ok = (
                recognized
                and action == exp_action
                and (exp_shape is None or shape == exp_shape)
                and (exp_color is None or color == exp_color)
                and (exp_size is None or size == exp_size)
                and (exp_size_delta is None or size_delta == exp_size_delta)
                and (exp_relation is None or relation == exp_relation)
                and (exp_reference_shape is None or reference_shape == exp_reference_shape)
                and (not expects_prompt or bool(prompt and prompt.strip()))
                and (exp_material is None or material == exp_material)
            )

        status = "PASS" if ok else "FAIL"
        if ok:
            passed += 1
        else:
            failed += 1
        print(f"{status}  ({elapsed_ms:5.0f}ms)  {transcript!r:65s} -> {result}")

    print()
    print(f"Results: {passed}/{len(CASES)} passed, {failed} failed")
    print(f"Average latency: {total_latency / len(CASES):.0f}ms")


if __name__ == "__main__":
    run()

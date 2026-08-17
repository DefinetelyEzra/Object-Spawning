using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace ObjectSpawning
{
    // Stage 2: sends the transcript to the backend for real LLM-based intent understanding,
    // replacing Stage 1's keyword spotting. The backend keeps the LLM's output contained to a
    // small JSON schema (shape/color/size) via tool-calling, so this class never has to deal
    // with free-form text -- only validate the schema before trusting it in Unity.
    public class LlmIntentClient : MonoBehaviour
    {
        [SerializeField] string backendUrl = "http://127.0.0.1:8000";
        // Standalone backend+Gemini latency measured ~600-930ms with zero network hops. The real
        // headset-to-PC WiFi round trip adds on top of that and can be inconsistent -- 8s was
        // occasionally too tight, silently degrading loose phrasing to the local keyword fallback.
        [SerializeField] float timeoutSeconds = 15f;

        string EffectiveBackendUrl => BackendUrlResolver.Resolve(backendUrl);

        [Serializable]
        class ParseIntentRequestBody
        {
            public string transcript;
        }

        [Serializable]
        class ParseIntentResponseBody
        {
            public bool recognized;
            public string action;      // "create" (default if empty) | generate | resize | recolor | move | rotate | duplicate | delete | clear | retexture
            public string shape;       // create only
            public string prompt;      // generate only
            public string color;       // create or recolor
            public string material;    // retexture only
            public string size;        // create only (absolute)
            public string size_delta;  // resize only ("bigger" | "smaller")
            public float resize_multiplier; // resize only, paired with size_delta -- 0 means unset, same reasoning as distance_meters/degrees
            public string relation;        // create or move ("on" | "next_to" | "on_ground")
            public string reference_shape; // create or move, only with relation set
            public float distance_meters;  // move only, paired with direction -- 0 means unset (JsonUtility has no nullable float, and no one asks to move 0 meters)
            public string direction;       // move only ("left" | "right" | "forward" | "backward" | "up" | "down")
            public float degrees;          // rotate only -- same 0-means-unset reasoning (a 0-degree rotate request isn't a real command)
        }

        // At most one of spawnIntent/editIntent/generateIntent is non-null. All three null means
        // either the LLM legitimately didn't recognize a command (error is null/empty) or the
        // request itself failed -- network unreachable, timeout, or a malformed/invalid response
        // (error describes what happened). Callers should fall back to the keyword parser for
        // spawn/edit in both null cases (there's no local fallback for generate -- see
        // GenerateIntent), but only the error case is worth surfacing as something went wrong.
        public void ParseIntent(string transcript, Action<SpawnIntent?, EditIntent?, GenerateIntent?, float, string> onComplete) =>
            StartCoroutine(ParseIntentRoutine(transcript, onComplete));

        IEnumerator ParseIntentRoutine(string transcript, Action<SpawnIntent?, EditIntent?, GenerateIntent?, float, string> onComplete)
        {
            var startTime = Time.realtimeSinceStartup;
            var bodyJson = JsonUtility.ToJson(new ParseIntentRequestBody { transcript = transcript });
            var bodyBytes = Encoding.UTF8.GetBytes(bodyJson);

            using var request = new UnityWebRequest($"{EffectiveBackendUrl}/parse-intent", "POST");
            request.uploadHandler = new UploadHandlerRaw(bodyBytes);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.timeout = Mathf.CeilToInt(timeoutSeconds);

            yield return request.SendWebRequest();

            var elapsedMs = (Time.realtimeSinceStartup - startTime) * 1000f;

            if (request.result != UnityWebRequest.Result.Success)
            {
                var error = $"Backend unreachable: {request.error}";
                Debug.LogWarning($"[LlmIntentClient] Request failed: {request.error}");
                onComplete?.Invoke(null, null, null, elapsedMs, error);
                yield break;
            }

            ParseIntentResponseBody response = null;
            try
            {
                response = JsonUtility.FromJson<ParseIntentResponseBody>(request.downloadHandler.text);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[LlmIntentClient] Malformed JSON response: {e.Message}");
                onComplete?.Invoke(null, null, null, elapsedMs, $"Malformed response: {e.Message}");
                yield break;
            }

            if (response == null)
            {
                onComplete?.Invoke(null, null, null, elapsedMs, "Empty or invalid response from backend.");
                yield break;
            }

            if (!response.recognized)
            {
                // Legitimate: the LLM ran fine and correctly found no command here.
                onComplete?.Invoke(null, null, null, elapsedMs, null);
                yield break;
            }

            var action = InferAction(response);

            if (action == "create")
            {
                if (!TryParseShape(response.shape, out var shape))
                {
                    var error = $"Backend returned invalid shape: '{response.shape}'";
                    Debug.LogWarning($"[LlmIntentClient] {error}");
                    onComplete?.Invoke(null, null, null, elapsedMs, error);
                    yield break;
                }

                ColorNaming.TryGetColor(response.color, out var color);
                var scale = ParseSize(response.size);

                SpatialRelation? relation = TryParseRelation(response.relation);
                PrimitiveShape? referenceShape = null;
                if (relation.HasValue && TryParseShape(response.reference_shape, out var refShape))
                    referenceShape = refShape;

                onComplete?.Invoke(new SpawnIntent(shape, color, scale, relation, referenceShape), null, null, elapsedMs, null);
                yield break;
            }

            if (action == "generate")
            {
                if (string.IsNullOrWhiteSpace(response.prompt))
                {
                    var error = "Backend returned action=generate with no prompt.";
                    Debug.LogWarning($"[LlmIntentClient] {error}");
                    onComplete?.Invoke(null, null, null, elapsedMs, error);
                    yield break;
                }

                onComplete?.Invoke(null, null, new GenerateIntent(response.prompt.Trim()), elapsedMs, null);
                yield break;
            }

            if (TryParseEditAction(action, response, out var editIntent))
            {
                onComplete?.Invoke(null, editIntent, null, elapsedMs, null);
                yield break;
            }

            var invalidActionError = $"Backend returned invalid action: '{response.action}'";
            Debug.LogWarning($"[LlmIntentClient] {invalidActionError}");
            onComplete?.Invoke(null, null, null, elapsedMs, invalidActionError);
        }

        // The LLM occasionally omits `action` even when it isn't a create command -- observed
        // for resize (size_delta present, action missing) and, in headset testing, for
        // retexture too (material present, action missing -- every "make it wood"/"marble"/
        // "stone" test came back this way, and only survived because the local keyword fallback
        // happened to also catch the same word). size_delta/prompt/material are each scoped by
        // the schema to exactly one action, so their presence is unambiguous evidence of intent
        // regardless of whether the model remembered to set action too.
        static string InferAction(ParseIntentResponseBody response)
        {
            if (!string.IsNullOrEmpty(response.action))
                return response.action.ToLowerInvariant();
            if (!string.IsNullOrEmpty(response.size_delta))
                return "resize";
            if (!string.IsNullOrEmpty(response.material))
                return "retexture";
            if (!string.IsNullOrEmpty(response.prompt))
                return "generate";
            return "create";
        }

        static bool TryParseShape(string value, out PrimitiveShape shape)
        {
            switch (value?.ToLowerInvariant())
            {
                case "cube": shape = PrimitiveShape.Cube; return true;
                case "sphere": shape = PrimitiveShape.Sphere; return true;
                case "cylinder": shape = PrimitiveShape.Cylinder; return true;
                case "table": shape = PrimitiveShape.Table; return true;
                case "shelf": shape = PrimitiveShape.Shelf; return true;
                case "lamp": shape = PrimitiveShape.LampBase; return true;
                case "crate": shape = PrimitiveShape.Crate; return true;
                case "chair": shape = PrimitiveShape.Chair; return true;
                case "stool": shape = PrimitiveShape.Stool; return true;
                case "bench": shape = PrimitiveShape.Bench; return true;
                case "sofa": shape = PrimitiveShape.Sofa; return true;
                default: shape = default; return false;
            }
        }

        static bool TryParseEditAction(string action, ParseIntentResponseBody response, out EditIntent intent)
        {
            switch (action)
            {
                case "resize":
                    var bigger = response.size_delta?.ToLowerInvariant() != "smaller";
                    var resizeMultiplier = response.resize_multiplier != 0f ? (float?)response.resize_multiplier : null;
                    intent = new EditIntent(EditAction.Resize, bigger: bigger, resizeMultiplier: resizeMultiplier);
                    return true;
                case "recolor":
                    ColorNaming.TryGetColor(response.color, out var color);
                    intent = new EditIntent(EditAction.Recolor, color: color);
                    return true;
                case "move":
                    // A direction alone wins over relation-based placement -- distance_meters is
                    // a refinement when the LLM captured a specific number, not a requirement, so
                    // "move it left" (no distance mentioned) doesn't silently fall through to
                    // unrelated default placement instead of actually moving left.
                    if (TryParseDirection(response.direction, out var moveDirection))
                    {
                        var moveDistance = response.distance_meters != 0f
                            ? response.distance_meters
                            : VoiceIntentParser.DefaultMoveDistanceMeters;
                        intent = new EditIntent(EditAction.Move,
                            moveDistanceMeters: moveDistance, moveDirection: moveDirection);
                        return true;
                    }

                    var moveRelation = TryParseRelation(response.relation);
                    PrimitiveShape? moveReferenceShape = null;
                    if (moveRelation.HasValue && TryParseShape(response.reference_shape, out var moveRefShape))
                        moveReferenceShape = moveRefShape;
                    intent = new EditIntent(EditAction.Move, relation: moveRelation, referenceShape: moveReferenceShape);
                    return true;
                case "rotate":
                    // 0 means the LLM left it unset (no one asks to rotate 0 degrees) -- caller
                    // applies the old fixed-default rotation in that case.
                    var rotateDegrees = response.degrees != 0f ? (float?)response.degrees : null;
                    intent = new EditIntent(EditAction.Rotate, rotateDegrees: rotateDegrees);
                    return true;
                case "duplicate":
                    intent = new EditIntent(EditAction.Duplicate);
                    return true;
                case "delete":
                    intent = new EditIntent(EditAction.Delete);
                    return true;
                case "clear":
                    intent = new EditIntent(EditAction.ClearAll);
                    return true;
                case "retexture":
                    // Unlike recolor's color (which gracefully defaults to white when
                    // unrecognized), there's no sensible default material -- treat an unrecognized
                    // or missing one as an invalid action entirely, same as generate's empty-prompt
                    // check above, so the caller surfaces a real error instead of silently no-oping.
                    if (!MaterialNaming.TryGetMaterial(response.material, out var material))
                    {
                        intent = default;
                        return false;
                    }
                    intent = new EditIntent(EditAction.Retexture, material: material);
                    return true;
                default:
                    intent = default;
                    return false;
            }
        }

        static bool TryParseDirection(string value, out MoveDirection direction)
        {
            switch (value?.ToLowerInvariant())
            {
                case "left": direction = MoveDirection.Left; return true;
                case "right": direction = MoveDirection.Right; return true;
                case "forward": direction = MoveDirection.Forward; return true;
                case "backward": direction = MoveDirection.Backward; return true;
                case "up": direction = MoveDirection.Up; return true;
                case "down": direction = MoveDirection.Down; return true;
                default: direction = default; return false;
            }
        }

        static float ParseSize(string value) => value?.ToLowerInvariant() switch
        {
            "small" => VoiceIntentParser.SmallScale,
            "large" => VoiceIntentParser.BigScale,
            _ => VoiceIntentParser.DefaultScale,
        };

        static SpatialRelation? TryParseRelation(string value) => value?.ToLowerInvariant() switch
        {
            "on" => SpatialRelation.On,
            "next_to" => SpatialRelation.NextTo,
            "on_ground" => SpatialRelation.OnGround,
            _ => null,
        };
    }
}

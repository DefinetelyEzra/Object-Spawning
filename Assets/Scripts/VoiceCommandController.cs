using Oculus.Voice.Dictation;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ObjectSpawning
{
    // Stage 2: push-to-talk voice capture -> LLM intent parsing (backend) -> primitive spawn,
    // falling back to Stage 1's local keyword parser if the backend is unreachable or the LLM
    // doesn't recognize a command. Uses Meta Voice SDK dictation for the raw transcript only.
    public class VoiceCommandController : MonoBehaviour
    {
        [SerializeField] AppDictationExperience dictation;
        [SerializeField] PrimitiveSpawner spawner;
        [SerializeField] LlmIntentClient llmClient;
        [SerializeField] ObjectSelector objectSelector;

        InputAction pushToTalkAction;
        float pushToTalkReleaseTime;

        // Live pipeline status, surfaced on DebugHud so it's visible in-headset without a Console.
        public bool IsButtonHeld { get; private set; }
        public bool IsMicListening { get; private set; }
        public float LastMicLevel { get; private set; }
        public string LastPartialTranscript { get; private set; } = "";
        public string LastFullTranscript { get; private set; } = "";
        public string LastError { get; private set; } = "";
        public string LastLlmError { get; private set; } = "";
        public float LastSttLatencyMs { get; private set; }
        public float LastLlmLatencyMs { get; private set; }
        public float LastTotalLatencyMs { get; private set; }

        // Stage 9: set whenever the LLM parsed a command but flagged low confidence in it -- the
        // command is held here rather than executed immediately, and GuestHud/DebugHud surface
        // this text so the player can see what was guessed. Empty string means nothing pending.
        public string PendingConfirmationText { get; private set; } = "";

        static readonly string[] AffirmativeWords = { "yes", "yeah", "yep", "yup", "correct", "confirm", "right" };

        SpawnIntent? pendingSpawnIntent;
        EditIntent? pendingEditIntent;
        GenerateIntent? pendingGenerateIntent;

        bool HasPendingConfirmation => pendingSpawnIntent.HasValue || pendingEditIntent.HasValue || pendingGenerateIntent.HasValue;

        void Awake()
        {
            pushToTalkAction = new InputAction(name: "PushToTalk", type: InputActionType.Button);
            pushToTalkAction.AddBinding("<Keyboard>/v");
            pushToTalkAction.AddBinding("<XRController>{LeftHand}/triggerButton");
            pushToTalkAction.started += OnPushToTalkStarted;
            pushToTalkAction.canceled += OnPushToTalkReleased;
        }

        void OnEnable()
        {
            pushToTalkAction.Enable();
            if (dictation != null)
            {
                dictation.DictationEvents.OnStartListening.AddListener(OnStartListening);
                dictation.DictationEvents.OnStoppedListening.AddListener(OnStoppedListening);
                dictation.DictationEvents.OnMicLevelChanged.AddListener(OnMicLevelChanged);
                dictation.DictationEvents.OnPartialTranscription.AddListener(OnPartialTranscription);
                dictation.DictationEvents.OnFullTranscription.AddListener(OnFullTranscription);
                dictation.DictationEvents.OnError.AddListener(OnError);
            }
        }

        void OnDisable()
        {
            pushToTalkAction.Disable();
            if (dictation != null)
            {
                dictation.DictationEvents.OnStartListening.RemoveListener(OnStartListening);
                dictation.DictationEvents.OnStoppedListening.RemoveListener(OnStoppedListening);
                dictation.DictationEvents.OnMicLevelChanged.RemoveListener(OnMicLevelChanged);
                dictation.DictationEvents.OnPartialTranscription.RemoveListener(OnPartialTranscription);
                dictation.DictationEvents.OnFullTranscription.RemoveListener(OnFullTranscription);
                dictation.DictationEvents.OnError.RemoveListener(OnError);
            }
        }

        void OnPushToTalkStarted(InputAction.CallbackContext context)
        {
            IsButtonHeld = true;
            LastError = "";
            LastLlmError = "";
            LastPartialTranscript = "";

            // Guard against a prior session that never fully tore down (repeated
            // activate/deactivate cycles can leave the SDK's audio pipeline stuck
            // "active" with no further events firing) -- force it closed first.
            if (dictation != null && dictation.Active)
            {
                Debug.LogWarning("[VoiceCommandController] Dictation was still active from a " +
                    "previous session; cancelling before starting a new one.");
                dictation.Cancel();
            }

            Debug.Log("[VoiceCommandController] Push-to-talk pressed, activating dictation...");
            dictation?.Activate();
        }

        void OnPushToTalkReleased(InputAction.CallbackContext context)
        {
            IsButtonHeld = false;
            pushToTalkReleaseTime = Time.realtimeSinceStartup;
            Debug.Log("[VoiceCommandController] Push-to-talk released.");
            dictation?.Deactivate();
        }

        void OnStartListening()
        {
            IsMicListening = true;
            Debug.Log("[VoiceCommandController] Mic listening started.");
        }

        void OnStoppedListening()
        {
            IsMicListening = false;
            Debug.Log("[VoiceCommandController] Mic listening stopped.");
        }

        void OnMicLevelChanged(float level) => LastMicLevel = level;

        void OnPartialTranscription(string transcript)
        {
            LastPartialTranscript = transcript;
            Debug.Log($"[VoiceCommandController] Partial: \"{transcript}\"");
        }

        void OnError(string errorCode, string errorMessage)
        {
            LastError = $"{errorCode}: {errorMessage}";
            Debug.LogError($"[VoiceCommandController] Dictation error [{errorCode}]: {errorMessage}");
        }

        void OnFullTranscription(string rawTranscript)
        {
            var transcript = TranscriptAutocorrect.Correct(rawTranscript);
            LastFullTranscript = transcript;
            LastSttLatencyMs = (Time.realtimeSinceStartup - pushToTalkReleaseTime) * 1000f;

            if (transcript != rawTranscript)
                Debug.Log($"[VoiceCommandController] Transcript: \"{rawTranscript}\" -> autocorrected to " +
                    $"\"{transcript}\" (STT: {LastSttLatencyMs:0}ms)");
            else
                Debug.Log($"[VoiceCommandController] Transcript: \"{transcript}\" (STT: {LastSttLatencyMs:0}ms)");

            // Stage 9: a low-confidence guess from last turn is waiting on a yes/no, and this
            // transcript IS that answer -- resolve it here, before sending anything to the LLM
            // (an affirmative like "yes" alone would just come back unrecognized anyway, wasting a
            // round trip, and risks the LLM guessing something unrelated from it). Anything other
            // than a clear "yes" discards the pending guess and falls through to treat this
            // transcript as a brand new command, rather than demanding an exact "no".
            if (HasPendingConfirmation)
            {
                var lower = transcript.ToLowerInvariant();
                var confirmed = System.Array.Exists(AffirmativeWords, w => ContainsWord(lower, w));
                if (confirmed)
                {
                    Debug.Log($"[VoiceCommandController] Confirmed pending action: {PendingConfirmationText}");
                    ApplyPendingIntent();
                    ClearPending();
                    return;
                }

                Debug.Log("[VoiceCommandController] Pending confirmation not confirmed -- discarding it.");
                ClearPending();
            }

            if (spawner == null)
                return;

            if (llmClient != null)
            {
                llmClient.ParseIntent(transcript, (spawnIntent, editIntent, generateIntent, llmLatencyMs, llmError, lowConfidence) =>
                {
                    LastLlmLatencyMs = llmLatencyMs;
                    LastTotalLatencyMs = LastSttLatencyMs + llmLatencyMs;
                    LastLlmError = llmError ?? "";

                    if (lowConfidence && (spawnIntent.HasValue || editIntent.HasValue || generateIntent.HasValue))
                    {
                        pendingSpawnIntent = spawnIntent;
                        pendingEditIntent = editIntent;
                        pendingGenerateIntent = generateIntent;
                        PendingConfirmationText = $"Did you mean: {DescribeIntent(spawnIntent, editIntent, generateIntent)}? Say \"yes\" to confirm.";
                        Debug.Log($"[VoiceCommandController] Low-confidence intent ({llmLatencyMs:0}ms) -- awaiting confirmation: {PendingConfirmationText}");
                        return;
                    }

                    if (spawnIntent.HasValue)
                    {
                        Debug.Log($"[VoiceCommandController] LLM parsed create intent in {llmLatencyMs:0}ms " +
                            $"(total {LastTotalLatencyMs:0}ms).");
                        spawner.Spawn(spawnIntent.Value);
                    }
                    else if (editIntent.HasValue)
                    {
                        Debug.Log($"[VoiceCommandController] LLM parsed edit intent in {llmLatencyMs:0}ms " +
                            $"(total {LastTotalLatencyMs:0}ms).");
                        ApplyEdit(editIntent.Value);
                    }
                    else if (generateIntent.HasValue)
                    {
                        Debug.Log($"[VoiceCommandController] LLM parsed {(generateIntent.Value.IsRecall ? "recall" : "generate")} " +
                            $"intent in {llmLatencyMs:0}ms (total {LastTotalLatencyMs:0}ms): \"{generateIntent.Value.Prompt}\"");
                        if (generateIntent.Value.IsRecall)
                            spawner.SpawnFromLibrary(generateIntent.Value.Prompt);
                        else
                            spawner.SpawnGenerating(generateIntent.Value.Prompt);
                    }
                    else if (!string.IsNullOrEmpty(llmError))
                    {
                        Debug.LogWarning($"[VoiceCommandController] LLM request failed: {llmError} " +
                            "-- falling back to keyword parser.");
                        TryLocalFallback(transcript);
                    }
                    else
                    {
                        Debug.Log($"[VoiceCommandController] LLM did not recognize a command " +
                            $"({llmLatencyMs:0}ms) -- falling back to keyword parser.");
                        TryLocalFallback(transcript);
                    }
                });
            }
            else
            {
                TryLocalFallback(transcript);
            }
        }

        void ApplyPendingIntent()
        {
            if (pendingSpawnIntent.HasValue)
                spawner.Spawn(pendingSpawnIntent.Value);
            else if (pendingEditIntent.HasValue)
                ApplyEdit(pendingEditIntent.Value);
            else if (pendingGenerateIntent.HasValue)
            {
                if (pendingGenerateIntent.Value.IsRecall)
                    spawner.SpawnFromLibrary(pendingGenerateIntent.Value.Prompt);
                else
                    spawner.SpawnGenerating(pendingGenerateIntent.Value.Prompt);
            }
        }

        void ClearPending()
        {
            pendingSpawnIntent = null;
            pendingEditIntent = null;
            pendingGenerateIntent = null;
            PendingConfirmationText = "";
        }

        static string DescribeIntent(SpawnIntent? spawn, EditIntent? edit, GenerateIntent? generate)
        {
            if (spawn.HasValue)
                return $"spawn a {spawn.Value.Shape}";
            if (generate.HasValue)
                return generate.Value.IsRecall
                    ? $"bring back \"{generate.Value.Prompt}\" from the library"
                    : $"generate \"{generate.Value.Prompt}\"";
            if (edit.HasValue)
                return $"{edit.Value.Action} the current object";
            return "that";
        }

        static bool ContainsWord(string text, string word)
        {
            var index = text.IndexOf(word, System.StringComparison.Ordinal);
            while (index >= 0)
            {
                var leftOk = index == 0 || !char.IsLetter(text[index - 1]);
                var rightIndex = index + word.Length;
                var rightOk = rightIndex >= text.Length || !char.IsLetter(text[rightIndex]);
                if (leftOk && rightOk)
                    return true;
                index = text.IndexOf(word, index + 1, System.StringComparison.Ordinal);
            }
            return false;
        }

        void TryLocalFallback(string transcript)
        {
            // Stage 10: checked before anything else -- requires an explicit trigger word
            // ("from earlier", "again", "before", ...) so it can never collide with a genuine new
            // "spawn a lamp" request, and short-circuits straight to the asset library rather than
            // going through ApplyEdit/Spawn (a recall isn't really an edit or a spawn intent).
            if (VoiceIntentParser.TryParseRecall(transcript, out var recallQuery))
            {
                spawner.SpawnFromLibrary(recallQuery);
                return;
            }

            // Stage 10: same reasoning as the recall check above -- unambiguous multi-word
            // phrases (save/load/export), checked early so nothing else could shadow them.
            if (VoiceIntentParser.TryParseSaveLoad(transcript, out var saveLoadIntent))
            {
                ApplyEdit(saveLoadIntent);
                return;
            }

            // Rendering-pipeline viz: same reasoning as the save/load check above.
            if (VoiceIntentParser.TryParsePipelineView(transcript, out var pipelineIntent))
            {
                ApplyEdit(pipelineIntent);
                return;
            }

            // Collision follow-up: same reasoning as the pipeline-view check above.
            if (VoiceIntentParser.TryParseResetOrientation(transcript, out var resetOrientationIntent))
            {
                ApplyEdit(resetOrientationIntent);
                return;
            }

            // It only ever matches when an explicit scene-referring word is present ("room"/
            // "lighting"/"ambiance"/...), so it can never collide with a normal per-object edit --
            // but it DOES need to win over the generic
            // bigger/smaller check inside TryParseEditAction for the phrases it does match
            // ("make the room brighter" is scene-wide, not a request to resize the current object).
            if (VoiceIntentParser.TryParseLighting(transcript, out var lightingIntent))
            {
                ApplyEdit(lightingIntent);
                return;
            }

            // Stage 9: same reasoning as the lighting check above -- "try a different style" has
            // its own unambiguous phrase vocabulary that never overlaps with a plain edit verb, so
            // it needs to be checked before the generic edit-action pass could ever get a chance
            // to miss it.
            if (VoiceIntentParser.TryParseRerollStyle(transcript, out var rerollIntent))
            {
                ApplyEdit(rerollIntent);
                return;
            }

            // Edit-action verbs (bigger/smaller/delete/rotate/duplicate/move) are checked before
            // shape matching, since they should win even if a shape noun also appears in the same
            // sentence ("make the table bigger" is an edit, not a request to spawn a new table).
            if (VoiceIntentParser.TryParseEditAction(transcript, out var editAction))
            {
                ApplyEdit(editAction);
                return;
            }

            if (VoiceIntentParser.TryParse(transcript, out var intent))
            {
                spawner.Spawn(intent);
                return;
            }

            if (VoiceIntentParser.TryParseRecolor(transcript, out var recolorIntent))
            {
                ApplyEdit(recolorIntent);
                return;
            }

            if (VoiceIntentParser.TryParseRetexture(transcript, out var retextureIntent))
            {
                ApplyEdit(retextureIntent);
                return;
            }

            Debug.Log($"[VoiceCommandController] No shape or edit action recognized in: \"{transcript}\"");
        }

        void ApplyEdit(EditIntent intent)
        {
            // Unlike every other edit action, ClearAll has no single target -- it should work
            // even with nothing pointed at or previously touched (e.g. the very first command,
            // or resetting an already-empty room).
            if (intent.Action == EditAction.ClearAll)
            {
                spawner.ClearAll();
                return;
            }

            // AdjustLighting likewise has no per-object target -- it's the scene's own
            // directional/ambient light, not whatever's pointed at or last touched.
            if (intent.Action == EditAction.AdjustLighting)
            {
                spawner.AdjustLighting(intent.LightBrighter, intent.ResizeMultiplier, intent.Color);
                return;
            }

            // Stage 9: same "no single object" shape as ClearAll/AdjustLighting -- reverts
            // whatever the most recent mutating command was, regardless of what's currently
            // pointed at or last touched.
            if (intent.Action == EditAction.Undo)
            {
                spawner.Undo();
                return;
            }

            // Stage 10: same "no single object" shape -- persists/restores the whole room.
            if (intent.Action == EditAction.SaveScene)
            {
                spawner.SaveScene();
                return;
            }
            if (intent.Action == EditAction.LoadScene)
            {
                spawner.LoadScene();
                return;
            }

            var target = objectSelector != null ? objectSelector.GetPointedAtObject() : null;
            if (target == null)
                target = spawner.LastTouchedGameObject;

            if (target == null)
            {
                Debug.Log("[VoiceCommandController] No object to edit -- nothing pointed at or previously touched.");
                return;
            }

            switch (intent.Action)
            {
                case EditAction.Resize: spawner.Resize(target, intent.Bigger, intent.ResizeMultiplier); break;
                case EditAction.Recolor: spawner.Recolor(target, intent.Color ?? Color.white); break;
                case EditAction.Move:
                    spawner.Move(target, intent.Relation, intent.ReferenceShape,
                        intent.MoveDistanceMeters, intent.MoveDirectionValue);
                    break;
                case EditAction.Rotate: spawner.Rotate(target, intent.RotateDegrees ?? 90f); break;
                case EditAction.Duplicate: spawner.Duplicate(target); break;
                case EditAction.Delete: spawner.Delete(target); break;
                case EditAction.Retexture:
                    if (intent.Material.HasValue)
                        spawner.Retexture(target, intent.Material.Value);
                    break;
                case EditAction.RerollStyle: spawner.RerollStyle(target); break;
                case EditAction.ExportMesh: spawner.ExportMesh(target); break;
                case EditAction.ShowWireframe: spawner.ShowWireframe(target); break;
                case EditAction.ShowUvMapping: spawner.ShowUvMapping(target); break;
                case EditAction.ShowNormalRendering: spawner.ShowNormalRendering(target); break;
                case EditAction.ResetOrientation: spawner.ResetOrientation(target); break;
            }
        }
    }
}

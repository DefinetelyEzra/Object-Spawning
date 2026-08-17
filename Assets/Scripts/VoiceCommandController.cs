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

            if (spawner == null)
                return;

            if (llmClient != null)
            {
                llmClient.ParseIntent(transcript, (spawnIntent, editIntent, generateIntent, llmLatencyMs, llmError) =>
                {
                    LastLlmLatencyMs = llmLatencyMs;
                    LastTotalLatencyMs = LastSttLatencyMs + llmLatencyMs;
                    LastLlmError = llmError ?? "";

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
                        Debug.Log($"[VoiceCommandController] LLM parsed generate intent in {llmLatencyMs:0}ms " +
                            $"(total {LastTotalLatencyMs:0}ms): \"{generateIntent.Value.Prompt}\"");
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

        void TryLocalFallback(string transcript)
        {
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
                case EditAction.Resize: spawner.Resize(target, intent.Bigger); break;
                case EditAction.Recolor: spawner.Recolor(target, intent.Color); break;
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
            }
        }
    }
}

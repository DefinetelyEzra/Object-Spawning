using UnityEngine;
using UnityEngine.UI;

namespace ObjectSpawning
{
    // Stage 0's "simple wrist-menu": a small world-space panel showing FPS and spawn count.
    // Builds its own UI at runtime so the scene only needs this component parented to a controller.
    [RequireComponent(typeof(RectTransform))]
    public class DebugHud : MonoBehaviour
    {
        [SerializeField] PrimitiveSpawner primitiveSpawner;
        [SerializeField] VoiceCommandController voiceCommandController;
        [SerializeField] ObjectSelector objectSelector;
        [SerializeField] float updateInterval = 0.15f;

        Text label;
        float smoothedFps;
        float timer;

        void Awake()
        {
            var canvasGO = new GameObject("DebugCanvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
            canvasGO.transform.SetParent(transform, false);

            var canvas = canvasGO.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var canvasRect = canvasGO.GetComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(340f, 280f);
            canvasGO.transform.localPosition = new Vector3(0f, 0.05f, 0.05f);
            canvasGO.transform.localRotation = Quaternion.Euler(45f, 0f, 0f);
            canvasGO.transform.localScale = Vector3.one * 0.001f;

            var labelGO = new GameObject("Label", typeof(RectTransform), typeof(Text));
            labelGO.transform.SetParent(canvasGO.transform, false);
            var labelRect = labelGO.GetComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;

            label = labelGO.GetComponent<Text>();
            label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            label.fontSize = 14;
            label.alignment = TextAnchor.UpperLeft;
            label.color = Color.green;
        }

        void Update()
        {
            timer += Time.unscaledDeltaTime;
            if (timer < updateInterval)
                return;
            timer = 0f;

            smoothedFps = 1f / Time.unscaledDeltaTime;
            int spawnCount = primitiveSpawner != null ? primitiveSpawner.SpawnCount : 0;
            var text = $"FPS: {smoothedFps:0}\nSpawned: {spawnCount}";

            if (primitiveSpawner != null)
            {
                var pointed = objectSelector != null ? objectSelector.GetPointedAtObject() : null;
                var target = pointed != null ? pointed : primitiveSpawner.LastTouchedGameObject;
                var source = pointed != null ? "pointed" : "last touched";
                text += $"\nEdit target: {(target != null ? $"{target.name} ({source})" : "(none)")}";

                if (primitiveSpawner.PendingGenerationCount > 0)
                    text += $"\nGenerating: {primitiveSpawner.PendingGenerationCount}";
                if (!string.IsNullOrEmpty(primitiveSpawner.LastGenerationError))
                    text += $"\nGEN ERR: {Truncate(primitiveSpawner.LastGenerationError)}";
            }

            if (voiceCommandController != null)
            {
                text += $"\nBtn: {(voiceCommandController.IsButtonHeld ? "HELD" : "-")}" +
                    $"  Mic: {(voiceCommandController.IsMicListening ? "ON" : "off")}" +
                    $"\nLevel: {voiceCommandController.LastMicLevel:0.00}" +
                    $"\nPartial: {Truncate(voiceCommandController.LastPartialTranscript)}" +
                    $"\nLast: {Truncate(voiceCommandController.LastFullTranscript)}";

                if (voiceCommandController.LastTotalLatencyMs > 0f)
                {
                    text += $"\nSTT:{voiceCommandController.LastSttLatencyMs:0}ms " +
                        $"LLM:{voiceCommandController.LastLlmLatencyMs:0}ms " +
                        $"Total:{voiceCommandController.LastTotalLatencyMs:0}ms";
                }

                if (!string.IsNullOrEmpty(voiceCommandController.LastLlmError))
                    text += $"\nLLM ERR: {Truncate(voiceCommandController.LastLlmError)}";

                if (!string.IsNullOrEmpty(voiceCommandController.LastError))
                    text += $"\nERROR: {Truncate(voiceCommandController.LastError)}";
            }

            label.text = text;
        }

        static string Truncate(string s)
        {
            const int maxLen = 38;
            if (string.IsNullOrEmpty(s))
                return "";
            return s.Length <= maxLen ? s : s.Substring(0, maxLen) + "...";
        }
    }
}

using UnityEngine;
using UnityEngine.UI;

namespace ObjectSpawning
{
    // Playground scene's guest-facing wrist panel -- shows what a visitor cares about (object
    // count, what's currently selected, mic status) without DebugHud's raw latency numbers and
    // error strings, which read as a developer debug panel rather than something meant for the
    // public to see.
    [RequireComponent(typeof(RectTransform))]
    public class GuestHud : MonoBehaviour
    {
        [SerializeField] PrimitiveSpawner primitiveSpawner;
        [SerializeField] VoiceCommandController voiceCommandController;
        [SerializeField] ObjectSelector objectSelector;
        [SerializeField] float updateInterval = 0.15f;

        Text label;
        float timer;

        void Awake()
        {
            var canvasGO = new GameObject("GuestCanvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
            canvasGO.transform.SetParent(transform, false);

            var canvas = canvasGO.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var canvasRect = canvasGO.GetComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(320f, 220f);
            canvasGO.transform.localPosition = new Vector3(0f, 0.05f, 0.05f);
            canvasGO.transform.localRotation = Quaternion.Euler(45f, 0f, 0f);
            canvasGO.transform.localScale = Vector3.one * 0.001f;

            var bgGO = new GameObject("Background", typeof(RectTransform), typeof(Image));
            bgGO.transform.SetParent(canvasGO.transform, false);
            var bgRect = bgGO.GetComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.offsetMin = Vector2.zero;
            bgRect.offsetMax = Vector2.zero;
            bgGO.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.5f);

            var labelGO = new GameObject("Label", typeof(RectTransform), typeof(Text));
            labelGO.transform.SetParent(canvasGO.transform, false);
            var labelRect = labelGO.GetComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = new Vector2(10f, 10f);
            labelRect.offsetMax = new Vector2(-10f, -10f);

            label = labelGO.GetComponent<Text>();
            label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            label.fontSize = 16;
            label.alignment = TextAnchor.UpperLeft;
            label.color = Color.white;
        }

        void Update()
        {
            timer += Time.unscaledDeltaTime;
            if (timer < updateInterval)
                return;
            timer = 0f;

            var count = primitiveSpawner != null ? primitiveSpawner.SpawnCount : 0;
            var text = $"Objects: {count}\n";

            if (primitiveSpawner != null)
            {
                var pointed = objectSelector != null ? objectSelector.GetPointedAtObject() : null;
                var target = pointed != null ? pointed : primitiveSpawner.LastTouchedGameObject;
                text += target != null ? $"Selected: {FriendlyName(target.name)}\n" : "Selected: (none)\n";
            }

            if (voiceCommandController != null)
                text += voiceCommandController.IsMicListening ? "\nListening..." : "\nHold left trigger to talk";

            text += "\n\nSay \"clear the room\" to reset";

            label.text = text;
        }

        // "SpawnedCube_3" / "Generating_1" -> "Cube" / "Generating" -- strips the internal
        // prefix/index, which means nothing to a visitor.
        static string FriendlyName(string rawName)
        {
            var name = rawName.StartsWith("Spawned") ? rawName.Substring("Spawned".Length) : rawName;
            var underscoreIndex = name.IndexOf('_');
            return underscoreIndex > 0 ? name.Substring(0, underscoreIndex) : name;
        }
    }
}

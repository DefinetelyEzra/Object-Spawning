using UnityEngine;
using UnityEngine.UI;

namespace ObjectSpawning
{
    // Stage 1: head-locked caption overlay. Only visible while push-to-talk is held,
    // showing the live partial transcript so the user can see what's being heard as they speak.
    [RequireComponent(typeof(RectTransform))]
    public class LiveTranscriptDisplay : MonoBehaviour
    {
        [SerializeField] VoiceCommandController voiceCommandController;

        GameObject panel;
        Text label;

        void Awake()
        {
            panel = new GameObject("TranscriptCanvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
            panel.transform.SetParent(transform, false);

            var canvas = panel.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var canvasRect = panel.GetComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(700f, 140f);
            panel.transform.localPosition = new Vector3(0f, -0.2f, 0.8f);
            panel.transform.localRotation = Quaternion.identity;
            panel.transform.localScale = Vector3.one * 0.001f;

            var bgGO = new GameObject("Background", typeof(RectTransform), typeof(Image));
            bgGO.transform.SetParent(panel.transform, false);
            var bgRect = bgGO.GetComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.offsetMin = Vector2.zero;
            bgRect.offsetMax = Vector2.zero;
            bgGO.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.65f);

            var labelGO = new GameObject("Label", typeof(RectTransform), typeof(Text));
            labelGO.transform.SetParent(panel.transform, false);
            var labelRect = labelGO.GetComponent<RectTransform>();
            labelRect.anchorMin = new Vector2(0.04f, 0.1f);
            labelRect.anchorMax = new Vector2(0.96f, 0.9f);
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;

            label = labelGO.GetComponent<Text>();
            label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            label.fontSize = 32;
            label.alignment = TextAnchor.MiddleCenter;
            label.color = Color.white;
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            label.verticalOverflow = VerticalWrapMode.Truncate;

            panel.SetActive(false);
        }

        void Update()
        {
            if (voiceCommandController == null)
                return;

            bool shouldShow = voiceCommandController.IsButtonHeld;
            if (panel.activeSelf != shouldShow)
                panel.SetActive(shouldShow);

            if (!shouldShow)
                return;

            var partial = voiceCommandController.LastPartialTranscript;
            label.text = string.IsNullOrEmpty(partial)
                ? (voiceCommandController.IsMicListening ? "Listening..." : "Connecting...")
                : partial;
        }
    }
}

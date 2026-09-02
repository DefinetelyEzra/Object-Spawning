using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace ObjectSpawning
{
    // Playground-scene tutorial popup: toggled by the left controller's Y button (secondaryButton),
    // lists what every bound button does on both controllers. Builds its own world-space canvas at
    // runtime, head-anchored so it always appears directly in front of the player.
    public class ControllerHelpPopup : MonoBehaviour
    {
        [SerializeField] Transform headTransform;
        [SerializeField] float distance = 0.7f;
        [SerializeField] bool startVisible = true;

        InputAction toggleAction;
        GameObject panelRoot;

        void Awake()
        {
            toggleAction = new InputAction(name: "ToggleControllerHelp", type: InputActionType.Button);
            toggleAction.AddBinding("<Keyboard>/h");
            toggleAction.AddBinding("<XRController>{LeftHand}/secondaryButton"); // Y button
            toggleAction.performed += _ => Toggle();

            BuildUI();
            panelRoot.SetActive(startVisible);
        }

        void OnEnable() => toggleAction.Enable();

        void OnDisable() => toggleAction.Disable();

        void Toggle() => panelRoot.SetActive(!panelRoot.activeSelf);

        void BuildUI()
        {
            var canvasGO = new GameObject("ControllerHelpCanvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
            panelRoot = canvasGO;
            canvasGO.transform.SetParent(headTransform != null ? headTransform : transform, false);

            var canvas = canvasGO.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var canvasRect = canvasGO.GetComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(560f, 480f);
            canvasGO.transform.localPosition = new Vector3(0f, 0f, distance);
            canvasGO.transform.localRotation = Quaternion.identity;
            canvasGO.transform.localScale = Vector3.one * 0.0015f;

            var bgGO = new GameObject("Background", typeof(RectTransform), typeof(Image));
            bgGO.transform.SetParent(canvasGO.transform, false);
            var bgRect = bgGO.GetComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.offsetMin = Vector2.zero;
            bgRect.offsetMax = Vector2.zero;
            bgGO.GetComponent<Image>().color = new Color(0.06f, 0.06f, 0.09f, 0.92f);

            var textGO = new GameObject("Label", typeof(RectTransform), typeof(Text));
            textGO.transform.SetParent(canvasGO.transform, false);
            var textRect = textGO.GetComponent<RectTransform>();
            textRect.anchorMin = new Vector2(0.06f, 0.06f);
            textRect.anchorMax = new Vector2(0.94f, 0.94f);
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            var text = textGO.GetComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = 24;
            text.alignment = TextAnchor.UpperLeft;
            text.color = Color.white;
            text.text =
                "CONTROLS\n\n" +
                "RIGHT HAND\n" +
                "  Trigger  -  Spawn a cube\n" +
                "  B Button  -  Hold + point to select an object\n" +
                "  Grip  -  Hold + point to grab and carry an object\n" +
                "  A Button  -  While grabbing, rotate 45°\n\n" +
                "LEFT HAND\n" +
                "  Trigger  -  Hold to talk (voice command)\n" +
                "  Y Button  -  Show / hide this help\n" +
                "  X Button  -  While grabbing, reset upright + face you\n\n" +
                "Try saying:\n" +
                "  \"spawn a red sphere\" / \"spawn a sofa\"\n" +
                "  \"make it bigger\" / \"make it 10 times bigger\"\n" +
                "  \"move it 3 meters to the left\"\n" +
                "  \"rotate it 180 degrees\"\n" +
                "  \"reset its orientation\" / \"face me\"\n" +
                "  \"move it onto the table\"\n" +
                "  \"make it look like rusted metal\" / \"make it glass\"\n" +
                "  \"spawn a light\" / \"make the room brighter\"\n" +
                "  \"try a different style\" / \"undo that\"\n" +
                "  \"save the scene\" / \"load the scene\"\n" +
                "  \"use the lamp from earlier\"\n" +
                "  \"export this mesh\"\n" +
                "  \"show the wireframe\" / \"show the UV map\"\n" +
                "  \"show the final render\"\n" +
                "  \"delete it\" / \"clear the room\"";
        }
    }
}

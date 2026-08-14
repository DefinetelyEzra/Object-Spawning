using UnityEngine;
using UnityEngine.InputSystem;

namespace ObjectSpawning
{
    // Stage 4: hold the right controller's B button (secondaryButton) to aim a laser at a
    // spawned object and set it as the edit target. Continuous, always-on raycasting proved
    // impossible to aim with no visual feedback, so pointing is now an explicit, visible action
    // gated behind a button. The selection is sticky -- it survives releasing the button, since
    // STT + LLM round trips take seconds and the player shouldn't have to hold the button the
    // entire time. VoiceCommandController falls back to "the last object created or touched"
    // when nothing has been pointed at yet, per the roadmap's own stated simplification.
    public class ObjectSelector : MonoBehaviour
    {
        [SerializeField] Transform pointerOrigin;
        [SerializeField] float maxDistance = 10f;
        [SerializeField] Color rayColor = Color.cyan;
        [SerializeField] float rayWidth = 0.005f;

        InputAction selectAction;
        LineRenderer lineRenderer;
        GameObject selectedObject;

        public GameObject GetPointedAtObject() => selectedObject;

        // Lets PrimitiveSpawner drop a stale sticky selection when it's no longer relevant (a
        // new object was just spawned, or the pointed-at object was just deleted) -- without
        // this, "make it bigger" would keep targeting whatever was pointed at minutes ago instead
        // of what the player most recently created or lost.
        public void ClearSelection() => selectedObject = null;

        void Awake()
        {
            selectAction = new InputAction(name: "SelectEditTarget", type: InputActionType.Button);
            selectAction.AddBinding("<XRController>{RightHand}/secondaryButton"); // B button

            lineRenderer = gameObject.AddComponent<LineRenderer>();
            lineRenderer.positionCount = 2;
            lineRenderer.startWidth = rayWidth;
            lineRenderer.endWidth = rayWidth;
            var rayShader = Shader.Find("Universal Render Pipeline/Unlit");
            if (rayShader != null)
                lineRenderer.material = new Material(rayShader);
            lineRenderer.startColor = rayColor;
            lineRenderer.endColor = rayColor;
            lineRenderer.enabled = false;
        }

        void OnEnable() => selectAction.Enable();

        void OnDisable()
        {
            selectAction.Disable();
            if (lineRenderer != null)
                lineRenderer.enabled = false;
        }

        void Update()
        {
            if (pointerOrigin == null)
                return;

            var held = selectAction.IsPressed();
            lineRenderer.enabled = held;

            if (!held)
                return;

            var origin = pointerOrigin.position;
            var direction = pointerOrigin.forward;

            if (Physics.Raycast(origin, direction, out var hit, maxDistance))
            {
                lineRenderer.SetPosition(0, origin);
                lineRenderer.SetPosition(1, hit.point);

                var info = hit.collider.GetComponentInParent<SpawnedObjectInfo>();
                if (info != null)
                    selectedObject = info.gameObject;
            }
            else
            {
                lineRenderer.SetPosition(0, origin);
                lineRenderer.SetPosition(1, origin + direction * maxDistance);
            }
        }
    }
}

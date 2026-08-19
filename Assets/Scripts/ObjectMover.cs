using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Jump;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Movement;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Turning;

namespace ObjectSpawning
{
    // Ray-grab: hold the right controller's grip to pick up whatever the beam is pointing at
    // and carry it by aiming, rotate it in fixed steps via the A/X face buttons, then release
    // to drop it snapped solidly to the floor. Fully kinematic/transform-driven throughout --
    // matches every other placement path in this project (ComputeOnGroundPosition etc.), no
    // physics/Rigidbody involved -- and reuses PrimitiveSpawner's registry tag
    // (SpawnedObjectInfo) the same way ObjectSelector does, so only legitimately spawned
    // objects are grabbable, never the floor/walls/rig itself.
    public class ObjectMover : MonoBehaviour
    {
        [SerializeField] Transform pointerOrigin;
        [SerializeField] PrimitiveSpawner primitiveSpawner;
        [SerializeField] float maxDistance = 10f;
        [SerializeField] float rotateStepDegrees = 45f;
        [SerializeField] float rayWidth = 0.006f;

        // Orange -- deliberately distinct from ObjectSelector's cyan pointer, so it's visually
        // obvious which mode is active if a guest tries both.
        [SerializeField] Color beamColor = new(1f, 0.6f, 0.1f);

        InputAction grabAction;
        InputAction rotateClockwiseAction;
        InputAction rotateCounterclockwiseAction;
        LineRenderer lineRenderer;

        GameObject heldObject;
        float holdDistance;

        // The XR rig's own default locomotion bindings (from XRI's shared "XRI Default Input
        // Actions" asset, wired up by the imported XR Origin prefab, not by this project's own
        // code) collide with the exact buttons this feature repurposes: the rig's own "Jump"
        // action is bound to the same right-hand primary button used here for clockwise
        // rotation, and its "Grab Move" locomotion technique is bound to the same right-hand
        // grip used here to grab an object -- confirmed in headset testing (pressing the rotate
        // button also made the player jump). Continuous Turn is suppressed too, for a related
        // but distinct reason: it changes the rig's own yaw, and since a held object's position
        // is recomputed from the controller's current world-space aim every frame, turning made
        // a held object visibly swing around the player -- also confirmed in headset testing.
        // Suppressed only for the duration of a grab, not disabled outright, since all three
        // should keep working normally the rest of the time. Continuous Move is deliberately
        // left alone -- walking while carrying something is normal, only turning caused the bug.
        JumpProvider jumpProvider;
        GrabMoveProvider[] grabMoveProviders;
        ContinuousTurnProvider continuousTurnProvider;

        void Awake()
        {
            jumpProvider = FindFirstObjectByType<JumpProvider>();
            grabMoveProviders = FindObjectsByType<GrabMoveProvider>(FindObjectsSortMode.None);
            continuousTurnProvider = FindFirstObjectByType<ContinuousTurnProvider>();

            grabAction = new InputAction(name: "GrabObject", type: InputActionType.Button);
            grabAction.AddBinding("<XRController>{RightHand}/gripButton");

            // Two separate single-purpose buttons rather than a stick, deliberately: the
            // thumbsticks are already busy with locomotion (move/turn) even while grabbing, and
            // a plain button press is inherently a single discrete step -- exactly the "strict
            // angles" rotation the roadmap asked for, with no continuous-drag case to debounce.
            rotateClockwiseAction = new InputAction(name: "RotateHeldObjectClockwise", type: InputActionType.Button);
            rotateClockwiseAction.AddBinding("<XRController>{RightHand}/primaryButton"); // A
            rotateCounterclockwiseAction = new InputAction(name: "RotateHeldObjectCounterclockwise", type: InputActionType.Button);
            rotateCounterclockwiseAction.AddBinding("<XRController>{LeftHand}/primaryButton"); // X

            rotateClockwiseAction.performed += _ => RotateHeld(rotateStepDegrees);
            rotateCounterclockwiseAction.performed += _ => RotateHeld(-rotateStepDegrees);

            lineRenderer = gameObject.AddComponent<LineRenderer>();
            lineRenderer.positionCount = 2;
            lineRenderer.startWidth = rayWidth;
            lineRenderer.endWidth = rayWidth;
            var rayShader = Shader.Find("Universal Render Pipeline/Unlit");
            if (rayShader != null)
                lineRenderer.material = new Material(rayShader);
            lineRenderer.startColor = beamColor;
            lineRenderer.endColor = beamColor;
            lineRenderer.enabled = false;
        }

        void OnEnable()
        {
            grabAction.Enable();
            rotateClockwiseAction.Enable();
            rotateCounterclockwiseAction.Enable();
        }

        void OnDisable()
        {
            grabAction.Disable();
            rotateClockwiseAction.Disable();
            rotateCounterclockwiseAction.Disable();
            if (lineRenderer != null)
                lineRenderer.enabled = false;
        }

        void Update()
        {
            if (pointerOrigin == null)
                return;

            var held = grabAction.IsPressed();
            lineRenderer.enabled = held;

            if (!held)
            {
                if (heldObject != null)
                    EndGrab();
                return;
            }

            var origin = pointerOrigin.position;
            var direction = pointerOrigin.forward;

            if (heldObject == null)
            {
                TryGrab(origin, direction);
            }
            else
            {
                // Follows the beam at the distance it was originally grabbed, not wherever a
                // fresh raycast happens to hit next -- otherwise it would fight against (or snap
                // onto) whatever's directly behind it along the same beam as you aim around.
                var targetPoint = origin + direction * holdDistance;
                heldObject.transform.position = targetPoint;
                lineRenderer.SetPosition(0, origin);
                lineRenderer.SetPosition(1, targetPoint);
            }
        }

        void TryGrab(Vector3 origin, Vector3 direction)
        {
            if (Physics.Raycast(origin, direction, out var hit, maxDistance))
            {
                lineRenderer.SetPosition(0, origin);
                lineRenderer.SetPosition(1, hit.point);

                var info = hit.collider.GetComponentInParent<SpawnedObjectInfo>();
                if (info != null)
                {
                    heldObject = info.gameObject;
                    holdDistance = hit.distance;
                    primitiveSpawner?.MarkAsTouched(heldObject);
                    SetConflictingLocomotionSuppressed(true);
                    Debug.Log($"[ObjectMover] Grabbed {heldObject.name} at distance {holdDistance:0.##}m.");
                }
            }
            else
            {
                lineRenderer.SetPosition(0, origin);
                lineRenderer.SetPosition(1, origin + direction * maxDistance);
            }
        }

        void EndGrab()
        {
            // Snapped to the floor on every release, not just when it happens to already be
            // near one -- "sticky to the ground" per the roadmap ask, so a guest can never leave
            // an object floating mid-air or clipped through the floor by releasing early/late.
            Debug.Log($"[ObjectMover] Released {heldObject.name} at {heldObject.transform.position}, snapping to ground.");
            primitiveSpawner?.SnapToGround(heldObject);
            primitiveSpawner?.MarkAsTouched(heldObject);
            Debug.Log($"[ObjectMover] {heldObject.name} landed at {heldObject.transform.position}.");
            SetConflictingLocomotionSuppressed(false);
            heldObject = null;
        }

        void RotateHeld(float degrees)
        {
            if (heldObject == null)
                return;
            heldObject.transform.Rotate(Vector3.up, degrees, Space.World);
            Debug.Log($"[ObjectMover] Rotated {heldObject.name} by {degrees:0.##} degrees " +
                $"(now facing {heldObject.transform.eulerAngles.y:0.#}°).");
        }

        void SetConflictingLocomotionSuppressed(bool suppressed)
        {
            if (jumpProvider != null)
                jumpProvider.enabled = !suppressed;
            if (continuousTurnProvider != null)
                continuousTurnProvider.enabled = !suppressed;
            foreach (var provider in grabMoveProviders)
            {
                if (provider != null)
                    provider.enabled = !suppressed;
            }
        }
    }
}

using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Jump;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Movement;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Turning;

namespace ObjectSpawning
{
    // Ray-grab: hold the right controller's grip to pick up whatever the beam is pointing at
    // and carry it by aiming, rotate it in fixed steps via the right-hand A button or straighten
    // it back out (upright, facing the player) via the left-hand X button, then release to drop
    // it. While held, the object is kinematic and follows the beam directly (frozen via
    // PrimitiveSpawner.FreezePhysics right when it's grabbed, in case it was still mid-settle from
    // something else); on release, PrimitiveSpawner.SettleObject hands it to real physics so it
    // falls and collides with whatever's actually below it -- the floor, or another object,
    // enabling real stacking -- then locks back to kinematic once it comes to rest. Reuses
    // PrimitiveSpawner's registry tag (SpawnedObjectInfo) the same way ObjectSelector does, so
    // only legitimately spawned objects are grabbable, never the floor/walls/rig itself.
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
        InputAction resetOrientationAction;
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
            // Originally counterclockwise rotation -- repurposed into a reset button (upright,
            // facing the player) instead, since physics settling can now leave a held object at an
            // arbitrary angle that a single fixed-step rotation can't reliably recover in one press.
            resetOrientationAction = new InputAction(name: "ResetHeldObjectOrientation", type: InputActionType.Button);
            resetOrientationAction.AddBinding("<XRController>{LeftHand}/primaryButton"); // X

            rotateClockwiseAction.performed += _ => RotateHeld(rotateStepDegrees);
            resetOrientationAction.performed += _ => ResetHeldOrientation();

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
            resetOrientationAction.Enable();
        }

        void OnDisable()
        {
            grabAction.Disable();
            rotateClockwiseAction.Disable();
            resetOrientationAction.Disable();
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
                    // Guards against grabbing something that's still mid-settle (e.g. a fast
                    // regrab right after release, or grabbing an object another one just landed
                    // near) -- without this, physics would fight the direct position control below
                    // for the rest of this settle's remaining lifetime.
                    primitiveSpawner?.FreezePhysics(heldObject);
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
            // Released to real physics on every release -- falls and collides with whatever's
            // actually below it (the floor, or another object -- this is what makes stacking one
            // object on another via ray-grab possible at all) rather than always being forced back
            // down to the floor regardless of what it was dropped onto.
            Debug.Log($"[ObjectMover] Released {heldObject.name} at {heldObject.transform.position}, settling.");
            primitiveSpawner?.SettleObject(heldObject);
            primitiveSpawner?.MarkAsTouched(heldObject);
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

        // Routed through PrimitiveSpawner (unlike RotateHeld's own direct transform.Rotate above)
        // so this gets the same undo support every other edit already has -- a mis-press doesn't
        // strand the object at a worse angle than before with no way back.
        void ResetHeldOrientation()
        {
            if (heldObject == null)
                return;
            primitiveSpawner?.ResetOrientation(heldObject);
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

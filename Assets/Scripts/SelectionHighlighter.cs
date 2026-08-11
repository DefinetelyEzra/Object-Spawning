using UnityEngine;

namespace ObjectSpawning
{
    // Playground-scene polish: shows a glowing disc under whichever object voice edit commands
    // would currently act on -- the same "pointed at, or else last touched" object
    // VoiceCommandController itself resolves to, made visible so a first-time visitor can tell
    // what "make it bigger" will affect once the selection laser (visible only while the button
    // is held) is gone.
    public class SelectionHighlighter : MonoBehaviour
    {
        [SerializeField] ObjectSelector objectSelector;
        [SerializeField] PrimitiveSpawner primitiveSpawner;
        [SerializeField] Color highlightColor = Color.cyan; // matches ObjectSelector's laser color
        [SerializeField] float pulseSpeed = 2.5f;

        Transform disc;

        void Awake()
        {
            var discGO = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            discGO.name = "SelectionHighlightDisc";
            if (discGO.TryGetComponent<Collider>(out var col))
                Destroy(col);

            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            var mat = new Material(shader != null ? shader : Shader.Find("Universal Render Pipeline/Lit"))
            {
                color = highlightColor,
            };
            discGO.GetComponent<Renderer>().sharedMaterial = mat;

            disc = discGO.transform;
            disc.SetParent(transform, false);
            disc.gameObject.SetActive(false);
        }

        void Update()
        {
            var target = objectSelector != null ? objectSelector.GetPointedAtObject() : null;
            if (target == null && primitiveSpawner != null)
                target = primitiveSpawner.LastTouchedGameObject;

            if (target == null)
            {
                disc.gameObject.SetActive(false);
                return;
            }

            var renderers = target.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
            {
                disc.gameObject.SetActive(false);
                return;
            }

            var bounds = renderers[0].bounds;
            for (var i = 1; i < renderers.Length; i++)
                bounds.Encapsulate(renderers[i].bounds);

            disc.gameObject.SetActive(true);
            var radius = Mathf.Max(bounds.extents.x, bounds.extents.z) * 1.3f;
            var pulse = 1f + Mathf.Sin(Time.time * pulseSpeed) * 0.05f;
            disc.position = new Vector3(bounds.center.x, bounds.min.y + 0.005f, bounds.center.z);
            disc.localScale = new Vector3(radius * 2f * pulse, 0.01f, radius * 2f * pulse);
        }
    }
}

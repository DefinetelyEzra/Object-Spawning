using UnityEngine;

namespace ObjectSpawning
{
    // Playground-scene polish: a small glowing marker that continuously follows
    // PrimitiveSpawner.GetSpawnPreviewPosition() -- where the next spawned object would land --
    // so visitors get an always-visible, intuitive sense of where to look right after giving a
    // spawn command instead of having to guess.
    public class SpawnPreviewMarker : MonoBehaviour
    {
        [SerializeField] PrimitiveSpawner primitiveSpawner;
        [SerializeField] Color markerColor = new(1f, 0.75f, 0.2f); // warm amber -- distinct from the cyan selection highlight
        [SerializeField] float diameter = 0.35f;
        [SerializeField] float pulseSpeed = 1.8f;

        Transform marker;

        void Awake()
        {
            var markerGO = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            markerGO.name = "SpawnPreviewMarker";
            if (markerGO.TryGetComponent<Collider>(out var col))
                Destroy(col);

            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            var mat = new Material(shader != null ? shader : Shader.Find("Universal Render Pipeline/Lit"))
            {
                color = markerColor,
            };
            markerGO.GetComponent<Renderer>().sharedMaterial = mat;

            marker = markerGO.transform;
            marker.SetParent(transform, false);
        }

        void Update()
        {
            if (primitiveSpawner == null)
            {
                marker.gameObject.SetActive(false);
                return;
            }

            marker.gameObject.SetActive(true);
            var pulse = 1f + Mathf.Sin(Time.time * pulseSpeed) * 0.08f;
            var position = primitiveSpawner.GetSpawnPreviewPosition();
            marker.position = new Vector3(position.x, position.y + 0.01f, position.z);
            marker.localScale = new Vector3(diameter * pulse, 0.01f, diameter * pulse);
        }
    }
}

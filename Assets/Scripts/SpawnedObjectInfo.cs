using UnityEngine;

namespace ObjectSpawning
{
    // Stage 4: tag component marking a GameObject as a tracked, editable spawned object.
    // Attached by PrimitiveSpawner to everything it creates so edit commands (resize/recolor/
    // move/rotate/duplicate/delete) and pointer-based selection can find it.
    public class SpawnedObjectInfo : MonoBehaviour
    {
        public int Id;
        public PrimitiveShape Shape;
    }
}

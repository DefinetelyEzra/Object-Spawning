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

        // Stage 10: only set when Shape == Generated -- the original text-to-3D prompt, kept
        // around so Save/Load can recreate the object from its source description (and so the
        // asset library can offer it back up by name) instead of needing a durable mesh URL,
        // which Tripo3D's own result links don't provide (they expire minutes after generation).
        public string GeneratedPrompt = "";
    }
}

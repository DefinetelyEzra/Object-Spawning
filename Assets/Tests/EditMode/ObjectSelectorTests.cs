using NUnit.Framework;
using UnityEngine;

namespace ObjectSpawning.Tests
{
    // Physics.Raycast is not reliably simulated in Edit Mode (Unity only actively updates the
    // physics broadphase during Play Mode), so actual hit/no-hit raycast behavior isn't testable
    // here without producing false confidence either way. That's verified live, in-headset,
    // instead. This only covers the pure logic that doesn't depend on physics running.
    public class ObjectSelectorTests
    {
        [Test]
        public void GetPointedAtObject_WithoutPointerOrigin_ReturnsNull()
        {
            var go = new GameObject("TestSelector");
            var selector = go.AddComponent<ObjectSelector>();

            var result = selector.GetPointedAtObject();

            Assert.IsNull(result);

            Object.DestroyImmediate(go);
        }
    }
}

using NUnit.Framework;
using UnityEngine;

namespace ObjectSpawning.Tests
{
    public class ProceduralGeometryFactoryTests
    {
        static readonly (PrimitiveShape Shape, int ExpectedPartCount)[] Composites =
        {
            (PrimitiveShape.Table, 5),   // tabletop + 4 legs
            (PrimitiveShape.Shelf, 6),   // 4 boards + 2 side panels
            (PrimitiveShape.Crate, 1),   // single body
            (PrimitiveShape.Chair, 6),   // seat + 4 legs + back
        };

        [Test]
        public void Build_EachCompositeShape_ProducesExpectedPartCount()
        {
            foreach (var (shape, expectedCount) in Composites)
            {
                var root = ProceduralGeometryFactory.Build(shape);
                Assert.IsNotNull(root, $"{shape} should produce a root GameObject.");

                var renderers = root.GetComponentsInChildren<Renderer>();
                Assert.AreEqual(expectedCount, renderers.Length,
                    $"{shape} should have {expectedCount} parts, found {renderers.Length}.");

                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void Build_EachCompositeShape_HasNoDegenerateParts()
        {
            foreach (var (shape, _) in Composites)
            {
                var root = ProceduralGeometryFactory.Build(shape);

                foreach (var renderer in root.GetComponentsInChildren<Renderer>())
                {
                    var scale = renderer.transform.localScale;
                    Assert.Greater(scale.x, 0f, $"{shape}/{renderer.name}: x scale should be positive.");
                    Assert.Greater(scale.y, 0f, $"{shape}/{renderer.name}: y scale should be positive.");
                    Assert.Greater(scale.z, 0f, $"{shape}/{renderer.name}: z scale should be positive.");

                    var mesh = renderer.GetComponent<MeshFilter>()?.sharedMesh;
                    Assert.IsNotNull(mesh, $"{shape}/{renderer.name}: should have a mesh.");
                    Assert.Greater(mesh.vertexCount, 0, $"{shape}/{renderer.name}: mesh should have vertices.");
                }

                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void Build_UnrecognizedPrimitiveShape_ReturnsNull()
        {
            var result = ProceduralGeometryFactory.Build(PrimitiveShape.Cube);
            Assert.IsNull(result, "Build() is only for composite shapes; plain primitives go through CreatePrimitive directly.");
        }
    }
}

using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace ObjectSpawning.Tests
{
    public class PrimitiveSpawnerTests
    {
        // Spawn() calls renderer.material, which logs an edit-mode-only warning about
        // instantiating materials outside Play Mode. Harmless at runtime; expected here.
        static void ExpectMaterialInstantiateWarning() =>
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Instantiating material.*"));

        static void ExpectMaterialInstantiateWarnings(int count)
        {
            for (var i = 0; i < count; i++)
                ExpectMaterialInstantiateWarning();
        }

        [Test]
        public void Spawn_CreatesPrimitiveWithIntentShapeColorAndScale()
        {
            var go = new GameObject("TestSpawner");
            var spawner = go.AddComponent<PrimitiveSpawner>();

            ExpectMaterialInstantiateWarning();
            spawner.Spawn(new SpawnIntent(PrimitiveShape.Sphere, Color.red, 0.4f));

            var spawned = GameObject.Find("SpawnedSphere_0");
            Assert.IsNotNull(spawned, "Expected a spawned object named 'SpawnedSphere_0'.");
            Assert.AreEqual(Vector3.one * 0.4f, spawned.transform.localScale);
            Assert.AreEqual(Color.red, spawned.GetComponent<Renderer>().material.color);
            Assert.AreEqual(1, spawner.SpawnCount);
            Assert.AreEqual(spawner.LastSpawnPosition, spawned.transform.position);

            Object.DestroyImmediate(spawned);
            Object.DestroyImmediate(go);
        }

        [Test]
        public void Spawn_CalledTwice_IncrementsCountAndCreatesDistinctObjects()
        {
            var go = new GameObject("TestSpawner");
            var spawner = go.AddComponent<PrimitiveSpawner>();

            ExpectMaterialInstantiateWarning();
            spawner.Spawn(new SpawnIntent(PrimitiveShape.Cube, Color.white, 0.2f));
            ExpectMaterialInstantiateWarning();
            spawner.Spawn(new SpawnIntent(PrimitiveShape.Cylinder, Color.blue, 0.2f));

            var first = GameObject.Find("SpawnedCube_0");
            var second = GameObject.Find("SpawnedCylinder_1");

            Assert.IsNotNull(first);
            Assert.IsNotNull(second);
            Assert.AreEqual(2, spawner.SpawnCount);

            Object.DestroyImmediate(first);
            Object.DestroyImmediate(second);
            Object.DestroyImmediate(go);
        }

        [Test]
        public void Spawn_CompositeShape_AppliesColorToEveryPart()
        {
            var go = new GameObject("TestSpawner");
            var spawner = go.AddComponent<PrimitiveSpawner>();

            ExpectMaterialInstantiateWarnings(5); // table = tabletop + 4 legs
            spawner.Spawn(new SpawnIntent(PrimitiveShape.Table, Color.green, VoiceIntentParser.DefaultScale));

            var spawned = GameObject.Find("SpawnedTable_0");
            Assert.IsNotNull(spawned, "Expected a spawned object named 'SpawnedTable_0'.");

            var renderers = spawned.GetComponentsInChildren<Renderer>();
            Assert.AreEqual(5, renderers.Length);
            foreach (var renderer in renderers)
                Assert.AreEqual(Color.green, renderer.material.color, $"{renderer.name} should be colored.");

            Object.DestroyImmediate(spawned);
            Object.DestroyImmediate(go);
        }

        [Test]
        public void Spawn_CompositeShape_DefaultSizeMapsToUnitScale()
        {
            var go = new GameObject("TestSpawner");
            var spawner = go.AddComponent<PrimitiveSpawner>();

            ExpectMaterialInstantiateWarnings(1); // crate = single body
            spawner.Spawn(new SpawnIntent(PrimitiveShape.Crate, Color.white, VoiceIntentParser.DefaultScale));

            var spawned = GameObject.Find("SpawnedCrate_0");
            Assert.AreEqual(Vector3.one, spawned.transform.localScale,
                "Default-size composite should keep its canonical (unscaled) proportions.");

            Object.DestroyImmediate(spawned);
            Object.DestroyImmediate(go);
        }

        [Test]
        public void Spawn_CompositeShape_LargeSizeScalesUpProportionally()
        {
            var go = new GameObject("TestSpawner");
            var spawner = go.AddComponent<PrimitiveSpawner>();

            ExpectMaterialInstantiateWarnings(1); // crate = single body
            spawner.Spawn(new SpawnIntent(PrimitiveShape.Crate, Color.white, VoiceIntentParser.BigScale));

            var spawned = GameObject.Find("SpawnedCrate_0");
            var expectedFactor = VoiceIntentParser.BigScale / VoiceIntentParser.DefaultScale;
            Assert.AreEqual(Vector3.one * expectedFactor, spawned.transform.localScale);

            Object.DestroyImmediate(spawned);
            Object.DestroyImmediate(go);
        }

        [Test]
        public void Spawn_WithoutHeadTransform_FallsBackToLocalForwardOffset()
        {
            var go = new GameObject("TestSpawner");
            var spawner = go.AddComponent<PrimitiveSpawner>();

            ExpectMaterialInstantiateWarning();
            spawner.Spawn(new SpawnIntent(PrimitiveShape.Cube, Color.white, 0.2f));

            var spawned = GameObject.Find("SpawnedCube_0");
            Assert.AreEqual(go.transform.position + Vector3.forward * 1.5f, spawned.transform.position);

            Object.DestroyImmediate(spawned);
            Object.DestroyImmediate(go);
        }
    }
}

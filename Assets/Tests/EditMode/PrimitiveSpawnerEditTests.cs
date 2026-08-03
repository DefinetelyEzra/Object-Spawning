using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace ObjectSpawning.Tests
{
    public class PrimitiveSpawnerEditTests
    {
        static void ExpectMaterialInstantiateWarning() =>
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Instantiating material.*"));

        static (GameObject spawnerGO, PrimitiveSpawner spawner, GameObject target) SpawnOne()
        {
            var go = new GameObject("TestSpawner");
            var spawner = go.AddComponent<PrimitiveSpawner>();
            ExpectMaterialInstantiateWarning();
            var target = spawner.Spawn(new SpawnIntent(PrimitiveShape.Cube, Color.white, VoiceIntentParser.DefaultScale));
            return (go, spawner, target);
        }

        [Test]
        public void Spawn_SetsLastTouchedGameObject()
        {
            var (spawnerGO, spawner, target) = SpawnOne();

            Assert.AreEqual(target, spawner.LastTouchedGameObject);

            Object.DestroyImmediate(target);
            Object.DestroyImmediate(spawnerGO);
        }

        [Test]
        public void Resize_Bigger_ScalesUp()
        {
            var (spawnerGO, spawner, target) = SpawnOne();
            var before = target.transform.localScale;

            spawner.Resize(target, bigger: true);

            Assert.Greater(target.transform.localScale.x, before.x);

            Object.DestroyImmediate(target);
            Object.DestroyImmediate(spawnerGO);
        }

        [Test]
        public void Resize_Smaller_ScalesDown()
        {
            var (spawnerGO, spawner, target) = SpawnOne();
            var before = target.transform.localScale;

            spawner.Resize(target, bigger: false);

            Assert.Less(target.transform.localScale.x, before.x);

            Object.DestroyImmediate(target);
            Object.DestroyImmediate(spawnerGO);
        }

        [Test]
        public void Recolor_ChangesAllRendererColors()
        {
            var (spawnerGO, spawner, target) = SpawnOne();

            // No new "Instantiating material" warning expected here: the renderer's material was
            // already instanced (no longer equal to the shared asset) during the initial Spawn()
            // above, so a further .material access on the same renderer doesn't re-instantiate.
            spawner.Recolor(target, Color.red);

            foreach (var renderer in target.GetComponentsInChildren<Renderer>())
                Assert.AreEqual(Color.red, renderer.material.color);

            Object.DestroyImmediate(target);
            Object.DestroyImmediate(spawnerGO);
        }

        [Test]
        public void Rotate_RotatesAroundYAxis()
        {
            var (spawnerGO, spawner, target) = SpawnOne();
            var before = target.transform.rotation;

            spawner.Rotate(target);

            Assert.AreNotEqual(before, target.transform.rotation);

            Object.DestroyImmediate(target);
            Object.DestroyImmediate(spawnerGO);
        }

        [Test]
        public void Duplicate_CreatesIndependentCopyWithOwnMaterial()
        {
            var (spawnerGO, spawner, target) = SpawnOne();
            var countBefore = spawner.SpawnCount;

            // Instantiate() clones the source GameObject, so the copy's Renderer starts out
            // referencing the *same* material instance as the source's -- but Unity's warning
            // is keyed per-Renderer-component, not per-Material-object, so the copy's Renderer
            // hasn't itself called .material yet. ApplyColor(copy, ...) triggers exactly one
            // fresh "instantiating material" warning here.
            ExpectMaterialInstantiateWarning();
            var copy = spawner.Duplicate(target);

            Assert.IsNotNull(copy);
            Assert.AreNotEqual(target, copy);
            Assert.AreEqual(countBefore + 1, spawner.SpawnCount);
            Assert.AreEqual(copy, spawner.LastTouchedGameObject);

            // Mutating the copy's color must not affect the original -- proves they don't share a material.
            spawner.Recolor(copy, Color.blue);
            Assert.AreNotEqual(Color.blue, target.GetComponent<Renderer>().material.color);

            Object.DestroyImmediate(target);
            Object.DestroyImmediate(copy);
            Object.DestroyImmediate(spawnerGO);
        }

        [Test]
        public void Delete_DestroysTargetAndClearsLastTouchedIfItWasTheTarget()
        {
            var (spawnerGO, spawner, target) = SpawnOne();

            spawner.Delete(target);

            Assert.IsNull(spawner.LastTouchedGameObject);

            Object.DestroyImmediate(spawnerGO);
        }

        [Test]
        public void Spawn_WithOnRelation_PlacesOnTopOfReference()
        {
            var go = new GameObject("TestSpawner");
            var spawner = go.AddComponent<PrimitiveSpawner>();

            ExpectMaterialInstantiateWarning();
            var baseObj = spawner.Spawn(new SpawnIntent(PrimitiveShape.Cube, Color.white, 1f));

            ExpectMaterialInstantiateWarning();
            var onTop = spawner.Spawn(new SpawnIntent(PrimitiveShape.Cube, Color.white, 0.2f,
                SpatialRelation.On, PrimitiveShape.Cube));

            var baseRenderer = baseObj.GetComponent<Renderer>();
            var topRenderer = onTop.GetComponent<Renderer>();

            Assert.AreEqual(baseRenderer.bounds.max.y, topRenderer.bounds.min.y, 0.001f);
            Assert.AreEqual(baseRenderer.bounds.center.x, topRenderer.bounds.center.x, 0.001f);
            Assert.AreEqual(baseRenderer.bounds.center.z, topRenderer.bounds.center.z, 0.001f);

            Object.DestroyImmediate(baseObj);
            Object.DestroyImmediate(onTop);
            Object.DestroyImmediate(go);
        }

        [Test]
        public void Spawn_WithNextToRelation_PlacesBesideReferenceWithoutOverlap()
        {
            var go = new GameObject("TestSpawner");
            var spawner = go.AddComponent<PrimitiveSpawner>();

            ExpectMaterialInstantiateWarning();
            var baseObj = spawner.Spawn(new SpawnIntent(PrimitiveShape.Cube, Color.white, 1f));

            ExpectMaterialInstantiateWarning();
            var beside = spawner.Spawn(new SpawnIntent(PrimitiveShape.Cube, Color.white, 0.4f,
                SpatialRelation.NextTo, PrimitiveShape.Cube));

            var baseRenderer = baseObj.GetComponent<Renderer>();
            var besideRenderer = beside.GetComponent<Renderer>();

            Assert.LessOrEqual(baseRenderer.bounds.max.x, besideRenderer.bounds.min.x + 0.001f);
            Assert.AreEqual(baseRenderer.bounds.min.y, besideRenderer.bounds.min.y, 0.001f);

            Object.DestroyImmediate(baseObj);
            Object.DestroyImmediate(beside);
            Object.DestroyImmediate(go);
        }

        [Test]
        public void Spawn_WithOnGroundRelation_PlacesBottomAtFloorLevel()
        {
            var go = new GameObject("TestSpawner");
            go.transform.position = new Vector3(0f, 5f, 0f); // well above the floor, to prove the override
            var spawner = go.AddComponent<PrimitiveSpawner>();

            ExpectMaterialInstantiateWarning();
            var result = spawner.Spawn(new SpawnIntent(PrimitiveShape.Cube, Color.white,
                VoiceIntentParser.DefaultScale, SpatialRelation.OnGround));

            var renderer = result.GetComponent<Renderer>();
            Assert.AreEqual(0f, renderer.bounds.min.y, 0.001f);

            Object.DestroyImmediate(result);
            Object.DestroyImmediate(go);
        }

        [Test]
        public void Spawn_WithRelationButNoMatchingReference_FallsBackToDefaultPosition()
        {
            var go = new GameObject("TestSpawner");
            var spawner = go.AddComponent<PrimitiveSpawner>();

            // ResolveSpawnPosition (which logs the fallback warning) runs before ApplyColor
            // (which triggers the material-instantiate warning), so expectations must be
            // registered in that same order -- LogAssert.Expect matches against actual Debug.Log
            // calls in sequence.
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("No existing Table found.*"));
            ExpectMaterialInstantiateWarning();
            var result = spawner.Spawn(new SpawnIntent(PrimitiveShape.Cube, Color.white, VoiceIntentParser.DefaultScale,
                SpatialRelation.On, PrimitiveShape.Table));

            Assert.IsNotNull(result);
            Assert.AreEqual(spawner.LastSpawnPosition, result.transform.position);

            Object.DestroyImmediate(result);
            Object.DestroyImmediate(go);
        }

        [Test]
        public void Move_WithOnRelation_PlacesOnTopOfReference()
        {
            var go = new GameObject("TestSpawner");
            var spawner = go.AddComponent<PrimitiveSpawner>();

            ExpectMaterialInstantiateWarning();
            var baseObj = spawner.Spawn(new SpawnIntent(PrimitiveShape.Cube, Color.white, 1f));
            ExpectMaterialInstantiateWarning();
            var mover = spawner.Spawn(new SpawnIntent(PrimitiveShape.Sphere, Color.white, 0.2f));

            spawner.Move(mover, SpatialRelation.On, PrimitiveShape.Cube);

            var baseRenderer = baseObj.GetComponent<Renderer>();
            var moverRenderer = mover.GetComponent<Renderer>();
            Assert.AreEqual(baseRenderer.bounds.max.y, moverRenderer.bounds.min.y, 0.001f);

            Object.DestroyImmediate(baseObj);
            Object.DestroyImmediate(mover);
            Object.DestroyImmediate(go);
        }

        [Test]
        public void Move_WithOnGroundRelation_PlacesBottomAtFloorLevel()
        {
            var go = new GameObject("TestSpawner");
            var spawner = go.AddComponent<PrimitiveSpawner>();

            ExpectMaterialInstantiateWarning();
            var target = spawner.Spawn(new SpawnIntent(PrimitiveShape.Cube, Color.white, VoiceIntentParser.DefaultScale));
            target.transform.position += new Vector3(0f, 5f, 0f); // simulate it having drifted up

            spawner.Move(target, SpatialRelation.OnGround);

            var renderer = target.GetComponent<Renderer>();
            Assert.AreEqual(0f, renderer.bounds.min.y, 0.001f);

            Object.DestroyImmediate(target);
            Object.DestroyImmediate(go);
        }

        [Test]
        public void Move_NextToSameShapeAsSelf_ExcludesSelfAsReference()
        {
            // Moving a cube "next to the cube" when the target itself is the only cube registered
            // must not resolve to itself -- should fall back to the default position instead.
            var go = new GameObject("TestSpawner");
            var spawner = go.AddComponent<PrimitiveSpawner>();

            ExpectMaterialInstantiateWarning();
            var target = spawner.Spawn(new SpawnIntent(PrimitiveShape.Cube, Color.white, VoiceIntentParser.DefaultScale));

            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("No existing Cube found.*"));
            spawner.Move(target, SpatialRelation.NextTo, PrimitiveShape.Cube);

            Assert.AreEqual(spawner.LastSpawnPosition, target.transform.position);

            Object.DestroyImmediate(target);
            Object.DestroyImmediate(go);
        }

        [Test]
        public void EditMethods_WithNullTarget_DoNotThrow()
        {
            var go = new GameObject("TestSpawner");
            var spawner = go.AddComponent<PrimitiveSpawner>();

            Assert.DoesNotThrow(() =>
            {
                spawner.Resize(null, true);
                spawner.Recolor(null, Color.red);
                spawner.Move(null);
                spawner.Rotate(null);
                spawner.Delete(null);
                spawner.Duplicate(null);
            });

            Object.DestroyImmediate(go);
        }
    }
}

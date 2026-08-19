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
        public void Resize_BiggerWithMultiplier_ScalesByExactFactor()
        {
            var (spawnerGO, spawner, target) = SpawnOne();
            var before = target.transform.localScale;

            spawner.Resize(target, bigger: true, multiplier: 10f);

            Assert.AreEqual(before.x * 10f, target.transform.localScale.x, 0.001f);

            Object.DestroyImmediate(target);
            Object.DestroyImmediate(spawnerGO);
        }

        [Test]
        public void Resize_SmallerWithMultiplier_ScalesByExactDivisor()
        {
            var (spawnerGO, spawner, target) = SpawnOne();
            var before = target.transform.localScale;

            spawner.Resize(target, bigger: false, multiplier: 2f);

            Assert.AreEqual(before.x * 0.5f, target.transform.localScale.x, 0.001f);

            Object.DestroyImmediate(target);
            Object.DestroyImmediate(spawnerGO);
        }

        [Test]
        public void Spawn_PointLight_CreatesRealLightComponent()
        {
            var go = new GameObject("TestSpawner");
            var spawner = go.AddComponent<PrimitiveSpawner>();
            ExpectMaterialInstantiateWarning();

            var target = spawner.Spawn(new SpawnIntent(PrimitiveShape.PointLight, Color.white, VoiceIntentParser.DefaultScale));

            Assert.IsNotNull(target);
            Assert.IsTrue(target.TryGetComponent<Light>(out var light));
            Assert.AreEqual(LightType.Point, light.type);

            Object.DestroyImmediate(target);
            Object.DestroyImmediate(go);
        }

        [Test]
        public void Spawn_PointLight_CapEnforced_RefusesFifthLight()
        {
            var go = new GameObject("TestSpawner");
            var spawner = go.AddComponent<PrimitiveSpawner>();
            var spawned = new System.Collections.Generic.List<GameObject>();

            for (var i = 0; i < 4; i++)
            {
                ExpectMaterialInstantiateWarning();
                spawned.Add(spawner.Spawn(new SpawnIntent(PrimitiveShape.PointLight, Color.white, VoiceIntentParser.DefaultScale)));
            }

            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("Point light cap.*"));
            var fifth = spawner.Spawn(new SpawnIntent(PrimitiveShape.PointLight, Color.white, VoiceIntentParser.DefaultScale));

            Assert.IsNull(fifth);

            foreach (var obj in spawned)
                Object.DestroyImmediate(obj);
            Object.DestroyImmediate(go);
        }

        [Test]
        public void Resize_LightTarget_AdjustsIntensityInsteadOfScale()
        {
            var go = new GameObject("TestSpawner");
            var spawner = go.AddComponent<PrimitiveSpawner>();
            ExpectMaterialInstantiateWarning();
            var target = spawner.Spawn(new SpawnIntent(PrimitiveShape.PointLight, Color.white, VoiceIntentParser.DefaultScale));
            var light = target.GetComponent<Light>();
            var beforeScale = target.transform.localScale;
            var beforeIntensity = light.intensity;

            spawner.Resize(target, bigger: true);

            Assert.AreEqual(beforeScale, target.transform.localScale);
            Assert.Greater(light.intensity, beforeIntensity);

            Object.DestroyImmediate(target);
            Object.DestroyImmediate(go);
        }

        [Test]
        public void Recolor_LightTarget_SetsLightColor()
        {
            var go = new GameObject("TestSpawner");
            var spawner = go.AddComponent<PrimitiveSpawner>();
            ExpectMaterialInstantiateWarning();
            var target = spawner.Spawn(new SpawnIntent(PrimitiveShape.PointLight, Color.white, VoiceIntentParser.DefaultScale));

            // No new "Instantiating material" warning expected here -- same reasoning as
            // Recolor_ChangesAllRendererColors: the renderer's material was already instanced
            // during Spawn above.
            spawner.Recolor(target, Color.red);

            Assert.AreEqual(Color.red, target.GetComponent<Light>().color);

            Object.DestroyImmediate(target);
            Object.DestroyImmediate(go);
        }

        [Test]
        public void Recolor_LightTarget_SetsBulbEmissionColor()
        {
            // The bulb's own surface sits essentially at its Light's origin and would otherwise
            // blow out to white under normal lit shading regardless of base color -- emission
            // bypasses that (confirmed against a real headset report of a white-looking bulb).
            var go = new GameObject("TestSpawner");
            var spawner = go.AddComponent<PrimitiveSpawner>();
            ExpectMaterialInstantiateWarning();
            var target = spawner.Spawn(new SpawnIntent(PrimitiveShape.PointLight, Color.white, VoiceIntentParser.DefaultScale));

            spawner.Recolor(target, Color.red);

            var material = target.GetComponent<Renderer>().material;
            Assert.IsTrue(material.IsKeywordEnabled("_EMISSION"));
            Assert.AreEqual(Color.red, material.GetColor("_EmissionColor"));

            Object.DestroyImmediate(target);
            Object.DestroyImmediate(go);
        }

        [Test]
        public void AdjustLighting_NoDirectionalLightWired_DoesNotThrow()
        {
            var go = new GameObject("TestSpawner");
            var spawner = go.AddComponent<PrimitiveSpawner>();

            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("No directional light wired.*"));
            Assert.DoesNotThrow(() => spawner.AdjustLighting(true, null, Color.blue));

            Object.DestroyImmediate(go);
        }

        [Test]
        public void AdjustLighting_Brighter_IncreasesDirectionalLightIntensity()
        {
            var spawnerGO = new GameObject("TestSpawner");
            var spawner = spawnerGO.AddComponent<PrimitiveSpawner>();
            var lightGO = new GameObject("TestDirectionalLight");
            var light = lightGO.AddComponent<Light>();
            light.intensity = 1f;

            var serialized = new UnityEditor.SerializedObject(spawner);
            serialized.FindProperty("directionalLight").objectReferenceValue = light;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            spawner.AdjustLighting(true, null, null);

            Assert.Greater(light.intensity, 1f);

            Object.DestroyImmediate(lightGO);
            Object.DestroyImmediate(spawnerGO);
        }

        [Test]
        public void AdjustLighting_ColorOnly_ChangesColorButNotIntensity()
        {
            var spawnerGO = new GameObject("TestSpawner");
            var spawner = spawnerGO.AddComponent<PrimitiveSpawner>();
            var lightGO = new GameObject("TestDirectionalLight");
            var light = lightGO.AddComponent<Light>();
            light.intensity = 1f;

            var serialized = new UnityEditor.SerializedObject(spawner);
            serialized.FindProperty("directionalLight").objectReferenceValue = light;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            spawner.AdjustLighting(null, null, Color.blue);

            Assert.AreEqual(1f, light.intensity, 0.0001f);
            Assert.AreEqual(Color.blue, light.color);

            Object.DestroyImmediate(lightGO);
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
        public void SnapToGround_ObjectAboveFloor_PlacesBottomAtFloorLevel()
        {
            var go = new GameObject("TestSpawner");
            var spawner = go.AddComponent<PrimitiveSpawner>();

            ExpectMaterialInstantiateWarning();
            var target = spawner.Spawn(new SpawnIntent(PrimitiveShape.Cube, Color.white, VoiceIntentParser.DefaultScale));
            target.transform.position += new Vector3(0.3f, 5f, -0.2f); // simulate having been carried through the air
            var beforeX = target.transform.position.x;
            var beforeZ = target.transform.position.z;

            spawner.SnapToGround(target);

            var renderer = target.GetComponent<Renderer>();
            Assert.AreEqual(0f, renderer.bounds.min.y, 0.001f);
            // X/Z should stay exactly where it was released -- only Y is corrected.
            Assert.AreEqual(beforeX, target.transform.position.x, 0.001f);
            Assert.AreEqual(beforeZ, target.transform.position.z, 0.001f);

            Object.DestroyImmediate(target);
            Object.DestroyImmediate(go);
        }

        [Test]
        public void SnapToGround_NullTarget_DoesNotThrow()
        {
            var go = new GameObject("TestSpawner");
            var spawner = go.AddComponent<PrimitiveSpawner>();

            Assert.DoesNotThrow(() => spawner.SnapToGround(null));

            Object.DestroyImmediate(go);
        }

        [Test]
        public void MarkAsTouched_SetsLastTouchedGameObject()
        {
            var go = new GameObject("TestSpawner");
            var spawner = go.AddComponent<PrimitiveSpawner>();

            ExpectMaterialInstantiateWarning();
            var first = spawner.Spawn(new SpawnIntent(PrimitiveShape.Cube, Color.white, VoiceIntentParser.DefaultScale));
            ExpectMaterialInstantiateWarning();
            var second = spawner.Spawn(new SpawnIntent(PrimitiveShape.Sphere, Color.white, VoiceIntentParser.DefaultScale));

            Assert.AreEqual(second, spawner.LastTouchedGameObject);

            spawner.MarkAsTouched(first);

            Assert.AreEqual(first, spawner.LastTouchedGameObject);

            Object.DestroyImmediate(first);
            Object.DestroyImmediate(second);
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

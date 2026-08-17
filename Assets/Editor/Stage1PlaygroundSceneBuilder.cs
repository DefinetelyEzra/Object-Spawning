using System.Linq;
using Meta.WitAi.Data.Configuration;
using ObjectSpawning;
using Oculus.Voice.Dictation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Readers;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Turning;

namespace ObjectSpawning.EditorTools
{
    // Builds the "Playground" exhibition scene: the same XR rig / voice / spawn pipeline Stage 0
    // already proved working, plus a bounded showroom environment, a controller-help popup, and
    // floor-anchored default spawning. Deliberately duplicates (rather than shares code with)
    // Stage0SceneBuilder so the working Stage 0 scene/tooling stays untouched.
    static class Stage1PlaygroundSceneBuilder
    {
        const string XROriginPrefabPath = "Assets/Samples/XR Interaction Toolkit/3.5.1/Starter Assets/Prefabs/XR Origin (XR Rig).prefab";
        const string TeleportAreaPrefabPath = "Assets/Samples/XR Interaction Toolkit/3.5.1/Starter Assets/DemoAssets/Prefabs/Teleport/Teleport Area.prefab";
        const string InteractionSimulatorPrefabPath = "Assets/Samples/XR Interaction Toolkit/3.5.1/XR Interaction Simulator/XR Interaction Simulator.prefab";
        const string ScenePath = "Assets/Scenes/Scene1Playground.unity";

        // Same LAN-IP reasoning as Stage0SceneBuilder -- the headset can't reach 127.0.0.1.
        const string BackendUrl = "http://192.168.0.193:8000";

        const float RoomHalfSize = 4f; // 8x8m interior
        const float WallHeight = 3f;
        const float WallThickness = 0.2f;

        [MenuItem("Tools/Object Spawning/Playground/Build Scene")]
        public static void BuildScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            BuildLighting();

            var rigPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(XROriginPrefabPath);
            if (rigPrefab == null)
            {
                Debug.LogError($"[Stage1PlaygroundSceneBuilder] Could not find XR Origin prefab at {XROriginPrefabPath}. Run 'Import XRI Samples' first.");
                return;
            }
            var rig = (GameObject)PrefabUtility.InstantiatePrefab(rigPrefab, scene);
            rig.transform.position = Vector3.zero;

            // Same fix as Stage 0: Snap Turn and Continuous Turn fight each other if both enabled.
            var snapTurn = rig.GetComponentInChildren<SnapTurnProvider>(true);
            if (snapTurn != null)
                snapTurn.enabled = false;
            var continuousTurn = rig.GetComponentInChildren<ContinuousTurnProvider>(true);
            if (continuousTurn != null)
            {
                continuousTurn.enabled = true;
                continuousTurn.rightHandTurnInput = CreateRawStickReader("Right Hand Turn Raw", "RightHand");
                continuousTurn.leftHandTurnInput = CreateRawStickReader("Left Hand Turn Raw", "LeftHand");
                continuousTurn.enableTurnAround = false;
            }

            var floorPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(TeleportAreaPrefabPath);
            if (floorPrefab == null)
            {
                Debug.LogError($"[Stage1PlaygroundSceneBuilder] Could not find Teleport Area prefab at {TeleportAreaPrefabPath}. Run 'Import XRI Samples' first.");
                return;
            }
            var floor = (GameObject)PrefabUtility.InstantiatePrefab(floorPrefab, scene);
            floor.transform.position = Vector3.zero;

            var simulatorPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(InteractionSimulatorPrefabPath);
            if (simulatorPrefab != null)
            {
                var simulator = (GameObject)PrefabUtility.InstantiatePrefab(simulatorPrefab, scene);
                simulator.SetActive(false);
            }
            else
            {
                Debug.LogWarning($"[Stage1PlaygroundSceneBuilder] Could not find XR Interaction Simulator prefab at {InteractionSimulatorPrefabPath}. Run 'Import XRI Samples' first.");
            }

            BuildEnvironment(floor);

            var managerGO = new GameObject("PlaygroundManager", typeof(PrimitiveSpawner));
            var primitiveSpawner = managerGO.GetComponent<PrimitiveSpawner>();

            var headTransform = rig.GetComponentsInChildren<Transform>(true)
                .FirstOrDefault(t => t.name == "Main Camera");

            var spawnerSerialized = new SerializedObject(primitiveSpawner);
            spawnerSerialized.FindProperty("baseMaterial").objectReferenceValue = GetOrCreateBaseMaterial();
            spawnerSerialized.FindProperty("floorReference").objectReferenceValue = floor.transform;

            if (headTransform != null)
            {
                spawnerSerialized.FindProperty("headTransform").objectReferenceValue = headTransform;
                spawnerSerialized.ApplyModifiedPropertiesWithoutUndo();
            }
            else
            {
                spawnerSerialized.ApplyModifiedPropertiesWithoutUndo();
                Debug.LogWarning("[Stage1PlaygroundSceneBuilder] Could not find 'Main Camera' in the XR Origin rig; spawn offset will fall back to world-forward.");
            }

            var rightController = rig.GetComponentsInChildren<Transform>(true)
                .FirstOrDefault(t => t.name == "Right Controller");

            ObjectSelector objectSelector = null;
            if (rightController != null)
            {
                var selectorGO = new GameObject("ObjectSelector");
                selectorGO.transform.SetParent(rightController, false);
                objectSelector = selectorGO.AddComponent<ObjectSelector>();

                var selectorSerialized = new SerializedObject(objectSelector);
                selectorSerialized.FindProperty("pointerOrigin").objectReferenceValue = selectorGO.transform;
                selectorSerialized.ApplyModifiedPropertiesWithoutUndo();

                // primitiveSpawner's own SerializedObject pass already ran earlier, before this
                // GameObject existed to reference -- a second pass here just adds this one field.
                var spawnerObjectSelectorSerialized = new SerializedObject(primitiveSpawner);
                spawnerObjectSelectorSerialized.FindProperty("objectSelector").objectReferenceValue = objectSelector;
                spawnerObjectSelectorSerialized.ApplyModifiedPropertiesWithoutUndo();
            }
            else
            {
                Debug.LogWarning("[Stage1PlaygroundSceneBuilder] Could not find 'Right Controller' in the XR Origin rig; pointer-based edit target selection will be unavailable.");
            }

            var voiceCommandController = AddVoiceCommandController(primitiveSpawner, objectSelector);

            if (headTransform != null && voiceCommandController != null)
            {
                var transcriptGO = new GameObject("LiveTranscriptDisplay", typeof(RectTransform));
                transcriptGO.transform.SetParent(headTransform, false);
                transcriptGO.AddComponent<LiveTranscriptDisplay>();

                var transcriptSerialized = new SerializedObject(transcriptGO.GetComponent<LiveTranscriptDisplay>());
                transcriptSerialized.FindProperty("voiceCommandController").objectReferenceValue = voiceCommandController;
                transcriptSerialized.ApplyModifiedPropertiesWithoutUndo();
            }

            var leftController = rig.GetComponentsInChildren<Transform>(true)
                .FirstOrDefault(t => t.name == "Left Controller");

            if (leftController != null)
            {
                // GuestHud, not DebugHud -- this scene faces the public, so the wrist panel
                // should read as a friendly status readout, not a developer debug dump.
                var hudGO = new GameObject("GuestHud", typeof(RectTransform));
                hudGO.transform.SetParent(leftController, false);
                hudGO.AddComponent<GuestHud>();

                var hudSerialized = new SerializedObject(hudGO.GetComponent<GuestHud>());
                hudSerialized.FindProperty("primitiveSpawner").objectReferenceValue = primitiveSpawner;
                hudSerialized.FindProperty("voiceCommandController").objectReferenceValue = voiceCommandController;
                hudSerialized.FindProperty("objectSelector").objectReferenceValue = objectSelector;
                hudSerialized.ApplyModifiedPropertiesWithoutUndo();
            }
            else
            {
                Debug.LogWarning("[Stage1PlaygroundSceneBuilder] Could not find 'Left Controller' in the XR Origin rig; skipping guest HUD attachment.");
            }

            var highlighterGO = new GameObject("SelectionHighlighter", typeof(SelectionHighlighter));
            var highlighterSerialized = new SerializedObject(highlighterGO.GetComponent<SelectionHighlighter>());
            highlighterSerialized.FindProperty("objectSelector").objectReferenceValue = objectSelector;
            highlighterSerialized.FindProperty("primitiveSpawner").objectReferenceValue = primitiveSpawner;
            highlighterSerialized.ApplyModifiedPropertiesWithoutUndo();

            var previewGO = new GameObject("SpawnPreviewMarker", typeof(SpawnPreviewMarker));
            var previewSerialized = new SerializedObject(previewGO.GetComponent<SpawnPreviewMarker>());
            previewSerialized.FindProperty("primitiveSpawner").objectReferenceValue = primitiveSpawner;
            previewSerialized.ApplyModifiedPropertiesWithoutUndo();

            if (headTransform != null)
            {
                var popupGO = new GameObject("ControllerHelpPopup", typeof(ControllerHelpPopup));
                var popupSerialized = new SerializedObject(popupGO.GetComponent<ControllerHelpPopup>());
                popupSerialized.FindProperty("headTransform").objectReferenceValue = headTransform;
                popupSerialized.ApplyModifiedPropertiesWithoutUndo();
            }
            else
            {
                Debug.LogWarning("[Stage1PlaygroundSceneBuilder] Could not find 'Main Camera'; skipping controller-help popup attachment.");
            }

            EditorSceneManager.SaveScene(scene, ScenePath);
            AddSceneToBuildSettings(ScenePath);

            Debug.Log($"[Stage1PlaygroundSceneBuilder] Playground scene built and saved to {ScenePath}.");
        }

        // Minimalist showroom: a clean bounded floor + four walls sized around the existing
        // Teleport Area, a soft gradient skybox, and gallery-style ambient lighting. Kept
        // deliberately simple (plain URP/Lit materials, primitive geometry, no custom shaders)
        // so it's fast to build and has zero pipeline-compatibility risk before an exhibition.
        static void BuildEnvironment(GameObject floor)
        {
            var floorRenderer = floor.GetComponentInChildren<Renderer>();
            var floorTopY = floorRenderer != null ? floorRenderer.bounds.max.y : 0f;

            var floorMat = CreateMaterial("PlaygroundFloor", new Color(0.82f, 0.80f, 0.77f));
            var wallMat = CreateMaterial("PlaygroundWall", new Color(0.93f, 0.93f, 0.95f));

            // Recessed 2cm below the Teleport Area's own surface -- sitting exactly coplanar with
            // it (the original version of this) made the teleport raycast hit whichever collider
            // won an unpredictable floating-point tie each frame, seen as flickering/unstable
            // teleportation. The 2cm gap alone resolves that: any realistic downward-ish teleport
            // ray now unambiguously reaches the Teleport Area's surface first, since it's higher.
            // Keeps its own collider (unlike the first fix, which removed it) -- this whole 8x8m
            // room is visually walkable, but the Teleport Area prefab's own collider only ever
            // covered its original, smaller footprint, so anyone physically walking (not
            // teleporting) onto the wider showroom floor outside that footprint had no ground at
            // all and fell through.
            const float floorRecess = 0.02f;
            var visualFloor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            visualFloor.name = "ShowroomFloor";
            visualFloor.transform.position = new Vector3(0f, floorTopY - floorRecess - 0.05f, 0f);
            visualFloor.transform.localScale = new Vector3(RoomHalfSize * 2f, 0.1f, RoomHalfSize * 2f);
            visualFloor.GetComponent<Renderer>().sharedMaterial = floorMat;

            var wallY = floorTopY + WallHeight / 2f;
            var span = RoomHalfSize * 2f + WallThickness;
            BuildWall("Wall_North", new Vector3(0f, wallY, RoomHalfSize), new Vector3(span, WallHeight, WallThickness), wallMat);
            BuildWall("Wall_South", new Vector3(0f, wallY, -RoomHalfSize), new Vector3(span, WallHeight, WallThickness), wallMat);
            BuildWall("Wall_East", new Vector3(RoomHalfSize, wallY, 0f), new Vector3(WallThickness, WallHeight, span), wallMat);
            BuildWall("Wall_West", new Vector3(-RoomHalfSize, wallY, 0f), new Vector3(WallThickness, WallHeight, span), wallMat);

            BuildSkybox();

            // Baked after the skybox/walls exist so the bake actually captures this finished room
            // rather than an empty scene. See Stage0SceneBuilder's own copy of this method for the
            // full "why baked, why box projection" reasoning -- same rationale applies here.
            BuildReflectionProbe(new Vector3(0f, wallY, 0f), new Vector3(RoomHalfSize * 2f, WallHeight, RoomHalfSize * 2f));
        }

        const string ReflectionProbeFolder = "Assets/Reflections";

        static void BuildReflectionProbe(Vector3 center, Vector3 size)
        {
            var probeGO = new GameObject("Room Reflection Probe", typeof(ReflectionProbe));
            probeGO.transform.position = center;

            var probe = probeGO.GetComponent<ReflectionProbe>();
            probe.mode = ReflectionProbeMode.Baked;
            probe.boxProjection = true;
            probe.size = size;
            probe.resolution = 128;
            probe.intensity = 1f;

            if (!AssetDatabase.IsValidFolder(ReflectionProbeFolder))
                AssetDatabase.CreateFolder("Assets", "Reflections");

            UnityEditor.Lightmapping.BakeReflectionProbe(probe, $"{ReflectionProbeFolder}/PlaygroundRoomProbe.exr");
        }

        static void BuildWall(string name, Vector3 position, Vector3 scale, Material mat)
        {
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.name = name;
            wall.transform.position = position;
            wall.transform.localScale = scale;
            wall.GetComponent<Renderer>().sharedMaterial = mat;
        }

        static void BuildSkybox()
        {
            var shader = Shader.Find("Skybox/Procedural");
            if (shader == null)
            {
                Debug.LogWarning("[Stage1PlaygroundSceneBuilder] Skybox/Procedural shader not found -- " +
                    "falling back to a solid camera background.");
                var cam = Camera.main;
                if (cam != null)
                {
                    cam.clearFlags = CameraClearFlags.SolidColor;
                    cam.backgroundColor = new Color(0.85f, 0.88f, 0.92f);
                }
                return;
            }

            var skyMat = new Material(shader);
            skyMat.SetColor("_SkyTint", new Color(0.85f, 0.88f, 0.95f));
            skyMat.SetColor("_GroundColor", new Color(0.72f, 0.70f, 0.67f));
            skyMat.SetFloat("_SunSize", 0.02f);
            skyMat.SetFloat("_AtmosphereThickness", 0.6f);
            skyMat.SetFloat("_Exposure", 1.1f);

            if (!AssetDatabase.IsValidFolder("Assets/Materials"))
                AssetDatabase.CreateFolder("Assets", "Materials");
            AssetDatabase.CreateAsset(skyMat, "Assets/Materials/PlaygroundSkybox.mat");

            RenderSettings.skybox = skyMat;
            RenderSettings.ambientMode = AmbientMode.Skybox;
        }

        static void BuildLighting()
        {
            var light = new GameObject("Directional Light", typeof(Light));
            light.transform.rotation = Quaternion.Euler(55f, -25f, 0f);
            var lightComp = light.GetComponent<Light>();
            lightComp.type = LightType.Directional;
            lightComp.intensity = 0.9f;
            lightComp.color = new Color(1f, 0.97f, 0.92f); // soft warm, gallery-style
            lightComp.shadows = LightShadows.Soft;
        }

        static Material CreateMaterial(string name, Color color)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            var mat = new Material(shader) { enableInstancing = true, color = color };

            if (!AssetDatabase.IsValidFolder("Assets/Materials"))
                AssetDatabase.CreateFolder("Assets", "Materials");
            AssetDatabase.CreateAsset(mat, $"Assets/Materials/{name}.mat");
            return mat;
        }

        static VoiceCommandController AddVoiceCommandController(PrimitiveSpawner primitiveSpawner, ObjectSelector objectSelector)
        {
            var witConfigGuids = AssetDatabase.FindAssets("t:WitConfiguration");
            if (witConfigGuids.Length == 0)
            {
                Debug.LogWarning("[Stage1PlaygroundSceneBuilder] No WitConfiguration asset found -- skipping voice setup.");
                return null;
            }

            var witConfigPath = AssetDatabase.GUIDToAssetPath(witConfigGuids[0]);
            var witConfig = AssetDatabase.LoadAssetAtPath<WitConfiguration>(witConfigPath);

            var dictationGO = new GameObject("AppDictationExperience", typeof(AppDictationExperience));
            var dictation = dictationGO.GetComponent<AppDictationExperience>();

            var dictationSerialized = new SerializedObject(dictation);
            dictationSerialized.FindProperty("runtimeConfiguration")
                .FindPropertyRelative("witConfiguration").objectReferenceValue = witConfig;
            dictationSerialized.ApplyModifiedPropertiesWithoutUndo();

            var voiceGO = new GameObject("VoiceCommandController",
                typeof(VoiceCommandController), typeof(LlmIntentClient), typeof(MeshGenerationClient));
            var voiceCommandController = voiceGO.GetComponent<VoiceCommandController>();
            var llmClient = voiceGO.GetComponent<LlmIntentClient>();
            var meshGenerationClient = voiceGO.GetComponent<MeshGenerationClient>();

            var voiceSerialized = new SerializedObject(voiceCommandController);
            voiceSerialized.FindProperty("dictation").objectReferenceValue = dictation;
            voiceSerialized.FindProperty("spawner").objectReferenceValue = primitiveSpawner;
            voiceSerialized.FindProperty("llmClient").objectReferenceValue = llmClient;
            voiceSerialized.FindProperty("objectSelector").objectReferenceValue = objectSelector;
            voiceSerialized.ApplyModifiedPropertiesWithoutUndo();

            var llmSerialized = new SerializedObject(llmClient);
            llmSerialized.FindProperty("backendUrl").stringValue = BackendUrl;
            llmSerialized.ApplyModifiedPropertiesWithoutUndo();

            var meshGenSerialized = new SerializedObject(meshGenerationClient);
            meshGenSerialized.FindProperty("backendUrl").stringValue = BackendUrl;
            meshGenSerialized.ApplyModifiedPropertiesWithoutUndo();

            var spawnerSerialized = new SerializedObject(primitiveSpawner);
            spawnerSerialized.FindProperty("meshGenerationClient").objectReferenceValue = meshGenerationClient;
            spawnerSerialized.ApplyModifiedPropertiesWithoutUndo();

            Debug.Log($"[Stage1PlaygroundSceneBuilder] Voice command controller wired to WitConfiguration at {witConfigPath}.");
            return voiceCommandController;
        }

        const string BaseMaterialPath = "Assets/Materials/RuntimePrimitive.mat";

        static Material GetOrCreateBaseMaterial()
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(BaseMaterialPath);
            if (existing != null)
                return existing;

            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                Debug.LogWarning("[Stage1PlaygroundSceneBuilder] Could not find 'Universal Render Pipeline/Lit' shader.");
                return null;
            }

            if (!AssetDatabase.IsValidFolder("Assets/Materials"))
                AssetDatabase.CreateFolder("Assets", "Materials");

            var material = new Material(shader) { enableInstancing = true };
            AssetDatabase.CreateAsset(material, BaseMaterialPath);
            AssetDatabase.SaveAssets();
            return material;
        }

        static XRInputValueReader<Vector2> CreateRawStickReader(string name, string hand)
        {
            var reader = new XRInputValueReader<Vector2>(name, XRInputValueReader.InputSourceMode.InputAction);
            reader.inputAction.AddBinding($"<XRController>{{{hand}}}/primary2DAxis").WithProcessor("StickDeadzone");
            return reader;
        }

        static void AddSceneToBuildSettings(string scenePath)
        {
            var scenes = EditorBuildSettings.scenes.ToList();
            if (scenes.Any(s => s.path == scenePath))
                return;

            scenes.Add(new EditorBuildSettingsScene(scenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }
    }
}

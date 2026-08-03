using System.Linq;
using Meta.WitAi.Data.Configuration;
using ObjectSpawning;
using Oculus.Voice.Dictation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Readers;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Turning;

namespace ObjectSpawning.EditorTools
{
    // Builds the Stage 0 "Silent Scaffold" scene: XR Origin, teleport floor, cube spawner, debug HUD.
    static class Stage0SceneBuilder
    {
        const string XROriginPrefabPath = "Assets/Samples/XR Interaction Toolkit/3.5.1/Starter Assets/Prefabs/XR Origin (XR Rig).prefab";
        const string TeleportAreaPrefabPath = "Assets/Samples/XR Interaction Toolkit/3.5.1/Starter Assets/DemoAssets/Prefabs/Teleport/Teleport Area.prefab";
        const string InteractionSimulatorPrefabPath = "Assets/Samples/XR Interaction Toolkit/3.5.1/XR Interaction Simulator/XR Interaction Simulator.prefab";
        const string ScenePath = "Assets/Scenes/Stage0_SilentScaffold.unity";

        // Backend is bound to 0.0.0.0:8000, so this LAN IP works from the Editor/Quest Link,
        // *and* from the real standalone APK on the headset (which can't reach 127.0.0.1 --
        // that's the headset's own loopback, not the PC's). Update if your PC's LAN IP changes.
        const string BackendUrl = "http://192.168.0.193:8000";

        [MenuItem("Tools/Object Spawning/Stage 0/Build Scene")]
        public static void BuildScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var light = new GameObject("Directional Light", typeof(Light));
            light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            var lightComp = light.GetComponent<Light>();
            lightComp.type = LightType.Directional;
            lightComp.intensity = 1f;

            var rigPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(XROriginPrefabPath);
            if (rigPrefab == null)
            {
                Debug.LogError($"[Stage0SceneBuilder] Could not find XR Origin prefab at {XROriginPrefabPath}. Run 'Import XRI Samples' first.");
                return;
            }
            var rig = (GameObject)PrefabUtility.InstantiatePrefab(rigPrefab, scene);
            rig.transform.position = Vector3.zero;

            // The rig ships with both Snap Turn and Continuous Turn enabled on the same object,
            // which fight each other every frame and produce choppy rotation. Keep only smooth turning.
            var snapTurn = rig.GetComponentInChildren<SnapTurnProvider>(true);
            if (snapTurn != null)
                snapTurn.enabled = false;
            var continuousTurn = rig.GetComponentInChildren<ContinuousTurnProvider>(true);
            if (continuousTurn != null)
            {
                continuousTurn.enabled = true;

                // The rig's default "Turn" action reuses Snap Turn's Sector interaction, which gates it to
                // discrete directional pulses instead of a continuous stream, so Continuous Turn never
                // receives a usable value. Give it its own clean raw reader with just a deadzone, matching
                // how "Move" is correctly configured.
                continuousTurn.rightHandTurnInput = CreateRawStickReader("Right Hand Turn Raw", "RightHand");
                continuousTurn.leftHandTurnInput = CreateRawStickReader("Left Hand Turn Raw", "LeftHand");

                // Continuous Turn has its own built-in 180 degree "turn around" snap on a South-direction
                // push, separate from Snap Turn's. It expects a sector-gated input; fed our raw unfiltered
                // stick it fires on any backward push, instantly spinning the rig and reading as a "teleport".
                // Not a feature we want -- disable it in favor of predictable, smooth-only turning.
                continuousTurn.enableTurnAround = false;
            }

            var floorPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(TeleportAreaPrefabPath);
            if (floorPrefab == null)
            {
                Debug.LogError($"[Stage0SceneBuilder] Could not find Teleport Area prefab at {TeleportAreaPrefabPath}. Run 'Import XRI Samples' first.");
                return;
            }
            var floor = (GameObject)PrefabUtility.InstantiatePrefab(floorPrefab, scene);
            floor.transform.position = Vector3.zero;

            var simulatorPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(InteractionSimulatorPrefabPath);
            if (simulatorPrefab != null)
            {
                // Editor-only: lets you fly the rig and press controller buttons with mouse/keyboard, no headset required.
                // Disabled by default -- it injects synthetic XR devices that conflict with a real connected headset.
                // Enable this object in the Hierarchy only when testing without hardware.
                var simulator = (GameObject)PrefabUtility.InstantiatePrefab(simulatorPrefab, scene);
                simulator.SetActive(false);
            }
            else
            {
                Debug.LogWarning($"[Stage0SceneBuilder] Could not find XR Interaction Simulator prefab at {InteractionSimulatorPrefabPath}. Run 'Import XRI Samples' first.");
            }

            var managerGO = new GameObject("Stage0Manager", typeof(PrimitiveSpawner));
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
                Debug.LogWarning("[Stage0SceneBuilder] Could not find 'Main Camera' in the XR Origin rig; spawn offset will fall back to world-forward.");
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
            }
            else
            {
                Debug.LogWarning("[Stage0SceneBuilder] Could not find 'Right Controller' in the XR Origin rig; pointer-based edit target selection will be unavailable.");
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
                var hudGO = new GameObject("DebugHud", typeof(RectTransform));
                hudGO.transform.SetParent(leftController, false);
                hudGO.AddComponent<DebugHud>();

                var hudSerialized = new SerializedObject(hudGO.GetComponent<DebugHud>());
                hudSerialized.FindProperty("primitiveSpawner").objectReferenceValue = primitiveSpawner;
                hudSerialized.FindProperty("voiceCommandController").objectReferenceValue = voiceCommandController;
                hudSerialized.FindProperty("objectSelector").objectReferenceValue = objectSelector;
                hudSerialized.ApplyModifiedPropertiesWithoutUndo();
            }
            else
            {
                Debug.LogWarning("[Stage0SceneBuilder] Could not find 'Left Controller' in the XR Origin rig; skipping debug HUD attachment.");
            }

            EditorSceneManager.SaveScene(scene, ScenePath);
            AddSceneToBuildSettings(ScenePath);

            Debug.Log($"[Stage0SceneBuilder] Stage 0 scene built and saved to {ScenePath}.");
        }

        static VoiceCommandController AddVoiceCommandController(PrimitiveSpawner primitiveSpawner, ObjectSelector objectSelector)
        {
            var witConfigGuids = AssetDatabase.FindAssets("t:WitConfiguration");
            if (witConfigGuids.Length == 0)
            {
                Debug.LogWarning("[Stage0SceneBuilder] No WitConfiguration asset found -- skipping voice setup. " +
                    "Run Meta > Voice SDK > Voice Hub and link your Wit.ai Server Access Token first, then rebuild the scene.");
                return null;
            }

            var witConfigPath = AssetDatabase.GUIDToAssetPath(witConfigGuids[0]);
            var witConfig = AssetDatabase.LoadAssetAtPath<WitConfiguration>(witConfigPath);

            // Default is 10s, which the headset's real-world WiFi round trip to Wit.ai's servers
            // has been observed to exceed intermittently ("Request Error: 14 ... timed out").
            witConfig.RequestTimeoutMs = 20000;
            EditorUtility.SetDirty(witConfig);

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

            // primitiveSpawner's own SerializedObject pass already ran earlier in BuildScene, before
            // this GameObject existed to reference -- a second pass here just adds this one field.
            var spawnerSerialized = new SerializedObject(primitiveSpawner);
            spawnerSerialized.FindProperty("meshGenerationClient").objectReferenceValue = meshGenerationClient;
            spawnerSerialized.ApplyModifiedPropertiesWithoutUndo();

            Debug.Log($"[Stage0SceneBuilder] Voice command controller wired to WitConfiguration at {witConfigPath}.");
            return voiceCommandController;
        }

        const string BaseMaterialFolder = "Assets/Materials";
        const string BaseMaterialPath = BaseMaterialFolder + "/RuntimePrimitive.mat";

        static Material GetOrCreateBaseMaterial()
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(BaseMaterialPath);
            if (existing != null)
                return existing;

            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                Debug.LogWarning("[Stage0SceneBuilder] Could not find 'Universal Render Pipeline/Lit' shader.");
                return null;
            }

            if (!AssetDatabase.IsValidFolder(BaseMaterialFolder))
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

            scenes.Insert(0, new EditorBuildSettingsScene(scenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }
    }
}

using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using UnityEngine.Rendering;

namespace ObjectSpawning.EditorTools
{
    // Configures Player Settings for a Meta Quest Android build and produces a sideloadable APK.
    static class AndroidBuildSetup
    {
        const string ApplicationIdentifier = "com.DefaultCompany.ObjectSpawning";
        const string ApkOutputPath = "Builds/Android/ObjectSpawning.apk";

        static readonly string[] RuntimeCreatedShaderNames =
        {
            "Universal Render Pipeline/Lit",
            "Universal Render Pipeline/Simple Lit",
            "Universal Render Pipeline/Unlit",
        };

        [MenuItem("Tools/Object Spawning/Stage 1/Configure Android Player Settings")]
        public static void ConfigurePlayerSettings()
        {
            PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, ApplicationIdentifier);
            EnsureRuntimeShadersAlwaysIncluded();

            // Meta's documented minimum for Quest compatibility.
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel29;

            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.colorSpace = ColorSpace.Linear;

            // Unity's own UnityWebRequest security policy blocks plain HTTP regardless of platform,
            // separate from (and in addition to) Android's manifest-level usesCleartextTraffic flag.
            // Our local dev backend is intentionally http:// (no TLS cert for a LAN dev server).
            PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;

            Debug.Log($"[AndroidBuildSetup] Application identifier set to '{ApplicationIdentifier}', " +
                "minSdk 29, IL2CPP, ARM64, Linear color space, insecure HTTP always allowed.");
        }

        [MenuItem("Tools/Object Spawning/Stage 1/Build Android APK")]
        public static void BuildApk()
        {
            ConfigurePlayerSettings();

            var scenes = EditorBuildSettings.scenes
                .Where(s => s.enabled)
                .Select(s => s.path)
                .ToArray();

            if (scenes.Length == 0)
            {
                Debug.LogError("[AndroidBuildSetup] No enabled scenes in Build Settings. Aborting build.");
                return;
            }

            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = ApkOutputPath,
                target = BuildTarget.Android,
                options = BuildOptions.None,
            };

            var report = BuildPipeline.BuildPlayer(options);
            var summary = report.summary;

            Debug.Log($"[AndroidBuildSetup] Build result: {summary.result}, " +
                $"{summary.totalErrors} errors, {summary.totalWarnings} warnings, " +
                $"output: {summary.outputPath}, size: {summary.totalSize} bytes.");
        }

        // GameObject.CreatePrimitive() assigns URP's default Lit material at runtime, but nothing in
        // any scene/asset references that shader statically, so the build-time shader stripper drops
        // it. Result on-device: primitives render with Unity's pink/magenta error shader, which also
        // doesn't handle Single Pass Instanced stereo correctly (explains the visibility/scale glitches
        // alongside the color). Force these shaders to always be included so they can't be stripped.
        static void EnsureRuntimeShadersAlwaysIncluded()
        {
            var graphicsSettings = AssetDatabase.LoadAssetAtPath<GraphicsSettings>("ProjectSettings/GraphicsSettings.asset");
            var settingsObject = new SerializedObject(graphicsSettings);
            var shaderListProperty = settingsObject.FindProperty("m_AlwaysIncludedShaders");

            foreach (var shaderName in RuntimeCreatedShaderNames)
            {
                var shader = Shader.Find(shaderName);
                if (shader == null)
                {
                    Debug.LogWarning($"[AndroidBuildSetup] Shader '{shaderName}' not found, skipping.");
                    continue;
                }

                bool alreadyIncluded = false;
                for (int i = 0; i < shaderListProperty.arraySize; i++)
                {
                    if (shaderListProperty.GetArrayElementAtIndex(i).objectReferenceValue == shader)
                    {
                        alreadyIncluded = true;
                        break;
                    }
                }

                if (alreadyIncluded)
                    continue;

                shaderListProperty.InsertArrayElementAtIndex(shaderListProperty.arraySize);
                shaderListProperty.GetArrayElementAtIndex(shaderListProperty.arraySize - 1).objectReferenceValue = shader;
            }

            settingsObject.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}

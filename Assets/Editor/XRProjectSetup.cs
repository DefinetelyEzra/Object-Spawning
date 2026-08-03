using UnityEditor;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
using UnityEngine.XR.OpenXR.Features.Interactions;
using UnityEngine.XR.OpenXR.Features.MetaQuestSupport;

namespace ObjectSpawning.EditorTools
{
    // One-time project configuration for Stage 0 (OpenXR loader + controller profile).
    // Run via Tools > Object Spawning > Stage 0 > Configure OpenXR, or -executeMethod for CI/batchmode.
    static class XRProjectSetup
    {
        const string OpenXRLoaderTypeName = "UnityEngine.XR.OpenXR.OpenXRLoader";
        const string GeneralSettingsFolder = "Assets/XR";
        const string GeneralSettingsAssetPath = GeneralSettingsFolder + "/XRGeneralSettingsPerBuildTarget.asset";

        [MenuItem("Tools/Object Spawning/Stage 0/Configure OpenXR")]
        public static void ConfigureOpenXR()
        {
            var generalSettings = GetOrCreateGeneralSettings();

            EnableLoaderForTarget(generalSettings, BuildTargetGroup.Standalone);
            EnableLoaderForTarget(generalSettings, BuildTargetGroup.Android);

            EnableFeature<OculusTouchControllerProfile>(BuildTargetGroup.Standalone);
            EnableFeature<OculusTouchControllerProfile>(BuildTargetGroup.Android);
            EnableFeature<MetaQuestTouchPlusControllerProfile>(BuildTargetGroup.Standalone);
            EnableFeature<MetaQuestTouchPlusControllerProfile>(BuildTargetGroup.Android);
            EnableFeature<MetaQuestFeature>(BuildTargetGroup.Android);

            AssetDatabase.SaveAssets();
            Debug.Log("[XRProjectSetup] OpenXR loader + Oculus Touch / Touch Plus profiles enabled for Standalone and Android; Meta Quest support enabled for Android.");
        }

        static XRGeneralSettingsPerBuildTarget GetOrCreateGeneralSettings()
        {
            EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.settingsKey, out XRGeneralSettingsPerBuildTarget generalSettings);
            if (generalSettings != null)
                return generalSettings;

            var existing = AssetDatabase.FindAssets("t:XRGeneralSettingsPerBuildTarget");
            if (existing.Length > 0)
            {
                var path = AssetDatabase.GUIDToAssetPath(existing[0]);
                generalSettings = AssetDatabase.LoadAssetAtPath<XRGeneralSettingsPerBuildTarget>(path);
                if (generalSettings != null)
                {
                    EditorBuildSettings.AddConfigObject(XRGeneralSettings.settingsKey, generalSettings, true);
                    return generalSettings;
                }
            }

            if (!AssetDatabase.IsValidFolder(GeneralSettingsFolder))
                AssetDatabase.CreateFolder("Assets", "XR");

            generalSettings = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
            AssetDatabase.CreateAsset(generalSettings, GeneralSettingsAssetPath);
            AssetDatabase.SaveAssets();
            EditorBuildSettings.AddConfigObject(XRGeneralSettings.settingsKey, generalSettings, true);
            return generalSettings;
        }

        static void EnableLoaderForTarget(XRGeneralSettingsPerBuildTarget generalSettings, BuildTargetGroup group)
        {
            if (!generalSettings.HasSettingsForBuildTarget(group))
                generalSettings.CreateDefaultSettingsForBuildTarget(group);

            if (!generalSettings.HasManagerSettingsForBuildTarget(group))
                generalSettings.CreateDefaultManagerSettingsForBuildTarget(group);

            var manager = generalSettings.ManagerSettingsForBuildTarget(group);

            // ScriptableObject.CreateInstance leaves these false; the Project Settings UI defaults them to true.
            // Without this, XR never actually initializes when entering Play Mode (black screen, no tracking).
            manager.automaticLoading = true;
            manager.automaticRunning = true;
            EditorUtility.SetDirty(manager);

            foreach (var loader in manager.activeLoaders)
            {
                if (loader != null && loader.GetType().FullName == OpenXRLoaderTypeName)
                    return; // already assigned
            }

            XRPackageMetadataStore.AssignLoader(manager, OpenXRLoaderTypeName, group);
        }

        static void EnableFeature<TFeature>(BuildTargetGroup group) where TFeature : OpenXRFeature
        {
            var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(group);
            if (settings == null)
            {
                Debug.LogWarning($"[XRProjectSetup] No OpenXR settings found for {group}; loader may not be assigned yet.");
                return;
            }

            var feature = settings.GetFeature<TFeature>();
            if (feature == null)
            {
                Debug.LogWarning($"[XRProjectSetup] Feature {typeof(TFeature).Name} not found for {group}.");
                return;
            }

            feature.enabled = true;
            EditorUtility.SetDirty(settings);
        }
    }
}

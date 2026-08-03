using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.UI;
using UnityEngine;

namespace ObjectSpawning.EditorTools
{
    // Imports the XR Interaction Toolkit samples needed for Stage 0 (rig prefabs + editor-only input simulator).
    static class XRISampleImporter
    {
        const string PackageName = "com.unity.xr.interaction.toolkit";

        static readonly string[] SamplesToImport =
        {
            "Starter Assets",
            "XR Interaction Simulator",
        };

        [MenuItem("Tools/Object Spawning/Stage 0/Import XRI Samples")]
        public static void ImportSamples()
        {
            var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/" + PackageName);
            if (packageInfo == null)
            {
                Debug.LogError($"[XRISampleImporter] Package {PackageName} not found.");
                return;
            }

            var samples = Sample.FindByPackage(PackageName, packageInfo.version).ToList();

            foreach (var name in SamplesToImport)
            {
                var sample = samples.FirstOrDefault(s => s.displayName == name);
                if (sample.displayName == null)
                {
                    Debug.LogWarning($"[XRISampleImporter] Sample '{name}' not found for {PackageName}@{packageInfo.version}.");
                    continue;
                }

                if (sample.isImported)
                {
                    Debug.Log($"[XRISampleImporter] Sample '{name}' already imported at {sample.importPath}.");
                    continue;
                }

                bool ok = sample.Import(Sample.ImportOptions.OverridePreviousImports);
                Debug.Log($"[XRISampleImporter] Imported sample '{name}': {ok} -> {sample.importPath}");
            }

            AssetDatabase.Refresh();
        }
    }
}

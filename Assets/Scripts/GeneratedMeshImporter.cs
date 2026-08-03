using System;
using System.Collections;
using System.Threading.Tasks;
using GLTFast;
using UnityEngine;
using UnityEngine.Networking;

namespace ObjectSpawning
{
    // Stage 6: downloads a generated GLB and imports it at runtime via glTFast, then normalizes
    // it (rescale to a sane on-screen size, recenter pivot to its base) since generated meshes
    // arrive in arbitrary units and pivot conventions -- Tripo3D makes no promises about either.
    public static class GeneratedMeshImporter
    {
        const float TargetMaxDimension = 0.4f; // roughly matches the size of a spawned crate/lamp
        const int PolycountWarningThreshold = 150000;

        // result: (importedRoot, null) on success -- already parented under `parent`, scaled, and
        // recentered so its base sits at parent's position. (null, error) on any failure, which
        // callers should treat as "leave the placeholder as-is", never a crash.
        public static IEnumerator DownloadAndImport(string glbUrl, Transform parent, Action<GameObject, string> onComplete)
        {
            using var request = UnityWebRequest.Get(glbUrl);
            request.timeout = 60;
            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                onComplete?.Invoke(null, $"Failed to download generated model: {request.error}");
                yield break;
            }

            var bytes = request.downloadHandler.data;

            var importTask = ImportAsync(bytes, parent);
            while (!importTask.IsCompleted)
                yield return null;

            if (importTask.IsFaulted)
            {
                var message = importTask.Exception?.GetBaseException().Message ?? "glTF import failed.";
                onComplete?.Invoke(null, $"Failed to import generated model: {message}");
                yield break;
            }

            var root = importTask.Result;
            if (root == null)
            {
                onComplete?.Invoke(null, "Failed to import generated model: glTFast reported failure.");
                yield break;
            }

            NormalizeTransform(root);
            WarnIfHighPolycount(root);

            onComplete?.Invoke(root, null);
        }

        static async Task<GameObject> ImportAsync(byte[] glbBytes, Transform parent)
        {
            var gltf = new GltfImport();
            var success = await gltf.LoadGltfBinary(glbBytes, new Uri("https://generated.invalid/model.glb"));
            if (!success)
            {
                gltf.Dispose();
                return null;
            }

            var root = new GameObject("GeneratedMesh");
            root.transform.SetParent(parent, false);

            success = await gltf.InstantiateMainSceneAsync(root.transform);
            gltf.Dispose();

            if (!success)
            {
                UnityEngine.Object.Destroy(root);
                return null;
            }

            return root;
        }

        static void NormalizeTransform(GameObject root)
        {
            var renderers = root.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
                return;

            var bounds = ComputeBounds(renderers);
            var maxDimension = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
            if (maxDimension > 0.0001f)
            {
                root.transform.localScale *= TargetMaxDimension / maxDimension;
                bounds = ComputeBounds(renderers); // bounds shifted under the new scale
            }

            // Recenter so the mesh's own base sits at the parent's origin -- matches every other
            // spawned object's pivot convention (composites are floor-pivoted too).
            var target = root.transform.parent.position;
            var offset = target - new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
            root.transform.position += offset;
        }

        static Bounds ComputeBounds(Renderer[] renderers)
        {
            var bounds = renderers[0].bounds;
            for (var i = 1; i < renderers.Length; i++)
                bounds.Encapsulate(renderers[i].bounds);
            return bounds;
        }

        static void WarnIfHighPolycount(GameObject root)
        {
            var triangleCount = 0;
            foreach (var meshFilter in root.GetComponentsInChildren<MeshFilter>())
            {
                if (meshFilter.sharedMesh != null)
                    triangleCount += meshFilter.sharedMesh.triangles.Length / 3;
            }

            if (triangleCount > PolycountWarningThreshold)
            {
                Debug.LogWarning($"[GeneratedMeshImporter] Generated mesh has {triangleCount} triangles " +
                    $"(over the {PolycountWarningThreshold} soft cap) -- may impact VR frame rate.");
            }
        }
    }
}

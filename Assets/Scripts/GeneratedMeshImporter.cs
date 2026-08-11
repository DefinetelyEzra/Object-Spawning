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
            // These GLBs run 40-50MB (mostly PBR texture data) and are pulled straight from
            // Tripo3D's CDN over whatever Wi-Fi the headset has -- 60s was tight enough that a
            // real, already-succeeded generation could still fail here on ordinary network
            // variance. Same reasoning as the generous generation-poll timeout: the placeholder
            // costs nothing to leave waiting, so give the download plenty of room too.
            request.timeout = 300;
            var op = request.SendWebRequest();

            // Logged periodically rather than every frame -- this whole download phase used to
            // be a silent multi-minute gap in the logs with nothing to show whether it was
            // progressing or stalled, which is exactly what made a slow-but-healthy download
            // indistinguishable from a hung one while debugging.
            var nextProgressLogTime = Time.realtimeSinceStartup + 5f;
            while (!op.isDone)
            {
                if (Time.realtimeSinceStartup >= nextProgressLogTime)
                {
                    Debug.Log($"[GeneratedMeshImporter] Download progress: {request.downloadProgress:P0} " +
                        $"({request.downloadedBytes} bytes)");
                    nextProgressLogTime = Time.realtimeSinceStartup + 5f;
                }
                yield return null;
            }

            if (request.result != UnityWebRequest.Result.Success)
            {
                onComplete?.Invoke(null, $"Failed to download generated model: {request.error} " +
                    $"({request.downloadedBytes} bytes received)");
                yield break;
            }

            var bytes = request.downloadHandler.data;
            Debug.Log($"[GeneratedMeshImporter] Downloaded {bytes?.Length ?? 0} bytes from {glbUrl}");

            var importTask = ImportAsync(bytes, parent);
            while (!importTask.IsCompleted)
                yield return null;

            if (importTask.IsFaulted)
            {
                var message = importTask.Exception?.GetBaseException().Message ?? "glTF import failed.";
                onComplete?.Invoke(null, $"Failed to import generated model: {message}");
                yield break;
            }

            var (root, gltf) = importTask.Result;
            if (root == null)
            {
                onComplete?.Invoke(null, "Failed to import generated model: glTFast reported failure.");
                yield break;
            }

            var rendererCount = root.GetComponentsInChildren<Renderer>().Length;
            Debug.Log($"[GeneratedMeshImporter] Imported with {rendererCount} renderer(s).");

            NormalizeTransform(root);
            WarnIfHighPolycount(root);

            // Disposed only now, after everything above has finished reading mesh/material data
            // off the instantiated hierarchy -- disposing right after InstantiateMainSceneAsync
            // returned (the previous approach) is suspected of tearing down data a bit too early
            // for this large a file (42MB+, mostly PBR texture data).
            gltf.Dispose();

            onComplete?.Invoke(root, null);
        }

        static async Task<(GameObject, GltfImport)> ImportAsync(byte[] glbBytes, Transform parent)
        {
            // No logger was ever passed to GltfImport before this -- every internal
            // m_Logger?.Error/Warning call (image decode failures, unsupported extensions,
            // JSON parse issues) was silently swallowed by the null-conditional. ConsoleLogger
            // routes those through Debug.Log so real failures stop looking like silent success.
            var gltf = new GltfImport(logger: new GLTFast.Logging.ConsoleLogger());
            GameObject root = null;
            try
            {
                var loadSuccess = await gltf.LoadGltfBinary(glbBytes, new Uri("https://generated.invalid/model.glb"));
                if (!loadSuccess)
                {
                    Debug.LogWarning("[GeneratedMeshImporter] LoadGltfBinary returned false.");
                    gltf.Dispose();
                    return (null, null);
                }

                Debug.Log($"[GeneratedMeshImporter] Loaded: materials={gltf.MaterialCount} " +
                    $"images={gltf.ImageCount} textures={gltf.TextureCount} scenes={gltf.SceneCount}");

                root = new GameObject("GeneratedMesh");
                root.transform.SetParent(parent, false);

                var instantiateSuccess = await gltf.InstantiateMainSceneAsync(root.transform);
                if (!instantiateSuccess)
                {
                    Debug.LogWarning("[GeneratedMeshImporter] InstantiateMainSceneAsync returned false.");
                    gltf.Dispose();
                    UnityEngine.Object.Destroy(root);
                    return (null, null);
                }

                return (root, gltf);
            }
            catch (Exception e)
            {
                // Full exception (type + stack trace), not just the message -- a bare message
                // like "Object reference not set..." gives no way to tell which glTFast step or
                // internal call actually failed.
                Debug.LogError($"[GeneratedMeshImporter] Import threw: {e}");
                gltf.Dispose();
                if (root != null)
                    UnityEngine.Object.Destroy(root);
                return (null, null);
            }
        }

        static void NormalizeTransform(GameObject root)
        {
            var renderers = root.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
                return;

            foreach (var r in renderers)
            {
                var mat = r.sharedMaterial;

                // glTFast's own single-mesh import path (GameObjectInstantiator.AddPrimitive)
                // never sets this -- it only does for the unrelated EXT_mesh_gpu_instancing
                // codepath (many copies of one mesh). Without it, Single Pass Instanced XR
                // rendering silently drops the draw call for both eyes: the object still exists
                // in the scene with a correct transform/bounds/collider, it just never actually
                // gets painted, which is indistinguishable from "not there" in the headset.
                if (mat != null)
                    mat.enableInstancing = true;

                // Diagnostic: source glTF materials from Tripo3D come back "doubleSided": false,
                // i.e. back-face culled. If glTFast's right-handed-Y-up -> left-handed-Y-up
                // conversion has any winding-order mismatch with how Tripo3D exports triangles,
                // every face would get culled from every possible viewing angle -- invisible from
                // any direction, zero GPU cost, unrelated to material/texture/stereo settings,
                // which fits every symptom seen so far better than anything else tried. Forcing
                // Cull Off tests that directly and, if it works, is a fine permanent fix for these
                // small single-object props (no meaningful perf cost, no adjacent geometry to
                // benefit from back-face culling anyway).
                if (mat != null && mat.HasProperty("_Cull"))
                {
                    mat.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
                    Debug.Log($"[GeneratedMeshImporter] Forced _Cull=Off on '{r.name}'.");
                }

                // Defensive cleanup pass -- none of these have been confirmed as the actual cause,
                // but each is a cheap, safe no-op if it isn't the problem, and covers ground the
                // targeted tests so far couldn't reach in one go:
                //  - isStatic can make a runtime-spawned object interact with batching/occlusion
                //    systems that never knew it existed at bake time (this project has no baked
                //    occlusion data, so low-odds, but free to rule out).
                //  - forceRenderingOff is a direct "never draw this" switch; nothing in our code
                //    sets it, but neither does anything guarantee glTFast doesn't.
                //  - an LODGroup culling the object at this screen size would look exactly like
                //    this; glTFast isn't expected to add one, but cheap to strip if present.
                r.gameObject.isStatic = false;
                r.forceRenderingOff = false;
                if (r.gameObject.TryGetComponent<LODGroup>(out var lodGroup))
                {
                    Debug.LogWarning($"[GeneratedMeshImporter] Removing unexpected LODGroup on '{r.name}'.");
                    UnityEngine.Object.Destroy(lodGroup);
                }

                Debug.Log($"[GeneratedMeshImporter] Renderer '{r.name}' enabled={r.enabled} " +
                    $"activeInHierarchy={r.gameObject.activeInHierarchy} " +
                    $"shader={(mat != null ? mat.shader?.name : "null")} " +
                    $"instancing={(mat != null ? mat.enableInstancing : false)} bounds={r.bounds}");

                // The "Loaded: ... images=0" line logged earlier has shown 0 on every device
                // test so far despite the source GLB genuinely declaring 3 images -- checking
                // whether the material's actual bound texture slots are null narrows down
                // whether that's a real failed-texture-load bug (which could plausibly zero out
                // an alpha channel and render the whole surface fully transparent) or just a
                // count read at the wrong point in glTFast's load sequence.
                if (mat != null)
                {
                    foreach (var propName in mat.GetTexturePropertyNames())
                    {
                        var tex = mat.GetTexture(propName);
                        Debug.Log($"[GeneratedMeshImporter]   texture prop '{propName}' = " +
                            $"{(tex != null ? $"{tex.width}x{tex.height}" : "NULL")}");
                    }
                    Debug.Log($"[GeneratedMeshImporter]   color={mat.color} " +
                        $"renderQueue={mat.renderQueue} alphaClip={(mat.HasProperty("_AlphaClip") ? mat.GetFloat("_AlphaClip").ToString() : "n/a")}");
                }

                // Every material/shader/culling/stereo-rendering angle on glTFast's own Shader
                // Graph material has been tried and none of them made the object visible, despite
                // the material, textures and mesh data all independently checking out as valid --
                // pointing at the compiled shader program itself failing on this device/driver in
                // a way no material property can work around. Rebuilding the visible material from
                // Universal Render Pipeline/Lit -- the exact shader every other spawned object in
                // this project already renders correctly with -- sidesteps glTFast's shader
                // entirely rather than trying to fix whatever is wrong with it.
                if (mat != null)
                {
                    var urpLitShader = Shader.Find("Universal Render Pipeline/Lit");
                    if (urpLitShader != null)
                    {
                        var replacement = new Material(urpLitShader) { enableInstancing = true };
                        var baseColorTex = mat.GetTexture("baseColorTexture");
                        if (baseColorTex != null && replacement.HasProperty("_BaseMap"))
                            replacement.SetTexture("_BaseMap", baseColorTex);
                        r.sharedMaterial = replacement;
                        Debug.Log($"[GeneratedMeshImporter] Replaced material on '{r.name}' with URP/Lit " +
                            $"(baseColorTex={(baseColorTex != null ? "applied" : "none")}).");
                    }
                }

                // renderer.bounds may just reflect the glTF accessor's declared min/max metadata
                // rather than the real uploaded vertex data -- everything checked so far (bounds,
                // triangle count, collider) is consistent with that metadata being fine even if
                // the actual vertex buffer is degenerate or NaN. Reading the raw array directly
                // is the one thing that can't lie about that.
                var mesh = r is SkinnedMeshRenderer smr ? smr.sharedMesh
                    : r.GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh != null)
                {
                    var verts = mesh.vertices;
                    var nanOrInfCount = 0;
                    var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                    var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                    foreach (var v in verts)
                    {
                        if (float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) ||
                            float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z))
                        {
                            nanOrInfCount++;
                            continue;
                        }
                        min = Vector3.Min(min, v);
                        max = Vector3.Max(max, v);
                    }
                    Debug.Log($"[GeneratedMeshImporter] Raw mesh '{mesh.name}': type={r.GetType().Name} " +
                        $"vertexCount={verts.Length} nanOrInf={nanOrInfCount} " +
                        $"rawLocalMin={min} rawLocalMax={max} " +
                        $"sample[0]={(verts.Length > 0 ? verts[0].ToString() : "n/a")} " +
                        $"sample[mid]={(verts.Length > 0 ? verts[verts.Length / 2].ToString() : "n/a")}");

                    // >65535 vertices requires 32-bit indices -- if glTFast didn't set this
                    // correctly for a mesh this large, that alone could silently fail to render.
                    Debug.Log($"[GeneratedMeshImporter] Mesh format: indexFormat={mesh.indexFormat} " +
                        $"subMeshCount={mesh.subMeshCount} " +
                        $"topology={(mesh.subMeshCount > 0 ? mesh.GetTopology(0).ToString() : "n/a")} " +
                        $"indexCount={(mesh.subMeshCount > 0 ? mesh.GetIndexCount(0).ToString() : "n/a")} " +
                        $"meshBounds(center={mesh.bounds.center}, size={mesh.bounds.size}) " +
                        $"isReadable={mesh.isReadable} normalsCount={mesh.normals.Length} " +
                        $"tangentsCount={mesh.tangents.Length} uvCount={mesh.uv.Length}");

                    // glTFast builds its meshes through Unity's low-level SetVertexBufferParams /
                    // SetIndexBufferParams API, which has strict, easy-to-get-subtly-wrong stream
                    // layout requirements. Everything read back from the managed side (vertices,
                    // bounds, normals, uv) has checked out as valid on every test so far, but that
                    // reads Unity's CPU-side cache -- it can't rule out the actual GPU-uploaded
                    // buffer being malformed in a way that's invisible to script. Rebuilding via
                    // the classic high-level Mesh API (the same simple path every basic Unity mesh
                    // has always used) sidesteps that possibility entirely rather than trying to
                    // prove or disprove it.
                    var freshMesh = new Mesh
                    {
                        name = mesh.name + "_rebuilt",
                        indexFormat = verts.Length > 65535
                            ? UnityEngine.Rendering.IndexFormat.UInt32
                            : UnityEngine.Rendering.IndexFormat.UInt16,
                    };
                    freshMesh.vertices = verts;
                    freshMesh.normals = mesh.normals.Length == verts.Length ? mesh.normals : null;
                    freshMesh.uv = mesh.uv.Length == verts.Length ? mesh.uv : null;
                    freshMesh.triangles = mesh.triangles;
                    if (freshMesh.normals == null)
                        freshMesh.RecalculateNormals();
                    freshMesh.RecalculateBounds();

                    if (r is SkinnedMeshRenderer skinned)
                        skinned.sharedMesh = freshMesh;
                    else if (r.TryGetComponent<MeshFilter>(out var meshFilterComp))
                        meshFilterComp.sharedMesh = freshMesh;

                    Debug.Log($"[GeneratedMeshImporter] Rebuilt mesh via classic API: " +
                        $"vertexCount={freshMesh.vertexCount} triCount={freshMesh.triangles.Length / 3} " +
                        $"bounds(center={freshMesh.bounds.center}, size={freshMesh.bounds.size})");
                }
                else
                {
                    Debug.LogWarning($"[GeneratedMeshImporter] No MeshFilter/SkinnedMeshRenderer sharedMesh found on '{r.name}'.");
                }
            }

            var bounds = ComputeBounds(renderers);
            Debug.Log($"[GeneratedMeshImporter] Raw bounds: center={bounds.center} size={bounds.size}");

            var maxDimension = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
            if (maxDimension > 0.0001f)
            {
                root.transform.localScale *= TargetMaxDimension / maxDimension;
                bounds = ComputeBounds(renderers); // bounds shifted under the new scale
                Debug.Log($"[GeneratedMeshImporter] Scaled by {TargetMaxDimension / maxDimension:0.######} " +
                    $"-> localScale={root.transform.localScale}, new bounds: center={bounds.center} size={bounds.size}");
            }
            else
            {
                Debug.LogWarning($"[GeneratedMeshImporter] maxDimension={maxDimension} too small to rescale -- " +
                    "mesh may be degenerate.");
            }

            // Recenter so the mesh's own base sits at the parent's origin -- matches every other
            // spawned object's pivot convention (composites are floor-pivoted too).
            var target = root.transform.parent.position;
            var offset = target - new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
            root.transform.position += offset;
            Debug.Log($"[GeneratedMeshImporter] Recentered: parentPos={target}, offset={offset}, " +
                $"final root.position={root.transform.position}, lossyScale={root.transform.lossyScale}");
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

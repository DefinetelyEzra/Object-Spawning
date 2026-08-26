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
        const float TargetMaxDimension = 0.4f; // roughly matches the size of a spawned crate
        const int PolycountWarningThreshold = 150000;

        // result: (importedRoot, null) on success -- already parented under `parent`, scaled, and
        // recentered so its base sits at parent's position. (null, error) on any failure, which
        // callers should treat as "leave the placeholder as-is", never a crash.
        //
        // Stage 10: onRawBytesDownloaded (optional) fires with the downloaded GLB bytes right
        // after a successful download, independent of whether import itself then succeeds -- lets
        // callers cache the bytes for reuse (see MeshCache) without this class needing to know
        // anything about caching itself.
        public static IEnumerator DownloadAndImport(string glbUrl, Transform parent, Action<GameObject, string> onComplete,
            Action<byte[]> onRawBytesDownloaded = null)
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
            onRawBytesDownloaded?.Invoke(bytes);

            yield return ImportBytes(bytes, parent, onComplete);
        }

        // Stage 10: the shared second half of DownloadAndImport, split out so a cache hit (bytes
        // already on disk, see MeshCache) can import without a network round trip at all.
        public static IEnumerator ImportBytes(byte[] bytes, Transform parent, Action<GameObject, string> onComplete)
        {
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

                // glTFast's own single-mesh import path never enables GPU instancing on its
                // materials, which Single Pass Instanced XR rendering depends on to draw both
                // eyes in one pass -- without it the object exists in the scene with a correct
                // transform/bounds/collider but never actually gets painted.
                if (mat != null)
                    mat.enableInstancing = true;

                // Source materials come back single-sided; force double-sided rendering so a
                // winding-order mismatch between glTF's and Unity's coordinate handedness can't
                // back-face-cull an entire generated mesh from every angle.
                if (mat != null && mat.HasProperty("_Cull"))
                    mat.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);

                // Defensive: a runtime-spawned object should never end up static (no baked
                // occlusion data exists to interact with anyway), force-hidden, or LOD-culled --
                // none of these are things our own code sets, but nothing guarantees glTFast doesn't.
                r.gameObject.isStatic = false;
                r.forceRenderingOff = false;
                if (r.gameObject.TryGetComponent<LODGroup>(out var lodGroup))
                    UnityEngine.Object.Destroy(lodGroup);

                // This project's URP asset has Light Layers enabled (m_SupportsLightLayers: 1),
                // and the scene's directional light is restricted to renderingLayerMask=1. A
                // MeshRenderer added at runtime via AddComponent (glTFast's own instantiation
                // path, not the normal Editor object-creation flow) isn't guaranteed to inherit
                // the expected default rendering layer -- if it doesn't match the light's mask,
                // the light simply never illuminates this renderer at all: fully visible mesh,
                // correctly assigned material and texture, zero lighting reaching it, i.e.
                // exactly "black". Force it to every layer so it can never mismatch any light.
                r.renderingLayerMask = uint.MaxValue;

                // glTFast's own Shader Graph material reliably failed to render on-device despite
                // the mesh/material/texture data all checking out as valid. Rebuild the visible
                // material from Universal Render Pipeline/Lit instead -- the same shader every
                // other spawned object in this project already renders correctly with.
                if (mat != null)
                {
                    var urpLitShader = Shader.Find("Universal Render Pipeline/Lit");
                    if (urpLitShader != null)
                    {
                        var replacement = new Material(urpLitShader) { enableInstancing = true };

                        // glTFast's decoded textures come back as RGB24 -- a 3-channel,
                        // byte-unaligned format known to sample inconsistently in shaders on some
                        // mobile GPU drivers (confirmed here: Graphics.Blit could read real,
                        // correct pixel data from it, but the actual rendered object was solid
                        // black -- Blit performs a full GPU render pass that implicitly
                        // reformats, direct SAMPLE_TEXTURE2D in a shader doesn't get that same
                        // conversion). Converting through the same proven Blit path into a real
                        // RGBA32 texture before handing it to the material sidesteps that.
                        var baseColorTex = ConvertToRGBA32(mat.GetTexture("baseColorTexture") as Texture2D);
                        if (baseColorTex != null && replacement.HasProperty("_BaseMap"))
                            replacement.SetTexture("_BaseMap", baseColorTex);

                        r.sharedMaterial = replacement;
                    }
                }

                // Rebuilt via Unity's classic high-level Mesh API rather than trusting glTFast's
                // low-level buffer-based construction, which has strict stream layout
                // requirements that are easy to get subtly wrong.
                var mesh = r is SkinnedMeshRenderer smr ? smr.sharedMesh
                    : r.GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh != null)
                {
                    var verts = mesh.vertices;
                    var freshMesh = new Mesh
                    {
                        name = mesh.name + "_rebuilt",
                        indexFormat = verts.Length > 65535
                            ? UnityEngine.Rendering.IndexFormat.UInt32
                            : UnityEngine.Rendering.IndexFormat.UInt16,
                    };
                    freshMesh.vertices = verts;
                    freshMesh.uv = mesh.uv.Length == verts.Length ? mesh.uv : null;
                    freshMesh.triangles = mesh.triangles;
                    freshMesh.RecalculateBounds();

                    // Recalculated from the mesh's actual triangle winding rather than trusting
                    // glTFast's imported normal vectors -- but RecalculateNormals() only derives
                    // normals CONSISTENT with whatever winding is already stored; it can't tell
                    // "correct" from "reversed" on its own. Every generated mesh so far has
                    // needed _Cull=Off (see above) precisely because winding, independent of
                    // normal data, comes out reversed by the glTF (right-handed) -> Unity
                    // (left-handed) conversion -- so a normals-only fix would likely just
                    // recompute normals that are STILL inverted, still rendering fully dark.
                    // FixInwardNormals below verifies which way the recalculated normals
                    // actually ended up pointing (against each sampled vertex's direction from
                    // the mesh's own center) and, only if they're predominantly inward, reverses
                    // the winding and recomputes -- self-correcting rather than assuming the
                    // reversal direction, so this can't make an already-correct mesh worse.
                    freshMesh.RecalculateNormals();
                    FixInwardNormals(freshMesh);
                    freshMesh.RecalculateTangents();

                    if (r is SkinnedMeshRenderer skinned)
                        skinned.sharedMesh = freshMesh;
                    else if (r.TryGetComponent<MeshFilter>(out var meshFilterComp))
                        meshFilterComp.sharedMesh = freshMesh;
                }
            }

            Debug.Log($"[GeneratedMeshImporter] Normalized {renderers.Length} renderer(s).");

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

        // glTFast's decoded textures come back as RGB24, which samples inconsistently in
        // shaders on some mobile GPU drivers. Blitting to a RenderTexture and reading back from
        // that (rather than assigning the source texture directly) forces a real GPU-side format
        // conversion to RGBA32 along the way -- the same mechanism used to confirm the source
        // texture actually had valid color data in the first place. Works regardless of the
        // source's isReadable flag, since Blit doesn't need CPU-side pixel access.
        static Texture2D ConvertToRGBA32(Texture2D source)
        {
            if (source == null)
                return null;

            var rt = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32);
            var previous = RenderTexture.active;
            try
            {
                Graphics.Blit(source, rt);
                RenderTexture.active = rt;
                var converted = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
                converted.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
                converted.Apply();
                return converted;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[GeneratedMeshImporter] RGBA32 conversion failed, falling back to source texture: {e.Message}");
                return source;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        // Votes each sampled vertex's normal against its direction from the mesh's own center --
        // a normal roughly agreeing with "away from center" is outward (correct), disagreeing is
        // inward (reversed winding). Only flips when a clear majority disagree, so an oddly-
        // shaped but genuinely correct mesh (concave regions, thin parts) can't get flipped by a
        // few legitimately inward-facing vertices.
        static void FixInwardNormals(Mesh mesh)
        {
            var verts = mesh.vertices;
            var normals = mesh.normals;
            if (verts.Length == 0 || normals.Length != verts.Length)
                return;

            var center = mesh.bounds.center;
            var sampleStep = Mathf.Max(1, verts.Length / 200); // sample up to ~200 vertices
            var sampled = 0;
            var inwardVotes = 0;
            for (var i = 0; i < verts.Length; i += sampleStep)
            {
                sampled++;
                if (Vector3.Dot(verts[i] - center, normals[i]) < 0f)
                    inwardVotes++;
            }

            if (sampled > 0 && inwardVotes > sampled / 2)
            {
                var tris = mesh.triangles;
                for (var i = 0; i < tris.Length; i += 3)
                    (tris[i + 1], tris[i + 2]) = (tris[i + 2], tris[i + 1]);
                mesh.triangles = tris;
                mesh.RecalculateNormals();
            }
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

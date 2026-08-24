using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace ObjectSpawning.EditorTools
{
    // Stage 9.1: generates the dedicated transparent base material PrimitiveSpawner.Retexture
    // instantiates from whenever a preset's IsTransparent is true (currently just "glass"),
    // instead of the normal opaque RuntimePrimitive.mat. Built as a real persisted asset --
    // never purely at runtime -- specifically so the build-time shader stripper can see and keep
    // the Transparent-surface shader variant it needs; an ad-hoc in-memory-only material is
    // invisible to that analysis, the exact same reasoning RuntimePrimitive.mat itself exists
    // for (see PrimitiveSpawner's baseMaterial field comment).
    //
    // GetOrCreateGlassMaterial is the shared entry point -- both Stage0SceneBuilder and
    // Stage1PlaygroundSceneBuilder call it the same way they each already call their own local
    // GetOrCreateBaseMaterial for RuntimePrimitive.mat, so the (meaningfully more involved)
    // transparent setup below lives in exactly one place instead of being copy-pasted across
    // both builders. The menu item is a thin manual-refresh wrapper over the same method.
    static class GlassMaterialSetup
    {
        const string AssetPath = "Assets/Materials/GlassMaterial.mat";

        [MenuItem("Object Spawning/Create Glass Material")]
        public static void CreateGlassMaterial() => GetOrCreateGlassMaterial();

        public static Material GetOrCreateGlassMaterial()
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                Debug.LogError("[GlassMaterialSetup] Could not find Universal Render Pipeline/Lit shader.");
                return null;
            }

            var existing = AssetDatabase.LoadAssetAtPath<Material>(AssetPath);
            var material = existing != null ? existing : new Material(shader);
            material.shader = shader;
            material.name = "GlassMaterial";

            // URP's own "Alpha" transparent blend preset (SrcAlpha/OneMinusSrcAlpha, no depth
            // write so transparent geometry doesn't occlude what's behind it, no alpha clip).
            // Cull Off (rather than RuntimePrimitive's default back-face culling) so looking
            // through a glass object also shows its far side, tinted -- the usual way to fake a
            // "you can see into it" look on a single-layer mesh.
            material.SetFloat("_Surface", 1f); // Transparent
            material.SetFloat("_Blend", 0f);   // Alpha
            material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            material.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
            material.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
            material.SetFloat("_ZWrite", 0f);
            material.SetFloat("_AlphaClip", 0f);
            material.SetFloat("_AlphaToMask", 0f);
            material.SetFloat("_Cull", (float)CullMode.Off);
            material.SetOverrideTag("RenderType", "Transparent");
            material.renderQueue = (int)RenderQueue.Transparent;

            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.DisableKeyword("_ALPHATEST_ON");
            material.DisableKeyword("_ALPHAPREMULTIPLY_ON");

            // Sensible preview defaults -- PrimitiveSpawner.Retexture overrides metallic/
            // smoothness/color per preset at runtime regardless, same as it does for
            // RuntimePrimitive.mat.
            material.SetFloat("_Metallic", 0f);
            material.SetFloat("_Smoothness", 0.95f);
            material.SetColor("_BaseColor", new Color(0.85f, 0.95f, 1f, 0.28f));
            if (material.HasProperty("_Color"))
                material.SetColor("_Color", new Color(0.85f, 0.95f, 1f, 0.28f));

            if (existing == null)
                AssetDatabase.CreateAsset(material, AssetPath);
            else
                EditorUtility.SetDirty(material);

            AssetDatabase.SaveAssets();
            Debug.Log($"[GlassMaterialSetup] Glass material ready at {AssetPath}.");
            return material;
        }
    }
}

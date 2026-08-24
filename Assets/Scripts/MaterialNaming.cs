using System;
using System.Collections.Generic;
using UnityEngine;

namespace ObjectSpawning
{
    // Stage 7: a single curated PBR preset -- base color plus URP/Lit's own _Metallic and
    // _Smoothness sliders, no texture image involved. Mirrors ColorNaming's (name, value) table
    // pattern exactly; the roadmap's own "keep it simple" guidance for this stage explicitly
    // calls for a curated parameter-driven library before attempting generative texturing.
    public readonly struct MaterialPreset
    {
        public readonly string Name;
        public readonly Color BaseColor;
        public readonly float Metallic;
        public readonly float Smoothness;

        // Stage 9.1: true only for "glass" so far. URP's alpha-blend rendering path is gated
        // behind a shader keyword/render-queue combination that has to already be baked into a
        // real persisted material ASSET for the build-time shader stripper to keep that variant
        // (an ad-hoc runtime-only material is invisible to it -- the exact same reasoning
        // PrimitiveSpawner's own baseMaterial field exists for, see its comment) -- so a
        // transparent preset needs PrimitiveSpawner to instantiate from its dedicated
        // glassMaterial asset instead of the normal opaque baseMaterial.
        public readonly bool IsTransparent;

        public MaterialPreset(string name, Color baseColor, float metallic, float smoothness, bool isTransparent = false)
        {
            Name = name;
            BaseColor = baseColor;
            Metallic = metallic;
            Smoothness = smoothness;
            IsTransparent = isTransparent;
        }
    }

    // Shared material-name table used by both the keyword parser's last-resort fallback and the
    // LLM intent client, same relationship ColorNaming already has to color words.
    public static class MaterialNaming
    {
        static readonly MaterialPreset[] Materials =
        {
            new("wood", new Color(0.45f, 0.28f, 0.12f), metallic: 0f, smoothness: 0.35f),
            new("metal", new Color(0.7f, 0.7f, 0.72f), metallic: 1f, smoothness: 0.55f),
            new("rusted_metal", new Color(0.55f, 0.27f, 0.12f), metallic: 0.6f, smoothness: 0.15f),
            new("gold", new Color(1f, 0.84f, 0.2f), metallic: 1f, smoothness: 0.85f),
            new("chrome", new Color(0.9f, 0.9f, 0.92f), metallic: 1f, smoothness: 0.95f),
            new("stone", new Color(0.55f, 0.55f, 0.53f), metallic: 0f, smoothness: 0.2f),
            new("concrete", new Color(0.62f, 0.62f, 0.6f), metallic: 0f, smoothness: 0.15f),
            new("marble", new Color(0.92f, 0.9f, 0.88f), metallic: 0f, smoothness: 0.7f),
            new("brick", new Color(0.55f, 0.27f, 0.2f), metallic: 0f, smoothness: 0.1f),
            new("plastic", new Color(0.85f, 0.85f, 0.85f), metallic: 0f, smoothness: 0.6f),
            new("rubber", new Color(0.08f, 0.08f, 0.08f), metallic: 0f, smoothness: 0.05f),
            new("fabric", new Color(0.5f, 0.5f, 0.6f), metallic: 0f, smoothness: 0.1f),
            new("leather", new Color(0.25f, 0.15f, 0.08f), metallic: 0f, smoothness: 0.4f),
            // Pale, faintly cyan-tinted, and mostly see-through (low alpha) -- a dielectric
            // (non-metallic), near-mirror-smooth surface is what actually reads as glass, not a
            // solid color at all.
            new("glass", new Color(0.85f, 0.95f, 1f, 0.28f), metallic: 0f, smoothness: 0.95f, isTransparent: true),
        };

        public static IReadOnlyList<MaterialPreset> All => Materials;

        public static bool TryGetMaterial(string name, out MaterialPreset preset)
        {
            if (!string.IsNullOrEmpty(name))
            {
                foreach (var candidate in Materials)
                {
                    if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        preset = candidate;
                        return true;
                    }
                }
            }

            preset = default;
            return false;
        }
    }
}

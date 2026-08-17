using System.Collections.Generic;
using UnityEngine;

namespace ObjectSpawning
{
    // Stage 7 follow-up: MaterialNaming's curated presets are flat PBR parameters only (no
    // texture assets, by design -- see its own comment), so a material like wood or marble
    // rendered as a recognizably-colored but perfectly flat swatch, with none of the grain/
    // veining/speckle that actually reads as that material. Generating a small detail texture
    // procedurally at runtime closes that gap without breaking the no-external-assets
    // constraint. One texture is generated per material name and cached for reuse -- every
    // object retextured to, say, wood shares the same generated texture rather than each
    // paying its own generation cost and its own chunk of texture memory.
    public static class ProceduralTextureFactory
    {
        // 128 read as noticeably blurry/low-detail up close in VR -- 512 plus mipmaps (see
        // BuildTexture) gives real crispness without meaningfully denting a Quest's texture
        // memory budget (512x512 RGBA32 is 1MB per cached material, ~8MB total worst case
        // across every preset that uses a detail texture).
        const int Size = 512;

        static readonly Dictionary<string, Texture2D> cache = new();

        public static Texture2D GetOrCreate(MaterialPreset preset)
        {
            if (cache.TryGetValue(preset.Name, out var cached) && cached != null)
                return cached;

            var texture = Generate(preset);
            cache[preset.Name] = texture;
            return texture;
        }

        // null means "no detail pattern for this material" -- gold/chrome/metal/plastic/rubber
        // are genuinely supposed to look smooth and uniform in reality, so the flat base color
        // Retexture already applies is correct for them, not a gap that needs filling.
        static Texture2D Generate(MaterialPreset preset) => preset.Name switch
        {
            "wood" => GenerateStreaked(preset.BaseColor, seed: 0.11f),
            "marble" => GenerateVeined(preset.BaseColor, veinColor: new Color(0.55f, 0.55f, 0.55f), seed: 0.23f),
            "stone" => GenerateSpeckled(preset.BaseColor, contrast: 0.32f, radius: 8f, seed: 0.37f),
            "concrete" => GenerateSpeckled(preset.BaseColor, contrast: 0.18f, radius: 14f, seed: 0.41f),
            "brick" => GenerateSpeckled(preset.BaseColor, contrast: 0.32f, radius: 22f, seed: 0.53f),
            "rusted_metal" => GenerateSpeckled(preset.BaseColor, contrast: 0.38f, radius: 16f, seed: 0.59f),
            "fabric" => GenerateSpeckled(preset.BaseColor, contrast: 0.14f, radius: 30f, seed: 0.67f),
            "leather" => GenerateSpeckled(preset.BaseColor, contrast: 0.20f, radius: 20f, seed: 0.71f),
            _ => null,
        };

        // Every pattern below is built from this: (u, v) each walked around their own circle in
        // Perlin-noise space rather than sampled as a raw line. cos/sin of (coord * 2*pi) is
        // exactly periodic with period 1 in that coord no matter the radius, so the noise value
        // at u=0 and u=1 (and v=0/v=1) always matches exactly -- the texture tiles seamlessly at
        // its own edges, and consequently at every repeat boundary once SetTextureScale repeats
        // it. Different radii per axis (radiusU/radiusV) trace a smaller or larger loop through
        // noise space per axis, which is what makes the wood-grain pattern below vary slowly
        // along one axis and quickly along the other while staying seamless in both.
        static float TileableNoise(float u, float v, float radiusU, float radiusV, float seed)
        {
            var angleU = (u + seed) * Mathf.PI * 2f;
            var angleV = (v + seed) * Mathf.PI * 2f;
            var nx = Mathf.Cos(angleU) * radiusU;
            var ny = Mathf.Sin(angleU) * radiusU;
            var nz = Mathf.Cos(angleV) * radiusV;
            var nw = Mathf.Sin(angleV) * radiusV;
            return (Mathf.PerlinNoise(nx, nz) + Mathf.PerlinNoise(ny, nw)) * 0.5f;
        }

        static float TileableFbm(float u, float v, float baseRadius, float seed, int octaves)
        {
            var value = 0f;
            var amplitude = 0.5f;
            var radius = baseRadius;
            for (var i = 0; i < octaves; i++)
            {
                // Seed shifted per octave -- otherwise doubling the radius alone can revisit a
                // noise-space region that looks like a simple scaled repeat of octave 0 instead
                // of genuinely new detail.
                value += TileableNoise(u, v, radius, radius, seed + i * 0.173f) * amplitude;
                radius *= 2f;
                amplitude *= 0.5f;
            }
            return value;
        }

        // Small radiusU (slow variation along U) + larger radiusV (fast variation along V) reads
        // as grain running along the U axis regardless of a given mesh's actual UV orientation.
        // Blended with a second, much higher-frequency layer for fine surface grain on top of
        // the broad streaks -- a single low-frequency layer alone reads as a smooth gradient
        // blur rather than actual wood texture once magnified to fill a whole face.
        static Texture2D GenerateStreaked(Color baseColor, float seed)
        {
            var pixels = new Color32[Size * Size];
            for (var y = 0; y < Size; y++)
            {
                var v = y / (float)Size;
                for (var x = 0; x < Size; x++)
                {
                    var u = x / (float)Size;
                    var broad = TileableNoise(u, v, radiusU: 1.5f, radiusV: 6f, seed: seed);
                    var fineGrain = TileableNoise(u, v, radiusU: 10f, radiusV: 40f, seed: seed + 0.31f);
                    var n = broad * 0.75f + fineGrain * 0.25f;
                    pixels[y * Size + x] = Scale(baseColor, Mathf.Lerp(0.68f, 1.22f, n));
                }
            }
            return BuildTexture(pixels);
        }

        // Classic "sine of a turbulent field" marble technique: FBM noise displaces the phase of
        // a sine wave, producing organic streaks instead of the perfectly regular bands a plain
        // sine would give. The sine itself is evaluated over an integer number of periods across
        // u (12 full waves), which is what keeps IT seamless too -- sin(theta + 2*pi*integer)
        // always equals sin(theta).
        static Texture2D GenerateVeined(Color baseColor, Color veinColor, float seed)
        {
            const int periodsAcrossU = 12;
            var pixels = new Color32[Size * Size];
            var baseColor32 = ToColor32(baseColor);
            var veinColor32 = ToColor32(veinColor);
            for (var y = 0; y < Size; y++)
            {
                var v = y / (float)Size;
                for (var x = 0; x < Size; x++)
                {
                    var u = x / (float)Size;
                    var turbulence = TileableFbm(u, v, baseRadius: 2f, seed: seed, octaves: 5) * 6f;
                    var vein = Mathf.Abs(Mathf.Sin(u * Mathf.PI * 2f * periodsAcrossU + turbulence));
                    var blend = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.82f, 0.97f, vein));
                    pixels[y * Size + x] = Color32.Lerp(baseColor32, veinColor32, blend);
                }
            }
            return BuildTexture(pixels);
        }

        // Shared by stone/concrete/brick/rusted_metal/fabric/leather -- only the noise radius
        // (blotch size) and contrast (how far brightness swings) differ per material, tuned to
        // roughly match how coarse or fine each material's real texture reads.
        static Texture2D GenerateSpeckled(Color baseColor, float contrast, float radius, float seed)
        {
            var pixels = new Color32[Size * Size];
            for (var y = 0; y < Size; y++)
            {
                var v = y / (float)Size;
                for (var x = 0; x < Size; x++)
                {
                    var u = x / (float)Size;
                    var n = TileableFbm(u, v, radius, seed, octaves: 5);
                    pixels[y * Size + x] = Scale(baseColor, Mathf.Lerp(1f - contrast, 1f + contrast, n));
                }
            }
            return BuildTexture(pixels);
        }

        static Texture2D BuildTexture(Color32[] pixels)
        {
            var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, true)
            {
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 4,
            };
            tex.SetPixels32(pixels);
            tex.Apply(updateMipmaps: true);
            return tex;
        }

        static Color32 Scale(Color baseColor, float brightness) => ToColor32(
            new Color(baseColor.r * brightness, baseColor.g * brightness, baseColor.b * brightness, 1f));

        static Color32 ToColor32(Color c) => new(
            (byte)(Mathf.Clamp01(c.r) * 255f),
            (byte)(Mathf.Clamp01(c.g) * 255f),
            (byte)(Mathf.Clamp01(c.b) * 255f),
            255);
    }
}

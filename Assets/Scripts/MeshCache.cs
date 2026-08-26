using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace ObjectSpawning
{
    // Stage 10: a flat folder of downloaded generated meshes, keyed by a hash of their prompt --
    // "no database needed yet", per the roadmap's own "keep it simple" guidance for this stage.
    // Lets a repeated prompt (a fresh "spawn a stone gargoyle" that already exists, or the asset
    // library recalling something by name) skip Tripo3D's paid generation entirely and import
    // straight from disk.
    public static class MeshCache
    {
        static string CacheDir => Path.Combine(Application.persistentDataPath, "MeshCache");

        // Case/whitespace-insensitive so "spawn a stone gargoyle" and "Spawn A Stone Gargoyle"
        // hit the same cache entry -- the exact wording a guest happens to use shouldn't matter.
        static string NormalizePrompt(string prompt) => prompt.Trim().ToLowerInvariant();

        static string KeyFor(string prompt)
        {
            var bytes = Encoding.UTF8.GetBytes(NormalizePrompt(prompt));
            // Classic instance API rather than the newer MD5.HashData static helper -- guaranteed
            // available regardless of this project's exact .NET API compatibility level.
            using var md5 = MD5.Create();
            var hash = md5.ComputeHash(bytes);
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        static string PathFor(string prompt) => Path.Combine(CacheDir, KeyFor(prompt) + ".glb");

        public static bool TryGetCachedBytes(string prompt, out byte[] bytes)
        {
            bytes = null;
            if (string.IsNullOrWhiteSpace(prompt))
                return false;

            var path = PathFor(prompt);
            if (!File.Exists(path))
                return false;

            try
            {
                bytes = File.ReadAllBytes(path);
                return bytes.Length > 0;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[MeshCache] Failed to read cached mesh for \"{prompt}\": {e.Message}");
                bytes = null;
                return false;
            }
        }

        public static void Save(string prompt, byte[] bytes)
        {
            if (string.IsNullOrWhiteSpace(prompt) || bytes == null || bytes.Length == 0)
                return;

            try
            {
                Directory.CreateDirectory(CacheDir);
                File.WriteAllBytes(PathFor(prompt), bytes);
                Debug.Log($"[MeshCache] Cached {bytes.Length} bytes for \"{prompt}\".");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[MeshCache] Failed to cache mesh for \"{prompt}\": {e.Message}");
            }
        }
    }
}

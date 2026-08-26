using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ObjectSpawning
{
    // Stage 10: remembers every prompt that's ever been successfully generated, independent of
    // the current scene -- so "use the lamp from earlier" still works after the room (or even
    // that specific object) has been cleared. Persists across app restarts (JSON on disk, same
    // "keep it simple" convention as PrimitiveSpawner's own scene save), separate from the scene
    // save file since the library is a standing personal collection, not part of any one scene.
    public static class AssetLibrary
    {
        [Serializable]
        class LibraryFile
        {
            public List<string> prompts = new();
        }

        static string FilePath => Path.Combine(Application.persistentDataPath, "asset_library.json");

        static LibraryFile cached;

        static LibraryFile Load()
        {
            if (cached != null)
                return cached;

            if (File.Exists(FilePath))
            {
                try
                {
                    cached = JsonUtility.FromJson<LibraryFile>(File.ReadAllText(FilePath));
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[AssetLibrary] Failed to read library file: {e.Message}");
                }
            }

            cached ??= new LibraryFile();
            return cached;
        }

        static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath) ?? Application.persistentDataPath);
                File.WriteAllText(FilePath, JsonUtility.ToJson(cached));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AssetLibrary] Failed to save library file: {e.Message}");
            }
        }

        // Moves an existing entry to the end (most-recently-used) rather than duplicating it, so
        // regenerating the same prompt doesn't clutter the library with repeats and "the lamp
        // from earlier" always resolves to the most recent lamp if there's more than one.
        public static void Remember(string prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt))
                return;

            var file = Load();
            file.prompts.RemoveAll(p => string.Equals(p, prompt, StringComparison.OrdinalIgnoreCase));
            file.prompts.Add(prompt);
            Save();
        }

        public static IReadOnlyList<string> All => Load().prompts;

        // Most-recently-remembered prompt containing every word of the query -- "the lamp from
        // earlier" (query="lamp") matches a stored prompt like "a brass desk lamp". No fuzzier
        // than that on purpose: real natural-language matching needs the LLM (see the backend's
        // recall_asset action), this is only the local, no-backend fallback.
        public static bool TryFindByQuery(string query, out string prompt)
        {
            prompt = null;
            if (string.IsNullOrWhiteSpace(query))
                return false;

            var words = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
                return false;

            var entries = Load().prompts;
            for (var i = entries.Count - 1; i >= 0; i--)
            {
                var candidate = entries[i].ToLowerInvariant();
                var matchesAll = true;
                foreach (var word in words)
                {
                    if (!candidate.Contains(word))
                    {
                        matchesAll = false;
                        break;
                    }
                }

                if (matchesAll)
                {
                    prompt = entries[i];
                    return true;
                }
            }

            return false;
        }
    }
}

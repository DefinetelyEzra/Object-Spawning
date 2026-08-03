using System;
using System.Collections.Generic;
using UnityEngine;

namespace ObjectSpawning
{
    // Shared color-name table used by both the keyword parser (Stage 1) and the LLM intent client (Stage 2).
    public static class ColorNaming
    {
        static readonly (string Name, Color Color)[] Colors =
        {
            ("red", Color.red),
            ("green", Color.green),
            ("blue", Color.blue),
            ("yellow", Color.yellow),
            ("white", Color.white),
            ("black", Color.black),
            ("orange", new Color(1f, 0.5f, 0f)),
            ("purple", new Color(0.5f, 0f, 0.5f)),
            ("pink", new Color(1f, 0.4f, 0.7f)),
            ("gray", new Color(0.5f, 0.5f, 0.5f)),
            ("brown", new Color(0.4f, 0.25f, 0.1f)),
            ("cyan", Color.cyan),
        };

        public static IReadOnlyList<(string Name, Color Color)> All => Colors;

        public static bool TryGetColor(string name, out Color color)
        {
            if (!string.IsNullOrEmpty(name))
            {
                foreach (var (candidateName, candidateColor) in Colors)
                {
                    if (string.Equals(candidateName, name, StringComparison.OrdinalIgnoreCase))
                    {
                        color = candidateColor;
                        return true;
                    }
                }
            }

            color = Color.white;
            return false;
        }
    }
}

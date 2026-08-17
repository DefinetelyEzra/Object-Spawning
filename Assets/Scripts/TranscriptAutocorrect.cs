using System;
using System.Collections.Generic;

namespace ObjectSpawning
{
    // Corrects near-miss mis-transcriptions of the specific words that actually drive intent
    // recognition (VoiceIntentParser.KnownVocabulary: shape/color/edit-verb/relation keywords),
    // applied once before either parsing path (LLM or local keyword fallback). Deliberately
    // narrow -- this is not a general spellchecker. A word with no close-enough vocabulary match
    // is left untouched, including ones that don't affect parsing at all: "spawn" itself is never
    // corrected against anything, since neither parser requires that specific word, only the
    // shape/color/verb that follows it.
    public static class TranscriptAutocorrect
    {
        // word.Length / this gives the max edit distance allowed for a correction -- scales
        // tolerance with word length so short, common words aren't over-corrected into unrelated
        // vocabulary (e.g. a 2-letter word can never be "close enough" to trigger a correction).
        const int MaxDistanceDivisor = 3;
        const int MinCorrectableLength = 3;

        // "create" is one edit away from "crate" (a real shape noun) and would otherwise get
        // silently corrected into it every single time -- confirmed in headset testing: "Create
        // a light" -> "crate a light", which is harmless when the LLM parses it (it understands
        // "crate a light" as a create command regardless), but corrupts the LOCAL fallback's
        // leftmost-shape-wins logic into resolving shape=crate instead of the real shape whenever
        // the LLM path fails for any reason. Denylisted the same way "spawn" already implicitly
        // is (never a correction target), just explicit here since proximity to "crate" would
        // otherwise pull it in.
        static readonly HashSet<string> NeverCorrect = new() { "create" };

        public static string Correct(string transcript)
        {
            if (string.IsNullOrWhiteSpace(transcript))
                return transcript;

            var words = transcript.Split(' ');
            for (var i = 0; i < words.Length; i++)
                words[i] = CorrectWord(words[i]);

            return string.Join(' ', words);
        }

        static string CorrectWord(string word)
        {
            var start = 0;
            var end = word.Length;
            while (start < end && !char.IsLetter(word[start]))
                start++;
            while (end > start && !char.IsLetter(word[end - 1]))
                end--;

            if (end - start < MinCorrectableLength)
                return word;

            var lower = word.Substring(start, end - start).ToLowerInvariant();

            if (NeverCorrect.Contains(lower))
                return word;

            string bestMatch = null;
            var bestDistance = int.MaxValue;
            foreach (var candidate in VoiceIntentParser.KnownVocabulary)
            {
                if (candidate == lower)
                    return word; // already an exact match -- nothing to correct

                var distance = LevenshteinDistance(lower, candidate);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestMatch = candidate;
                }
            }

            var maxAllowed = Math.Max(1, lower.Length / MaxDistanceDivisor);
            if (bestMatch == null || bestDistance > maxAllowed)
                return word;

            return word.Substring(0, start) + bestMatch + word.Substring(end);
        }

        static int LevenshteinDistance(string a, string b)
        {
            var dp = new int[a.Length + 1, b.Length + 1];
            for (var i = 0; i <= a.Length; i++)
                dp[i, 0] = i;
            for (var j = 0; j <= b.Length; j++)
                dp[0, j] = j;

            for (var i = 1; i <= a.Length; i++)
            {
                for (var j = 1; j <= b.Length; j++)
                {
                    var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    dp[i, j] = Math.Min(Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1), dp[i - 1, j - 1] + cost);
                }
            }

            return dp[a.Length, b.Length];
        }
    }
}

using NUnit.Framework;

namespace ObjectSpawning.Tests
{
    public class TranscriptAutocorrectEditTests
    {
        [TestCase("cub", "cube")]
        [TestCase("speher", "sphere")]
        [TestCase("delet", "delete")]
        [TestCase("shrikn", "shrink")]
        [TestCase("crat", "crate")]
        public void Correct_NearMissVocabularyWord_CorrectsToClosestMatch(string misheard, string expected)
        {
            var result = TranscriptAutocorrect.Correct($"spawn a {misheard}");

            Assert.AreEqual($"spawn a {expected}", result);
        }

        [TestCase("spawn a cube")]
        [TestCase("delete that")]
        [TestCase("move it next to the table")]
        public void Correct_AlreadyExactVocabulary_LeavesTranscriptUnchanged(string transcript)
        {
            Assert.AreEqual(transcript, TranscriptAutocorrect.Correct(transcript));
        }

        [Test]
        public void Correct_UnrelatedMishearing_LeavesWordUnchanged()
        {
            // "respond" isn't close enough to any known vocabulary word (and "spawn" itself isn't
            // in the vocabulary at all, since neither parser requires that specific word) --
            // autocorrect should not attempt a correction here rather than guess wrong.
            var result = TranscriptAutocorrect.Correct("respond a cube");

            Assert.AreEqual("respond a cube", result);
        }

        [TestCase("on")]
        [TestCase("to")]
        [TestCase("it")]
        public void Correct_ShortWord_NeverCorrected(string shortWord)
        {
            Assert.AreEqual(shortWord, TranscriptAutocorrect.Correct(shortWord));
        }

        [Test]
        public void Correct_PreservesPunctuationAroundCorrectedWord()
        {
            var result = TranscriptAutocorrect.Correct("spawn a cub.");

            Assert.AreEqual("spawn a cube.", result);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void Correct_NullOrWhitespace_ReturnsInputUnchanged(string input)
        {
            Assert.AreEqual(input, TranscriptAutocorrect.Correct(input));
        }
    }
}

using NUnit.Framework;
using UnityEngine;

namespace ObjectSpawning.Tests
{
    public class VoiceIntentParserTests
    {
        [TestCase("spawn a cube", PrimitiveShape.Cube)]
        [TestCase("make a sphere", PrimitiveShape.Sphere)]
        [TestCase("give me a cylinder", PrimitiveShape.Cylinder)]
        [TestCase("spawn a box", PrimitiveShape.Cube)]
        [TestCase("spawn a ball", PrimitiveShape.Sphere)]
        [TestCase("give me a table", PrimitiveShape.Table)]
        [TestCase("make a desk", PrimitiveShape.Table)]
        [TestCase("spawn a shelf", PrimitiveShape.Shelf)]
        [TestCase("give me a bookshelf", PrimitiveShape.Shelf)]
        [TestCase("make a light", PrimitiveShape.PointLight)]
        [TestCase("spawn a crate", PrimitiveShape.Crate)]
        [TestCase("give me a chair", PrimitiveShape.Chair)]
        public void TryParse_RecognizesShape(string transcript, PrimitiveShape expectedShape)
        {
            var ok = VoiceIntentParser.TryParse(transcript, out var intent);

            Assert.IsTrue(ok);
            Assert.AreEqual(expectedShape, intent.Shape);
        }

        [Test]
        public void TryParse_MakeASphereRed_ParsesColor()
        {
            var ok = VoiceIntentParser.TryParse("make a sphere red", out var intent);

            Assert.IsTrue(ok);
            Assert.AreEqual(PrimitiveShape.Sphere, intent.Shape);
            Assert.AreEqual(Color.red, intent.Color);
        }

        [Test]
        public void TryParse_BigCylinder_ParsesLargeScale()
        {
            var ok = VoiceIntentParser.TryParse("big cylinder", out var intent);

            Assert.IsTrue(ok);
            Assert.AreEqual(PrimitiveShape.Cylinder, intent.Shape);
            Assert.Greater(intent.Scale, 0.2f);
        }

        [Test]
        public void TryParse_SmallCube_ParsesSmallScale()
        {
            var ok = VoiceIntentParser.TryParse("spawn a small cube", out var intent);

            Assert.IsTrue(ok);
            Assert.Less(intent.Scale, 0.2f);
        }

        [Test]
        public void TryParse_NoShapeKeyword_ReturnsFalse()
        {
            var ok = VoiceIntentParser.TryParse("hello there how are you", out _);

            Assert.IsFalse(ok);
        }

        [Test]
        public void TryParse_EmptyString_ReturnsFalse()
        {
            var ok = VoiceIntentParser.TryParse("", out _);

            Assert.IsFalse(ok);
        }

        [Test]
        public void TryParse_PartialWordDoesNotFalsePositiveMatch()
        {
            // "cubed" should not match "cube" as a whole word in a phrase with no real shape.
            var ok = VoiceIntentParser.TryParse("the number cubed is large", out _);

            Assert.IsFalse(ok);
        }
    }
}

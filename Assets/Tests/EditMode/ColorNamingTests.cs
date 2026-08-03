using NUnit.Framework;
using UnityEngine;

namespace ObjectSpawning.Tests
{
    public class ColorNamingTests
    {
        [Test]
        public void TryGetColor_KnownName_ReturnsMatchingColor()
        {
            Assert.IsTrue(ColorNaming.TryGetColor("red", out var color));
            Assert.AreEqual(Color.red, color);
        }

        [Test]
        public void TryGetColor_IsCaseInsensitive()
        {
            Assert.IsTrue(ColorNaming.TryGetColor("ReD", out var color));
            Assert.AreEqual(Color.red, color);
        }

        [Test]
        public void TryGetColor_UnknownName_ReturnsFalseAndWhite()
        {
            Assert.IsFalse(ColorNaming.TryGetColor("chartreuse", out var color));
            Assert.AreEqual(Color.white, color);
        }

        [Test]
        public void TryGetColor_NullOrEmpty_ReturnsFalse()
        {
            Assert.IsFalse(ColorNaming.TryGetColor(null, out _));
            Assert.IsFalse(ColorNaming.TryGetColor("", out _));
        }
    }
}

using NUnit.Framework;
using UnityEngine;

namespace ObjectSpawning.Tests
{
    public class VoiceIntentParserEditTests
    {
        [TestCase("make it bigger", EditAction.Resize, true)]
        [TestCase("that's too small, make it larger", EditAction.Resize, true)]
        [TestCase("grow it a bit", EditAction.Resize, true)]
        [TestCase("make it smaller", EditAction.Resize, false)]
        [TestCase("shrink it", EditAction.Resize, false)]
        public void TryParseEditAction_RecognizesResizeDirection(string transcript, EditAction expectedAction, bool expectedBigger)
        {
            var ok = VoiceIntentParser.TryParseEditAction(transcript, out var intent);

            Assert.IsTrue(ok);
            Assert.AreEqual(expectedAction, intent.Action);
            Assert.AreEqual(expectedBigger, intent.Bigger);
        }

        [TestCase("delete that", EditAction.Delete)]
        [TestCase("remove it", EditAction.Delete)]
        [TestCase("duplicate that", EditAction.Duplicate)]
        [TestCase("copy it", EditAction.Duplicate)]
        [TestCase("rotate it", EditAction.Rotate)]
        [TestCase("turn it around", EditAction.Rotate)]
        [TestCase("move it here", EditAction.Move)]
        [TestCase("bring it closer", EditAction.Move)]
        public void TryParseEditAction_RecognizesActionVerb(string transcript, EditAction expectedAction)
        {
            var ok = VoiceIntentParser.TryParseEditAction(transcript, out var intent);

            Assert.IsTrue(ok);
            Assert.AreEqual(expectedAction, intent.Action);
        }

        [Test]
        public void TryParseEditAction_MakeTheTableBigger_WinsOverShapeNoun()
        {
            // "table" is a valid shape keyword, but the edit verb should take priority --
            // this must resolve as an edit, not a request to spawn a new table.
            var ok = VoiceIntentParser.TryParseEditAction("make the table bigger", out var intent);

            Assert.IsTrue(ok);
            Assert.AreEqual(EditAction.Resize, intent.Action);
            Assert.IsTrue(intent.Bigger);
        }

        [Test]
        public void TryParseEditAction_NoEditVerb_ReturnsFalse()
        {
            var ok = VoiceIntentParser.TryParseEditAction("spawn a red cube", out _);

            Assert.IsFalse(ok);
        }

        [Test]
        public void TryParseRecolor_ColorWordAlone_RecognizesRecolor()
        {
            var ok = VoiceIntentParser.TryParseRecolor("make it red", out var intent);

            Assert.IsTrue(ok);
            Assert.AreEqual(EditAction.Recolor, intent.Action);
            Assert.AreEqual(Color.red, intent.Color);
        }

        [Test]
        public void TryParseRecolor_NoColorWord_ReturnsFalse()
        {
            var ok = VoiceIntentParser.TryParseRecolor("delete that", out _);

            Assert.IsFalse(ok);
        }

        [TestCase("put a lamp on the table", PrimitiveShape.LampBase, SpatialRelation.On, PrimitiveShape.Table)]
        [TestCase("place a crate next to the shelf", PrimitiveShape.Crate, SpatialRelation.NextTo, PrimitiveShape.Shelf)]
        public void TryParse_RecognizesSpatialRelation(string transcript, PrimitiveShape expectedShape,
            SpatialRelation expectedRelation, PrimitiveShape expectedReferenceShape)
        {
            var ok = VoiceIntentParser.TryParse(transcript, out var intent);

            Assert.IsTrue(ok);
            Assert.AreEqual(expectedShape, intent.Shape);
            Assert.AreEqual(expectedRelation, intent.Relation);
            Assert.AreEqual(expectedReferenceShape, intent.ReferenceShape);
        }

        [Test]
        public void TryParse_NoSpatialPhrase_LeavesRelationUnset()
        {
            var ok = VoiceIntentParser.TryParse("spawn a red cube", out var intent);

            Assert.IsTrue(ok);
            Assert.IsNull(intent.Relation);
            Assert.IsNull(intent.ReferenceShape);
        }

        [Test]
        public void TryParse_OnWithNoSecondShape_LeavesRelationUnset()
        {
            // "on" with nothing recognizable afterward (not a shape, not ground/floor) should
            // degrade gracefully, not guess.
            var ok = VoiceIntentParser.TryParse("put a cube on there", out var intent);

            Assert.IsTrue(ok);
            Assert.AreEqual(PrimitiveShape.Cube, intent.Shape);
            Assert.IsNull(intent.Relation);
        }

        [TestCase("put a cube on the ground")]
        [TestCase("place a crate on the floor")]
        public void TryParse_RecognizesOnGroundRelation(string transcript)
        {
            var ok = VoiceIntentParser.TryParse(transcript, out var intent);

            Assert.IsTrue(ok);
            Assert.AreEqual(SpatialRelation.OnGround, intent.Relation);
            Assert.IsNull(intent.ReferenceShape);
        }

        [TestCase("move it to the ground", SpatialRelation.OnGround, null)]
        [TestCase("move it to the floor", SpatialRelation.OnGround, null)]
        [TestCase("move it onto the table", SpatialRelation.On, PrimitiveShape.Table)]
        [TestCase("move it next to the shelf", SpatialRelation.NextTo, PrimitiveShape.Shelf)]
        public void TryParseEditAction_Move_RecognizesSpatialRelation(string transcript,
            SpatialRelation expectedRelation, PrimitiveShape? expectedReferenceShape)
        {
            var ok = VoiceIntentParser.TryParseEditAction(transcript, out var intent);

            Assert.IsTrue(ok);
            Assert.AreEqual(EditAction.Move, intent.Action);
            Assert.AreEqual(expectedRelation, intent.Relation);
            Assert.AreEqual(expectedReferenceShape, intent.ReferenceShape);
        }

        [Test]
        public void TryParseEditAction_MoveWithNoRelation_LeavesRelationUnset()
        {
            var ok = VoiceIntentParser.TryParseEditAction("move it here", out var intent);

            Assert.IsTrue(ok);
            Assert.AreEqual(EditAction.Move, intent.Action);
            Assert.IsNull(intent.Relation);
            Assert.IsNull(intent.ReferenceShape);
        }
    }
}

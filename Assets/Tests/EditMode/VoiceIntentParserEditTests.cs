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

        [TestCase("make it 10x bigger", true, 10f)]
        [TestCase("make it 10 x bigger", true, 10f)]
        [TestCase("make it 3 times bigger", true, 3f)]
        [TestCase("make it 2x smaller", false, 2f)]
        public void TryParseEditAction_RecognizesResizeMultiplier(string transcript, bool expectedBigger, float expectedMultiplier)
        {
            var ok = VoiceIntentParser.TryParseEditAction(transcript, out var intent);

            Assert.IsTrue(ok);
            Assert.AreEqual(EditAction.Resize, intent.Action);
            Assert.AreEqual(expectedBigger, intent.Bigger);
            Assert.IsTrue(intent.ResizeMultiplier.HasValue);
            Assert.AreEqual(expectedMultiplier, intent.ResizeMultiplier.Value);
        }

        [Test]
        public void TryParseEditAction_ResizeWithNoMultiplier_LeavesMultiplierUnset()
        {
            var ok = VoiceIntentParser.TryParseEditAction("make it bigger", out var intent);

            Assert.IsTrue(ok);
            Assert.IsFalse(intent.ResizeMultiplier.HasValue);
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

        [TestCase("make the room brighter", true)]
        [TestCase("brighten the scene", true)]
        [TestCase("dim the lighting", false)]
        [TestCase("darken the environment", false)]
        public void TryParseLighting_SceneWordWithBrightness_RecognizesAdjustLighting(string transcript, bool expectedBrighter)
        {
            var ok = VoiceIntentParser.TryParseLighting(transcript, out var intent);

            Assert.IsTrue(ok);
            Assert.AreEqual(EditAction.AdjustLighting, intent.Action);
            Assert.IsTrue(intent.LightBrighter.HasValue);
            Assert.AreEqual(expectedBrighter, intent.LightBrighter.Value);
        }

        [Test]
        public void TryParseLighting_SceneWordWithColorOnly_LeavesBrightnessUnset()
        {
            var ok = VoiceIntentParser.TryParseLighting("make the lighting blue", out var intent);

            Assert.IsTrue(ok);
            Assert.AreEqual(EditAction.AdjustLighting, intent.Action);
            Assert.IsFalse(intent.LightBrighter.HasValue);
            Assert.AreEqual(Color.blue, intent.Color);
        }

        [TestCase("make it brighter")]
        [TestCase("dim it")]
        [TestCase("delete that")]
        public void TryParseLighting_NoSceneWord_ReturnsFalse(string transcript)
        {
            var ok = VoiceIntentParser.TryParseLighting(transcript, out _);

            Assert.IsFalse(ok);
        }

        [Test]
        public void TryParseLighting_SceneWordAloneWithNoChange_ReturnsFalse()
        {
            var ok = VoiceIntentParser.TryParseLighting("nice room", out _);

            Assert.IsFalse(ok);
        }

        [Test]
        public void TryParseEditAction_BrighterWithNoSceneWord_IsPlainResize()
        {
            // Disambiguation: TryParseLighting is checked first in VoiceCommandController's own
            // fallback chain and declines here (no scene word), so a bare "make it brighter"
            // reaching TryParseEditAction directly must resolve as an ordinary Resize, not
            // AdjustLighting -- confirms the two can't collide.
            var ok = VoiceIntentParser.TryParseEditAction("make it brighter", out var intent);

            Assert.IsTrue(ok);
            Assert.AreEqual(EditAction.Resize, intent.Action);
            Assert.IsTrue(intent.Bigger);
        }

        [TestCase("make it look like rusted metal", "rusted_metal")]
        [TestCase("make it wood", "wood")]
        [TestCase("give it a marble finish", "marble")]
        public void TryParseRetexture_MaterialWordAlone_RecognizesRetexture(string transcript, string expectedMaterialName)
        {
            var ok = VoiceIntentParser.TryParseRetexture(transcript, out var intent);

            Assert.IsTrue(ok);
            Assert.AreEqual(EditAction.Retexture, intent.Action);
            Assert.IsTrue(intent.Material.HasValue);
            Assert.AreEqual(expectedMaterialName, intent.Material.Value.Name);
        }

        [Test]
        public void TryParseRetexture_NoMaterialWord_ReturnsFalse()
        {
            var ok = VoiceIntentParser.TryParseRetexture("delete that", out _);

            Assert.IsFalse(ok);
        }

        [Test]
        public void TryParse_RecognizesLightShape()
        {
            var ok = VoiceIntentParser.TryParse("spawn a light", out var intent);

            Assert.IsTrue(ok);
            Assert.AreEqual(PrimitiveShape.PointLight, intent.Shape);
        }

        [TestCase("put a light on the table", PrimitiveShape.PointLight, SpatialRelation.On, PrimitiveShape.Table)]
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

        // "wire frame" (two words) is a regression case, not a nice-to-have: confirmed in headset
        // testing that STT reliably transcribes "wireframe" as two separate words, which silently
        // failed to match every phrase in WireframePhrases (all one-word "wireframe") across three
        // separate attempts before this was fixed.
        [TestCase("show the wireframe", EditAction.ShowWireframe)]
        [TestCase("show the wire frame", EditAction.ShowWireframe)]
        [TestCase("show me the edges", EditAction.ShowWireframe)]
        [TestCase("show the uv map", EditAction.ShowUvMapping)]
        [TestCase("show how it's textured", EditAction.ShowUvMapping)]
        [TestCase("show the final render", EditAction.ShowNormalRendering)]
        [TestCase("hide the wireframe", EditAction.ShowNormalRendering)]
        public void TryParsePipelineView_RecognizesPhrase(string transcript, EditAction expectedAction)
        {
            var ok = VoiceIntentParser.TryParsePipelineView(transcript, out var intent);

            Assert.IsTrue(ok);
            Assert.AreEqual(expectedAction, intent.Action);
        }

        [Test]
        public void TryParsePipelineView_NoMatch_ReturnsFalse()
        {
            var ok = VoiceIntentParser.TryParsePipelineView("spawn a red cube", out _);

            Assert.IsFalse(ok);
        }
    }
}

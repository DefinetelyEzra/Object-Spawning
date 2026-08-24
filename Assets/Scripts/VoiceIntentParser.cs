using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;

namespace ObjectSpawning
{
    // Despite the name (kept for minimal churn from Stage 1/2), this now also covers Stage 3's
    // procedurally-composed objects (Table/Shelf/Crate/Chair), not just true primitives.
    // Stage 6's Generated is a registry-bookkeeping sentinel only -- it never drives a
    // CreatePrimitive/ProceduralGeometryFactory path like the others; PrimitiveSpawner always
    // builds a generated object's placeholder as a plain cube and swaps its visuals in later.
    // Stage 8: PointLight is a real UnityEngine.Light-carrying object (a small visible bulb
    // sphere + a Light component) -- it participates in the exact same registry/pointer-
    // selection/edit machinery as every other shape (see PrimitiveSpawner's BuildPointLight),
    // rather than needing a parallel system. There used to also be a purely decorative LampBase
    // composite here, but it was removed -- in headset testing the LLM reliably conflated "spawn
    // a light" with the decorative lamp rather than the new functional PointLight regardless of
    // how the two were distinguished in the prompt, so the ambiguous shape was cut instead of
    // chasing an unreliable prompt fix.
    public enum PrimitiveShape
    {
        Cube,
        Sphere,
        Cylinder,
        Table,
        Shelf,
        Crate,
        Chair,
        Stool,
        Bench,
        Sofa,
        PointLight,
        Generated,
    }

    // Move-by-distance ("move it 3 meters to the left"), relative to the PLAYER's current
    // flattened facing direction -- not the object's own orientation, and not world axes, since
    // "left" only means something intuitive relative to whoever's giving the command.
    public enum MoveDirection
    {
        Left,
        Right,
        Forward,
        Backward,
        Up,
        Down,
    }

    // Stage 6: a request for a real generated mesh (via Tripo3D) rather than the fixed
    // procedural shape library. Carries only the clean text-to-3D prompt the LLM derived from
    // the transcript -- there's no local keyword-fallback equivalent, since composing a
    // sensible generation prompt from loose speech genuinely needs the LLM.
    public readonly struct GenerateIntent
    {
        public readonly string Prompt;
        public GenerateIntent(string prompt) => Prompt = prompt;
    }

    // Stage 5: how a newly created object should be placed relative to an existing one.
    // "In front of the user" -- the roadmap's third relation -- needs no explicit case: it's just
    // what happens when Relation/ReferenceShape are unset, which is already the pre-Stage-5 default.
    // OnGround ("put it on the floor/ground") is a variant of On that needs no reference object at
    // all -- the floor is part of the environment, not something in the spawn registry.
    public enum SpatialRelation
    {
        On,
        NextTo,
        OnGround,
    }

    public readonly struct SpawnIntent
    {
        public readonly PrimitiveShape Shape;
        public readonly Color Color;
        public readonly float Scale;
        public readonly SpatialRelation? Relation;
        public readonly PrimitiveShape? ReferenceShape;

        public SpawnIntent(PrimitiveShape shape, Color color, float scale,
            SpatialRelation? relation = null, PrimitiveShape? referenceShape = null)
        {
            Shape = shape;
            Color = color;
            Scale = scale;
            Relation = relation;
            ReferenceShape = referenceShape;
        }
    }

    // Stage 4: what to do to an existing object. Which object is a separate concern, resolved
    // by whatever the player is pointing at (or the last one touched) -- never encoded here.
    public enum EditAction
    {
        Resize,
        Recolor,
        Move,
        Rotate,
        Duplicate,
        Delete,
        ClearAll,
        Retexture,

        // Stage 8: adjusts the SCENE's directional/ambient light, not a specific object -- has no
        // target to resolve (like ClearAll), dispatched as an early exit before pointer/last-
        // touched resolution runs. Only triggered by an explicit scene-referring word ("room",
        // "lighting", "ambiance", "environment", "scene") so it can never collide with an ordinary
        // per-object edit -- "make it brighter" on a pointed-at light still goes through the
        // normal Resize path, which PrimitiveSpawner.Resize reinterprets as intensity for a Light.
        AdjustLighting,

        // Stage 9: reverts the single most recent mutating command (any action below this point
        // in the enum, or a prior create/generate) -- has no target of its own to resolve, same
        // early-exit dispatch as ClearAll/AdjustLighting.
        Undo,

        // Stage 9: "try a different style" -- retexture the current object with a different
        // curated preset than whatever it's wearing now, WITHOUT the user naming a specific one.
        // Unlike Retexture, has no Material of its own; PrimitiveSpawner.RerollStyle picks it.
        RerollStyle,
    }

    public readonly struct EditIntent
    {
        public readonly EditAction Action;

        // Meaningful for Recolor (falls back to white if null -- ColorNaming's own established
        // default) and for AdjustLighting (null means no color change was requested at all, which
        // AdjustLighting -- unlike Recolor -- must be able to express, since a pure brightness
        // command like "make the room brighter" must never silently reset the light's color).
        public readonly Color? Color;

        public readonly bool Bigger;   // only meaningful for Resize -- always has a direction

        // Explicit factor ("make it 10x bigger", "make it 2x smaller") -- an alternative to the
        // default fixed 1.25x/0.8x single-press step, not a combination of the two. Null means
        // "no specific factor given", which callers should treat as that old fixed-step default.
        // Bigger applies this as a straight multiplier (10x bigger -> *10); smaller applies it as
        // a divisor (2x smaller -> half size, i.e. *0.5), matching how people actually say it.
        public readonly float? ResizeMultiplier;
        public readonly SpatialRelation? Relation;       // only meaningful for Move
        public readonly PrimitiveShape? ReferenceShape;  // only meaningful for Move

        // Move-by-distance ("move it 3 meters to the left") -- an alternative to Relation for
        // Move, not a combination of the two. Both set means "use the distance/direction move";
        // only Relation set means the existing on/next_to/on_ground placement.
        public readonly float? MoveDistanceMeters;
        public readonly MoveDirection? MoveDirectionValue;

        // Rotate-by-degrees ("rotate it 180 degrees") -- null means "no specific amount given",
        // which callers should treat as the old fixed single-press default.
        public readonly float? RotateDegrees;

        // Stage 7: only meaningful for Retexture -- always set when Action is Retexture, never
        // otherwise. Unlike Recolor's color (which gracefully defaults to white when unrecognized),
        // an unrecognized material has no sensible default, so callers that can't resolve one
        // should fail to produce a Retexture intent at all rather than set this to null.
        public readonly MaterialPreset? Material;

        // Stage 8: only meaningful for AdjustLighting -- null means no brightness change was
        // requested (as opposed to Resize's Bigger, which is always meaningful since a resize
        // command always implies some direction). ResizeMultiplier above is reused as-is for
        // AdjustLighting's own multiplier -- "how much" means the same thing in both contexts.
        public readonly bool? LightBrighter;

        public EditIntent(EditAction action, Color? color = null, bool bigger = true,
            SpatialRelation? relation = null, PrimitiveShape? referenceShape = null,
            float? moveDistanceMeters = null, MoveDirection? moveDirection = null,
            float? rotateDegrees = null, MaterialPreset? material = null, float? resizeMultiplier = null,
            bool? lightBrighter = null)
        {
            Action = action;
            Color = color;
            Bigger = bigger;
            Relation = relation;
            ReferenceShape = referenceShape;
            MoveDistanceMeters = moveDistanceMeters;
            MoveDirectionValue = moveDirection;
            RotateDegrees = rotateDegrees;
            Material = material;
            ResizeMultiplier = resizeMultiplier;
            LightBrighter = lightBrighter;
        }
    }

    // Stage 1: dumbest-possible intent parser. Keyword matching only, no LLM.
    // Scans for a shape keyword (required), a color keyword (optional), and a size keyword (optional).
    public static class VoiceIntentParser
    {
        public const float DefaultScale = 0.2f;
        public const float BigScale = 0.5f;
        public const float SmallScale = 0.08f;

        // Applied when a move command names a direction but no explicit distance ("move it
        // left") -- a reasonable single nudge, distinct from a number the user actually said.
        public const float DefaultMoveDistanceMeters = 0.3f;

        static readonly (string keyword, PrimitiveShape shape)[] ShapeKeywords =
        {
            ("cube", PrimitiveShape.Cube),
            ("box", PrimitiveShape.Cube),
            ("sphere", PrimitiveShape.Sphere),
            ("ball", PrimitiveShape.Sphere),
            ("cylinder", PrimitiveShape.Cylinder),
            ("table", PrimitiveShape.Table),
            ("desk", PrimitiveShape.Table),
            ("shelf", PrimitiveShape.Shelf),
            ("bookshelf", PrimitiveShape.Shelf),
            ("crate", PrimitiveShape.Crate),
            ("chair", PrimitiveShape.Chair),
            ("stool", PrimitiveShape.Stool),
            ("bench", PrimitiveShape.Bench),
            ("sofa", PrimitiveShape.Sofa),
            ("couch", PrimitiveShape.Sofa),
            ("light", PrimitiveShape.PointLight),
        };

        public static bool TryParse(string transcript, out SpawnIntent intent)
        {
            intent = default;
            if (string.IsNullOrWhiteSpace(transcript))
                return false;

            var text = transcript.ToLowerInvariant();

            // Leftmost mention wins (not first-in-array-order): "put a light on the table" must
            // resolve the object being created as "light", even though "table" appears earlier in
            // ShapeKeywords -- the earlier-declared-but-later-spoken word isn't what's being made.
            if (!TryFindLeftmostShape(text, 0, out var shape, out var shapeIndex))
                return false;

            var color = Color.white;
            foreach (var (keyword, candidate) in ColorNaming.All)
            {
                if (ContainsWord(text, keyword))
                {
                    color = candidate;
                    break;
                }
            }

            var scale = DefaultScale;
            if (ContainsWord(text, "big") || ContainsWord(text, "large"))
                scale = BigScale;
            else if (ContainsWord(text, "small") || ContainsWord(text, "tiny"))
                scale = SmallScale;

            var (relation, referenceShape) = TryFindSpatialRelation(text, shapeIndex);

            intent = new SpawnIntent(shape, color, scale, relation, referenceShape);
            return true;
        }

        // Only recognizes a relation when there's an unambiguous second shape mention after
        // "on"/"next to" to serve as the reference -- e.g. "put a light on the table". No second
        // shape mention (or no relation keyword at all) just leaves Relation unset, which is the
        // graceful degradation the roadmap asks for: the object spawns at the normal default
        // position instead of guessing.
        static (SpatialRelation? relation, PrimitiveShape? referenceShape) TryFindSpatialRelation(string text, int primaryShapeIndex)
        {
            var nextToIndex = text.IndexOf("next to", StringComparison.Ordinal);
            if (nextToIndex >= 0 && nextToIndex > primaryShapeIndex &&
                TryFindLeftmostShape(text, nextToIndex + "next to".Length, out var nextToShape, out _))
            {
                return (SpatialRelation.NextTo, nextToShape);
            }

            // Ground/floor mentions are checked independently of any particular preposition --
            // "put it ON the ground", "move it TO the ground", and "down to the floor" should all
            // resolve the same way, not just phrasings that happen to use the word "on".
            var groundIndex = IndexOfWord(text, "ground");
            var floorIndex = IndexOfWord(text, "floor");
            if ((groundIndex >= 0 && groundIndex > primaryShapeIndex) ||
                (floorIndex >= 0 && floorIndex > primaryShapeIndex))
            {
                return (SpatialRelation.OnGround, null);
            }

            // "onto" doesn't satisfy IndexOfWord("on")'s word-boundary check (the "t" right after
            // "on" isn't a boundary), so it needs its own check -- prefer it when present since
            // it's the more specific/common phrasing ("move it onto the table").
            var ontoIndex = IndexOfWord(text, "onto");
            var onIndex = ontoIndex >= 0 ? ontoIndex : IndexOfWord(text, "on");
            var onWordLength = ontoIndex >= 0 ? 4 : 2;
            if (onIndex >= 0 && onIndex > primaryShapeIndex &&
                TryFindLeftmostShape(text, onIndex + onWordLength, out var onShape, out _))
            {
                return (SpatialRelation.On, onShape);
            }

            return (null, null);
        }

        static bool TryFindLeftmostShape(string text, int minIndex, out PrimitiveShape shape, out int index)
        {
            shape = default;
            index = -1;

            foreach (var (keyword, candidate) in ShapeKeywords)
            {
                var idx = IndexOfWord(text, keyword);
                if (idx >= minIndex && (index == -1 || idx < index))
                {
                    index = idx;
                    shape = candidate;
                }
            }

            return index >= 0;
        }

        static readonly (string keyword, EditAction action)[] EditActionKeywords =
        {
            // Checked before plain "delete"/"remove" would otherwise just target the current
            // edit target -- "clear"/"reset" mean the whole room, not one object, and exhibition
            // demo resets need this to be unambiguous.
            ("clear", EditAction.ClearAll),
            ("reset", EditAction.ClearAll),
            ("delete", EditAction.Delete),
            ("remove", EditAction.Delete),
            ("duplicate", EditAction.Duplicate),
            ("copy", EditAction.Duplicate),
            ("rotate", EditAction.Rotate),
            ("turn", EditAction.Rotate),
            ("move", EditAction.Move),
            ("bring", EditAction.Move),
            ("undo", EditAction.Undo),
        };

        // Exposed for TranscriptAutocorrect: the specific words whose exact recognition actually
        // drives intent extraction (shape/color/edit-verb/resize/relation keywords). Everything
        // else in a spoken phrase -- articles, filler words, "spawn" itself -- is irrelevant to
        // parsing, so autocorrect only needs to defend this vocabulary, not the whole transcript.
        public static readonly string[] KnownVocabulary = BuildVocabulary();

        static string[] BuildVocabulary()
        {
            var words = new List<string>();
            foreach (var (keyword, _) in ShapeKeywords)
                words.Add(keyword);
            foreach (var (keyword, _) in EditActionKeywords)
                words.Add(keyword);
            foreach (var (name, _) in ColorNaming.All)
                words.Add(name);
            foreach (var material in MaterialNaming.All)
                words.AddRange(material.Name.Replace('_', ' ').Split(' '));
            words.AddRange(new[]
            {
                "bigger", "larger", "grow", "smaller", "shrink", "times",
                "big", "large", "small", "tiny",
                "next", "onto", "ground", "floor",
                "left", "right", "forward", "ahead", "backward", "back", "up", "down",
                "meters", "meter", "degrees", "degree", "clockwise", "counterclockwise", "around",
                "brighter", "brighten", "lighter", "dimmer", "dim", "darker", "darken",
                "room", "lighting", "ambiance", "ambience", "environment", "scene",
                "undo", "different", "style", "something", "else", "switch",
            });
            return words.ToArray();
        }

        // Checks unambiguous edit-action verbs (bigger/smaller/delete/rotate/duplicate/move) --
        // deliberately does NOT check color words here, since those alone are ambiguous with a
        // create command ("red cube"). Callers should try this BEFORE TryParse's shape matching,
        // since an edit verb should win even if a shape noun also appears ("make the table bigger"
        // is an edit, not a request to spawn a new table -- Stage 4 doesn't do name-based lookup,
        // per the roadmap's own simplification, so the word "table" there is just discarded).
        public static bool TryParseEditAction(string transcript, out EditIntent intent)
        {
            intent = default;
            if (string.IsNullOrWhiteSpace(transcript))
                return false;

            var text = transcript.ToLowerInvariant();

            // "brighter"/"dimmer" (and synonyms) are generic verbs here too --
            // PrimitiveSpawner.Resize reinterprets bigger/smaller as intensity/range when the
            // target carries a Light component (a spawned PointLight), same generic-verb-on-
            // varying-target pattern as everything else Resize already touches. "dim it"/
            // "brighten it" only reach here at all because TryParseLighting (checked first,
            // requires an explicit scene word) already declined to match. "lighter"/"darker" are
            // deliberately NOT included here (unlike in TryParseLighting, where a scene word
            // already disambiguates them) -- out of context they're genuinely ambiguous with
            // "less heavy"/"a darker shade", not brightness.
            if (ContainsWord(text, "bigger") || ContainsWord(text, "larger") || ContainsWord(text, "grow") ||
                ContainsWord(text, "brighter") || ContainsWord(text, "brighten"))
            {
                var multiplier = TryFindMultiplier(text, out var biggerFactor) ? (float?)biggerFactor : null;
                intent = new EditIntent(EditAction.Resize, bigger: true, resizeMultiplier: multiplier);
                return true;
            }
            if (ContainsWord(text, "smaller") || ContainsWord(text, "shrink") ||
                ContainsWord(text, "dimmer") || ContainsWord(text, "dim"))
            {
                var multiplier = TryFindMultiplier(text, out var smallerFactor) ? (float?)smallerFactor : null;
                intent = new EditIntent(EditAction.Resize, bigger: false, resizeMultiplier: multiplier);
                return true;
            }

            foreach (var (keyword, action) in EditActionKeywords)
            {
                if (ContainsWord(text, keyword))
                {
                    if (action == EditAction.Move)
                    {
                        // A direction word alone ("move it left") wins over relation-based
                        // placement -- distance is a bonus refinement when a number is also
                        // present ("move it 3 meters to the left"), not a requirement, so a
                        // bare direction doesn't fall through to unrelated default placement.
                        // Only reliably catches digit-form numbers ("3", not "three") -- this is
                        // the dumb local fallback; the backend LLM handles spoken-word numbers.
                        if (TryFindDirection(text, out var direction))
                        {
                            var distance = TryFindNumber(text, out var num) ? num : DefaultMoveDistanceMeters;
                            intent = new EditIntent(action, moveDistanceMeters: distance, moveDirection: direction);
                        }
                        else
                        {
                            // "move it to the ground" / "move it onto the table" / "move it next
                            // to the shelf" -- reuses the same relation scan as create, with no
                            // primary shape to anchor against (there's no new object here, just
                            // the edit target), so any relation keyword position qualifies.
                            var (relation, referenceShape) = TryFindSpatialRelation(text, -1);
                            intent = new EditIntent(action, relation: relation, referenceShape: referenceShape);
                        }
                    }
                    else if (action == EditAction.Rotate)
                    {
                        // "rotate it 180 degrees" -> that many degrees. "turn it around"/"flip
                        // it" -> a half turn. Plain "rotate it"/"turn it" with no amount keeps
                        // the old fixed default (null -- caller decides the default). "left"/
                        // "counterclockwise" negates the sign; otherwise it's clockwise (viewed
                        // from above), matching the backend's own convention.
                        float? degrees = null;
                        if (TryFindNumber(text, out var deg))
                            degrees = deg;
                        else if (ContainsWord(text, "around") || ContainsWord(text, "flip"))
                            degrees = 180f;

                        if (degrees.HasValue && (ContainsWord(text, "left") || ContainsWord(text, "counterclockwise")))
                            degrees = -degrees.Value;

                        intent = new EditIntent(action, rotateDegrees: degrees);
                    }
                    else
                    {
                        intent = new EditIntent(action);
                    }
                    return true;
                }
            }

            return false;
        }

        static readonly (string keyword, MoveDirection direction)[] DirectionKeywords =
        {
            ("left", MoveDirection.Left),
            ("right", MoveDirection.Right),
            ("forward", MoveDirection.Forward),
            ("ahead", MoveDirection.Forward),
            ("backward", MoveDirection.Backward),
            ("back", MoveDirection.Backward),
            ("up", MoveDirection.Up),
            ("down", MoveDirection.Down),
        };

        static bool TryFindDirection(string text, out MoveDirection direction)
        {
            foreach (var (keyword, dir) in DirectionKeywords)
            {
                if (ContainsWord(text, keyword))
                {
                    direction = dir;
                    return true;
                }
            }
            direction = default;
            return false;
        }

        static bool TryFindNumber(string text, out float value)
        {
            var match = Regex.Match(text, @"\d+(\.\d+)?");
            if (match.Success && float.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                return true;
            value = 0f;
            return false;
        }

        // "10x bigger" / "10 x bigger" / "10 times bigger" -- only digit-form numbers, same
        // "dumb local fallback" scope as TryFindNumber; the backend LLM handles spoken-word
        // numbers ("ten times bigger").
        static bool TryFindMultiplier(string text, out float value)
        {
            var match = Regex.Match(text, @"(\d+(?:\.\d+)?)\s*(?:x\b|times\b)");
            if (match.Success && float.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                return true;
            value = 0f;
            return false;
        }

        // Last-resort fallback: a color word with no shape word means "recolor the current
        // object" rather than "spawn something this color". Callers should try this only after
        // both TryParseEditAction and TryParse (shape matching) have failed.
        public static bool TryParseRecolor(string transcript, out EditIntent intent)
        {
            intent = default;
            if (string.IsNullOrWhiteSpace(transcript))
                return false;

            var text = transcript.ToLowerInvariant();

            foreach (var (keyword, color) in ColorNaming.All)
            {
                if (ContainsWord(text, keyword))
                {
                    intent = new EditIntent(EditAction.Recolor, color: color);
                    return true;
                }
            }

            return false;
        }

        // Stage 7: same last-resort shape rationale as TryParseRecolor -- a material word with no
        // shape word means "retexture the current object". Checked after TryParseRecolor since
        // it's the same tier of fallback; the two vocabularies don't overlap, so ordering between
        // them doesn't actually matter in practice.
        //
        // Longest matching preset name wins rather than first-array-order: "rusted metal" also
        // contains "metal" as a whole word, so a plain first-match scan would always resolve the
        // shorter, less specific preset instead of the one the user actually said.
        public static bool TryParseRetexture(string transcript, out EditIntent intent)
        {
            intent = default;
            if (string.IsNullOrWhiteSpace(transcript))
                return false;

            var text = transcript.ToLowerInvariant();

            MaterialPreset? best = null;
            foreach (var material in MaterialNaming.All)
            {
                // Preset names use underscores ("rusted_metal") to double as the LLM schema's
                // enum values -- spoken/transcribed text never contains one, so match the
                // space-joined form instead.
                var spoken = material.Name.Replace('_', ' ');
                if (ContainsWord(text, spoken) && (best == null || spoken.Length > best.Value.Name.Replace('_', ' ').Length))
                    best = material;
            }

            if (best == null)
                return false;

            intent = new EditIntent(EditAction.Retexture, material: best.Value);
            return true;
        }

        // Stage 9: multi-word phrases, so this doesn't fit ContainsWord's single-keyword scan --
        // checked as plain substrings instead, same as "next to" above. None of these overlap with
        // any EditActionKeywords/shape/color/material vocabulary, so calling this before or after
        // TryParseEditAction in the fallback chain doesn't actually matter; VoiceCommandController
        // checks it early anyway, alongside TryParseLighting.
        static readonly string[] RerollStylePhrases =
        {
            "different style", "different look", "different material",
            "try something else", "something else", "switch it up",
            "change the style", "change the look", "change the material",
            "another style", "another look",
        };

        // Stage 9: a vague request to change how the current object looks WITHOUT naming a
        // specific material -- distinct from TryParseRetexture, which requires a named material
        // from the curated list. "make it wood" is retexture; "try a different style" is this.
        public static bool TryParseRerollStyle(string transcript, out EditIntent intent)
        {
            intent = default;
            if (string.IsNullOrWhiteSpace(transcript))
                return false;

            var text = transcript.ToLowerInvariant();
            foreach (var phrase in RerollStylePhrases)
            {
                if (text.Contains(phrase))
                {
                    intent = new EditIntent(EditAction.RerollStyle);
                    return true;
                }
            }

            return false;
        }

        static readonly string[] SceneReferringWords =
            { "room", "lighting", "ambiance", "ambience", "environment", "scene" };

        // Stage 8: the SCENE's overall ambient/directional light, not a specific object -- gated
        // behind an explicit scene-referring word so it can never collide with a normal per-object
        // edit ("make it brighter" with nothing scene-related mentioned stays a plain Resize,
        // reinterpreted by PrimitiveSpawner as intensity if the target happens to be a light).
        // Called before TryParseEditAction in VoiceCommandController's fallback chain specifically
        // so this more specific match wins over the generic bigger/smaller check there.
        public static bool TryParseLighting(string transcript, out EditIntent intent)
        {
            intent = default;
            if (string.IsNullOrWhiteSpace(transcript))
                return false;

            var text = transcript.ToLowerInvariant();

            var mentionsScene = false;
            foreach (var word in SceneReferringWords)
            {
                if (ContainsWord(text, word))
                {
                    mentionsScene = true;
                    break;
                }
            }
            if (!mentionsScene)
                return false;

            bool? brighter = null;
            if (ContainsWord(text, "brighter") || ContainsWord(text, "brighten") || ContainsWord(text, "lighter"))
                brighter = true;
            else if (ContainsWord(text, "dimmer") || ContainsWord(text, "dim") || ContainsWord(text, "darker") ||
                ContainsWord(text, "darken"))
                brighter = false;

            Color? color = null;
            foreach (var (keyword, candidate) in ColorNaming.All)
            {
                if (ContainsWord(text, keyword))
                {
                    color = candidate;
                    break;
                }
            }

            // A scene-word alone with no actual change requested ("nice room") isn't a command --
            // degrade gracefully rather than guessing.
            if (!brighter.HasValue && !color.HasValue)
                return false;

            var multiplier = TryFindMultiplier(text, out var m) ? (float?)m : null;
            intent = new EditIntent(EditAction.AdjustLighting, color: color, lightBrighter: brighter, resizeMultiplier: multiplier);
            return true;
        }

        static bool ContainsWord(string text, string word) => IndexOfWord(text, word) >= 0;

        static int IndexOfWord(string text, string word)
        {
            var index = text.IndexOf(word, StringComparison.Ordinal);
            while (index >= 0)
            {
                var leftOk = index == 0 || !char.IsLetter(text[index - 1]);
                var rightIndex = index + word.Length;
                var rightOk = rightIndex >= text.Length || !char.IsLetter(text[rightIndex]);
                if (leftOk && rightOk)
                    return index;

                index = text.IndexOf(word, index + 1, StringComparison.Ordinal);
            }

            return -1;
        }
    }
}

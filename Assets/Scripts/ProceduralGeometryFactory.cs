using UnityEngine;

namespace ObjectSpawning
{
    // Stage 3: small library of parametric generators, each built from composed primitives
    // (CreatePrimitive gives correct normals/UVs/colliders for free -- no hand-authored mesh
    // vertex work, which sidesteps the roadmap's own named risk of fiddly procedural-mesh edge
    // cases). Every generator builds at a fixed, real-world-proportioned canonical size; overall
    // small/medium/large sizing is applied uniformly by the caller via the root transform's scale,
    // same as it already is for plain primitives.
    public static class ProceduralGeometryFactory
    {
        public static GameObject Build(PrimitiveShape shape) => shape switch
        {
            PrimitiveShape.Table => BuildTable(),
            PrimitiveShape.Shelf => BuildShelf(),
            PrimitiveShape.LampBase => BuildLampBase(),
            PrimitiveShape.Crate => BuildCrate(),
            PrimitiveShape.Chair => BuildChair(),
            _ => null,
        };

        static GameObject BuildTable()
        {
            const float width = 1.0f;
            const float depth = 0.6f;
            const float height = 0.75f;
            const float topThickness = 0.08f;
            const float legThickness = 0.06f;

            var root = new GameObject("Table");

            var top = CreatePart(PrimitiveType.Cube, root.transform, "Tabletop");
            top.transform.localPosition = new Vector3(0f, height - topThickness / 2f, 0f);
            top.transform.localScale = new Vector3(width, topThickness, depth);

            var legHeight = height - topThickness;
            var legY = legHeight / 2f;
            var legX = (width - legThickness) / 2f;
            var legZ = (depth - legThickness) / 2f;
            AddLeg(root.transform, new Vector3(legX, legY, legZ), legThickness, legHeight);
            AddLeg(root.transform, new Vector3(-legX, legY, legZ), legThickness, legHeight);
            AddLeg(root.transform, new Vector3(legX, legY, -legZ), legThickness, legHeight);
            AddLeg(root.transform, new Vector3(-legX, legY, -legZ), legThickness, legHeight);

            return root;
        }

        static GameObject BuildShelf()
        {
            const float width = 0.6f;
            const float height = 1.2f;
            const float depth = 0.25f;
            const float boardThickness = 0.03f;
            const int shelfCount = 4; // includes top and bottom boards

            var root = new GameObject("Shelf");

            for (var i = 0; i < shelfCount; i++)
            {
                var t = shelfCount == 1 ? 0f : i / (float)(shelfCount - 1);
                var board = CreatePart(PrimitiveType.Cube, root.transform, $"Board_{i}");
                board.transform.localPosition = new Vector3(0f, t * (height - boardThickness) + boardThickness / 2f, 0f);
                board.transform.localScale = new Vector3(width, boardThickness, depth);
            }

            var sideThickness = 0.03f;
            var sideX = (width - sideThickness) / 2f;
            AddPanel(root.transform, new Vector3(sideX, height / 2f, 0f), new Vector3(sideThickness, height, depth), "SidePanel_R");
            AddPanel(root.transform, new Vector3(-sideX, height / 2f, 0f), new Vector3(sideThickness, height, depth), "SidePanel_L");

            return root;
        }

        static GameObject BuildLampBase()
        {
            const float baseRadius = 0.15f;
            const float baseHeight = 0.05f;
            const float stemRadius = 0.03f;
            const float stemHeight = 0.5f;
            const float bulbRadius = 0.12f;

            var root = new GameObject("LampBase");

            var baseDisc = CreatePart(PrimitiveType.Cylinder, root.transform, "Base");
            baseDisc.transform.localPosition = new Vector3(0f, baseHeight / 2f, 0f);
            // Default cylinder is radius 0.5, height 2 -- scale.y is a multiplier on that height.
            baseDisc.transform.localScale = new Vector3(baseRadius * 2f, baseHeight / 2f, baseRadius * 2f);

            var stem = CreatePart(PrimitiveType.Cylinder, root.transform, "Stem");
            stem.transform.localPosition = new Vector3(0f, baseHeight + stemHeight / 2f, 0f);
            stem.transform.localScale = new Vector3(stemRadius * 2f, stemHeight / 2f, stemRadius * 2f);

            var bulb = CreatePart(PrimitiveType.Sphere, root.transform, "Bulb");
            bulb.transform.localPosition = new Vector3(0f, baseHeight + stemHeight + bulbRadius * 0.6f, 0f);
            bulb.transform.localScale = Vector3.one * (bulbRadius * 2f);

            return root;
        }

        static GameObject BuildCrate()
        {
            const float size = 0.4f;

            var root = new GameObject("Crate");
            var box = CreatePart(PrimitiveType.Cube, root.transform, "CrateBody");
            box.transform.localPosition = new Vector3(0f, size / 2f, 0f);
            box.transform.localScale = new Vector3(size, size, size);

            return root;
        }

        static GameObject BuildChair()
        {
            const float seatWidth = 0.4f;
            const float seatDepth = 0.4f;
            const float seatThickness = 0.05f;
            const float seatHeight = 0.45f;
            const float legThickness = 0.05f;
            const float backHeight = 0.5f;
            const float backThickness = 0.05f;

            var root = new GameObject("Chair");

            var seat = CreatePart(PrimitiveType.Cube, root.transform, "Seat");
            seat.transform.localPosition = new Vector3(0f, seatHeight, 0f);
            seat.transform.localScale = new Vector3(seatWidth, seatThickness, seatDepth);

            var legY = (seatHeight - seatThickness / 2f) / 2f;
            var legX = (seatWidth - legThickness) / 2f;
            var legZ = (seatDepth - legThickness) / 2f;
            var legHeight = seatHeight - seatThickness / 2f;
            AddLeg(root.transform, new Vector3(legX, legY, legZ), legThickness, legHeight);
            AddLeg(root.transform, new Vector3(-legX, legY, legZ), legThickness, legHeight);
            AddLeg(root.transform, new Vector3(legX, legY, -legZ), legThickness, legHeight);
            AddLeg(root.transform, new Vector3(-legX, legY, -legZ), legThickness, legHeight);

            var back = CreatePart(PrimitiveType.Cube, root.transform, "Back");
            back.transform.localPosition = new Vector3(0f, seatHeight + backHeight / 2f, -(seatDepth - backThickness) / 2f);
            back.transform.localScale = new Vector3(seatWidth, backHeight, backThickness);

            return root;
        }

        static void AddLeg(Transform parent, Vector3 localPosition, float thickness, float legHeight)
        {
            var leg = CreatePart(PrimitiveType.Cylinder, parent, "Leg");
            leg.transform.localPosition = localPosition;
            leg.transform.localScale = new Vector3(thickness, legHeight / 2f, thickness);
        }

        static void AddPanel(Transform parent, Vector3 localPosition, Vector3 size, string name)
        {
            var panel = CreatePart(PrimitiveType.Cube, parent, name);
            panel.transform.localPosition = localPosition;
            panel.transform.localScale = size;
        }

        static GameObject CreatePart(PrimitiveType type, Transform parent, string name)
        {
            var part = GameObject.CreatePrimitive(type);
            part.name = name;
            part.transform.SetParent(parent, false);
            return part;
        }
    }
}

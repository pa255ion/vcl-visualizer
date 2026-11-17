// The board the teacher draws on, resizable with `board W,H[,D]`: a port of
// viewer/js/board.js.
//
// W×H is the usable area in board units (x -W/2..W/2, y -H/2..H/2); the slate
// is half a unit larger all round. The board grows sideways and upwards: its
// bottom edge stays at the same height, so the subtitles below never move.
// D > 0 adds a stage in front of the board (toward the learner, Unity -z).
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Vcl
{
    public class Board
    {
        public static readonly Vector3 Default = new Vector3(16, 9, 0);
        static readonly Vector2 Margin = new Vector2(0.5f, 0.4f);

        readonly Transform stage;
        readonly Vector3 home;
        public Vector3 Size { get; private set; } = Default;
        public event Action<Vector3> Changed;

        readonly MeshRenderer slate;
        readonly Stroke edge;
        readonly MeshRenderer floor;
        readonly Stroke floorGrid, rim;

        public float Width => Size.x;
        public float Height => Size.y;
        public float Depth => Size.z;

        /// <summary>`stage` is the board's coordinate system; `center` its centre (metres) at the default size.</summary>
        public Board(Transform stage, Vector3 center)
        {
            this.stage = stage;
            home = center;
            var root = Draw.Child(stage, "board").transform;
            slate = Draw.MeshObject(root, "slate", new Mesh(), Draw.Unlit(Draw.Hex("#121924") * new Color(1, 1, 1, 0.96f), 2900), -10);
            slate.transform.localPosition = new Vector3(0, 0, 0.05f);
            edge = new Stroke(root, Draw.Hex("#2a3647"), 0.05f) { Order = -9 };
            edge.Obj.transform.localPosition = new Vector3(0, 0, 0.04f);
            floor = Draw.MeshObject(root, "floor", Draw.Primitive(PrimitiveType.Quad), Draw.Unlit(Draw.Hex("#16202e") * new Color(1, 1, 1, 0.85f), 2900), -10);
            floor.transform.localRotation = Quaternion.Euler(90, 0, 0);
            floorGrid = new Stroke(root, Draw.Hex("#1d2735"), 0.03f) { Order = -9 };
            floorGrid.Obj.transform.localRotation = Quaternion.Euler(90, 0, 0);
            rim = new Stroke(root, Draw.Hex("#2a3647"), 0.05f) { Order = -9 };
            rim.Obj.transform.localRotation = Quaternion.Euler(90, 0, 0);
            Apply(Default);
        }

        /// <summary>Set the size now (no animation).</summary>
        public void Apply(Vector3 size)
        {
            Size = size;
            float w = size.x, h = size.y, d = size.z;
            float ow = w + 2 * Margin.x, oh = h + 2 * Margin.y;
            var outline = Draw.RoundedRect(ow, oh, 0.35f);
            var old = slate.GetComponent<MeshFilter>().sharedMesh;
            slate.GetComponent<MeshFilter>().sharedMesh = Draw.Fill(outline);
            Draw.Kill(old);
            edge.Set(new List<List<Vector3>> { outline });

            // Grow upwards: keep the bottom edge where the default board has it.
            float lift = (h - Default.y) / 2 * Draw.StageScale;
            stage.localPosition = home + new Vector3(0, lift, 0);

            bool show = d > 0.01f;
            floor.gameObject.SetActive(show);
            floorGrid.Obj.SetActive(show);
            rim.Obj.SetActive(show);
            if (show)
            {
                float y = -oh / 2;
                // Quad rotated 90° about x lies flat; its local y now runs along -z (toward the learner).
                floor.transform.localScale = new Vector3(ow, d, 1);
                floor.transform.localPosition = new Vector3(0, y, -(d / 2 - 0.05f));
                // In the rotated strokes' plane, local (x, y) = board (x, -z).
                var lines = new List<List<Vector3>>();
                for (float x = -ow / 2; x <= ow / 2 + 1e-3f; x += 1) lines.Add(new List<Vector3> { new Vector3(x, 0.05f, 0), new Vector3(x, d - 0.05f, 0) });
                for (float z = 1; z < d; z += 1) lines.Add(new List<Vector3> { new Vector3(-ow / 2, z, 0), new Vector3(ow / 2, z, 0) });
                floorGrid.Obj.transform.localPosition = new Vector3(0, y + 0.01f, 0);
                floorGrid.Set(lines);
                rim.Obj.transform.localPosition = new Vector3(0, y + 0.02f, 0);
                rim.Set(new List<List<Vector3>> { new List<Vector3> { new Vector3(-ow / 2, -0.05f), new Vector3(-ow / 2, d - 0.05f), new Vector3(ow / 2, d - 0.05f), new Vector3(ow / 2, -0.05f) } });
            }
            Changed?.Invoke(size);
        }

        public (Action Begin, Action<float, float> Update, Action Finish) Tween(Vector3 to)
        {
            Vector3 from = Size;
            return (
                () => from = Size,
                (p, raw) => Apply(Vector3.Lerp(from, to, p)),
                () => Apply(to)
            );
        }

        /// <summary>World-space points that should be in view: slate corners and the stage's front corners.</summary>
        public IEnumerable<Vector3> WorldCorners()
        {
            float hw = Size.x / 2 + Margin.x, hh = Size.y / 2 + Margin.y, d = Size.z;
            foreach (var v in new[] { new Vector3(-hw, -hh, 0), new Vector3(hw, hh, 0), new Vector3(-hw, hh, 0), new Vector3(hw, -hh, 0), new Vector3(-hw, -hh, -d), new Vector3(hw, -hh, -d) })
                yield return stage.TransformPoint(v);
        }
    }
}

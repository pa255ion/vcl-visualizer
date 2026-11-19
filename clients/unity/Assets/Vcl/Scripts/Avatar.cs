// The teacher: a small floating robot built from primitives, plus world-space
// subtitles for `say`. A port of viewer/js/avatar.js (front faces the learner,
// which in Unity is -z).
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace Vcl
{
    public class Avatar
    {
        static readonly Color Skin = Draw.Hex("#dfe7f1"), Suit = Draw.Hex("#2b3a52"), Glow = Draw.Hex("#7fe3ff");

        public readonly Transform Root, SubRoot;
        public readonly Voice Voice;
        readonly Transform body, head, mouth, bulb;
        readonly Transform[] eyes, hands;
        readonly Vector3[] handRest;
        readonly LineRenderer beam;
        readonly Transform dot;
        readonly TextMeshPro sub;
        readonly MeshRenderer subPanel;
        readonly float baseY;

        bool thinking;
        Vector3? pointTarget;
        float pointUntil, speakUntil, subUntil, nextBlink = 2, t;
        Color subColor = Draw.Hex("#f5f7fa");

        /// <summary>A VRM avatar standing in for the robot (null: the robot).</summary>
        public VrmTeacher Model { get; private set; }
        readonly List<Renderer> robotParts = new List<Renderer>();
        readonly Transform parent;

        /// <summary>Replace the robot with a VRM avatar (null: the robot again).</summary>
        public void SetModel(VrmTeacher m)
        {
            if (Model != null && Model != m) Model.Dispose();
            Model = m;
            foreach (var r in robotParts) if (r) r.enabled = m == null;
        }

        /// <summary>Where a VRM teacher stands: under the robot, on the floor.</summary>
        public Vector3 FloorSpot => new Vector3(Root.localPosition.x - 0.3f, 0, Root.localPosition.z - 0.15f);

        public Avatar(Transform parent, Vector3 position, Vector3 subtitleAt, float scale = 0.72f)
        {
            this.parent = parent;
            Root = Draw.Child(parent, "teacher").transform;
            Root.localPosition = position;
            Root.localScale = Vector3.one * scale;
            baseY = position.y;

            Material Std(Color c) => Draw.Lit(c, smooth: 0.5f, metal: 0.1f);
            var glow = Draw.Unlit(Glow, 2000, false);
            glow.SetFloat("_ZWrite", 1);
            Transform Prim(Transform par, PrimitiveType t, Material m, Vector3 pos, Vector3 scl)
            {
                var r = Draw.MeshObject(par, t.ToString(), Draw.Primitive(t), m);
                r.transform.localPosition = pos;
                r.transform.localScale = scl;
                return r.transform;
            }

            body = Prim(Root, PrimitiveType.Capsule, Std(Suit), new Vector3(0, 1.08f, 0), new Vector3(0.3f, 0.3f, 0.3f));
            Prim(Root, PrimitiveType.Sphere, glow, new Vector3(0, 1.17f, -0.145f), new Vector3(0.07f, 0.07f, 0.01f));

            head = Draw.Child(Root, "head").transform;
            head.localPosition = new Vector3(0, 1.52f, 0);
            Prim(head, PrimitiveType.Sphere, Std(Skin), Vector3.zero, new Vector3(0.4f, 0.34f, 0.34f));
            var visorMat = Draw.Lit(Draw.Hex("#0d1522"), smooth: 0.8f);
            Prim(head, PrimitiveType.Sphere, visorMat, new Vector3(0, 0.0f, -0.07f), new Vector3(0.34f, 0.24f, 0.22f));
            eyes = new[]
            {
                Prim(head, PrimitiveType.Capsule, glow, new Vector3(-0.055f, 0.025f, -0.175f), new Vector3(0.032f, 0.03f, 0.01f)),
                Prim(head, PrimitiveType.Capsule, glow, new Vector3(0.055f, 0.025f, -0.175f), new Vector3(0.032f, 0.03f, 0.01f)),
            };
            mouth = Prim(head, PrimitiveType.Capsule, glow, new Vector3(0, -0.045f, -0.178f), new Vector3(0.016f, 0.034f, 0.01f));
            mouth.localRotation = Quaternion.Euler(0, 0, 90);
            Prim(head, PrimitiveType.Cylinder, Std(Skin), new Vector3(0, 0.2f, 0), new Vector3(0.012f, 0.045f, 0.012f));
            bulb = Prim(head, PrimitiveType.Sphere, glow, new Vector3(0, 0.255f, 0), Vector3.one * 0.044f);
            Voice = new Voice(head);

            handRest = new[] { new Vector3(-0.27f, 1.0f, -0.05f), new Vector3(0.27f, 1.0f, -0.05f) };
            hands = new[]
            {
                Prim(Root, PrimitiveType.Sphere, Std(Skin), handRest[0], Vector3.one * 0.1f),
                Prim(Root, PrimitiveType.Sphere, Std(Skin), handRest[1], Vector3.one * 0.1f),
            };

            robotParts.AddRange(Root.GetComponentsInChildren<Renderer>(true));

            // Pointer beam + target dot (in the classroom's space).
            beam = Draw.Child(parent, "beam").AddComponent<LineRenderer>();
            beam.useWorldSpace = true;
            beam.sharedMaterial = Draw.Unlit(new Color(Glow.r, Glow.g, Glow.b, 0.7f), 3004);
            beam.widthMultiplier = 0.008f;
            beam.positionCount = 2;
            beam.enabled = false;
            dot = Draw.MeshObject(parent, "dot", Draw.Primitive(PrimitiveType.Sphere), Draw.Unlit(Glow, 3004), 4).transform;
            dot.localScale = Vector3.one * 0.036f;
            dot.gameObject.SetActive(false);

            // Subtitles.
            SubRoot = Draw.Child(parent, "subtitles").transform;
            SubRoot.localPosition = subtitleAt;
            SubRoot.localRotation = Quaternion.Euler(0.25f * Mathf.Rad2Deg, 0, 0);
            subPanel = Draw.MeshObject(SubRoot, "panel", Draw.Primitive(PrimitiveType.Quad), Draw.Unlit(new Color(0.043f, 0.067f, 0.1f, 0.8f), 3005), 5);
            subPanel.transform.localPosition = new Vector3(0, 0, 0.002f);
            sub = Draw.Text(SubRoot, " ", 0.07f, subColor, "center", false);
            sub.renderer.sortingOrder = 6;
            SubRoot.gameObject.SetActive(false);
        }

        /// <summary>Show what the learner asked (or a status line) in the subtitle strip.</summary>
        public void Hear(string text, bool isThinking = true, float secs = 4)
        {
            thinking = isThinking;
            subColor = Draw.Hex("#9fb4cc");
            Subtitle(text, secs);
        }

        /// <summary>Paused: the subtitles, pointing and the mouth wait too.</summary>
        public bool Paused;

        /// <summary>Stop talking now (the step was skipped).</summary>
        public void Silence()
        {
            speakUntil = t;
            subUntil = Mathf.Min(subUntil, t + 0.3f);
        }

        public void Say(string text, float dur, AudioClip clip = null)
        {
            thinking = false;
            if (clip) Voice.Play(clip);
            subColor = Draw.Hex("#f5f7fa");
            speakUntil = t + dur;
            Subtitle(text, dur + 0.8f);
        }

        void Subtitle(string text, float secs)
        {
            subUntil = t + secs;
            var lines = new List<string>();
            var cur = "";
            foreach (var w in (text ?? "").Split(' '))
            {
                if ((cur + " " + w).Trim().Length > 64) { lines.Add(cur); cur = w; }
                else cur = (cur + " " + w).Trim();
            }
            if (cur.Length > 0) lines.Add(cur);
            sub.text = string.Join("\n", lines);
            var size = sub.GetPreferredValues(sub.text);
            subPanel.transform.localScale = new Vector3(size.x + 0.06f, size.y + 0.02f, 1);
            SubRoot.gameObject.SetActive(true);
        }

        /// <summary>Point at a world position (or stop with null).</summary>
        public void Point(Vector3? world, float dur)
        {
            pointTarget = world;
            pointUntil = t + Mathf.Max(3.5f, dur);
        }

        public void Update(float dt, Vector3 viewer)
        {
            if (Paused)
            {
                if (speakUntil > t) speakUntil += dt;
                if (subUntil > t) subUntil += dt;
                if (pointUntil > t) pointUntil += dt;
            }
            t += dt;
            var p = Root.localPosition;
            p.y = baseY + Mathf.Sin(t * 1.6f) * 0.012f;
            Root.localPosition = p;

            if (pointTarget.HasValue && t > pointUntil) pointTarget = null;
            var look = pointTarget ?? (thinking ? head.position + Root.TransformDirection(new Vector3(0.4f, 0.5f, -1)) : viewer);
            // The face is on -z: turn -z toward the target.
            var want = Quaternion.LookRotation(head.position - look, Vector3.up);
            head.rotation = Quaternion.Slerp(head.rotation, want, Mathf.Min(1, dt * 5));
            float bodyYaw = Mathf.LerpAngle(body.localEulerAngles.y, pointTarget.HasValue ? -20 : 0, Mathf.Min(1, dt * 3));
            body.localRotation = Quaternion.Euler(0, bodyYaw, 0);

            // Right hand (the learner's left) reaches toward the target.
            var hand = hands[1];
            var goal = handRest[1] + new Vector3(0, Mathf.Sin(t * 2.1f) * 0.01f, 0);
            if (pointTarget.HasValue)
            {
                var shoulder = Root.TransformPoint(new Vector3(0.15f, 1.25f, 0));
                var dir = (pointTarget.Value - shoulder).normalized;
                goal = Root.InverseTransformPoint(shoulder + dir * 0.32f);
            }
            hand.localPosition = Vector3.Lerp(hand.localPosition, goal, Mathf.Min(1, dt * 6));
            var left = hands[0];
            left.localPosition = handRest[0] + new Vector3(0, Mathf.Sin(t * 2.1f + 1) * 0.01f, 0);

            bool showBeam = pointTarget.HasValue && (Model != null ? Model.Pointing : Vector3.Distance(hand.localPosition, goal) < 0.08f);
            beam.enabled = showBeam;
            dot.gameObject.SetActive(showBeam);
            if (showBeam)
            {
                beam.SetPosition(0, Model != null ? Model.Fingertip : hand.position);
                beam.SetPosition(1, pointTarget.Value);
                dot.position = pointTarget.Value;
                dot.localScale = Vector3.one * 0.036f * (1 + 0.25f * Mathf.Sin(t * 8));
            }

            bool speaking = (t < speakUntil && !Paused) || Voice.Speaking;
            float talk = Voice.Speaking ? Voice.Level()
                : speaking ? 0.5f + 0.5f * Mathf.Abs(Mathf.Sin(t * 13) * Mathf.Sin(t * 4.7f)) : 0;
            mouth.localScale = new Vector3(0.016f * (1 + talk * 3), 0.034f, 0.01f);
            Model?.Update(t, dt, look, pointTarget, Voice.Speaking ? Mathf.Clamp01(talk * 1.6f) : speaking ? talk * 0.6f : 0);
            bulb.localScale = Vector3.one * 0.044f * (1 + (speaking ? 0.3f * Mathf.Abs(Mathf.Sin(t * 6)) : thinking ? 0.45f * (0.5f + 0.5f * Mathf.Sin(t * 5)) : 0));

            if (t > nextBlink) nextBlink = t + 2.5f + Random.value * 3;
            float blink = nextBlink - t < 0.12f ? 0.15f : 1;
            foreach (var e in eyes) e.localScale = new Vector3(0.032f, 0.03f * blink, 0.01f);

            if (SubRoot.gameObject.activeSelf)
            {
                float leftT = subUntil - t;
                float a = Mathf.Clamp01(leftT / 0.5f);
                var c = subColor;
                c.a = a;
                sub.color = c;
                subPanel.sharedMaterial.color = new Color(0.043f, 0.067f, 0.1f, 0.8f * a);
                if (leftT <= 0) SubRoot.gameObject.SetActive(false);
            }
        }
    }
}

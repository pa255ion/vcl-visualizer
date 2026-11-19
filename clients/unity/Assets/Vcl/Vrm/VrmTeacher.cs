// A VRM avatar as the teacher, loaded at runtime (UniVRM: VRM 1.0 and 0.x).
// It stands where the robot floats and does what the robot does: looks at the
// learner (or what it points at), points with its right arm, moves its mouth
// with the voice, blinks, breathes. Posed procedurally through UniVRM's
// control rig, whose bones are normalized (identity in T-pose, facing +z,
// the right arm along -x and the left along +x), so the same rotations fit any
// humanoid.
using System;
using System.Threading.Tasks;
using UniVRM10;
using UnityEngine;

namespace Vcl
{
    public class VrmTeacher : IDisposable
    {
        public readonly Vrm10Instance Vrm;
        readonly Vrm10RuntimeControlRig rig;
        readonly Transform eyeTarget;
        public readonly string Name;
        float point, blinkT = 2, blink;
        /// <summary>The arm is (all but) up at the target: show the beam.</summary>
        public bool Pointing => point > 0.95f;
        Quaternion neck = Quaternion.identity, head = Quaternion.identity;

        VrmTeacher(Vrm10Instance vrm, string name)
        {
            Vrm = vrm;
            Name = name;
            rig = vrm.Runtime.ControlRig;
            vrm.UpdateType = Vrm10Instance.UpdateTypes.None; // we pose, then Process(), each frame
            eyeTarget = new GameObject("teacher gaze").transform;
            eyeTarget.SetParent(vrm.transform, false);
            vrm.LookAtTargetType = VRM10ObjectLookAt.LookAtTargetTypes.SpecifiedTransform;
            vrm.LookAtTarget = eyeTarget;
        }

        /// <summary>Load a .vrm (bytes) and stand it at `parent`-local `at`, facing the learner (-z).</summary>
        public static async Task<VrmTeacher> Load(byte[] bytes, string name, Transform parent, Vector3 at)
        {
            // (UniVRM waits frame by frame when playing, and runs straight through in the editor.)
            var vrm = await Vrm10.LoadBytesAsync(bytes, canLoadVrm0X: true, showMeshes: true);
            if (!vrm) throw new Exception("not a VRM avatar");
            var t = vrm.transform;
            t.SetParent(parent, false);
            t.localPosition = at;
            t.localRotation = Quaternion.Euler(0, 180, 0);
            // People come in all sizes; keep the teacher between 1.4 and 1.9 m.
            var head = vrm.TryGetBoneTransform(HumanBodyBones.Head, out var h) ? h.position.y - t.position.y : 1.5f;
            float height = head + 0.12f;
            if (height < 1.4f || height > 1.9f) t.localScale = Vector3.one * (1.65f / Mathf.Max(0.1f, height));
            foreach (var r in vrm.GetComponentsInChildren<Renderer>()) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return new VrmTeacher(vrm, name);
        }

        public Transform Bone(HumanBodyBones b) => Vrm.TryGetBoneTransform(b, out var t) ? t : null;

        /// <summary>Where a pointing beam starts: the right index finger's tip, else the hand.</summary>
        public Vector3 Fingertip
        {
            get
            {
                var tip = Bone(HumanBodyBones.RightIndexDistal) ?? Bone(HumanBodyBones.RightHand);
                return tip ? tip.position : Vrm.transform.position + Vector3.up;
            }
        }

        public Vector3 HeadPosition => Bone(HumanBodyBones.Head)?.position ?? Vrm.transform.position + Vector3.up * 1.5f;

        void Rot(HumanBodyBones b, Quaternion q)
        {
            var t = rig.GetBoneTransform(b);
            if (t) t.localRotation = q;
        }

        /// <summary>Pose for this frame. look: what to face (world); target: what to point at,
        /// if anything; talk: mouth opening 0..1; paused: hold still.</summary>
        public void Update(float t, float dt, Vector3 look, Vector3? target, float talk)
        {
            var root = Vrm.transform;
            // Pointing blends in and out over a third of a second.
            point = Mathf.MoveTowards(point, target.HasValue ? 1 : 0, dt * 3);

            // Breathing and a slow sway.
            Rot(HumanBodyBones.Spine, Quaternion.Euler(Mathf.Sin(t * 0.7f) * 1.2f, Mathf.Sin(t * 0.45f) * 2f + point * 12, Mathf.Sin(t * 0.5f) * 1.0f));
            Rot(HumanBodyBones.Chest, Quaternion.Euler(Mathf.Sin(t * 1.6f) * 0.8f, 0, 0));

            // Arms down by the sides, elbows a little bent forward (+z).
            Rot(HumanBodyBones.LeftUpperArm, Quaternion.Euler(0, 0, -72) * Quaternion.Euler(0, -8, 0));
            Rot(HumanBodyBones.LeftLowerArm, Quaternion.Euler(0, -18, 0));
            var rUp = Quaternion.Euler(0, 0, 72) * Quaternion.Euler(0, 8, 0);
            var rLow = Quaternion.Euler(0, 18, 0);
            if (point > 0 && target.HasValue)
            {
                // Straight arm from the shoulder toward the target, in the chest's frame.
                var chest = rig.GetBoneTransform(HumanBodyBones.Chest) ?? rig.GetBoneTransform(HumanBodyBones.Spine);
                var shoulder = rig.GetBoneTransform(HumanBodyBones.RightUpperArm);
                if (chest && shoulder)
                {
                    var dir = chest.InverseTransformDirection(target.Value - shoulder.position).normalized;
                    var aim = Quaternion.FromToRotation(Vector3.left, dir);
                    float k = Mathf.SmoothStep(0, 1, point);
                    rUp = Quaternion.Slerp(rUp, aim, k);
                    rLow = Quaternion.Slerp(rLow, Quaternion.identity, k);
                }
            }
            Rot(HumanBodyBones.RightUpperArm, rUp);
            Rot(HumanBodyBones.RightLowerArm, rLow);

            // Head (and neck) turn toward what it looks at, within comfortable limits.
            var headBone = Bone(HumanBodyBones.Head);
            if (headBone)
            {
                var d = root.InverseTransformDirection(look - headBone.position);
                float yaw = Mathf.Clamp(Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg, -65, 65);
                float pitch = Mathf.Clamp(-Mathf.Atan2(d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg, -30, 30);
                var want = Quaternion.Euler(pitch, yaw, 0);
                float s = Mathf.Min(1, dt * 5);
                neck = Quaternion.Slerp(neck, Quaternion.Slerp(Quaternion.identity, want, 0.4f), s);
                head = Quaternion.Slerp(head, Quaternion.Slerp(Quaternion.identity, want, 0.6f), s);
                Rot(HumanBodyBones.Neck, neck);
                Rot(HumanBodyBones.Head, head);
            }
            eyeTarget.position = look;

            // Face: the mouth follows the voice; a blink every few seconds.
            blinkT -= dt;
            if (blinkT <= 0) { blinkT = 2.5f + UnityEngine.Random.value * 3; blink = 1; }
            blink = Mathf.MoveTowards(blink, 0, dt * 7);
            var ex = Vrm.Runtime.Expression;
            ex.SetWeight(ExpressionKey.Aa, Mathf.Clamp01(talk));
            ex.SetWeight(ExpressionKey.Blink, 1 - Mathf.Abs(blink * 2 - 1));

            Vrm.Runtime.Process();
        }

        public void SetVisible(bool on)
        {
            foreach (var r in Vrm.GetComponentsInChildren<Renderer>(true)) r.enabled = on;
        }

        public void Dispose()
        {
            if (!Vrm) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(Vrm.gameObject); else UnityEngine.Object.DestroyImmediate(Vrm.gameObject);
        }
    }
}

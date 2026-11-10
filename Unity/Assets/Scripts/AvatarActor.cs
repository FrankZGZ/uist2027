using System;
using System.Collections;
using UnityEngine;

namespace MysterySort
{
    // Mixamo idle/walk through the Animator for the body. After the Animator runs, two things are
    // layered on top: the head turns toward LookPoint, and the right arm is solved with a two-bone IK,
    // because a canned clip cannot look at or reach an arbitrary slot.
    public sealed class AvatarActor : MonoBehaviour
    {
        public Transform Carried;
        public Transform Viewer;      // a carried note is turned toward this so its text stays readable
        public Vector3? LookPoint;    // world point the head follows; null = look straight ahead
        public float WalkSpeed = 1.1f;

        Animator animator;
        Transform upper, fore, hand, head, neck;
        Vector3 reachTarget;
        float reachWeight, carryWeight, speed, targetSpeed, lookYaw, lookPitch;

        public bool Ready => upper != null && fore != null && hand != null;
        public float ReachError { get; private set; }
        public float HeadYaw => lookYaw;

        public void Initialize(RuntimeAnimatorController controller)
        {
            animator = GetComponentInChildren<Animator>();
            if (animator == null || !animator.isHuman) { Debug.LogError("Avatar needs a humanoid Animator."); return; }
            animator.runtimeAnimatorController = controller;
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            upper = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
            fore = animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
            hand = animator.GetBoneTransform(HumanBodyBones.RightHand);
            head = animator.GetBoneTransform(HumanBodyBones.Head);
            neck = animator.GetBoneTransform(HumanBodyBones.Neck);
        }

        void Update()
        {
            if (animator == null) return;
            speed = Mathf.MoveTowards(speed, targetSpeed, Time.deltaTime * 4f);
            animator.SetFloat("Speed", speed);
        }

        void Turn(Vector3 direction)
        {
            direction.y = 0;
            if (direction.sqrMagnitude < 0.0001f) return;
            transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(direction), 300f * Time.deltaTime);
        }

        public void Stop() { targetSpeed = 0; }

        // One frame of walking toward a point and then turning to face a direction. Returns true once
        // there. Calling it every frame instead of WalkTo lets the caller change its mind at any moment.
        public bool Step(Vector3 point, Vector3 face)
        {
            point.y = transform.position.y; face.y = 0;
            Vector3 delta = point - transform.position;
            if (delta.sqrMagnitude > 0.0004f)
            {
                bool close = delta.magnitude <= 0.15f;
                if (!close) Turn(delta);
                if (close || Vector3.Angle(transform.forward, delta) < 50f)
                {
                    targetSpeed = 1;
                    transform.position = Vector3.MoveTowards(transform.position, point, WalkSpeed * Time.deltaTime);
                }
                else targetSpeed = 0;
                return false;
            }
            targetSpeed = 0;
            if (face.sqrMagnitude > 0.0001f && Vector3.Angle(transform.forward, face) > 2f) { Turn(face); return false; }
            return true;
        }

        public IEnumerator WalkTo(Vector3 point, Vector3 face)
        {
            while (!Step(point, face)) yield return null;
        }

        // Arm goes out to the target, contact() fires at full extension, arm comes back.
        public IEnumerator Reach(Vector3 target, Action contact)
        {
            reachTarget = target;
            for (float t = 0; t < 1; t += Time.deltaTime / 0.5f) { reachWeight = Mathf.SmoothStep(0, 1, t); yield return null; }
            reachWeight = 1;
            yield return null; // one frame at full extension so ReachError reflects the contact pose
            contact?.Invoke();
            yield return new WaitForSeconds(0.15f);
            for (float t = 0; t < 1; t += Time.deltaTime / 0.5f) { reachWeight = Mathf.SmoothStep(1, 0, t); yield return null; }
            reachWeight = 0;
        }

        void LateUpdate()
        {
            if (!Ready) return;
            LookWithHead();
            carryWeight = Mathf.MoveTowards(carryWeight, Carried != null ? 1 : 0, Time.deltaTime * 3f);
            float weight = Mathf.Max(carryWeight, reachWeight);
            if (weight > 0.001f)
            {
                // Carry pose: note held in front of the chest.
                Vector3 carryPoint = transform.position + Vector3.up * 1.12f + transform.forward * 0.34f + transform.right * 0.16f;
                Vector3 target = carryWeight > 0.001f ? Vector3.Lerp(carryPoint, reachTarget, reachWeight) : reachTarget;
                SolveArm(target, weight);
                if (reachWeight > 0.999f) ReachError = Vector3.Distance(hand.position, reachTarget);
            }
            if (Carried != null)
            {
                Carried.position = hand.position + (hand.position - fore.position).normalized * 0.09f + Vector3.up * 0.10f;
                Vector3 look = Viewer != null ? Carried.position - Viewer.position : -transform.forward; look.y = 0;
                Carried.rotation = Quaternion.LookRotation(look);
            }
        }

        // Yaw/pitch toward LookPoint in body space, clamped to what a neck can do and eased over time.
        // The turn is split between neck and head. Assumes the animated head faces the body's forward.
        void LookWithHead()
        {
            if (head == null) return;
            float yaw = 0, pitch = 0;
            if (LookPoint.HasValue)
            {
                Vector3 local = transform.InverseTransformDirection((LookPoint.Value - head.position).normalized);
                yaw = Mathf.Clamp(Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg, -75f, 75f);
                pitch = Mathf.Clamp(Mathf.Asin(Mathf.Clamp(local.y, -1f, 1f)) * Mathf.Rad2Deg, -50f, 35f);
            }
            float ease = 1f - Mathf.Exp(-7f * Time.deltaTime);
            lookYaw = Mathf.Lerp(lookYaw, yaw, ease); lookPitch = Mathf.Lerp(lookPitch, pitch, ease);
            Quaternion delta = transform.rotation * Quaternion.Euler(-lookPitch, lookYaw, 0) * Quaternion.Inverse(transform.rotation);
            if (neck != null) neck.rotation = Quaternion.Slerp(Quaternion.identity, delta, 0.35f) * neck.rotation;
            head.rotation = Quaternion.Slerp(Quaternion.identity, delta, neck != null ? 0.65f : 1f) * head.rotation;
        }

        void SolveArm(Vector3 target, float weight)
        {
            Quaternion animatedUpper = upper.rotation, animatedFore = fore.localRotation;
            Vector3 a = upper.position, b = fore.position, c = hand.position;
            float lab = (b - a).magnitude, lcb = (c - b).magnitude;
            float lat = Mathf.Clamp((target - a).magnitude, 0.05f, lab + lcb - 0.005f);
            float current = Vector3.Angle(a - b, c - b);
            float wanted = Mathf.Acos(Mathf.Clamp((lab * lab + lcb * lcb - lat * lat) / (2 * lab * lcb), -1f, 1f)) * Mathf.Rad2Deg;
            Vector3 axis = Vector3.Cross(a - b, c - b);
            if (axis.sqrMagnitude < 1e-8f) axis = Vector3.Cross(c - a, transform.up);
            // Rotating (c-b) about cross(a-b, c-b) by a positive angle opens the elbow.
            fore.rotation = Quaternion.AngleAxis(wanted - current, axis.normalized) * fore.rotation;
            upper.rotation = Quaternion.FromToRotation(hand.position - a, target - a) * upper.rotation;
            Quaternion solvedUpper = upper.rotation, solvedFore = fore.localRotation;
            upper.rotation = Quaternion.Slerp(animatedUpper, solvedUpper, weight);
            fore.localRotation = Quaternion.Slerp(animatedFore, solvedFore, weight);
        }
    }
}

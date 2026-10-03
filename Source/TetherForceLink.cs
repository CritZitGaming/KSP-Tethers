using System;
using UnityEngine;

namespace KSPTethers
{
    /// <summary>
    /// How a tether holds on: through a PhysX joint, or by adding forces to the two parts.
    /// </summary>
    public enum TetherLinkMode
    {
        /// <summary>Forces when a mod that integrates vessels itself (Principia) is installed, a joint otherwise.</summary>
        Auto,
        /// <summary>A ConfigurableJoint with a soft spherical limit: PhysX solves it with everything else.</summary>
        Joint,
        /// <summary>Part.AddForceAtPosition on both ends, which trajectory mods see and keep.</summary>
        Forces
    }

    /// <summary>
    /// Pulls the two ends together once the tether runs out of slack, by adding a force to each part rather
    /// than by creating a joint.
    ///
    /// Mods that integrate vessel motion themselves - Principia above all - compute each vessel's trajectory
    /// and then write the result back over whatever PhysX produced, so a joint a mod adds between two
    /// separate vessels is simply discarded and the tether never pulls. Forces handed to
    /// <see cref="Part.AddForceAtPosition"/> are read out of the part's own accumulator, so they survive and
    /// the tether works.
    ///
    /// The pull is a soft constraint rather than a plain spring: the impulse is worked out from how fast the
    /// ends are separating, how far past its length the tether already is, and the mass behind each end.
    /// For a slack spring it reduces exactly to a spring-damper of stiffness k and damping c; for a stiff one
    /// it stops short of the impulse that would send the ends flying, so it cannot blow up however the
    /// stiffness, the masses or the time step are set.
    /// </summary>
    internal sealed class TetherForceLink
    {
        /// <summary>Pull applied on the last tick, in kN (what the joint's currentForce would have read).</summary>
        public float Tension { get; private set; }

        public void Reset()
        {
            Tension = 0f;
        }

        /// <summary>
        /// Applies this tick's pull. <paramref name="aw"/> and <paramref name="bw"/> are the two ends in world
        /// space, <paramref name="limit"/> the length the tether is holding to, and k and c the spring
        /// stiffness (kN/m) and damping (kN s/m) chosen for the masses involved. Returns false when the
        /// tether is slack and nothing was applied.
        /// </summary>
        public bool Apply(Part pa, Rigidbody ra, Vector3 aw, Part pb, Rigidbody rb, Vector3 bw,
            float limit, float k, float c, float dt, float maxForce)
        {
            Tension = 0f;
            if (pa == null || pb == null || ra == null || rb == null || ra == rb || dt <= 0f)
                return false;

            Vector3 d = aw - bw;
            float dist = d.magnitude;
            if (dist < 1e-4f)
                return false;
            float extension = dist - limit;
            if (extension <= 0f)
                return false;   // slack: a rope pulls, it never pushes
            Vector3 n = d / dist;

            // How much mass actually resists a pull along n at these two points, rotation included.
            Vector3 rA = aw - ra.worldCenterOfMass;
            Vector3 rB = bw - rb.worldCenterOfMass;
            float invMass = 1f / Mathf.Max(1e-4f, ra.mass) + 1f / Mathf.Max(1e-4f, rb.mass)
                            + AngularTerm(ra, rA, n) + AngularTerm(rb, rB, n);
            if (invMass <= 1e-9f)
                return false;

            float separating = Vector3.Dot(ra.GetPointVelocity(aw) - rb.GetPointVelocity(bw), n);
            float force = TetherTension.Solve(extension, separating, invMass, k, c, dt);
            if (force <= 0f)
                return false;
            if (maxForce > 0f && force > maxForce)
                force = maxForce;
            Tension = force;

            Vector3 pull = n * force;
            pa.AddForceAtPosition(-pull, aw);
            pb.AddForceAtPosition(pull, bw);
            return true;
        }

        /// <summary>(r x n) . I^-1 . (r x n): how much the body's spin absorbs a pull at an off-centre point.</summary>
        private static float AngularTerm(Rigidbody body, Vector3 r, Vector3 n)
        {
            Vector3 rn = Vector3.Cross(r, n);
            Vector3 i = InverseInertiaTimes(body, rn);
            return Vector3.Dot(Vector3.Cross(i, r), n);
        }

        private static Vector3 InverseInertiaTimes(Rigidbody body, Vector3 w)
        {
            Quaternion q = body.rotation * body.inertiaTensorRotation;
            Vector3 local = Quaternion.Inverse(q) * w;
            Vector3 it = body.inertiaTensor;
            local.x = it.x > 1e-9f ? local.x / it.x : 0f;
            local.y = it.y > 1e-9f ? local.y / it.y : 0f;
            local.z = it.z > 1e-9f ? local.z / it.z : 0f;
            return q * local;
        }
    }
}

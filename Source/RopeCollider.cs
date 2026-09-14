using System.Collections.Generic;
using UnityEngine;

namespace KSPTethers
{
    /// <summary>
    /// Finds contact planes between rope nodes and nearby parts, kerbals and terrain. Candidate colliders are
    /// gathered once per frame around the rope; each node is tested against their (cheap) cached bounds
    /// before any real physics query. The rope solver then enforces the planes inside its iterations.
    /// </summary>
    internal sealed class RopeCollider : IRopeCollisionSolver
    {
        // Default (parts), Local Scenery (terrain/statics), EVA, PhysicalObjects, TerrainColliders.
        public const int Mask = (1 << 0) | (1 << 15) | (1 << 17) | (1 << 19) | (1 << 28);
        public const int TerrainBody = -2;
        private const int TerrainMask = (1 << 15) | (1 << 28);
        private const float TerrainProbe = 0.6f;
        private const float Margin = 0.08f;  // contacts are found this far ahead of the surface
        private const int MaxBuffer = 4096;

        /// <summary>Which end's own body a collider belongs to (rope near that end ignores it).</summary>
        private enum Owner : byte { None, EndA, EndB }

        private static Collider[] overlapBuffer = new Collider[256];

        private Collider[] convex = new Collider[256];
        private Bounds[] convexBounds = new Bounds[256];
        // Bounds as plain floats so the per-node rejection test is inlined arithmetic, not a method call.
        private float[] boxMin = new float[256 * 3];
        private float[] boxMax = new float[256 * 3];
        private Owner[] convexOwner = new Owner[256];
        private int convexCount;
        private bool hasTerrain;
        private Bounds terrainBounds;

        private readonly HashSet<Collider> collidersA = new HashSet<Collider>();
        private readonly HashSet<Collider> collidersB = new HashSet<Collider>();

        private Vector3 origin;
        private Vector3 up = Vector3.up;
        private float radius = 0.02f;
        private int excludeA;
        private int excludeB;

        /// <summary>Records the colliders of the bodies at each end of the rope.</summary>
        public void SetParts(Part a, Part b)
        {
            Collect(a, collidersA);
            Collect(b, collidersB);
        }

        private static void Collect(Part p, HashSet<Collider> set)
        {
            set.Clear();
            if (p == null)
                return;
            if (p.vessel != null && p.vessel.isEVA)
            {
                // Kerbals: body capsules, helmet and ragdoll colliders all live under the part.
                foreach (Collider c in p.GetComponentsInChildren<Collider>(true))
                    set.Add(c);
                return;
            }
            // Parts: only this part's own colliders (children in the hierarchy may be other parts).
            Collider[] cols = p.GetPartColliders();
            if (cols == null)
                return;
            foreach (Collider c in cols)
                if (c != null) set.Add(c);
        }

        /// <summary>Gathers the colliders that could touch a rope inside <paramref name="worldBox"/> this frame.</summary>
        /// <param name="ignoreA">Metres of rope next to end A that ignore end A's own body.</param>
        /// <param name="ignoreB">Metres of rope next to end B that ignore end B's own body.</param>
        public void Prepare(Vector3 frameOrigin, Bounds worldBox, Vector3 worldUp, float ropeRadius, float rest,
            float ignoreA, float ignoreB)
        {
            origin = frameOrigin;
            up = worldUp.sqrMagnitude > 1e-6f ? worldUp.normalized : Vector3.up;
            radius = ropeRadius;
            // Otherwise the fittings would constantly fight the bodies they are bolted to.
            excludeA = Mathf.CeilToInt(ignoreA / Mathf.Max(rest, 0.01f)) + 1;
            excludeB = Mathf.CeilToInt(ignoreB / Mathf.Max(rest, 0.01f)) + 1;

            convexCount = 0;
            hasTerrain = false;
            // Never silently drop colliders: a truncated list is what lets a rope slip through big ships.
            int n;
            while (true)
            {
                n = Physics.OverlapBoxNonAlloc(worldBox.center, worldBox.extents, overlapBuffer, Quaternion.identity, Mask,
                    QueryTriggerInteraction.Ignore);
                if (n < overlapBuffer.Length || overlapBuffer.Length >= MaxBuffer)
                    break;
                overlapBuffer = new Collider[overlapBuffer.Length * 2];
            }
            EnsureCapacity(n);

            float expand = 2f * (radius + Margin);
            for (int k = 0; k < n; k++)
            {
                Collider c = overlapBuffer[k];
                overlapBuffer[k] = null;
                if (c == null || !c.enabled || c.isTrigger)
                    continue;

                bool concave;
                if (c is MeshCollider mc)
                    concave = !mc.convex;
                else if (c is BoxCollider || c is SphereCollider || c is CapsuleCollider)
                    concave = false;
                else
                    continue; // wheel colliders and anything exotic

                Bounds bb = c.bounds;
                bb.Expand(expand);
                if (concave)
                {
                    if (hasTerrain)
                        terrainBounds.Encapsulate(bb);
                    else
                        terrainBounds = bb;
                    hasTerrain = true;
                    continue;
                }

                convex[convexCount] = c;
                convexBounds[convexCount] = bb;
                Vector3 lo = bb.min, hi = bb.max;
                int o = convexCount * 3;
                boxMin[o] = lo.x; boxMin[o + 1] = lo.y; boxMin[o + 2] = lo.z;
                boxMax[o] = hi.x; boxMax[o + 1] = hi.y; boxMax[o + 2] = hi.z;
                convexOwner[convexCount] = collidersA.Contains(c) ? Owner.EndA
                    : collidersB.Contains(c) ? Owner.EndB : Owner.None;
                convexCount++;
            }
        }

        private void EnsureCapacity(int n)
        {
            if (convex.Length >= n)
                return;
            int cap = Mathf.NextPowerOfTwo(n);
            convex = new Collider[cap];
            convexBounds = new Bounds[cap];
            boxMin = new float[cap * 3];
            boxMax = new float[cap * 3];
            convexOwner = new Owner[cap];
        }

        /// <summary>The part a contact's body id refers to (valid until <see cref="Clear"/>).</summary>
        public Part PartOf(int body)
        {
            if (body < 0 || body >= convexCount)
                return null;
            Collider c = convex[body];
            return c != null ? FlightGlobals.GetPartUpwardsCached(c.gameObject) : null;
        }

        public void Clear()
        {
            for (int i = 0; i < convexCount; i++)
                convex[i] = null;
            convexCount = 0;
            hasTerrain = false;
        }

        public bool Probe(int index, int last, Vector3 pos, Vector3 prev, Vector3 hintA, Vector3 hintB, bool finalSubstep,
            out RopeContact contact)
        {
            contact = default(RopeContact);
            Vector3 wp = pos + origin;
            float reach = radius + Margin;
            float best = float.MaxValue; // signed distance of the node to the allowed surface (negative = inside)
            Vector3 bestPoint = Vector3.zero, bestNormal = Vector3.up;
            int bestBody = -1;
            RaycastHit hit = default(RaycastHit);

            for (int c = 0; c < convexCount; c++)
            {
                Owner owner = convexOwner[c];
                if (owner == Owner.EndA && index < excludeA)
                    continue;
                if (owner == Owner.EndB && index > last - excludeB)
                    continue;
                int o = c * 3;
                if (wp.x < boxMin[o] || wp.x > boxMax[o] || wp.y < boxMin[o + 1] || wp.y > boxMax[o + 1] ||
                    wp.z < boxMin[o + 2] || wp.z > boxMax[o + 2])
                    continue;
                Collider col = convex[c];
                if (col == null || !col.enabled)
                    continue;

                Vector3 cp = col.ClosestPoint(wp);
                Vector3 d = wp - cp;
                float d2 = d.sqrMagnitude;
                if (d2 > 1e-10f)
                {
                    if (d2 >= reach * reach)
                        continue;
                    float dl = Mathf.Sqrt(d2);
                    if (dl - radius < best)
                    {
                        best = dl - radius;
                        bestNormal = d / dl;
                        bestPoint = cp + bestNormal * radius;
                        bestBody = c;
                    }
                    continue;
                }

                // Inside the collider: find the way out along the node's own motion, then from either neighbour
                // (the rope around it is usually still outside), and only as a last resort away from the centre.
                if (ExitAlong(col, prev + origin, wp, out hit) || ExitAlong(col, hintA + origin, wp, out hit) ||
                    ExitAlong(col, hintB + origin, wp, out hit) || ExitRadial(col, convexBounds[c], wp, out hit))
                {
                    float sd = Vector3.Dot(wp - hit.point, hit.normal) - radius;
                    if (sd < best)
                    {
                        best = sd;
                        bestNormal = hit.normal;
                        bestPoint = hit.point + hit.normal * radius;
                        bestBody = c;
                    }
                }
            }

            // Terrain and statics (non-convex meshes): once per frame, with a swept test and a downward probe.
            if (finalSubstep && hasTerrain && terrainBounds.Contains(wp))
            {
                Vector3 wq = prev + origin;
                Vector3 mv = wp - wq;
                float ml = mv.magnitude;
                bool got = ml > 1e-4f && Physics.Raycast(wq, mv / ml, out hit, ml + reach, TerrainMask, QueryTriggerInteraction.Ignore);
                if (!got)
                    got = Physics.Raycast(wp + up * TerrainProbe, -up, out hit, TerrainProbe + reach, TerrainMask, QueryTriggerInteraction.Ignore);
                if (got)
                {
                    float sd = Vector3.Dot(wp - hit.point, hit.normal) - radius;
                    if (sd < best && sd < Margin)
                    {
                        best = sd;
                        bestNormal = hit.normal;
                        bestPoint = hit.point + hit.normal * radius;
                        bestBody = TerrainBody;
                    }
                }
            }

            if (best == float.MaxValue)
                return false;
            contact.Point = bestPoint - origin;
            contact.Normal = bestNormal;
            contact.Body = bestBody;
            return true;
        }

        /// <summary>Entry point on <paramref name="col"/> along the line from an outside point to a node inside it.</summary>
        private bool ExitAlong(Collider col, Vector3 from, Vector3 to, out RaycastHit hit)
        {
            hit = default(RaycastHit);
            Vector3 d = to - from;
            float len = d.magnitude;
            if (len < 1e-5f)
                return false;
            if ((col.ClosestPoint(from) - from).sqrMagnitude < 1e-10f)
                return false; // that point is inside too
            return col.Raycast(new Ray(from, d / len), out hit, len + radius);
        }

        private static bool ExitRadial(Collider col, Bounds bb, Vector3 wp, out RaycastHit hit)
        {
            Vector3 dir = wp - bb.center;
            if (dir.sqrMagnitude < 1e-8f)
                dir = Vector3.up;
            dir.Normalize();
            float far = bb.extents.magnitude * 2f + 1f;
            return col.Raycast(new Ray(wp + dir * far, -dir), out hit, far);
        }
    }
}

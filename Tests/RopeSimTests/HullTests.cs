using System;
using UnityEngine;

namespace KSPTethers.Tests
{
    /// <summary>
    /// Rope versus a ship hull: an axis-aligned box standing in for a capsule, resolved the way the in-game
    /// RopeCollider resolves convex part colliders (closest point outside, entry ray or push-from-centre inside).
    /// </summary>
    internal static class HullTests
    {
        public static void RunAll(Action<string, Action> run, Action<bool, string> check)
        {
            run("rope wraps around the hull instead of cutting through it", () => WrapsAroundHull(check));
            run("over-stretched rope stays outside the hull", () => OverStretched(check));
            run("thin beam: rope pulled hard across it at 30 fps", () => ThinBeam(check));
            run("rope piling up on the ground doesn't pass through itself", () => SelfCollision(check));
        }

        /// <summary>Closest distance between two segments (used to measure self-intersection).</summary>
        private static float SegmentDistance(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2)
        {
            float best = float.MaxValue;
            // Dense sampling is plenty accurate for a test and obviously correct.
            for (int i = 0; i <= 8; i++)
            {
                Vector3 a = Vector3.Lerp(p1, q1, i / 8f);
                for (int j = 0; j <= 8; j++)
                    best = Math.Min(best, (a - Vector3.Lerp(p2, q2, j / 8f)).magnitude);
            }
            return best;
        }

        /// <summary>
        /// A free-floating rope folded into a hairpin: the outgoing strand lies still along x, the returning
        /// strand starts 10 cm above it and is fired down through it at 2 m/s. Returns the smallest height of
        /// the returning strand above the outgoing one at the end (negative = it went through).
        /// </summary>
        private static float FireStrandThrough(float selfThickness, out float closest)
        {
            var rope = new RopeSimulation(120, 0.22f, 8f);
            rope.Initialize(Vector3.zero, new Vector3(0f, 0f, 0.1f), 4f, Vector3.right);
            int last = rope.Count - 1, fold = last / 2;
            float span = 1.8f;
            for (int i = 0; i <= last; i++)
            {
                bool outgoing = i <= fold;
                float x = outgoing ? span * i / fold : span * (last - i) / (last - fold);
                rope.Pos[i] = new Vector3(x, 0f, outgoing ? 0f : 0.1f);
                // Returning strand moves toward -z at 2 m/s (one 60 Hz frame of motion stored in Prev).
                rope.Prev[i] = rope.Pos[i] + (outgoing ? Vector3.zero : new Vector3(0f, 0f, 2f / 60f));
            }
            RopeStepParams p = Program.Defaults(Vector3.zero);
            p.PinA = p.PinB = false;
            p.Bend = 0f;
            p.MinBendRadius = 0f;
            p.IdleFlow = 0f;
            p.Damping = 0f;
            p.SelfThickness = selfThickness;
            closest = float.MaxValue;
            for (int f = 0; f < 30; f++)
                rope.Step(1f / 60f, ref p, null);
            float lowest = float.MaxValue;
            for (int j = fold + 3; j < last - 2; j++)
            {
                int i = last - j; // node of the outgoing strand at the same x
                lowest = Math.Min(lowest, rope.Pos[j].z - rope.Pos[i].z);
                closest = Math.Min(closest, SegmentDistance(rope.Pos[i], rope.Pos[i + 1], rope.Pos[j - 1], rope.Pos[j]));
            }
            return lowest;
        }

        private static void SelfCollision(Action<bool, string> check)
        {
            float c0, c1;
            float without = FireStrandThrough(0f, out c0);
            float with = FireStrandThrough(0.044f, out c1);
            check(without < 0f, "without self-collision the strand tunnels straight through (ends " + (without * 100f).ToString("F1") + " cm below)");
            check(with > 0.03f, "with it, the strands collide and stay apart (" + (with * 100f).ToString("F1") + " cm above, closest " + (c1 * 1000f).ToString("F0") + " mm, thickness 44 mm)");
        }

        /// <summary>Box hull the size of a small capsule. Rope radius is folded into the half extents.</summary>
        internal sealed class BoxHull
        {
            public Vector3 Half = new Vector3(1.25f, 1.25f, 1.25f);
            public float Radius = 0.022f;

            public bool Inside(Vector3 p, float margin)
            {
                Vector3 h = Half + Vector3.one * (Radius - margin);
                return Math.Abs(p.x) < h.x && Math.Abs(p.y) < h.y && Math.Abs(p.z) < h.z;
            }

            public Vector3 ClosestPoint(Vector3 p)
            {
                return new Vector3(Mathf.Clamp(p.x, -Half.x, Half.x), Mathf.Clamp(p.y, -Half.y, Half.y), Mathf.Clamp(p.z, -Half.z, Half.z));
            }

            /// <summary>Ray against the box (slab method), from outside only, like Collider.Raycast.</summary>
            public bool Raycast(Vector3 o, Vector3 d, float maxDist, out Vector3 point, out Vector3 normal)
            {
                point = normal = Vector3.zero;
                float tmin = 0f, tmax = maxDist;
                int axis = -1;
                float sign = 0;
                for (int k = 0; k < 3; k++)
                {
                    float ok = o[k], dk = d[k], h = Half[k];
                    if (Math.Abs(dk) < 1e-9f)
                    {
                        if (ok < -h || ok > h) return false;
                        continue;
                    }
                    float t1 = (-h - ok) / dk, t2 = (h - ok) / dk;
                    float s = -1f;
                    if (t1 > t2) { float t = t1; t1 = t2; t2 = t; s = 1f; }
                    if (t1 > tmin) { tmin = t1; axis = k; sign = s; }
                    if (t2 < tmax) tmax = t2;
                    if (tmin > tmax) return false;
                }
                if (axis < 0) return false; // origin inside: Unity reports no hit
                point = o + d * tmin;
                normal = Vector3.zero;
                normal[axis] = sign;
                return true;
            }

            /// <summary>Deepest penetration of any rope segment into the hull (0 when clear).</summary>
            public float WorstSegmentDepth(RopeSimulation r)
            {
                float worst = 0f;
                for (int i = 1; i < r.Count; i++)
                {
                    for (int k = 1; k < 4; k++)
                    {
                        Vector3 q = Vector3.Lerp(r.Pos[i - 1], r.Pos[i], k / 4f);
                        Vector3 cp = ClosestPoint(q);
                        if (cp == q)
                        {
                            float depth = Math.Min(Half.x - Math.Abs(q.x), Math.Min(Half.y - Math.Abs(q.y), Half.z - Math.Abs(q.z)));
                            worst = Math.Max(worst, depth);
                        }
                    }
                }
                return worst;
            }
        }

        private static void Orbit(Action<bool, string> check, float ropeLength, float orbitRadius, string what)
        {
            var hull = new BoxHull();
            var solver = new HullSolver(hull);
            var rope = new RopeSimulation(120, 0.22f, 2f);
            Vector3 hatch = new Vector3(hull.Half.x + 0.004f, 0f, 0f);
            Vector3 kerbal = new Vector3(orbitRadius, 0.3f, 0f);
            rope.Initialize(kerbal, hatch, ropeLength, Vector3.up);
            RopeStepParams p = Program.Defaults(Vector3.zero);
            p.IdleFlow = 0f;
            float worst = 0f;
            int badFrames = 0, frames = 0;
            float dt = 1f / 60f;
            // The kerbal jetpacks half way around the hull (over the top) to the far side and holds there.
            for (int f = 0; f < 60 * 14; f++)
            {
                float t = Math.Min(1f, f / (60f * 8f));
                double ang = Math.PI * t;
                kerbal = new Vector3(orbitRadius * (float)Math.Cos(ang), orbitRadius * (float)Math.Sin(ang) + 0.3f, 0.2f);
                p.A = kerbal;
                p.B = hatch;
                p.DirA = (hatch - kerbal).normalized;
                p.DirB = Vector3.right;
                rope.Step(dt, ref p, solver);
                float depth = hull.WorstSegmentDepth(rope);
                worst = Math.Max(worst, depth);
                frames++;
                if (depth > 0.05f)
                    badFrames++;
            }
            check(worst < 0.05f, what + ": deepest cut into the hull " + (worst * 100f).ToString("F1") + " cm, " + badFrames + "/" + frames + " frames cut deeper than 5 cm");
        }

        private static void WrapsAroundHull(Action<bool, string> check)
        {
            // Around-the-hull path is ~7.2 m; 9 m of rope can always wrap without stretching.
            Orbit(check, 9f, 3.2f, "9 m rope, kerbal circles to the far side");
        }

        private static void OverStretched(Action<bool, string> check)
        {
            // Straight-line distance stays under 6 m (what a straight-line tether allows) but the way around is
            // longer: this is the "pulled through the ship" case. The rope must stretch around, not cut through.
            Orbit(check, 6f, 3.2f, "6 m rope forced round a 7 m path");
        }

        private static void ThinBeam(Action<bool, string> check)
        {
            // A 0.7 m thick, 5 m long beam (a truss or tank seen edge-on). The tether is clipped on top; the
            // kerbal drops underneath, where a straight-line tether still allows it but the rope would have to
            // go round the end of the beam, far longer than the rope. At 30 fps the solver takes big steps.
            var hull = new BoxHull { Half = new Vector3(2.5f, 0.35f, 0.35f) };
            var solver = new HullSolver(hull);
            var rope = new RopeSimulation(120, 0.22f, 5f);
            Vector3 anchor = new Vector3(0.4f, hull.Half.y + 0.004f, 0f);
            Vector3 kerbal = new Vector3(0.6f, 2.2f, 0.1f);
            rope.Initialize(kerbal, anchor, 4f, Vector3.right);
            RopeStepParams p = Program.Defaults(Vector3.zero);
            p.IdleFlow = 0f;
            float worst = 0f;
            int bad = 0, frames = 0;
            float dt = 1f / 30f;
            for (int f = 0; f < 30 * 10; f++)
            {
                float t = Math.Min(1f, f / (30f * 3f));
                // Swing out past the side of the beam and underneath it.
                double ang = Math.PI * t;
                kerbal = new Vector3(0.6f, 2.2f * (float)Math.Cos(ang), 1.4f * (float)Math.Sin(ang) + 0.1f);
                p.A = kerbal;
                p.B = anchor;
                p.DirA = (anchor - kerbal).normalized;
                p.DirB = Vector3.up;
                rope.Step(dt, ref p, solver);
                float depth = hull.WorstSegmentDepth(rope);
                worst = Math.Max(worst, depth);
                frames++;
                if (depth > 0.05f)
                    bad++;
            }
            check(worst < 0.05f, "deepest cut into the beam " + (worst * 100f).ToString("F1") + " cm, " + bad + "/" + frames + " frames cut deeper than 5 cm");
        }

        /// <summary>Collision solver equivalent to RopeCollider for one convex hull.</summary>
        internal sealed class HullSolver : IRopeCollisionSolver
        {
            private readonly BoxHull hull;
            private const float Margin = 0.1f;

            public HullSolver(BoxHull hull)
            {
                this.hull = hull;
            }

            public bool Probe(int index, int last, Vector3 pos, Vector3 prev, Vector3 hintA, Vector3 hintB, bool finalSubstep, out RopeContact contact)
            {
                contact = default(RopeContact);
                float r = hull.Radius;
                Vector3 cp = hull.ClosestPoint(pos);
                Vector3 d = pos - cp;
                float dl = d.magnitude;
                if (dl > 1e-5f)
                {
                    if (dl >= r + Margin)
                        return false;
                    Vector3 n = d / dl;
                    contact = new RopeContact { Point = cp + n * r, Normal = n, Body = 1 };
                    return true;
                }
                Vector3 hp, hn;
                if (Exit(prev, pos, out hp, out hn) || Exit(hintA, pos, out hp, out hn) || Exit(hintB, pos, out hp, out hn))
                {
                    contact = new RopeContact { Point = hp + hn * r, Normal = hn, Body = 1 };
                    return true;
                }
                Vector3 dir = pos.sqrMagnitude > 1e-8f ? pos.normalized : Vector3.up;
                if (hull.Raycast(pos + dir * 10f, -dir, 10f, out hp, out hn))
                {
                    contact = new RopeContact { Point = hp + hn * r, Normal = hn, Body = 1 };
                    return true;
                }
                return false;
            }

            private bool Exit(Vector3 from, Vector3 to, out Vector3 point, out Vector3 normal)
            {
                point = normal = Vector3.zero;
                if (hull.ClosestPoint(from) == from)
                    return false; // starts inside
                Vector3 d = to - from;
                float l = d.magnitude;
                return l > 1e-5f && hull.Raycast(from, d / l, l + hull.Radius, out point, out normal);
            }
        }
    }
}

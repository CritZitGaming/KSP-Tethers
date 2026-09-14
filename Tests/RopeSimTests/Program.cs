using System;
using System.Diagnostics;
using UnityEngine;

namespace KSPTethers.Tests
{
    internal static class Program
    {
        private static int failures;
        private static int checks;

        private static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "render")
            {
                Snapshots.Render(args.Length > 1 ? args[1] : "snapshots");
                return 0;
            }

            Run("zero-g slack rope follows a moving kerbal", ZeroGMovingKerbal);
            Run("taut rope goes straight", TautRopeIsStraight);
            Run("rope sags under gravity and settles", GravitySagSettles);
            Run("paying out / reeling in never moves existing rope", SpoolIsContinuous);
            Run("layout regimes meet continuously", LayoutContinuity);
            Run("released rope retracts with a free end", FreeEndRetract);
            Run("rope drapes over an obstacle without penetrating", DrapesOverSphere);
            Run("rope lies on the ground with friction", LiesOnGround);
            Run("rope streams downwind in atmosphere", StreamsInWind);
            Run("endpoint teleport does not explode", EndpointTeleport);
            Run("tube mesh is well formed and faces outward", TubeMeshWellFormed);
            FeatureTests.RunAll(Run, Check);
            HullTests.RunAll(Run, Check);
            Run("performance", Performance);

            Console.WriteLine();
            Console.WriteLine(failures == 0
                ? "ALL PASSED (" + checks + " checks)"
                : failures + " FAILED of " + checks + " checks");
            return failures == 0 ? 0 : 1;
        }

        private static void Run(string name, Action test)
        {
            Console.WriteLine("== " + name);
            try
            {
                test();
            }
            catch (Exception e)
            {
                failures++;
                Console.WriteLine("   EXCEPTION: " + e);
            }
        }

        private static void Check(bool ok, string what)
        {
            checks++;
            if (!ok)
                failures++;
            Console.WriteLine("   [" + (ok ? "ok" : "FAIL") + "] " + what);
        }

        // Mirrors the defaults in GameData/KSPTethers/Settings.cfg.
        internal static RopeStepParams Defaults(Vector3 gravity)
        {
            return new RopeStepParams
            {
                PinA = true,
                PinB = true,
                Gravity = gravity,
                Damping = 0.12f,
                Bend = 0.05f,
                MinBendRadius = 0.45f,
                EndStiffness = 0.45f,
                IdleFlow = 0.03f,
                Friction = 0.45f,
                SelfThickness = 0.044f,
                Iterations = 12,
                SubstepRate = 90f
            };
        }

        private static RopeStepParams Params(Vector3 a, Vector3 b, Vector3 gravity)
        {
            RopeStepParams p = Defaults(gravity);
            p.A = a;
            p.B = b;
            p.DirA = (b - a).sqrMagnitude > 1e-6f ? (b - a).normalized : Vector3.forward;
            p.DirB = (a - b).sqrMagnitude > 1e-6f ? (a - b).normalized : Vector3.back;
            return p;
        }

        /// <summary>Largest turn (degrees) between consecutive segments, ignoring the fittings at each end.</summary>
        internal static float MaxTurnDegrees(RopeSimulation r, out float meanDegrees)
        {
            float worst = 0f, sum = 0f;
            int n = 0;
            for (int i = 3; i < r.Count - 3; i++)
            {
                Vector3 u = r.Pos[i] - r.Pos[i - 1], v = r.Pos[i + 1] - r.Pos[i];
                if (u.sqrMagnitude < 1e-10f || v.sqrMagnitude < 1e-10f)
                    continue;
                float ang = Vector3.Angle(u, v);
                worst = Math.Max(worst, ang);
                sum += ang;
                n++;
            }
            meanDegrees = n > 0 ? sum / n : 0f;
            return worst;
        }

        private static RopeSimulation NewRope(float seed)
        {
            return new RopeSimulation(120, 0.22f, seed);
        }

        private static bool AllFinite(RopeSimulation r)
        {
            for (int i = 0; i < r.Count; i++)
            {
                Vector3 p = r.Pos[i];
                if (float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z) ||
                    float.IsInfinity(p.x) || float.IsInfinity(p.y) || float.IsInfinity(p.z))
                    return false;
            }
            return true;
        }

        private static float MaxSegmentError(RopeSimulation r)
        {
            float worst = 0f;
            int last = r.Count - 1;
            for (int i = 1; i < last; i++) // the spool segment is checked separately
                worst = Math.Max(worst, Math.Abs((r.Pos[i] - r.Pos[i - 1]).magnitude - r.Rest) / r.Rest);
            return worst;
        }

        private static void ZeroGMovingKerbal()
        {
            var rope = NewRope(3.3f);
            Vector3 anchor = Vector3.zero;
            Vector3 kerbal = new Vector3(0f, 0f, 2f);
            rope.Initialize(kerbal, anchor, 10f, Vector3.up);
            Check(Math.Abs(rope.PolylineLength() - 10f) < 0.05f, "initial layout matches length (" + rope.PolylineLength().ToString("F3") + " m)");

            float maxFromAnchor = 0f;
            float dt = 1f / 60f;
            for (int f = 0; f < 60 * 20; f++)
            {
                float t = f * dt;
                kerbal = new Vector3(3f * (float)Math.Sin(t * 0.7), 1.5f * (float)Math.Sin(t * 1.3), 4f + 2f * (float)Math.Cos(t * 0.5));
                var p = Params(kerbal, anchor, Vector3.zero);
                rope.Step(dt, ref p, null);
                for (int i = 0; i < rope.Count; i++)
                    maxFromAnchor = Math.Max(maxFromAnchor, rope.Pos[i].magnitude);
            }
            Check(AllFinite(rope), "no NaN/Inf after 20 s");
            Check(Math.Abs(rope.PolylineLength() - rope.Length) / rope.Length < 0.03f,
                "rope stays within 3% of its length (" + rope.PolylineLength().ToString("F2") + " / " + rope.Length.ToString("F2") + " m)");
            Check(MaxSegmentError(rope) < 0.08f, "worst segment stretch " + (MaxSegmentError(rope) * 100f).ToString("F1") + "% < 8%");
            Check(maxFromAnchor <= rope.Length * 1.02f, "no node ever further from the anchor than the rope length (" + maxFromAnchor.ToString("F2") + " m)");
            Check((rope.Pos[0] - kerbal).magnitude < 1e-4f && rope.Pos[rope.Count - 1].magnitude < 1e-4f, "both ends stay pinned");
            float mean;
            float worst = MaxTurnDegrees(rope, out mean);
            // 0.22 m segments on a 0.45 m minimum bend radius allow ~28 degrees per node.
            Check(worst < 34f, "slack forms smooth curves, no kinks (worst turn " + worst.ToString("F1") + " deg, mean " + mean.ToString("F1") + " deg)");
        }

        private static void TautRopeIsStraight()
        {
            var rope = NewRope(1f);
            Vector3 a = new Vector3(0f, 0f, 12f), b = Vector3.zero;
            rope.Initialize(a, b, 10f, Vector3.up);
            var p = Params(a, b, Vector3.zero);
            p.IdleFlow = 0f;
            for (int f = 0; f < 120; f++)
                rope.Step(1f / 60f, ref p, null);
            float dev = 0f;
            for (int i = 0; i < rope.Count; i++)
            {
                Vector3 q = rope.Pos[i];
                dev = Math.Max(dev, new Vector2(q.x, q.y).magnitude);
            }
            Check(dev < 0.01f, "max sideways deviation " + (dev * 1000f).ToString("F1") + " mm < 10 mm");
            Check(Math.Abs(rope.PolylineLength() - 12f) < 0.05f, "stretched to the span (" + rope.PolylineLength().ToString("F3") + " m)");
        }

        private static float KineticEnergy(RopeSimulation r)
        {
            float e = 0f;
            for (int i = 1; i < r.Count - 1; i++)
                e += (r.Pos[i] - r.Prev[i]).sqrMagnitude;
            return e;
        }

        private static void GravitySagSettles()
        {
            var rope = NewRope(7f);
            Vector3 a = new Vector3(-1f, 2f, 0f), b = new Vector3(1f, 2f, 0f);
            rope.Initialize(a, b, 4f, Vector3.down);
            var p = Params(a, b, new Vector3(0f, -9.81f, 0f));
            p.IdleFlow = 0f;
            p.Damping = 0.5f;
            float early = 0f;
            for (int f = 0; f < 60 * 15; f++)
            {
                rope.Step(1f / 60f, ref p, null);
                if (f == 30)
                    early = KineticEnergy(rope);
            }
            float lowest = float.MaxValue;
            for (int i = 0; i < rope.Count; i++)
                lowest = Math.Min(lowest, rope.Pos[i].y);
            float sag = 2f - lowest;
            // A 4 m rope over a 2 m span hangs roughly 1.5 m below its ends.
            Check(sag > 1.2f && sag < 2.0f, "sags " + sag.ToString("F2") + " m below its ends (expected 1.2-2.0 m)");
            Check(KineticEnergy(rope) < early * 0.05f + 1e-8f, "motion settles (energy " + KineticEnergy(rope).ToString("E2") + " vs " + early.ToString("E2") + ")");
            Check(AllFinite(rope), "no NaN/Inf");
        }

        private static void SpoolIsContinuous()
        {
            var rope = NewRope(2f);
            Vector3 a = new Vector3(0f, 0f, 1f), b = Vector3.zero;
            rope.Initialize(a, b, 1.5f, Vector3.up);
            var p = Params(a, b, Vector3.zero);
            float dt = 1f / 60f;
            int startCount = rope.Count, maxCount = 0;
            float worstMove = 0f;
            var before = new Vector3[rope.MaxNodes];
            for (int f = 0; f < 60 * 12; f++)
            {
                float t = f * dt;
                float len = t < 4f ? 1.5f + t * 4f : t < 8f ? 17.5f - (t - 4f) * 3.5f : 3.5f;
                a = new Vector3(0f, 0f, Math.Min(len * 0.8f, 1f + t));
                p.A = a;
                p.DirA = -a.normalized; // kerbal fitting points back toward the anchor
                p.DirB = a.normalized;  // anchor fitting points out toward the kerbal

                int oldCount = rope.Count;
                Array.Copy(rope.Pos, before, oldCount);
                rope.SetLength(len);
                // Everything except the two nodes next to the anchor must stay exactly where it was.
                int keep = Math.Min(oldCount, rope.Count) - 2;
                for (int i = 0; i < keep; i++)
                    worstMove = Math.Max(worstMove, (rope.Pos[i] - before[i]).magnitude);

                rope.Step(dt, ref p, null);
                maxCount = Math.Max(maxCount, rope.Count);
            }
            Check(maxCount > startCount && rope.Count < maxCount, "node count grew (" + startCount + " -> " + maxCount + ") and shrank again (" + rope.Count + ")");
            Check(worstMove < 1e-6f, "length changes never displace existing rope (worst " + worstMove.ToString("E1") + " m)");
            Check(AllFinite(rope), "no NaN/Inf");
            Check(Math.Abs(rope.PolylineLength() - 3.5f) < 0.1f, "final length close to 3.5 m (" + rope.PolylineLength().ToString("F2") + ")");
        }

        private static void LayoutContinuity()
        {
            var rope = NewRope(0f);
            float worstJump = 0f;
            float prevTotal = -1f;
            int prevSegs = -1;
            bool countsOk = true;
            for (float len = 0.3f; len < 60f; len += 0.0137f)
            {
                float regular, spool;
                int segs = rope.SegmentsFor(len, out regular, out spool);
                float total = (segs - 1) * regular + spool;
                if (Math.Abs(total - len) > 1e-3f)
                    countsOk = false;
                if (prevSegs >= 0 && Math.Abs(segs - prevSegs) > 1)
                    countsOk = false;
                if (prevTotal >= 0f)
                    worstJump = Math.Max(worstJump, Math.Abs(total - prevTotal) - 0.0137f);
                prevTotal = total;
                prevSegs = segs;
            }
            Check(countsOk, "segments always sum to the length and change by at most one");
            Check(worstJump < 1e-3f, "no discontinuity across regimes");
        }

        private static void FreeEndRetract()
        {
            var rope = NewRope(5f);
            Vector3 a = Vector3.zero, b = new Vector3(0f, 0f, 6f);
            rope.Initialize(a, b, 9f, Vector3.up);
            var p = Params(a, b, Vector3.zero);
            p.PinB = false;
            float remaining = 9f;
            float dt = 1f / 60f;
            while (remaining > 0.05f)
            {
                remaining -= 7f * dt;
                rope.SetLength(Math.Max(0.05f, remaining));
                rope.Step(dt, ref p, null);
            }
            Check(AllFinite(rope), "no NaN/Inf while retracting");
            Check(rope.PolylineLength() < 0.5f, "rope pulled in to the kerbal (" + rope.PolylineLength().ToString("F2") + " m left)");
        }

        private sealed class SphereSolver : IRopeCollisionSolver
        {
            public Vector3 Center;
            public float Radius;
            public int Contacts;

            public bool Probe(int index, int last, Vector3 pos, Vector3 prev, Vector3 hintA, Vector3 hintB, bool finalSubstep, out RopeContact contact)
            {
                contact = default(RopeContact);
                Vector3 d = pos - Center;
                float m = d.magnitude;
                if (m >= Radius + 0.08f)
                    return false;
                Vector3 n = m > 1e-6f ? d / m : Vector3.up;
                contact = new RopeContact { Point = Center + n * Radius, Normal = n, Body = 1 };
                if (m < Radius + 0.005f)
                    Contacts++;
                return true;
            }
        }

        internal sealed class GroundSolver : IRopeCollisionSolver
        {
            public float Height;
            public int Contacts;

            public bool Probe(int index, int last, Vector3 pos, Vector3 prev, Vector3 hintA, Vector3 hintB, bool finalSubstep, out RopeContact contact)
            {
                contact = default(RopeContact);
                if (pos.y >= Height + 0.022f + 0.08f)
                    return false;
                contact = new RopeContact { Point = new Vector3(pos.x, Height + 0.022f, pos.z), Normal = Vector3.up, Body = 2 };
                if (pos.y < Height + 0.022f * 1.05f)
                    Contacts++;
                return true;
            }
        }

        private static void DrapesOverSphere()
        {
            var rope = NewRope(9f);
            Vector3 a = new Vector3(-2.5f, 1.2f, 0f), b = new Vector3(2.5f, 1.2f, 0f);
            rope.Initialize(a, b, 8f, Vector3.down);
            var solver = new SphereSolver { Center = Vector3.zero, Radius = 1f };
            var p = Params(a, b, new Vector3(0f, -9.81f, 0f));
            p.IdleFlow = 0f;
            float minDist = float.MaxValue;
            for (int f = 0; f < 60 * 10; f++)
            {
                rope.Step(1f / 60f, ref p, solver);
                for (int i = 0; i < rope.Count; i++)
                    minDist = Math.Min(minDist, (rope.Pos[i] - solver.Center).magnitude);
            }
            Check(solver.Contacts > 0, "rope touched the obstacle (" + solver.Contacts + " contacts)");
            Check(minDist > 0.97f, "no node ever penetrates the sphere (closest " + minDist.ToString("F3") + " m, radius 1)");
            Check(AllFinite(rope), "no NaN/Inf");
        }

        private static void LiesOnGround()
        {
            var rope = NewRope(4f);
            // Kerbal standing 3 m from a lander hatch 1.5 m up, with 10 m of rope out.
            Vector3 a = new Vector3(0f, 1.0f, 3f), b = new Vector3(0f, 1.5f, 0f);
            rope.Initialize(a, b, 10f, Vector3.down);
            var ground = new GroundSolver { Height = 0f };
            var p = Params(a, b, new Vector3(0f, -9.81f, 0f));
            p.IdleFlow = 0f;
            p.Damping = 0.5f;
            float lowest = float.MaxValue;
            for (int f = 0; f < 60 * 12; f++)
            {
                rope.Step(1f / 60f, ref p, ground);
                for (int i = 0; i < rope.Count; i++)
                    lowest = Math.Min(lowest, rope.Pos[i].y);
            }
            int resting = 0;
            for (int i = 0; i < rope.Count; i++)
                if (rope.Pos[i].y < 0.03f) resting++;
            Check(lowest > 0.02f, "never sinks into the ground (lowest " + lowest.ToString("F3") + " m)");
            Check(resting > rope.Count / 3, resting + " of " + rope.Count + " nodes rest on the ground");
            Check(KineticEnergy(rope) < 1e-6f, "comes to rest (energy " + KineticEnergy(rope).ToString("E2") + ")");
            int gripping = rope.StuckCount;
            Check(gripping > rope.Count / 4, gripping + " nodes held by static friction");

            // Now the kerbal walks off: the rope must be dragged along rather than stretching.
            float worstStretch = 0f;
            for (int f = 0; f < 60 * 4; f++)
            {
                p.A = new Vector3(0f, 1.0f, 3f + 6f * Math.Min(1f, f / 180f));
                rope.Step(1f / 60f, ref p, ground);
                worstStretch = Math.Max(worstStretch, rope.PolylineLength() / Math.Max(rope.Length, (p.A - p.B).magnitude) - 1f);
            }
            Check(worstStretch < 0.03f, "dragged without over-stretching (worst " + (worstStretch * 100f).ToString("F1") + "%)");
            Check(rope.StuckCount < gripping, "grips let go as the rope is pulled (" + gripping + " -> " + rope.StuckCount + ")");
            Check(AllFinite(rope), "no NaN/Inf");
        }

        private static void StreamsInWind()
        {
            var rope = NewRope(6f);
            Vector3 a = new Vector3(0f, -0.5f, 0f), b = Vector3.zero;
            rope.Initialize(a, b, 5f, Vector3.down);
            var p = Params(a, b, new Vector3(0f, -9.81f, 0f));
            p.AirVelocity = new Vector3(15f, 0f, 0f);
            p.AirDrag = 4.8f; // ~Kerbin sea level with the default airDrag = 4
            p.IdleFlow = 0f;
            for (int f = 0; f < 60 * 8; f++)
                rope.Step(1f / 60f, ref p, null);
            float meanX = 0f;
            for (int i = 0; i < rope.Count; i++)
                meanX += rope.Pos[i].x;
            meanX /= rope.Count;
            Check(meanX > 0.8f, "rope blown downwind (mean offset " + meanX.ToString("F2") + " m)");
            Check(AllFinite(rope), "no NaN/Inf");
        }

        private static void EndpointTeleport()
        {
            var rope = NewRope(4f);
            Vector3 a = new Vector3(0f, 0f, 3f), b = Vector3.zero;
            rope.Initialize(a, b, 6f, Vector3.up);
            var p = Params(a, b, Vector3.zero);
            for (int f = 0; f < 30; f++)
                rope.Step(1f / 60f, ref p, null);
            p.A = new Vector3(40f, -25f, 60f); // e.g. a vessel switch placing the kerbal far away for a frame
            rope.Step(1f / 60f, ref p, null);
            p.A = new Vector3(0f, 0f, 3f);
            for (int f = 0; f < 120; f++)
                rope.Step(1f / 60f, ref p, null);
            float maxDist = 0f;
            for (int i = 0; i < rope.Count; i++)
                maxDist = Math.Max(maxDist, rope.Pos[i].magnitude);
            Check(AllFinite(rope), "no NaN/Inf after a teleport");
            Check(maxDist < 8f, "rope recovered near its anchor (furthest node " + maxDist.ToString("F2") + " m)");
        }

        private static void TubeMeshWellFormed()
        {
            // A helix makes every frame/tangent case show up.
            int n = 40;
            var nodes = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)(n - 1);
                nodes[i] = new Vector3((float)Math.Cos(t * 12f) * 0.6f, (float)Math.Sin(t * 12f) * 0.6f, t * 5f);
            }
            var builder = new TubeMeshBuilder();
            var tp = new TubeBuildParams
            {
                Radius = 0.022f,
                Sides = 10,
                Subdivisions = 3,
                VPerMeter = 7f,
                DirA = (nodes[1] - nodes[0]).normalized,
                DirB = (nodes[n - 2] - nodes[n - 1]).normalized,
                RefUpA = Vector3.up,
                FittingA = true,
                FittingB = true,
                BasePlateB = true
            };
            builder.Build(nodes, n, ref tp);
            int rings = (n - 1) * 3 + 1;
            Check(builder.RopeVertexCount == rings * 11, "rope vertex count " + builder.RopeVertexCount + " == rings * (sides + 1)");
            Check(builder.VertexCount < 65000, "fits 16-bit indices (" + builder.VertexCount + " vertices)");

            Check(TrianglesFaceOutward(builder, builder.RopeTriangles, builder.RopeTriangleCount, out string ropeMsg), "rope triangles: " + ropeMsg);
            Check(TrianglesFaceOutward(builder, builder.FittingTriangles, builder.FittingTriangleCount, out string fitMsg), "fitting triangles: " + fitMsg);

            float worstRadius = 0f, worstOrtho = 0f;
            for (int i = 0; i < builder.RopeVertexCount; i++)
            {
                worstRadius = Math.Max(worstRadius, Math.Abs(builder.Normals[i].magnitude - 1f));
                Vector4 t4 = builder.Tangents[i];
                worstOrtho = Math.Max(worstOrtho, Math.Abs(Vector3.Dot(new Vector3(t4.x, t4.y, t4.z), builder.Normals[i])));
            }
            Check(worstRadius < 1e-3f, "normals are unit length");
            Check(worstOrtho < 1e-3f, "tangents are perpendicular to normals");

            // Rebuilding with the same topology must not touch the triangle lists.
            int version = builder.TopologyVersion;
            nodes[5] += new Vector3(0.1f, 0f, 0f);
            builder.Build(nodes, n, ref tp);
            Check(builder.TopologyVersion == version, "same topology reuses triangles");
            builder.Build(nodes, n - 3, ref tp);
            Check(builder.TopologyVersion != version, "fewer nodes rebuilds triangles");
            Check(MaxIndex(builder) < builder.VertexCount, "all indices in range after a rebuild");
            tp.Sides = 6;
            tp.Subdivisions = 1;
            builder.Build(nodes, n, ref tp);
            Check(MaxIndex(builder) < builder.VertexCount && TrianglesFaceOutward(builder, builder.RopeTriangles, builder.RopeTriangleCount, out ropeMsg),
                "low-detail LOD is valid too (" + ropeMsg + ")");
        }

        private static int MaxIndex(TubeMeshBuilder b)
        {
            int maxIndex = 0;
            for (int i = 0; i < b.RopeTriangleCount; i++) maxIndex = Math.Max(maxIndex, b.RopeTriangles[i]);
            for (int i = 0; i < b.FittingTriangleCount; i++) maxIndex = Math.Max(maxIndex, b.FittingTriangles[i]);
            return maxIndex;
        }

        private static bool TrianglesFaceOutward(TubeMeshBuilder b, int[] tris, int count, out string msg)
        {
            int bad = 0, degenerate = 0;
            for (int i = 0; i < count; i += 3)
            {
                Vector3 v0 = b.Vertices[tris[i]], v1 = b.Vertices[tris[i + 1]], v2 = b.Vertices[tris[i + 2]];
                // Unity treats clockwise triangles as front faces; with its left-handed axes the face normal is cross(e1, e2).
                Vector3 face = Vector3.Cross(v1 - v0, v2 - v0);
                if (face.sqrMagnitude < 1e-16f)
                {
                    degenerate++;
                    continue;
                }
                Vector3 avg = b.Normals[tris[i]] + b.Normals[tris[i + 1]] + b.Normals[tris[i + 2]];
                if (Vector3.Dot(face, avg) <= 0f)
                    bad++;
            }
            msg = (count / 3) + " tris, " + bad + " facing inward, " + degenerate + " degenerate";
            return bad == 0 && count > 0;
        }

        private static void Performance()
        {
            foreach (int iterations in new[] { 12, 18 })
            {
                var rope = NewRope(1f);
                Vector3 a = new Vector3(0f, 0f, 5f), b = Vector3.zero;
                rope.Initialize(a, b, 26f, Vector3.up); // hits the 120-node cap
                var p = Params(a, b, Vector3.zero);
                p.Iterations = iterations;
                var builder = new TubeMeshBuilder();
                var tp = new TubeBuildParams { Radius = 0.022f, Sides = 10, Subdivisions = 3, VPerMeter = 7f, DirA = Vector3.forward, DirB = Vector3.back, RefUpA = Vector3.up, FittingA = true, FittingB = true, BasePlateB = true };
                for (int f = 0; f < 120; f++) { rope.Step(1f / 60f, ref p, null); builder.Build(rope.Pos, rope.Count, ref tp); }

                int frames = 600;
                var sw = Stopwatch.StartNew();
                for (int f = 0; f < frames; f++)
                {
                    p.A = new Vector3((float)Math.Sin(f * 0.02), 0f, 5f);
                    rope.Step(1f / 60f, ref p, null);
                }
                double simMs = sw.Elapsed.TotalMilliseconds / frames;
                sw.Restart();
                for (int f = 0; f < frames; f++)
                    builder.Build(rope.Pos, rope.Count, ref tp);
                double meshMs = sw.Elapsed.TotalMilliseconds / frames;
                Console.WriteLine("   " + iterations + " iterations, " + rope.Count + " nodes, " + builder.VertexCount + " vertices: sim " +
                                  simMs.ToString("F3") + " ms + mesh " + meshMs.ToString("F3") + " ms per frame");
                if (iterations == 12)
                    Check(simMs + meshMs < 0.6, "default settings under 0.6 ms per frame on .NET (Mono in KSP is slower)");
            }

            // Worst case for self-collision: 26 m of rope bunched into a tight coil, so almost every segment
            // overlaps many others.
            {
                var rope = NewRope(3f);
                rope.Initialize(Vector3.zero, new Vector3(0f, 0f, 0.3f), 26f, Vector3.up);
                int last = rope.Count - 1;
                for (int i = 0; i <= last; i++)
                {
                    double ang = i * 0.9;
                    rope.Pos[i] = rope.Prev[i] = new Vector3(0.18f * (float)Math.Cos(ang), 0.18f * (float)Math.Sin(ang), 0.3f * i / last);
                }
                RopeStepParams p = Defaults(Vector3.zero);
                p.A = rope.Pos[0];
                p.B = rope.Pos[last];
                p.DirA = Vector3.back;
                p.DirB = Vector3.forward;
                for (int f = 0; f < 10; f++) rope.Step(1f / 60f, ref p, null);
                var sw = Stopwatch.StartNew();
                int frames = 200;
                for (int f = 0; f < frames; f++) rope.Step(1f / 60f, ref p, null);
                double ms = sw.Elapsed.TotalMilliseconds / frames;
                Console.WriteLine("   coiled 120-node rope with self-collision: " + ms.ToString("F3") + " ms per frame");
                Check(ms < 1.5, "worst-case coil stays under 1.5 ms per frame");
            }
        }
    }
}

using System;
using UnityEngine;

namespace KSPTethers
{
    /// <summary>
    /// A contact plane in the rope's local frame: the node must stay on the side of <see cref="Normal"/>, with
    /// <c>dot(pos - Point, Normal) &gt;= 0</c>. The plane is already offset by the rope's radius.
    /// </summary>
    internal struct RopeContact
    {
        public Vector3 Point;
        public Vector3 Normal;
        /// <summary>Solver-defined id of what was touched (used to find where the rope wraps a vessel).</summary>
        public int Body;
    }

    /// <summary>Finds rope-vs-world contacts. Positions are in the rope's local frame.</summary>
    internal interface IRopeCollisionSolver
    {
        /// <summary>
        /// Returns the plane of the surface nearest a node (if it is close), or the way out if the node has
        /// ended up inside something.
        /// </summary>
        /// <param name="index">Node index (0 = end A, last = end B).</param>
        /// <param name="last">Index of the last node.</param>
        /// <param name="pos">Predicted node position.</param>
        /// <param name="prev">Node position at the previous substep (usually outside everything).</param>
        /// <param name="hintA">Neighbouring node toward end A (a good guess at the way out).</param>
        /// <param name="hintB">Neighbouring node toward end B.</param>
        /// <param name="finalSubstep">True on the last substep of a frame (expensive checks only run then).</param>
        bool Probe(int index, int last, Vector3 pos, Vector3 prev, Vector3 hintA, Vector3 hintB, bool finalSubstep,
            out RopeContact contact);
    }

    internal struct RopeStepParams
    {
        public Vector3 A;            // kerbal end
        public Vector3 B;            // anchor end
        public Vector3 DirA;         // unit direction the rope leaves the kerbal fitting
        public Vector3 DirB;         // unit direction the rope leaves the anchor fitting
        public bool PinA;
        public bool PinB;
        public Vector3 Gravity;      // effective acceleration in the rope frame (m/s^2)
        public Vector3 AirVelocity;  // velocity of the surrounding air in the rope frame (m/s)
        public float AirDrag;        // 1/s
        public float Damping;        // 1/s
        public float Bend;           // 0..1 per solver iteration: how strongly the hose springs back straight
        public float MinBendRadius;  // m: the hose can never kink tighter than this
        public float EndStiffness;   // 0..1 per substep
        public float IdleFlow;       // m/s^2
        public float Friction;       // Coulomb coefficient
        public float SelfThickness;  // m: closest two parts of the rope may come (0 = no self-collision)
        public int Iterations;
        public float SubstepRate;    // Hz
    }

    /// <summary>
    /// Position-based (Verlet) rope. Nodes are stored relative to a moving frame origin chosen by the caller
    /// (the anchor part), which keeps the rope immune to KSP's floating-origin and Krakensbane shifts.
    ///
    /// Length changes behave like a spool at the anchor end: every segment has the target length except the
    /// one at the anchor, which grows or shrinks; nodes are inserted/removed there when it wraps. Nothing
    /// else moves, so paying out or reeling in never makes the rope jump.
    ///
    /// Contacts use Coulomb friction: a node gripping a surface is held fixed while the rest of the rope is
    /// solved, and breaks loose once the rope pulls on it harder than its contact can resist.
    ///
    /// Pure math (no engine calls) so it can be tested outside Unity. Hot loops use explicit component
    /// math because Unity's Vector3 operators are not inlined by Mono.
    /// </summary>
    internal sealed class RopeSimulation
    {
        public const int MinNodes = 6;
        private const int DenseFactor = 4;

        public readonly Vector3[] Pos;
        public readonly Vector3[] Prev;
        public int Count { get; private set; }
        public float Length { get; private set; }
        public float SegmentTarget { get; }
        public int MaxNodes { get; }

        /// <summary>Current rest length of a regular segment (scaled up when the rope is pulled taut).</summary>
        public float Rest { get; private set; }

        /// <summary>Current rest length of the spool segment at the anchor end.</summary>
        public float LastRest { get; private set; }

        private float baseRest;
        private float lastRest;
        private bool spoolPinned;

        private readonly bool[] stuck;
        private readonly float[] grip;
        private readonly Vector3[] contactNormal;
        private readonly Vector3[] contactPoint;
        private readonly bool[] hasContact;
        private readonly int[] contactBody;
        private readonly float[] contactDepth;
        private readonly bool[] touching;
        private readonly int[] segOrder;
        private readonly float[] segMin;
        // Contacts for segment midpoints, so straight segments can't cut across the corners of a hull.
        private readonly bool[] midHas;
        private readonly Vector3[] midPoint;
        private readonly Vector3[] midNormal;
        private readonly Vector3[] dense;
        private readonly float[] denseArc;
        private readonly Vector3[] flow;
        private readonly float flowSeed;
        private float lastH;
        private float time;
        private bool hasLast;
        private Vector3 lastA;
        private Vector3 lastB;
        private bool sweepForward;

        public RopeSimulation(int maxNodes, float segmentTarget, float seed)
        {
            MaxNodes = Math.Max(MinNodes + 1, maxNodes);
            SegmentTarget = Math.Max(0.01f, segmentTarget);
            Pos = new Vector3[MaxNodes];
            Prev = new Vector3[MaxNodes];
            stuck = new bool[MaxNodes];
            grip = new float[MaxNodes];
            contactNormal = new Vector3[MaxNodes];
            contactPoint = new Vector3[MaxNodes];
            hasContact = new bool[MaxNodes];
            contactBody = new int[MaxNodes];
            contactDepth = new float[MaxNodes];
            touching = new bool[MaxNodes];
            segOrder = new int[MaxNodes];
            segMin = new float[MaxNodes];
            midHas = new bool[MaxNodes];
            midPoint = new Vector3[MaxNodes];
            midNormal = new Vector3[MaxNodes];
            for (int i = 0; i < MaxNodes; i++)
                segOrder[i] = i;
            flow = new Vector3[MaxNodes];
            dense = new Vector3[(MaxNodes - 1) * DenseFactor + 1];
            denseArc = new float[dense.Length];
            flowSeed = seed;
        }

        /// <summary>Number of nodes currently held by static friction.</summary>
        public int StuckCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < Count; i++)
                    if (stuck[i]) n++;
                return n;
            }
        }

        /// <summary>
        /// Node layout for a rope length: short ropes use <see cref="MinNodes"/> equal segments, long ones
        /// <see cref="MaxNodes"/> equal segments, and in between every segment is the target length except the
        /// spool segment at the anchor. All three regimes meet continuously.
        /// </summary>
        public int SegmentsFor(float length, out float regular, out float spool)
        {
            float s = SegmentTarget;
            int minSeg = MinNodes - 1, maxSeg = MaxNodes - 1;
            if (length <= minSeg * s)
            {
                regular = spool = length / minSeg;
                return minSeg;
            }
            if (length >= maxSeg * s)
            {
                regular = spool = length / maxSeg;
                return maxSeg;
            }
            int full = (int)Math.Floor(length / s);
            float r = length - full * s;
            regular = s;
            if (r <= s * 1e-4f)
            {
                spool = s;
                return full;
            }
            spool = r;
            return full + 1;
        }

        /// <summary>Lays the rope out between a and b with a smooth bow so its length matches.</summary>
        public void Initialize(Vector3 a, Vector3 b, float length, Vector3 bowDir)
        {
            Length = Math.Max(0.01f, length);
            int segs = SegmentsFor(Length, out baseRest, out lastRest);
            Count = segs + 1;

            Vector3 ab = b - a;
            float d = ab.magnitude;
            Vector3 axis = d > 1e-4f ? ab / d : Perpendicular(bowDir.sqrMagnitude > 1e-8f ? bowDir : Vector3.up);
            Vector3 n1 = bowDir - axis * Vector3.Dot(bowDir, axis);
            if (n1.sqrMagnitude < 1e-8f)
                n1 = Perpendicular(axis);
            n1.Normalize();
            Vector3 n2 = Vector3.Cross(axis, n1);

            int samples = segs * DenseFactor;
            float amp = 0f;
            if (Length > d * 1.0005f)
            {
                float lo = 0f, hi = Length;
                for (int it = 0; it < 40; it++)
                {
                    float mid = 0.5f * (lo + hi);
                    if (SampleShape(a, ab, n1, n2, mid, samples) < Length)
                        lo = mid;
                    else
                        hi = mid;
                }
                amp = lo;
            }
            float total = SampleShape(a, ab, n1, n2, amp, samples);

            // Place the nodes along the dense curve at their rest-length arc positions.
            float scale = total / Length;
            int k = 0;
            for (int i = 0; i <= segs; i++)
            {
                float target = (i < segs ? i * baseRest : Length) * scale;
                while (k < samples - 1 && denseArc[k + 1] < target)
                    k++;
                float span = denseArc[k + 1] - denseArc[k];
                float t = span > 1e-7f ? Mathf.Clamp01((target - denseArc[k]) / span) : 0f;
                Pos[i] = Vector3.Lerp(dense[k], dense[k + 1], t);
                Prev[i] = Pos[i];
                stuck[i] = false;
                grip[i] = 0f;
            }
            Pos[0] = a;
            Pos[segs] = b;
            Prev[0] = a;
            Prev[segs] = b;

            Rest = baseRest;
            LastRest = lastRest;
            lastH = 0f;
            hasLast = false;
        }

        private float SampleShape(Vector3 a, Vector3 ab, Vector3 n1, Vector3 n2, float amp, int samples)
        {
            float len = 0f;
            denseArc[0] = 0f;
            dense[0] = ShapePoint(a, ab, n1, n2, amp, 0f);
            for (int i = 1; i <= samples; i++)
            {
                dense[i] = ShapePoint(a, ab, n1, n2, amp, (float)i / samples);
                len += (dense[i] - dense[i - 1]).magnitude;
                denseArc[i] = len;
            }
            return len;
        }

        private static Vector3 ShapePoint(Vector3 a, Vector3 ab, Vector3 n1, Vector3 n2, float amp, float t)
        {
            float s1 = (float)Math.Sin(Math.PI * t);
            float s2 = (float)Math.Sin(2.0 * Math.PI * t);
            return a + ab * t + n1 * (amp * s1) + n2 * (amp * 0.35f * s2);
        }

        /// <summary>Pays rope out of (or reels it into) the spool at the anchor end.</summary>
        public void SetLength(float length)
        {
            Length = Math.Max(0.01f, length);
            int want = SegmentsFor(Length, out baseRest, out lastRest) + 1;
            int guard = 0;
            while (Count < want && guard++ < 4096)
                InsertAtAnchor();
            while (Count > want && guard++ < 4096)
                RemoveNearAnchor();
        }

        private void InsertAtAnchor()
        {
            int last = Count - 1;
            Pos[last + 1] = Pos[last];
            Prev[last + 1] = Prev[last];
            stuck[last + 1] = false;
            // The new node emerges from the anchor, on the line towards the previous node.
            Vector3 anchor = Pos[last + 1];
            Vector3 toward = Pos[last - 1] - anchor;
            float d = toward.magnitude;
            Vector3 dir = d > 1e-6f ? toward / d : Vector3.up;
            float off = Math.Max(1e-3f, Math.Min(lastRest, d * 0.5f));
            Pos[last] = anchor + dir * off;
            Prev[last] = Prev[last + 1] + dir * off;
            stuck[last] = false;
            grip[last] = 0f;
            Count++;
        }

        private void RemoveNearAnchor()
        {
            int last = Count - 1;
            Pos[last - 1] = Pos[last];
            Prev[last - 1] = Prev[last];
            stuck[last - 1] = false;
            stuck[last - 2] = false;
            Count--;
        }

        /// <summary>Flips the rope end for end (when the tether's ends are swapped).</summary>
        public void Reverse()
        {
            Array.Reverse(Pos, 0, Count);
            Array.Reverse(Prev, 0, Count);
            Array.Reverse(stuck, 0, Count);
            Array.Reverse(grip, 0, Count);
            Array.Reverse(contactNormal, 0, Count);
            Vector3 t = lastA;
            lastA = lastB;
            lastB = t;
        }

        /// <summary>Moves the whole rope (used when the frame origin jumps).</summary>
        public void Shift(Vector3 delta)
        {
            for (int i = 0; i < Count; i++)
            {
                Pos[i] += delta;
                Prev[i] += delta;
            }
            lastA += delta;
            lastB += delta;
        }

        public void Step(float dt, ref RopeStepParams p, IRopeCollisionSolver collider)
        {
            int last = Count - 1;
            if (last < 2)
                return;
            if (!hasLast)
            {
                lastA = p.A;
                lastB = p.B;
                hasLast = true;
            }
            if (dt <= 0f)
            {
                if (p.PinA) Pos[0] = p.A;
                if (p.PinB) Pos[last] = p.B;
                return;
            }
            if (collider == null)
            {
                for (int i = 0; i < Count; i++)
                    stuck[i] = false;
            }

            dt = Math.Min(dt, 0.1f);
            int steps = Mathf.Clamp((int)Math.Ceiling(dt * p.SubstepRate - 1e-3f), 1, 8);
            float h = dt / steps;
            int iterations = Math.Max(1, p.Iterations);

            ComputeFlow(dt, p.IdleFlow);

            for (int s = 1; s <= steps; s++)
            {
                float f = (float)s / steps;
                Vector3 a = p.PinA ? Vector3.Lerp(lastA, p.A, f) : Pos[0];
                Vector3 b = p.PinB ? Vector3.Lerp(lastB, p.B, f) : Pos[last];
                // Both ends pinned: a rope pulled past its length goes straight instead of fighting the pins.
                float scale = 1f;
                if (p.PinA && p.PinB)
                {
                    float span = (b - a).magnitude;
                    if (span > Length)
                    {
                        scale = span / Length;
                        for (int i = 0; i < Count; i++)
                            stuck[i] = false;
                    }
                }
                Rest = baseRest * scale;
                LastRest = lastRest * scale;
                // While the spool segment is short the newest node is still inside the fitting.
                spoolPinned = p.PinB && LastRest < 0.5f * Rest;
                if (spoolPinned)
                    stuck[last - 1] = false;

                Integrate(h, ref p);
                if (p.PinA) { Prev[0] = Pos[0]; Pos[0] = a; }
                if (p.PinB) { Prev[last] = Pos[last]; Pos[last] = b; }
                if (spoolPinned) { Prev[last - 1] = Pos[last - 1]; }
                PinSpool(b, p.DirB);

                EndDirections(a, b, ref p);
                PinSpool(b, p.DirB);

                // Contacts are found once per substep from the predicted positions, then enforced inside every
                // solver iteration, so rope length and hulls are solved together instead of fighting.
                if (collider != null)
                    GenerateContacts(collider, s == steps);
                else
                    ClearContacts();

                // Largest turn allowed between consecutive segments for the minimum bend radius.
                float maxTurn = p.MinBendRadius > 1e-4f ? Math.Min(2.6f, Rest / p.MinBendRadius) : 2.6f;
                float cosMaxTurn = (float)Math.Cos(maxTurn);
                int selfPassA = iterations / 2, selfPassB = iterations - 1;
                for (int it = 0; it < iterations; it++)
                {
                    SolveDistances(p.PinA, p.PinB);
                    SolveBending(p.Bend, cosMaxTurn, p.PinA, p.PinB);
                    if (p.SelfThickness > 0f && (it == selfPassA || it == selfPassB))
                        SolveSelfCollision(p.SelfThickness, p.PinA, p.PinB);
                    if (collider != null)
                    {
                        SolveContacts();
                        SolveMidpoints(p.PinA, p.PinB);
                    }
                    sweepForward = !sweepForward;
                }
                SolveDistances(p.PinA, p.PinB);
                LongRange(a, b, p.PinA, p.PinB);

                // Contacts get the last word: an over-stretched rope may stretch round a hull, never cut through it.
                if (collider != null)
                {
                    ReleaseOverloadedGrips(p.Friction);
                    SolveMidpoints(p.PinA, p.PinB);
                    FinishContacts(p.Friction);
                }

                if (p.PinA) Pos[0] = a;
                if (p.PinB) Pos[last] = b;
                PinSpool(b, p.DirB);
            }

            lastA = p.A;
            lastB = p.B;

            if (!IsFinite(Pos[last / 2]) || !IsFinite(Pos[last - 1]) || !IsFinite(Pos[1]))
            {
                // Never let a numerical blow-up escape; lay the rope out again.
                Vector3 bow = p.Gravity.sqrMagnitude > 0.25f ? p.Gravity : Perpendicular(p.B - p.A);
                Initialize(p.A, p.B, Length, bow);
            }
        }

        private void PinSpool(Vector3 b, Vector3 dirB)
        {
            // Its velocity (Pos - Prev) is set once per substep in Step, so it carries the pin's motion.
            if (spoolPinned)
                Pos[Count - 2] = b + dirB * LastRest;
        }

        private void ComputeFlow(float dt, float strength)
        {
            time += dt;
            if (strength <= 0f)
                return;
            double t = time + flowSeed;
            int n = Count;
            for (int i = 0; i < n; i++)
            {
                double x = i * 0.37;
                flow[i] = new Vector3(
                    (float)(Math.Sin(t * 0.63 + x * 1.3) + 0.5 * Math.Sin(t * 1.37 - x * 2.1 + 1.7)) * 0.66f * strength,
                    (float)(Math.Sin(t * 0.71 - x * 1.1 + 2.3) + 0.5 * Math.Sin(t * 1.19 + x * 1.7 + 0.4)) * 0.66f * strength,
                    (float)(Math.Sin(t * 0.57 + x * 0.9 + 4.1) + 0.5 * Math.Sin(t * 1.53 - x * 1.5 + 5.2)) * 0.66f * strength);
            }
        }

        private void Integrate(float h, ref RopeStepParams p)
        {
            int last = Count - 1;
            float ratio = lastH > 0f ? Mathf.Clamp(h / lastH, 0.5f, 2f) : 1f;
            float rk = ratio / (1f + p.Damping * h);
            float drag = Math.Min(0.9f, p.AirDrag * h);
            float awx = p.AirVelocity.x * h, awy = p.AirVelocity.y * h, awz = p.AirVelocity.z * h;
            float h2 = h * h;
            float gx = p.Gravity.x * h2, gy = p.Gravity.y * h2, gz = p.Gravity.z * h2;
            float maxStep = 60f * h; // cap node speed at 60 m/s relative to the frame
            float maxStep2 = maxStep * maxStep;
            bool useFlow = p.IdleFlow > 0f;
            Vector3[] P = Pos, Q = Prev, F = flow;

            int first = p.PinA ? 1 : 0;
            int end = p.PinB ? (spoolPinned ? last - 2 : last - 1) : last;
            for (int i = first; i <= end; i++)
            {
                Vector3 cur = P[i];
                float vx = (cur.x - Q[i].x) * rk;
                float vy = (cur.y - Q[i].y) * rk;
                float vz = (cur.z - Q[i].z) * rk;
                if (drag > 0f)
                {
                    vx += (awx - vx) * drag;
                    vy += (awy - vy) * drag;
                    vz += (awz - vz) * drag;
                }
                float v2 = vx * vx + vy * vy + vz * vz;
                if (v2 > maxStep2)
                {
                    float k = maxStep / (float)Math.Sqrt(v2);
                    vx *= k; vy *= k; vz *= k;
                }
                float ax = gx, ay = gy, az = gz;
                if (useFlow && !stuck[i])
                {
                    ax += F[i].x * h2;
                    ay += F[i].y * h2;
                    az += F[i].z * h2;
                }
                Q[i] = cur;
                P[i].x = cur.x + vx + ax;
                P[i].y = cur.y + vy + ay;
                P[i].z = cur.z + vz + az;
            }
            lastH = h;
        }

        /// <summary>
        /// Bending as a distance constraint between every node and the one after next: the pair is pushed
        /// apart softly (the hose springs back towards straight) and firmly once the turn at the middle node
        /// exceeds the minimum bend radius. Moving only the outer nodes straightens the middle one without
        /// fighting the stretch constraints, which is what gives slack hose its smooth, lazy loops.
        /// </summary>
        private void SolveBending(float softness, float cosMaxTurn, bool pinA, bool pinB)
        {
            int last = Count - 1;
            float rest = Rest, spool = LastRest;
            int pinnedLo = pinA ? 0 : -1;
            int pinnedHi = pinB ? (spoolPinned ? last - 1 : last) : last + 1;
            Vector3[] P = Pos;
            bool[] S = stuck;
            if (sweepForward)
            {
                for (int i = 0; i + 2 <= last; i++)
                    BendPair(P, S, i, rest, i + 1 == last - 1 ? spool : rest, softness, cosMaxTurn, pinnedLo, pinnedHi);
            }
            else
            {
                for (int i = last - 2; i >= 0; i--)
                    BendPair(P, S, i, rest, i + 1 == last - 1 ? spool : rest, softness, cosMaxTurn, pinnedLo, pinnedHi);
            }
        }

        private static void BendPair(Vector3[] P, bool[] S, int i, float r1, float r2, float softness, float cosMaxTurn,
            int pinnedLo, int pinnedHi)
        {
            int k = i + 2;
            bool fixI = i <= pinnedLo || i >= pinnedHi || S[i];
            bool fixK = k <= pinnedLo || k >= pinnedHi || S[k];
            if (fixI && fixK)
                return;
            float dx = P[k].x - P[i].x, dy = P[k].y - P[i].y, dz = P[k].z - P[i].z;
            float len2 = dx * dx + dy * dy + dz * dz;
            if (len2 < 1e-14f)
                return;
            float straight = r1 + r2;
            if (len2 >= straight * straight)
                return;
            float len = (float)Math.Sqrt(len2);
            // Span across the middle node when it turns by exactly the maximum angle.
            float minSpan = (float)Math.Sqrt(Math.Max(0f, r1 * r1 + r2 * r2 + 2f * r1 * r2 * cosMaxTurn));
            float goal = len < minSpan ? minSpan : len + (straight - len) * softness;
            float c = (len - goal) / (len * (fixI || fixK ? 1f : 2f));
            dx *= c; dy *= c; dz *= c;
            if (!fixI) { P[i].x += dx; P[i].y += dy; P[i].z += dz; }
            if (!fixK) { P[k].x -= dx; P[k].y -= dy; P[k].z -= dz; }
        }

        private void EndDirections(Vector3 a, Vector3 b, ref RopeStepParams p)
        {
            float k = p.EndStiffness;
            if (k <= 0f)
                return;
            int last = Count - 1;
            if (p.PinA)
            {
                if (!stuck[1])
                    Pos[1] = Vector3.Lerp(Pos[1], a + p.DirA * Rest, k);
                if (last >= 4 && !stuck[2])
                    Pos[2] = Vector3.Lerp(Pos[2], a + p.DirA * (2f * Rest), k * 0.35f);
            }
            if (p.PinB && !spoolPinned)
            {
                if (!stuck[last - 1])
                    Pos[last - 1] = Vector3.Lerp(Pos[last - 1], b + p.DirB * LastRest, k);
                if (last >= 4 && !stuck[last - 2])
                    Pos[last - 2] = Vector3.Lerp(Pos[last - 2], b + p.DirB * (LastRest + Rest), k * 0.35f);
            }
        }

        private void SolveDistances(bool pinA, bool pinB)
        {
            int last = Count - 1;
            float rest = Rest, spool = LastRest;
            // Fixed nodes: the kerbal end, the anchor end, a spool node still inside the anchor fitting, and
            // anything held by static friction.
            int pinnedLo = pinA ? 0 : -1;
            int pinnedHi = pinB ? (spoolPinned ? last - 1 : last) : last + 1;
            Vector3[] P = Pos;
            bool[] S = stuck;
            if (sweepForward)
            {
                for (int i = 0; i < last; i++)
                    SolvePair(P, S, i, i == last - 1 ? spool : rest, pinnedLo, pinnedHi);
            }
            else
            {
                for (int i = last - 1; i >= 0; i--)
                    SolvePair(P, S, i, i == last - 1 ? spool : rest, pinnedLo, pinnedHi);
            }
        }

        private static void SolvePair(Vector3[] P, bool[] S, int i, float rest, int pinnedLo, int pinnedHi)
        {
            int j = i + 1;
            bool fixI = i <= pinnedLo || i >= pinnedHi || S[i];
            bool fixJ = j <= pinnedLo || j >= pinnedHi || S[j];
            if (fixI && fixJ)
                return;
            float dx = P[j].x - P[i].x, dy = P[j].y - P[i].y, dz = P[j].z - P[i].z;
            float len2 = dx * dx + dy * dy + dz * dz;
            if (len2 < 1e-14f)
                return;
            float len = (float)Math.Sqrt(len2);
            float k = (len - rest) / (len * (fixI || fixJ ? 1f : 2f));
            dx *= k; dy *= k; dz *= k;
            if (!fixI) { P[i].x += dx; P[i].y += dy; P[i].z += dz; }
            if (!fixJ) { P[j].x -= dx; P[j].y -= dy; P[j].z -= dz; }
        }

        /// <summary>Long-range attachments: no node may be further from an end than the rope between them.</summary>
        private void LongRange(Vector3 a, Vector3 b, bool pinA, bool pinB)
        {
            int last = Count - 1;
            float rest = Rest, spool = LastRest;
            Vector3[] P = Pos;
            int end = spoolPinned ? last - 2 : last - 1;
            for (int i = 1; i <= end; i++)
            {
                if (pinA)
                {
                    float max = i * rest;
                    float dx = P[i].x - a.x, dy = P[i].y - a.y, dz = P[i].z - a.z;
                    float l2 = dx * dx + dy * dy + dz * dz;
                    if (l2 > max * max * 1.000001f)
                    {
                        float k = max / (float)Math.Sqrt(l2);
                        P[i].x = a.x + dx * k; P[i].y = a.y + dy * k; P[i].z = a.z + dz * k;
                        stuck[i] = false; // a rope pulled this hard can't be gripping anything
                    }
                }
                if (pinB)
                {
                    float max = (last - 1 - i) * rest + spool;
                    float dx = P[i].x - b.x, dy = P[i].y - b.y, dz = P[i].z - b.z;
                    float l2 = dx * dx + dy * dy + dz * dz;
                    if (l2 > max * max * 1.000001f)
                    {
                        float k = max / (float)Math.Sqrt(l2);
                        P[i].x = b.x + dx * k; P[i].y = b.y + dy * k; P[i].z = b.z + dz * k;
                        stuck[i] = false;
                    }
                }
            }
        }

        /// <summary>
        /// A gripping node lets go when the pull of its neighbours along the surface exceeds what its contact
        /// can hold (the grip, measured as how hard the contact pushes back each substep).
        /// </summary>
        private void ReleaseOverloadedGrips(float friction)
        {
            int last = Count - 1;
            float rest = Rest, spool = LastRest;
            Vector3[] P = Pos;
            for (int i = 1; i < last; i++)
            {
                if (!stuck[i])
                    continue;
                Vector3 pull = Vector3.zero;
                for (int side = -1; side <= 1; side += 2)
                {
                    int k = i + side;
                    float r = (side < 0 ? k : i) == last - 1 ? spool : rest;
                    float dx = P[k].x - P[i].x, dy = P[k].y - P[i].y, dz = P[k].z - P[i].z;
                    float len = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    if (len < 1e-7f)
                        continue;
                    float c = (len - r) / len;
                    pull.x += dx * c; pull.y += dy * c; pull.z += dz * c;
                }
                Vector3 n = contactNormal[i];
                float pn = pull.x * n.x + pull.y * n.y + pull.z * n.z;
                if (pn > 0f)
                {
                    // Lifted away from the surface.
                    stuck[i] = false;
                    continue;
                }
                pull.x -= n.x * pn; pull.y -= n.y * pn; pull.z -= n.z * pn;
                float limit = friction * grip[i];
                if (pull.x * pull.x + pull.y * pull.y + pull.z * pull.z > limit * limit)
                    stuck[i] = false;
            }
        }

        private void ClearContacts()
        {
            for (int i = 0; i < Count; i++)
            {
                hasContact[i] = false;
                midHas[i] = false;
                touching[i] = false;
                contactBody[i] = -1;
            }
        }

        /// <summary>Asks the world for a contact plane near each free node (once per substep).</summary>
        private void GenerateContacts(IRopeCollisionSolver collider, bool finalSubstep)
        {
            int last = Count - 1;
            int end = spoolPinned ? last - 2 : last - 1;
            hasContact[0] = hasContact[last] = false;
            if (spoolPinned)
                hasContact[last - 1] = false;
            for (int i = 1; i <= end; i++)
            {
                RopeContact c;
                if (collider.Probe(i, last, Pos[i], Prev[i], Pos[i - 1], Pos[i + 1], finalSubstep, out c))
                {
                    hasContact[i] = true;
                    contactPoint[i] = c.Point;
                    contactNormal[i] = c.Normal;
                    contactBody[i] = c.Body;
                    float d = Vector3.Dot(Pos[i] - c.Point, c.Normal);
                    contactDepth[i] = d < 0f ? -d : 0f; // how hard the node is being pressed in this substep
                }
                else
                {
                    hasContact[i] = false;
                    contactBody[i] = -1;
                    // Terrain is only probed on the final substep, so only then does "no contact" mean airborne.
                    if (finalSubstep)
                        stuck[i] = false;
                }
            }

            // Segments next to a contact also get a plane at their midpoint (the deepest point of a chord across
            // a hull's edge), probed with both of their ends as the way out.
            for (int i = 0; i < last; i++)
            {
                midHas[i] = false;
                if (!hasContact[i] && !hasContact[i + 1])
                    continue;
                RopeContact c;
                Vector3 m = (Pos[i] + Pos[i + 1]) * 0.5f;
                Vector3 mp = (Prev[i] + Prev[i + 1]) * 0.5f;
                if (collider.Probe(Math.Max(1, Math.Min(i, last - 1)), last, m, mp, Pos[i], Pos[i + 1], finalSubstep, out c))
                {
                    midHas[i] = true;
                    midPoint[i] = c.Point;
                    midNormal[i] = c.Normal;
                }
            }
        }

        /// <summary>Pushes a segment out so its midpoint satisfies its contact plane.</summary>
        private void SolveMidpoints(bool pinA, bool pinB)
        {
            int last = Count - 1;
            for (int i = 0; i < last; i++)
            {
                if (!midHas[i])
                    continue;
                Vector3 n = midNormal[i];
                float d = Vector3.Dot((Pos[i] + Pos[i + 1]) * 0.5f - midPoint[i], n);
                if (d >= 0f)
                    continue;
                float w0 = InvMass(i, last, pinA, pinB), w1 = InvMass(i + 1, last, pinA, pinB);
                float W = (w0 + w1) * 0.25f;
                if (W <= 0f)
                    continue;
                // Moving the midpoint by -d needs lambda * (w0 + w1) / 2 = -d along n.
                float lambda = -d / W * 0.5f;
                Pos[i] += n * (lambda * w0);
                Pos[i + 1] += n * (lambda * w1);
            }
        }

        /// <summary>Keeps nodes on the outside of their contact planes (inside the solver loop).</summary>
        private void SolveContacts()
        {
            int last = Count - 1;
            Vector3[] P = Pos;
            for (int i = 1; i < last; i++)
            {
                if (!hasContact[i] || stuck[i])
                    continue;
                Vector3 n = contactNormal[i], c = contactPoint[i];
                float d = (P[i].x - c.x) * n.x + (P[i].y - c.y) * n.y + (P[i].z - c.z) * n.z;
                if (d < 0f)
                {
                    P[i].x -= n.x * d;
                    P[i].y -= n.y * d;
                    P[i].z -= n.z * d;
                }
            }
        }

        /// <summary>Final contact projection plus Coulomb friction and velocity clean-up.</summary>
        private void FinishContacts(float friction)
        {
            int last = Count - 1;
            for (int i = 1; i < last; i++)
            {
                touching[i] = false;
                if (!hasContact[i])
                    continue;
                Vector3 n = contactNormal[i];
                float d = Vector3.Dot(Pos[i] - contactPoint[i], n);
                float push = contactDepth[i];
                if (d < 0f)
                {
                    Pos[i] -= n * d;
                    push += -d;
                    d = 0f;
                }
                if (d > 0.005f)
                {
                    // Near the surface but not on it.
                    stuck[i] = false;
                    continue;
                }
                touching[i] = true;

                // How hard the contact pushed back this substep stands in for the normal force.
                grip[i] = Math.Max(push, grip[i] * 0.8f);
                Vector3 disp = Pos[i] - Prev[i];
                float dn = Vector3.Dot(disp, n);
                Vector3 slip = disp - n * dn;
                float slipLen = slip.magnitude;
                float limit = friction * (push + 1e-5f);
                if (slipLen <= limit)
                {
                    // Static friction: stay put and hold this node while the next substep is solved.
                    Pos[i] -= slip;
                    stuck[i] = true;
                }
                else
                {
                    // Kinetic friction: slide, losing up to the friction limit.
                    Pos[i] -= slip * (limit / slipLen);
                    stuck[i] = false;
                }
                // Never keep velocity into the surface.
                dn = Vector3.Dot(Pos[i] - Prev[i], n);
                if (dn < 0f)
                    Prev[i] += n * dn;
            }
        }

        private int segOrderCount = -1;

        /// <summary>
        /// Stops the rope passing through itself: segment-segment separation, with candidate pairs found by
        /// sweep-and-prune along x (the order barely changes between passes, so the insertion sort is cheap).
        /// </summary>
        private void SolveSelfCollision(float thickness, bool pinA, bool pinB)
        {
            int last = Count - 1;
            int segs = last;
            if (segs < 4)
                return;
            float half = thickness * 0.5f;
            Vector3[] P = Pos;
            if (segOrderCount != segs)
            {
                for (int k = 0; k < segs; k++)
                    segOrder[k] = k;
                segOrderCount = segs;
            }
            for (int k = 0; k < segs; k++)
                segMin[k] = Math.Min(P[k].x, P[k + 1].x) - half;
            for (int k = 1; k < segs; k++)
            {
                int v = segOrder[k];
                float key = segMin[v];
                int j = k - 1;
                while (j >= 0 && segMin[segOrder[j]] > key)
                {
                    segOrder[j + 1] = segOrder[j];
                    j--;
                }
                segOrder[j + 1] = v;
            }

            for (int ia = 0; ia < segs; ia++)
            {
                int a = segOrder[ia];
                float maxX = Math.Max(P[a].x, P[a + 1].x) + half;
                float minY = Math.Min(P[a].y, P[a + 1].y) - half, maxY = Math.Max(P[a].y, P[a + 1].y) + half;
                float minZ = Math.Min(P[a].z, P[a + 1].z) - half, maxZ = Math.Max(P[a].z, P[a + 1].z) + half;
                for (int ib = ia + 1; ib < segs; ib++)
                {
                    int b = segOrder[ib];
                    if (segMin[b] > maxX)
                        break;
                    if (b - a < 2 && a - b < 2)
                        continue; // neighbours share a node
                    if (Math.Max(P[b].y, P[b + 1].y) + half < minY || Math.Min(P[b].y, P[b + 1].y) - half > maxY ||
                        Math.Max(P[b].z, P[b + 1].z) + half < minZ || Math.Min(P[b].z, P[b + 1].z) - half > maxZ)
                        continue;
                    SeparateSegments(a, b, thickness, last, pinA, pinB);
                }
            }
        }

        private float InvMass(int i, int last, bool pinA, bool pinB)
        {
            if ((i == 0 && pinA) || (i == last && pinB) || (spoolPinned && i == last - 1) || stuck[i])
                return 0f;
            return 1f;
        }

        private void SeparateSegments(int a, int b, float thickness, int last, bool pinA, bool pinB)
        {
            Vector3 p1 = Pos[a], q1 = Pos[a + 1], p2 = Pos[b], q2 = Pos[b + 1];
            Vector3 d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
            float aa = Vector3.Dot(d1, d1), ee = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, r);
            float s, t;
            const float eps = 1e-9f;
            if (aa <= eps && ee <= eps)
            {
                s = t = 0f;
            }
            else if (aa <= eps)
            {
                s = 0f;
                t = Mathf.Clamp01(f / ee);
            }
            else
            {
                float c = Vector3.Dot(d1, r);
                if (ee <= eps)
                {
                    t = 0f;
                    s = Mathf.Clamp01(-c / aa);
                }
                else
                {
                    float bb = Vector3.Dot(d1, d2);
                    float denom = aa * ee - bb * bb;
                    s = denom > eps ? Mathf.Clamp01((bb * f - c * ee) / denom) : 0f;
                    t = (bb * s + f) / ee;
                    if (t < 0f)
                    {
                        t = 0f;
                        s = Mathf.Clamp01(-c / aa);
                    }
                    else if (t > 1f)
                    {
                        t = 1f;
                        s = Mathf.Clamp01((bb - c) / aa);
                    }
                }
            }
            Vector3 diff = (p1 + d1 * s) - (p2 + d2 * t);
            float dist2 = diff.sqrMagnitude;
            if (dist2 >= thickness * thickness)
                return;
            float dist = (float)Math.Sqrt(dist2);
            Vector3 n;
            if (dist > 1e-6f)
                n = diff / dist;
            else
            {
                n = Vector3.Cross(d1, d2);
                if (n.sqrMagnitude < 1e-12f)
                    return;
                n.Normalize();
            }

            float w0 = InvMass(a, last, pinA, pinB), w1 = InvMass(a + 1, last, pinA, pinB);
            float w2 = InvMass(b, last, pinA, pinB), w3 = InvMass(b + 1, last, pinA, pinB);
            float g0 = 1f - s, g1 = s, g2 = 1f - t, g3 = t;
            float W = w0 * g0 * g0 + w1 * g1 * g1 + w2 * g2 * g2 + w3 * g3 * g3;
            if (W < 1e-9f)
                return;
            float lambda = (thickness - dist) / W;
            Pos[a] += n * (lambda * w0 * g0);
            Pos[a + 1] += n * (lambda * w1 * g1);
            Pos[b] -= n * (lambda * w2 * g2);
            Pos[b + 1] -= n * (lambda * w3 * g3);
        }

        /// <summary>After a step: is node <paramref name="i"/> resting against something, and what?</summary>
        public int TouchingBody(int i)
        {
            return i > 0 && i < Count - 1 && touching[i] ? contactBody[i] : -1;
        }

        /// <summary>Rope (at rest length) between node <paramref name="i"/> and end B.</summary>
        public float ArcToEndB(int i)
        {
            int last = Count - 1;
            if (i >= last)
                return 0f;
            return (last - 1 - i) * baseRest + lastRest;
        }

        /// <summary>Axis-aligned bounds of the nodes in the rope's local frame.</summary>
        public void LocalBounds(out Vector3 min, out Vector3 max)
        {
            float x0 = float.MaxValue, y0 = float.MaxValue, z0 = float.MaxValue;
            float x1 = float.MinValue, y1 = float.MinValue, z1 = float.MinValue;
            for (int i = 0; i < Count; i++)
            {
                Vector3 p = Pos[i];
                if (p.x < x0) x0 = p.x;
                if (p.x > x1) x1 = p.x;
                if (p.y < y0) y0 = p.y;
                if (p.y > y1) y1 = p.y;
                if (p.z < z0) z0 = p.z;
                if (p.z > z1) z1 = p.z;
            }
            min = new Vector3(x0, y0, z0);
            max = new Vector3(x1, y1, z1);
        }

        /// <summary>Total length of the current node polyline.</summary>
        public float PolylineLength()
        {
            float len = 0f;
            for (int i = 1; i < Count; i++)
                len += (Pos[i] - Pos[i - 1]).magnitude;
            return len;
        }

        public static Vector3 Perpendicular(Vector3 v)
        {
            Vector3 c = Vector3.Cross(v, Vector3.up);
            if (c.sqrMagnitude < 1e-6f)
                c = Vector3.Cross(v, Vector3.right);
            if (c.sqrMagnitude < 1e-12f)
                return Vector3.forward;
            return c.normalized;
        }

        private static bool IsFinite(Vector3 v)
        {
            return !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) ||
                     float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));
        }
    }
}

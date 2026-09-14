using System;
using UnityEngine;

namespace KSPTethers
{
    internal struct TubeBuildParams
    {
        public float Radius;
        public int Sides;
        public int Subdivisions;
        public float VPerMeter;          // texture V units per metre of rope
        public Vector3 DirA;            // direction the rope leaves the kerbal fitting
        public Vector3 DirB;            // direction the rope leaves the anchor fitting
        public Vector3 RefUpA;          // orientation reference at the kerbal end (keeps the texture from spinning)
        public bool FittingA;           // collar at end A
        public bool FittingB;           // collar at end B
        public bool BasePlateA;         // mounting plate where end A is clipped to a part
        public bool BasePlateB;         // mounting plate where end B is clipped to a part
    }

    /// <summary>
    /// Builds a smooth tube along rope nodes: Catmull-Rom smoothing, parallel-transport frames (no twisting),
    /// a ribbed-texture UV layout and small metal fittings at both ends. Output goes into reusable arrays so
    /// nothing is allocated per frame. Pure geometry so it can be unit tested outside Unity.
    /// </summary>
    internal sealed class TubeMeshBuilder
    {
        public Vector3[] Vertices = new Vector3[0];
        public Vector3[] Normals = new Vector3[0];
        public Vector4[] Tangents = new Vector4[0];
        public Vector2[] UVs = new Vector2[0];
        public int VertexCount { get; private set; }
        public int RopeVertexCount { get; private set; }

        public int[] RopeTriangles = new int[0];
        public int RopeTriangleCount { get; private set; }
        public int[] FittingTriangles = new int[0];
        public int FittingTriangleCount { get; private set; }

        /// <summary>Changes whenever the triangle lists changed and must be re-uploaded.</summary>
        public int TopologyVersion { get; private set; }

        private Vector3[] curve = new Vector3[0];
        private Vector3[] curveTan = new Vector3[0];
        private float[] cosTable = new float[0];
        private float[] sinTable = new float[0];
        private int tableSides = -1;
        private int builtRings = -1;
        private int builtSides = -1;
        private int builtFittingMask = -1;
        private bool writeTriangles;

        public void Build(Vector3[] nodes, int count, ref TubeBuildParams p)
        {
            int sides = Math.Max(3, p.Sides);
            int sub = Math.Max(1, p.Subdivisions);
            int last = count - 1;
            if (last < 1)
                return;

            int rings = last * sub + 1;
            int fittingMask = (p.FittingA ? 1 : 0) | (p.FittingB ? 2 : 0) | (p.BasePlateB ? 4 : 0) | (p.BasePlateA ? 8 : 0);
            writeTriangles = rings != builtRings || sides != builtSides || fittingMask != builtFittingMask;

            int fittingSides = sides + 4;
            int fittingVerts = CylinderVertexCount(sides) * 2 + CylinderVertexCount(fittingSides) * 2;
            EnsureCurveCapacity(rings);
            EnsureVertexCapacity(rings * (sides + 1) + fittingVerts);
            if (writeTriangles)
            {
                RopeTriangleCount = 0;
                FittingTriangleCount = 0;
                EnsureTriangleCapacity((rings - 1) * sides * 6, CylinderTriangleIndexCount(sides) * 2 + CylinderTriangleIndexCount(fittingSides) * 2);
            }
            SampleCurve(nodes, count, sub, p.DirA, p.DirB);
            BuildRope(rings, sides, ref p);

            float collarR = p.Radius * 1.75f;
            float collarLen = Mathf.Max(0.05f, p.Radius * 3f);
            Vector3 start = curve[0];
            Vector3 end = curve[rings - 1];
            Vector3 upB = RopeSimulation.Perpendicular(p.DirB);
            if (p.BasePlateA)
                AddCylinder(start - p.DirA * 0.004f, start + p.DirA * 0.012f, p.Radius * 3.4f, p.RefUpA, fittingSides);
            if (p.FittingA)
                AddCylinder(start - p.DirA * 0.01f, start + p.DirA * collarLen, collarR, p.RefUpA, sides);
            if (p.BasePlateB)
                AddCylinder(end - p.DirB * 0.004f, end + p.DirB * 0.012f, p.Radius * 3.4f, upB, fittingSides);
            if (p.FittingB)
                AddCylinder(end + p.DirB * collarLen, end - p.DirB * 0.005f, collarR, upB, sides);

            if (writeTriangles)
            {
                builtRings = rings;
                builtSides = sides;
                builtFittingMask = fittingMask;
                TopologyVersion++;
            }
        }

        private void BuildRope(int rings, int sides, ref TubeBuildParams p)
        {
            EnsureTrigTable(sides);
            float radius = p.Radius;
            float invSides = 1f / sides;
            Vector3 n = p.RefUpA - curveTan[0] * Vector3.Dot(p.RefUpA, curveTan[0]);
            if (n.sqrMagnitude < 1e-8f)
                n = RopeSimulation.Perpendicular(curveTan[0]);
            n.Normalize();

            int vi = 0;
            float arc = 0f;
            for (int i = 0; i < rings; i++)
            {
                Vector3 c = curve[i];
                Vector3 t = curveTan[i];
                if (i > 0)
                {
                    Vector3 pc = curve[i - 1];
                    float ex = c.x - pc.x, ey = c.y - pc.y, ez = c.z - pc.z;
                    arc += (float)Math.Sqrt(ex * ex + ey * ey + ez * ez);
                    // Parallel transport: remove the component of the previous normal along the new tangent.
                    float d = n.x * t.x + n.y * t.y + n.z * t.z;
                    n.x -= t.x * d; n.y -= t.y * d; n.z -= t.z * d;
                    float m2 = n.x * n.x + n.y * n.y + n.z * n.z;
                    if (m2 < 1e-10f)
                        n = RopeSimulation.Perpendicular(t);
                    else
                    {
                        float inv = 1f / (float)Math.Sqrt(m2);
                        n.x *= inv; n.y *= inv; n.z *= inv;
                    }
                }
                // b = t x n
                float bx = t.y * n.z - t.z * n.y;
                float by = t.z * n.x - t.x * n.z;
                float bz = t.x * n.y - t.y * n.x;
                float v = arc * p.VPerMeter;
                for (int s = 0; s <= sides; s++)
                {
                    float co = cosTable[s], si = sinTable[s];
                    float rx = n.x * co + bx * si, ry = n.y * co + by * si, rz = n.z * co + bz * si;
                    Vertices[vi] = new Vector3(c.x + rx * radius, c.y + ry * radius, c.z + rz * radius);
                    Normals[vi] = new Vector3(rx, ry, rz);
                    Tangents[vi] = new Vector4(bx * co - n.x * si, by * co - n.y * si, bz * co - n.z * si, 1f);
                    UVs[vi] = new Vector2(s * invSides, v);
                    vi++;
                }
            }
            VertexCount = vi;
            RopeVertexCount = vi;

            if (!writeTriangles)
                return;
            int stride = sides + 1;
            int ti = 0;
            int[] tri = RopeTriangles;
            for (int i = 0; i < rings - 1; i++)
            {
                int r0 = i * stride, r1 = r0 + stride;
                for (int s = 0; s < sides; s++)
                {
                    // Winding gives outward-facing triangles for Unity's clockwise front faces.
                    tri[ti++] = r0 + s;
                    tri[ti++] = r0 + s + 1;
                    tri[ti++] = r1 + s;
                    tri[ti++] = r0 + s + 1;
                    tri[ti++] = r1 + s + 1;
                    tri[ti++] = r1 + s;
                }
            }
            RopeTriangleCount = ti;
        }

        private void SampleCurve(Vector3[] nodes, int count, int sub, Vector3 dirA, Vector3 dirB)
        {
            int last = count - 1;
            float segA = Mathf.Max((nodes[1] - nodes[0]).magnitude, 1e-3f);
            float segB = Mathf.Max((nodes[last] - nodes[last - 1]).magnitude, 1e-3f);
            Vector3 before = nodes[0] - dirA * segA;
            Vector3 after = nodes[last] - dirB * segB;

            int k = 0;
            float invSub = 1f / sub;
            for (int j = 0; j < last; j++)
            {
                Vector3 p0 = j == 0 ? before : nodes[j - 1];
                Vector3 p1 = nodes[j];
                Vector3 p2 = nodes[j + 1];
                Vector3 p3 = j + 2 <= last ? nodes[j + 2] : after;
                // Catmull-Rom coefficients for this span.
                float c1x = p2.x - p0.x, c1y = p2.y - p0.y, c1z = p2.z - p0.z;
                float c2x = 2f * p0.x - 5f * p1.x + 4f * p2.x - p3.x;
                float c2y = 2f * p0.y - 5f * p1.y + 4f * p2.y - p3.y;
                float c2z = 2f * p0.z - 5f * p1.z + 4f * p2.z - p3.z;
                float c3x = 3f * p1.x - p0.x - 3f * p2.x + p3.x;
                float c3y = 3f * p1.y - p0.y - 3f * p2.y + p3.y;
                float c3z = 3f * p1.z - p0.z - 3f * p2.z + p3.z;
                for (int s = 0; s < sub; s++)
                {
                    float t = s * invSub, t2 = t * t, t3 = t2 * t;
                    curve[k++] = new Vector3(
                        p1.x + 0.5f * (c1x * t + c2x * t2 + c3x * t3),
                        p1.y + 0.5f * (c1y * t + c2y * t2 + c3y * t3),
                        p1.z + 0.5f * (c1z * t + c2z * t2 + c3z * t3));
                }
            }
            curve[k] = nodes[last];
            int rings = k + 1;

            for (int i = 0; i < rings; i++)
            {
                Vector3 t;
                if (i == 0)
                    t = dirA;
                else if (i == rings - 1)
                    t = -dirB;
                else
                    t = curve[i + 1] - curve[i - 1];
                float m2 = t.x * t.x + t.y * t.y + t.z * t.z;
                if (m2 < 1e-12f)
                {
                    curveTan[i] = i > 0 ? curveTan[i - 1] : Vector3.forward;
                    continue;
                }
                float inv = 1f / (float)Math.Sqrt(m2);
                curveTan[i] = new Vector3(t.x * inv, t.y * inv, t.z * inv);
            }
        }

        private static int CylinderVertexCount(int sides)
        {
            return 2 * (sides + 1) + 2 * (sides + 1);
        }

        private static int CylinderTriangleIndexCount(int sides)
        {
            return sides * 6 + sides * 6;
        }

        /// <summary>Closed cylinder from a to b (caps included) appended to the fitting sub-mesh.</summary>
        private void AddCylinder(Vector3 a, Vector3 b, float r, Vector3 refUp, int sides)
        {
            Vector3 axis = b - a;
            float len = axis.magnitude;
            axis = len > 1e-6f ? axis / len : Vector3.up;
            Vector3 n = refUp - axis * Vector3.Dot(refUp, axis);
            if (n.sqrMagnitude < 1e-8f)
                n = RopeSimulation.Perpendicular(axis);
            n.Normalize();
            Vector3 bn = Vector3.Cross(axis, n);
            int stride = sides + 1;
            int baseIndex = VertexCount;
            int vi = baseIndex;
            var tan = new Vector4(n.x, n.y, n.z, 1f);
            double step = 2.0 * Math.PI / sides;

            // Side walls: two rings with radial normals.
            for (int ring = 0; ring < 2; ring++)
            {
                Vector3 c = ring == 0 ? a : b;
                for (int s = 0; s <= sides; s++)
                {
                    Vector3 radial = n * (float)Math.Cos(s * step) + bn * (float)Math.Sin(s * step);
                    Vertices[vi] = c + radial * r;
                    Normals[vi] = radial;
                    Tangents[vi] = tan;
                    UVs[vi] = Vector2.zero;
                    vi++;
                }
            }
            // Caps: centre vertex + ring with flat normals.
            for (int cap = 0; cap < 2; cap++)
            {
                Vector3 c = cap == 0 ? a : b;
                Vector3 cn = cap == 0 ? -axis : axis;
                Vertices[vi] = c;
                Normals[vi] = cn;
                Tangents[vi] = tan;
                UVs[vi] = Vector2.zero;
                vi++;
                for (int s = 0; s < sides; s++)
                {
                    Vector3 radial = n * (float)Math.Cos(s * step) + bn * (float)Math.Sin(s * step);
                    Vertices[vi] = c + radial * r;
                    Normals[vi] = cn;
                    Tangents[vi] = tan;
                    UVs[vi] = Vector2.zero;
                    vi++;
                }
            }
            VertexCount = vi;

            if (!writeTriangles)
                return;
            int[] tri = FittingTriangles;
            int ti = FittingTriangleCount;
            for (int s = 0; s < sides; s++)
            {
                int r0 = baseIndex + s, r1 = baseIndex + stride + s;
                tri[ti++] = r0;
                tri[ti++] = r0 + 1;
                tri[ti++] = r1;
                tri[ti++] = r0 + 1;
                tri[ti++] = r1 + 1;
                tri[ti++] = r1;
            }
            int capA = baseIndex + 2 * stride;
            int capB = capA + sides + 1;
            for (int s = 0; s < sides; s++)
            {
                int s1 = (s + 1) % sides;
                // Cap at 'a' faces -axis, cap at 'b' faces +axis.
                tri[ti++] = capA;
                tri[ti++] = capA + 1 + s1;
                tri[ti++] = capA + 1 + s;
                tri[ti++] = capB;
                tri[ti++] = capB + 1 + s;
                tri[ti++] = capB + 1 + s1;
            }
            FittingTriangleCount = ti;
        }

        private void EnsureTrigTable(int sides)
        {
            if (tableSides == sides)
                return;
            cosTable = new float[sides + 1];
            sinTable = new float[sides + 1];
            for (int s = 0; s <= sides; s++)
            {
                double ang = 2.0 * Math.PI * s / sides;
                cosTable[s] = (float)Math.Cos(ang);
                sinTable[s] = (float)Math.Sin(ang);
            }
            tableSides = sides;
        }

        private void EnsureCurveCapacity(int rings)
        {
            if (curve.Length >= rings)
                return;
            int cap = Math.Max(rings, curve.Length * 2);
            curve = new Vector3[cap];
            curveTan = new Vector3[cap];
        }

        private void EnsureVertexCapacity(int n)
        {
            if (Vertices.Length >= n)
                return;
            int cap = Math.Max(n, Vertices.Length * 3 / 2);
            Vertices = new Vector3[cap];
            Normals = new Vector3[cap];
            Tangents = new Vector4[cap];
            UVs = new Vector2[cap];
        }

        private void EnsureTriangleCapacity(int rope, int fitting)
        {
            if (RopeTriangles.Length < rope)
                RopeTriangles = new int[Math.Max(rope, RopeTriangles.Length * 3 / 2)];
            if (FittingTriangles.Length < fitting)
                FittingTriangles = new int[Math.Max(fitting, FittingTriangles.Length * 3 / 2)];
        }
    }
}

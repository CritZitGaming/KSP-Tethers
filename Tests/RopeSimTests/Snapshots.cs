using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using UnityEngine;
using Color = System.Drawing.Color;
using Graphics = System.Drawing.Graphics;

namespace KSPTethers.Tests
{
    /// <summary>
    /// Renders solver output to PNG contact sheets so the rope's motion can be eyeballed without the game:
    /// dotnet run -c Release -- render &lt;outputDir&gt;
    /// </summary>
    internal static class Snapshots
    {
        private const int Cell = 360;

        public static void Render(string outDir)
        {
            System.IO.Directory.CreateDirectory(outDir);
            RenderZeroG(System.IO.Path.Combine(outDir, "zero_g_eva.png"));
            RenderSlackLoops(System.IO.Path.Combine(outDir, "slack_loops.png"));
            RenderLanded(System.IO.Path.Combine(outDir, "landed_eva.png"));
            RenderHullWrap(System.IO.Path.Combine(outDir, "hull_wrap.png"));
            string cfg = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\..\..\KSPTethers\Settings.cfg");
            if (System.IO.File.Exists(cfg))
                CableSwatches.Render(cfg, System.IO.Path.Combine(outDir, "cable_styles.png"));
            Console.WriteLine("Wrote snapshots to " + outDir);
        }

        private static RopeStepParams Params(Vector3 gravity)
        {
            return Program.Defaults(gravity);
        }

        /// <summary>A kerbal jetpacks away from a capsule hatch, drifts around and comes back, in orbit.</summary>
        private static void RenderZeroG(string file)
        {
            var rope = new RopeSimulation(120, 0.22f, 11f);
            Vector3 hatch = Vector3.zero, hatchNormal = Vector3.right;
            Func<float, Vector3> path = t =>
            {
                // Out 7 m, a lazy figure-eight, then back toward the hatch.
                float r = t < 6f ? 0.8f + t * 1.1f : t < 16f ? 7.4f : 7.4f - (t - 16f) * 0.9f;
                double a = t * 0.35;
                return new Vector3(r * (float)Math.Cos(a * 0.6), r * 0.55f * (float)Math.Sin(a), r * 0.35f * (float)Math.Sin(a * 0.8 + 1));
            };
            Vector3 kerbal = path(0f);
            float length = 1.2f;
            rope.Initialize(kerbal, hatch, length, Vector3.up);
            var p = Params(Vector3.zero);

            float[] shots = { 3f, 7f, 11f, 15f, 18f, 21f };
            RenderSheet(file, "Zero-g EVA: kerbal (orange) tethered to a hatch (grey); rope pays out as it drifts, then loops as it returns",
                shots, 0.9f, (t, dt) =>
                {
                    Vector3 prevKerbal = kerbal;
                    kerbal = path(t);
                    float dist = kerbal.magnitude;
                    float target = dist * 1.35f + 0.6f;
                    if (length < target) length = Math.Min(target, length + 4f * dt);
                    length = Math.Min(length, 15f);
                    rope.SetLength(length);
                    p.A = kerbal;
                    p.B = hatch;
                    Vector3 away = kerbal - prevKerbal;
                    p.DirA = away.sqrMagnitude > 1e-8f ? -away.normalized : (hatch - kerbal).normalized;
                    p.DirB = hatchNormal;
                    rope.Step(dt, ref p, null);
                    return rope;
                }, () => kerbal, () => hatch);
        }

        /// <summary>Like the loading-screen art: a kerbal bobbing a few metres off the hatch on a long, slack umbilical.</summary>
        private static void RenderSlackLoops(string file)
        {
            var rope = new RopeSimulation(120, 0.22f, 23f);
            Vector3 hatch = Vector3.zero, hatchNormal = Vector3.right;
            Func<float, Vector3> path = t => new Vector3(
                2.6f + 0.9f * (float)Math.Sin(t * 0.31),
                0.8f * (float)Math.Sin(t * 0.47 + 0.6),
                1.2f * (float)Math.Sin(t * 0.23 + 2.0));
            Vector3 kerbal = path(0f);
            rope.Initialize(kerbal, hatch, 9f, new Vector3(0.2f, 1f, 0.4f));
            var p = Params(Vector3.zero);

            float[] shots = { 1f, 4f, 8f, 12f, 16f, 20f };
            RenderSheet(file, "Zero-g, 9 m of slack umbilical with the kerbal drifting 2-4 m from the hatch",
                shots, 0.5f, (t, dt) =>
                {
                    Vector3 prev = kerbal;
                    kerbal = path(t);
                    p.A = kerbal;
                    p.B = hatch;
                    Vector3 v = kerbal - prev;
                    p.DirA = v.sqrMagnitude > 1e-10f ? -v.normalized : Vector3.left;
                    p.DirB = hatchNormal;
                    rope.Step(dt, ref p, null);
                    return rope;
                }, () => kerbal, () => hatch);
        }

        /// <summary>The "pulled through the ship" case: 6 m of rope, kerbal flies over the hull to the far side.</summary>
        private static void RenderHullWrap(string file)
        {
            var hull = new HullTests.BoxHull();
            var solver = new HullTests.HullSolver(hull);
            var rope = new RopeSimulation(120, 0.22f, 2f);
            Vector3 hatch = new Vector3(hull.Half.x + 0.004f, 0f, 0f);
            Vector3 kerbal = new Vector3(3.2f, 0.3f, 0.2f);
            rope.Initialize(kerbal, hatch, 6f, Vector3.up);
            var p = Params(Vector3.zero);
            p.IdleFlow = 0f;
            float[] shots = { 1f, 3f, 5f, 7f, 9f, 12f };
            hullHalf = hull.Half;
            RenderSheet(file, "6 m tether, kerbal flies over a 2.5 m hull to the far side: the rope drapes round it (grey box)",
                shots, 0.35f, (t, dt) =>
                {
                    float u = Math.Min(1f, t / 8f);
                    double ang = Math.PI * u;
                    kerbal = new Vector3(3.2f * (float)Math.Cos(ang), 3.2f * (float)Math.Sin(ang) + 0.3f, 0.2f);
                    p.A = kerbal;
                    p.B = hatch;
                    p.DirA = (hatch - kerbal).normalized;
                    p.DirB = Vector3.right;
                    rope.Step(dt, ref p, solver);
                    return rope;
                }, () => kerbal, () => hatch);
            hullHalf = Vector3.zero;
        }

        private static Vector3 hullHalf;

        private sealed class Ground : IRopeCollisionSolver
        {
            private readonly Program.GroundSolver inner = new Program.GroundSolver();

            public bool Probe(int index, int last, Vector3 pos, Vector3 prev, Vector3 hintA, Vector3 hintB, bool finalSubstep, out RopeContact contact)
            {
                return inner.Probe(index, last, pos, prev, hintA, hintB, finalSubstep, out contact);
            }
        }

        /// <summary>A kerbal walks around a lander on the Mun, dragging a tether through the dust.</summary>
        private static void RenderLanded(string file)
        {
            var rope = new RopeSimulation(120, 0.22f, 5f);
            Vector3 hatch = new Vector3(0f, 1.6f, 0f), hatchNormal = Vector3.right;
            Func<float, Vector3> path = t =>
            {
                double a = t * 0.3;
                float r = 1.2f + Math.Min(t, 8f) * 0.6f;
                return new Vector3(r * (float)Math.Cos(a), 0.9f, r * (float)Math.Sin(a));
            };
            Vector3 kerbal = path(0f);
            float length = 2f;
            rope.Initialize(kerbal, hatch, length, Vector3.down);
            var p = Params(new Vector3(0f, -1.63f, 0f));
            var ground = new Ground();

            float[] shots = { 2f, 5f, 8f, 11f, 14f, 17f };
            RenderSheet(file, "Landed EVA (Mun gravity): rope hangs from the hatch, piles up and grips the ground, then is dragged",
                shots, 0.7f, (t, dt) =>
                {
                    kerbal = path(t);
                    float dist = (kerbal - hatch).magnitude;
                    float target = dist * 1.35f + 0.6f;
                    if (length < target) length = Math.Min(target, length + 4f * dt);
                    length = Math.Min(length, 15f);
                    rope.SetLength(length);
                    p.A = kerbal;
                    p.B = hatch;
                    p.DirA = Vector3.down;
                    p.DirB = hatchNormal;
                    rope.Step(dt, ref p, ground);
                    return rope;
                }, () => kerbal, () => hatch, drawGround: true);
        }

        private static void RenderSheet(string file, string title, float[] shots, float yaw,
            Func<float, float, RopeSimulation> step, Func<Vector3> kerbal, Func<Vector3> anchor, bool drawGround = false)
        {
            int cols = 3, rows = (shots.Length + cols - 1) / cols;
            using (var bmp = new Bitmap(cols * Cell, rows * Cell + 28))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.FromArgb(12, 14, 22));
                using (var font = new Font("Segoe UI", 10f))
                    g.DrawString(title, font, Brushes.Gainsboro, 6, 6);

                float t = 0f, dt = 1f / 60f;
                int shot = 0;
                RopeSimulation rope = null;
                while (shot < shots.Length)
                {
                    t += dt;
                    rope = step(t, dt);
                    if (t + 1e-4f < shots[shot])
                        continue;
                    int cx = shot % cols, cy = shot / cols;
                    DrawCell(g, new Rectangle(cx * Cell, 28 + cy * Cell, Cell, Cell), rope, kerbal(), anchor(), yaw, drawGround,
                        "t = " + shots[shot].ToString("F0") + " s, rope " + rope.Length.ToString("F1") + " m");
                    shot++;
                }
                bmp.Save(file, ImageFormat.Png);
            }
        }

        private static PointF Project(Vector3 p, float yaw, Rectangle r, float scale, out float depth)
        {
            // Orbit camera: yaw around Y, slight pitch.
            float cy = (float)Math.Cos(yaw), sy = (float)Math.Sin(yaw);
            float x = p.x * cy - p.z * sy;
            float z = p.x * sy + p.z * cy;
            const float pitch = 0.35f;
            float cp = (float)Math.Cos(pitch), sp = (float)Math.Sin(pitch);
            float y = p.y * cp - z * sp;
            depth = p.y * sp + z * cp;
            return new PointF(r.X + r.Width * 0.5f + x * scale, r.Y + r.Height * 0.55f - y * scale);
        }

        private static void DrawCell(Graphics g, Rectangle r, RopeSimulation rope, Vector3 kerbal, Vector3 anchor, float yaw,
            bool drawGround, string label)
        {
            float scale = 22f;
            using (var border = new Pen(Color.FromArgb(40, 44, 60)))
                g.DrawRectangle(border, r.X, r.Y, r.Width - 1, r.Height - 1);
            float d;
            if (drawGround)
            {
                using (var pen = new Pen(Color.FromArgb(55, 58, 64)))
                {
                    for (int i = -8; i <= 8; i += 2)
                    {
                        g.DrawLine(pen, Project(new Vector3(i, 0, -8), yaw, r, scale, out d), Project(new Vector3(i, 0, 8), yaw, r, scale, out d));
                        g.DrawLine(pen, Project(new Vector3(-8, 0, i), yaw, r, scale, out d), Project(new Vector3(8, 0, i), yaw, r, scale, out d));
                    }
                }
            }

            PointF ap = Project(anchor, yaw, r, scale, out d);
            if (hullHalf != Vector3.zero)
            {
                // Wireframe of the box hull.
                Vector3 h = hullHalf;
                var corners = new Vector3[8];
                for (int k = 0; k < 8; k++)
                    corners[k] = new Vector3((k & 1) == 0 ? -h.x : h.x, (k & 2) == 0 ? -h.y : h.y, (k & 4) == 0 ? -h.z : h.z);
                using (var pen = new Pen(Color.FromArgb(150, 160, 175), 1.5f))
                {
                    for (int k = 0; k < 8; k++)
                        for (int bit = 1; bit < 8; bit <<= 1)
                            if ((k & bit) == 0)
                                g.DrawLine(pen, Project(corners[k], yaw, r, scale, out d), Project(corners[k | bit], yaw, r, scale, out d));
                }
            }
            else
            {
                // Capsule stand-in behind the hatch.
                using (var hull = new SolidBrush(Color.FromArgb(150, 160, 175)))
                    g.FillEllipse(hull, ap.X - 26, ap.Y - 22, 34, 44);
            }

            // Rope, as the tube would be drawn: smooth spline, brighter when nearer the camera.
            var pts = new PointF[rope.Count];
            var depths = new float[rope.Count];
            for (int i = 0; i < rope.Count; i++)
                pts[i] = Project(rope.Pos[i], yaw, r, scale, out depths[i]);
            for (int i = 1; i < rope.Count; i++)
            {
                float shade = Math.Max(0.45f, Math.Min(1f, 0.75f - (depths[i] + depths[i - 1]) * 0.03f));
                int c = (int)(245 * shade);
                using (var pen = new Pen(Color.FromArgb(c, c, (int)(c * 0.97f)), 3.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLine(pen, pts[i - 1], pts[i]);
            }

            PointF kp = Project(kerbal, yaw, r, scale, out d);
            g.FillEllipse(Brushes.DarkOrange, kp.X - 7, kp.Y - 7, 14, 14);
            g.FillEllipse(new SolidBrush(Color.FromArgb(170, 230, 120)), kp.X - 4, kp.Y - 9, 8, 8);
            using (var font = new Font("Segoe UI", 9f))
                g.DrawString(label, font, Brushes.Silver, r.X + 6, r.Bottom - 20);
        }
    }
}

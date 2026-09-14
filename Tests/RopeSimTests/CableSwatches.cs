using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text.RegularExpressions;
using Color = System.Drawing.Color;
using Color32 = UnityEngine.Color32;
using Graphics = System.Drawing.Graphics;

namespace KSPTethers.Tests
{
    /// <summary>
    /// Renders each CABLE_STYLE in Settings.cfg as a lit length of cable using the real procedural
    /// textures and normal maps, approximating the game's specular shader, so styles can be judged offline.
    /// </summary>
    internal static class CableSwatches
    {
        private sealed class Style
        {
            public string Name, Title, Pattern = "Ribbed";
            public float[] Color = { 1, 1, 1 }, Spec = { 0.3f, 0.3f, 0.3f };
            public float Shininess = 0.1f, Radius = 0.02f, Tiles = 7f, Bump = 1f;
        }

        public static void Render(string settingsPath, string outFile)
        {
            List<Style> styles = Parse(System.IO.File.ReadAllText(settingsPath));
            const int width = 760, rowH = 70, pxPerMeter = 1400;
            using (var bmp = new Bitmap(width, rowH * styles.Count + 30))
            using (Graphics g = Graphics.FromImage(bmp))
            using (var font = new Font("Segoe UI", 10f, FontStyle.Bold))
            using (var small = new Font("Segoe UI", 8.5f))
            {
                g.Clear(Color.FromArgb(18, 20, 28));
                g.DrawString("Cable styles (offline render of the procedural textures, approximating KSP/Bumped Specular)", small, Brushes.Silver, 6, 6);
                for (int s = 0; s < styles.Count; s++)
                {
                    Style st = styles[s];
                    CablePattern pattern = CablePatterns.Parse(st.Pattern, CablePattern.Ribbed);
                    Color32[] albedo, normal;
                    CablePatterns.Generate(pattern, st.Bump, out albedo, out normal);
                    int top = 30 + s * rowH;
                    g.DrawString(st.Title, font, Brushes.Gainsboro, 8, top + 4);
                    g.DrawString(st.Name + " - " + st.Pattern + ", " + (st.Radius * 2000f).ToString("F0") + " mm", small, Brushes.Gray, 8, top + 24);
                    DrawCable(bmp, st, albedo, normal, 230, width - 12, top + rowH / 2, pxPerMeter);
                }
                bmp.Save(outFile, ImageFormat.Png);
            }
            Console.WriteLine("Wrote " + outFile);
        }

        private static void DrawCable(Bitmap bmp, Style st, Color32[] albedo, Color32[] normal, int x0, int x1, int cy, int pxPerMeter)
        {
            int n = CablePatterns.Size;
            float rPx = st.Radius * pxPerMeter;
            // Light from the upper left, slightly toward the viewer; viewer looks along -z.
            float lx = -0.45f, ly = 0.6f, lz = 0.66f;
            float ll = (float)Math.Sqrt(lx * lx + ly * ly + lz * lz);
            lx /= ll; ly /= ll; lz /= ll;
            float hx = lx, hy = ly, hz = lz + 1f;
            float hl = (float)Math.Sqrt(hx * hx + hy * hy + hz * hz);
            hx /= hl; hy /= hl; hz /= hl;
            float exponent = Math.Max(2f, st.Shininess * 128f);

            for (int py = (int)(cy - rPx); py <= (int)(cy + rPx); py++)
            {
                float sy = (cy - py) / rPx; // +1 at the top edge
                if (Math.Abs(sy) >= 1f)
                    continue;
                float theta = (float)Math.Asin(sy);
                // Cylinder normal (y up, z toward the viewer); tangent runs around, bitangent along the cable.
                float nyBase = (float)Math.Sin(theta), nzBase = (float)Math.Cos(theta);
                float ty = (float)Math.Cos(theta), tz = -(float)Math.Sin(theta);
                float u = (float)(theta / (2 * Math.PI) + 0.25);
                for (int px = x0; px < x1; px++)
                {
                    float v = (px - x0) / (float)pxPerMeter * st.Tiles;
                    int ix = Wrap((int)(u * n), n), iy = Wrap((int)(v * n), n);
                    Color32 a = albedo[iy * n + ix], nm = normal[iy * n + ix];
                    float tnx = nm.a / 255f * 2f - 1f, tny = nm.g / 255f * 2f - 1f;
                    float tnz = (float)Math.Sqrt(Math.Max(0f, 1f - tnx * tnx - tny * tny));
                    // n = T * tnx + B * tny + N * tnz, with B = +x (along the cable).
                    float wx = tny, wy = ty * tnx + nyBase * tnz, wz = tz * tnx + nzBase * tnz;
                    float wl = (float)Math.Sqrt(wx * wx + wy * wy + wz * wz);
                    wx /= wl; wy /= wl; wz /= wl;
                    float diff = Math.Max(0f, wx * lx + wy * ly + wz * lz);
                    float spec = (float)Math.Pow(Math.Max(0f, wx * hx + wy * hy + wz * hz), exponent) * (diff > 0 ? 1f : 0f);
                    float alb = a.g / 255f;
                    int r = Channel(alb * st.Color[0] * (0.22f + 0.85f * diff) + spec * st.Spec[0]);
                    int gg = Channel(alb * st.Color[1] * (0.22f + 0.85f * diff) + spec * st.Spec[1]);
                    int b = Channel(alb * st.Color[2] * (0.22f + 0.85f * diff) + spec * st.Spec[2]);
                    bmp.SetPixel(px, py, Color.FromArgb(r, gg, b));
                }
            }
        }

        private static int Wrap(int i, int n)
        {
            i %= n;
            return i < 0 ? i + n : i;
        }

        private static int Channel(float f)
        {
            return Math.Max(0, Math.Min(255, (int)(f * 255f)));
        }

        private static List<Style> Parse(string text)
        {
            var list = new List<Style>();
            foreach (Match m in Regex.Matches(text, @"CABLE_STYLE\s*\{(?<body>[^{}]*)\}"))
            {
                var st = new Style();
                foreach (string raw in m.Groups["body"].Value.Split('\n'))
                {
                    string line = raw.Split(new[] { "//" }, StringSplitOptions.None)[0].Trim();
                    int eq = line.IndexOf('=');
                    if (eq < 0)
                        continue;
                    string key = line.Substring(0, eq).Trim(), val = line.Substring(eq + 1).Trim();
                    switch (key)
                    {
                        case "name": st.Name = val; break;
                        case "title": st.Title = val; break;
                        case "pattern": st.Pattern = val; break;
                        case "color": st.Color = Floats(val); break;
                        case "specular": st.Spec = Floats(val); break;
                        case "shininess": st.Shininess = F(val); break;
                        case "radius": st.Radius = F(val); break;
                        case "tilesPerMeter": st.Tiles = F(val); break;
                        case "bump": st.Bump = F(val); break;
                    }
                }
                list.Add(st);
            }
            return list;
        }

        private static float F(string s)
        {
            return float.Parse(s, CultureInfo.InvariantCulture);
        }

        private static float[] Floats(string s)
        {
            string[] p = s.Split(',');
            return new[] { F(p[0]), F(p[1]), F(p[2]) };
        }
    }
}

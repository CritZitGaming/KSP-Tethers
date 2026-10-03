using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using Color = System.Drawing.Color;
using Color32 = UnityEngine.Color32;
using Graphics = System.Drawing.Graphics;
using UColor = UnityEngine.Color;

namespace KSPTethers.Tests
{
    /// <summary>
    /// Renders every CABLE_STYLE in Settings.cfg as a lit length of cable, using the real generated maps,
    /// with a rough stand-in for KSP's Bumped Specular shader. Lets the styles be judged without the game.
    /// </summary>
    internal static class CableSwatches
    {
        internal sealed class Style
        {
            public string Name = "", Title = "", Construction = "ribbed";
            public float Radius = 0.02f, Aspect = 0.18f, Gloss = 34f, Tiles;
            public bool Metal, Flat;
            public CableTextureSpec Spec = new CableTextureSpec();

            public float Tiling => Tiles > 0f ? Tiles : 1f / (Flat ? 4f * Radius : 4f * (float)Math.PI * Radius);
        }

        /// <summary>The path to the Settings.cfg this build ships, or null if it isn't beside the tests.</summary>
        public static string SettingsPath
        {
            get
            {
                string p = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    @"..\..\..\..\..\KSPTethers\Settings.cfg");
                return System.IO.File.Exists(p) ? p : null;
            }
        }

        /// <summary>Every style the mod ships, read out of Settings.cfg.</summary>
        public static List<Style> Load(string settingsPath)
        {
            return Parse(System.IO.File.ReadAllText(settingsPath));
        }

        public static void Render(string settingsPath, string outFile)
        {
            List<Style> styles = Parse(System.IO.File.ReadAllText(settingsPath));
            const int width = 860, rowH = 120, pxPerMeter = 1500;
            using (var bmp = new Bitmap(width, rowH * styles.Count + 34))
            using (Graphics g = Graphics.FromImage(bmp))
            using (var font = new Font("Segoe UI", 10f, FontStyle.Bold))
            using (var small = new Font("Segoe UI", 8.5f))
            {
                g.Clear(Color.FromArgb(15, 18, 24));
                g.DrawString("Cable styles - offline render of the generated maps, approximating KSP/Bumped Specular",
                    small, Brushes.Silver, 6, 7);
                for (int s = 0; s < styles.Count; s++)
                {
                    Style st = styles[s];
                    Color32[] main, normal;
                    int w, h;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    CablePatterns.Generate(st.Spec, CablePatterns.DefaultAcross, out main, out normal, out w, out h);
                    Console.WriteLine("   " + st.Name.PadRight(12) + w + "x" + h + " generated in " +
                                      sw.Elapsed.TotalMilliseconds.ToString("F0") + " ms");
                    int top = 34 + s * rowH;
                    g.DrawString(st.Title, font, Brushes.Gainsboro, 8, top + 8);
                    // A 25 mm strap drawn at life size is a few pixels tall, so flat tethers are magnified.
                    float mag = st.Flat ? 3f : 1f;
                    g.DrawString(st.Name + " - " + st.Construction + ", " + (st.Radius * 2000f).ToString("F0") + " mm" +
                                 (st.Flat ? " wide, flat, shown 3x" : ""), small, Brushes.Gray, 8, top + 28);
                    DrawCable(bmp, st, main, normal, w, h, 250, width - 12, top + rowH / 2, pxPerMeter * mag, mag);
                }
                bmp.Save(outFile, ImageFormat.Png);
            }
            Console.WriteLine("Wrote " + outFile);
        }

        private static void DrawCable(Bitmap bmp, Style st, Color32[] main, Color32[] normal, int texW, int texH,
            int x0, int x1, int cy, float pxPerMeter, float magnification)
        {
            float rPx = st.Radius * pxPerMeter;
            float halfPx = rPx;
            // Light from the upper left, slightly toward the viewer; the viewer looks along -z.
            float lx = -0.45f, ly = 0.6f, lz = 0.66f;
            float ll = (float)Math.Sqrt(lx * lx + ly * ly + lz * lz);
            lx /= ll; ly /= ll; lz /= ll;
            float hx = lx, hy = ly, hz = lz + 1f;
            float hl = (float)Math.Sqrt(hx * hx + hy * hy + hz * hz);
            hx /= hl; hy /= hl; hz /= hl;
            float exponent = Math.Max(2f, st.Gloss / 100f * 128f);
            UColor tint = st.Metal
                ? new UColor(st.Spec.Colour.r * 0.9f + 0.1f, st.Spec.Colour.g * 0.9f + 0.1f, st.Spec.Colour.b * 0.9f + 0.1f)
                : new UColor(0.75f, 0.75f, 0.75f);

            // Each pixel is averaged over a few samples: the texture is far finer than the screen here,
            // which would otherwise alias into a scribble.
            int ss = st.Flat ? 3 : 2;
            float inv = 1f / (ss * ss);
            int yTop = Math.Max(0, (int)(cy - halfPx)), yBottom = Math.Min(bmp.Height - 1, (int)(cy + halfPx));
            for (int py = yTop; py <= yBottom; py++)
            {
                for (int px = x0; px < x1; px++)
                {
                    float sr = 0f, sg = 0f, sb = 0f;
                    for (int sy = 0; sy < ss; sy++)
                    {
                        float fy = py + (sy + 0.5f) / ss;
                        float across = (cy - fy) / halfPx;
                        if (Math.Abs(across) >= 1f)
                            continue;
                        float nyBase, nzBase, ty, tz, u;
                        if (st.Flat)
                        {
                            // A flat strap, face on: the texture folds round the perimeter, so the visible
                            // face is the first half of U and the edges sit at 0 and 0.5.
                            float tilt = across * st.Aspect;
                            nyBase = tilt;
                            nzBase = (float)Math.Sqrt(Math.Max(0f, 1f - tilt * tilt));
                            ty = nzBase;
                            tz = -tilt;
                            u = (across + 1f) * 0.25f;
                        }
                        else
                        {
                            float theta = (float)Math.Asin(across);
                            nyBase = (float)Math.Sin(theta);
                            nzBase = (float)Math.Cos(theta);
                            ty = (float)Math.Cos(theta);
                            tz = -(float)Math.Sin(theta);
                            u = (float)(theta / (2 * Math.PI) + 0.25);
                        }
                        for (int sx = 0; sx < ss; sx++)
                        {
                            float fx = px + (sx + 0.5f) / ss;
                            float v = (fx - x0) / pxPerMeter * st.Tiling;
                            int ix = Wrap((int)(u * texW), texW), iy = Wrap((int)(v * texH), texH);
                            Color32 a = main[iy * texW + ix], nm = normal[iy * texW + ix];
                            float tnx = nm.a / 255f * 2f - 1f, tny = nm.g / 255f * 2f - 1f;
                            float tnz = (float)Math.Sqrt(Math.Max(0f, 1f - tnx * tnx - tny * tny));
                            // world = T * tnx + B * tny + N * tnz, with B = +x (along the cable).
                            float wx = tny, wy = ty * tnx + nyBase * tnz, wz = tz * tnx + nzBase * tnz;
                            float wl = (float)Math.Sqrt(wx * wx + wy * wy + wz * wz);
                            wx /= wl; wy /= wl; wz /= wl;
                            float diff = Math.Max(0f, wx * lx + wy * ly + wz * lz);
                            float spec = (float)Math.Pow(Math.Max(0f, wx * hx + wy * hy + wz * hz), exponent) * (diff > 0 ? 1f : 0f);
                            float level = a.a / 255f;
                            float light = 0.2f + 0.9f * diff;
                            sr += a.r / 255f * light + spec * level * tint.r;
                            sg += a.g / 255f * light + spec * level * tint.g;
                            sb += a.b / 255f * light + spec * level * tint.b;
                        }
                    }
                    if (sr + sg + sb <= 0f && Math.Abs((cy - py) / halfPx) >= 1f)
                        continue;
                    bmp.SetPixel(px, py, Color.FromArgb(Channel(sr * inv), Channel(sg * inv), Channel(sb * inv)));
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

        // ---- a small reader for the slice of KSP's config format Settings.cfg uses -------------------

        private sealed class Node
        {
            public string Name = "";
            public readonly List<KeyValuePair<string, string>> Values = new List<KeyValuePair<string, string>>();
            public readonly List<Node> Nodes = new List<Node>();

            public Node Child(string name)
            {
                return Nodes.Find(n => n.Name == name);
            }

            public string Value(string key)
            {
                foreach (KeyValuePair<string, string> kv in Values)
                    if (kv.Key == key)
                        return kv.Value;
                return null;
            }
        }

        private static List<Style> Parse(string text)
        {
            Node root = ParseNodes(Lines(text), 0, out int _);
            var list = new List<Style>();
            Node settings = root.Child("KSP_TETHERS");
            if (settings == null)
                return list;
            foreach (Node n in settings.Nodes)
            {
                if (n.Name != "CABLE_STYLE")
                    continue;
                var st = new Style { Name = n.Value("name") ?? "", Title = n.Value("title") ?? n.Value("name") ?? "" };
                st.Construction = n.Value("construction") ?? n.Value("pattern") ?? "ribbed";
                st.Spec.Construction = CablePatterns.ParseConstruction(st.Construction, CableConstruction.Ribbed);
                Set(n.Value("radius"), v => st.Radius = v);
                Set(n.Value("aspect"), v => st.Aspect = v);
                Set(n.Value("gloss"), v => st.Gloss = v);
                Set(n.Value("tilesPerMeter"), v => st.Tiles = v);
                st.Metal = n.Value("metal") != null && CableTextureSpec.B(n.Value("metal"));
                st.Flat = n.Value("flat") != null && CableTextureSpec.B(n.Value("flat"));
                st.Spec.Flat = st.Flat;
                Node tex = n.Child("TEXTURE");
                if (tex != null)
                {
                    foreach (KeyValuePair<string, string> kv in tex.Values)
                        st.Spec.Set(kv.Key, kv.Value);
                    var stripes = new List<WeaveStripe>();
                    foreach (Node sn in tex.Nodes)
                    {
                        if (sn.Name != "STRIPE")
                            continue;
                        var ws = new WeaveStripe { Colour = CableTextureSpec.C(sn.Value("color") ?? "0,0,0,1") };
                        ws.From = CableTextureSpec.F(sn.Value("from") ?? "0");
                        ws.To = CableTextureSpec.F(sn.Value("to") ?? "0");
                        if (ws.To > ws.From)
                            stripes.Add(ws);
                    }
                    if (stripes.Count > 0)
                        st.Spec.Stripes = stripes.ToArray();
                }
                list.Add(st);
            }
            return list;
        }

        private static void Set(string raw, Action<float> assign)
        {
            if (!string.IsNullOrEmpty(raw))
                assign(float.Parse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture));
        }

        private static List<string> Lines(string text)
        {
            var lines = new List<string>();
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Split(new[] { "//" }, StringSplitOptions.None)[0].Trim();
                if (line.Length > 0)
                    lines.Add(line);
            }
            return lines;
        }

        /// <summary>Reads NAME / { / ... / } blocks from <paramref name="at"/> until the matching close brace.</summary>
        private static Node ParseNodes(List<string> lines, int at, out int next)
        {
            var node = new Node();
            int i = at;
            while (i < lines.Count)
            {
                string line = lines[i];
                if (line == "}")
                {
                    i++;
                    break;
                }
                int eq = line.IndexOf('=');
                if (eq > 0)
                {
                    node.Values.Add(new KeyValuePair<string, string>(line.Substring(0, eq).Trim(), line.Substring(eq + 1).Trim()));
                    i++;
                    continue;
                }
                if (line == "{")
                {
                    i++;
                    continue;
                }
                // A node name, with its brace on the next line.
                string name = line;
                if (i + 1 < lines.Count && lines[i + 1] == "{")
                {
                    Node child = ParseNodes(lines, i + 2, out int after);
                    child.Name = name;
                    node.Nodes.Add(child);
                    i = after;
                    continue;
                }
                i++;
            }
            next = i;
            return node;
        }
    }
}

using System;
using System.Globalization;
using UnityEngine;

namespace KSPTethers
{
    /// <summary>How a cable is made, which decides which generator draws its surface.</summary>
    internal enum CableConstruction { Ribbed, Woven, FlatWeb, Braid, WireRope, Smooth }

    /// <summary>Weave of a flat strap.</summary>
    internal enum WeavePattern { Twill, Plain, Herringbone }

    internal struct WeaveStripe
    {
        public float From, To;   // in thread units along the strap's width (0..Ny)
        public Color Colour;
    }

    /// <summary>
    /// Everything the surface generator needs for one cable style. Values come from a CABLE_STYLE node in
    /// Settings.cfg; the defaults here match the built-in styles.
    /// </summary>
    internal sealed class CableTextureSpec
    {
        public CableConstruction Construction = CableConstruction.Ribbed;
        public bool Flat;                 // a flat strap rather than a round cable
        public int Seed = 11;

        // Relief and baked shading.
        public float HeightScale = 40f;   // how deep the height field reads as a normal map
        public float AoRadius = 14f;      // blur radius (px at 1024) for the cavity term
        public float AoContrast = 3f;     // how dark cavities go
        public float AoHeight = 0.25f;    // extra shading from absolute height
        public float Spec = 0.5f;         // specular level before dirt and cavities

        public Color Colour = new Color(0.9f, 0.9f, 0.88f);
        public Color ColourB = new Color(0.78f, 0.79f, 0.8f);  // second strand set (braid)
        public Color SoilColour = new Color(0.52f, 0.49f, 0.44f);
        public float Soil = 0.45f;        // dirt / tarnish / grease strength
        public float Smudge = 0.25f;
        public float Tone = 0.07f;        // large-scale brightness variation

        // Helix / thread counts. Nu and Kv are turns per tile along and around the cable.
        public float Nu = 6f, Kv = 1f;
        public float Nx = 128f, Ny = 64f; // woven and flat straps: threads per tile
        public float Nw = 48f, Kw = 96f;  // wire rope: wires within a strand
        public float Fib = 0f;            // fibres per thread / per strand
        public float M = 7f;              // braid: filaments per carrier
        public float Lf = 4f;             // braid: lustre frequency

        // Ribbed hose.
        public float Fill = 0.985f, RibFlat = 0.55f, GrainH = 0.012f, StreakH = 0.006f, PitH = 0.15f;

        // Braid.
        public float EndH = 0.12f, FibH = 0f, Fuzz = 0f;

        // Flat strap.
        public WeavePattern Weave = WeavePattern.Twill;
        public float Hk = 8f;             // herringbone reversal period
        public float Edge = 0.06f;        // how far the weave fades toward the edge
        public float Selv = 0.05f;        // selvedge band width
        public float SelvDark = 0.12f;
        public WeaveStripe[] Stripes;

        public CableTextureSpec Clone()
        {
            return (CableTextureSpec)MemberwiseClone();
        }

        /// <summary>
        /// Applies one setting from a TEXTURE node. Kept here, working on plain strings, so the game and the
        /// offline renders read exactly the same keys. Returns false for a key this doesn't know.
        /// </summary>
        public bool Set(string key, string value)
        {
            if (string.IsNullOrEmpty(key) || value == null)
                return false;
            try
            {
                switch (key)
                {
                    case "seed": Seed = (int)F(value); return true;
                    case "flat": Flat = B(value); return true;
                    case "heightScale": HeightScale = F(value); return true;
                    case "aoRadius": AoRadius = F(value); return true;
                    case "aoContrast": AoContrast = F(value); return true;
                    case "aoHeight": AoHeight = F(value); return true;
                    case "spec": Spec = F(value); return true;
                    case "color": Colour = C(value); return true;
                    case "colorB": ColourB = C(value); return true;
                    case "soilColor": SoilColour = C(value); return true;
                    case "soil": Soil = F(value); return true;
                    case "smudge": Smudge = F(value); return true;
                    case "tone": Tone = F(value); return true;
                    case "turns": Nu = F(value); return true;
                    case "wraps": Kv = F(value); return true;
                    case "threadsAlong": Nx = F(value); return true;
                    case "threadsAcross": Ny = F(value); return true;
                    case "wires": Nw = F(value); return true;
                    case "wireLay": Kw = F(value); return true;
                    case "fibres": Fib = F(value); return true;
                    case "filaments": M = F(value); return true;
                    case "lustre": Lf = F(value); return true;
                    case "fill": Fill = F(value); return true;
                    case "ribFlat": RibFlat = F(value); return true;
                    case "grain": GrainH = F(value); return true;
                    case "streak": StreakH = F(value); return true;
                    case "pitting": PitH = F(value); return true;
                    case "strandRelief": EndH = F(value); return true;
                    case "fibreRelief": FibH = F(value); return true;
                    case "fuzz": Fuzz = F(value); return true;
                    case "weave": Weave = CablePatterns.ParseWeave(value, Weave); return true;
                    case "herringbonePeriod": Hk = F(value); return true;
                    case "edgeFade": Edge = F(value); return true;
                    case "selvedge": Selv = F(value); return true;
                    case "selvedgeDark": SelvDark = F(value); return true;
                    default: return false;
                }
            }
            catch (FormatException)
            {
                return false;
            }
        }

        internal static float F(string s)
        {
            return float.Parse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        internal static bool B(string s)
        {
            s = s.Trim();
            return s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1";
        }

        internal static Color C(string s)
        {
            string[] p = s.Split(',');
            if (p.Length < 3)
                return Color.white;
            return new Color(F(p[0]), F(p[1]), F(p[2]), p.Length > 3 ? F(p[3]) : 1f);
        }
    }

    /// <summary>
    /// Procedural cable surfaces: a corrugated hose, tubular webbing, a flat woven strap, a diamond braid and
    /// a lang-lay wire rope, each as a seamless tile two circumferences long. The generators produce a height
    /// field, a colour and a specular level per texel; <see cref="Generate"/> then turns those into the two
    /// maps KSP wants: KSP/Bumped Specular's _MainTex (albedo with cavity shading, specular level in alpha)
    /// and a normal map.
    ///
    /// The tile runs with U around the cable and V along it. A flat strap is drawn the same way and then
    /// folded around the perimeter, so its two faces mirror and the selvedge edges land on the strap's edges.
    ///
    /// Pure maths - no engine calls beyond Color/Color32 - so the tiling can be rendered and checked offline.
    /// </summary>
    internal static class CablePatterns
    {
        /// <summary>Texels around the cable; a tile is twice this along it.</summary>
        public const int DefaultAcross = 256;

        private struct Texel
        {
            public float H, R, G, B, S;
        }

        // ---- scalar helpers ------------------------------------------------------------------------

        private static float Hash3(int a, int b, int c)
        {
            unchecked
            {
                int h = (a * 374761393) ^ (b * 668265263) ^ (c * 1274126177);
                h = (h ^ (int)((uint)h >> 13)) * 1103515245;
                h ^= (int)((uint)h >> 16);
                return (uint)h / 4294967296f;
            }
        }

        private static int Mod(int a, int n)
        {
            int m = a % n;
            return m < 0 ? m + n : m;
        }

        private static float Fract(float x)
        {
            return x - (float)Math.Floor(x);
        }

        private static float Clamp(float x, float a, float b)
        {
            return x < a ? a : x > b ? b : x;
        }

        private static float Lerp(float a, float b, float t)
        {
            return a + (b - a) * t;
        }

        private static float SStep(float a, float b, float x)
        {
            float t = (x - a) / (b - a);
            t = t < 0f ? 0f : t > 1f ? 1f : t;
            return t * t * (3f - 2f * t);
        }

        private static int Gcd(int a, int b)
        {
            a = Math.Abs(a);
            b = Math.Abs(b);
            while (b != 0)
            {
                int t = a % b;
                a = b;
                b = t;
            }
            return a == 0 ? 1 : a;
        }

        /// <summary>Round cross-section of a strand or thread of width <paramref name="w"/> within its cell.</summary>
        private static float Tube(float f, float w)
        {
            float g = (f - 0.5f) / (w * 0.5f);
            return g >= 1f || g <= -1f ? 0f : (float)Math.Sqrt(1f - g * g);
        }

        /// <summary>Value noise that tiles over integer periods in both axes.</summary>
        private static float Vn2(float x, float y, int px, int py, int s)
        {
            int xi = (int)Math.Floor(x), yi = (int)Math.Floor(y);
            float fx = x - xi, fy = y - yi;
            int x0 = Mod(xi, px), x1 = Mod(xi + 1, px), y0 = Mod(yi, py), y1 = Mod(yi + 1, py);
            float ux = fx * fx * (3f - 2f * fx), uy = fy * fy * (3f - 2f * fy);
            float a = Hash3(x0, y0, s), b = Hash3(x1, y0, s), c = Hash3(x0, y1, s), d = Hash3(x1, y1, s);
            return a + (b - a) * ux + (c - a) * uy + (a - b - c + d) * ux * uy;
        }

        private static float Fbm(float u, float v, int px, int py, int oct, int s)
        {
            float sum = 0f, amp = 1f, n = 0f;
            for (int o = 0; o < oct; o++)
            {
                sum += amp * Vn2(u * px, v * py, px, py, s + o * 101);
                n += amp;
                amp *= 0.5f;
                px *= 2;
                py *= 2;
            }
            return sum / n;
        }

        /// <summary>One-dimensional tiling value noise, used for per-thread lustre and length variation.</summary>
        private static float Vn1(float x, int p, int s)
        {
            int xi = (int)Math.Floor(x);
            float f = x - xi;
            float a = Hash3(Mod(xi, p), 7, s), b = Hash3(Mod(xi + 1, p), 7, s);
            return a + (b - a) * f * f * (3f - 2f * f);
        }

        // ---- the five constructions ----------------------------------------------------------------

        /// <summary>Corrugated hose: a helical rib, with grain, streaking and pitting.</summary>
        private static void Ribbed(CableTextureSpec o, float u, float v, ref Texel t)
        {
            float p = u * o.Nu + v * o.Kv;
            float f = p - (float)Math.Floor(p);
            float r = (float)Math.Pow(Tube(f, o.Fill), o.RibFlat);
            float grain = Fbm(u, v, 256, 128, 3, o.Seed);
            float big = Fbm(u, v, 8, 4, 3, o.Seed + 7);
            float streak = Fbm(u, v, 4, 96, 3, o.Seed + 13);
            float pits = Fbm(u, v, 128, 64, 2, o.Seed + 29);
            t.H = r + (grain - 0.5f) * o.GrainH + (streak - 0.5f) * o.StreakH - Math.Max(0f, pits - 0.78f) * o.PitH;

            float groove = (float)Math.Pow(1f - r, 2.5);
            float dirt = Clamp(groove * (0.4f + big) * o.Soil + Math.Max(0f, big - 0.72f) * o.Smudge, 0f, 1f);
            float k = 1f - o.Tone + o.Tone * (0.55f * big + 0.45f * grain);
            t.R = Lerp(o.Colour.r * k, o.SoilColour.r, dirt);
            t.G = Lerp(o.Colour.g * k, o.SoilColour.g, dirt);
            t.B = Lerp(o.Colour.b * k, o.SoilColour.b, dirt);
            t.S = o.Spec * (0.65f + 0.35f * grain) * (0.75f + 0.25f * streak) * (1f - dirt * 0.8f);
        }

        private static int Twill(int a, int b)
        {
            return ((a + b) & 3) < 2 ? 1 : 0;
        }

        /// <summary>Tubular webbing in a 2/2 twill: warp along the cable, weft around it.</summary>
        private static void Woven(CableTextureSpec o, float u, float v, ref Texel t)
        {
            int fibN = Math.Max(1, (int)o.Fib);
            float X = u * o.Nx, Y = v * o.Ny;
            int i = (int)Math.Floor(X), j = (int)Math.Floor(Y);
            float fx = X - i, fy = Y - j;
            float ew = fx < 0.5f
                ? Lerp(Twill(i - 1, j), Twill(i, j), SStep(-0.5f, 0.5f, fx))
                : Lerp(Twill(i, j), Twill(i + 1, j), SStep(0.5f, 1.5f, fx));
            float ef = 1f - (fy < 0.5f
                ? Lerp(Twill(i, j - 1), Twill(i, j), SStep(-0.5f, 0.5f, fy))
                : Lerp(Twill(i, j), Twill(i, j + 1), SStep(0.5f, 1.5f, fy)));
            float tw = Tube(fy, 0.9f), tf = Tube(fx, 0.9f);
            float mw = fy * fibN, mf = fx * fibN;
            int kw = (int)Math.Floor(mw), kf = (int)Math.Floor(mf);
            int idW = j * fibN + kw, idF = 4000 + i * fibN + kf;
            float slW = Vn1(u * o.Nx * 2f, (int)(o.Nx * 2f), o.Seed + idW);
            float slF = Vn1(v * o.Ny * 4f, (int)(o.Ny * 4f), o.Seed + idF);
            float hw = tw > 0f ? 0.3f * (float)Math.Sqrt(tw) + 0.6f * ew + 0.07f * Tube(mw - kw, 1f) + 0.04f * slW : -1f;
            float hf = tf > 0f ? 0.3f * (float)Math.Sqrt(tf) + 0.6f * ef + 0.07f * Tube(mf - kf, 1f) + 0.04f * slF : -1f;
            float fuzz = Fbm(u, v, 512, 256, 2, o.Seed + 3);
            float big = Fbm(u, v, 6, 3, 3, o.Seed + 9);
            t.H = Math.Max(Math.Max(hw, hf), 0f) + (fuzz - 0.5f) * 0.05f;

            float br, thr;
            if (hw >= hf)
            {
                br = 0.92f + 0.08f * Hash3(idW, 1, o.Seed);
                thr = 0.95f + 0.05f * Hash3(j, 2, o.Seed);
                br *= 0.9f + 0.1f * slW;
            }
            else
            {
                br = 0.92f + 0.08f * Hash3(idF, 1, o.Seed);
                thr = 0.95f + 0.05f * Hash3(i, 3, o.Seed);
                br *= 0.9f + 0.1f * slF;
            }
            float gap = Math.Max(hw, hf) < 0f ? 0.55f : 1f;
            float k = br * thr * gap * (0.94f + 0.06f * fuzz) * (0.96f + 0.04f * big);
            float soil = Math.Max(0f, big - 0.62f) * 0.5f;
            t.R = Lerp(o.Colour.r * k, o.SoilColour.r, soil);
            t.G = Lerp(o.Colour.g * k, o.SoilColour.g, soil);
            t.B = Lerp(o.Colour.b * k, o.SoilColour.b, soil);
            t.S = o.Spec * (0.6f + 0.4f * fuzz) * gap;
        }

        /// <summary>Flat strap webbing: selvedge edges, optional woven-in stripes, and a weave that fades at the edge.</summary>
        private static void FlatWeb(CableTextureSpec o, float u, float v, ref Texel t)
        {
            int fibN = Math.Max(1, (int)o.Fib);
            int hk = Math.Max(1, (int)o.Hk);
            float X = u * o.Nx, Y = v * o.Ny;
            int i = (int)Math.Floor(X), j = (int)Math.Floor(Y);
            float fx = X - i, fy = Y - j;
            float ew = fx < 0.5f
                ? Lerp(Over(o, i - 1, j, hk), Over(o, i, j, hk), SStep(-0.5f, 0.5f, fx))
                : Lerp(Over(o, i, j, hk), Over(o, i + 1, j, hk), SStep(0.5f, 1.5f, fx));
            float ef = 1f - (fy < 0.5f
                ? Lerp(Over(o, i, j - 1, hk), Over(o, i, j, hk), SStep(-0.5f, 0.5f, fy))
                : Lerp(Over(o, i, j, hk), Over(o, i, j + 1, hk), SStep(0.5f, 1.5f, fy)));
            float tw = Tube(fy, 0.94f), tf = Tube(fx, 0.8f);
            float mw = fy * fibN, mf = fx * fibN;
            int kw = (int)Math.Floor(mw), kf = (int)Math.Floor(mf);
            int idW = j * fibN + kw, idF = 4000 + i * fibN + kf;
            float slW = Vn1(u * o.Nx * 2f, (int)(o.Nx * 2f), o.Seed + idW);
            float slF = Vn1(v * o.Ny * 4f, (int)(o.Ny * 4f), o.Seed + idF);
            float hw = tw > 0f ? 0.32f * (float)Math.Sqrt(tw) + 0.6f * ew + 0.07f * Tube(mw - kw, 1f) + 0.04f * slW : -1f;
            float hf = tf > 0f ? 0.25f * (float)Math.Sqrt(tf) + 0.5f * ef + 0.05f * Tube(mf - kf, 1f) + 0.03f * slF : -1f;
            float fuzz = Fbm(u, v, 512, 256, 2, o.Seed + 3);
            float big = Fbm(u, v, 6, 3, 3, o.Seed + 9);

            float d = Math.Min(v, 1f - v);
            float edge = (float)Math.Sqrt(SStep(0f, o.Edge, d));
            float selv = 1f - SStep(o.Selv * 0.6f, o.Selv, d);
            t.H = (Math.Max(Math.Max(hw, hf), 0f) + (fuzz - 0.5f) * 0.05f) * (0.3f + 0.7f * edge) + selv * 0.18f * edge;

            float br;
            Color col = o.Colour;
            if (hw >= hf)
            {
                br = (0.92f + 0.08f * Hash3(idW, 1, o.Seed)) * (0.95f + 0.05f * Hash3(j, 2, o.Seed)) * (0.9f + 0.1f * slW);
                if (o.Stripes != null)
                {
                    for (int k2 = 0; k2 < o.Stripes.Length; k2++)
                    {
                        if (Y >= o.Stripes[k2].From && Y < o.Stripes[k2].To)
                            col = o.Stripes[k2].Colour;
                    }
                }
            }
            else
            {
                br = (0.86f + 0.08f * Hash3(idF, 1, o.Seed)) * (0.9f + 0.1f * slF);
            }
            float gap = Math.Max(hw, hf) < 0f ? 0.55f : 1f;
            float k = br * gap * (0.94f + 0.06f * fuzz) * (0.96f + 0.04f * big) * (0.75f + 0.25f * edge) * (1f - selv * o.SelvDark);
            float soil = Math.Max(0f, big - 0.62f) * o.Soil;
            t.R = Lerp(col.r * k, o.SoilColour.r, soil);
            t.G = Lerp(col.g * k, o.SoilColour.g, soil);
            t.B = Lerp(col.b * k, o.SoilColour.b, soil);
            t.S = o.Spec * (0.6f + 0.4f * fuzz) * gap * edge;
        }

        private static float Over(CableTextureSpec o, int a, int b, int hk)
        {
            switch (o.Weave)
            {
                case WeavePattern.Plain:
                    return (a + b) & 1;
                case WeavePattern.Herringbone:
                    return ((int)Math.Floor((double)b / hk) & 1) != 0 ? Twill(-a, b) : Twill(a, b);
                default:
                    return Twill(a, b);
            }
        }

        /// <summary>Sixteen-carrier diamond braid: two sets of strands spiralling opposite ways, over and under.</summary>
        private static void Braid(CableTextureSpec o, float u, float v, ref Texel t)
        {
            int g = Gcd((int)o.Nu, (int)o.Kv);
            int m = Math.Max(1, (int)o.M);
            int lf = Math.Max(1, (int)o.Lf);
            float a = u * o.Nu + v * o.Kv, b = u * o.Nu - v * o.Kv;
            int ia = (int)Math.Floor(a), ib = (int)Math.Floor(b);
            float fa = a - ia, fb = b - ib;
            float eA = 0.5f + 0.5f * (float)Math.Sin(Math.PI / 2.0 * (b + ia));
            float eB = 0.5f + 0.5f * (float)Math.Sin(Math.PI / 2.0 * (a + ib + 2));
            float pA = Tube(fa, 0.95f), pB = Tube(fb, 0.95f);
            float mA = fa * m, mB = fb * m;
            int kA = (int)Math.Floor(mA), kB = (int)Math.Floor(mB);
            int idA = Mod(ia, g) * m + kA, idB = 1000 + Mod(ib, g) * m + kB;
            float lenA = Vn1(b * lf, g * lf, o.Seed + idA * 3);
            float lenB = Vn1(a * lf, g * lf, o.Seed + idB * 3);
            float fibA = o.Fib > 0f ? Tube(Fract(mA * o.Fib), 1f) : 0f;
            float fibB = o.Fib > 0f ? Tube(Fract(mB * o.Fib), 1f) : 0f;
            float hA = pA > 0f
                ? 0.22f * (float)Math.Pow(pA, 0.35) + 0.55f * eA + o.EndH * (float)Math.Sqrt(Tube(mA - kA, 1f)) + o.FibH * fibA + 0.03f * lenA
                : -1f;
            float hB = pB > 0f
                ? 0.22f * (float)Math.Pow(pB, 0.35) + 0.55f * eB + o.EndH * (float)Math.Sqrt(Tube(mB - kB, 1f)) + o.FibH * fibB + 0.03f * lenB
                : -1f;
            float fuzz = o.Fuzz > 0f ? Fbm(u, v, 512, 256, 2, o.Seed + 5) : 0.5f;
            float big = Fbm(u, v, 8, 4, 3, o.Seed + 11);
            t.H = Math.Max(Math.Max(hA, hB), 0f) + (fuzz - 0.5f) * o.Fuzz;

            Color col;
            int id;
            float len;
            if (hA >= hB)
            {
                col = o.Colour;
                id = idA;
                len = lenA;
            }
            else
            {
                col = o.ColourB;
                id = idB;
                len = lenB;
            }
            float gap = Math.Max(hA, hB) < 0f ? 0.35f : 1f;
            float k = (0.88f + 0.12f * Hash3(id, 1, o.Seed)) * (0.9f + 0.1f * len) * gap * (1f - o.Tone + o.Tone * big);
            float tarn = Math.Max(0f, big - 0.6f) * o.Soil;
            t.R = Lerp(col.r * k, o.SoilColour.r, tarn);
            t.G = Lerp(col.g * k, o.SoilColour.g, tarn);
            t.B = Lerp(col.b * k, o.SoilColour.b, tarn);
            t.S = o.Spec * (0.75f + 0.25f * len) * gap * (1f - tarn);
        }

        /// <summary>Six-strand lang-lay wire rope: each strand is a bundle of finer wires, with grease in the valleys.</summary>
        private static void WireRope(CableTextureSpec o, float u, float v, ref Texel t)
        {
            int g = Gcd((int)o.Nu, (int)o.Kv), gw = Gcd((int)o.Nw, (int)o.Kw);
            float s = u * o.Nu + v * o.Kv;
            int ist = (int)Math.Floor(s);
            float fs = s - ist;
            float ps = Tube(fs, 0.985f), psh = (float)Math.Pow(ps, 0.6);
            float off = Hash3(Mod(ist, g), 1, o.Seed);
            float w = u * o.Nw + v * o.Kw + off * 5f;
            int iw = (int)Math.Floor(w);
            float fw = w - iw;
            float wire = (float)Math.Pow(Tube(fw, 0.95f), 0.5);
            int wid = Mod(ist, g) * 1000 + Mod(iw, gw);
            float grain = Fbm(u, v, 64, 256, 3, o.Seed + 3);
            float big = Fbm(u, v, 8, 4, 3, o.Seed + 9);
            float scr = Vn1(u * o.Nw * 8f, (int)(o.Nw * 8f), o.Seed + wid);
            t.H = ps > 0f ? 0.72f * psh + 0.28f * wire * (float)Math.Pow(ps, 0.3) + (grain - 0.5f) * 0.02f : 0f;

            float grease = Clamp((1f - psh) * 1.3f - 0.25f + (big - 0.5f) * 0.7f + (1f - wire) * 0.15f, 0f, 1f);
            float k = (0.84f + 0.16f * Hash3(wid, 2, o.Seed)) * (0.9f + 0.1f * grain) * (0.95f + 0.05f * scr);
            t.R = Lerp(o.Colour.r * k, o.SoilColour.r, grease);
            t.G = Lerp(o.Colour.g * k, o.SoilColour.g, grease);
            t.B = Lerp(o.Colour.b * k, o.SoilColour.b, grease);
            t.S = o.Spec * (1f - grease * 0.9f) * (0.55f + 0.45f * wire) * (0.85f + 0.15f * scr);
        }

        private static void Smooth(CableTextureSpec o, float u, float v, ref Texel t)
        {
            float grain = Fbm(u, v, 256, 128, 3, o.Seed);
            float big = Fbm(u, v, 8, 4, 3, o.Seed + 7);
            t.H = 0.5f + (grain - 0.5f) * 0.05f;
            float k = 1f - o.Tone + o.Tone * (0.55f * big + 0.45f * grain);
            float soil = Math.Max(0f, big - 0.7f) * o.Soil;
            t.R = Lerp(o.Colour.r * k, o.SoilColour.r, soil);
            t.G = Lerp(o.Colour.g * k, o.SoilColour.g, soil);
            t.B = Lerp(o.Colour.b * k, o.SoilColour.b, soil);
            t.S = o.Spec * (0.85f + 0.15f * grain);
        }

        /// <summary>Samples one texel of the tile. u runs along the cable, v around it (or across a flat strap).</summary>
        public static void Sample(CableTextureSpec o, float u, float v, out float h, out float r, out float g, out float b, out float s)
        {
            var t = new Texel();
            switch (o.Construction)
            {
                case CableConstruction.Woven: Woven(o, u, v, ref t); break;
                case CableConstruction.FlatWeb: FlatWeb(o, u, v, ref t); break;
                case CableConstruction.Braid: Braid(o, u, v, ref t); break;
                case CableConstruction.WireRope: WireRope(o, u, v, ref t); break;
                case CableConstruction.Smooth: Smooth(o, u, v, ref t); break;
                default: Ribbed(o, u, v, ref t); break;
            }
            h = t.H;
            r = t.R;
            g = t.G;
            b = t.B;
            s = t.S;
        }

        // ---- maps ----------------------------------------------------------------------------------

        /// <summary>Separable box blur that wraps in both axes, so blurring never breaks the tiling.</summary>
        private static float[] BlurWrap(float[] src, int w, int h, int r, int passes)
        {
            var a = (float[])src.Clone();
            var t = new float[w * h];
            float n = 2 * r + 1;
            for (int p = 0; p < passes; p++)
            {
                for (int y = 0; y < h; y++)
                {
                    int o = y * w;
                    float s = 0f;
                    for (int k = -r; k <= r; k++)
                        s += a[o + Mod(k, w)];
                    for (int x = 0; x < w; x++)
                    {
                        t[o + x] = s / n;
                        s += a[o + Mod(x + r + 1, w)] - a[o + Mod(x - r, w)];
                    }
                }
                for (int x = 0; x < w; x++)
                {
                    float s = 0f;
                    for (int k = -r; k <= r; k++)
                        s += t[Mod(k, h) * w + x];
                    for (int y = 0; y < h; y++)
                    {
                        a[y * w + x] = s / n;
                        s += t[Mod(y + r + 1, h) * w + x] - t[Mod(y - r, h) * w + x];
                    }
                }
            }
            return a;
        }

        /// <summary>A flat strap's tile is folded around the perimeter: v 0 -> 1 -> 0 over one trip around.</summary>
        private static float FoldAcross(float t)
        {
            return t < 0.5f ? 2f * t : 2f * (1f - t);
        }

        /// <summary>
        /// Builds the two maps for a style. <paramref name="across"/> is the texel count around the cable;
        /// the tile is twice that long, and a flat strap is twice as wide again because both faces are drawn.
        /// </summary>
        public static void Generate(CableTextureSpec spec, int across, out Color32[] main, out Color32[] normal,
            out int width, out int height)
        {
            across = Mathf.Clamp(across, 32, 1024);
            width = spec.Flat ? across * 2 : across;   // around the cable (U)
            height = across * 2;                       // along the cable (V)
            int n = width * height;
            // Feature sizes in the generator are quoted in pixels of a 1024-long tile.
            float sc = height / 1024f;

            var h = new float[n];
            var col = new float[n * 3];
            var sp = new float[n];
            var t = new Texel();
            for (int y = 0; y < height; y++)
            {
                float along = (y + 0.5f) / height;
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    float around = (x + 0.5f) / width;
                    float v = spec.Flat ? FoldAcross(around) : around;
                    switch (spec.Construction)
                    {
                        case CableConstruction.Woven: Woven(spec, along, v, ref t); break;
                        case CableConstruction.FlatWeb: FlatWeb(spec, along, v, ref t); break;
                        case CableConstruction.Braid: Braid(spec, along, v, ref t); break;
                        case CableConstruction.WireRope: WireRope(spec, along, v, ref t); break;
                        case CableConstruction.Smooth: Smooth(spec, along, v, ref t); break;
                        default: Ribbed(spec, along, v, ref t); break;
                    }
                    int i = row + x;
                    h[i] = t.H;
                    col[i * 3] = t.R;
                    col[i * 3 + 1] = t.G;
                    col[i * 3 + 2] = t.B;
                    sp[i] = t.S;
                }
            }

            float[] hs = BlurWrap(h, width, height, Math.Max(1, (int)Math.Round(sc)), 1);
            float[] hb = BlurWrap(h, width, height, Math.Max(1, (int)Math.Round(spec.AoRadius * sc)), 2);
            float k = spec.HeightScale * sc * 0.5f;

            main = new Color32[n];
            normal = new Color32[n];
            for (int y = 0; y < height; y++)
            {
                int row = y * width;
                int up = Mod(y + 1, height) * width, down = Mod(y - 1, height) * width;
                for (int x = 0; x < width; x++)
                {
                    int i = row + x;
                    int xl = Mod(x - 1, width), xr = Mod(x + 1, width);
                    // U runs around the cable, V along it; the height field is in the same frame as the mesh.
                    float du = (hs[row + xr] - hs[row + xl]) * k;
                    float dv = (hs[up + x] - hs[down + x]) * k;
                    float nx = -du, ny = -dv;   // z is 1 before normalising: a height field's normal
                    float inv = 1f / (float)Math.Sqrt(nx * nx + ny * ny + 1f);
                    nx *= inv;
                    ny *= inv;

                    float cav = Clamp(1f - Math.Max(0f, hb[i] - h[i]) * spec.AoContrast, 0.3f, 1f);
                    float ao = cav * (1f - spec.AoHeight + spec.AoHeight * Clamp(h[i], 0f, 1f));
                    byte r = ToByte(col[i * 3] * ao);
                    byte g = ToByte(col[i * 3 + 1] * ao);
                    byte b = ToByte(col[i * 3 + 2] * ao);
                    byte s = ToByte(Clamp(sp[i] * ao * ao, 0f, 1f));
                    // KSP/Bumped Specular: albedo in RGB, specular level in alpha.
                    main[i] = new Color32(r, g, b, s);
                    // Unity's desktop normal format (DXT5nm): x in alpha, y in green.
                    byte bx = ToByte(nx * 0.5f + 0.5f);
                    byte by = ToByte(ny * 0.5f + 0.5f);
                    normal[i] = new Color32(255, by, by, bx);
                }
            }
        }

        private static byte ToByte(float v)
        {
            int i = (int)(v * 255f + 0.5f);
            return (byte)(i < 0 ? 0 : i > 255 ? 255 : i);
        }

        public static CableConstruction ParseConstruction(string s, CableConstruction fallback)
        {
            if (string.IsNullOrEmpty(s))
                return fallback;
            switch (s.Trim().ToLowerInvariant())
            {
                case "ribbed": return CableConstruction.Ribbed;
                case "woven": return CableConstruction.Woven;
                case "flat":
                case "flatweb":
                case "strap": return CableConstruction.FlatWeb;
                case "braid":
                case "braided": return CableConstruction.Braid;
                case "rope":
                case "wirerope":
                case "twisted": return CableConstruction.WireRope;
                case "smooth": return CableConstruction.Smooth;
                default: return fallback;
            }
        }

        public static WeavePattern ParseWeave(string s, WeavePattern fallback)
        {
            if (string.IsNullOrEmpty(s))
                return fallback;
            switch (s.Trim().ToLowerInvariant())
            {
                case "plain": return WeavePattern.Plain;
                case "herring":
                case "herringbone": return WeavePattern.Herringbone;
                case "twill": return WeavePattern.Twill;
                default: return fallback;
            }
        }
    }
}

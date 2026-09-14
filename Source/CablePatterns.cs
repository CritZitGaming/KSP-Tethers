using System;
using UnityEngine;

namespace KSPTethers
{
    internal enum CablePattern { Ribbed, Braided, Woven, Twisted, Smooth }

    /// <summary>
    /// Seamlessly tiling surface patterns for cables. U runs around the circumference and V along the cable;
    /// every pattern is a function of phases with integer frequencies in both, so tiles wrap without seams.
    /// Pure math (no engine calls) so the tiling can be tested outside Unity.
    /// </summary>
    internal static class CablePatterns
    {
        public const int Size = 128;

        /// <summary>Height field in 0..1 for the pattern at (u, v) in tile space.</summary>
        public static float Height(CablePattern pattern, float u, float v)
        {
            switch (pattern)
            {
                case CablePattern.Ribbed:
                {
                    // Corrugated hose: one helical rib per turn, 4 ribs per tile.
                    return Rib(4f * v + u);
                }
                case CablePattern.Braided:
                {
                    // Braided sleeve: two sets of strands in opposite helices. Each strand rises and dips
                    // smoothly as it passes over and under the other set (no height jumps, so no harsh normals).
                    float p1 = 8f * u + 4f * v, p2 = -8f * u + 4f * v;
                    float over1 = 0.7f + 0.3f * (float)Math.Sin(Math.PI * p2);
                    float over2 = 0.7f - 0.3f * (float)Math.Sin(Math.PI * p1);
                    return 0.35f + 0.65f * Math.Max(Strand(p1) * over1, Strand(p2) * over2);
                }
                case CablePattern.Woven:
                {
                    // Fine plain-weave fabric webbing: warp along the cable, weft around it, over-under.
                    float pu = 24f * u, pv = 24f * v;
                    float warp = Strand(pu) * (0.7f + 0.3f * (float)Math.Sin(Math.PI * pv));
                    float weft = Strand(pv) * (0.7f - 0.3f * (float)Math.Sin(Math.PI * pu));
                    return 0.55f + 0.45f * Math.Max(warp, weft);
                }
                case CablePattern.Twisted:
                {
                    // Wire rope: six strands laid in a helix, each made of finer wires twisted the other way.
                    float strand = Strand(6f * u + 2f * v);
                    float wires = Strand(36f * u - 18f * v);
                    return strand * (0.85f + 0.15f * wires);
                }
                default:
                    return 0.5f;
            }
        }

        private static float Rib(float phase)
        {
            float f = phase - (float)Math.Floor(phase);
            return (float)Math.Pow(Math.Sin(Math.PI * f), 0.6);
        }

        private static float Strand(float phase)
        {
            // Round strand cross-section with V-shaped grooves (finite slopes, so no sparkling normals).
            float f = phase - (float)Math.Floor(phase);
            return 4f * f * (1f - f);
        }

        private static float Hash(int x, int y)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263);
                h = (h ^ (h >> 13)) * 1274126177u;
                return ((h ^ (h >> 16)) & 0xFFFF) / 65535f;
            }
        }

        /// <summary>
        /// Greyscale albedo (tinted by the material colour) and a DXT5nm-packed normal map
        /// (x in alpha, y in green) as Unity's UnpackNormal expects on desktop.
        /// </summary>
        public static void Generate(CablePattern pattern, float bump, out Color32[] albedo, out Color32[] normal)
        {
            albedo = new Color32[Size * Size];
            normal = new Color32[Size * Size];
            const float eps = 1f / (Size * 4f);
            float contrast = pattern == CablePattern.Smooth ? 0f : pattern == CablePattern.Woven ? 0.12f : 0.22f;
            float noise = pattern == CablePattern.Woven ? 0.06f : 0.04f;

            for (int y = 0; y < Size; y++)
            {
                float v = (y + 0.5f) / Size;
                for (int x = 0; x < Size; x++)
                {
                    float u = (x + 0.5f) / Size;
                    float h = Height(pattern, u, v);
                    float dhdu = (Height(pattern, u + eps, v) - Height(pattern, u - eps, v)) / (2f * eps);
                    float dhdv = (Height(pattern, u, v + eps) - Height(pattern, u, v - eps)) / (2f * eps);

                    float g = 1f - contrast + contrast * h + (Hash(x, y) - 0.5f) * noise;
                    byte gb = (byte)(Mathf.Clamp01(g) * 255f);
                    albedo[y * Size + x] = new Color32(gb, gb, gb, 255);

                    // Slopes per tile unit; relief is exaggerated a little so it reads at normal viewing distances.
                    float nx = -dhdu * bump * 0.06f, ny = -dhdv * bump * 0.06f;
                    float inv = 1f / (float)Math.Sqrt(nx * nx + ny * ny + 1f);
                    nx *= inv;
                    ny *= inv;
                    byte bx = (byte)Mathf.Clamp(Mathf.RoundToInt((nx * 0.5f + 0.5f) * 255f), 0, 255);
                    byte by = (byte)Mathf.Clamp(Mathf.RoundToInt((ny * 0.5f + 0.5f) * 255f), 0, 255);
                    normal[y * Size + x] = new Color32(255, by, by, bx);
                }
            }
        }

        public static CablePattern Parse(string s, CablePattern fallback)
        {
            if (string.IsNullOrEmpty(s))
                return fallback;
            try
            {
                return (CablePattern)Enum.Parse(typeof(CablePattern), s.Trim(), true);
            }
            catch
            {
                return fallback;
            }
        }
    }
}

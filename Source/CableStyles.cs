using System.Collections.Generic;
using UnityEngine;

namespace KSPTethers
{
    /// <summary>How a cable looks and how stiff it is. Defined by CABLE_STYLE nodes in Settings.cfg.</summary>
    internal sealed class CableStyle
    {
        public string Name = "umbilical";
        public string Title = "White Umbilical";
        public string Description = "";
        public CablePattern Pattern = CablePattern.Ribbed;
        public Color Color = new Color(0.94f, 0.94f, 0.92f, 1f);
        public Color Specular = new Color(0.28f, 0.28f, 0.28f, 1f);
        public Color FittingColor = new Color(0.55f, 0.57f, 0.60f, 1f);
        public float Shininess = 0.12f;
        public float Radius = 0.022f;
        public float TilesPerMeter = 7f;
        public float Bump = 1.4f;
        public float BendStiffness = 0.05f;
        public float MinBendRadius = 0.45f;
        public string TexturePath = "";
        public string NormalPath = "";

        private Material rope;
        private Material fitting;

        public Material RopeMaterial
        {
            get { if (rope == null) rope = CreateRope(); return rope; }
        }

        public Material FittingMaterial
        {
            get { if (fitting == null) fitting = CreateFitting(); return fitting; }
        }

        public static CableStyle FromNode(ConfigNode n)
        {
            var s = new CableStyle();
            n.TryGetValue("name", ref s.Name);
            s.Title = s.Name;
            n.TryGetValue("title", ref s.Title);
            n.TryGetValue("description", ref s.Description);
            s.Pattern = CablePatterns.Parse(n.GetValue("pattern"), CablePattern.Ribbed);
            n.TryGetValue("color", ref s.Color);
            n.TryGetValue("specular", ref s.Specular);
            n.TryGetValue("fittingColor", ref s.FittingColor);
            n.TryGetValue("shininess", ref s.Shininess);
            n.TryGetValue("radius", ref s.Radius);
            n.TryGetValue("tilesPerMeter", ref s.TilesPerMeter);
            n.TryGetValue("bump", ref s.Bump);
            n.TryGetValue("bendStiffness", ref s.BendStiffness);
            n.TryGetValue("minBendRadius", ref s.MinBendRadius);
            n.TryGetValue("texture", ref s.TexturePath);
            n.TryGetValue("normalMap", ref s.NormalPath);
            s.Radius = Mathf.Clamp(s.Radius, 0.003f, 0.2f);
            s.TilesPerMeter = Mathf.Clamp(s.TilesPerMeter, 0.1f, 200f);
            s.Bump = Mathf.Clamp(s.Bump, 0f, 5f);
            s.BendStiffness = Mathf.Clamp01(s.BendStiffness);
            s.MinBendRadius = Mathf.Clamp(s.MinBendRadius, 0f, 5f);
            return s;
        }

        private Material CreateRope()
        {
            Shader shader = CableStyles.FindShader("KSP/Bumped Specular", "KSP/Specular", "KSP/Diffuse", "Standard", "Diffuse");
            var m = new Material(shader) { name = "KSPTethers-" + Name };
            Texture2D albedo = null, normal = null;
            if (!string.IsNullOrEmpty(TexturePath) && GameDatabase.Instance.ExistsTexture(TexturePath))
                albedo = GameDatabase.Instance.GetTexture(TexturePath, false);
            if (!string.IsNullOrEmpty(NormalPath) && GameDatabase.Instance.ExistsTexture(NormalPath))
                normal = GameDatabase.Instance.GetTexture(NormalPath, true);
            if (albedo == null)
                albedo = CableStyles.PatternAlbedo(Pattern);
            if (normal == null && string.IsNullOrEmpty(TexturePath))
                normal = CableStyles.PatternNormal(Pattern, Bump);
            SetTexture(m, "_MainTex", albedo);
            if (normal != null && m.HasProperty("_BumpMap"))
            {
                m.SetTexture("_BumpMap", normal);
                m.EnableKeyword("_NORMALMAP");
            }
            SetColor(m, "_Color", Color);
            SetColor(m, "_SpecColor", Specular);
            SetFloat(m, "_Shininess", Shininess);
            SetFloat(m, "_Glossiness", Mathf.Clamp01(Shininess * 1.5f));
            SetFloat(m, "_Opacity", 1f);
            return m;
        }

        private Material CreateFitting()
        {
            Shader shader = CableStyles.FindShader("KSP/Specular", "KSP/Diffuse", "Standard", "Diffuse");
            var m = new Material(shader) { name = "KSPTethers-" + Name + "-Fitting" };
            SetTexture(m, "_MainTex", CableStyles.White);
            SetColor(m, "_Color", FittingColor);
            SetColor(m, "_SpecColor", new Color(0.8f, 0.8f, 0.8f, 1f));
            SetFloat(m, "_Shininess", 0.55f);
            SetFloat(m, "_Glossiness", 0.7f);
            SetFloat(m, "_Metallic", 0.8f);
            SetFloat(m, "_Opacity", 1f);
            return m;
        }

        private static void SetTexture(Material m, string prop, Texture t)
        {
            if (t != null && m.HasProperty(prop)) m.SetTexture(prop, t);
        }

        private static void SetColor(Material m, string prop, Color c)
        {
            if (m.HasProperty(prop)) m.SetColor(prop, c);
        }

        private static void SetFloat(Material m, string prop, float f)
        {
            if (m.HasProperty(prop)) m.SetFloat(prop, f);
        }
    }

    /// <summary>The catalogue of cable styles and the procedurally generated textures they share.</summary>
    internal static class CableStyles
    {
        private static List<CableStyle> styles;
        private static readonly Dictionary<CablePattern, Texture2D> albedoCache = new Dictionary<CablePattern, Texture2D>();
        private static readonly Dictionary<string, Texture2D> normalCache = new Dictionary<string, Texture2D>();
        private static Texture2D white;
        private static Material line;

        /// <summary>Bumped whenever the chosen styles or thickness change, so every tether refreshes.</summary>
        public static int Version { get; private set; }

        public static IList<CableStyle> All
        {
            get { if (styles == null) Load(); return styles; }
        }

        public static void NotifyChanged()
        {
            Version++;
        }

        public static void Load()
        {
            styles = new List<CableStyle>();
            ConfigNode[] roots = GameDatabase.Instance != null ? GameDatabase.Instance.GetConfigNodes("KSP_TETHERS") : null;
            if (roots != null && roots.Length > 0)
            {
                foreach (ConfigNode n in roots[0].GetNodes("CABLE_STYLE"))
                {
                    CableStyle s = CableStyle.FromNode(n);
                    if (Get(s.Name, false) == null)
                        styles.Add(s);
                }
            }
            if (styles.Count == 0)
                styles.AddRange(BuiltIn());
            Version++;
        }

        public static CableStyle Get(string name)
        {
            return Get(name, true);
        }

        private static CableStyle Get(string name, bool fallback)
        {
            if (styles == null)
                Load();
            foreach (CableStyle s in styles)
                if (s.Name == name)
                    return s;
            return fallback && styles.Count > 0 ? styles[0] : null;
        }

        public static CableStyle For(TetherKind kind)
        {
            TetherUserSettings user = TetherUserSettings.Instance;
            return Get(kind == TetherKind.Kerbal ? user.evaStyle : user.cableStyle);
        }

        /// <summary>Styles used if Settings.cfg defines none.</summary>
        private static IEnumerable<CableStyle> BuiltIn()
        {
            yield return new CableStyle();
            yield return new CableStyle
            {
                Name = "fabric", Title = "White Fabric Webbing", Pattern = CablePattern.Woven,
                Color = new Color(0.98f, 0.98f, 0.97f), Specular = new Color(0.05f, 0.05f, 0.05f), Shininess = 0.05f,
                Radius = 0.017f, TilesPerMeter = 9f, Bump = 0.35f, BendStiffness = 0.02f, MinBendRadius = 0.2f
            };
            yield return new CableStyle
            {
                Name = "goldbraid", Title = "Gold-Silver Braid", Pattern = CablePattern.Braided,
                Color = new Color(0.86f, 0.80f, 0.60f), Specular = new Color(0.9f, 0.85f, 0.6f), Shininess = 0.55f,
                FittingColor = new Color(0.75f, 0.70f, 0.50f), Radius = 0.02f, TilesPerMeter = 8f, Bump = 0.6f,
                BendStiffness = 0.04f, MinBendRadius = 0.35f
            };
            yield return new CableStyle
            {
                Name = "steel", Title = "Steel Wire Rope", Pattern = CablePattern.Twisted,
                Color = new Color(0.60f, 0.62f, 0.64f), Specular = new Color(0.8f, 0.8f, 0.8f), Shininess = 0.6f,
                FittingColor = new Color(0.35f, 0.36f, 0.38f), Radius = 0.012f, TilesPerMeter = 10f, Bump = 0.7f,
                BendStiffness = 0.08f, MinBendRadius = 0.3f
            };
        }

        public static Texture2D PatternAlbedo(CablePattern pattern)
        {
            Texture2D t;
            if (albedoCache.TryGetValue(pattern, out t) && t != null)
                return t;
            BuildPattern(pattern, 1f);
            return albedoCache[pattern];
        }

        public static Texture2D PatternNormal(CablePattern pattern, float bump)
        {
            string key = pattern + "@" + bump.ToString("F2");
            Texture2D t;
            if (normalCache.TryGetValue(key, out t) && t != null)
                return t;
            BuildPattern(pattern, bump);
            return normalCache[key];
        }

        private static void BuildPattern(CablePattern pattern, float bump)
        {
            Color32[] a, n;
            CablePatterns.Generate(pattern, bump, out a, out n);
            int size = CablePatterns.Size;

            Texture2D existing;
            if (!albedoCache.TryGetValue(pattern, out existing) || existing == null)
            {
                var alb = new Texture2D(size, size, TextureFormat.RGBA32, true, false)
                {
                    name = "KSPTethers-" + pattern, wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 8
                };
                alb.SetPixels32(a);
                alb.Apply(true, true);
                albedoCache[pattern] = alb;
            }

            var nrm = new Texture2D(size, size, TextureFormat.RGBA32, true, true)
            {
                name = "KSPTethers-" + pattern + "-Normal", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 8
            };
            nrm.SetPixels32(n);
            nrm.Apply(true, true);
            normalCache[pattern + "@" + bump.ToString("F2")] = nrm;
        }

        public static Texture2D White
        {
            get
            {
                if (white == null)
                {
                    white = new Texture2D(4, 4, TextureFormat.RGBA32, false) { name = "KSPTethers-White" };
                    var px = new Color32[16];
                    for (int i = 0; i < px.Length; i++)
                        px[i] = new Color32(255, 255, 255, 255);
                    white.SetPixels32(px);
                    white.Apply(false, true);
                }
                return white;
            }
        }

        /// <summary>Unlit, vertex-coloured material for the aiming line.</summary>
        public static Material LineMaterial
        {
            get
            {
                if (line == null)
                {
                    Shader shader = FindShader("Legacy Shaders/Particles/Alpha Blended", "Particles/Alpha Blended",
                        "Sprites/Default", "KSP/Particles/Alpha Blended", "Unlit/Color");
                    line = new Material(shader) { name = "KSPTethers-Line" };
                    if (line.HasProperty("_TintColor"))
                        line.SetColor("_TintColor", new Color(0.5f, 0.5f, 0.5f, 0.5f));
                }
                return line;
            }
        }

        public static Shader FindShader(params string[] names)
        {
            foreach (string name in names)
            {
                Shader s = Shader.Find(name);
                if (s != null)
                    return s;
            }
            TetherLog.Warn("None of the shaders [" + string.Join(", ", names) + "] were found.");
            return Shader.Find("Hidden/Internal-Colored");
        }
    }
}

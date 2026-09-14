using System;
using UnityEngine;

namespace KSPTethers
{
    /// <summary>
    /// Global (not per-save) tuning loaded from the KSP_TETHERS node in GameData/KSPTethers/Settings.cfg.
    /// Anything here can be changed with a ModuleManager patch. Cable looks live in CABLE_STYLE nodes
    /// (<see cref="CableStyles"/>) and the transferable resources in TETHER_RESOURCE nodes (<see cref="TetherResources"/>).
    /// Player choices made in the toolbar app are kept separately in <see cref="TetherUserSettings"/>.
    /// </summary>
    internal sealed class TetherConfig
    {
        private static TetherConfig instance;

        public static TetherConfig Instance
        {
            get
            {
                if (instance == null)
                    instance = Load();
                return instance;
            }
        }

        // ---- Mesh detail -----------------------------------------------------------------------
        public int radialSides = 10;
        public int smoothingSubdivisions = 3;
        public bool castShadows = true;
        public float lodNearDistance = 25f;    // full detail inside this camera distance
        public float lodFarDistance = 80f;     // lowest detail beyond this

        // ---- Rope simulation -------------------------------------------------------------------
        public float segmentLength = 0.22f;
        public int maxNodes = 120;
        public int solverIterations = 12;
        public float substepRate = 90f;
        public float damping = 0.12f;          // fraction of velocity lost per second in vacuum
        public float endStiffness = 0.45f;     // how firmly the rope leaves its fittings
        public float idleFlow = 0.03f;         // m/s^2 of gentle drifting noise (zero-g "floating")
        public float airDrag = 4f;             // 1/s per kg/m^3 of air density
        public bool collisions = true;
        public bool selfCollision = true;      // the rope can't pass through itself
        public bool wrapAroundHulls = true;    // a rope draped round the ship pulls from where it touches the hull
        public float friction = 0.45f;         // Coulomb coefficient for rope on parts and terrain
        public float slackFactor = 0.35f;      // automatic pay-out keeps rope >= distance * (1 + slackFactor) + slackBase
        public float slackBase = 0.6f;
        public float payoutSpeed = 4f;         // m/s, speed of the automatic pay-out
        public float cullDistance = 1200f;     // don't simulate ropes further than this from the camera

        // ---- Physical link ---------------------------------------------------------------------
        // Springs are set by natural frequency and damping ratio of the masses on each end, so they
        // behave the same for a kerbal on a line and for a tug towing a station.
        public float springFrequency = 2f;        // Hz, kerbal tethers
        public float springDampingRatio = 0.6f;
        public float cableSpringFrequency = 1f;   // Hz, cables between parts
        public float cableDampingRatio = 0.8f;
        public float maxSpring = 5000f;           // kN/m
        public float breakForce = 0f;             // kN a kerbal tether holds before snapping; 0 = unbreakable
        public float cableBreakForce = 0f;        // kN for cables between parts; 0 = unbreakable
        public float minLength = 0.5f;
        public float cableEndPickupRadius = 0.6f; // m, how close to a cable end a kerbal must click to pick it up

        // ---- Kerbal attach point ---------------------------------------------------------------
        public string kerbalBone = "bn_jetpack01";
        public Vector3 kerbalOffset = new Vector3(0f, 0.049f, -0.09f);

        // ---- Default keys (players can rebind them in the toolbar app) -------------------------
        public KeyCode toggleKey = KeyCode.Y;
        public KeyCode reelInKey = KeyCode.Minus;
        public KeyCode reelOutKey = KeyCode.Equals;
        public float releaseHoldTime = 0.8f;

        // ---- Resource transfer -----------------------------------------------------------------
        public float resourceTickInterval = 0.25f; // s between resource exchanges

        // ---- Sounds ----------------------------------------------------------------------------
        public string clipSound = "Squad/Sounds/sound_click_latch";
        public string releaseSound = "Squad/Sounds/ksp1_strunts_disconnect_v3_pitched2";
        public string reelSound = "Squad/Sounds/sound_servomotor";
        public float soundVolume = 0.8f;

        public static void Reload()
        {
            instance = Load();
            CableStyles.Load();
            TetherResources.Load();
        }

        private static TetherConfig Load()
        {
            var cfg = new TetherConfig();
            try
            {
                if (GameDatabase.Instance == null)
                    return cfg;
                ConfigNode[] nodes = GameDatabase.Instance.GetConfigNodes("KSP_TETHERS");
                if (nodes == null || nodes.Length == 0)
                {
                    TetherLog.Warn("No KSP_TETHERS settings node found; using built-in defaults.");
                    return cfg;
                }
                cfg.Apply(nodes[0]);
                TetherLog.Info("Settings loaded.");
            }
            catch (Exception e)
            {
                TetherLog.Exception("Failed to load settings", e);
            }
            return cfg;
        }

        private void Apply(ConfigNode n)
        {
            n.TryGetValue("radialSides", ref radialSides);
            n.TryGetValue("smoothingSubdivisions", ref smoothingSubdivisions);
            n.TryGetValue("castShadows", ref castShadows);
            n.TryGetValue("lodNearDistance", ref lodNearDistance);
            n.TryGetValue("lodFarDistance", ref lodFarDistance);

            n.TryGetValue("segmentLength", ref segmentLength);
            n.TryGetValue("maxNodes", ref maxNodes);
            n.TryGetValue("solverIterations", ref solverIterations);
            n.TryGetValue("substepRate", ref substepRate);
            n.TryGetValue("damping", ref damping);
            n.TryGetValue("endStiffness", ref endStiffness);
            n.TryGetValue("idleFlow", ref idleFlow);
            n.TryGetValue("airDrag", ref airDrag);
            n.TryGetValue("collisions", ref collisions);
            n.TryGetValue("selfCollision", ref selfCollision);
            n.TryGetValue("wrapAroundHulls", ref wrapAroundHulls);
            n.TryGetValue("friction", ref friction);
            n.TryGetValue("slackFactor", ref slackFactor);
            n.TryGetValue("slackBase", ref slackBase);
            n.TryGetValue("payoutSpeed", ref payoutSpeed);
            n.TryGetValue("cullDistance", ref cullDistance);

            n.TryGetValue("springFrequency", ref springFrequency);
            n.TryGetValue("springDampingRatio", ref springDampingRatio);
            n.TryGetValue("cableSpringFrequency", ref cableSpringFrequency);
            n.TryGetValue("cableDampingRatio", ref cableDampingRatio);
            n.TryGetValue("maxSpring", ref maxSpring);
            n.TryGetValue("breakForce", ref breakForce);
            n.TryGetValue("cableBreakForce", ref cableBreakForce);
            n.TryGetValue("minLength", ref minLength);
            n.TryGetValue("cableEndPickupRadius", ref cableEndPickupRadius);

            n.TryGetValue("kerbalBone", ref kerbalBone);
            n.TryGetValue("kerbalOffset", ref kerbalOffset);

            toggleKey = ParseKey(n, "toggleKey", toggleKey);
            reelInKey = ParseKey(n, "reelInKey", reelInKey);
            reelOutKey = ParseKey(n, "reelOutKey", reelOutKey);
            n.TryGetValue("releaseHoldTime", ref releaseHoldTime);

            n.TryGetValue("resourceTickInterval", ref resourceTickInterval);

            n.TryGetValue("clipSound", ref clipSound);
            n.TryGetValue("releaseSound", ref releaseSound);
            n.TryGetValue("reelSound", ref reelSound);
            n.TryGetValue("soundVolume", ref soundVolume);

            // Keep values in sane ranges so a typo can't blow up the solver.
            radialSides = Mathf.Clamp(radialSides, 4, 24);
            smoothingSubdivisions = Mathf.Clamp(smoothingSubdivisions, 1, 8);
            lodNearDistance = Mathf.Max(1f, lodNearDistance);
            lodFarDistance = Mathf.Max(lodNearDistance + 1f, lodFarDistance);
            segmentLength = Mathf.Clamp(segmentLength, 0.05f, 2f);
            maxNodes = Mathf.Clamp(maxNodes, 8, 400);
            solverIterations = Mathf.Clamp(solverIterations, 2, 80);
            substepRate = Mathf.Clamp(substepRate, 30f, 240f);
            damping = Mathf.Clamp(damping, 0f, 20f);
            endStiffness = Mathf.Clamp01(endStiffness);
            friction = Mathf.Clamp(friction, 0f, 2f);
            slackFactor = Mathf.Clamp(slackFactor, 0f, 3f);
            slackBase = Mathf.Clamp(slackBase, 0f, 10f);
            payoutSpeed = Mathf.Clamp(payoutSpeed, 0.1f, 50f);
            springFrequency = Mathf.Clamp(springFrequency, 0.1f, 20f);
            cableSpringFrequency = Mathf.Clamp(cableSpringFrequency, 0.05f, 20f);
            springDampingRatio = Mathf.Clamp(springDampingRatio, 0f, 5f);
            cableDampingRatio = Mathf.Clamp(cableDampingRatio, 0f, 5f);
            maxSpring = Mathf.Max(1f, maxSpring);
            breakForce = Mathf.Max(0f, breakForce);
            cableBreakForce = Mathf.Max(0f, cableBreakForce);
            minLength = Mathf.Clamp(minLength, 0.1f, 5f);
            cableEndPickupRadius = Mathf.Clamp(cableEndPickupRadius, 0.1f, 5f);
            releaseHoldTime = Mathf.Clamp(releaseHoldTime, 0.2f, 5f);
            resourceTickInterval = Mathf.Clamp(resourceTickInterval, 0.02f, 5f);
        }

        private static KeyCode ParseKey(ConfigNode n, string name, KeyCode fallback)
        {
            string s = n.GetValue(name);
            if (string.IsNullOrEmpty(s))
                return fallback;
            try
            {
                return (KeyCode)Enum.Parse(typeof(KeyCode), s.Trim(), true);
            }
            catch
            {
                TetherLog.Warn("Unknown key '" + s + "' for " + name + "; using " + fallback);
                return fallback;
            }
        }
    }
}

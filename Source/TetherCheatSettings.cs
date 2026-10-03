using System.Reflection;
using UnityEngine;

namespace KSPTethers
{
    /// <summary>
    /// The cheat half of the per-save options, shown under "KSP Tethers" in the Difficulty Settings below the
    /// ordinary ones. Everything here is off until <see cref="cheatsEnabled"/> is ticked, so a save can't drift
    /// into silly physics by accident.
    /// </summary>
    public class TetherCheatSettings : GameParameters.CustomParameterNode
    {
        private static readonly TetherCheatSettings Defaults = new TetherCheatSettings();

        public override string Title => "Cheats";
        public override string Section => "KSP Tethers";
        public override string DisplaySection => "KSP Tethers";
        public override int SectionOrder => 2;
        public override GameParameters.GameMode GameMode => GameParameters.GameMode.ANY;
        public override bool HasPresets => false;

        [GameParameters.CustomParameterUI("Enable tether cheats",
            toolTip = "Turns on the options below. They are meant for building, filming and messing about.")]
        public bool cheatsEnabled = false;

        [GameParameters.CustomParameterUI("Infinite length",
            toolTip = "Tethers and cables reel out as far as you like, ignoring the maximum length.")]
        public bool infiniteLength = false;

        [GameParameters.CustomFloatParameterUI("Reel speed", minValue = 0.1f, maxValue = 20f, stepCount = 100,
            displayFormat = "F1", asPercentage = false,
            toolTip = "Multiplies the reel speed. Twenty times is fast enough to pull a kerbal back across a station in seconds.")]
        public float reelSpeedMultiplier = 1f;

        [GameParameters.CustomFloatParameterUI("Clip reach", minValue = 1f, maxValue = 20f, stepCount = 39,
            displayFormat = "F1",
            toolTip = "Multiplies how far a kerbal can reach to clip a tether on.")]
        public float clipReachMultiplier = 1f;

        [GameParameters.CustomFloatParameterUI("Tether strength", minValue = 0.1f, maxValue = 20f, stepCount = 100,
            displayFormat = "F1",
            toolTip = "Multiplies the spring that holds the tether. Above about 4 a tether will happily tow things it shouldn't.")]
        public float strengthMultiplier = 1f;

        [GameParameters.CustomParameterUI("Unbreakable",
            toolTip = "Tethers never snap and never pull free, however hard they are loaded.")]
        public bool unbreakable = false;

        [GameParameters.CustomParameterUI("Crazy physics",
            toolTip = "Tethers become bungee cords: stiff, bouncy and barely damped. Expect kerbals to be catapulted.")]
        public bool crazyPhysics = false;

        public static TetherCheatSettings Current
        {
            get
            {
                if (HighLogic.CurrentGame != null && HighLogic.CurrentGame.Parameters != null)
                {
                    var s = HighLogic.CurrentGame.Parameters.CustomParams<TetherCheatSettings>();
                    if (s != null)
                        return s;
                }
                return Defaults;
            }
        }

        /// <summary>True when the master switch is on; every getter below already takes that into account.</summary>
        public bool On => cheatsEnabled;

        public bool InfiniteLength => cheatsEnabled && infiniteLength;
        public bool Unbreakable => cheatsEnabled && unbreakable;
        public bool CrazyPhysics => cheatsEnabled && crazyPhysics;
        public float ReelSpeed => cheatsEnabled ? Mathf.Clamp(reelSpeedMultiplier, 0.1f, 20f) : 1f;
        public float ClipReach => cheatsEnabled ? Mathf.Clamp(clipReachMultiplier, 1f, 20f) : 1f;
        public float Strength => cheatsEnabled ? Mathf.Clamp(strengthMultiplier, 0.1f, 20f) : 1f;

        /// <summary>Longest a tether may be reeled out to, with the cheat applied.</summary>
        public float MaxLength(float setting)
        {
            return InfiniteLength ? 100000f : setting;
        }

        /// <summary>A one-line note for the toolbar app when anything is switched on.</summary>
        public string Summary
        {
            get
            {
                if (!cheatsEnabled)
                    return null;
                string s = "";
                if (infiniteLength) s += "infinite length, ";
                if (unbreakable) s += "unbreakable, ";
                if (crazyPhysics) s += "crazy physics, ";
                if (Mathf.Abs(reelSpeedMultiplier - 1f) > 0.05f) s += "reel " + reelSpeedMultiplier.ToString("F1") + "x, ";
                if (Mathf.Abs(clipReachMultiplier - 1f) > 0.05f) s += "reach " + clipReachMultiplier.ToString("F1") + "x, ";
                if (Mathf.Abs(strengthMultiplier - 1f) > 0.05f) s += "strength " + strengthMultiplier.ToString("F1") + "x, ";
                return s.Length > 0 ? s.Substring(0, s.Length - 2) : "on, but nothing changed";
            }
        }

        public override bool Enabled(MemberInfo member, GameParameters parameters)
        {
            return true;
        }

        public override bool Interactible(MemberInfo member, GameParameters parameters)
        {
            return member.Name == nameof(cheatsEnabled) || cheatsEnabled;
        }
    }
}

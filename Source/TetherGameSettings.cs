using System.Reflection;

namespace KSPTethers
{
    /// <summary>
    /// Per-save options, shown in the game's Difficulty Settings under "KSP Tethers".
    /// </summary>
    public class TetherGameSettings : GameParameters.CustomParameterNode
    {
        private static readonly TetherGameSettings Defaults = new TetherGameSettings();

        public override string Title => "EVA Tethers";
        public override string Section => "KSP Tethers";
        public override string DisplaySection => "KSP Tethers";
        public override int SectionOrder => 1;
        public override GameParameters.GameMode GameMode => GameParameters.GameMode.ANY;
        public override bool HasPresets => false;

        [GameParameters.CustomParameterUI("Auto-tether on EVA",
            toolTip = "Kerbals leaving a hatch are automatically clipped to the part they left.")]
        public bool autoTether = true;

        [GameParameters.CustomParameterUI("Only when in space",
            toolTip = "Only auto-tether when the vessel is outside the atmosphere and not landed.")]
        public bool autoTetherSpaceOnly = true;

        [GameParameters.CustomParameterUI("Physical tethers",
            toolTip = "Tethers restrain the kerbal (and can reel them in). Turn off for purely cosmetic tethers.")]
        public bool physicalTethers = true;

        [GameParameters.CustomParameterUI("Kerbal-to-kerbal tethers",
            toolTip = "Allow clipping a tether onto another kerbal on EVA.")]
        public bool allowKerbalToKerbal = true;

        [GameParameters.CustomParameterUI("Self-retracting reel",
            toolTip = "Take up slack automatically instead of letting it float freely.")]
        public bool retractSlack = false;

        [GameParameters.CustomFloatParameterUI("Default length (m)", minValue = 2f, maxValue = 50f, stepCount = 49,
            displayFormat = "F0", toolTip = "Tether length given to a freshly clipped tether.")]
        public float defaultLength = 15f;

        [GameParameters.CustomFloatParameterUI("Maximum length (m)", minValue = 5f, maxValue = 100f, stepCount = 96,
            displayFormat = "F0", toolTip = "Longest a tether can be reeled out.")]
        public float maxLength = 40f;

        [GameParameters.CustomFloatParameterUI("Clip reach (m)", minValue = 1f, maxValue = 10f, stepCount = 19,
            displayFormat = "F1", toolTip = "How close a kerbal must be to a part to clip a tether onto it.")]
        public float clipReach = 3f;

        [GameParameters.CustomFloatParameterUI("Reel speed (m/s)", minValue = 0.1f, maxValue = 3f, stepCount = 30,
            displayFormat = "F1", toolTip = "Speed of the tether reel.")]
        public float reelSpeed = 1f;

        public static TetherGameSettings Current
        {
            get
            {
                if (HighLogic.CurrentGame != null && HighLogic.CurrentGame.Parameters != null)
                {
                    var s = HighLogic.CurrentGame.Parameters.CustomParams<TetherGameSettings>();
                    if (s != null)
                        return s;
                }
                return Defaults;
            }
        }

        public override bool Enabled(MemberInfo member, GameParameters parameters)
        {
            return true;
        }

        public override bool Interactible(MemberInfo member, GameParameters parameters)
        {
            if (member.Name == nameof(autoTetherSpaceOnly))
                return autoTether;
            return true;
        }
    }
}

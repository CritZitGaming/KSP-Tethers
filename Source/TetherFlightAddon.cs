using System;
using UnityEngine;

namespace KSPTethers
{
    /// <summary>Auto-clips a safety tether when a kerbal steps out of a hatch.</summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class TetherFlightAddon : MonoBehaviour
    {
        private void Start()
        {
            GameEvents.onCrewOnEva.Add(OnCrewOnEva);
        }

        private void OnDestroy()
        {
            GameEvents.onCrewOnEva.Remove(OnCrewOnEva);
        }

        private static void OnCrewOnEva(GameEvents.FromToAction<Part, Part> data)
        {
            try
            {
                TetherGameSettings gs = TetherGameSettings.Current;
                if (!gs.autoTether)
                    return;
                Part from = data.from;
                Part kerbal = data.to;
                if (from == null || kerbal == null || from.vessel == null)
                    return;
                if (gs.autoTetherSpaceOnly && !IsInSpace(from.vessel))
                    return;
                var module = kerbal.FindModuleImplementing<ModuleKerbalTether>();
                if (module != null)
                    module.RequestAutoTether(from);
            }
            catch (Exception e)
            {
                TetherLog.Exception("onCrewOnEva", e);
            }
        }

        private static bool IsInSpace(Vessel v)
        {
            if (v.LandedOrSplashed)
                return false;
            CelestialBody body = v.mainBody;
            if (body != null && body.atmosphere && v.altitude < body.atmosphereDepth)
                return false;
            return true;
        }
    }

    /// <summary>
    /// Once the game database (including ModuleManager patches) is ready: reads Settings.cfg, cable styles and
    /// resource rules, and registers the app with ToolbarControl so players can choose stock and/or Blizzy's toolbar.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public class TetherConfigLoader : MonoBehaviour
    {
        private void Start()
        {
            TetherConfig.Reload();
            TetherToolbar.RegisterWithToolbarControl();
            TetherLog.Info("Toolbar: " + (TetherToolbar.UsingToolbarControl ? "ToolbarControl (stock and Blizzy)" : "stock launcher") +
                           "; windows: " + (GuiWindow.UsingClickThroughBlocker ? "ClickThroughBlocker" : "input lock"));
        }
    }
}

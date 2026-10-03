using System;
using UnityEngine;

namespace KSPTethers
{
    /// <summary>
    /// Keyboard control of ship-to-ship cables from inside a ship, where there is no kerbal to right-click.
    ///
    /// One cable at a time is "selected" - picked in the toolbar app, stepped through with the next/previous
    /// keys, or chosen by a key bound to that cable's own slot - and the ordinary reel keys drive it. A kerbal
    /// on EVA still owns those keys for their own tether, so the two never fight.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class TetherCableKeys : MonoBehaviour
    {
        private int errors;

        private void Update()
        {
            try
            {
                Tick();
            }
            catch (Exception e)
            {
                if (errors++ < 5)
                    TetherLog.Exception("Cable keys", e);
            }
        }

        private void Tick()
        {
            if (FlightDriver.Pause || MapView.MapIsEnabled || TetherWindow.IsCapturingKey ||
                !InputLockManager.IsUnlocked(ControlTypes.CUSTOM_ACTION_GROUPS))
                return;
            TetherUserSettings user = TetherUserSettings.Instance;

            if (Pressed(user.selectNextKey))
                Announce(TetherRegistry.SelectStep(1));
            if (Pressed(user.selectPrevKey))
                Announce(TetherRegistry.SelectStep(-1));

            for (int i = 0; i < user.slotKeys.Length; i++)
            {
                if (!Pressed(user.slotKeys[i]))
                    continue;
                TetherCore c = TetherRegistry.BySlot(i + 1);
                if (c != null)
                    Announce(TetherRegistry.Selected = c);
                else
                    Post("No cable is bound to key " + (i + 1) + " yet; assign one in the KSP Tethers app");
            }

            TetherCore sel = TetherRegistry.Selected;
            if (Pressed(user.releaseSelectedKey) && sel != null && TetherScenario.Instance != null)
            {
                TetherScenario.Instance.ReleaseCable(sel, true);
                return;
            }

            // Reel keys: only ours while the player isn't flying a kerbal whose own tether answers to them.
            if (sel == null || !user.cableKeysFromShip || OwnedByEva())
                return;
            if (Input.GetKey(user.reelInKey))
            {
                sel.Reel = ReelMode.None;
                sel.HoldReelFromApp(true);
            }
            else if (Input.GetKey(user.reelOutKey))
            {
                sel.Reel = ReelMode.None;
                sel.HoldReelFromApp(false);
            }
        }

        /// <summary>True when an EVA kerbal with a tether is being flown, so the reel keys are theirs.</summary>
        private static bool OwnedByEva()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null || !v.isEVA || v.rootPart == null)
                return false;
            var m = v.rootPart.FindModuleImplementing<ModuleKerbalTether>();
            return m != null && m.IsTethered;
        }

        private static bool Pressed(KeyCode k)
        {
            return k != KeyCode.None && Input.GetKeyDown(k);
        }

        private static void Announce(TetherCore c)
        {
            if (c == null)
            {
                Post("No cables between ships to control");
                return;
            }
            string slot = c.Slot > 0 ? " [key " + c.Slot + "]" : "";
            Post("Cable selected" + slot + ": " + c.A.LongTitle + " <-> " + c.B.LongTitle);
        }

        private static void Post(string message)
        {
            ScreenMessages.PostScreenMessage(message, 3f, ScreenMessageStyle.UPPER_CENTER);
        }
    }
}

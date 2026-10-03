using System;
using System.Collections.Generic;
using UnityEngine;

namespace KSPTethers
{
    /// <summary>Every tether in the flight scene, for rendering and for the toolbar app's list.</summary>
    internal static class TetherRegistry
    {
        public static readonly List<TetherCore> All = new List<TetherCore>();

        private static TetherCore selected;

        /// <summary>
        /// The cable the reel keys drive while you are not flying a tethered kerbal. Chosen in the toolbar
        /// app, by the next/previous keys, or by a key bound to the cable's own slot.
        /// </summary>
        public static TetherCore Selected
        {
            get
            {
                if (selected != null && (selected.Released || selected.Finished || !All.Contains(selected)))
                    selected = null;
                return selected;
            }
            set { selected = value; }
        }

        public static void Add(TetherCore core)
        {
            if (!All.Contains(core))
                All.Add(core);
        }

        public static void Remove(TetherCore core)
        {
            All.Remove(core);
            if (selected == core)
                selected = null;
        }

        /// <summary>Every cable between parts that is live enough to be controlled, in a stable order.</summary>
        public static List<TetherCore> Cables(List<TetherCore> into)
        {
            into.Clear();
            foreach (TetherCore c in All)
            {
                if (!c.Released && !c.Finished && c.Kind == TetherKind.Vessel)
                    into.Add(c);
            }
            into.Sort((x, y) => x.Id.CompareTo(y.Id));
            return into;
        }

        private static readonly List<TetherCore> scratch = new List<TetherCore>();

        /// <summary>Moves the selection along the list of cables; returns the new one, or null if there are none.</summary>
        public static TetherCore SelectStep(int step)
        {
            List<TetherCore> list = Cables(scratch);
            if (list.Count == 0)
                return Selected = null;
            int at = Selected != null ? list.IndexOf(Selected) : -1;
            at = at < 0 ? (step > 0 ? 0 : list.Count - 1) : (at + step % list.Count + list.Count) % list.Count;
            return Selected = list[at];
        }

        /// <summary>The cable the player put in slot <paramref name="slot"/> (1-based), if any.</summary>
        public static TetherCore BySlot(int slot)
        {
            foreach (TetherCore c in All)
            {
                if (!c.Released && !c.Finished && c.Slot == slot)
                    return c;
            }
            return null;
        }

        /// <summary>Gives a cable a slot, taking it off whichever cable had it before.</summary>
        public static void SetSlot(TetherCore core, int slot)
        {
            if (slot > 0)
            {
                foreach (TetherCore c in All)
                {
                    if (c != core && c.Slot == slot)
                        c.Slot = 0;
                }
            }
            if (core != null)
                core.Slot = slot;
        }

        /// <summary>Finds a cable end clipped to <paramref name="p"/> near <paramref name="point"/>.</summary>
        public static TetherCore FindEndNear(Part p, Vector3 point, float radius, out bool isEndA)
        {
            isEndA = false;
            TetherCore best = null;
            float bestD = radius * radius;
            foreach (TetherCore c in All)
            {
                if (!c.Attached || c.Kind != TetherKind.Vessel)
                    continue;
                if (c.A.Part == p && !c.A.IsKerbal)
                {
                    float d = (c.A.WorldPos - point).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = c; isEndA = true; }
                }
                if (c.B.Part == p && !c.B.IsKerbal)
                {
                    float d = (c.B.WorldPos - point).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = c; isEndA = false; }
                }
            }
            return best;
        }
    }

    /// <summary>
    /// Simulates and draws every tether once per frame, right before the first camera renders: after all
    /// animation and LateUpdates, so rope ends sit exactly on the kerbals' animated backpacks.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class TetherRenderDriver : MonoBehaviour
    {
        private int lastFrame = -1;
        private readonly Dictionary<TetherCore, int> errors = new Dictionary<TetherCore, int>();

        private void Start()
        {
            Camera.onPreCull += OnPreCullAny;
            GameEvents.onVesselGoOnRails.Add(OnVesselGoOnRails);
        }

        private void OnDestroy()
        {
            Camera.onPreCull -= OnPreCullAny;
            GameEvents.onVesselGoOnRails.Remove(OnVesselGoOnRails);
            // Anything still here at scene end (e.g. a rope mid-retract) is torn down with the scene.
            for (int i = TetherRegistry.All.Count - 1; i >= 0; i--)
                TetherRegistry.All[i].Destroy();
            TetherRegistry.All.Clear();
        }

        private void OnVesselGoOnRails(Vessel v)
        {
            foreach (TetherCore c in TetherRegistry.All)
                c.OnVesselGoOnRails(v);
        }

        private void OnPreCullAny(Camera cam)
        {
            if (Time.frameCount == lastFrame)
                return;
            lastFrame = Time.frameCount;
            float dt = Time.deltaTime;
            List<TetherCore> all = TetherRegistry.All;
            for (int i = all.Count - 1; i >= 0; i--)
            {
                if (i >= all.Count)
                    continue;
                TetherCore c = all[i];
                try
                {
                    c.RenderTick(dt);
                }
                catch (Exception e)
                {
                    int n;
                    errors.TryGetValue(c, out n);
                    errors[c] = ++n;
                    TetherLog.Exception("Rope update failed", e);
                    if (n > 5)
                    {
                        TetherLog.Error("Dropping the visual of a tether after repeated errors.");
                        c.Destroy();
                    }
                    continue;
                }
                // Released tethers that finished retracting are cleaned up here; owners have let go of them.
                if (c.Finished && c.Released)
                {
                    c.Destroy();
                    errors.Remove(c);
                }
            }
        }
    }
}

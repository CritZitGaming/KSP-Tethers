using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace KSPTethers
{
    /// <summary>
    /// Owns the cables that run between two parts (ship to ship, station to rover...) rather than from a
    /// kerbal. They are made when a kerbal clips the free end of their tether onto another part, persist in
    /// the save file, and come back whenever both ends are loaded.
    /// </summary>
    [KSPScenario(ScenarioCreationOptions.AddToAllGames, GameScenes.FLIGHT)]
    public class TetherScenario : ScenarioModule
    {
        public static TetherScenario Instance { get; private set; }

        private readonly List<TetherCore> cables = new List<TetherCore>();
        private float nextExistenceCheck;
        private float resourceTimer;
        private double lastResourceUT;

        internal IList<TetherCore> Cables => cables;

        public override void OnAwake()
        {
            base.OnAwake();
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
            foreach (TetherCore c in cables)
                c.Destroy();
            cables.Clear();
        }

        public override void OnSave(ConfigNode node)
        {
            base.OnSave(node);
            foreach (TetherCore c in cables)
            {
                if (c.Released)
                    continue;
                ConfigNode n = node.AddNode("CABLE");
                c.A.Save(n, "a");
                c.B.Save(n, "b");
                n.AddValue("length", c.LengthLimit.ToString("R", CultureInfo.InvariantCulture));
                n.AddValue("share", c.ShareResources ? "True" : "False");
            }
        }

        public override void OnLoad(ConfigNode node)
        {
            base.OnLoad(node);
            foreach (TetherCore c in cables)
                c.Destroy();
            cables.Clear();
            foreach (ConfigNode n in node.GetNodes("CABLE"))
            {
                TetherEnd a = TetherEnd.Load(n, "a");
                TetherEnd b = TetherEnd.Load(n, "b");
                if (a.Pid == 0 || b.Pid == 0)
                    continue;
                float length = 5f;
                bool share = false;
                n.TryGetValue("length", ref length);
                n.TryGetValue("share", ref share);
                cables.Add(new TetherCore(TetherKind.Vessel, a, b, length, length, this) { ShareResources = share });
            }
            if (cables.Count > 0)
                TetherLog.Info("Loaded " + cables.Count + " cable(s) between parts.");
        }

        /// <summary>Takes over a kerbal's tether whose free end was just clipped onto a part.</summary>
        internal TetherCore AdoptFromKerbal(TetherCore core, TetherEnd newEndA)
        {
            core.ReplaceEndA(newEndA);
            core.SetKind(TetherKind.Vessel);
            core.Owner = this;
            core.Reel = ReelMode.None;
            float dist = core.Attached ? core.Distance : core.RopeLength;
            core.LengthLimit = Mathf.Max(TetherConfig.Instance.minLength, Mathf.Max(core.RopeLength, dist + 0.1f));
            core.RopeLength = core.LengthLimit;
            core.ShareResources = TetherUserSettings.Instance.cableSharingDefault;
            cables.Add(core);
            return core;
        }

        /// <summary>Stops managing a cable whose end a kerbal just picked up (the kerbal owns it now).</summary>
        internal void Detach(TetherCore core)
        {
            cables.Remove(core);
        }

        internal void ReleaseCable(TetherCore core, bool announce)
        {
            if (!cables.Remove(core))
                return;
            if (announce)
                Post("Cable between " + core.A.LongTitle + " and " + core.B.LongTitle + " released");
            core.Release();
        }

        private void FixedUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight)
                return;
            float dt = Time.fixedDeltaTime;
            bool checkExistence = Time.time >= nextExistenceCheck;
            if (checkExistence)
                nextExistenceCheck = Time.time + 5f;

            for (int i = cables.Count - 1; i >= 0; i--)
            {
                TetherCore c = cables[i];
                try
                {
                    if (!c.A.IsBound)
                        c.A.TryResolve();
                    if (!c.B.IsBound)
                        c.B.TryResolve();

                    // Parts recovered or destroyed while unloaded never come back.
                    if (checkExistence && ((!c.A.IsBound && !TetherEnd.ExistsAnywhere(c.A.Pid)) ||
                                           (!c.B.IsBound && !TetherEnd.ExistsAnywhere(c.B.Pid))))
                    {
                        cables.RemoveAt(i);
                        c.Destroy();
                        TetherLog.Info("Dropped a cable whose part no longer exists.");
                        continue;
                    }

                    string problem;
                    if (!c.PhysicsTick(dt, out problem))
                    {
                        cables.RemoveAt(i);
                        TetherEnd survivor = c.A.IsAlive ? c.A : c.B.IsAlive ? c.B : null;
                        Post(problem == "snapped" ? "A cable snapped!"
                            : problem == "pulled free" ? "A cable pulled free"
                            : "A cable came loose: the part at one end is gone");
                        if (survivor != null)
                            c.ReleaseToward(survivor);
                        else
                            c.Destroy();
                    }
                }
                catch (Exception e)
                {
                    TetherLog.Exception("Cable update", e);
                }
            }

            resourceTimer += dt;
            if (resourceTimer >= TetherConfig.Instance.resourceTickInterval)
            {
                double ut = Planetarium.GetUniversalTime();
                double rdt = lastResourceUT > 0 ? ut - lastResourceUT : resourceTimer;
                lastResourceUT = ut;
                resourceTimer = 0f;
                if (rdt > 0 && rdt < 21600)
                {
                    foreach (TetherCore c in cables)
                    {
                        if (c.ShareResources && c.Attached)
                            TetherResources.TickCable(c.A, c.B, rdt);
                    }
                }
            }
        }

        private static void Post(string message)
        {
            ScreenMessages.PostScreenMessage(message, 3.5f, ScreenMessageStyle.UPPER_CENTER);
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;

namespace KSPTethers
{
    /// <summary>A kerbal's EVA suit: only the resources stored on the kerbal part itself.</summary>
    internal sealed class SuitSide : IResourceSide
    {
        private readonly Part part;

        public SuitSide(Part part)
        {
            this.part = part;
        }

        private PartResource Res(string name)
        {
            return part != null && part.Resources != null ? part.Resources.Get(name) : null;
        }

        public double Amount(string resource)
        {
            PartResource r = Res(resource);
            return r != null ? r.amount : 0.0;
        }

        public double Capacity(string resource)
        {
            PartResource r = Res(resource);
            return r != null ? r.maxAmount : 0.0;
        }

        public double Take(string resource, double amount)
        {
            PartResource r = Res(resource);
            if (r == null || amount <= 0)
                return 0.0;
            double t = Math.Min(amount, r.amount);
            r.amount -= t;
            return t;
        }

        public double Put(string resource, double amount)
        {
            PartResource r = Res(resource);
            if (r == null || amount <= 0)
                return 0.0;
            double s = Math.Min(amount, r.maxAmount - r.amount);
            if (s <= 0)
                return 0.0;
            r.amount += s;
            return s;
        }
    }

    /// <summary>Everything a vessel can reach from one part (whole-vessel flow, respecting locked tanks).</summary>
    internal sealed class VesselSide : IResourceSide
    {
        private static readonly Dictionary<string, int> Ids = new Dictionary<string, int>();
        private readonly Part part;

        public VesselSide(Part part)
        {
            this.part = part;
        }

        private static int Id(string name)
        {
            int id;
            if (Ids.TryGetValue(name, out id))
                return id;
            PartResourceDefinition def = PartResourceLibrary.Instance != null ? PartResourceLibrary.Instance.GetDefinition(name) : null;
            id = def != null ? def.id : 0;
            Ids[name] = id;
            return id;
        }

        private void Totals(string resource, out double amount, out double max)
        {
            amount = max = 0;
            int id = Id(resource);
            if (id == 0 || part == null)
                return;
            part.GetConnectedResourceTotals(id, ResourceFlowMode.ALL_VESSEL, out amount, out max, true);
        }

        public double Amount(string resource)
        {
            double a, m;
            Totals(resource, out a, out m);
            return a;
        }

        public double Capacity(string resource)
        {
            double a, m;
            Totals(resource, out a, out m);
            return m;
        }

        public double Take(string resource, double amount)
        {
            int id = Id(resource);
            if (id == 0 || part == null || amount <= 0)
                return 0.0;
            return Math.Max(0.0, part.RequestResource(id, amount, ResourceFlowMode.ALL_VESSEL));
        }

        public double Put(string resource, double amount)
        {
            int id = Id(resource);
            if (id == 0 || part == null || amount <= 0)
                return 0.0;
            // A negative request stores resources and returns the (negative) amount accepted.
            return Math.Max(0.0, -part.RequestResource(id, -amount, ResourceFlowMode.ALL_VESSEL));
        }
    }

    /// <summary>Which resources travel down a tether, and the per-tick exchange for each kind of tether.</summary>
    internal static class TetherResources
    {
        private static List<ResourceRule> rules;

        public static IList<ResourceRule> Rules
        {
            get { if (rules == null) Load(); return rules; }
        }

        public static void Load()
        {
            rules = new List<ResourceRule>();
            ConfigNode[] roots = GameDatabase.Instance != null ? GameDatabase.Instance.GetConfigNodes("KSP_TETHERS") : null;
            if (roots != null && roots.Length > 0)
            {
                foreach (ConfigNode n in roots[0].GetNodes("TETHER_RESOURCE"))
                {
                    string name = n.GetValue("name");
                    if (string.IsNullOrEmpty(name) || rules.Exists(r => r.Name == name))
                        continue;
                    var rule = new ResourceRule { Name = name, Source = name, Title = name };
                    n.TryGetValue("source", ref rule.Source);
                    n.TryGetValue("ratio", ref rule.Ratio);
                    n.TryGetValue("title", ref rule.Title);
                    n.TryGetValue("group", ref rule.Group);
                    n.TryGetValue("enabled", ref rule.DefaultOn);
                    string flow = n.GetValue("flow");
                    rule.Flow = flow != null && flow.Trim().Equals("waste", StringComparison.OrdinalIgnoreCase)
                        ? ResourceFlow.Waste : ResourceFlow.Supply;
                    rules.Add(rule);
                }
            }
            if (rules.Count == 0)
            {
                foreach (string s in new[] { "ElectricCharge", "Oxygen", "Food", "Water" })
                    rules.Add(new ResourceRule { Name = s, Source = s, Title = s, Flow = ResourceFlow.Supply });
                foreach (string s in new[] { "CarbonDioxide", "Waste", "WasteWater" })
                    rules.Add(new ResourceRule { Name = s, Source = s, Title = s, Flow = ResourceFlow.Waste });
            }
        }

        /// <summary>True if the resource exists in this game (installed by some mod).</summary>
        public static bool IsDefined(string name)
        {
            return PartResourceLibrary.Instance != null && PartResourceLibrary.Instance.GetDefinition(name) != null;
        }

        private static bool Enabled(ResourceRule r)
        {
            return TetherUserSettings.Instance.IsResourceEnabled(r) && IsDefined(r.Name) && IsDefined(r.Source);
        }

        /// <summary>Exchange between an EVA kerbal and what its tether is clipped to.</summary>
        public static void TickKerbal(Part kerbal, TetherEnd other, double dt, IDictionary<string, double> flow)
        {
            TetherUserSettings user = TetherUserSettings.Instance;
            if (!user.resourceTransfer || kerbal == null || !other.IsAlive)
                return;
            var suit = new SuitSide(kerbal);
            if (other.IsKerbal)
            {
                if (user.buddySharing)
                    ResourceExchange.Balance(suit, new SuitSide(other.Part), Rules, Enabled, dt, user.transferTime);
            }
            else
            {
                ResourceExchange.SuitWithVessel(suit, new VesselSide(other.Part), Rules, Enabled, dt, user.transferTime, flow);
            }
        }

        /// <summary>Evens out resources between two vessels joined by a cable with sharing switched on.</summary>
        public static void TickCable(TetherEnd a, TetherEnd b, double dt)
        {
            TetherUserSettings user = TetherUserSettings.Instance;
            if (!user.resourceTransfer || !a.IsAlive || !b.IsAlive || a.Part.vessel == b.Part.vessel)
                return;
            IResourceSide sa = a.IsKerbal ? (IResourceSide)new SuitSide(a.Part) : new VesselSide(a.Part);
            IResourceSide sb = b.IsKerbal ? (IResourceSide)new SuitSide(b.Part) : new VesselSide(b.Part);
            ResourceExchange.Balance(sa, sb, Rules, Enabled, dt, user.transferTime);
        }
    }

    /// <summary>Reports which life support mods are present, for the Resources tab.</summary>
    internal static class LifeSupportDetection
    {
        internal struct Entry
        {
            public string Name;
            public string Status;
            public bool Good;
        }

        private static List<Entry> cached;

        public static List<Entry> Detect()
        {
            if (cached != null)
                return cached;
            var list = new List<Entry>();
            bool tacDll = AssemblyLoaded("TacLifeSupport");
            bool tacFolder = Directory.Exists(Path.Combine(KSPUtil.ApplicationRootPath, "GameData/ThunderAerospace/TacLifeSupport"));
            if (tacDll)
                list.Add(new Entry { Name = "TAC Life Support", Status = "active: suits are topped up with Food, Water, Oxygen and ElectricCharge", Good = true });
            else if (tacFolder)
                list.Add(new Entry { Name = "TAC Life Support", Status = "configs found, but TacLifeSupport.dll isn't loaded, so TAC-LS isn't running", Good = false });
            if (AssemblyLoadedPrefix("Kerbalism"))
                list.Add(new Entry { Name = "Kerbalism", Status = "active: suit supplies are topped up and waste is taken back", Good = true });
            if (AssemblyLoaded("USILifeSupport"))
                list.Add(new Entry { Name = "USI Life Support", Status = "detected: it doesn't keep supplies in EVA suits, so tethers can't feed it", Good = false });
            if (AssemblyLoaded("Snacks"))
                list.Add(new Entry { Name = "Snacks!", Status = "active: resources it stores in suits are transferred", Good = true });
            if (list.Count == 0)
                list.Add(new Entry { Name = "No life support mod", Status = "tethers still carry power and jetpack fuel", Good = true });
            cached = list;
            return list;
        }

        private static bool AssemblyLoaded(string name)
        {
            foreach (AssemblyLoader.LoadedAssembly a in AssemblyLoader.loadedAssemblies)
            {
                if (a.name == name || (a.assembly != null && a.assembly.GetName().Name == name))
                    return true;
            }
            return false;
        }

        private static bool AssemblyLoadedPrefix(string prefix)
        {
            foreach (AssemblyLoader.LoadedAssembly a in AssemblyLoader.loadedAssemblies)
            {
                string n = a.assembly != null ? a.assembly.GetName().Name : a.name;
                if (n != null && n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}

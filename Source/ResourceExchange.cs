using System;
using System.Collections.Generic;

namespace KSPTethers
{
    internal enum ResourceFlow { Supply, Waste }

    /// <summary>One resource the tether can carry. Defined by TETHER_RESOURCE nodes in Settings.cfg.</summary>
    internal sealed class ResourceRule
    {
        public string Name;           // resource held in the kerbal's suit
        public string Source;         // resource drawn from the vessel for supplies (usually the same)
        public double Ratio = 1.0;    // vessel units per suit unit
        public ResourceFlow Flow;
        public string Title;
        public string Group = "Life support";
        public bool DefaultOn = true;

        /// <summary>Only same-resource rules can be evened out between two equal partners.</summary>
        public bool CanBalance => Source == Name;
    }

    /// <summary>Somewhere resources can be taken from or put into: a kerbal's suit, or a whole vessel.</summary>
    internal interface IResourceSide
    {
        double Amount(string resource);
        double Capacity(string resource);
        /// <returns>The amount actually removed.</returns>
        double Take(string resource, double amount);
        /// <returns>The amount actually stored.</returns>
        double Put(string resource, double amount);
    }

    /// <summary>
    /// The transfer rules, independent of KSP so they can be tested: nothing is ever created or destroyed,
    /// only moved, and rates are limited so a suit fills (or empties) over <c>fillTime</c> seconds.
    /// </summary>
    internal static class ResourceExchange
    {
        private const double Epsilon = 1e-9;

        /// <summary>Supplies flow from the vessel into the suit, waste flows from the suit into the vessel.</summary>
        /// <param name="flow">Optional: receives the signed rate per resource (+ into the suit, - out of it).</param>
        public static void SuitWithVessel(IResourceSide suit, IResourceSide vessel, IList<ResourceRule> rules,
            Predicate<ResourceRule> enabled, double dt, double fillTime, IDictionary<string, double> flow = null)
        {
            if (dt <= 0)
                return;
            fillTime = Math.Max(0.1, fillTime);
            foreach (ResourceRule rule in rules)
            {
                if (!enabled(rule))
                    continue;
                double cap = suit.Capacity(rule.Name);
                if (cap <= Epsilon)
                    continue;
                double maxMove = cap * dt / fillTime;
                double moved = 0;

                if (rule.Flow == ResourceFlow.Supply)
                {
                    double need = cap - suit.Amount(rule.Name);
                    if (need <= Epsilon)
                        continue;
                    double want = Math.Min(need, maxMove);
                    double ratio = rule.Ratio > 0 ? rule.Ratio : 1.0;
                    double got = vessel.Take(rule.Source, want * ratio) / ratio;
                    if (got <= Epsilon)
                        continue;
                    double stored = suit.Put(rule.Name, got);
                    if (stored < got - Epsilon)
                        vessel.Put(rule.Source, (got - stored) * ratio); // hand back anything the suit refused
                    moved = stored;
                }
                else
                {
                    double have = suit.Amount(rule.Name);
                    if (have <= Epsilon)
                        continue;
                    double taken = suit.Take(rule.Name, Math.Min(have, maxMove));
                    double stored = vessel.Put(rule.Name, taken);
                    if (stored < taken - Epsilon)
                        suit.Put(rule.Name, taken - stored); // vessel full: the suit keeps the rest
                    moved = -stored;
                }

                if (flow != null && Math.Abs(moved) > Epsilon)
                {
                    double prev;
                    flow.TryGetValue(rule.Name, out prev);
                    flow[rule.Name] = prev + moved / dt;
                }
            }
        }

        /// <summary>Evens out fill levels between two partners (two kerbals, or two vessels on a cable).</summary>
        public static void Balance(IResourceSide a, IResourceSide b, IList<ResourceRule> rules,
            Predicate<ResourceRule> enabled, double dt, double fillTime)
        {
            if (dt <= 0)
                return;
            fillTime = Math.Max(0.1, fillTime);
            foreach (ResourceRule rule in rules)
            {
                if (!rule.CanBalance || !enabled(rule))
                    continue;
                double capA = a.Capacity(rule.Name), capB = b.Capacity(rule.Name);
                if (capA <= Epsilon || capB <= Epsilon)
                    continue;
                double amtA = a.Amount(rule.Name), amtB = b.Amount(rule.Name);
                // Amount A must give up so both end at the same fraction of their capacity.
                double move = amtA - (amtA + amtB) / (capA + capB) * capA;
                double limit = Math.Min(capA, capB) * dt / fillTime;
                if (move > limit) move = limit;
                if (move < -limit) move = -limit;
                if (Math.Abs(move) <= Epsilon)
                    continue;
                if (move > 0)
                    Move(a, b, rule.Name, move);
                else
                    Move(b, a, rule.Name, -move);
            }
        }

        private static void Move(IResourceSide from, IResourceSide to, string resource, double amount)
        {
            double taken = from.Take(resource, amount);
            double stored = to.Put(resource, taken);
            if (stored < taken - Epsilon)
                from.Put(resource, taken - stored);
        }
    }
}

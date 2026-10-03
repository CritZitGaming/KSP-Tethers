using System;
using System.Collections.Generic;
using UnityEngine;

namespace KSPTethers.Tests
{
    /// <summary>Cable surfaces, the lifeline and rope reversal.</summary>
    internal static class FeatureTests
    {
        public static void RunAll(Action<string, Action> run, Action<bool, string> check)
        {
            run("every cable style tiles seamlessly with valid normals", () => PatternsTile(check));
            run("lifeline tops up suits from the ship", () => SuitSupply(check));
            run("lifeline returns waste to the ship", () => SuitWaste(check));
            run("lifeline never creates or loses resources", () => Conservation(check));
            run("jetpack refuels from ship MonoPropellant", () => SourceMapping(check));
            run("buddy lines and ship cables even out supplies", () => Balancing(check));
            run("rope reverses end for end", () => ReverseRope(check));
            run("the force-based pull holds without blowing up", () => Tension(check));
        }

        // ---- patterns ----------------------------------------------------------------------------

        private static void PatternsTile(Action<bool, string> check)
        {
            string cfg = CableSwatches.SettingsPath;
            if (cfg == null)
            {
                check(false, "Settings.cfg not found beside the tests");
                return;
            }
            foreach (CableSwatches.Style style in CableSwatches.Load(cfg))
            {
                Color32[] main, normal;
                int w, h;
                CablePatterns.Generate(style.Spec, CablePatterns.DefaultAcross, out main, out normal, out w, out h);

                // Differences across each wrap seam must look like any other neighbouring texels, or the tile
                // will show a line down the cable.
                double seamU = 0, seamV = 0, innerU = 0, innerV = 0;
                for (int y = 0; y < h; y++)
                {
                    seamU += Math.Abs(main[y * w].g - main[y * w + w - 1].g);
                    for (int x = 1; x < w; x++)
                        innerU += Math.Abs(main[y * w + x].g - main[y * w + x - 1].g);
                }
                for (int x = 0; x < w; x++)
                {
                    seamV += Math.Abs(main[x].g - main[(h - 1) * w + x].g);
                    for (int y = 1; y < h; y++)
                        innerV += Math.Abs(main[y * w + x].g - main[(y - 1) * w + x].g);
                }
                seamU /= h;
                seamV /= w;
                innerU /= h * (w - 1);
                innerV /= w * (h - 1);
                bool seamless = seamU <= innerU * 2.5 + 6 && seamV <= innerV * 2.5 + 6;

                // Normals have to be packed the way Unity reads them on desktop, and never fold past vertical.
                bool normalsOk = true;
                float flattest = 1f;
                for (int i = 0; i < normal.Length; i++)
                {
                    float nx = normal[i].a / 255f * 2f - 1f, ny = normal[i].g / 255f * 2f - 1f;
                    float xy = nx * nx + ny * ny;
                    if (xy > 1.02f || normal[i].r != 255)
                        normalsOk = false;
                    flattest = Math.Min(flattest, (float)Math.Sqrt(Math.Max(0f, 1f - xy)));
                }

                // And the surface has to have real relief, or the weave has quietly stopped being generated.
                // A black hose is almost flat in the albedo, so this is measured on the normals.
                bool hasRelief = style.Spec.Construction == CableConstruction.Smooth || flattest < 0.97f;

                check(seamless && normalsOk && flattest > 0.15f && hasRelief,
                    style.Name + ": seam " + seamU.ToString("F1") + "/" + seamV.ToString("F1") +
                    " vs neighbours " + innerU.ToString("F1") + "/" + innerV.ToString("F1") +
                    ", steepest normal z " + flattest.ToString("F2"));
            }
        }

        // ---- the pull a tether applies -------------------------------------------------------------

        private static void Tension(Action<bool, string> check)
        {
            const float dt = 0.02f;                 // KSP's physics step
            const float kerbal = 0.09375f;          // tonnes
            float invMass = 1f / kerbal + 1f / 20f; // kerbal on a 20 t ship

            check(TetherTension.Solve(-0.5f, 3f, invMass, 15f, 1.4f, dt) == 0f &&
                  TetherTension.Solve(0f, 3f, invMass, 15f, 1.4f, dt) == 0f,
                "a slack tether pulls with nothing at all");

            float pull = TetherTension.Solve(0.1f, 2f, invMass, 15f, 1.4f, dt);
            check(pull > 0.5f && pull < 20f, "a tether 10 cm past its length pulls " + pull.ToString("F2") + " kN");

            // With light springs the soft constraint has to reduce to the spring-damper it was asked for.
            const float k = 4f, c = 0.8f;
            float light = 1f / 500f;                // 500 t each end: the spring is what limits the force
            float got = TetherTension.Solve(0.4f, 1.5f, light, k, c, dt);
            float want = k * 0.4f + (c + dt * k) * 1.5f;
            check(Math.Abs(got - want) / want < 0.02f,
                "heavy ends: " + got.ToString("F3") + " kN against a plain spring-damper's " + want.ToString("F3"));

            // A silly spring on a light kerbal at a low frame rate must still not catapult anyone: the
            // impulse can never be more than the one that just stops the ends separating.
            bool stable = true;
            float worstSpeed = 0f;
            foreach (float step in new[] { 0.02f, 0.05f, 0.1f })
            {
                float x = 1f, v = 6f;               // a metre past the limit, flying outward at 6 m/s
                for (int i = 0; i < 4000; i++)
                {
                    float f = TetherTension.Solve(x, v, 1f / kerbal, 1e6f, 500f, step);
                    v -= f / kerbal * step;         // the tether only ever pulls inward
                    x += v * step;
                    if (x < 0f) { x = 0f; if (v < 0f) v = 0f; }
                    worstSpeed = Math.Max(worstSpeed, Math.Abs(v));
                    if (float.IsNaN(v) || Math.Abs(v) > 20f)
                        stable = false;
                }
            }
            check(stable, "an absurd spring at 10 fps hauls in without catapulting (fastest the kerbal moved: " +
                          worstSpeed.ToString("F1") + " m/s, started at 6)");

            // And the pull has to grow with how far past its length the tether is.
            float near = TetherTension.Solve(0.05f, 0f, invMass, 15f, 1.4f, dt);
            float far = TetherTension.Solve(0.5f, 0f, invMass, 15f, 1.4f, dt);
            check(far > near * 3f, "pull rises with stretch (" + near.ToString("F2") + " kN at 5 cm, " +
                                   far.ToString("F2") + " kN at 50 cm)");
        }

        // ---- lifeline ----------------------------------------------------------------------------

        private sealed class FakeSide : IResourceSide
        {
            public readonly Dictionary<string, double> Amounts = new Dictionary<string, double>();
            public readonly Dictionary<string, double> Caps = new Dictionary<string, double>();

            public FakeSide With(string r, double amount, double cap)
            {
                Amounts[r] = amount;
                Caps[r] = cap;
                return this;
            }

            public double Amount(string r)
            {
                double v;
                return Amounts.TryGetValue(r, out v) ? v : 0;
            }

            public double Capacity(string r)
            {
                double v;
                return Caps.TryGetValue(r, out v) ? v : 0;
            }

            public double Take(string r, double amount)
            {
                double t = Math.Min(amount, Amount(r));
                if (t <= 0) return 0;
                Amounts[r] = Amount(r) - t;
                return t;
            }

            public double Put(string r, double amount)
            {
                double s = Math.Min(amount, Capacity(r) - Amount(r));
                if (s <= 0) return 0;
                Amounts[r] = Amount(r) + s;
                return s;
            }
        }

        private static List<ResourceRule> Rules()
        {
            return new List<ResourceRule>
            {
                new ResourceRule { Name = "Oxygen", Source = "Oxygen", Flow = ResourceFlow.Supply },
                new ResourceRule { Name = "Food", Source = "Food", Flow = ResourceFlow.Supply },
                new ResourceRule { Name = "ElectricCharge", Source = "ElectricCharge", Flow = ResourceFlow.Supply },
                new ResourceRule { Name = "CarbonDioxide", Source = "CarbonDioxide", Flow = ResourceFlow.Waste },
                new ResourceRule { Name = "EVA Propellant", Source = "MonoPropellant", Flow = ResourceFlow.Supply }
            };
        }

        private static void SuitSupply(Action<bool, string> check)
        {
            var suit = new FakeSide().With("Oxygen", 2, 10).With("Food", 0.5, 1);
            var ship = new FakeSide().With("Oxygen", 100, 200).With("Food", 0, 50);
            var flow = new Dictionary<string, double>();
            ResourceExchange.SuitWithVessel(suit, ship, Rules(), r => true, 1.0, 10.0, flow);
            check(Math.Abs(suit.Amount("Oxygen") - 3) < 1e-9, "one second fills a tenth of the suit (O2 " + suit.Amount("Oxygen") + " / 10)");
            check(Math.Abs(ship.Amount("Oxygen") - 99) < 1e-9, "taken from the ship (O2 " + ship.Amount("Oxygen") + ")");
            check(Math.Abs(suit.Amount("Food") - 0.5) < 1e-12, "an empty ship tank gives nothing");
            check(flow.ContainsKey("Oxygen") && flow["Oxygen"] > 0 && !flow.ContainsKey("Food"), "flow report lists only what moved");

            for (int i = 0; i < 100; i++)
                ResourceExchange.SuitWithVessel(suit, ship, Rules(), r => true, 1.0, 10.0);
            check(Math.Abs(suit.Amount("Oxygen") - 10) < 1e-9 && Math.Abs(ship.Amount("Oxygen") - 92) < 1e-9, "stops at a full suit (ship O2 " + ship.Amount("Oxygen") + ")");

            var off = new FakeSide().With("Oxygen", 0, 10);
            ResourceExchange.SuitWithVessel(off, ship, Rules(), r => r.Name != "Oxygen", 1.0, 10.0);
            check(off.Amount("Oxygen") == 0, "a switched-off resource is left alone");
        }

        private static void SuitWaste(Action<bool, string> check)
        {
            var suit = new FakeSide().With("CarbonDioxide", 5, 10);
            var ship = new FakeSide().With("CarbonDioxide", 0, 100);
            ResourceExchange.SuitWithVessel(suit, ship, Rules(), r => true, 2.0, 10.0);
            check(Math.Abs(suit.Amount("CarbonDioxide") - 3) < 1e-9 && Math.Abs(ship.Amount("CarbonDioxide") - 2) < 1e-9, "CO2 moves to the ship at the set rate");

            var fullShip = new FakeSide().With("CarbonDioxide", 99.5, 100);
            var suit2 = new FakeSide().With("CarbonDioxide", 5, 10);
            ResourceExchange.SuitWithVessel(suit2, fullShip, Rules(), r => true, 10.0, 10.0);
            check(Math.Abs(suit2.Amount("CarbonDioxide") - 4.5) < 1e-9 && Math.Abs(fullShip.Amount("CarbonDioxide") - 100) < 1e-9,
                "a nearly full ship takes what fits and the suit keeps the rest");
        }

        private static double Total(FakeSide a, FakeSide b, string r)
        {
            return a.Amount(r) + b.Amount(r);
        }

        private static void Conservation(Action<bool, string> check)
        {
            var rnd = new System.Random(7);
            bool ok = true;
            for (int trial = 0; trial < 200; trial++)
            {
                var suit = new FakeSide().With("Oxygen", rnd.NextDouble() * 10, 10).With("CarbonDioxide", rnd.NextDouble() * 5, 5)
                    .With("ElectricCharge", rnd.NextDouble() * 50, 50);
                var ship = new FakeSide().With("Oxygen", rnd.NextDouble() * 30, 30).With("CarbonDioxide", rnd.NextDouble() * 8, 8)
                    .With("ElectricCharge", rnd.NextDouble() * 20, 20);
                double o2 = Total(suit, ship, "Oxygen"), co2 = Total(suit, ship, "CarbonDioxide"), ec = Total(suit, ship, "ElectricCharge");
                for (int s = 0; s < 20; s++)
                    ResourceExchange.SuitWithVessel(suit, ship, Rules(), r => true, rnd.NextDouble() * 5, 1 + rnd.NextDouble() * 20);
                ok &= Math.Abs(Total(suit, ship, "Oxygen") - o2) < 1e-9 && Math.Abs(Total(suit, ship, "CarbonDioxide") - co2) < 1e-9 &&
                      Math.Abs(Total(suit, ship, "ElectricCharge") - ec) < 1e-9;
                foreach (FakeSide side in new[] { suit, ship })
                    foreach (string r in side.Caps.Keys)
                        ok &= side.Amount(r) >= -1e-12 && side.Amount(r) <= side.Capacity(r) + 1e-9;
            }
            check(ok, "200 random exchanges: totals unchanged, nothing negative or over capacity");
        }

        private static void SourceMapping(Action<bool, string> check)
        {
            var suit = new FakeSide().With("EVA Propellant", 1, 5);
            var ship = new FakeSide().With("MonoPropellant", 40, 40);
            for (int i = 0; i < 20; i++)
                ResourceExchange.SuitWithVessel(suit, ship, Rules(), r => true, 1.0, 5.0);
            check(Math.Abs(suit.Amount("EVA Propellant") - 5) < 1e-9 && Math.Abs(ship.Amount("MonoPropellant") - 36) < 1e-9,
                "jetpack full, 4 units of MonoPropellant used");
        }

        private static void Balancing(Action<bool, string> check)
        {
            var a = new FakeSide().With("Oxygen", 9, 10);
            var b = new FakeSide().With("Oxygen", 1, 10);
            ResourceExchange.Balance(a, b, Rules(), r => true, 1.0, 10.0);
            check(Math.Abs(a.Amount("Oxygen") - 8) < 1e-9 && Math.Abs(b.Amount("Oxygen") - 2) < 1e-9, "rate limited (one unit per second)");
            for (int i = 0; i < 50; i++)
                ResourceExchange.Balance(a, b, Rules(), r => true, 1.0, 10.0);
            check(Math.Abs(a.Amount("Oxygen") - 5) < 1e-9 && Math.Abs(b.Amount("Oxygen") - 5) < 1e-9, "settles at equal fill without overshoot");

            var big = new FakeSide().With("ElectricCharge", 900, 1000);
            var small = new FakeSide().With("ElectricCharge", 0, 100);
            for (int i = 0; i < 200; i++)
                ResourceExchange.Balance(big, small, Rules(), r => true, 1.0, 10.0);
            double fa = big.Amount("ElectricCharge") / 1000, fb = small.Amount("ElectricCharge") / 100;
            check(Math.Abs(fa - fb) < 1e-6 && Math.Abs(big.Amount("ElectricCharge") + small.Amount("ElectricCharge") - 900) < 1e-9,
                "different tank sizes end at the same fraction (" + fa.ToString("P1") + ")");

            var jet = new FakeSide().With("EVA Propellant", 5, 5);
            var jet2 = new FakeSide().With("EVA Propellant", 0, 5);
            ResourceExchange.Balance(jet, jet2, new List<ResourceRule> { new ResourceRule { Name = "EVA Propellant", Source = "MonoPropellant" } }, r => true, 10, 1);
            check(jet2.Amount("EVA Propellant") == 0, "rules that convert resources are never used for balancing");
        }

        // ---- rope reversal -----------------------------------------------------------------------

        private static void ReverseRope(Action<bool, string> check)
        {
            var rope = new RopeSimulation(120, 0.22f, 1f);
            rope.Initialize(new Vector3(0, 0, 3), Vector3.zero, 6f, Vector3.up);
            var original = new Vector3[rope.Count];
            Array.Copy(rope.Pos, original, rope.Count);
            rope.Reverse();
            bool flipped = (rope.Pos[0] - original[rope.Count - 1]).sqrMagnitude < 1e-12f && (rope.Pos[rope.Count - 1] - original[0]).sqrMagnitude < 1e-12f;
            rope.Reverse();
            bool restored = true;
            for (int i = 0; i < rope.Count; i++)
                restored &= (rope.Pos[i] - original[i]).sqrMagnitude < 1e-12f;
            check(flipped && restored, "reversing swaps the ends, twice restores the rope");

            // After a reversal the solver keeps going with the new ends pinned.
            rope.Reverse();
            var p = Program.Defaults(Vector3.zero);
            p.A = Vector3.zero;
            p.B = new Vector3(0, 0, 3);
            p.DirA = Vector3.forward;
            p.DirB = Vector3.back;
            for (int f = 0; f < 120; f++)
                rope.Step(1f / 60f, ref p, null);
            check(Math.Abs(rope.PolylineLength() - 6f) < 0.2f && rope.Pos[0].sqrMagnitude < 1e-8f,
                "a reversed rope simulates normally (" + rope.PolylineLength().ToString("F2") + " m)");
        }
    }
}

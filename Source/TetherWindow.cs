using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace KSPTethers
{
    /// <summary>
    /// The toolbar app: manage live tethers and cables, choose which cable the reel keys drive, pick cable
    /// styles, switch the lifeline (resource transfer) and its resources on or off, rebind keys and choose how
    /// tethers hold on. Available in flight and at the Space Center.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.FlightAndKSC, false)]
    public class TetherWindow : MonoBehaviour
    {
        // Key rows, in the order they are drawn. Slots run from Slot1 upwards.
        private const int KeyNone = -1;
        private const int KeyToggle = 0, KeyReelIn = 1, KeyReelOut = 2;
        private const int KeySelectNext = 3, KeySelectPrev = 4, KeyReleaseSel = 5, KeySlot1 = 6;

        private const string LockId = "KSPTethers_Window";
        private static readonly string[] TabNames = { "Tethers", "Cables", "Resources", "Keys", "Setup" };
        private static readonly string[] LinkModeNames = { "Automatic", "Joint", "Forces" };

        /// <summary>True while waiting for the player to press a key to bind; tether keys are ignored meanwhile.</summary>
        public static bool IsCapturingKey { get; private set; }

        private TetherToolbar toolbar;
        private bool visible;
        private Rect rect;
        private int windowId;
        private Vector2 scroll;
        private int capturing = KeyNone;
        private bool locked;
        private Texture2D swatch;
        private GUIStyle headerStyle, smallStyle, goodStyle, badStyle, selectedStyle;
        private readonly Dictionary<KeyCode, string> conflicts = new Dictionary<KeyCode, string>();
        private readonly List<TetherCore> cableScratch = new List<TetherCore>();

        private void Start()
        {
            windowId = GetInstanceID();
            TetherUserSettings user = TetherUserSettings.Instance;
            float x = user.windowX >= 0 ? user.windowX : Screen.width - 480f;
            float y = user.windowY >= 0 ? user.windowY : 90f;
            rect = new Rect(x, y, 420f, 500f);
            toolbar = new TetherToolbar(gameObject, Open, Close);
            GameEvents.onHideUI.Add(OnHideUI);
            GameEvents.onShowUI.Add(OnShowUI);
        }

        private bool uiHidden;

        private void OnHideUI()
        {
            uiHidden = true;
        }

        private void OnShowUI()
        {
            uiHidden = false;
        }

        private void OnDestroy()
        {
            GameEvents.onHideUI.Remove(OnHideUI);
            GameEvents.onShowUI.Remove(OnShowUI);
            if (toolbar != null)
                toolbar.Destroy();
            SetLock(false);
            IsCapturingKey = false;
            TetherUserSettings.Instance.SaveIfDirty();
            if (swatch != null)
                Destroy(swatch);
        }

        private void Open()
        {
            visible = true;
            RefreshConflicts();
        }

        private void Close()
        {
            visible = false;
            capturing = KeyNone;
            IsCapturingKey = false;
            SetLock(false);
            TetherUserSettings.Instance.SaveIfDirty();
        }

        private void OnGUI()
        {
            if (!visible || uiHidden)
            {
                SetLock(false);
                return;
            }
            GUI.skin = HighLogic.Skin;
            EnsureStyles();

            // Key capture happens before the window draws so the press isn't also handled by a button.
            if (capturing != KeyNone && Event.current.type == EventType.KeyDown && Event.current.keyCode != KeyCode.None)
            {
                KeyCode k = Event.current.keyCode;
                if (k != KeyCode.Escape)
                    AssignKey(capturing, k);
                capturing = KeyNone;
                IsCapturingKey = false;
                Event.current.Use();
            }

            Rect before = rect;
            rect = GuiWindow.Draw(windowId, rect, DrawWindow, "KSP Tethers");
            rect.x = Mathf.Clamp(rect.x, 0f, Mathf.Max(0f, Screen.width - rect.width));
            rect.y = Mathf.Clamp(rect.y, 0f, Mathf.Max(0f, Screen.height - 40f));
            if (rect.x != before.x || rect.y != before.y)
            {
                TetherUserSettings user = TetherUserSettings.Instance;
                user.windowX = rect.x;
                user.windowY = rect.y;
                user.MarkDirty();
            }

            if (!GuiWindow.UsingClickThroughBlocker)
            {
                Vector2 mouse = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
                SetLock(rect.Contains(mouse));
            }
        }

        private void SetLock(bool on)
        {
            if (on == locked)
                return;
            locked = on;
            if (on)
                InputLockManager.SetControlLock(ControlTypes.ALLBUTCAMERAS, LockId);
            else
                InputLockManager.RemoveControlLock(LockId);
        }

        private void EnsureStyles()
        {
            if (headerStyle != null)
                return;
            headerStyle = new GUIStyle(HighLogic.Skin.label) { fontStyle = FontStyle.Bold };
            headerStyle.normal.textColor = new Color(0.95f, 0.8f, 0.35f);
            smallStyle = new GUIStyle(HighLogic.Skin.label) { fontSize = 11, wordWrap = true };
            goodStyle = new GUIStyle(smallStyle);
            goodStyle.normal.textColor = new Color(0.55f, 0.95f, 0.55f);
            badStyle = new GUIStyle(smallStyle);
            badStyle.normal.textColor = new Color(1f, 0.6f, 0.4f);
            selectedStyle = new GUIStyle(HighLogic.Skin.label) { fontStyle = FontStyle.Bold };
            selectedStyle.normal.textColor = new Color(0.5f, 0.9f, 1f);
            swatch = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            swatch.SetPixel(0, 0, Color.white);
            swatch.Apply();
        }

        private void DrawWindow(int id)
        {
            TetherUserSettings user = TetherUserSettings.Instance;
            if (GUI.Button(new Rect(rect.width - 24f, 4f, 20f, 18f), "x"))
            {
                Close();
                toolbar.SetOff();
                return;
            }

            int tab = GUILayout.Toolbar(Mathf.Clamp(user.tab, 0, TabNames.Length - 1), TabNames);
            if (tab != user.tab)
            {
                user.tab = tab;
                user.MarkDirty();
                scroll = Vector2.zero;
            }
            GUILayout.Space(4f);
            scroll = GUILayout.BeginScrollView(scroll, GUILayout.ExpandHeight(true));
            switch (tab)
            {
                case 0: DrawTethers(user); break;
                case 1: DrawCables(user); break;
                case 2: DrawResources(user); break;
                case 3: DrawKeys(user); break;
                default: DrawSetup(user); break;
            }
            GUILayout.EndScrollView();
            GUI.DragWindow(new Rect(0f, 0f, rect.width - 28f, 22f));
        }

        // ---- Tethers -----------------------------------------------------------------------------

        private void DrawTethers(TetherUserSettings user)
        {
            if (!HighLogic.LoadedSceneIsFlight)
            {
                GUILayout.Label("Tethers and cables are listed here during flight.", smallStyle);
                return;
            }
            string key = user.toggleKey.ToString();
            TetherCore selected = TetherRegistry.Selected;
            int shown = 0;
            List<TetherCore> all = TetherRegistry.All;
            for (int i = 0; i < all.Count; i++)
            {
                TetherCore c = all[i];
                if (c.Released)
                    continue;
                shown++;
                bool cable = c.Kind == TetherKind.Vessel;
                GUILayout.BeginVertical(HighLogic.Skin.box);
                GUILayout.Label((cable ? "Cable: " : "") + (cable ? c.A.LongTitle : c.A.Title) + "  <->  " + (cable ? c.B.LongTitle : c.B.Title),
                    cable && c == selected ? selectedStyle : headerStyle);
                if (c.Attached)
                {
                    GUILayout.Label("Distance " + c.Distance.ToString("F1") + " m   length " + c.LengthLimit.ToString("F1") +
                                    " m   rope out " + c.RopeLength.ToString("F1") + " m" +
                                    (c.ReelingIn ? "   (reeling in)" : c.ReelingOut ? "   (reeling out)" : "") +
                                    (c.Tension > 0.01f ? "   pulling " + c.Tension.ToString("F1") + " kN" : ""), smallStyle);
                }
                else
                {
                    GUILayout.Label("Waiting for both ends to load", smallStyle);
                }

                GUILayout.BeginHorizontal();
                if (GUILayout.RepeatButton("<< Reel in", GUILayout.Width(95f)))
                    c.HoldReelFromApp(true);
                if (GUILayout.RepeatButton("Reel out >>", GUILayout.Width(95f)))
                    c.HoldReelFromApp(false);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Release", GUILayout.Width(80f)))
                    ReleaseFromApp(c);
                GUILayout.EndHorizontal();

                if (cable)
                    DrawCableControls(user, c, c == selected);

                var owner = c.Owner as ModuleKerbalTether;
                if (owner != null && !string.IsNullOrEmpty(owner.LifelineText))
                    GUILayout.Label("Lifeline: " + owner.LifelineText, goodStyle);
                if (cable)
                {
                    bool share = GUILayout.Toggle(c.ShareResources, " Share resources between the two vessels");
                    if (share != c.ShareResources)
                        c.ShareResources = share;
                }
                GUILayout.EndVertical();
            }
            if (shown == 0)
            {
                GUILayout.Label("No tethers yet.", headerStyle);
                GUILayout.Label("On EVA, press [" + key + "] and click a part to clip on (press it twice for the nearest part). " +
                                "Kerbals leaving a hatch in space clip on automatically.", smallStyle);
            }
            GUILayout.Space(6f);
            GUILayout.Label("Tethered kerbal: tap [" + key + "] and click another part to clip your end there, making a cable " +
                            "between two ships, or click a kerbal to hand the tether over. Hold [" + key + "] to release. " +
                            "An untethered kerbal can click a cable's end to pick it up.", smallStyle);
        }

        /// <summary>The row that decides which cable the reel keys drive, and which key picks it directly.</summary>
        private void DrawCableControls(TetherUserSettings user, TetherCore c, bool isSelected)
        {
            GUILayout.BeginHorizontal();
            bool want = GUILayout.Toggle(isSelected, isSelected ? " Controlled by the reel keys" : " Control with the reel keys");
            if (want != isSelected)
                TetherRegistry.Selected = want ? c : null;
            GUILayout.FlexibleSpace();
            GUILayout.Label("Key:", smallStyle, GUILayout.Width(28f));
            if (GUILayout.Button(c.Slot == 0 ? "-" : c.Slot.ToString(), GUILayout.Width(26f)))
                TetherRegistry.SetSlot(c, c.Slot >= TetherUserSettings.SlotCount ? 0 : c.Slot + 1);
            GUILayout.EndHorizontal();
            if (c.Slot > 0)
            {
                KeyCode k = user.slotKeys[c.Slot - 1];
                GUILayout.Label(k == KeyCode.None
                    ? "   Cable key " + c.Slot + " is not bound yet - set it on the Keys tab."
                    : "   [" + k + "] selects this cable.", k == KeyCode.None ? badStyle : smallStyle);
            }
        }

        private static void ReleaseFromApp(TetherCore c)
        {
            var kerbal = c.Owner as ModuleKerbalTether;
            if (kerbal != null)
            {
                kerbal.Release(true, kerbal.KerbalName + "'s tether released");
                return;
            }
            if (TetherScenario.Instance != null)
                TetherScenario.Instance.ReleaseCable(c, true);
        }

        // ---- Cables (styles) ---------------------------------------------------------------------

        private void DrawCables(TetherUserSettings user)
        {
            GUILayout.Label("EVA tethers", headerStyle);
            string eva = StylePicker(user.evaStyle);
            GUILayout.Space(6f);
            GUILayout.Label("Cables between ships", headerStyle);
            string cable = StylePicker(user.cableStyle);
            GUILayout.Space(6f);
            GUILayout.Label("Thickness: " + user.thickness.ToString("F2") + "x", smallStyle);
            float thickness = GUILayout.HorizontalSlider(user.thickness, 0.5f, 2f);
            thickness = Mathf.Round(thickness * 20f) / 20f;

            if (eva != user.evaStyle || cable != user.cableStyle || Math.Abs(thickness - user.thickness) > 1e-4f)
            {
                user.evaStyle = eva;
                user.cableStyle = cable;
                user.thickness = thickness;
                user.MarkDirty();
                CableStyles.NotifyChanged();
                CableStyles.PrewarmSelected();
            }
            GUILayout.Space(4f);
            GUILayout.Label("Changes apply to every tether immediately. Each style's weave, braid or corrugation is " +
                            "generated as a seamless tile; add your own with CABLE_STYLE nodes in Settings.cfg.", smallStyle);
        }

        private string StylePicker(string current)
        {
            string result = current;
            foreach (CableStyle s in CableStyles.All)
            {
                GUILayout.BeginHorizontal();
                Rect r = GUILayoutUtility.GetRect(18f, 18f, GUILayout.Width(18f), GUILayout.Height(18f));
                Color old = GUI.color;
                GUI.color = s.SwatchColor;
                GUI.DrawTexture(new Rect(r.x, r.y + 2f, r.width, s.Flat ? 7f : r.height - 2f), swatch);
                GUI.color = old;
                bool on = GUILayout.Toggle(s.Name == current, " " + s.Title + (s.Flat ? "  (flat)" : ""));
                if (on && s.Name != current)
                    result = s.Name;
                GUILayout.EndHorizontal();
            }
            return result;
        }

        // ---- Resources ---------------------------------------------------------------------------

        private void DrawResources(TetherUserSettings user)
        {
            bool master = GUILayout.Toggle(user.resourceTransfer, " Lifeline: carry power and life support down tethers");
            if (master != user.resourceTransfer)
            {
                user.resourceTransfer = master;
                user.MarkDirty();
            }

            GUILayout.Space(4f);
            foreach (LifeSupportDetection.Entry e in LifeSupportDetection.Detect())
                GUILayout.Label(e.Name + ": " + e.Status, e.Good ? goodStyle : badStyle);

            GUI.enabled = master;
            GUILayout.Space(4f);
            GUILayout.Label("Transfer speed: an empty suit fills in " + user.transferTime.ToString("F0") + " s", smallStyle);
            float t = Mathf.Round(GUILayout.HorizontalSlider(user.transferTime, 2f, 60f));
            if (Math.Abs(t - user.transferTime) > 0.5f)
            {
                user.transferTime = t;
                user.MarkDirty();
            }
            bool buddy = GUILayout.Toggle(user.buddySharing, " Kerbal-to-kerbal tethers share suit supplies");
            bool share = GUILayout.Toggle(user.cableSharingDefault, " New cables between ships share resources");
            if (buddy != user.buddySharing || share != user.cableSharingDefault)
            {
                user.buddySharing = buddy;
                user.cableSharingDefault = share;
                user.MarkDirty();
            }

            int hidden = 0;
            GUILayout.Space(6f);
            GUILayout.Label("Ship to kerbal", headerStyle);
            hidden += ResourceToggles(user, ResourceFlow.Supply);
            GUILayout.Space(4f);
            GUILayout.Label("Kerbal to ship", headerStyle);
            hidden += ResourceToggles(user, ResourceFlow.Waste);
            GUI.enabled = true;
            if (hidden > 0)
                GUILayout.Label(hidden + " resource(s) from mods you don't have are hidden.", smallStyle);
            GUILayout.Label("Only resources the kerbal's suit actually holds are moved; nothing is created or destroyed.", smallStyle);
        }

        private int ResourceToggles(TetherUserSettings user, ResourceFlow flow)
        {
            int hidden = 0;
            foreach (ResourceRule r in TetherResources.Rules)
            {
                if (r.Flow != flow)
                    continue;
                if (!TetherResources.IsDefined(r.Name) || !TetherResources.IsDefined(r.Source))
                {
                    hidden++;
                    continue;
                }
                bool on = user.IsResourceEnabled(r);
                bool now = GUILayout.Toggle(on, " " + r.Title + (r.Group != null ? "   (" + r.Group + ")" : ""));
                if (now != on)
                    user.SetResourceEnabled(r, now);
            }
            return hidden;
        }

        // ---- Keys --------------------------------------------------------------------------------

        private void DrawKeys(TetherUserSettings user)
        {
            GUILayout.Label("On EVA", headerStyle);
            KeyRow("Clip / clip free end / hold to release", KeyToggle, user.toggleKey);
            KeyRow("Reel in (hold)", KeyReelIn, user.reelInKey);
            KeyRow("Reel out (hold)", KeyReelOut, user.reelOutKey);

            GUILayout.Space(8f);
            GUILayout.Label("Cables between ships", headerStyle);
            bool fromShip = GUILayout.Toggle(user.cableKeysFromShip,
                " Reel keys drive the selected cable when you aren't flying a tethered kerbal");
            if (fromShip != user.cableKeysFromShip)
            {
                user.cableKeysFromShip = fromShip;
                user.MarkDirty();
            }
            KeyRow("Select the next cable", KeySelectNext, user.selectNextKey);
            KeyRow("Select the previous cable", KeySelectPrev, user.selectPrevKey);
            KeyRow("Release the selected cable", KeyReleaseSel, user.releaseSelectedKey);
            GUILayout.Space(4f);
            GUILayout.Label("Or give a cable a key of its own: set its number on the Tethers tab, then bind it here.", smallStyle);
            for (int i = 0; i < user.slotKeys.Length; i++)
            {
                TetherCore c = HighLogic.LoadedSceneIsFlight ? TetherRegistry.BySlot(i + 1) : null;
                KeyRow("Cable key " + (i + 1) + (c != null ? "  -  " + c.B.Title : ""), KeySlot1 + i, user.slotKeys[i]);
            }

            GUILayout.Space(6f);
            if (GUILayout.Button("Reset to defaults", GUILayout.Width(140f)))
            {
                user.ResetKeys();
                RefreshConflicts();
            }
            GUILayout.Space(4f);
            GUILayout.Label(capturing != KeyNone
                ? "Press the new key (Esc cancels)."
                : "Click a key to change it. EVA keys only act while you control a kerbal on EVA.", smallStyle);
        }

        private void KeyRow(string label, int action, KeyCode key)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, smallStyle, GUILayout.Width(230f));
            string text = capturing == action ? "press a key..." : key == KeyCode.None ? "(none)" : key.ToString();
            if (GUILayout.Button(text, GUILayout.Width(110f)))
            {
                capturing = capturing == action ? KeyNone : action;
                IsCapturingKey = capturing != KeyNone;
            }
            if (key != KeyCode.None && GUILayout.Button("x", GUILayout.Width(22f)))
                AssignKey(action, KeyCode.None);
            GUILayout.EndHorizontal();
            string clash;
            if (capturing != action && key != KeyCode.None && conflicts.TryGetValue(key, out clash))
                GUILayout.Label("   Also bound in KSP to: " + clash, badStyle);
        }

        private void AssignKey(int action, KeyCode key)
        {
            TetherUserSettings user = TetherUserSettings.Instance;
            switch (action)
            {
                case KeyToggle: user.toggleKey = key; break;
                case KeyReelIn: user.reelInKey = key; break;
                case KeyReelOut: user.reelOutKey = key; break;
                case KeySelectNext: user.selectNextKey = key; break;
                case KeySelectPrev: user.selectPrevKey = key; break;
                case KeyReleaseSel: user.releaseSelectedKey = key; break;
                default:
                    int slot = action - KeySlot1;
                    if (slot >= 0 && slot < user.slotKeys.Length)
                        user.slotKeys[slot] = key;
                    break;
            }
            user.MarkDirty();
            user.Save();
            RefreshConflicts();
        }

        // ---- Setup -------------------------------------------------------------------------------

        private void DrawSetup(TetherUserSettings user)
        {
            GUILayout.Label("How tethers hold on", headerStyle);
            int mode = (int)user.linkMode;
            int now = GUILayout.Toolbar(Mathf.Clamp(mode, 0, LinkModeNames.Length - 1), LinkModeNames);
            if (now != mode)
            {
                user.linkMode = (TetherLinkMode)now;
                user.MarkDirty();
                user.Save();
            }
            bool principia = TetherCompat.PrincipiaInstalled;
            switch (user.linkMode)
            {
                case TetherLinkMode.Joint:
                    GUILayout.Label("A PhysX joint, solved along with the rest of the ship. The usual choice.", smallStyle);
                    if (principia)
                        GUILayout.Label("Principia is installed: a joint between two vessels is overwritten by its own " +
                                        "integrator, so tethers will not pull. Use Automatic or Forces.", badStyle);
                    break;
                case TetherLinkMode.Forces:
                    GUILayout.Label("A force added to the part at each end. Slightly softer than a joint, and the only " +
                                    "kind of pull that mods which integrate vessels themselves keep.", smallStyle);
                    break;
                default:
                    GUILayout.Label(principia
                        ? "Principia is installed, so tethers pull with forces: it replaces what PhysX works out, and a " +
                          "joint between two vessels would be thrown away."
                        : "Nothing installed needs special handling, so tethers use a joint.", principia ? goodStyle : smallStyle);
                    break;
            }

            GUILayout.Space(8f);
            GUILayout.Label("Detected", headerStyle);
            Detected("Principia", principia, "tethers pull with forces", "not installed");
            Detected("KAS", TetherCompat.KasInstalled, "winches, ports and pylons are tether points", "not installed");
            Detected("ToolbarControl", TetherToolbar.UsingToolbarControl, "stock and Blizzy's toolbars", "stock launcher only");
            Detected("ClickThroughBlocker", GuiWindow.UsingClickThroughBlocker, "clicks stay in this window", "using an input lock");

            GUILayout.Space(8f);
            GUILayout.Label("Cheats", headerStyle);
            TetherCheatSettings cheats = TetherCheatSettings.Current;
            string summary = cheats.Summary;
            GUILayout.Label(summary == null
                ? "Off. Difficulty Settings > KSP Tethers > Cheats has infinite length, reel speed, reach, strength " +
                  "and crazy physics."
                : "On: " + summary, summary == null ? smallStyle : badStyle);

            GUILayout.Space(8f);
            GUILayout.Label("Tether points", headerStyle);
            GUILayout.Label("Docking ports, claws, ladders, crewed parts and - with KAS - winches, ports and pylons can be " +
                            "clipped to with one right-click, and two of them can be rigged together in the editor so the " +
                            "craft launches with a cable already strung between them.", smallStyle);
        }

        private void Detected(string name, bool present, string yes, string no)
        {
            GUILayout.Label(name + ": " + (present ? yes : no), present ? goodStyle : smallStyle);
        }

        /// <summary>Finds KSP key bindings that share our keys, so the player can see clashes.</summary>
        private void RefreshConflicts()
        {
            conflicts.Clear();
            TetherUserSettings user = TetherUserSettings.Instance;
            var ours = new List<KeyCode>
            {
                user.toggleKey, user.reelInKey, user.reelOutKey,
                user.selectNextKey, user.selectPrevKey, user.releaseSelectedKey
            };
            ours.AddRange(user.slotKeys);
            try
            {
                foreach (FieldInfo f in typeof(GameSettings).GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    if (f.FieldType != typeof(KeyBinding))
                        continue;
                    var kb = f.GetValue(null) as KeyBinding;
                    if (kb == null)
                        continue;
                    foreach (KeyCode k in ours)
                    {
                        if (k == KeyCode.None)
                            continue;
                        if ((kb.primary != null && kb.primary.code == k) || (kb.secondary != null && kb.secondary.code == k))
                        {
                            string existing;
                            conflicts.TryGetValue(k, out existing);
                            conflicts[k] = string.IsNullOrEmpty(existing) ? f.Name : existing + ", " + f.Name;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                TetherLog.Exception("Checking key conflicts", e);
            }
        }
    }
}

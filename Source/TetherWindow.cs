using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace KSPTethers
{
    /// <summary>
    /// The toolbar app: manage live tethers and cables, pick cable styles, switch the lifeline (resource
    /// transfer) and its resources on or off, and rebind keys. Available in flight and at the Space Center.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.FlightAndKSC, false)]
    public class TetherWindow : MonoBehaviour
    {
        private enum KeyAction { None, Toggle, ReelIn, ReelOut }

        private const string LockId = "KSPTethers_Window";
        private static readonly string[] TabNames = { "Tethers", "Cables", "Resources", "Keys" };

        /// <summary>True while waiting for the player to press a key to bind; tether keys are ignored meanwhile.</summary>
        public static bool IsCapturingKey { get; private set; }

        private TetherToolbar toolbar;
        private bool visible;
        private Rect rect;
        private int windowId;
        private Vector2 scroll;
        private KeyAction capturing;
        private bool locked;
        private Texture2D swatch;
        private GUIStyle headerStyle, smallStyle, goodStyle, badStyle;
        private readonly Dictionary<KeyCode, string> conflicts = new Dictionary<KeyCode, string>();

        private void Start()
        {
            windowId = GetInstanceID();
            TetherUserSettings user = TetherUserSettings.Instance;
            float x = user.windowX >= 0 ? user.windowX : Screen.width - 460f;
            float y = user.windowY >= 0 ? user.windowY : 90f;
            rect = new Rect(x, y, 400f, 470f);
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
            capturing = KeyAction.None;
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
            if (capturing != KeyAction.None && Event.current.type == EventType.KeyDown && Event.current.keyCode != KeyCode.None)
            {
                KeyCode k = Event.current.keyCode;
                if (k != KeyCode.Escape)
                    AssignKey(capturing, k);
                capturing = KeyAction.None;
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
                case 0: DrawTethers(); break;
                case 1: DrawCables(user); break;
                case 2: DrawResources(user); break;
                default: DrawKeys(user); break;
            }
            GUILayout.EndScrollView();
            GUI.DragWindow(new Rect(0f, 0f, rect.width - 28f, 22f));
        }

        // ---- Tethers -----------------------------------------------------------------------------

        private void DrawTethers()
        {
            if (!HighLogic.LoadedSceneIsFlight)
            {
                GUILayout.Label("Tethers and cables are listed here during flight.", smallStyle);
                return;
            }
            string key = TetherUserSettings.Instance.toggleKey.ToString();
            int shown = 0;
            List<TetherCore> all = TetherRegistry.All;
            for (int i = 0; i < all.Count; i++)
            {
                TetherCore c = all[i];
                if (c.Released)
                    continue;
                shown++;
                GUILayout.BeginVertical(HighLogic.Skin.box);
                bool cable = c.Kind == TetherKind.Vessel;
                GUILayout.Label((cable ? "Cable: " : "") + (cable ? c.A.LongTitle : c.A.Title) + "  <->  " + (cable ? c.B.LongTitle : c.B.Title), headerStyle);
                if (c.Attached)
                    GUILayout.Label("Distance " + c.Distance.ToString("F1") + " m   length " + c.LengthLimit.ToString("F1") +
                                    " m   rope out " + c.RopeLength.ToString("F1") + " m" + (c.ReelingIn ? "   (reeling in)" : c.ReelingOut ? "   (reeling out)" : ""), smallStyle);
                else
                    GUILayout.Label("Waiting for both ends to load", smallStyle);

                GUILayout.BeginHorizontal();
                if (GUILayout.RepeatButton("<< Reel in", GUILayout.Width(95f)))
                    c.HoldReelFromApp(true);
                if (GUILayout.RepeatButton("Reel out >>", GUILayout.Width(95f)))
                    c.HoldReelFromApp(false);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Release", GUILayout.Width(80f)))
                    ReleaseFromApp(c);
                GUILayout.EndHorizontal();

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
            }
            GUILayout.Space(4f);
            GUILayout.Label("Changes apply to every tether immediately. Add your own looks with CABLE_STYLE nodes in Settings.cfg.", smallStyle);
        }

        private string StylePicker(string current)
        {
            string result = current;
            foreach (CableStyle s in CableStyles.All)
            {
                GUILayout.BeginHorizontal();
                Rect r = GUILayoutUtility.GetRect(18f, 18f, GUILayout.Width(18f), GUILayout.Height(18f));
                Color old = GUI.color;
                GUI.color = s.Color;
                GUI.DrawTexture(new Rect(r.x, r.y + 2f, r.width, r.height - 2f), swatch);
                GUI.color = old;
                bool on = GUILayout.Toggle(s.Name == current, " " + s.Title);
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
            KeyRow("Clip / clip free end / hold to release", KeyAction.Toggle, user.toggleKey);
            KeyRow("Reel in (hold)", KeyAction.ReelIn, user.reelInKey);
            KeyRow("Reel out (hold)", KeyAction.ReelOut, user.reelOutKey);
            GUILayout.Space(6f);
            if (GUILayout.Button("Reset to defaults", GUILayout.Width(140f)))
            {
                user.ResetKeys();
                RefreshConflicts();
            }
            GUILayout.Space(4f);
            GUILayout.Label(capturing != KeyAction.None
                ? "Press the new key (Esc cancels)."
                : "Click a key to change it. Keys only act while you control an EVA kerbal.", smallStyle);
        }

        private void KeyRow(string label, KeyAction action, KeyCode key)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(230f));
            string text = capturing == action ? "press a key..." : key.ToString();
            if (GUILayout.Button(text, GUILayout.Width(120f)))
            {
                capturing = capturing == action ? KeyAction.None : action;
                IsCapturingKey = capturing != KeyAction.None;
            }
            GUILayout.EndHorizontal();
            string clash;
            if (capturing != action && conflicts.TryGetValue(key, out clash))
                GUILayout.Label("   Also bound in KSP to: " + clash, badStyle);
        }

        private void AssignKey(KeyAction action, KeyCode key)
        {
            TetherUserSettings user = TetherUserSettings.Instance;
            switch (action)
            {
                case KeyAction.Toggle: user.toggleKey = key; break;
                case KeyAction.ReelIn: user.reelInKey = key; break;
                case KeyAction.ReelOut: user.reelOutKey = key; break;
            }
            user.MarkDirty();
            user.Save();
            RefreshConflicts();
        }

        /// <summary>Finds KSP key bindings that share our keys, so the player can see clashes.</summary>
        private void RefreshConflicts()
        {
            conflicts.Clear();
            TetherUserSettings user = TetherUserSettings.Instance;
            var ours = new[] { user.toggleKey, user.reelInKey, user.reelOutKey };
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

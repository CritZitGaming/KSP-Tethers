using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace KSPTethers
{
    /// <summary>
    /// The player's choices from the toolbar app (keys, cable styles, resource transfer), saved to
    /// GameData/KSPTethers/PluginData/UserSettings.cfg. PluginData is not read by the game database, so
    /// ModuleManager never touches it and it survives reinstalling the mod's configs.
    /// </summary>
    internal sealed class TetherUserSettings
    {
        private static TetherUserSettings instance;

        public static TetherUserSettings Instance
        {
            get
            {
                if (instance == null)
                    instance = Load();
                return instance;
            }
        }

        public KeyCode toggleKey;
        public KeyCode reelInKey;
        public KeyCode reelOutKey;
        public KeyCode selectNextKey;
        public KeyCode selectPrevKey;
        public KeyCode releaseSelectedKey;
        /// <summary>A key of its own for each of the first few cables; unbound by default.</summary>
        public readonly KeyCode[] slotKeys = new KeyCode[SlotCount];

        public const int SlotCount = 4;

        /// <summary>The reel keys drive the selected cable whenever you are not flying a tethered kerbal.</summary>
        public bool cableKeysFromShip = true;
        /// <summary>Joint, forces, or whichever suits the mods that are installed.</summary>
        public TetherLinkMode linkMode = TetherLinkMode.Auto;

        public string evaStyle = "umbilical";
        public string cableStyle = "steel";
        public float thickness = 1f;

        public bool resourceTransfer = true;
        public bool buddySharing = true;
        public bool cableSharingDefault;
        public float transferTime = 10f;
        private readonly Dictionary<string, bool> resources = new Dictionary<string, bool>();

        public float windowX = -1f;
        public float windowY = -1f;
        public int tab;

        private bool dirty;

        private static string FilePath =>
            Path.Combine(KSPUtil.ApplicationRootPath, "GameData/KSPTethers/PluginData/UserSettings.cfg");

        private TetherUserSettings()
        {
            TetherConfig cfg = TetherConfig.Instance;
            linkMode = cfg.linkMode;
            ResetKeys(false);
        }

        public void ResetKeys()
        {
            ResetKeys(true);
        }

        private void ResetKeys(bool dirty)
        {
            TetherConfig cfg = TetherConfig.Instance;
            toggleKey = cfg.toggleKey;
            reelInKey = cfg.reelInKey;
            reelOutKey = cfg.reelOutKey;
            selectNextKey = cfg.selectNextKey;
            selectPrevKey = cfg.selectPrevKey;
            releaseSelectedKey = KeyCode.None;
            for (int i = 0; i < slotKeys.Length; i++)
                slotKeys[i] = KeyCode.None;
            if (dirty)
                MarkDirty();
        }

        public bool IsResourceEnabled(ResourceRule rule)
        {
            bool on;
            return resources.TryGetValue(rule.Name, out on) ? on : rule.DefaultOn;
        }

        public void SetResourceEnabled(ResourceRule rule, bool on)
        {
            resources[rule.Name] = on;
            MarkDirty();
        }

        public void MarkDirty()
        {
            dirty = true;
        }

        public void SaveIfDirty()
        {
            if (dirty)
                Save();
        }

        public void Save()
        {
            dirty = false;
            try
            {
                var root = new ConfigNode();
                ConfigNode n = root.AddNode("KSP_TETHERS_USER");
                n.AddValue("toggleKey", toggleKey.ToString());
                n.AddValue("reelInKey", reelInKey.ToString());
                n.AddValue("reelOutKey", reelOutKey.ToString());
                n.AddValue("selectNextKey", selectNextKey.ToString());
                n.AddValue("selectPrevKey", selectPrevKey.ToString());
                n.AddValue("releaseSelectedKey", releaseSelectedKey.ToString());
                for (int i = 0; i < slotKeys.Length; i++)
                    n.AddValue("slotKey" + (i + 1), slotKeys[i].ToString());
                n.AddValue("cableKeysFromShip", cableKeysFromShip.ToString());
                n.AddValue("linkMode", linkMode.ToString());
                n.AddValue("evaStyle", evaStyle);
                n.AddValue("cableStyle", cableStyle);
                n.AddValue("thickness", thickness.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                n.AddValue("resourceTransfer", resourceTransfer.ToString());
                n.AddValue("buddySharing", buddySharing.ToString());
                n.AddValue("cableSharingDefault", cableSharingDefault.ToString());
                n.AddValue("transferTime", transferTime.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                n.AddValue("windowX", windowX.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                n.AddValue("windowY", windowY.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                n.AddValue("tab", tab.ToString());
                ConfigNode res = n.AddNode("RESOURCES");
                foreach (KeyValuePair<string, bool> kv in resources)
                {
                    ConfigNode r = res.AddNode("RESOURCE");
                    r.AddValue("name", kv.Key);
                    r.AddValue("enabled", kv.Value.ToString());
                }
                string path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                root.Save(path);
            }
            catch (Exception e)
            {
                TetherLog.Exception("Saving user settings", e);
            }
        }

        private static TetherUserSettings Load()
        {
            var s = new TetherUserSettings();
            try
            {
                string path = FilePath;
                if (!File.Exists(path))
                    return s;
                ConfigNode root = ConfigNode.Load(path);
                ConfigNode n = root != null ? root.GetNode("KSP_TETHERS_USER") : null;
                if (n == null)
                    return s;
                s.toggleKey = ParseKey(n.GetValue("toggleKey"), s.toggleKey);
                s.reelInKey = ParseKey(n.GetValue("reelInKey"), s.reelInKey);
                s.reelOutKey = ParseKey(n.GetValue("reelOutKey"), s.reelOutKey);
                s.selectNextKey = ParseKey(n.GetValue("selectNextKey"), s.selectNextKey);
                s.selectPrevKey = ParseKey(n.GetValue("selectPrevKey"), s.selectPrevKey);
                s.releaseSelectedKey = ParseKey(n.GetValue("releaseSelectedKey"), s.releaseSelectedKey);
                for (int i = 0; i < s.slotKeys.Length; i++)
                    s.slotKeys[i] = ParseKey(n.GetValue("slotKey" + (i + 1)), s.slotKeys[i]);
                n.TryGetValue("cableKeysFromShip", ref s.cableKeysFromShip);
                s.linkMode = ParseLinkMode(n.GetValue("linkMode"), s.linkMode);
                n.TryGetValue("evaStyle", ref s.evaStyle);
                n.TryGetValue("cableStyle", ref s.cableStyle);
                n.TryGetValue("thickness", ref s.thickness);
                n.TryGetValue("resourceTransfer", ref s.resourceTransfer);
                n.TryGetValue("buddySharing", ref s.buddySharing);
                n.TryGetValue("cableSharingDefault", ref s.cableSharingDefault);
                n.TryGetValue("transferTime", ref s.transferTime);
                n.TryGetValue("windowX", ref s.windowX);
                n.TryGetValue("windowY", ref s.windowY);
                n.TryGetValue("tab", ref s.tab);
                ConfigNode res = n.GetNode("RESOURCES");
                if (res != null)
                {
                    foreach (ConfigNode r in res.GetNodes("RESOURCE"))
                    {
                        string name = r.GetValue("name");
                        bool on = true;
                        if (!string.IsNullOrEmpty(name) && r.TryGetValue("enabled", ref on))
                            s.resources[name] = on;
                    }
                }
                s.thickness = Mathf.Clamp(s.thickness, 0.25f, 3f);
                s.transferTime = Mathf.Clamp(s.transferTime, 1f, 600f);
            }
            catch (Exception e)
            {
                TetherLog.Exception("Loading user settings", e);
            }
            return s;
        }

        public static TetherLinkMode ParseLinkMode(string s, TetherLinkMode fallback)
        {
            if (string.IsNullOrEmpty(s))
                return fallback;
            try
            {
                return (TetherLinkMode)Enum.Parse(typeof(TetherLinkMode), s.Trim(), true);
            }
            catch
            {
                return fallback;
            }
        }

        private static KeyCode ParseKey(string s, KeyCode fallback)
        {
            if (string.IsNullOrEmpty(s))
                return fallback;
            try
            {
                return (KeyCode)Enum.Parse(typeof(KeyCode), s, true);
            }
            catch
            {
                return fallback;
            }
        }
    }
}

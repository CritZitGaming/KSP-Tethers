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
            toggleKey = cfg.toggleKey;
            reelInKey = cfg.reelInKey;
            reelOutKey = cfg.reelOutKey;
        }

        public void ResetKeys()
        {
            TetherConfig cfg = TetherConfig.Instance;
            toggleKey = cfg.toggleKey;
            reelInKey = cfg.reelInKey;
            reelOutKey = cfg.reelOutKey;
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

using System;
using System.Reflection;
using KSP.UI.Screens;
using UnityEngine;

namespace KSPTethers
{
    /// <summary>
    /// The app button. With ToolbarControl installed the button lives on the stock launcher and/or Blizzy's
    /// toolbar (players choose in ToolbarControl's own settings); without it, a plain stock launcher button.
    /// ToolbarControl is used through reflection so it stays an optional dependency.
    /// </summary>
    internal sealed class TetherToolbar
    {
        public const string NameSpace = "KSPTethers";
        private const string ToolbarId = "KSPTethers_App";
        private const string IconLarge = "KSPTethers/Icons/tether_38";
        private const string IconSmall = "KSPTethers/Icons/tether_24";
        private const ApplicationLauncher.AppScenes Scenes =
            ApplicationLauncher.AppScenes.FLIGHT | ApplicationLauncher.AppScenes.MAPVIEW | ApplicationLauncher.AppScenes.SPACECENTER;

        private static Type toolbarControlType;
        private static bool searched;

        private readonly GameObject host;
        private readonly Action onOpen;
        private readonly Action onClose;
        private Component toolbarControl;
        private ApplicationLauncherButton stockButton;
        private bool destroyed;

        private static Type ToolbarControlType
        {
            get
            {
                if (!searched)
                {
                    searched = true;
                    toolbarControlType = Reflect.FindType("ToolbarControl_NS.ToolbarControl");
                }
                return toolbarControlType;
            }
        }

        public static bool UsingToolbarControl => ToolbarControlType != null;

        /// <summary>Registers with ToolbarControl (call once, at the main menu) so players can pick stock/Blizzy.</summary>
        public static void RegisterWithToolbarControl()
        {
            Type t = ToolbarControlType;
            if (t == null)
                return;
            try
            {
                MethodInfo register = t.GetMethod("RegisterMod", BindingFlags.Public | BindingFlags.Static);
                if (register != null)
                    register.Invoke(null, new object[] { NameSpace, "KSP Tethers", false, true, true });
            }
            catch (Exception e)
            {
                TetherLog.Exception("Registering with ToolbarControl", e);
            }
        }

        public TetherToolbar(GameObject host, Action onOpen, Action onClose)
        {
            this.host = host;
            this.onOpen = onOpen;
            this.onClose = onClose;
            if (!TryCreateToolbarControl())
                CreateStockButton();
        }

        private bool TryCreateToolbarControl()
        {
            Type t = ToolbarControlType;
            if (t == null)
                return false;
            try
            {
                toolbarControl = host.AddComponent(t);
                Type handler = t.GetNestedType("TC_ClickHandler");
                var callbacks = new ClickCallbacks(this);
                Delegate onTrue = Delegate.CreateDelegate(handler, callbacks, typeof(ClickCallbacks).GetMethod("OnTrue"));
                Delegate onFalse = Delegate.CreateDelegate(handler, callbacks, typeof(ClickCallbacks).GetMethod("OnFalse"));
                MethodInfo add = t.GetMethod("AddToAllToolbars", new[]
                {
                    handler, handler, typeof(ApplicationLauncher.AppScenes),
                    typeof(string), typeof(string), typeof(string), typeof(string), typeof(string)
                });
                if (add == null)
                    throw new MissingMethodException("ToolbarControl.AddToAllToolbars");
                add.Invoke(toolbarControl, new object[] { onTrue, onFalse, Scenes, NameSpace, ToolbarId, IconLarge, IconSmall, "KSP Tethers" });
                return true;
            }
            catch (Exception e)
            {
                TetherLog.Exception("ToolbarControl button failed; falling back to the stock launcher", e);
                if (toolbarControl != null)
                    UnityEngine.Object.Destroy(toolbarControl);
                toolbarControl = null;
                return false;
            }
        }

        private void CreateStockButton()
        {
            GameEvents.onGUIApplicationLauncherReady.Add(AddStockButton);
            GameEvents.onGUIApplicationLauncherDestroyed.Add(RemoveStockButton);
            if (ApplicationLauncher.Ready)
                AddStockButton();
        }

        private void AddStockButton()
        {
            if (destroyed || stockButton != null || ApplicationLauncher.Instance == null)
                return;
            Texture tex = GameDatabase.Instance.ExistsTexture(IconLarge) ? GameDatabase.Instance.GetTexture(IconLarge, false) : Texture2D.whiteTexture;
            stockButton = ApplicationLauncher.Instance.AddModApplication(
                () => onOpen(), () => onClose(), Noop, Noop, Noop, Noop, Scenes, tex);
        }

        private void RemoveStockButton()
        {
            if (stockButton != null && ApplicationLauncher.Instance != null)
                ApplicationLauncher.Instance.RemoveModApplication(stockButton);
            stockButton = null;
        }

        private static void Noop()
        {
        }

        /// <summary>Pops the button back up (when the window is closed with its own X).</summary>
        public void SetOff()
        {
            try
            {
                if (toolbarControl != null)
                {
                    MethodInfo m = toolbarControl.GetType().GetMethod("SetFalse", new[] { typeof(bool) });
                    if (m != null)
                        m.Invoke(toolbarControl, new object[] { false });
                }
                else if (stockButton != null)
                {
                    stockButton.SetFalse(false);
                }
            }
            catch (Exception e)
            {
                TetherLog.Exception("Resetting the toolbar button", e);
            }
        }

        public void Destroy()
        {
            destroyed = true;
            GameEvents.onGUIApplicationLauncherReady.Remove(AddStockButton);
            GameEvents.onGUIApplicationLauncherDestroyed.Remove(RemoveStockButton);
            RemoveStockButton();
            if (toolbarControl != null)
            {
                try
                {
                    MethodInfo m = toolbarControl.GetType().GetMethod("OnDestroy", Type.EmptyTypes);
                    if (m != null)
                        m.Invoke(toolbarControl, null);
                }
                catch (Exception e)
                {
                    TetherLog.Exception("Removing the ToolbarControl button", e);
                }
                UnityEngine.Object.Destroy(toolbarControl);
                toolbarControl = null;
            }
        }

        /// <summary>Public targets for ToolbarControl's click delegates.</summary>
        public sealed class ClickCallbacks
        {
            private readonly TetherToolbar owner;

            public ClickCallbacks(TetherToolbar owner)
            {
                this.owner = owner;
            }

            public void OnTrue()
            {
                owner.onOpen();
            }

            public void OnFalse()
            {
                owner.onClose();
            }
        }
    }

    /// <summary>
    /// Draws IMGUI windows through ClickThroughBlocker when it is installed, so clicks on the window don't
    /// reach the game; otherwise plain GUILayout windows with a manual input lock.
    /// </summary>
    internal static class GuiWindow
    {
        private delegate Rect CtbWindow(int id, Rect rect, GUI.WindowFunction func, string text, GUILayoutOption[] options);

        private static readonly GUILayoutOption[] NoOptions = new GUILayoutOption[0];
        private static CtbWindow ctb;
        private static bool searched;

        public static bool UsingClickThroughBlocker
        {
            get
            {
                Search();
                return ctb != null;
            }
        }

        private static void Search()
        {
            if (searched)
                return;
            searched = true;
            Type t = Reflect.FindType("ClickThroughFix.ClickThruBlocker");
            if (t == null)
                return;
            MethodInfo m = t.GetMethod("GUILayoutWindow", BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(int), typeof(Rect), typeof(GUI.WindowFunction), typeof(string), typeof(GUILayoutOption[]) }, null);
            if (m == null)
                return;
            try
            {
                ctb = (CtbWindow)Delegate.CreateDelegate(typeof(CtbWindow), m);
            }
            catch (Exception e)
            {
                TetherLog.Exception("Binding ClickThroughBlocker", e);
            }
        }

        public static Rect Draw(int id, Rect rect, GUI.WindowFunction func, string title)
        {
            Search();
            return ctb != null ? ctb(id, rect, func, title, NoOptions) : GUILayout.Window(id, rect, func, title, NoOptions);
        }
    }

    internal static class Reflect
    {
        public static Type FindType(string fullName)
        {
            foreach (AssemblyLoader.LoadedAssembly a in AssemblyLoader.loadedAssemblies)
            {
                try
                {
                    Type t = a.assembly != null ? a.assembly.GetType(fullName, false) : null;
                    if (t != null)
                        return t;
                }
                catch
                {
                    // Assemblies with missing dependencies can throw here; skip them.
                }
            }
            return null;
        }
    }
}

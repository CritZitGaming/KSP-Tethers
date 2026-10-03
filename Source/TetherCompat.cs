using System;
using System.Reflection;
using UnityEngine;

namespace KSPTethers
{
    /// <summary>
    /// What other mods are installed, and the small amount of reflection needed to work with them. Nothing
    /// here is a build-time dependency: every lookup fails quietly into "not installed".
    /// </summary>
    internal static class TetherCompat
    {
        private static int principia = -1;
        private static int kas = -1;
        private static Type linkPeer;
        private static PropertyInfo peerNodeTransform;
        private static PropertyInfo peerLinkType;

        /// <summary>
        /// True when Principia is installed. Principia integrates every vessel itself and overwrites what
        /// PhysX worked out, so a joint between two vessels does nothing; forces added to a part are read
        /// and honoured, which is why the tether can switch to pulling with forces instead.
        /// </summary>
        public static bool PrincipiaInstalled
        {
            get
            {
                if (principia < 0)
                {
                    principia = 0;
                    try
                    {
                        foreach (AssemblyLoader.LoadedAssembly a in AssemblyLoader.loadedAssemblies)
                        {
                            string n = a != null ? a.name : null;
                            if (string.IsNullOrEmpty(n))
                                continue;
                            if (n.IndexOf("principia", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                n.IndexOf("ksp_plugin_adapter", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                principia = 1;
                                break;
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        TetherLog.Exception("Looking for Principia", e);
                    }
                    if (principia == 1)
                        TetherLog.Info("Principia detected: tethers will pull with forces rather than a joint.");
                }
                return principia == 1;
            }
        }

        public static bool KasInstalled
        {
            get
            {
                if (kas < 0)
                {
                    linkPeer = Reflect.FindType("KASAPIv2.ILinkPeer") ?? Reflect.FindType("KASAPIv1.ILinkPeer");
                    if (linkPeer != null)
                    {
                        peerNodeTransform = linkPeer.GetProperty("nodeTransform");
                        peerLinkType = linkPeer.GetProperty("cfgLinkType");
                    }
                    kas = linkPeer != null ? 1 : 0;
                    if (kas == 1)
                        TetherLog.Info("KAS detected: winches, ports and pylons can be used as tether points.");
                }
                return kas == 1;
            }
        }

        /// <summary>
        /// The socket a tether should clip to on a KAS winch, port or pylon: the transform KAS itself runs
        /// its cable from, so our rope leaves the same place theirs would. Null if the part has no KAS link
        /// point (or KAS isn't installed).
        /// </summary>
        public static Transform KasSocket(Part p, out string label)
        {
            label = null;
            if (p == null || !KasInstalled || peerNodeTransform == null)
                return null;
            try
            {
                for (int i = 0; i < p.Modules.Count; i++)
                {
                    PartModule m = p.Modules[i];
                    if (m == null || !linkPeer.IsInstanceOfType(m))
                        continue;
                    var t = peerNodeTransform.GetValue(m, null) as Transform;
                    if (t == null)
                        continue;
                    label = peerLinkType != null ? peerLinkType.GetValue(m, null) as string : null;
                    return t;
                }
            }
            catch (Exception e)
            {
                TetherLog.Exception("Reading a KAS link point", e);
            }
            return null;
        }

        /// <summary>The KAS socket nearest <paramref name="worldPoint"/>, if one is within reach of the click.</summary>
        public static bool TrySnapToKasSocket(Part p, ref Vector3 worldPoint, ref Vector3 worldNormal, float maxDistance, out string label)
        {
            label = null;
            Transform socket = KasSocket(p, out label);
            if (socket == null)
                return false;
            Vector3 at = socket.position;
            if ((at - worldPoint).sqrMagnitude > maxDistance * maxDistance)
            {
                label = null;
                return false;
            }
            worldPoint = at;
            worldNormal = socket.forward.sqrMagnitude > 1e-6f ? socket.forward : worldNormal;
            return true;
        }
    }
}

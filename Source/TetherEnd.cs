using System.Globalization;
using UnityEngine;

namespace KSPTethers
{
    /// <summary>
    /// One end of a tether: a clip on a part's surface, or a kerbal's backpack reel. An end can be saved
    /// before its part is loaded and resolved later by the part's persistent id.
    /// </summary>
    internal sealed class TetherEnd
    {
        public Part Part { get; private set; }
        public bool IsKerbal { get; private set; }
        public uint Pid { get; private set; }
        public uint FlightId { get; private set; }
        public Vector3 LocalPos;
        public Vector3 LocalNormal = Vector3.up;

        private Transform bone;
        private Transform torso;
        private bool bound;

        private TetherEnd()
        {
        }

        /// <summary>The backpack of an EVA kerbal.</summary>
        public static TetherEnd Kerbal(Part kerbal)
        {
            var e = new TetherEnd();
            e.Bind(kerbal);
            return e;
        }

        /// <summary>A clip at a point on a part (or a kerbal's backpack if the part is an EVA kerbal).</summary>
        public static TetherEnd AtPoint(Part p, Vector3 worldPoint, Vector3 worldNormal)
        {
            var e = new TetherEnd();
            e.Bind(p);
            if (!e.IsKerbal)
            {
                if (worldNormal.sqrMagnitude < 1e-8f)
                    worldNormal = worldPoint - p.transform.position;
                if (worldNormal.sqrMagnitude < 1e-8f)
                    worldNormal = p.transform.up;
                worldNormal.Normalize();
                e.LocalPos = p.transform.InverseTransformPoint(worldPoint + worldNormal * 0.004f);
                e.LocalNormal = p.transform.InverseTransformDirection(worldNormal);
            }
            return e;
        }

        /// <summary>An end restored from a save whose part may not be loaded yet.</summary>
        public static TetherEnd Unresolved(uint pid, uint flightId, Vector3 localPos, Vector3 localNormal)
        {
            return new TetherEnd { Pid = pid, FlightId = flightId, LocalPos = localPos, LocalNormal = localNormal };
        }

        public bool IsBound => bound;

        /// <summary>The part has been found and still exists.</summary>
        public bool IsAlive => bound && Part != null && Part.State != PartStates.DEAD && Part.vessel != null;

        /// <summary>The part was found once but has since been destroyed.</summary>
        public bool WasLost => bound && !IsAlive;

        public Vessel Vessel => IsAlive ? Part.vessel : null;

        public void Bind(Part p)
        {
            Part = p;
            bound = p != null;
            if (p == null)
                return;
            Pid = p.persistentId;
            FlightId = p.flightID;
            KerbalEVA eva = p.vessel != null && p.vessel.isEVA ? p.FindModuleImplementing<KerbalEVA>() : null;
            IsKerbal = eva != null;
            bone = IsKerbal ? FindDeep(p.transform, TetherConfig.Instance.kerbalBone) : null;
            torso = IsKerbal ? eva.upperTorso : null;
            if (IsKerbal && bone == null)
                TetherLog.Warn("Bone '" + TetherConfig.Instance.kerbalBone + "' not found on " + p.name + "; using an approximate attach point.");
        }

        /// <summary>Finds the part among loaded vessels; true once bound.</summary>
        public bool TryResolve()
        {
            if (bound)
                return IsAlive;
            Part p = FindLoadedPart(Pid, FlightId);
            if (p == null)
                return false;
            Bind(p);
            return true;
        }

        public Vector3 WorldPos
        {
            get
            {
                if (IsKerbal)
                {
                    if (bone != null)
                        return bone.TransformPoint(TetherConfig.Instance.kerbalOffset);
                    Transform t = Part.transform;
                    return t.position + t.up * 0.2f - t.forward * 0.18f;
                }
                return Part.transform.TransformPoint(LocalPos);
            }
        }

        /// <summary>Direction the tether leaves this end (out of the backpack, or out of the hull).</summary>
        public Vector3 WorldDir
        {
            get
            {
                if (IsKerbal)
                {
                    Transform from = torso != null ? torso : bone;
                    if (from != null)
                    {
                        Vector3 d = WorldPos - from.position;
                        if (d.sqrMagnitude > 1e-6f)
                            return d.normalized;
                    }
                    return -Part.transform.forward;
                }
                Vector3 n = Part.transform.TransformDirection(LocalNormal);
                return n.sqrMagnitude > 1e-8f ? n.normalized : Vector3.up;
            }
        }

        /// <summary>Keeps the hose texture from spinning around the rope at this end.</summary>
        public Vector3 RefUp => IsKerbal && bone != null ? bone.up : Part.transform.up;

        /// <summary>The rigidbody carrying this end (physicsless parts ride on their parent's).</summary>
        public Rigidbody Body => BodyOf(Part);

        public static Rigidbody BodyOf(Part p)
        {
            int guard = 0;
            while (p != null && p.rb == null && guard++ < 64)
                p = p.parent;
            return p != null ? p.rb : null;
        }

        public string Title
        {
            get
            {
                if (!IsAlive)
                    return "(not loaded)";
                if (IsKerbal)
                    return Part.vessel.GetDisplayName();
                return Part.partInfo != null ? Part.partInfo.title : Part.name;
            }
        }

        /// <summary>Title including the vessel, for lists of ship cables.</summary>
        public string LongTitle
        {
            get
            {
                if (!IsAlive || IsKerbal)
                    return Title;
                return Part.vessel.GetDisplayName() + " (" + Title + ")";
            }
        }

        public void Save(ConfigNode node, string prefix)
        {
            node.AddValue(prefix + "Pid", Pid.ToString(CultureInfo.InvariantCulture));
            node.AddValue(prefix + "FlightId", FlightId.ToString(CultureInfo.InvariantCulture));
            node.AddValue(prefix + "Pos", FormatVector(LocalPos));
            node.AddValue(prefix + "Normal", FormatVector(LocalNormal));
        }

        public static TetherEnd Load(ConfigNode node, string prefix)
        {
            uint pid = 0, fid = 0;
            Vector3 pos = Vector3.zero, normal = Vector3.up;
            node.TryGetValue(prefix + "Pid", ref pid);
            node.TryGetValue(prefix + "FlightId", ref fid);
            node.TryGetValue(prefix + "Pos", ref pos);
            node.TryGetValue(prefix + "Normal", ref normal);
            return Unresolved(pid, fid, pos, normal);
        }

        public static Part FindLoadedPart(uint pid, uint flightId)
        {
            Part p;
            if (pid != 0 && FlightGlobals.FindLoadedPart(pid, out p) && p != null)
                return p;
            if (flightId != 0)
            {
                p = FlightGlobals.FindPartByID(flightId);
                if (p != null)
                    return p;
            }
            return null;
        }

        /// <summary>True if a part with this id still exists anywhere in the game, loaded or not.</summary>
        public static bool ExistsAnywhere(uint pid)
        {
            if (pid == 0)
                return false;
            Part p;
            if (FlightGlobals.FindLoadedPart(pid, out p) && p != null)
                return true;
            foreach (Vessel v in FlightGlobals.Vessels)
            {
                if (v == null || v.loaded || v.protoVessel == null)
                    continue;
                foreach (ProtoPartSnapshot pp in v.protoVessel.protoPartSnapshots)
                {
                    if (pp.persistentId == pid)
                        return true;
                }
            }
            return false;
        }

        public static Transform FindDeep(Transform root, string name)
        {
            if (root == null || string.IsNullOrEmpty(name))
                return null;
            if (root.name == name)
                return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindDeep(root.GetChild(i), name);
                if (found != null)
                    return found;
            }
            return null;
        }

        public static string FormatVector(Vector3 v)
        {
            return v.x.ToString("R", CultureInfo.InvariantCulture) + "," +
                   v.y.ToString("R", CultureInfo.InvariantCulture) + "," +
                   v.z.ToString("R", CultureInfo.InvariantCulture);
        }
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;

namespace KSPTethers
{
    /// <summary>
    /// A fixed point on a part that a tether can be clipped to, and that can be rigged to another such point
    /// in the editor so the craft launches with a cable already strung between them, as slack as you like.
    ///
    /// ModuleManager adds this to KAS winches, ports and pylons, to docking ports and claws, to ladders and to
    /// crewed parts. On a KAS part the cable leaves the same socket KAS runs its own cable from; elsewhere it
    /// leaves the named transform, attach node or offset the config gives.
    /// </summary>
    public class ModuleTetherPort : PartModule
    {
        private const string Group = "KSPTethersPort";
        private const string GroupTitle = "Tether Point";
        private const float UnfocusedRange = 4f;

        /// <summary>Named transform the cable leaves from. Blank means "work it out".</summary>
        [KSPField] public string socketTransform = "";
        /// <summary>Attach node the cable leaves from, if there is no transform.</summary>
        [KSPField] public string socketNode = "";
        /// <summary>Last resort: an offset from the part's origin, in part space.</summary>
        [KSPField] public Vector3 socketOffset = Vector3.zero;

        /// <summary>
        /// Shared between the two ports of a rigged cable. A token rather than the partner's part id, because
        /// KSP is free to hand out new part ids when a craft is launched, and an id would then point at nobody.
        /// </summary>
        [KSPField(isPersistant = true)] public string rigId = "";
        /// <summary>Set once the cable has actually been made, so releasing it in flight is permanent.</summary>
        [KSPField(isPersistant = true)] public bool cableMade;

        [KSPField(isPersistant = true, guiName = "Cable length", guiActive = true, guiActiveEditor = true,
            guiActiveUnfocused = true, unfocusedRange = UnfocusedRange, guiUnits = " m", guiFormat = "F1",
            groupName = Group, groupDisplayName = GroupTitle)]
        [UI_FloatRange(minValue = 0.5f, maxValue = 40f, stepIncrement = 0.5f, scene = UI_Scene.All)]
        public float cableLength = 5f;

        [KSPField(guiName = "Rigging", guiActive = false, guiActiveEditor = true,
            groupName = Group, groupDisplayName = GroupTitle)]
        public string riggingStatus = "Not rigged";

        /// <summary>The port waiting for a partner while the player rigs a cable in the editor.</summary>
        private static ModuleTetherPort pending;

        private Transform cachedSocket;
        private bool socketSearched;
        private string kasLink;
        private TetherCore cable;
        private ModuleTetherPort partner;
        private float nextPawRefresh;
        private float createDeadline;
        private float appliedLength = -1f;

        // ---- where the cable leaves ----------------------------------------------------------------

        private Transform Socket
        {
            get
            {
                if (!socketSearched)
                {
                    socketSearched = true;
                    cachedSocket = TetherCompat.KasSocket(part, out kasLink);
                    if (cachedSocket == null && !string.IsNullOrEmpty(socketTransform))
                        cachedSocket = TetherEnd.FindDeep(part.transform, socketTransform);
                }
                return cachedSocket;
            }
        }

        public Vector3 SocketPosition
        {
            get
            {
                Transform t = Socket;
                if (t != null)
                    return t.position;
                AttachNode n = string.IsNullOrEmpty(socketNode) ? null : part.FindAttachNode(socketNode);
                if (n != null)
                    return part.transform.TransformPoint(n.position);
                return part.transform.TransformPoint(socketOffset);
            }
        }

        /// <summary>
        /// True when this part really does have a place the cable comes out of. Parts that merely carry a
        /// tether point - a pod, a ladder - do not, so a clip stays wherever it was put on the hull.
        /// </summary>
        public bool HasSocket
        {
            get
            {
                if (Socket != null)
                    return true;
                if (!string.IsNullOrEmpty(socketNode) && part.FindAttachNode(socketNode) != null)
                    return true;
                return socketOffset.sqrMagnitude > 1e-6f;
            }
        }

        /// <summary>Where a kerbal standing at <paramref name="from"/> should clip onto this part.</summary>
        public void ClipPoint(Vector3 from, out Vector3 point, out Vector3 normal)
        {
            if (HasSocket)
            {
                point = SocketPosition;
                normal = SocketNormal;
                return;
            }
            if (TetherGeometry.ClosestPointOnPart(part, from, out point, out normal))
                return;
            point = SocketPosition;
            normal = SocketNormal;
        }

        public Vector3 SocketNormal
        {
            get
            {
                Transform t = Socket;
                if (t != null && t.forward.sqrMagnitude > 1e-6f)
                    return t.forward;
                AttachNode n = string.IsNullOrEmpty(socketNode) ? null : part.FindAttachNode(socketNode);
                if (n != null && n.orientation.sqrMagnitude > 1e-6f)
                    return part.transform.TransformDirection(n.orientation);
                Vector3 d = SocketPosition - part.transform.position;
                return d.sqrMagnitude > 1e-6f ? d.normalized : part.transform.up;
            }
        }

        /// <summary>What to call this point in messages: the KAS link type when there is one.</summary>
        public string PointName
        {
            get
            {
                string title = part.partInfo != null ? part.partInfo.title : part.name;
                Transform unused = Socket;
                return string.IsNullOrEmpty(kasLink) ? title : title + " (" + kasLink + ")";
            }
        }

        // ---- lifecycle -----------------------------------------------------------------------------

        public override void OnStart(StartState state)
        {
            base.OnStart(state);
            TetherConfig cfg = TetherConfig.Instance;
            float maxLen = Mathf.Max(TetherGameSettings.Current.maxLength, cfg.minLength + 1f);
            SetRange(Fields[nameof(cableLength)].uiControlEditor as UI_FloatRange, cfg.minLength, maxLen);
            SetRange(Fields[nameof(cableLength)].uiControlFlight as UI_FloatRange, cfg.minLength, maxLen);
            cableLength = Mathf.Clamp(cableLength, cfg.minLength, maxLen);
            // Give the other end of a rigged cable time to load before giving up on it.
            createDeadline = Time.time + 30f;
            UpdatePaw();
        }

        private static void SetRange(UI_FloatRange r, float min, float max)
        {
            if (r == null)
                return;
            r.minValue = min;
            r.maxValue = max;
        }

        private void OnDestroy()
        {
            if (pending == this)
                pending = null;
        }

        private void Update()
        {
            // Refreshing the menu means looking the partner up, which is not something to do every frame on
            // every port of a large craft.
            if (Time.unscaledTime < nextPawRefresh)
                return;
            nextPawRefresh = Time.unscaledTime + 0.4f;
            UpdatePaw();
        }

        private void FixedUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight || !IsRigged || cableMade || Time.time > createDeadline)
                return;
            if (vessel == null || vessel.packed || part.rb == null)
                return;
            try
            {
                CreateRiggedCable();
            }
            catch (Exception e)
            {
                TetherLog.Exception("Rigging the cable a craft was built with", e);
                cableMade = true;   // don't keep throwing every tick
            }
        }

        /// <summary>Strings the cable this port was rigged to in the editor, the first time the craft flies.</summary>
        private void CreateRiggedCable()
        {
            if (TetherScenario.Instance == null)
                return;
            ModuleTetherPort port = FindPartner();
            if (port == null || port.part == null)
                return;
            // Only one of the pair makes the cable, so the two can never race to make it twice.
            if (part.persistentId > port.part.persistentId)
                return;
            if (TetherScenario.Instance.FindCable(part.persistentId, port.part.persistentId) != null)
            {
                cableMade = true;
                port.cableMade = true;
                return;
            }
            TetherEnd a = TetherEnd.AtPoint(part, SocketPosition, SocketNormal);
            TetherEnd b = TetherEnd.AtPoint(port.part, port.SocketPosition, port.SocketNormal);
            float length = Mathf.Max(TetherConfig.Instance.minLength, cableLength);
            cable = TetherScenario.Instance.AddCable(a, b, length, TetherUserSettings.Instance.cableSharingDefault);
            cableMade = true;
            port.cableMade = true;
            port.cable = cable;
            port.cableLength = cableLength;
            TetherLog.Info("Rigged a cable between " + part.name + " and " + port.part.name + " at " +
                           length.ToString("F1") + " m.");
        }

        /// <summary>The live cable this port is holding, if it still exists.</summary>
        private TetherCore LiveCable
        {
            get
            {
                if (cable != null && (cable.Released || cable.Finished))
                    cable = null;
                if (cable == null && HighLogic.LoadedSceneIsFlight && IsRigged && TetherScenario.Instance != null)
                {
                    ModuleTetherPort other = FindPartner();
                    if (other != null && other.part != null)
                        cable = TetherScenario.Instance.FindCable(part.persistentId, other.part.persistentId);
                }
                return cable;
            }
        }

        // ---- editor rigging ------------------------------------------------------------------------

        [KSPEvent(guiName = "Rig Cable From Here", guiActiveEditor = true, guiActive = false,
            groupName = Group, groupDisplayName = GroupTitle)]
        public void EventRigFrom()
        {
            pending = this;
            UpdatePaw();
            Post("Now open the other tether point and choose \"Rig Cable To Here\"");
        }

        [KSPEvent(guiName = "Rig Cable To Here", guiActiveEditor = true, guiActive = false, active = false,
            groupName = Group, groupDisplayName = GroupTitle)]
        public void EventRigTo()
        {
            ModuleTetherPort from = pending;
            pending = null;
            if (from == null || from == this || from.part == null)
            {
                UpdatePaw();
                return;
            }
            float span = Vector3.Distance(from.SocketPosition, SocketPosition);
            float length = Mathf.Clamp(span * 1.3f + 0.5f, TetherConfig.Instance.minLength,
                Mathf.Max(TetherGameSettings.Current.maxLength, TetherConfig.Instance.minLength + 1f));
            string token = Guid.NewGuid().ToString("N").Substring(0, 12);
            from.rigId = token;
            rigId = token;
            from.partner = this;
            partner = from;
            from.cableLength = length;
            cableLength = length;
            from.cableMade = false;
            cableMade = false;
            from.UpdatePaw();
            UpdatePaw();
            Post("Cable rigged: " + from.PointName + " to " + PointName + ", " + length.ToString("F1") + " m");
        }

        [KSPEvent(guiName = "Cancel Rigging", guiActiveEditor = true, guiActive = false, active = false,
            groupName = Group, groupDisplayName = GroupTitle)]
        public void EventCancelRig()
        {
            pending = null;
            UpdatePaw();
        }

        [KSPEvent(guiName = "Unrig Cable", guiActiveEditor = true, guiActive = false, active = false,
            groupName = Group, groupDisplayName = GroupTitle)]
        public void EventUnrig()
        {
            ModuleTetherPort other = FindPartner();
            if (other != null)
            {
                other.rigId = "";
                other.cableMade = false;
                other.partner = null;
                other.UpdatePaw();
            }
            rigId = "";
            cableMade = false;
            partner = null;
            UpdatePaw();
        }

        public bool IsRigged => !string.IsNullOrEmpty(rigId);

        /// <summary>The other port carrying the same rigging token, on this craft.</summary>
        private ModuleTetherPort FindPartner()
        {
            if (!IsRigged)
                return partner = null;
            if (partner != null && partner.part != null && partner != this && partner.rigId == rigId)
                return partner;
            partner = null;
            List<Part> parts = HighLogic.LoadedSceneIsEditor
                ? (EditorLogic.fetch != null && EditorLogic.fetch.ship != null ? EditorLogic.fetch.ship.Parts : null)
                : (vessel != null ? vessel.parts : null);
            if (parts == null)
                return null;
            foreach (Part p in parts)
            {
                if (p == null || p == part)
                    continue;
                var m = p.FindModuleImplementing<ModuleTetherPort>();
                if (m == null || m.rigId != rigId)
                    continue;
                // Copying a rigged part can leave more than two ports sharing a token; take the lowest id, so
                // both ends make the same choice.
                if (partner == null || p.persistentId < partner.part.persistentId)
                    partner = m;
            }
            return partner;
        }

        // ---- flight --------------------------------------------------------------------------------

        [KSPEvent(guiName = "Clip Tether Here", guiActive = false, guiActiveUnfocused = true,
            externalToEVAOnly = true, unfocusedRange = UnfocusedRange,
            groupName = Group, groupDisplayName = GroupTitle)]
        public void EventClipHere()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            ModuleKerbalTether kerbal = v != null && v.isEVA && v.rootPart != null
                ? v.rootPart.FindModuleImplementing<ModuleKerbalTether>()
                : null;
            if (kerbal == null)
            {
                Post("Only a kerbal on EVA can clip a tether here");
                return;
            }
            Vector3 point, normal;
            ClipPoint(kerbal.KerbalEndWorld, out point, out normal);
            kerbal.CommitTarget(part, point, normal);
        }

        [KSPEvent(guiName = "Release Cable", guiActive = true, guiActiveUnfocused = true,
            unfocusedRange = UnfocusedRange, active = false,
            groupName = Group, groupDisplayName = GroupTitle)]
        public void EventReleaseCable()
        {
            TetherCore c = LiveCable;
            if (c != null && TetherScenario.Instance != null)
                TetherScenario.Instance.ReleaseCable(c, true);
            cable = null;
            UpdatePaw();
        }

        // ---- PAW -----------------------------------------------------------------------------------

        private void UpdatePaw()
        {
            bool editor = HighLogic.LoadedSceneIsEditor;
            bool rigged = IsRigged;
            Events[nameof(EventRigFrom)].active = editor && !rigged && pending != this;
            Events[nameof(EventRigTo)].active = editor && !rigged && pending != null && pending != this;
            Events[nameof(EventCancelRig)].active = editor && pending == this;
            Events[nameof(EventUnrig)].active = editor && rigged;
            Fields[nameof(riggingStatus)].guiActiveEditor = editor;
            Fields[nameof(cableLength)].guiActiveEditor = rigged;

            if (!editor)
            {
                TetherCore live = LiveCable;
                Events[nameof(EventReleaseCable)].active = live != null;
                Fields[nameof(cableLength)].guiActive = live != null;
                Fields[nameof(cableLength)].guiActiveUnfocused = live != null;
                if (live != null)
                {
                    if (Mathf.Abs(cableLength - appliedLength) > 0.01f)
                        live.LengthLimit = cableLength;     // the player moved the slider
                    else
                        cableLength = live.LengthLimit;     // the reel moved it: follow along
                    appliedLength = cableLength;
                }
                return;
            }

            if (pending == this)
                riggingStatus = "Pick the other end";
            else if (rigged)
            {
                ModuleTetherPort other = FindPartner();
                riggingStatus = other != null ? "Cable to " + other.PointName : "Cable to a part that is gone";
                if (other == null)
                    rigId = "";
            }
            else
            {
                riggingStatus = "Not rigged";
            }
        }

        public override string GetInfo()
        {
            return "A tether can be clipped here, and a cable rigged to another tether point before launch.";
        }

        private static void Post(string message)
        {
            ScreenMessages.PostScreenMessage(message, 4f, ScreenMessageStyle.UPPER_CENTER);
        }
    }
}

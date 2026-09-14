using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace KSPTethers
{
    /// <summary>
    /// Added to every EVA kerbal by ModuleManager. Owns the kerbal's tether (end A = this kerbal's backpack)
    /// and everything the player does with it: clipping on, handing the free end over to another part
    /// (making a ship-to-ship cable) or to another kerbal, picking up a cable end, reeling, releasing, the
    /// lifeline that feeds the suit, persistence and the right-click menu.
    /// </summary>
    public class ModuleKerbalTether : PartModule
    {
        private const string Group = "KSPTethers";
        private const string GroupTitle = "EVA Tether";
        private const float UnfocusedRange = 60f;
        private const int PartLayerMask = 1 << 0;

        private static readonly Collider[] NearBuffer = new Collider[64];

        // ---- PAW ---------------------------------------------------------------------------------

        [KSPField(guiName = "Tether", guiActive = true, guiActiveUnfocused = true, unfocusedRange = UnfocusedRange,
            groupName = Group, groupDisplayName = GroupTitle)]
        public string tetherStatus = "Not clipped";

        [KSPField(guiName = "Distance", guiActive = false, guiActiveUnfocused = false, unfocusedRange = UnfocusedRange,
            groupName = Group, groupDisplayName = GroupTitle)]
        public string tetherDistance = "";

        [KSPField(guiName = "Lifeline", guiActive = false, guiActiveUnfocused = false, unfocusedRange = UnfocusedRange,
            groupName = Group, groupDisplayName = GroupTitle)]
        public string lifelineStatus = "";

        [KSPField(isPersistant = true, guiName = "Tether length", guiActive = false, guiActiveUnfocused = false,
            unfocusedRange = UnfocusedRange, guiUnits = " m", guiFormat = "F1", groupName = Group, groupDisplayName = GroupTitle)]
        [UI_FloatRange(minValue = 0.5f, maxValue = 40f, stepIncrement = 0.5f, scene = UI_Scene.Flight, affectSymCounterparts = UI_Scene.None)]
        public float tetherLength = 15f;

        // ---- saved state (v1.0-compatible keys) ----------------------------------------------------

        private bool loadedTethered;
        private uint loadedPid;
        private uint loadedFlightId;
        private Vector3 loadedPos;
        private Vector3 loadedNormal = Vector3.up;
        private float loadedRopeLength = 1f;

        // ---- runtime -----------------------------------------------------------------------------

        private bool started;
        private KerbalEVA eva;
        private TetherEnd ownEnd;
        private TetherCore core;
        private float anchorSearchDeadline;
        private float nextAnchorSearch;

        private Part pendingAutoAnchor;
        private float pendingAutoDeadline;
        private int pendingAutoFrames;

        private float keyDownTime = -1f;
        private bool holdHandled;
        private ScreenMessage holdMessage;
        private bool reelKeyIn;
        private bool reelKeyOut;

        private float resourceTimer;
        private double lastResourceUT;
        private readonly Dictionary<string, double> flow = new Dictionary<string, double>();
        private string lifelineText = "";

        private TetherAudio audioFx;
        private TetherTargeting targeting;
        private float nextPawUpdate;
        private string pawKey;
        private int tickErrors;

        // ---- API used by the app, targeting and other modules --------------------------------------

        public bool IsTethered => core != null;
        internal TetherCore Core => core;
        internal bool IsActiveEva => vessel != null && vessel.isEVA && vessel == FlightGlobals.ActiveVessel;
        internal Vector3 KerbalEndWorld => OwnEnd.WorldPos;

        private TetherEnd OwnEnd
        {
            get
            {
                if (ownEnd == null || !ownEnd.IsAlive)
                    ownEnd = TetherEnd.Kerbal(part);
                return ownEnd;
            }
        }
        internal string KerbalName => vessel != null ? vessel.GetDisplayName() : part.partInfo.title;
        internal string LifelineText => lifelineText;

        internal bool IsTetheredTo(Part p)
        {
            return core != null && core.B.IsAlive && core.B.Part == p;
        }

        // ---- lifecycle ---------------------------------------------------------------------------

        public override void OnStart(StartState state)
        {
            base.OnStart(state);
            if (!HighLogic.LoadedSceneIsFlight)
                return;

            TetherConfig cfg = TetherConfig.Instance;
            TetherGameSettings gs = TetherGameSettings.Current;
            eva = part.FindModuleImplementing<KerbalEVA>();

            var range = Fields[nameof(tetherLength)].uiControlFlight as UI_FloatRange;
            if (range != null)
            {
                range.minValue = cfg.minLength;
                range.maxValue = Mathf.Max(gs.maxLength, cfg.minLength + 1f);
            }
            tetherLength = Mathf.Clamp(tetherLength, cfg.minLength, Mathf.Max(gs.maxLength, cfg.minLength));

            audioFx = new TetherAudio(part.transform);
            targeting = new TetherTargeting(this);

            if (loadedTethered && core == null)
            {
                core = new TetherCore(TetherKind.Kerbal, OwnEnd,
                    TetherEnd.Unresolved(loadedPid, loadedFlightId, loadedPos, loadedNormal),
                    tetherLength, loadedRopeLength, this);
                loadedTethered = false; // from here on the live tether is saved
                anchorSearchDeadline = Time.time + 20f;
                nextAnchorSearch = 0f;
            }

            started = true;
            UpdatePaw(true);
        }

        private void OnDestroy()
        {
            if (targeting != null)
                targeting.Stop();
            ClearHoldMessage();
            // The kerbal is gone (boarded, destroyed, scene change): the tether goes with them.
            if (core != null)
            {
                core.Destroy();
                core = null;
            }
            if (audioFx != null)
                audioFx.Destroy();
            audioFx = null;
        }

        public override void OnSave(ConfigNode node)
        {
            base.OnSave(node);
            bool tethered = core != null ? !core.Released : loadedTethered;
            node.AddValue("tethered", tethered ? "True" : "False");
            if (!tethered)
                return;
            TetherEnd b = core != null ? core.B : null;
            node.AddValue("anchorPid", (b != null ? b.Pid : loadedPid).ToString(CultureInfo.InvariantCulture));
            node.AddValue("anchorFlightId", (b != null ? b.FlightId : loadedFlightId).ToString(CultureInfo.InvariantCulture));
            node.AddValue("anchorPos", TetherEnd.FormatVector(b != null ? b.LocalPos : loadedPos));
            node.AddValue("anchorNormal", TetherEnd.FormatVector(b != null ? b.LocalNormal : loadedNormal));
            float rope = core != null ? core.RopeLength : loadedRopeLength;
            node.AddValue("ropeLength", rope.ToString("R", CultureInfo.InvariantCulture));
        }

        public override void OnLoad(ConfigNode node)
        {
            base.OnLoad(node);
            loadedTethered = false;
            node.TryGetValue("tethered", ref loadedTethered);
            if (!loadedTethered)
                return;
            node.TryGetValue("anchorPid", ref loadedPid);
            node.TryGetValue("anchorFlightId", ref loadedFlightId);
            node.TryGetValue("anchorPos", ref loadedPos);
            node.TryGetValue("anchorNormal", ref loadedNormal);
            node.TryGetValue("ropeLength", ref loadedRopeLength);
        }

        public override string GetInfo()
        {
            return "Carries a reel-mounted EVA safety tether with a lifeline for power and life support.";
        }

        private void Update()
        {
            if (!started || vessel == null)
                return;
            try
            {
                HandleInput();
                if (targeting != null && targeting.Active)
                    targeting.Update();
                if (Time.unscaledTime >= nextPawUpdate)
                {
                    nextPawUpdate = Time.unscaledTime + 0.2f;
                    UpdatePaw(false);
                }
            }
            catch (Exception e)
            {
                if (tickErrors++ < 5)
                    TetherLog.Exception("Update", e);
            }
        }

        private void FixedUpdate()
        {
            if (!started || vessel == null)
                return;
            try
            {
                PhysicsTick(Time.fixedDeltaTime);
            }
            catch (Exception e)
            {
                if (tickErrors++ < 5)
                    TetherLog.Exception("FixedUpdate", e);
            }
        }

        // ---- PAW events --------------------------------------------------------------------------

        [KSPEvent(guiName = "Clip Tether (point & click)", guiActive = true, guiActiveUnfocused = false,
            groupName = Group, groupDisplayName = GroupTitle)]
        public void EventClipTarget()
        {
            StartTargeting();
        }

        [KSPEvent(guiName = "Clip Tether to Nearest Part", guiActive = true, guiActiveUnfocused = false,
            groupName = Group, groupDisplayName = GroupTitle)]
        public void EventClipNearest()
        {
            TryClipNearest(true, null);
        }

        [KSPEvent(guiName = "Clip Free End Elsewhere", guiActive = true, guiActiveUnfocused = false, active = false,
            groupName = Group, groupDisplayName = GroupTitle)]
        public void EventHandOff()
        {
            StartTargeting();
        }

        [KSPEvent(guiName = "Release Tether", guiActive = true, guiActiveUnfocused = true, externalToEVAOnly = false,
            unfocusedRange = UnfocusedRange, active = false, groupName = Group, groupDisplayName = GroupTitle)]
        public void EventRelease()
        {
            Release(true, KerbalName + " released the tether");
        }

        [KSPEvent(guiName = "Reel In", guiActive = true, guiActiveUnfocused = true, externalToEVAOnly = false,
            unfocusedRange = UnfocusedRange, active = false, groupName = Group, groupDisplayName = GroupTitle)]
        public void EventReelIn()
        {
            if (core != null)
                core.Reel = core.Reel == ReelMode.In ? ReelMode.None : ReelMode.In;
            UpdatePaw(true);
        }

        [KSPEvent(guiName = "Reel Out", guiActive = true, guiActiveUnfocused = true, externalToEVAOnly = false,
            unfocusedRange = UnfocusedRange, active = false, groupName = Group, groupDisplayName = GroupTitle)]
        public void EventReelOut()
        {
            if (core != null)
                core.Reel = core.Reel == ReelMode.Out ? ReelMode.None : ReelMode.Out;
            UpdatePaw(true);
        }

        private void UpdatePaw(bool force)
        {
            bool tethered = core != null;
            if (!tethered)
            {
                tetherStatus = "Not clipped";
                tetherDistance = "";
                lifelineStatus = "";
            }
            else if (!core.B.IsAlive)
            {
                tetherStatus = "Re-attaching...";
            }
            else
            {
                string reel = core.ReelingIn ? " (reeling in)" : core.ReelingOut ? " (reeling out)" : "";
                tetherStatus = "Clipped to " + core.B.Title + reel;
                tetherDistance = core.Distance.ToString("F1") + " m  (rope out " + core.RopeLength.ToString("F1") + " m)";
                lifelineStatus = lifelineText;
            }

            ReelMode mode = tethered ? core.Reel : ReelMode.None;
            string key = tethered + "|" + mode + "|" + (lifelineText.Length > 0);
            if (!force && key == pawKey)
                return;
            pawKey = key;

            Events[nameof(EventClipTarget)].active = !tethered;
            Events[nameof(EventClipNearest)].active = !tethered;
            Events[nameof(EventHandOff)].active = tethered;
            Events[nameof(EventRelease)].active = tethered;
            Events[nameof(EventReelIn)].active = tethered;
            Events[nameof(EventReelOut)].active = tethered;
            Events[nameof(EventReelIn)].guiName = mode == ReelMode.In ? "Stop Reeling In" : "Reel In";
            Events[nameof(EventReelOut)].guiName = mode == ReelMode.Out ? "Stop Reeling Out" : "Reel Out";

            BaseField lengthField = Fields[nameof(tetherLength)];
            lengthField.guiActive = tethered;
            lengthField.guiActiveUnfocused = tethered;
            BaseField distField = Fields[nameof(tetherDistance)];
            distField.guiActive = tethered;
            distField.guiActiveUnfocused = tethered;
            BaseField lifeField = Fields[nameof(lifelineStatus)];
            lifeField.guiActive = tethered && lifelineText.Length > 0;
            lifeField.guiActiveUnfocused = lifeField.guiActive;
        }

        // ---- input -------------------------------------------------------------------------------

        private void HandleInput()
        {
            if (!IsActiveEva)
            {
                reelKeyIn = reelKeyOut = false;
                if (targeting != null && targeting.Active)
                    targeting.Stop();
                CancelHold();
                return;
            }
            if (FlightDriver.Pause || MapView.MapIsEnabled || !InputLockManager.IsUnlocked(ControlTypes.EVA_INPUT) ||
                TetherWindow.IsCapturingKey)
            {
                reelKeyIn = reelKeyOut = false;
                CancelHold();
                return;
            }

            TetherUserSettings user = TetherUserSettings.Instance;
            TetherConfig cfg = TetherConfig.Instance;

            if (Input.GetKeyDown(user.toggleKey))
            {
                if (targeting.Active)
                    targeting.ConfirmOrNearest();
                else if (core == null)
                    StartTargeting();
                else
                {
                    // Tethered: a tap clips the free end elsewhere, a long press releases.
                    keyDownTime = Time.unscaledTime;
                    holdHandled = false;
                }
            }

            if (keyDownTime >= 0f)
            {
                float held = Time.unscaledTime - keyDownTime;
                if (!Input.GetKey(user.toggleKey))
                {
                    if (!holdHandled && core != null && held < cfg.releaseHoldTime)
                        StartTargeting();
                    CancelHold();
                }
                else if (!holdHandled)
                {
                    if (held >= cfg.releaseHoldTime)
                    {
                        holdHandled = true;
                        ClearHoldMessage();
                        Release(true, KerbalName + " released the tether");
                    }
                    else if (held > 0.25f && holdMessage == null)
                    {
                        holdMessage = ScreenMessages.PostScreenMessage(
                            "Keep holding [" + user.toggleKey + "] to release the tether", cfg.releaseHoldTime,
                            ScreenMessageStyle.UPPER_CENTER);
                    }
                }
            }

            bool inKey = core != null && Input.GetKey(user.reelInKey);
            bool outKey = core != null && Input.GetKey(user.reelOutKey);
            if ((inKey || outKey) && core != null && core.Reel != ReelMode.None)
                core.Reel = ReelMode.None; // manual keys override the toggles
            reelKeyIn = inKey;
            reelKeyOut = outKey && !inKey;
        }

        private void CancelHold()
        {
            keyDownTime = -1f;
            ClearHoldMessage();
        }

        private void ClearHoldMessage()
        {
            if (holdMessage != null)
            {
                ScreenMessages.RemoveMessage(holdMessage);
                holdMessage = null;
            }
        }

        private void StartTargeting()
        {
            if (targeting == null || !IsActiveEva)
                return;
            targeting.Start(core != null);
        }

        // ---- targeting (called by TetherTargeting) -------------------------------------------------

        internal struct TargetInfo
        {
            public bool Valid;
            public string Action;
            public string Reason;
            public TetherCore Cable;
            public bool CableEndIsA;
        }

        /// <summary>What clicking <paramref name="p"/> at <paramref name="point"/> would do.</summary>
        internal TargetInfo EvaluateTarget(Part p, Vector3 point)
        {
            var info = new TargetInfo();
            TetherGameSettings gs = TetherGameSettings.Current;
            if (p == null || p == part || p.vessel == null || p.vessel == vessel)
            {
                info.Reason = "Can't clip onto yourself";
                return info;
            }

            float d = Vector3.Distance(KerbalEndWorld, point);
            bool inReach = d <= gs.clipReach;
            string tooFar = "Out of reach (" + d.ToString("F1") + " m, reach is " + gs.clipReach.ToString("F1") + " m)";
            bool targetIsKerbal = p.vessel.isEVA;
            ModuleKerbalTether other = targetIsKerbal ? p.FindModuleImplementing<ModuleKerbalTether>() : null;

            if (core == null)
            {
                // Picking up the end of an existing cable?
                bool endIsA = false;
                TetherCore cable = targetIsKerbal ? null : TetherRegistry.FindEndNear(p, point, TetherConfig.Instance.cableEndPickupRadius, out endIsA);
                if (cable != null)
                {
                    info.Cable = cable;
                    info.CableEndIsA = endIsA;
                    info.Action = "Pick up the cable end";
                    info.Valid = inReach;
                    info.Reason = inReach ? null : tooFar;
                    return info;
                }
                if (targetIsKerbal)
                {
                    if (!gs.allowKerbalToKerbal)
                    {
                        info.Reason = "Kerbal-to-kerbal tethers are disabled in the difficulty settings";
                        return info;
                    }
                    if (other != null && other.IsTetheredTo(part))
                    {
                        info.Reason = p.vessel.GetDisplayName() + " is already tethered to " + KerbalName;
                        return info;
                    }
                    info.Action = "Clip tether to " + p.vessel.GetDisplayName();
                }
                else
                {
                    info.Action = "Clip tether here";
                }
            }
            else
            {
                // Handing the free end over.
                if (core.B.IsAlive && p == core.B.Part)
                {
                    info.Reason = "The tether is already clipped to this part";
                    return info;
                }
                if (targetIsKerbal)
                {
                    if (!gs.allowKerbalToKerbal)
                    {
                        info.Reason = "Kerbal-to-kerbal tethers are disabled in the difficulty settings";
                        return info;
                    }
                    if (other == null || other.IsTethered)
                    {
                        info.Reason = p.vessel.GetDisplayName() + " already has a tether";
                        return info;
                    }
                    info.Action = "Hand the tether to " + p.vessel.GetDisplayName();
                }
                else
                {
                    info.Action = "Clip your end here: cable to " + core.B.Title;
                }
            }
            info.Valid = inReach;
            if (!inReach)
                info.Reason = tooFar;
            return info;
        }

        /// <summary>Carries out what <see cref="EvaluateTarget"/> described.</summary>
        internal void CommitTarget(Part p, Vector3 point, Vector3 normal)
        {
            TargetInfo info = EvaluateTarget(p, point);
            if (!info.Valid)
            {
                if (!string.IsNullOrEmpty(info.Reason))
                    Post(info.Reason);
                return;
            }
            if (info.Cable != null)
                PickUpCableEnd(info.Cable, info.CableEndIsA);
            else if (core == null)
                AttachTo(p, point, normal, false);
            else
                HandOff(p, point, normal);
        }

        // ---- attaching, handing off, releasing -----------------------------------------------------

        /// <summary>Called by the flight addon when this kerbal has just left <paramref name="from"/> through its hatch.</summary>
        internal void RequestAutoTether(Part from)
        {
            if (from == null || core != null)
                return;
            pendingAutoAnchor = from;
            pendingAutoDeadline = Time.time + 10f;
            pendingAutoFrames = 0;
        }

        private void TryCompleteAutoTether()
        {
            Part from = pendingAutoAnchor;
            if (core != null || from == null || from.vessel == null || Time.time > pendingAutoDeadline)
            {
                pendingAutoAnchor = null;
                return;
            }
            if (vessel.packed || part.rb == null || ++pendingAutoFrames < 3)
                return;
            pendingAutoAnchor = null;

            Vector3 kp = KerbalEndWorld;
            Vector3 point, normal;
            // Clip onto the hull right beside the hatch the kerbal just came out of.
            if (!RaycastPartSurface(from, kp, from.transform.position, out point, out normal) &&
                !ClosestPointOnPart(from, kp, out point, out normal))
            {
                point = from.airlock != null ? from.airlock.position : from.transform.position;
                normal = kp - point;
            }
            AttachTo(from, point, normal, true);
        }

        internal bool TryClipNearest(bool announce, Part exclude)
        {
            if (!started)
                return false;
            TetherGameSettings gs = TetherGameSettings.Current;
            Vector3 from = KerbalEndWorld;
            int n = Physics.OverlapSphereNonAlloc(from, gs.clipReach, NearBuffer, PartLayerMask, QueryTriggerInteraction.Ignore);

            Part best = null;
            Vector3 bestPoint = Vector3.zero;
            float bestD = float.MaxValue;
            for (int k = 0; k < n; k++)
            {
                Collider c = NearBuffer[k];
                NearBuffer[k] = null;
                if (c == null || !c.enabled)
                    continue;
                Part p = FlightGlobals.GetPartUpwardsCached(c.gameObject);
                if (p == null || p == part || p == exclude || p.vessel == null || p.vessel == vessel || p.vessel.isEVA)
                    continue;
                Vector3 cp = SupportsClosestPoint(c) ? c.ClosestPoint(from) : c.ClosestPointOnBounds(from);
                float d = (cp - from).sqrMagnitude;
                if (d < bestD)
                {
                    bestD = d;
                    best = p;
                    bestPoint = cp;
                }
            }

            if (best == null)
            {
                if (announce)
                    Post("Nothing within " + gs.clipReach.ToString("F1") + " m to clip onto");
                return false;
            }

            Vector3 point, normal;
            if (!RaycastPartSurface(best, from, bestPoint, out point, out normal))
            {
                point = bestPoint;
                normal = from - bestPoint;
                if (normal.sqrMagnitude < 1e-6f)
                    normal = bestPoint - best.transform.position;
            }
            if (core == null)
                return AttachTo(best, point, normal, false);
            HandOff(best, point, normal);
            return true;
        }

        internal bool AttachTo(Part target, Vector3 worldPoint, Vector3 worldNormal, bool automatic)
        {
            if (!started || target == null || target == part || target.vessel == null || target.vessel == vessel)
                return false;
            if (core != null)
                Release(false, null);

            TetherConfig cfg = TetherConfig.Instance;
            TetherGameSettings gs = TetherGameSettings.Current;
            TetherEnd a = OwnEnd;
            TetherEnd b = TetherEnd.AtPoint(target, worldPoint, worldNormal);
            float maxLen = Mathf.Max(gs.maxLength, cfg.minLength);
            float dist = Vector3.Distance(a.WorldPos, b.WorldPos);
            tetherLength = Mathf.Clamp(Mathf.Max(gs.defaultLength, dist + 0.5f), cfg.minLength, maxLen);
            float rope = Mathf.Clamp(dist * (1f + cfg.slackFactor) + cfg.slackBase, cfg.minLength, tetherLength);
            core = new TetherCore(TetherKind.Kerbal, a, b, tetherLength, rope, this);
            ResetLifeline();

            if (audioFx != null)
                audioFx.PlayClip();
            Post(KerbalName + (automatic ? " clipped a safety tether to " : " clipped the tether to ") + b.Title);
            TetherLog.Info(KerbalName + " tethered to " + target.name + " (" + target.persistentId + ")");
            UpdatePaw(true);
            return true;
        }

        /// <summary>Clips this kerbal's free end onto a part (making a cable) or hands it to another kerbal.</summary>
        private void HandOff(Part target, Vector3 point, Vector3 normal)
        {
            if (core == null || target == null)
                return;
            TetherCore c = core;
            if (target.vessel != null && target.vessel.isEVA)
            {
                ModuleKerbalTether other = target.FindModuleImplementing<ModuleKerbalTether>();
                if (other == null || other.IsTethered)
                    return;
                DropCore();
                other.AdoptCore(c);
                if (audioFx != null)
                    audioFx.PlayClip();
                Post(KerbalName + " handed the tether to " + other.KerbalName);
                return;
            }
            if (TetherScenario.Instance == null)
            {
                Post("Cables between parts are only available in flight");
                return;
            }
            DropCore();
            TetherScenario.Instance.AdoptFromKerbal(c, TetherEnd.AtPoint(target, point, normal));
            if (audioFx != null)
                audioFx.PlayClip();
            Post(KerbalName + " clipped the cable between " + c.A.LongTitle + " and " + c.B.LongTitle);
        }

        /// <summary>Takes over a tether handed over by another kerbal (end B stays where it is).</summary>
        internal void AdoptCore(TetherCore c)
        {
            if (core != null)
                Release(false, null);
            c.ReplaceEndA(OwnEnd);
            c.SetKind(TetherKind.Kerbal);
            c.Owner = this;
            c.Reel = ReelMode.None;
            float dist = c.Attached ? c.Distance : c.RopeLength;
            c.LengthLimit = Mathf.Max(c.LengthLimit, dist + 0.2f);
            core = c;
            tetherLength = c.LengthLimit;
            ResetLifeline();
            UpdatePaw(true);
        }

        /// <summary>Unclips one end of a cable between parts and carries it on the backpack.</summary>
        private void PickUpCableEnd(TetherCore cable, bool endIsA)
        {
            if (core != null || TetherScenario.Instance == null)
                return;
            TetherScenario.Instance.Detach(cable);
            if (!endIsA)
                cable.SwapEnds(); // the end being picked up must become end A
            string remaining = cable.B.LongTitle;
            AdoptCore(cable);
            if (audioFx != null)
                audioFx.PlayClip();
            Post(KerbalName + " picked up the cable end; still clipped to " + remaining);
        }

        /// <summary>Forgets the tether without releasing it (it now belongs to someone else).</summary>
        private void DropCore()
        {
            core = null;
            loadedTethered = false;
            if (audioFx != null)
                audioFx.SetReeling(false);
            ResetLifeline();
            UpdatePaw(true);
        }

        /// <summary>Unclips the tether; the loose end whips back into the kerbal's reel.</summary>
        internal void Release(bool announce, string message)
        {
            if (core == null)
                return;
            if (announce && !string.IsNullOrEmpty(message))
                Post(message);
            TetherCore c = core;
            core = null;
            loadedTethered = false;
            c.ReleaseToward(c.A.IsAlive ? c.A : c.B);
            if (audioFx != null)
            {
                audioFx.SetReeling(false);
                audioFx.PlayRelease();
            }
            ResetLifeline();
            UpdatePaw(true);
        }

        // ---- physics and lifeline ----------------------------------------------------------------

        private void PhysicsTick(float dt)
        {
            if (pendingAutoAnchor != null)
                TryCompleteAutoTether();
            if (core == null)
            {
                if (audioFx != null)
                    audioFx.SetReeling(false);
                return;
            }

            // Restoring after a load: wait for the anchor's vessel to appear.
            if (!core.B.IsBound)
            {
                if (Time.time >= nextAnchorSearch)
                {
                    nextAnchorSearch = Time.time + 0.5f;
                    if (core.B.TryResolve())
                    {
                        TetherLog.Info("Restored tether of " + KerbalName + " to " + core.B.Part.name);
                        UpdatePaw(true);
                    }
                    else if (Time.time > anchorSearchDeadline)
                    {
                        Release(true, "The part " + KerbalName + " was tethered to is no longer here; tether released");
                        return;
                    }
                }
                if (!core.B.IsBound)
                    return;
            }

            if (eva != null && eva.IsSeated())
            {
                Release(true, KerbalName + " unclipped the tether to take a seat");
                return;
            }

            core.HoldIn = reelKeyIn;
            core.HoldOut = reelKeyOut;
            core.LengthLimit = tetherLength;
            string problem;
            bool ok = core.PhysicsTick(dt, out problem);
            if (!ok)
            {
                Release(true, problem == "snapped" ? KerbalName + "'s tether snapped!"
                    : problem == "pulled free" ? KerbalName + "'s tether pulled free"
                    : "Tether anchor lost: " + KerbalName + "'s tether came free!");
                return;
            }
            tetherLength = core.LengthLimit;
            if (core.ReachedMinimum)
                Post(KerbalName + "'s tether is fully reeled in");
            if (audioFx != null)
                audioFx.SetReeling(core.ReelingIn || core.ReelingOut);

            TickLifeline(dt);
        }

        private void TickLifeline(float dt)
        {
            TetherUserSettings user = TetherUserSettings.Instance;
            if (!core.Attached || !user.resourceTransfer)
            {
                ResetLifeline();
                return;
            }
            resourceTimer += dt;
            if (resourceTimer < TetherConfig.Instance.resourceTickInterval)
                return;
            double ut = Planetarium.GetUniversalTime();
            double rdt = lastResourceUT > 0 ? ut - lastResourceUT : resourceTimer;
            lastResourceUT = ut;
            resourceTimer = 0f;
            if (rdt <= 0 || rdt > 21600)
                return;

            flow.Clear();
            TetherResources.TickKerbal(part, core.B, rdt, flow);
            if (core.B.IsKerbal)
            {
                lifelineText = user.buddySharing ? "Sharing suit supplies" : "";
                return;
            }
            lifelineText = DescribeFlow();
        }

        private string DescribeFlow()
        {
            if (flow.Count == 0)
                return "Connected";
            var parts = new List<string>();
            foreach (KeyValuePair<string, double> kv in flow)
            {
                PartResourceDefinition def = PartResourceLibrary.Instance.GetDefinition(kv.Key);
                string name = def != null && !string.IsNullOrEmpty(def.displayName) ? def.displayName : kv.Key;
                parts.Add((kv.Value > 0 ? "+" : "-") + name);
            }
            return string.Join(", ", parts.ToArray());
        }

        private void ResetLifeline()
        {
            resourceTimer = 0f;
            lastResourceUT = 0;
            lifelineText = "";
            flow.Clear();
        }

        // ---- helpers -----------------------------------------------------------------------------

        private static bool SupportsClosestPoint(Collider c)
        {
            if (c is BoxCollider || c is SphereCollider || c is CapsuleCollider)
                return true;
            return c is MeshCollider mc && mc.convex;
        }

        private static bool RaycastPartSurface(Part p, Vector3 from, Vector3 toward, out Vector3 point, out Vector3 normal)
        {
            point = Vector3.zero;
            normal = Vector3.up;
            Vector3 dir = toward - from;
            float len = dir.magnitude;
            if (len < 1e-4f)
                return false;
            var ray = new Ray(from, dir / len);
            Collider[] cols = p.GetPartColliders();
            if (cols == null)
                return false;
            float best = float.MaxValue;
            bool found = false;
            foreach (Collider c in cols)
            {
                if (c == null || !c.enabled || c.isTrigger)
                    continue;
                RaycastHit hit;
                if (c.Raycast(ray, out hit, len + 2f) && hit.distance < best)
                {
                    best = hit.distance;
                    point = hit.point;
                    normal = hit.normal;
                    found = true;
                }
            }
            return found;
        }

        private static bool ClosestPointOnPart(Part p, Vector3 from, out Vector3 point, out Vector3 normal)
        {
            point = Vector3.zero;
            normal = Vector3.up;
            Collider[] cols = p.GetPartColliders();
            if (cols == null)
                return false;
            float best = float.MaxValue;
            bool found = false;
            foreach (Collider c in cols)
            {
                if (c == null || !c.enabled || c.isTrigger)
                    continue;
                Vector3 cp = SupportsClosestPoint(c) ? c.ClosestPoint(from) : c.ClosestPointOnBounds(from);
                float d = (cp - from).sqrMagnitude;
                if (d < best)
                {
                    best = d;
                    point = cp;
                    found = true;
                }
            }
            if (!found)
                return false;
            normal = from - point;
            if (normal.sqrMagnitude < 1e-6f)
                normal = point - p.transform.position;
            return true;
        }

        private static void Post(string message)
        {
            ScreenMessages.PostScreenMessage(message, 3.5f, ScreenMessageStyle.UPPER_CENTER);
        }
    }
}

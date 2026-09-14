using System;
using UnityEngine;

namespace KSPTethers
{
    internal enum TetherKind
    {
        /// <summary>A kerbal's EVA tether: pays out automatically from the backpack reel.</summary>
        Kerbal,
        /// <summary>A cable between two parts (ships, stations, rovers): fixed length unless reeled.</summary>
        Vessel
    }

    internal enum ReelMode { None, In, Out }

    /// <summary>
    /// One tether between two ends: the physical link (a soft ConfigurableJoint that only pulls when taut),
    /// the reel, and the simulated, rendered rope. Owned by a <see cref="ModuleKerbalTether"/> (kerbal tethers)
    /// or by <see cref="TetherScenario"/> (cables between parts); rendered by <see cref="TetherRegistry"/>.
    /// End A carries the joint; the rope is simulated in the frame of end B.
    /// </summary>
    internal sealed class TetherCore
    {
        private const float RetractSpeed = 7f;

        public TetherKind Kind { get; private set; }
        public TetherEnd A { get; private set; }
        public TetherEnd B { get; private set; }

        /// <summary>The furthest the ends can separate before the tether pulls.</summary>
        public float LengthLimit;
        /// <summary>Rope actually paid out (drawn); never more than <see cref="LengthLimit"/>.</summary>
        public float RopeLength;
        public ReelMode Reel;
        /// <summary>Reel keys held this frame (set by the owner).</summary>
        public bool HoldIn, HoldOut;
        /// <summary>Cables only: even out resources between the two vessels.</summary>
        public bool ShareResources;
        /// <summary>Owner, for the toolbar app to route commands.</summary>
        public object Owner;

        public bool Released { get; private set; }
        public bool Finished { get; private set; }
        /// <summary>Set when a toggled reel-in reached the minimum length (owners announce it).</summary>
        public bool ReachedMinimum { get; private set; }

        private float appInUntil, appOutUntil;

        // physics
        private ConfigurableJoint joint;
        private Rigidbody jointBodyA, jointBodyB;
        private float jointLimit;
        private float appliedLimit = -1f;
        private int linkReadyFrames;
        private float overloadTime;
        private float nextSpringUpdate;

        // Wrapping: when the rope drapes round end B's vessel, the joint pulls from the last point where the
        // rope touches the hull (seen from end A) instead of straight through the ship.
        private static readonly RaycastHit[] ObstructionHits = new RaycastHit[16];
        private Part wrapCandidatePart;
        private Vector3 wrapCandidateLocal;
        private float wrapCandidateArc;
        private Part wrapPart;
        private Vector3 wrapLocal;
        private float wrapArc;
        private bool wrapActive;
        private float wrapFirstSeen = -1f;
        private float wrapLastSeen;
        private float nextObstructionCheck;
        private bool obstructed;
        private Vector3 appliedPivot;

        // visuals
        private RopeSimulation rope;
        private Part ropeFramePart;
        private Vector3 lastOrigin;
        private bool ropeStale = true;
        private float retractRemaining;
        private readonly RopeCollider ropeCollider = new RopeCollider();
        private bool collidersDirty = true;
        private TetherRenderer ropeRenderer;
        private TubeBuildParams tube;
        private Vector3 smoothedGravity;
        private bool gravityValid;
        private int lodLevel;
        private int styleVersion = -1;
        private CableStyle style;

        public TetherCore(TetherKind kind, TetherEnd a, TetherEnd b, float lengthLimit, float ropeLength, object owner)
        {
            Kind = kind;
            A = a;
            B = b;
            LengthLimit = lengthLimit;
            RopeLength = ropeLength;
            Owner = owner;
            TetherRegistry.Add(this);
        }

        public bool Attached => !Released && A.IsAlive && B.IsAlive;
        public bool ReelingIn => !Released && (Reel == ReelMode.In || HoldIn || Time.time < appInUntil);
        public bool ReelingOut => !Released && !ReelingIn && (Reel == ReelMode.Out || HoldOut || Time.time < appOutUntil);
        public float Distance => Attached ? Vector3.Distance(A.WorldPos, B.WorldPos) : 0f;
        public CableStyle Style => style ?? CableStyles.For(Kind);

        /// <summary>Reel while a toolbar-app button is held (call every frame it is held).</summary>
        public void HoldReelFromApp(bool inward)
        {
            if (inward)
                appInUntil = Time.time + 0.15f;
            else
                appOutUntil = Time.time + 0.15f;
        }

        public void SetKind(TetherKind kind)
        {
            Kind = kind;
            styleVersion = -1;
            nextSpringUpdate = 0f;
        }

        /// <summary>Moves end A somewhere else (handing the tether over); the rope keeps its shape.</summary>
        public void ReplaceEndA(TetherEnd end)
        {
            DestroyJoint();
            A = end;
            linkReadyFrames = 0;
            collidersDirty = true;
            ResetWrap();
        }

        private void ResetWrap()
        {
            wrapActive = false;
            wrapPart = wrapCandidatePart = null;
            wrapFirstSeen = -1f;
            obstructed = false;
        }

        /// <summary>Swaps the ends so the other one carries the joint (and the rope is reversed to match).</summary>
        public void SwapEnds()
        {
            DestroyJoint();
            TetherEnd t = A;
            A = B;
            B = t;
            if (rope != null)
                rope.Reverse();
            linkReadyFrames = 0;
            collidersDirty = true;
            ResetWrap();
        }

        // ---- physics -----------------------------------------------------------------------------

        /// <summary>
        /// Runs from the owner's FixedUpdate. Returns false (with a reason) when the tether can no longer hold
        /// and must be released: an end was destroyed, it snapped, or it was dragged hopelessly far.
        /// </summary>
        public bool PhysicsTick(float dt, out string problem)
        {
            problem = null;
            ReachedMinimum = false;
            if (Released)
            {
                DestroyJoint();
                return true;
            }
            if (A.WasLost || B.WasLost)
            {
                DestroyJoint();
                problem = "lost";
                return false;
            }
            if (!A.IsAlive || !B.IsAlive)
            {
                DestroyJoint(); // still waiting for a part to load
                return true;
            }
            UpdateReel(dt);
            return ManageJoint(dt, out problem);
        }

        private void UpdateReel(float dt)
        {
            TetherConfig cfg = TetherConfig.Instance;
            TetherGameSettings gs = TetherGameSettings.Current;
            float maxLen = Mathf.Max(gs.maxLength, cfg.minLength);
            float speed = gs.reelSpeed;

            if (ReelingIn)
            {
                // Take up the slack first, then start hauling.
                if (LengthLimit > RopeLength)
                    LengthLimit = Mathf.Max(RopeLength, cfg.minLength);
                LengthLimit = Mathf.Max(cfg.minLength, LengthLimit - speed * dt);
                if (Reel == ReelMode.In && LengthLimit <= cfg.minLength + 1e-3f)
                {
                    Reel = ReelMode.None;
                    ReachedMinimum = true;
                }
            }
            else if (ReelingOut)
            {
                LengthLimit = Mathf.Min(maxLen, LengthLimit + speed * dt);
                if (Reel == ReelMode.Out && LengthLimit >= maxLen - 1e-3f)
                    Reel = ReelMode.None;
            }
            LengthLimit = Mathf.Clamp(LengthLimit, cfg.minLength, maxLen);
            if (Kind == TetherKind.Vessel)
                RopeLength = LengthLimit; // a cable has no automatic pay-out: what is reeled out is what hangs
        }

        private bool ManageJoint(float dt, out string problem)
        {
            problem = null;
            TetherConfig cfg = TetherConfig.Instance;
            TetherGameSettings gs = TetherGameSettings.Current;
            Vector3 aw = A.WorldPos, bw = B.WorldPos;
            Vessel va = A.Vessel, vb = B.Vessel;
            Rigidbody ra = A.Body, rb = B.Body;
            float target = LengthLimit;

            // Pull from where the rope wraps the hull, if it does: the kerbal is then held by the rope's real
            // path round the ship and can't drag it through.
            UpdateWrap(aw, bw);
            if (wrapActive && wrapPart != null && wrapPart.vessel == vb)
            {
                Rigidbody pr = TetherEnd.BodyOf(wrapPart);
                if (pr != null && !pr.isKinematic)
                {
                    rb = pr;
                    bw = wrapPart.transform.TransformPoint(wrapLocal);
                    target = Mathf.Max(cfg.minLength, LengthLimit - wrapArc);
                }
            }
            float dist = Vector3.Distance(aw, bw);

            bool wanted = gs.physicalTethers && ra != null && rb != null && ra != rb &&
                          !ra.isKinematic && !rb.isKinematic && !va.packed && !vb.packed;
            linkReadyFrames = wanted ? linkReadyFrames + 1 : 0;
            wanted &= linkReadyFrames > 3;

            if (joint != null && (joint.connectedBody == null || jointBodyA != ra || jointBodyB != rb))
                DestroyJoint();

            if (!wanted)
            {
                DestroyJoint();
                // Cosmetic tethers (or ones that can't hold) come free if hopelessly overstretched.
                float limit = gs.physicalTethers ? LengthLimit * 3f + 20f : LengthLimit * 1.5f + 3f;
                if (!va.packed && !vb.packed && dist > limit)
                {
                    problem = "pulled free";
                    return false;
                }
                return true;
            }

            if (joint == null)
            {
                CreateJoint(ra, rb, aw, bw);
                // Never start with a yank: begin at the current distance and reel down to the set length.
                jointLimit = Mathf.Max(target, dist + 0.02f);
                ApplyLimit(true);
            }
            else
            {
                if ((bw - appliedPivot).sqrMagnitude > 1e-4f)
                {
                    // The wrap point slides as the rope moves round the hull.
                    joint.connectedAnchor = rb.transform.InverseTransformPoint(bw);
                    appliedPivot = bw;
                }
                if (jointLimit <= target)
                {
                    jointLimit = target;
                }
                else
                {
                    jointLimit = Mathf.Min(jointLimit, Mathf.Max(dist, target)); // drop unused slack instantly
                    jointLimit = Mathf.Max(target, jointLimit - gs.reelSpeed * dt); // then haul at reel speed
                }
                ApplyLimit(false);
            }

            if (Time.time >= nextSpringUpdate)
            {
                nextSpringUpdate = Time.time + 1f;
                UpdateSpring(va, vb);
            }

            float breakForce = Kind == TetherKind.Kerbal ? cfg.breakForce : cfg.cableBreakForce;
            if (breakForce > 0f)
            {
                if (joint.currentForce.magnitude > breakForce)
                {
                    overloadTime += dt;
                    if (overloadTime > 0.1f)
                    {
                        problem = "snapped";
                        return false;
                    }
                }
                else
                {
                    overloadTime = 0f;
                }
            }
            return true;
        }

        /// <summary>
        /// Decides whether the joint should pull from a wrap point: only when the straight line between the ends
        /// is blocked by end B's vessel and the rope is resting against that vessel. Short-lived contacts are
        /// ignored and a wrap point is kept briefly when contact flickers, so the joint doesn't chatter.
        /// </summary>
        private void UpdateWrap(Vector3 aw, Vector3 bw)
        {
            if (!TetherConfig.Instance.wrapAroundHulls || B.IsKerbal)
            {
                wrapActive = false;
                return;
            }
            float now = Time.time;
            if (now >= nextObstructionCheck)
            {
                nextObstructionCheck = now + 0.1f;
                obstructed = IsObstructed(aw, bw);
            }
            bool candidate = obstructed && wrapCandidatePart != null && wrapCandidatePart.vessel == B.Vessel;
            if (candidate)
            {
                if (wrapFirstSeen < 0f)
                    wrapFirstSeen = now;
                if (wrapActive || now - wrapFirstSeen > 0.15f)
                {
                    wrapActive = true;
                    wrapPart = wrapCandidatePart;
                    wrapLocal = wrapCandidateLocal;
                    wrapArc = wrapCandidateArc;
                    wrapLastSeen = now;
                }
            }
            else
            {
                wrapFirstSeen = -1f;
                if (wrapActive && now - wrapLastSeen > 0.3f)
                    wrapActive = false;
            }
        }

        /// <summary>True if end B's vessel sits on the straight line between the ends.</summary>
        private bool IsObstructed(Vector3 aw, Vector3 bw)
        {
            Vector3 d = bw - aw;
            float len = d.magnitude;
            if (len < 0.6f)
                return false;
            // Stop short of end B so the part it's clipped to doesn't count.
            int n = Physics.RaycastNonAlloc(aw, d / len, ObstructionHits, len - 0.3f, 1 << 0, QueryTriggerInteraction.Ignore);
            Vessel vb = B.Vessel;
            for (int i = 0; i < n; i++)
            {
                Collider c = ObstructionHits[i].collider;
                ObstructionHits[i] = default(RaycastHit);
                if (c == null)
                    continue;
                Part p = FlightGlobals.GetPartUpwardsCached(c.gameObject);
                if (p != null && p.vessel == vb)
                    return true;
            }
            return false;
        }

        /// <summary>After a rope step: the first node, counting from end A, that rests against end B's vessel.</summary>
        private void FindWrapCandidate(Vector3 origin)
        {
            wrapCandidatePart = null;
            Vessel vb = B.Vessel;
            if (vb == null)
                return;
            int last = rope.Count - 1;
            for (int i = 1; i < last; i++)
            {
                float arc = rope.ArcToEndB(i);
                if (arc < 0.5f)
                    break; // contacts right beside end B change nothing
                int body = rope.TouchingBody(i);
                if (body < 0)
                    continue;
                Part p = ropeCollider.PartOf(body);
                if (p == null || p.vessel != vb)
                    continue;
                wrapCandidatePart = p;
                wrapCandidateLocal = p.transform.InverseTransformPoint(rope.Pos[i] + origin);
                wrapCandidateArc = arc;
                return;
            }
        }

        /// <summary>
        /// Spring tuned to the masses on each end: a natural frequency and damping ratio rather than a raw
        /// stiffness, so a kerbal on a 15 m line and a tug towing a station both feel right.
        /// </summary>
        private void UpdateSpring(Vessel va, Vessel vb)
        {
            if (joint == null)
                return;
            TetherConfig cfg = TetherConfig.Instance;
            float ma = Mathf.Max(0.01f, va.GetTotalMass());
            float mb = Mathf.Max(0.01f, vb.GetTotalMass());
            float mu = va == vb ? ma * 0.5f : ma * mb / (ma + mb);
            float f = Kind == TetherKind.Kerbal ? cfg.springFrequency : cfg.cableSpringFrequency;
            float zeta = Kind == TetherKind.Kerbal ? cfg.springDampingRatio : cfg.cableDampingRatio;
            float w = 2f * Mathf.PI * f;
            float k = Mathf.Clamp(w * w * mu, 0.1f, cfg.maxSpring);
            float c = 2f * zeta * Mathf.Sqrt(k * mu);
            joint.linearLimitSpring = new SoftJointLimitSpring { spring = k, damper = c };
        }

        private void ApplyLimit(bool force)
        {
            if (joint == null)
                return;
            if (!force && Mathf.Abs(appliedLimit - jointLimit) < 0.001f)
                return;
            joint.linearLimit = new SoftJointLimit { limit = jointLimit, bounciness = 0f, contactDistance = 0f };
            appliedLimit = jointLimit;
        }

        private void CreateJoint(Rigidbody ra, Rigidbody rb, Vector3 aw, Vector3 bw)
        {
            ConfigurableJoint j = ra.gameObject.AddComponent<ConfigurableJoint>();
            j.autoConfigureConnectedAnchor = false;
            j.connectedBody = rb;
            j.anchor = ra.transform.InverseTransformPoint(aw);
            j.connectedAnchor = rb.transform.InverseTransformPoint(bw);
            j.xMotion = ConfigurableJointMotion.Limited;
            j.yMotion = ConfigurableJointMotion.Limited;
            j.zMotion = ConfigurableJointMotion.Limited;
            j.angularXMotion = ConfigurableJointMotion.Free;
            j.angularYMotion = ConfigurableJointMotion.Free;
            j.angularZMotion = ConfigurableJointMotion.Free;
            j.enableCollision = true; // bodies on each end must still bump into each other
            j.enablePreprocessing = false;
            j.projectionMode = JointProjectionMode.None;
            j.breakForce = float.PositiveInfinity; // breaking is handled in ManageJoint
            j.breakTorque = float.PositiveInfinity;
            joint = j;
            jointBodyA = ra;
            jointBodyB = rb;
            appliedPivot = bw;
            appliedLimit = -1f;
            nextSpringUpdate = 0f;
            UpdateSpring(A.Vessel, B.Vessel);
        }

        public void DestroyJoint()
        {
            if (joint != null)
            {
                // Neutralise first: Destroy is deferred and a joint whose body vanished would pin its end to space.
                joint.xMotion = ConfigurableJointMotion.Free;
                joint.yMotion = ConfigurableJointMotion.Free;
                joint.zMotion = ConfigurableJointMotion.Free;
                UnityEngine.Object.Destroy(joint);
            }
            joint = null;
            jointBodyA = jointBodyB = null;
            appliedLimit = -1f;
        }

        public void OnVesselGoOnRails(Vessel v)
        {
            if (v == null)
                return;
            if ((A.IsAlive && v == A.Part.vessel) || (B.IsAlive && v == B.Part.vessel))
            {
                DestroyJoint();
                linkReadyFrames = 0;
            }
        }

        // ---- release -----------------------------------------------------------------------------

        /// <summary>Unclips end B; the loose rope whips back into end A before disappearing.</summary>
        public void Release()
        {
            if (Released)
                return;
            Released = true;
            DestroyJoint();
            Reel = ReelMode.None;
            if (rope != null && !ropeStale && A.IsAlive)
            {
                // Re-home the rope on A so it can retract even if B is gone.
                Vector3 newOrigin = A.Part.transform.position;
                rope.Shift(lastOrigin - newOrigin);
                lastOrigin = newOrigin;
                ropeFramePart = A.Part;
                retractRemaining = RopeLength;
            }
            else
            {
                HideRope();
                Finished = true;
            }
        }

        /// <summary>Releases, retracting toward whichever end survived.</summary>
        public void ReleaseToward(TetherEnd survivor)
        {
            if (survivor == B && !Released)
                SwapEnds();
            Release();
        }

        public void Destroy()
        {
            DestroyJoint();
            if (ropeRenderer != null)
                ropeRenderer.Destroy();
            ropeRenderer = null;
            Finished = true;
            TetherRegistry.Remove(this);
        }

        // ---- rope visual -------------------------------------------------------------------------

        /// <summary>Simulates and draws the rope; runs once per frame just before rendering.</summary>
        public void RenderTick(float dt)
        {
            if (Finished)
                return;
            TetherConfig cfg = TetherConfig.Instance;
            bool attached = Attached;
            bool retracting = Released && retractRemaining > 0f && rope != null && A.IsAlive;
            if (Released && !retracting)
            {
                HideRope();
                Finished = true;
                return;
            }
            if (!attached && !retracting)
            {
                HideRope(); // waiting for an end to load
                ropeStale = true;
                return;
            }

            Part framePart = attached ? B.Part : A.Part;
            Vector3 origin = framePart.transform.position;
            Vector3 a = A.WorldPos;
            Vector3 dirA = A.WorldDir;
            Vector3 b = attached ? B.WorldPos : a;
            Vector3 dirB = attached ? B.WorldDir : -dirA;

            Camera cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            float camDist = cam != null ? Vector3.Distance(cam.transform.position, (a + b) * 0.5f) : 0f;
            if (camDist > cfg.cullDistance)
            {
                HideRope();
                ropeStale = true;
                if (!attached)
                {
                    retractRemaining = 0f;
                    Finished = true;
                }
                return;
            }
            UpdateLod(camDist);

            if (styleVersion != CableStyles.Version)
            {
                style = CableStyles.For(Kind);
                styleVersion = CableStyles.Version;
                if (ropeRenderer != null)
                    ropeRenderer.SetStyle(style);
            }
            float radius = style.Radius * TetherUserSettings.Instance.thickness;

            if (attached)
            {
                // When the rope wraps the hull, what it has to span is the path round it, not the straight line.
                float span = Vector3.Distance(a, b);
                if (wrapActive && wrapPart != null)
                    span = Mathf.Max(span, wrapArc + Vector3.Distance(a, wrapPart.transform.TransformPoint(wrapLocal)));
                UpdateRopeLength(dt, span);
            }
            else
            {
                retractRemaining -= RetractSpeed * dt;
                if (retractRemaining <= 0.05f)
                {
                    retractRemaining = 0f;
                    HideRope();
                    Finished = true;
                    return;
                }
                RopeLength = retractRemaining;
            }

            if (rope == null)
                rope = new RopeSimulation(cfg.maxNodes, cfg.segmentLength, UnityEngine.Random.value * 100f);
            else if (!ropeStale && framePart != ropeFramePart)
                rope.Shift(lastOrigin - origin);
            ropeFramePart = framePart;

            Vessel frameVessel = framePart.vessel;
            Vector3 gravity = EffectiveGravity(dt, frameVessel, b);
            Vector3 la = a - origin;
            Vector3 lb = b - origin;
            if (ropeStale)
            {
                rope.Initialize(la, lb, RopeLength, gravity.sqrMagnitude > 0.25f ? gravity : A.RefUp);
                ropeStale = false;
            }
            else
            {
                rope.SetLength(RopeLength);
            }

            var sp = new RopeStepParams
            {
                A = la,
                B = lb,
                DirA = dirA,
                DirB = dirB,
                PinA = true,
                PinB = attached,
                Gravity = gravity,
                Damping = cfg.damping,
                Bend = style.BendStiffness,
                MinBendRadius = style.MinBendRadius,
                EndStiffness = cfg.endStiffness,
                IdleFlow = cfg.idleFlow,
                Friction = cfg.friction,
                SelfThickness = cfg.selfCollision ? radius * 2f : 0f,
                Iterations = cfg.solverIterations,
                SubstepRate = cfg.substepRate
            };
            if (frameVessel.atmDensity > 1e-5)
            {
                sp.AirDrag = Mathf.Min(30f, (float)frameVessel.atmDensity * cfg.airDrag);
                sp.AirVelocity = -(Vector3)frameVessel.srf_velocity;
            }

            IRopeCollisionSolver solver = null;
            if (cfg.collisions)
            {
                if (collidersDirty && A.IsAlive)
                {
                    ropeCollider.SetParts(A.Part, B.IsAlive ? B.Part : null);
                    collidersDirty = false;
                }
                // Query only around where the rope is (plus how far it can move in a frame).
                Vector3 lo, hi;
                rope.LocalBounds(out lo, out hi);
                lo = Vector3.Min(lo, Vector3.Min(la, lb));
                hi = Vector3.Max(hi, Vector3.Max(la, lb));
                var margin = new Vector3(0.6f, 0.6f, 0.6f);
                var box = new Bounds();
                box.SetMinMax(lo + origin - margin, hi + origin + margin);
                ropeCollider.Prepare(origin, box, (Vector3)frameVessel.upAxis, radius, rope.Rest,
                    A.IsKerbal ? 0.45f : 0.3f, attached && B.IsKerbal ? 0.45f : 0.3f);
                solver = ropeCollider;
            }
            rope.Step(dt, ref sp, solver);
            if (solver != null && attached && !B.IsKerbal)
                FindWrapCandidate(origin);
            else
                wrapCandidatePart = null;
            ropeCollider.Clear();
            lastOrigin = origin;

            if (MapView.MapIsEnabled)
            {
                HideRope();
                return;
            }

            int last = rope.Count - 1;
            if (!attached)
            {
                Vector3 tail = rope.Pos[last - 1] - rope.Pos[last];
                dirB = tail.sqrMagnitude > 1e-8f ? tail.normalized : -dirA;
            }

            if (ropeRenderer == null)
            {
                ropeRenderer = new TetherRenderer("KSPTethers-Rope");
                ropeRenderer.SetStyle(style);
            }
            tube.Radius = radius;
            tube.Sides = lodLevel == 0 ? cfg.radialSides : lodLevel == 1 ? Math.Max(6, cfg.radialSides - 2) : 6;
            tube.Subdivisions = lodLevel == 0 ? cfg.smoothingSubdivisions : lodLevel == 1 ? Math.Max(1, cfg.smoothingSubdivisions - 1) : 1;
            tube.VPerMeter = style.TilesPerMeter;
            tube.DirA = dirA;
            tube.DirB = dirB;
            tube.RefUpA = A.RefUp;
            tube.FittingA = true;
            tube.FittingB = attached;
            tube.BasePlateA = !A.IsKerbal;
            tube.BasePlateB = attached && !B.IsKerbal;
            ropeRenderer.Update(origin, rope.Pos, rope.Count, ref tube);
        }

        /// <summary>Fewer rings and sides for distant tethers, with hysteresis so the mesh doesn't flicker.</summary>
        private void UpdateLod(float camDist)
        {
            TetherConfig cfg = TetherConfig.Instance;
            float near = cfg.lodNearDistance, far = cfg.lodFarDistance;
            int target = camDist < near ? 0 : camDist < far ? 1 : 2;
            if (target > lodLevel)
            {
                if (camDist > (lodLevel == 0 ? near : far) * 1.1f)
                    lodLevel = target;
            }
            else if (target < lodLevel)
            {
                if (camDist < (lodLevel == 2 ? far : near) * 0.9f)
                    lodLevel = target;
            }
        }

        private void UpdateRopeLength(float dt, float dist)
        {
            TetherConfig cfg = TetherConfig.Instance;
            TetherGameSettings gs = TetherGameSettings.Current;
            if (Kind == TetherKind.Vessel)
            {
                RopeLength = LengthLimit;
                return;
            }
            float target = dist * (1f + cfg.slackFactor) + cfg.slackBase;
            if (ReelingIn)
            {
                // LengthLimit shrinks in UpdateReel; the clamp below retracts the rope with it.
            }
            else if (ReelingOut)
            {
                RopeLength += gs.reelSpeed * dt;
            }
            else if (RopeLength < target)
            {
                RopeLength = Mathf.Min(target, RopeLength + cfg.payoutSpeed * dt); // automatic pay-out
            }
            else if (gs.retractSlack)
            {
                RopeLength = Mathf.Max(target, RopeLength - gs.reelSpeed * dt);
            }
            RopeLength = Mathf.Clamp(RopeLength, cfg.minLength * 0.5f, Mathf.Max(cfg.minLength, LengthLimit));
        }

        /// <summary>
        /// Acceleration felt by the rope in the (non-rotating) frame of the part it hangs from: full gravity when
        /// landed, nothing in free fall, and pushed back during burns.
        /// </summary>
        private Vector3 EffectiveGravity(float dt, Vessel frameVessel, Vector3 at)
        {
            Vector3 g;
            Vessel other = A.Vessel;
            if (frameVessel == null)
                g = Vector3.zero;
            else if (frameVessel.LandedOrSplashed || (other != null && other.LandedOrSplashed))
                g = (Vector3)FlightGlobals.getGeeForceAtPosition(at, frameVessel.mainBody);
            else if (frameVessel.packed)
                g = Vector3.zero;
            else
                g = -(Vector3)frameVessel.perturbation;

            float gm = g.magnitude;
            if (gm > 40f)
                g *= 40f / gm;
            if (!gravityValid)
            {
                smoothedGravity = g;
                gravityValid = true;
                return smoothedGravity;
            }
            if (dt > 0f)
                smoothedGravity = Vector3.Lerp(smoothedGravity, g, 1f - Mathf.Exp(-dt / 0.35f));
            return smoothedGravity;
        }

        private void HideRope()
        {
            if (ropeRenderer != null)
                ropeRenderer.Visible = false;
        }
    }
}

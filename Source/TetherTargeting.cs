using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace KSPTethers
{
    /// <summary>
    /// Point-and-click mode for the active EVA kerbal. Untethered it clips the tether on (or picks up the
    /// end of a cable); tethered it clips the free end somewhere else, making a cable between two parts or
    /// handing the tether to another kerbal. An aiming line runs from the backpack to the surface under the
    /// mouse, the part is highlighted green/red, and a label next to the cursor says what a click will do.
    /// </summary>
    internal sealed class TetherTargeting
    {
        private const int PickMask = (1 << 0) | (1 << 17);
        private static readonly Color ValidColor = new Color(0.35f, 1f, 0.45f, 0.9f);
        private static readonly Color InvalidColor = new Color(1f, 0.35f, 0.3f, 0.9f);
        private static readonly RaycastHit[] Hits = new RaycastHit[32];

        private readonly ModuleKerbalTether owner;
        private GameObject lineObject;
        private LineRenderer line;
        private TargetingHint hint;
        private Part highlighted;
        private ScreenMessage prompt;
        private float rmbDownTime = -1f;
        private Vector3 rmbDownPos;
        private bool handOff;

        private Part hoverPart;
        private Vector3 hoverPoint;
        private Vector3 hoverNormal;
        private ModuleKerbalTether.TargetInfo hoverInfo;

        public bool Active { get; private set; }

        public TetherTargeting(ModuleKerbalTether owner)
        {
            this.owner = owner;
        }

        public void Start(bool handingOff)
        {
            if (Active)
                Stop();
            Active = true;
            handOff = handingOff;
            float reach = TetherGameSettings.Current.clipReach;
            string key = TetherUserSettings.Instance.toggleKey.ToString();
            prompt = ScreenMessages.PostScreenMessage(handOff
                    ? "Click a part within " + reach.ToString("F1") + " m to clip your end there (a cable between the two), or a kerbal to hand it over  -  [" + key + "] nearest part  -  right-click to cancel"
                    : "Click a part within " + reach.ToString("F1") + " m to clip the tether  -  [" + key + "] nearest part  -  right-click to cancel",
                3600f, ScreenMessageStyle.UPPER_CENTER);

            lineObject = new GameObject("KSPTethers-AimLine");
            lineObject.layer = 0;
            line = lineObject.AddComponent<LineRenderer>();
            line.sharedMaterial = CableStyles.LineMaterial;
            line.useWorldSpace = true;
            line.positionCount = 2;
            line.startWidth = 0.018f;
            line.endWidth = 0.01f;
            line.numCapVertices = 2;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.enabled = false;
            hint = lineObject.AddComponent<TargetingHint>();
        }

        public void Stop()
        {
            if (!Active)
                return;
            Active = false;
            SetHighlight(null, false);
            if (prompt != null)
            {
                ScreenMessages.RemoveMessage(prompt);
                prompt = null;
            }
            if (lineObject != null)
                UnityEngine.Object.Destroy(lineObject);
            lineObject = null;
            line = null;
            hint = null;
            hoverPart = null;
        }

        /// <summary>Commits to whatever is under the mouse if valid, otherwise to the nearest part in reach.</summary>
        public void ConfirmOrNearest()
        {
            if (hoverPart != null && hoverInfo.Valid)
            {
                Part p = hoverPart;
                Vector3 pt = hoverPoint, n = hoverNormal;
                Stop();
                owner.CommitTarget(p, pt, n);
                return;
            }
            Stop();
            TetherCore core = owner.Core;
            owner.TryClipNearest(true, core != null && core.B.IsAlive ? core.B.Part : null);
        }

        public void Update()
        {
            if (!Active)
                return;
            if (!owner.IsActiveEva || handOff != owner.IsTethered)
            {
                Stop();
                return;
            }

            Pick();

            bool overUI = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
            if (line != null)
            {
                line.enabled = hoverPart != null;
                if (hoverPart != null)
                {
                    Color c = hoverInfo.Valid ? ValidColor : InvalidColor;
                    line.startColor = c;
                    line.endColor = c;
                    line.SetPosition(0, owner.KerbalEndWorld);
                    line.SetPosition(1, hoverPoint);
                }
            }
            if (hint != null)
            {
                hint.Text = hoverPart == null || overUI ? null : hoverInfo.Valid ? hoverInfo.Action : hoverInfo.Reason;
                hint.Valid = hoverInfo.Valid;
            }
            SetHighlight(hoverPart, hoverInfo.Valid);

            if (Input.GetMouseButtonDown(0) && !overUI && hoverPart != null)
            {
                if (hoverInfo.Valid)
                {
                    Part p = hoverPart;
                    Vector3 pt = hoverPoint, n = hoverNormal;
                    Stop();
                    owner.CommitTarget(p, pt, n);
                    return;
                }
                if (!string.IsNullOrEmpty(hoverInfo.Reason))
                    ScreenMessages.PostScreenMessage(hoverInfo.Reason, 2.5f, ScreenMessageStyle.UPPER_CENTER);
            }

            // A quick right-click (not a camera drag) cancels.
            if (Input.GetMouseButtonDown(1))
            {
                rmbDownTime = Time.unscaledTime;
                rmbDownPos = Input.mousePosition;
            }
            if (Input.GetMouseButtonUp(1) && rmbDownTime >= 0f)
            {
                bool click = Time.unscaledTime - rmbDownTime < 0.3f && (Input.mousePosition - rmbDownPos).magnitude < 8f;
                rmbDownTime = -1f;
                if (click)
                {
                    Stop();
                    ScreenMessages.PostScreenMessage("Cancelled", 1.5f, ScreenMessageStyle.UPPER_CENTER);
                }
            }
        }

        private void Pick()
        {
            hoverPart = null;
            hoverInfo = default(ModuleKerbalTether.TargetInfo);
            Camera cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            if (cam == null)
                return;

            Ray ray = cam.ScreenPointToRay(Input.mousePosition);
            int n = Physics.RaycastNonAlloc(ray, Hits, 500f, PickMask, QueryTriggerInteraction.Ignore);
            if (n <= 0)
                return;
            Array.Sort(Hits, 0, n, HitDistanceComparer.Instance);

            for (int i = 0; i < n; i++)
            {
                Collider col = Hits[i].collider;
                if (col == null)
                    continue;
                Part p = FlightGlobals.GetPartUpwardsCached(col.gameObject);
                if (p == null || p == owner.part)
                    continue;
                hoverPart = p;
                hoverPoint = Hits[i].point;
                hoverNormal = Hits[i].normal;
                hoverInfo = owner.EvaluateTarget(p, hoverPoint);
                break;
            }
        }

        private void SetHighlight(Part p, bool valid)
        {
            if (highlighted != null && highlighted != p)
            {
                highlighted.SetHighlightType(Part.HighlightType.OnMouseOver);
                highlighted.SetHighlightDefault();
                highlighted.SetHighlight(false, false);
                highlighted = null;
            }
            if (p == null)
                return;
            highlighted = p;
            p.SetHighlightType(Part.HighlightType.AlwaysOn);
            p.SetHighlightColor(valid ? ValidColor : InvalidColor);
            p.SetHighlight(true, false);
        }

        private sealed class HitDistanceComparer : System.Collections.Generic.IComparer<RaycastHit>
        {
            public static readonly HitDistanceComparer Instance = new HitDistanceComparer();

            public int Compare(RaycastHit x, RaycastHit y)
            {
                return x.distance.CompareTo(y.distance);
            }
        }
    }

    /// <summary>Label beside the mouse cursor saying what a click will do.</summary>
    internal sealed class TargetingHint : MonoBehaviour
    {
        public string Text;
        public bool Valid;
        private GUIStyle style;

        private void OnGUI()
        {
            if (string.IsNullOrEmpty(Text))
                return;
            if (style == null)
            {
                style = new GUIStyle(HighLogic.Skin.label) { fontSize = 13, fontStyle = FontStyle.Bold, wordWrap = false };
            }
            Vector2 m = Input.mousePosition;
            var content = new GUIContent(Text);
            Vector2 size = style.CalcSize(content);
            var r = new Rect(m.x + 18f, Screen.height - m.y + 10f, size.x + 4f, size.y + 2f);
            style.normal.textColor = Color.black;
            GUI.Label(new Rect(r.x + 1f, r.y + 1f, r.width, r.height), content, style);
            style.normal.textColor = Valid ? new Color(0.55f, 1f, 0.6f) : new Color(1f, 0.55f, 0.5f);
            GUI.Label(r, content, style);
        }
    }
}

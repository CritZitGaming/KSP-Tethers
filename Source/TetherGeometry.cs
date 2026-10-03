using UnityEngine;

namespace KSPTethers
{
    /// <summary>Finding a sensible spot on a part to clip a tether to.</summary>
    internal static class TetherGeometry
    {
        /// <summary>How close a click has to be to a proper socket before it snaps onto it.</summary>
        public const float SnapRadius = 0.6f;

        public static bool SupportsClosestPoint(Collider c)
        {
            if (c is BoxCollider || c is SphereCollider || c is CapsuleCollider)
                return true;
            return c is MeshCollider mc && mc.convex;
        }

        /// <summary>Where a ray from <paramref name="from"/> toward <paramref name="toward"/> meets the part.</summary>
        public static bool RaycastPartSurface(Part p, Vector3 from, Vector3 toward, out Vector3 point, out Vector3 normal)
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

        public static bool ClosestPointOnPart(Part p, Vector3 from, out Vector3 point, out Vector3 normal)
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

        /// <summary>
        /// Moves a clip onto a proper socket when the player clicked close to one: a KAS winch, port or pylon
        /// runs its cable from a particular transform, and a tether point can name one too. Parts that just
        /// carry a tether point without a socket of their own are left alone, so a clip on the hull stays
        /// where it was put.
        /// </summary>
        public static void SnapToSocket(Part p, ref Vector3 point, ref Vector3 normal)
        {
            if (p == null)
                return;
            var port = p.FindModuleImplementing<ModuleTetherPort>();
            if (port != null && port.HasSocket)
            {
                Vector3 at = port.SocketPosition;
                if ((at - point).sqrMagnitude <= SnapRadius * SnapRadius)
                {
                    point = at;
                    normal = port.SocketNormal;
                    return;
                }
            }
            string label;
            TetherCompat.TrySnapToKasSocket(p, ref point, ref normal, SnapRadius, out label);
        }
    }
}

using UnityEngine;
using UnityEngine.Rendering;

namespace KSPTethers
{
    /// <summary>Owns the GameObject and dynamic mesh that draw one tether.</summary>
    internal sealed class TetherRenderer
    {
        private readonly GameObject go;
        private readonly Mesh mesh;
        private readonly MeshRenderer meshRenderer;
        private readonly TubeMeshBuilder builder = new TubeMeshBuilder();
        private int uploadedTopology = -1;
        private bool visible = true;

        public TetherRenderer(string name)
        {
            go = new GameObject(name);
            go.layer = 0; // same layer as parts, rendered by the flight cameras
            var filter = go.AddComponent<MeshFilter>();
            meshRenderer = go.AddComponent<MeshRenderer>();
            mesh = new Mesh { name = name };
            mesh.MarkDynamic();
            filter.sharedMesh = mesh;
            meshRenderer.shadowCastingMode = TetherConfig.Instance.castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            meshRenderer.receiveShadows = true;
        }

        /// <summary>Switches the cable's materials (rope sub-mesh, then the metal fittings).</summary>
        public void SetStyle(CableStyle style)
        {
            if (meshRenderer != null && style != null)
                meshRenderer.sharedMaterials = new[] { style.RopeMaterial, style.FittingMaterial };
        }

        public bool Visible
        {
            get { return visible; }
            set
            {
                if (visible == value)
                    return;
                visible = value;
                if (meshRenderer != null)
                    meshRenderer.enabled = value;
            }
        }

        public void Update(Vector3 worldOrigin, Vector3[] localNodes, int count, ref TubeBuildParams p)
        {
            if (go == null)
                return;
            go.transform.SetPositionAndRotation(worldOrigin, Quaternion.identity);
            builder.Build(localNodes, count, ref p);

            int n = builder.VertexCount;
            bool topologyChanged = builder.TopologyVersion != uploadedTopology;
            if (topologyChanged)
            {
                // Vertex count changed: clear first so old triangles never index past the new vertex array.
                mesh.Clear();
            }
            mesh.SetVertices(builder.Vertices, 0, n);
            mesh.SetNormals(builder.Normals, 0, n);
            mesh.SetTangents(builder.Tangents, 0, n);
            mesh.SetUVs(0, builder.UVs, 0, n);
            if (topologyChanged)
            {
                mesh.subMeshCount = 2;
                mesh.SetTriangles(builder.RopeTriangles, 0, builder.RopeTriangleCount, 0, false, 0);
                mesh.SetTriangles(builder.FittingTriangles, 0, builder.FittingTriangleCount, 1, false, 0);
                uploadedTopology = builder.TopologyVersion;
            }
            mesh.RecalculateBounds();
            Visible = true;
        }

        public void Destroy()
        {
            if (go != null)
                Object.Destroy(go);
            if (mesh != null)
                Object.Destroy(mesh);
        }
    }
}

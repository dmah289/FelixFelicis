using UnityEngine;

namespace FelixFelicis.Prototype.ParticleRendering
{
    /// <summary>
    /// Factory for the unit quad mesh used by GPU-instanced particle rendering.
    /// Each particle instance shares this single 4-vertex mesh — the vertex shader
    /// scales and positions it per-instance via StructuredBuffer data.
    /// </summary>
    public static class QuadMeshHelper
    {
        /// <summary>
        /// Creates a 1×1 quad centered at the origin (vertices at ±0.5).
        /// Calls <see cref="Mesh.UploadMeshData"/> with <c>markNoLongerReadable = true</c>
        /// to free the CPU-side vertex/index copy — this mesh never changes at runtime.
        /// </summary>
        public static Mesh CreateQuadMesh()
        {
            var mesh = new Mesh { name = "ParticleQuad" };

            mesh.SetVertices(new Vector3[]
            {
                new (-0.5f, 0.5f, 0),
                new (0.5f, 0.5f, 0),
                new (-0.5f, -0.5f, 0),
                new (0.5f, -0.5f, 0),
            });
            mesh.SetTriangles(new[] { 0, 1, 2, 2, 1, 3 }, 0, true);

            // Free CPU copy — mesh is static, only GPU needs it
            mesh.UploadMeshData(true);

            return mesh;
        }
    }
}

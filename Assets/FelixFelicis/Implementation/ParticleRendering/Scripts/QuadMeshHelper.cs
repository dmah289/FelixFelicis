using UnityEngine;

namespace FelixFelicis.ParticleRendering
{
    public static class QuadMeshHelper
    {
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
            return mesh;
        }
    }
}

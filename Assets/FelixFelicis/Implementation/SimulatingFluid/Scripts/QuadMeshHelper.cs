using UnityEngine;

namespace FelixFelicis.SimulatingFluid
{
    public static class QuadMeshHelper
    {
        public static Mesh CreateQuadMesh()
        {
            Mesh mesh = new Mesh();

            Vector3[] vertices = new Vector3[4]
            {
                new (-0.5f, 0.5f, 0),
                new (0.5f, 0.5f, 0),
                new (-0.5f, -0.5f, 0),
                new (0.5f, -0.5f, 0),
            };

            int[] triangles = { 0, 1, 2, 2, 1, 3 };
            
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0, true);
            return mesh;
        }
    }
}
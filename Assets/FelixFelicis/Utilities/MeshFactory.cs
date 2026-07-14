using UnityEngine;

namespace FelixFelicis.FelixFelicis.Utilities
{
    public static class MeshFactory
    {
        public static Mesh GenerateQuadMesh()
        {
            Mesh mesh = new Mesh();
            
            mesh.SetVertices(new Vector3[]
            {
                new(-0.5f, 0.5f, 0),
                new(0.5f, 0.5f, 0),
                new(-0.5f, -0.5f, 0),
                new(0.5f, -0.5f, 0)
            });

            mesh.SetTriangles(new[] { 0, 1, 2, 2, 1, 3 }, 0, true);
            
            mesh.UploadMeshData(true);
            return mesh;
        }
    }
}
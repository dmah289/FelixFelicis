using System.Runtime.InteropServices;
using UnityEngine;

namespace FelixFelicis.SimulatingFluid
{
    // Ensure the struct is laid out sequentially in memory for data layout in GPU
    [StructLayout(LayoutKind.Sequential)]
    public struct ParticleRenderData
    {
        // public float _padding; // RESERVED: Padding to align the struct to 32 bytes
        public Vector2 center;
        public float radius;
        public Color color;
    }
}
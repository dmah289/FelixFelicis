using System.Runtime.InteropServices;
using UnityEngine;

namespace FelixFelicis.Implements.ParticleRendering.Scripts
{
    [StructLayout(LayoutKind.Sequential)]
    public struct CircleRenderData
    {
        public Vector2 center;
        public float radius;
        public uint packedColor;
    }
}
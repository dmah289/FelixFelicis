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

        public static uint PackColor(Color color)
        {
            Color32 c = color;
            return (uint)(c.r | (c.g << 8) | (c.b << 16) | (c.a << 24));
        }
    }
}
using System.Runtime.InteropServices;
using UnityEngine;

namespace FelixFelicis.Prototype.ParticleRendering
{
    /// <summary>
    /// GPU instance data — must match ParticleData struct in ParticleDraw.shader byte-by-byte.
    /// Layout: float2 center (8B) + float radius (4B) + uint packedColor (4B) = 16 bytes.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ParticleRenderData
    {
        public Vector2 center;
        public float radius;
        public uint packedColor;

        /// <summary>
        /// Pack Color into RGBA8 uint. Color32 implicit conversion clamps to [0,255].
        /// Byte order on little-endian: R | G&lt;&lt;8 | B&lt;&lt;16 | A&lt;&lt;24.
        /// </summary>
        public static uint PackColor(Color color)
        {
            Color32 c = color;
            return (uint)(c.r | (c.g << 8) | (c.b << 16) | (c.a << 24));
        }
    }
}

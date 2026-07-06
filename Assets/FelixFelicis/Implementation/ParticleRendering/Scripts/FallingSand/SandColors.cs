using UnityEngine;

namespace FelixFelicis.ParticleRendering.Simulation
{
    /// <summary>
    /// Natural sand color palette generator.
    /// 5 base tones from light tan to dark brown, each with HSV jitter
    /// for organic variation. Called once per particle at spawn — zero per-frame cost.
    /// </summary>
    public static class SandColors
    {
        // Base sand tones — warm earth palette
        private static readonly Color[] BaseTones =
        {
            HexToColor(0xE8D5A3), // light tan
            HexToColor(0xD4B896), // wheat
            HexToColor(0xC4A882), // sand
            HexToColor(0xB09070), // dark sand
            HexToColor(0x9A7B5B), // brown
        };

        /// <summary>
        /// Generates a packed RGBA8 color with natural sand variation.
        /// Picks a random base tone, then applies ±5° hue, ±10% saturation, ±8% value jitter.
        /// </summary>
        public static uint GeneratePacked()
        {
            var baseColor = BaseTones[Random.Range(0, BaseTones.Length)];

            Color.RGBToHSV(baseColor, out float h, out float s, out float v);

            h += Random.Range(-0.014f, 0.014f); // ±5° / 360°
            s += Random.Range(-0.10f, 0.10f);
            v += Random.Range(-0.08f, 0.08f);

            // Wrap hue, clamp sat/val
            if (h < 0f) h += 1f;
            if (h > 1f) h -= 1f;
            s = Mathf.Clamp01(s);
            v = Mathf.Clamp01(v);

            var color = Color.HSVToRGB(h, s, v);
            return ParticleRenderData.PackColor(color);
        }

        private static Color HexToColor(uint hex)
        {
            float r = ((hex >> 16) & 0xFF) / 255f;
            float g = ((hex >> 8) & 0xFF) / 255f;
            float b = (hex & 0xFF) / 255f;
            return new Color(r, g, b, 1f);
        }
    }
}

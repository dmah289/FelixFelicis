using Unity.Collections;
using UnityEngine;

namespace FelixFelicis.ParticleRendering
{
    /// <summary>
    /// Debug MonoBehaviour — spawns random particles each frame to test the rendering pipeline.
    /// Attach to any GameObject in the scene. Replace with real simulation when ready.
    /// </summary>
    public class ParticleDebugSim : MonoBehaviour
    {
        [Header("Spawn")]
        [SerializeField] private int particleCount = 200;
        [SerializeField] private float spawnRange = 10f;

        [Header("Appearance")]
        [SerializeField] private float radiusMin = 0.1f;
        [SerializeField] private float radiusMax = 0.5f;

        [Header("Animation")]
        [SerializeField] private float driftSpeed = 1f;

        // Pre-allocated arrays — no per-frame allocations
        private Vector2[] positions;
        private Vector2[] velocities;
        private float[] radii;
        private uint[] packedColors;

        private void Start()
        {
            positions = new Vector2[particleCount];
            velocities = new Vector2[particleCount];
            radii = new float[particleCount];
            packedColors = new uint[particleCount];

            for (int i = 0; i < particleCount; i++)
            {
                positions[i] = new Vector2(
                    Random.Range(-spawnRange, spawnRange),
                    Random.Range(-spawnRange, spawnRange)
                );

                velocities[i] = Random.insideUnitCircle.normalized;
                radii[i] = Random.Range(radiusMin, radiusMax);

                // Pack color once at startup — zero per-frame cost
                packedColors[i] = ParticleRenderData.PackColor(
                    Color.HSVToRGB(Random.value, 0.7f, 1f));
            }
        }

        private void Update()
        {
            for (int i = 0; i < particleCount; i++)
            {
                positions[i] += velocities[i] * (driftSpeed * Time.deltaTime);

                if (positions[i].x > spawnRange || positions[i].x < -spawnRange)
                    velocities[i].x = -velocities[i].x;

                if (positions[i].y > spawnRange || positions[i].y < -spawnRange)
                    velocities[i].y = -velocities[i].y;
            }

            // Write directly into GPU staging memory — zero intermediate copy
            var writer = ParticleProvider.Writer;
            if (writer == null) return;

            NativeArray<ParticleRenderData> buffer = writer.BeginFrame(particleCount);
            if (!buffer.IsCreated) return;

            for (int i = 0; i < particleCount; i++)
            {
                buffer[i] = new ParticleRenderData
                {
                    center = positions[i],
                    radius = radii[i],
                    packedColor = packedColors[i]
                };
            }

            writer.EndFrame(particleCount);
        }
    }
}

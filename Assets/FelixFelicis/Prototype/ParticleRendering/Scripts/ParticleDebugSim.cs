using Unity.Collections;
using UnityEngine;

namespace FelixFelicis.Prototype.ParticleRendering
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

        // Pre-allocated arrays — no per-frame allocations.
        // Size captured once in Start(); runtime Inspector changes require re-enter Play.
        private int count;
        private Vector2[] positions;
        private Vector2[] velocities;
        private float[] radii;
        private uint[] packedColors;

        private void Start()
        {
            count = particleCount;

            positions = new Vector2[count];
            velocities = new Vector2[count];
            radii = new float[count];
            packedColors = new uint[count];

            for (int i = 0; i < count; i++)
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
            float dt = driftSpeed * Time.deltaTime;

            for (int i = 0; i < count; i++)
            {
                positions[i] += velocities[i] * dt;

                if (positions[i].x > spawnRange || positions[i].x < -spawnRange)
                    velocities[i].x = -velocities[i].x;

                if (positions[i].y > spawnRange || positions[i].y < -spawnRange)
                    velocities[i].y = -velocities[i].y;
            }

            // Write directly into GPU staging memory — zero intermediate copy
            var writer = ParticleProvider.Writer;
            if (writer == null) return;

            NativeArray<ParticleRenderData> buffer = writer.BeginFrame(count);
            if (!buffer.IsCreated) return;

            for (int i = 0; i < count; i++)
            {
                buffer[i] = new ParticleRenderData
                {
                    center = positions[i],
                    radius = radii[i],
                    packedColor = packedColors[i]
                };
            }

            writer.EndFrame(count);
        }
    }
}

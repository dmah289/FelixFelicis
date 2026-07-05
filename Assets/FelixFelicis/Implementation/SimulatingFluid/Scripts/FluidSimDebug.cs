using UnityEngine;

namespace FelixFelicis.SimulatingFluid
{
    /// <summary>
    /// Debug MonoBehaviour — sinh random particles mỗi frame.
    /// Gắn vào bất kỳ GameObject nào trong scene để test rendering pipeline.
    /// Khi có simulation thật, thay thế class này.
    /// </summary>
    public class FluidSimDebug : MonoBehaviour
    {
        [Header("Spawn")]
        [Tooltip("Số particles sinh ra")]
        [SerializeField] private int particleCount = 200;

        [Tooltip("Particles phân bố trong vùng này (world units)")]
        [SerializeField] private float spawnRange = 10f;

        [Header("Appearance")]
        [SerializeField] private float radiusMin = 0.1f;
        [SerializeField] private float radiusMax = 0.5f;

        [Header("Animation")]
        [Tooltip("Particles di chuyển nhẹ mỗi frame")]
        [SerializeField] private float driftSpeed = 1f;

        // Lưu vị trí để animate, không sinh lại random mỗi frame
        private Vector2[] positions;
        private Vector2[] velocities;
        private float[] radii;
        private Color[] colors;

        private void Start()
        {
            positions = new Vector2[particleCount];
            velocities = new Vector2[particleCount];
            radii = new float[particleCount];
            colors = new Color[particleCount];

            for (int i = 0; i < particleCount; i++)
            {
                positions[i] = new Vector2(
                    Random.Range(-spawnRange, spawnRange),
                    Random.Range(-spawnRange, spawnRange)
                );

                // Hướng di chuyển ngẫu nhiên, normalize để tốc độ đều
                velocities[i] = Random.insideUnitCircle.normalized;

                radii[i] = Random.Range(radiusMin, radiusMax);

                // Màu ngẫu nhiên, giữ alpha = 1
                colors[i] = Color.HSVToRGB(Random.value, 0.7f, 1f);
            }
        }

        private void Update()
        {
            // Cập nhật vị trí — bounce khi ra ngoài vùng spawn
            for (int i = 0; i < particleCount; i++)
            {
                positions[i] += velocities[i] * (driftSpeed * Time.deltaTime);

                // Bounce X
                if (positions[i].x > spawnRange || positions[i].x < -spawnRange)
                    velocities[i].x = -velocities[i].x;

                // Bounce Y
                if (positions[i].y > spawnRange || positions[i].y < -spawnRange)
                    velocities[i].y = -velocities[i].y;
            }

            // Ghi vào static provider — RenderPass sẽ đọc trong cùng frame
            var list = FluidParticleProvider.Particles;
            list.Clear();

            for (int i = 0; i < particleCount; i++)
            {
                list.Add(new ParticleRenderData
                {
                    center = positions[i],
                    radius = radii[i],
                    color = colors[i]
                });
            }
        }
    }
}

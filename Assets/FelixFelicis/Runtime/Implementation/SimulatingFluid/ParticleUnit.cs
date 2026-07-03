using System;
using UnityEngine;

namespace FelixFelicis.Runtime.Implementation
{
    public class ParticleUnit : MonoBehaviour
    {
        [SerializeField] private float gravity;
        private Vector2 velocity;
        [SerializeField] private Vector2 boundSize;
        [SerializeField] private Vector2 scale;
        [SerializeField] private float damping = 0.8f;

        private void Start()
        {
            transform.localScale = 2 * scale;
        }

        private void OnDrawGizmos()
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireCube(Vector3.zero, boundSize);
        }

        private void Update()
        {
            velocity += Vector2.down * (gravity * Time.deltaTime);
            transform.position += (Vector3)velocity * Time.deltaTime;
            ResolveCollisions();
        }

        private void ResolveCollisions()
        {
            Vector2 halfBoundSize = boundSize * 0.5f - Vector2.one * scale;
            if (Mathf.Abs(transform.position.x) > halfBoundSize.x)
            {
                transform.position = new Vector3(Mathf.Sign(transform.position.x) * halfBoundSize.x, transform.position.y, transform.position.z);
                velocity.x *= -1 * damping;
            }

            if (Mathf.Abs(transform.position.y) > halfBoundSize.y)
            {
                transform.position = new Vector3(transform.position.x, Mathf.Sign(transform.position.y) * halfBoundSize.y, transform.position.z);
                velocity.y *= -1 * damping;
            }
        }
    }
}
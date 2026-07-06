using UnityEngine;

namespace FelixFelicis.ParticleRendering.Simulation
{
    /// <summary>
    /// Per-particle state for falling sand simulation.
    /// Stored in a pre-allocated array — no per-frame allocation.
    /// Verlet-style: velocity is derived from <c>pos - prevPos</c>.
    /// </summary>
    public struct SandParticle
    {
        public Vector2 pos;
        public Vector2 prevPos;
        public float radius;
        public uint packedColor;
        public byte sleepCounter;
        public bool isSleeping;

        // Snapshot of position at the start of each FixedUpdate frame.
        // Used by sleep detection to measure total frame displacement
        // instead of per-substep micro-jitter from collision corrections.
        public Vector2 frameStartPos;
    }
}

using System;
using UnityEngine;

namespace FelixFelicis.ParticleRendering.Simulation
{
    /// <summary>
    /// Zero-GC 2D spatial hash using counting-sort.
    /// Rebuilds every substep in O(n). All arrays pre-allocated at construction.
    /// Fields are internal for direct access from <see cref="SandPhysics"/> — avoids
    /// method call overhead in the hot collision loop (significant on Mono).
    /// </summary>
    public class SpatialHash2D : IDisposable
    {
        internal readonly float invCellSize;
        internal readonly int gridWidth;
        internal readonly int gridHeight;
        internal readonly int cellCount;
        internal readonly float originX;
        internal readonly float originY;

        internal readonly int[] cellCounts;
        internal readonly int[] cellOffsets;
        internal int[] sortedIndices;
        private int capacity;

        public SpatialHash2D(float cellSize, float minX, float maxX, float minY, float maxY, int initialCapacity)
        {
            invCellSize = 1f / cellSize;
            originX = minX;
            originY = minY;

            gridWidth = Mathf.CeilToInt((maxX - minX) * invCellSize) + 1;
            gridHeight = Mathf.CeilToInt((maxY - minY) * invCellSize) + 1;
            cellCount = gridWidth * gridHeight;

            cellCounts = new int[cellCount];
            cellOffsets = new int[cellCount];
            capacity = initialCapacity;
            sortedIndices = new int[capacity];
        }

        /// <summary>
        /// Rebuild the hash from particle positions. O(n) — 3 passes.
        /// </summary>
        public void Build(SandParticle[] particles, int count)
        {
            if (count > capacity)
            {
                capacity = count;
                sortedIndices = new int[capacity];
            }

            int gw = gridWidth;
            int gwM1 = gw - 1;
            int ghM1 = gridHeight - 1;
            float inv = invCellSize;
            float ox = originX;
            float oy = originY;

            // Pass 1: zero + count
            Array.Clear(cellCounts, 0, cellCount);

            for (int i = 0; i < count; i++)
            {
                int cx = (int)((particles[i].pos.x - ox) * inv);
                if (cx < 0) cx = 0; else if (cx > gwM1) cx = gwM1;
                int cy = (int)((particles[i].pos.y - oy) * inv);
                if (cy < 0) cy = 0; else if (cy > ghM1) cy = ghM1;
                cellCounts[cy * gw + cx]++;
            }

            // Pass 2: prefix-sum → offsets
            cellOffsets[0] = 0;
            for (int i = 1; i < cellCount; i++)
                cellOffsets[i] = cellOffsets[i - 1] + cellCounts[i - 1];

            // Pass 3: scatter (decrement cellCounts as write cursor)
            for (int i = 0; i < count; i++)
            {
                int cx = (int)((particles[i].pos.x - ox) * inv);
                if (cx < 0) cx = 0; else if (cx > gwM1) cx = gwM1;
                int cy = (int)((particles[i].pos.y - oy) * inv);
                if (cy < 0) cy = 0; else if (cy > ghM1) cy = ghM1;
                int cell = cy * gw + cx;
                sortedIndices[cellOffsets[cell] + cellCounts[cell] - 1] = i;
                cellCounts[cell]--;
            }

            // Restore cellCounts from offsets
            for (int i = 0; i < cellCount - 1; i++)
                cellCounts[i] = cellOffsets[i + 1] - cellOffsets[i];
            cellCounts[cellCount - 1] = count - cellOffsets[cellCount - 1];
        }

        public void Dispose() { }
    }
}

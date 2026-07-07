using System;
using Unity.Collections;

namespace FelixFelicis.ParticleRendering.Simulation
{
    /// <summary>
    /// Zero-GC 2D spatial hash using counting-sort.
    /// Rebuilds every substep in O(n). All NativeArrays pre-allocated at construction.
    /// Fields are internal for direct access from Burst jobs — avoids
    /// method call overhead in the hot collision loop.
    /// </summary>
    public class SpatialHash2D : IDisposable
    {
        internal readonly float invCellSize;
        internal readonly int gridWidth;
        internal readonly int gridHeight;
        internal readonly int cellCount;
        internal readonly float originX;
        internal readonly float originY;

        internal NativeArray<int> cellCounts;
        internal NativeArray<int> cellOffsets;
        internal NativeArray<int> sortedIndices;
        internal NativeArray<int> particleCells;

        private int capacity;
        private bool disposed;

        public SpatialHash2D(float cellSize, float minX, float maxX, float minY, float maxY, int initialCapacity)
        {
            invCellSize = 1f / cellSize;
            originX = minX;
            originY = minY;

            gridWidth = (int)Math.Ceiling((maxX - minX) * invCellSize) + 1;
            gridHeight = (int)Math.Ceiling((maxY - minY) * invCellSize) + 1;
            cellCount = gridWidth * gridHeight;

            cellCounts = new NativeArray<int>(cellCount, Allocator.Persistent);
            cellOffsets = new NativeArray<int>(cellCount, Allocator.Persistent);
            capacity = initialCapacity;
            sortedIndices = new NativeArray<int>(capacity, Allocator.Persistent);
            particleCells = new NativeArray<int>(capacity, Allocator.Persistent);
        }

        /// <summary>
        /// Ensures particle-indexed arrays can hold <paramref name="count"/> entries.
        /// Disposes old arrays and reallocates if needed. Called before Build.
        /// </summary>
        public void EnsureCapacity(int count)
        {
            if (count <= capacity) return;

            capacity = count;
            sortedIndices.Dispose();
            particleCells.Dispose();
            sortedIndices = new NativeArray<int>(capacity, Allocator.Persistent);
            particleCells = new NativeArray<int>(capacity, Allocator.Persistent);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            if (cellCounts.IsCreated) cellCounts.Dispose();
            if (cellOffsets.IsCreated) cellOffsets.Dispose();
            if (sortedIndices.IsCreated) sortedIndices.Dispose();
            if (particleCells.IsCreated) particleCells.Dispose();
        }
    }
}

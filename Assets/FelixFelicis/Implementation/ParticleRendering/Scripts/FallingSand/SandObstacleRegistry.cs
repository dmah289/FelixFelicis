using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FelixFelicis.ParticleRendering.Simulation
{
    /// <summary>
    /// Collects active <see cref="SandObstacle"/> instances and packs them into a
    /// <see cref="NativeArray{ObstacleData}"/> for consumption by Burst jobs.
    /// <para>
    /// Event-driven: the NativeArray is rebuilt only when obstacles are added/removed
    /// (<see cref="isDirty"/> flag). Zero per-frame work when the obstacle set is unchanged.
    /// </para>
    /// <para>
    /// Follows the same static-bridge pattern as <c>ParticleProvider</c>.
    /// <see cref="FallingSandSim"/> owns the lifetime via <see cref="SetInstance"/>/<see cref="ClearInstance"/>.
    /// </para>
    /// </summary>
    public class SandObstacleRegistry : IDisposable
    {
        private static SandObstacleRegistry instance;

        private readonly List<SandObstacle> obstacles = new();
        private NativeArray<ObstacleData> dataArray;
        private int activeCount;
        private int capacity;
        private bool isDirty = true;
        private bool disposed;

        // ── Static bridge ─────────────────────────────────────────────

        internal static void SetInstance(SandObstacleRegistry registry)
        {
            instance = registry;

            // Retroactive registration: SandObstacle.OnEnable() may have fired
            // before this registry existed (Unity does not guarantee execution order
            // between MonoBehaviours). Scan for all already-active obstacles and
            // register them now.
            foreach (var obstacle in Object.FindObjectsByType<SandObstacle>(FindObjectsSortMode.None))
            {
                if (obstacle.isActiveAndEnabled)
                    Register(obstacle);
            }
        }

        internal static void ClearInstance(SandObstacleRegistry registry)
        {
            if (instance == registry) instance = null;
        }

        /// <summary>
        /// Called by <see cref="SandObstacle.OnEnable"/> and by
        /// <see cref="SetInstance"/> (retroactive scan for already-active obstacles).
        /// No-op when the registry does not exist yet — <see cref="SetInstance"/>
        /// catches up missed obstacles via <c>FindObjectsByType</c>.
        /// </summary>
        public static void Register(SandObstacle obstacle)
        {
            if (instance == null) return;
            if (instance.obstacles.Contains(obstacle)) return;
            instance.obstacles.Add(obstacle);
            instance.isDirty = true;
        }

        /// <summary>Called by <see cref="SandObstacle.OnDisable"/>.</summary>
        public static void Unregister(SandObstacle obstacle)
        {
            if (instance == null) return;
            if (instance.obstacles.Remove(obstacle))
                instance.isDirty = true;
        }

        /// <summary>
        /// Marks the registry dirty so the NativeArray is rebuilt next query.
        /// Called when an existing obstacle's Inspector values change at edit time.
        /// </summary>
        public static void MarkDirty()
        {
            if (instance != null) instance.isDirty = true;
        }

        // ── Query ─────────────────────────────────────────────────────

        /// <summary>
        /// Returns the packed obstacle data for the current frame.
        /// Rebuilds the NativeArray only if the obstacle set has changed.
        /// </summary>
        public (NativeArray<ObstacleData> data, int count) GetObstacleData()
        {
            if (isDirty) RebuildArray();
            return (dataArray, activeCount);
        }

        // ── Internal ──────────────────────────────────────────────────

        private void RebuildArray()
        {
            isDirty = false;
            activeCount = obstacles.Count;

            if (activeCount == 0) return;

            EnsureCapacity(activeCount);

            for (int i = 0; i < activeCount; i++)
                dataArray[i] = obstacles[i].ToObstacleData();
        }

        /// <summary>
        /// Grow-only capacity — same pattern as <see cref="SpatialHash2D.EnsureCapacity"/>.
        /// Avoids reallocation churn when obstacles are frequently toggled.
        /// </summary>
        private void EnsureCapacity(int count)
        {
            if (dataArray.IsCreated && dataArray.Length >= count)
                return;

            if (dataArray.IsCreated)
                dataArray.Dispose();

            capacity = Math.Max(count, capacity * 2);
            if (capacity < 8) capacity = 8;

            dataArray = new NativeArray<ObstacleData>(
                capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        }

        // ── Disposal ──────────────────────────────────────────────────

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            if (dataArray.IsCreated) dataArray.Dispose();
            obstacles.Clear();
        }
    }
}

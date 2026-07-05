using System;
using System.Runtime.InteropServices;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace FelixFelicis.ParticleRendering
{
    /// <summary>
    /// Manages GPU buffers and issues instanced draw calls for particle circles.
    /// Uses double buffering to avoid GPU/CPU contention — CPU writes to one buffer
    /// while GPU reads from the other.
    /// <para>Lifecycle per frame:</para>
    /// <list type="number">
    ///   <item>Simulation calls <see cref="BeginFrame"/> → writes into NativeArray → calls <see cref="EndFrame"/></item>
    ///   <item>Render pass(es) call <see cref="Draw"/> per camera (no data upload, just bind + draw)</item>
    /// </list>
    /// </summary>
    public class ParticleDrawer : IInstanceWriter<ParticleRenderData>, IInstanceDrawer, IDisposable
    {
        private static readonly int InstanceDataID = Shader.PropertyToID("InstanceData");
        private static readonly int InstanceOffsetID = Shader.PropertyToID("InstanceOffset");
        private static readonly int ScreenSizeID = Shader.PropertyToID("ScreenSize");
        private static readonly int UseScreenSpaceID = Shader.PropertyToID("useScreenSpace");
        private static readonly int WorldToClipID = Shader.PropertyToID("WorldToClipSpace");

        private static readonly int Stride = Marshal.SizeOf<ParticleRenderData>();

        private const string ShaderName = "FelixFelicis/ParticleDraw";
        private const int BufferCount = 2;

        private Mesh quadMesh;
        private Material material;
        private readonly uint[] args = new uint[5];

        // Double buffering — CPU writes to writeIndex, GPU reads from readIndex
        private readonly GraphicsBuffer[] instanceBuffers = new GraphicsBuffer[BufferCount];
        private int writeIndex;
        private GraphicsBuffer argsBuffer;

        private int cachedScreenWidth, cachedScreenHeight;
        private int lastArgsInstanceCount = -1;

        private int currentFrameCount;
        private int lastUploadFrame = -1;
        private bool frameDataReady;
        private bool isBufferLocked;

        public ParticleDrawer()
        {
            quadMesh = QuadMeshHelper.CreateQuadMesh();
        }

        /// <summary>
        /// Lazy init — avoids Shader.Find returning null when Create() runs before shader import.
        /// </summary>
        private bool EnsureMaterial()
        {
            if (material != null) return true;

            var shader = Shader.Find(ShaderName);
            if (shader == null)
            {
                Debug.LogWarning($"[ParticleDrawer] Shader '{ShaderName}' not found yet.");
                return false;
            }

            material = new Material(shader);
            material.SetInt(UseScreenSpaceID, 0);
            material.SetInt(InstanceOffsetID, 0);
            return true;
        }

        /// <summary>
        /// Ensures the write-target buffer at <paramref name="index"/> has at least <paramref name="count"/> capacity.
        /// Returns true if the buffer was (re)created.
        /// </summary>
        private bool EnsureInstanceBuffer(int index, int count)
        {
            ref var buffer = ref instanceBuffers[index];
            if (buffer != null && buffer.IsValid() && buffer.count >= count)
                return false;

            buffer?.Release();
            buffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                GraphicsBuffer.UsageFlags.LockBufferForWrite,
                count, Stride);
            return true;
        }

        /// <inheritdoc/>
        public NativeArray<ParticleRenderData> BeginFrame(int count)
        {
            // Reset each frame — prevents stale Draw() after simulation is destroyed
            frameDataReady = false;

            if (!EnsureMaterial()) return default;
            if (count == 0) return default;

            if (lastUploadFrame == Time.frameCount)
            {
                Debug.LogWarning("[ParticleDrawer] BeginFrame called twice in the same frame.");
                return default;
            }

            // Safety: if previous BeginFrame was not followed by EndFrame (e.g. exception),
            // unlock the orphaned buffer before swapping
            if (isBufferLocked)
            {
                var lockedBuffer = instanceBuffers[writeIndex];
                if (lockedBuffer != null && lockedBuffer.IsValid())
                    lockedBuffer.UnlockBufferAfterWrite<ParticleRenderData>(0);
                isBufferLocked = false;
            }

            // Swap to the other buffer so GPU can finish reading the previous one
            writeIndex = (writeIndex + 1) % BufferCount;

            EnsureInstanceBuffer(writeIndex, count);

            currentFrameCount = count;
            isBufferLocked = true;
            return instanceBuffers[writeIndex].LockBufferForWrite<ParticleRenderData>(0, count);
        }

        /// <inheritdoc/>
        public void EndFrame(int count)
        {
            if (!isBufferLocked) return;

            instanceBuffers[writeIndex].UnlockBufferAfterWrite<ParticleRenderData>(count);
            isBufferLocked = false;
            lastUploadFrame = Time.frameCount;
            frameDataReady = true;

            if (argsBuffer == null || !argsBuffer.IsValid())
            {
                argsBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.IndirectArguments, 1, sizeof(uint) * 5);
                args[0] = quadMesh.GetIndexCount(0);
                args[2] = quadMesh.GetIndexStart(0);
                args[3] = quadMesh.GetBaseVertex(0);
                args[4] = 0;
                lastArgsInstanceCount = -1;
            }

            if (count != lastArgsInstanceCount)
            {
                args[1] = (uint)count;
                argsBuffer.SetData(args);
                lastArgsInstanceCount = count;
            }

            // Always rebind after swap — the active buffer changed each frame
            material.SetBuffer(InstanceDataID, instanceBuffers[writeIndex]);
        }

        /// <inheritdoc/>
        public void Draw(CommandBuffer cmd, Camera cam)
        {
            if (!frameDataReady || currentFrameCount == 0) return;

            if (cam.pixelWidth != cachedScreenWidth || cam.pixelHeight != cachedScreenHeight)
            {
                cachedScreenWidth = cam.pixelWidth;
                cachedScreenHeight = cam.pixelHeight;
                material.SetVector(ScreenSizeID,
                    new Vector4(cachedScreenWidth, cachedScreenHeight, 0, 0));
            }

            Matrix4x4 vp = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true)
                         * cam.worldToCameraMatrix;
            material.SetMatrix(WorldToClipID, vp);

            cmd.DrawMeshInstancedIndirect(quadMesh, 0, material, 0, argsBuffer);
        }

        public void Dispose()
        {
            // Unlock before release to avoid driver warnings
            if (isBufferLocked && instanceBuffers[writeIndex] != null && instanceBuffers[writeIndex].IsValid())
            {
                instanceBuffers[writeIndex].UnlockBufferAfterWrite<ParticleRenderData>(0);
                isBufferLocked = false;
            }

            for (int i = 0; i < BufferCount; i++)
            {
                instanceBuffers[i]?.Release();
                instanceBuffers[i] = null;
            }

            argsBuffer?.Release();
            argsBuffer = null;

            if (material != null)
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(material);
                else UnityEngine.Object.DestroyImmediate(material);
                material = null;
            }

            if (quadMesh != null)
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(quadMesh);
                else UnityEngine.Object.DestroyImmediate(quadMesh);
                quadMesh = null;
            }
        }
    }
}

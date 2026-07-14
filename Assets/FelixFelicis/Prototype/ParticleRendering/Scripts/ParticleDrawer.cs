using System;
using System.Runtime.InteropServices;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace FelixFelicis.Prototype.ParticleRendering
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
    /// <remarks>
    /// All per-draw uniforms are set via CommandBuffer (cmd.SetGlobal*) instead of
    /// material.Set*() to ensure correct binding on RenderGraph + Vulkan.
    /// material.Set*() is immediate and may not sync with deferred CommandBuffer
    /// execution on Vulkan's descriptor set model.
    /// </remarks>
    public class ParticleDrawer : IInstanceWriter<ParticleRenderData>, IInstanceDrawer, IDisposable
    {
        private static readonly int InstanceDataID = Shader.PropertyToID("InstanceData");
        private static readonly int InstanceOffsetID = Shader.PropertyToID("InstanceOffset");
        private static readonly int ScreenSizeID = Shader.PropertyToID("ScreenSize");
        private static readonly int UseScreenSpaceID = Shader.PropertyToID("useScreenSpace");
        private static readonly int WorldToClipID = Shader.PropertyToID("WorldToClipSpace");

        private static readonly int Stride = Marshal.SizeOf<ParticleRenderData>();

        private const string ShaderResourceName = "ParticleDraw";
        private const int BufferCount = 2;

        private Mesh quadMesh;
        private Material material;
        private readonly uint[] args = new uint[5];

        // Double buffering — CPU writes to writeIndex, GPU reads from readIndex
        private readonly GraphicsBuffer[] instanceBuffers = new GraphicsBuffer[BufferCount];
        private int writeIndex;
        private GraphicsBuffer argsBuffer;

        // CPU-side staging — NativeArray allocated once, reused every frame
        private NativeArray<ParticleRenderData> cpuStaging;

        private int lastArgsInstanceCount = -1;

        private int currentFrameCount;
        private int lastUploadFrame = -1;
        private bool frameDataReady;

        public ParticleDrawer()
        {
            quadMesh = QuadMeshHelper.CreateQuadMesh();
        }

        /// <summary>
        /// Lazy init — uses Resources.Load instead of Shader.Find to guarantee the shader
        /// is included in builds. Shader.Find only works if the shader is referenced by a
        /// material asset or listed in Always Included Shaders — neither applies here since
        /// the material is created at runtime.
        /// </summary>
        private bool EnsureMaterial()
        {
            if (material != null) return true;

            var shader = Resources.Load<Shader>(ShaderResourceName);
            if (shader == null)
            {
                Debug.LogError($"[ParticleDrawer] Shader '{ShaderResourceName}' not found in Resources.");
                return false;
            }

            material = new Material(shader);
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
                count, Stride);
            return true;
        }

        /// <summary>
        /// Ensures CPU staging array has at least <paramref name="count"/> capacity.
        /// Grow-only to avoid repeated allocation when count fluctuates.
        /// </summary>
        private void EnsureStagingCapacity(int count)
        {
            if (cpuStaging.IsCreated && cpuStaging.Length >= count)
                return;

            if (cpuStaging.IsCreated)
                cpuStaging.Dispose();

            cpuStaging = new NativeArray<ParticleRenderData>(
                count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
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

            // Swap to the other buffer so GPU can finish reading the previous one
            writeIndex = (writeIndex + 1) % BufferCount;

            EnsureInstanceBuffer(writeIndex, count);
            EnsureStagingCapacity(count);

            currentFrameCount = count;
            return cpuStaging;
        }

        /// <inheritdoc/>
        public void EndFrame(int count)
        {
            if (count == 0) return;

            // Upload CPU staging → GPU buffer (1 memcpy, reliable on all drivers)
            instanceBuffers[writeIndex].SetData(cpuStaging, 0, 0, count);

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
        }

        /// <inheritdoc/>
        public void Draw(CommandBuffer cmd, Camera cam)
        {
            if (!frameDataReady || currentFrameCount == 0) return;

            // All uniforms via CommandBuffer — syncs correctly with RenderGraph + Vulkan.
            // material.Set*() is immediate mode and may not be visible to deferred
            // command buffer execution on Vulkan's descriptor set model.
            cmd.SetGlobalBuffer(InstanceDataID, instanceBuffers[writeIndex]);
            cmd.SetGlobalInt(UseScreenSpaceID, 0);
            cmd.SetGlobalInt(InstanceOffsetID, 0);
            cmd.SetGlobalVector(ScreenSizeID,
                new Vector4(cam.pixelWidth, cam.pixelHeight, 0, 0));

            Matrix4x4 vp = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true)
                         * cam.worldToCameraMatrix;
            cmd.SetGlobalMatrix(WorldToClipID, vp);

            cmd.DrawMeshInstancedIndirect(quadMesh, 0, material, 0, argsBuffer);
        }

        public void Dispose()
        {
            for (int i = 0; i < BufferCount; i++)
            {
                instanceBuffers[i]?.Release();
                instanceBuffers[i] = null;
            }

            argsBuffer?.Release();
            argsBuffer = null;

            if (cpuStaging.IsCreated)
            {
                cpuStaging.Dispose();
                cpuStaging = default;
            }

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

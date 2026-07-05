using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace FelixFelicis.SimulatingFluid
{
    public class ParticleDrawer
    {
        private static readonly int InstanceData = Shader.PropertyToID("InstanceData");
        private static readonly int InstanceOffset = Shader.PropertyToID("InstanceOffset");
        private static readonly int ScreenSize = Shader.PropertyToID("ScreenSize");
        private static readonly int UseScreenSpace = Shader.PropertyToID("useScreenSpace");
        private static readonly int WorldToClipID = Shader.PropertyToID("WorldToClipSpace");

        private const string ShaderName = "FelixFelicis/FluidDraw";

        private readonly Mesh quadMesh;
        private Material material;
        private readonly List<ParticleRenderData> frameData = new();
        private readonly int stride = Marshal.SizeOf<ParticleRenderData>();
        private readonly uint[] args = new uint[5];

        private ComputeBuffer instanceBuffer;
        private ComputeBuffer argsBuffer;

        public ParticleDrawer()
        {
            quadMesh = QuadMeshHelper.CreateQuadMesh();
        }

        /// <summary>
        /// Lazy init — tránh Shader.Find trả null khi Create() chạy trước shader import.
        /// Trả true nếu material sẵn sàng, false nếu shader chưa tìm thấy.
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
            return true;
        }

        public void AddParticle(Vector2 center, float radius, Color color)
        {
            frameData.Add(new ParticleRenderData()
            {
                center = center,
                radius = radius,
                color = color
            });
        }

        public void Dispatch(CommandBuffer cmd, Camera cam)
        {
            if (!EnsureMaterial()) return;

            int count = frameData.Count;
            if (count == 0) return;

            if (instanceBuffer == null || !instanceBuffer.IsValid() || instanceBuffer.count < count)
            {
                instanceBuffer?.Release();
                instanceBuffer = new ComputeBuffer(count, stride);
            }
            instanceBuffer.SetData(frameData);

            if (argsBuffer == null || !argsBuffer.IsValid())
            {
                argsBuffer = new ComputeBuffer(1, sizeof(uint) * 5, ComputeBufferType.IndirectArguments);
            }

            args[0] = quadMesh.GetIndexCount(0);
            args[1] = (uint)count;
            args[2] = quadMesh.GetIndexStart(0);
            args[3] = quadMesh.GetBaseVertex(0);
            args[4] = 0;
            argsBuffer.SetData(args);
            
            material.SetBuffer(InstanceData, instanceBuffer);
            material.SetInt(InstanceOffset, 0);
            material.SetVector(ScreenSize, new Vector4(cam.pixelWidth, cam.pixelHeight, 0f, 0f));
            material.SetInt(UseScreenSpace, 0);
            
            Matrix4x4 vp = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true) * cam.worldToCameraMatrix;
            material.SetMatrix(WorldToClipID, vp);

            cmd.DrawMeshInstancedIndirect(quadMesh, 0, material, 0, argsBuffer);
        }

        public void Clear() => frameData.Clear();

        public void Release()
        {
            instanceBuffer?.Release();
            instanceBuffer = null;
            argsBuffer?.Release();
            argsBuffer = null;

            if (material != null)
            {
                if (Application.isPlaying) Object.Destroy(material);
                else Object.DestroyImmediate(material);
                material = null;
            }

            if (quadMesh != null)
            {
                if (Application.isPlaying) Object.Destroy(quadMesh);
                else Object.DestroyImmediate(quadMesh);
            }
        }
    }
}
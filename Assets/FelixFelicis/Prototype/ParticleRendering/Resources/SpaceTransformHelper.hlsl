#ifndef SPACE_TRANSFORM_HELPER_INCLUDED
#define SPACE_TRANSFORM_HELPER_INCLUDED

// Explicit CBUFFER ensures correct constant buffer binding on Vulkan/GLES SPIR-V.
// Loose globals (no CBUFFER) work on D3D11 but may miscompile on mobile Vulkan.
CBUFFER_START(ParticleUniforms)
    float4x4 WorldToClipSpace;
    float2 ScreenSize;
    int useScreenSpace;
    uint InstanceOffset;
CBUFFER_END

float4 WorldToClipPos(float3 worldPos)
{
    // For UI/2D
    if (useScreenSpace)
    {
        float2 uv = worldPos.xy / ScreenSize;
        return float4(uv * 2 - 1, 0, 1);   // NDC-space
    }

    return mul(WorldToClipSpace, float4(worldPos, 1));
}

#endif

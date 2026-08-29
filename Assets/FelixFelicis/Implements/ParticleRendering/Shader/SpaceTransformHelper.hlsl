#ifndef SPACE_TRANSFORM_HELPER_INCLUDED
#define SPACE_TRANSFORM_HELPER_INCLUDED

CBUFFER_START(ParticleUniforms)
    float4x4 WorldToClipSpace;
    int InstanceOffset;
CBUFFER_END

float4 WorldToClipPos(float3 worldPos)
{
    return mul(WorldToClipSpace, float4(worldPos, 1));
}

#endif
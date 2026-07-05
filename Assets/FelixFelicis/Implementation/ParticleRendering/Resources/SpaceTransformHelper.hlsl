float4x4 WorldToClipSpace;
float2 ScreenSize;
int useScreenSpace;

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
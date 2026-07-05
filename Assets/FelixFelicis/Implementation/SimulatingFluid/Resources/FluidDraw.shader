Shader "FelixFelicis/FluidDraw"
{
    SubShader
    {
        Tags
        {
            "RenderType"="Transparent"
            "Queue"="Transparent"
        }
        
        ZWrite Off
        ZTest Always
        Cull Off
        Blend SrcAlpha OneMinusSrcAlpha
        
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "SpaceTransformHelper.hlsl"

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 clipPos : SV_POSITION;
                float2 localPos : TEXCOORD0;
                float radius : TEXCOORD1;
                float4 color : TEXCOORD2;
            };

            struct ParticleData
            {
                float2 center;
                float radius;
                float4 color;
            };
            
            StructuredBuffer<ParticleData> InstanceData;
            uint InstanceOffset;

            v2f vert(appdata v, uint instanceID : SV_InstanceID)
            {
                ParticleData p = InstanceData[instanceID + InstanceOffset];
                v2f o;
                
                float2 aaPadding = useScreenSpace ? 2.0 : p.radius * 0.1;
                float2 diameter = p.radius * 2;
                float2 localVert = v.vertex.xy * (diameter + aaPadding);
                float3 worldPos = float3(localVert + p.center, 0);
                
                o.localPos = localVert;
                o.radius = p.radius;
                o.clipPos = WorldToClipPos(worldPos);
                o.color = p.color;
                
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float sdf = length(i.localPos) - i.radius;
                
                float fw = fwidth(sdf);
                float alpha = 1.0 - smoothstep(-0.5 * fw, 0.5 * fw, sdf);
                
                return float4(i.color.rgb, i.color.a * alpha);
            }
            ENDCG
        }
    }
}
Shader "Hidden/SGame/OffscreenParticles/Processing"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Cull Off ZWrite Off ZTest Always
        HLSLINCLUDE
        #pragma target 3.5
        #include "../OffscreenParticleDepth.hlsl"
        TEXTURE2D(_OSPDepth);
        TEXTURE2D(_OSPColor);
        SAMPLER(sampler_PointClamp);
        SAMPLER(sampler_LinearClamp);
        float4 _OSPSize;
        float4 _OSPThreshold;
        float _OSPDebug;
        struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };
        Varyings Vert(uint vertexID : SV_VertexID)
        {
            Varyings o;
            o.positionCS = GetFullScreenTriangleVertexPosition(vertexID);
            o.uv = GetFullScreenTriangleTexCoord(vertexID);
            return o;
        }
        float2 ClampUV(float2 uv) { return clamp(uv, 0.5 * _OSPSize.zw, 1.0 - 0.5 * _OSPSize.zw); }
        float DepthFrag(Varyings i) : SV_Target { return OSPSceneDepth(i.uv); }
        half4 Upsample(float2 uv, out float edge)
        {
            edge = 0;
            float2 baseUV = (floor(uv * _OSPSize.xy - 0.5) + 0.5) * _OSPSize.zw;
            float fullDepth = OSPSceneDepth(uv);
            float minDistance = 1e30, maxDistance = 0;
            float2 bestUV = ClampUV(baseUV);
            [unroll] for (int y = 0; y < 2; ++y)
            {
                [unroll] for (int x = 0; x < 2; ++x)
                {
                    float2 sampleUV = ClampUV(baseUV + float2(x, y) * _OSPSize.zw);
                    float depth = SAMPLE_TEXTURE2D_LOD(_OSPDepth, sampler_PointClamp, sampleUV, 0).r;
                    float distance = abs(depth - fullDepth);
                    if (distance < minDistance) { minDistance = distance; bestUV = sampleUV; }
                    maxDistance = max(maxDistance, distance);
                }
            }
            edge = maxDistance > max(_OSPThreshold.x, _OSPThreshold.y * fullDepth) ? 1 : 0;
            half4 result = 0;
            [branch] if (edge > 0.5)
                result = SAMPLE_TEXTURE2D_LOD(_OSPColor, sampler_PointClamp, bestUV, 0);
            else
                result = SAMPLE_TEXTURE2D_LOD(_OSPColor, sampler_LinearClamp, ClampUV(uv), 0);
            return result;
        }
        half4 CompositeFrag(Varyings i) : SV_Target { float edge; return Upsample(i.uv, edge); }
        half4 DebugFrag(Varyings i) : SV_Target
        {
            float edge;
            half4 color = Upsample(i.uv, edge);
            if (_OSPDebug < 1.5) return half4(color.rgb, 1);
            if (_OSPDebug < 2.5) return half4(color.aaa, 1);
            if (_OSPDebug < 3.5)
            {
                float d = SAMPLE_TEXTURE2D_LOD(_OSPDepth, sampler_PointClamp, ClampUV(i.uv), 0).r / _ProjectionParams.z;
                return half4(d.xxx, 1);
            }
            return half4(edge, 0, 0, 1);
        }
        ENDHLSL
        Pass
        {
            Name "Depth Downsample"
            Blend Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment DepthFrag
            ENDHLSL
        }
        Pass
        {
            Name "Premultiplied Composite"
            Blend One OneMinusSrcAlpha
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment CompositeFrag
            ENDHLSL
        }
        Pass
        {
            Name "Debug View"
            Blend Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment DebugFrag
            ENDHLSL
        }
    }
}

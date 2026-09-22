Shader "SGame/OffscreenParticles/CloudVolume"
{
    Properties
    {
        _MainTex ("Particle Texture", 2D) = "white" {}
        _angle_bias ("Angle Bias", Range(0,0.99)) = 0.2
        _near_plane ("Near Plane", Float) = 2
        _fade_in_distance ("Distance Fade In", Float) = 30
        _fade_hold_distance ("Distance Fade Hold", Float) = 10000
        _fade_out_distance ("Distance Fade Out", Float) = 10000
        [HDR] _color ("Color", Color) = (1,1,1,1)
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" "RenderType"="Transparent" }
        Pass
        {
            Name "Offscreen Cloud"
            Tags { "LightMode"="OffscreenParticle" }
            Blend One OneMinusSrcAlpha
            Cull Off ZWrite Off ZTest Always
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #include "../OffscreenParticleDepth.hlsl"
            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                half4 _color;
                float _angle_bias, _near_plane, _fade_in_distance, _fade_hold_distance, _fade_out_distance;
            CBUFFER_END
            float4 _OSPRasterSize;
            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float eyeDepth : TEXCOORD1;
                half alpha : TEXCOORD2;
            };
            Varyings Vert(Attributes i)
            {
                UNITY_SETUP_INSTANCE_ID(i);
                Varyings o;
                float3 world = TransformObjectToWorld(i.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(world);
                o.eyeDepth = -TransformWorldToView(world).z;
                o.uv = TRANSFORM_TEX(i.uv, _MainTex);
                float3 view = _WorldSpaceCameraPos - world;
                float3 viewDir = unity_OrthoParams.w > 0.5 ? UNITY_MATRIX_V[2].xyz : SafeNormalize(view);
                float a = saturate(abs(dot(TransformObjectToWorldNormal(i.normalOS), viewDir)) - _angle_bias);
                a *= a; a *= a;
                float distance = length(view);
                a *= saturate((distance - _near_plane) / max(_fade_in_distance, 0.0001));
                a *= 1 - saturate((distance - _fade_in_distance - _fade_hold_distance) / max(_fade_out_distance, 0.0001));
                o.alpha = a;
                return o;
            }
            half4 Frag(Varyings i) : SV_Target
            {
                float2 screenUV = (i.positionCS.xy - _OSPRasterSize.xy) * _OSPRasterSize.zw;
                float delta = OSPSceneDepth(screenUV) - i.eyeDepth;
                half alpha = saturate(SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv).a * _color.a * i.alpha);
                alpha *= saturate((delta + 0.01) * 10000);
                return half4(_color.rgb * alpha, alpha);
            }
            ENDHLSL
        }
    }
}

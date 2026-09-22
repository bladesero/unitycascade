Shader "SGame/OffscreenParticles/AlphaBlend"
{
    Properties
    {
        [HDR] _TintColor ("Tint Color", Color) = (0.5,0.5,0.5,0.5)
        _MainTex ("Particle Texture", 2D) = "white" {}
        _InvFade ("Soft Particles Factor", Range(0.01,3)) = 1
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" "RenderType"="Transparent" }
        Pass
        {
            Name "Offscreen Particle"
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
                half4 _TintColor;
                float _InvFade;
            CBUFFER_END
            float4 _OSPRasterSize;
            struct Attributes
            {
                float4 positionOS : POSITION;
                half4 color : COLOR;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half4 color : COLOR;
                float2 uv : TEXCOORD0;
                float eyeDepth : TEXCOORD1;
            };
            Varyings Vert(Attributes i)
            {
                UNITY_SETUP_INSTANCE_ID(i);
                Varyings o;
                float3 world = TransformObjectToWorld(i.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(world);
                o.eyeDepth = -TransformWorldToView(world).z;
                o.color = i.color;
                o.uv = TRANSFORM_TEX(i.uv, _MainTex);
                return o;
            }
            half4 Frag(Varyings i) : SV_Target
            {
                float2 screenUV = (i.positionCS.xy - _OSPRasterSize.xy) * _OSPRasterSize.zw;
                float delta = OSPSceneDepth(screenUV) - i.eyeDepth;
                half4 col = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv) * i.color * _TintColor;
                col.a = saturate(col.a) * saturate(delta * _InvFade);
                col.rgb *= col.a;
                return col;
            }
            ENDHLSL
        }
    }
}

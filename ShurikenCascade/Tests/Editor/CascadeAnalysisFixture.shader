Shader "Hidden/ShurikenCascade/AnalysisFixture"
{
    Properties { _MainTex ("Texture", 2D) = "white" {} _Tint ("Tint", Color) = (1,1,1,1) }
    SubShader
    {
        Pass
        {
            Name "Forward"
            HLSLPROGRAM
            #pragma target 4.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_local _ CASCADE_ANALYSIS_COMPLEX
            #include "UnityCG.cginc"
            struct V { float4 position : SV_POSITION; float2 uv : TEXCOORD0; };
            V vert(float4 vertex : POSITION, float2 uv : TEXCOORD0)
            { V o; o.position = UnityObjectToClipPos(vertex); o.uv = uv; return o; }
            sampler2D _MainTex; float4 _Tint;
            float4 frag(V input) : SV_Target
            {
                float4 color = tex2D(_MainTex, input.uv) * _Tint;
                #if defined(CASCADE_ANALYSIS_COMPLEX)
                color.rgb = sin(color.rgb * input.uv.x + input.uv.y) * color.a + cos(input.uv.x);
                #endif
                return color;
            }
            ENDHLSL
        }
        Pass
        {
            Name "Second"
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 vert(float4 vertex : POSITION) : SV_POSITION { return vertex; }
            float4 frag() : SV_Target { return float4(1,0,0,1); }
            ENDHLSL
        }
    }
}

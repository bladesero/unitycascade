#ifndef OSP_DEPTH_INCLUDED
#define OSP_DEPTH_INCLUDED
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

float OSPEyeDepth(float rawDepth)
{
    if (unity_OrthoParams.w > 0.5)
    {
        #if UNITY_REVERSED_Z
        rawDepth = 1.0 - rawDepth;
        #endif
        return lerp(_ProjectionParams.y, _ProjectionParams.z, rawDepth);
    }
    return LinearEyeDepth(rawDepth, _ZBufferParams);
}
float OSPSceneDepth(float2 uv)
{
    return OSPEyeDepth(SampleSceneDepth(saturate(uv)));
}
#endif

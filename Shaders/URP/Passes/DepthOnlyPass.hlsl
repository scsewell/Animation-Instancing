#ifndef ANIMATION_INSTANCING_DEPTH_ONLY_PASS_INCLUDED
#define ANIMATION_INSTANCING_DEPTH_ONLY_PASS_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/Shaders/DepthOnlyPass.hlsl"
#include "../../AnimationInstancing.hlsl"

struct AttributesAnimated
{
    float4 positionOS   : POSITION;
    float2 texcoord     : TEXCOORD0;
    float2 uv2          : TEXCOORD2;
    float2 uv3          : TEXCOORD3;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

Varyings DepthOnlyVertexAnimated(AttributesAnimated input)
{
    Varyings output = (Varyings)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    SkinningData skinningData = GetSkinningData(input.positionOS, input.uv2, input.uv3);
    BonePose pose = GetBonePose(skinningData);
    SkinPosition(pose, skinningData, input.positionOS);

    output.uv = TRANSFORM_TEX(input.texcoord, _BaseMap);
    output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
    return output;
}

#endif // ANIMATION_INSTANCING_DEPTH_ONLY_PASS_INCLUDED

#ifndef ANIMATION_INSTANCING_DEPTH_NORMALS_PASS_INCLUDED
#define ANIMATION_INSTANCING_DEPTH_NORMALS_PASS_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/Shaders/DepthNormalsPass.hlsl"
#include "../../AnimationInstancing.hlsl"

struct AttributesAnimated
{
    float4 positionOS   : POSITION;
    float3 normalOS     : NORMAL;
    float4 tangentOS    : TANGENT;
    float2 texcoord     : TEXCOORD0;
    float2 uv2          : TEXCOORD2;
    float2 uv3          : TEXCOORD3;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

Varyings DepthNormalsVertexAnimated(AttributesAnimated input)
{
    Varyings output = (Varyings)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    SkinningData skinningData = GetSkinningData(input.positionOS, input.uv2, input.uv3);
    BonePose pose = GetBonePose(skinningData);
    Skin(pose, skinningData, input.positionOS, input.normalOS, input.tangentOS);

    output.uv         = TRANSFORM_TEX(input.texcoord, _BaseMap);
    output.positionCS = TransformObjectToHClip(input.positionOS.xyz);

    VertexNormalInputs normalInput = GetVertexNormalInputs(input.normalOS, input.tangentOS);
    output.normalWS = NormalizeNormalPerVertex(normalInput.normalWS);

    return output;
}

#endif // ANIMATION_INSTANCING_DEPTH_NORMALS_PASS_INCLUDED

#ifndef ANIMATION_INSTANCING_SHADOW_CASTER_PASS_INCLUDED
#define ANIMATION_INSTANCING_SHADOW_CASTER_PASS_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/Shaders/ShadowCasterPass.hlsl"
#include "../../AnimationInstancing.hlsl"

struct AttributesAnimated
{
    float4 positionOS   : POSITION;
    float3 normalOS     : NORMAL;
    float2 texcoord     : TEXCOORD0;
    float2 uv2          : TEXCOORD2;
    float2 uv3          : TEXCOORD3;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

Varyings ShadowPassVertexAnimated(AttributesAnimated input)
{
    Varyings output;
    UNITY_SETUP_INSTANCE_ID(input);
    
    SkinningData skinningData = GetSkinningData(input.positionOS, input.uv2, input.uv3);
    BonePose pose = GetBonePose(skinningData);
    SkinPosition(pose, skinningData, input.positionOS);
    SkinNormal(pose, input.normalOS);

    float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
    float3 normalWS = TransformObjectToWorldNormal(input.normalOS);

#if _CASTING_PUNCTUAL_LIGHT_SHADOW
    float3 lightDirectionWS = normalize(_LightPosition - positionWS);
#else
    float3 lightDirectionWS = _LightDirection;
#endif

    float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));

#if UNITY_REVERSED_Z
    positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
#else
    positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
#endif

    output.positionCS = positionCS;
    
    output.uv = TRANSFORM_TEX(input.texcoord, _BaseMap);
    return output;
}

#endif // ANIMATION_INSTANCING_SHADOW_CASTER_PASS_INCLUDED

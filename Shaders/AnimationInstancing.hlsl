#ifndef ANIMATION_INSTANCING_INPUT_INCLUDED
#define ANIMATION_INSTANCING_INPUT_INCLUDED

#include "InstancingCore.hlsl"

// Interpolated quaternions should be renormalized for correctness,
// but with a high enough frame rate the effect might not be noticable.
#define ANIMATION_INSTANCING_HIGH_QUALITY_INTERPOLATION

#ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED

TEXTURE2D(_Animation);  SAMPLER(sampler_Animation);
float4 _Animation_TexelSize;

uint _DrawArgsOffset;
StructuredBuffer<DrawArgs> _DrawArgs;
StructuredBuffer<AnimationData> _AnimationData;
StructuredBuffer<InstanceProperties> _InstanceProperties;

AnimationData _AnimationInfo;
float _AnimationTime;

void Setup()
{
#if defined(SHADER_API_METAL)
    uint instanceIndex = unity_InstanceID;
#else
    uint instanceIndex = _DrawArgs[_DrawArgsOffset].instanceStart + unity_InstanceID;
#endif
    
    InstanceProperties props = _InstanceProperties[instanceIndex];

    // set the mode matrix for the current instance
    UNITY_MATRIX_M = props.model;
    UNITY_MATRIX_I_M = props.modelInv;

    // get the properties of the animation used by this instance
    _AnimationInfo = _AnimationData[props.animationIndex];

    // get the animation time of this instance
    _AnimationTime = props.animationTime;
}

float3 RotatePoint(float3 p, float4 q)
{
    return p + 2.0 * cross(q.xyz, cross(q.xyz, p) + (p * q.w));
}

struct Pose
{
    float3 position;
    float4 rotation;
};

Pose SampleAnimation(half time, half boneCoord)
{
    // Sample the position and rotation for the current frame and the next frame.
    // We interpolate between them based on the time value subframe component.

    // OPTMINIZE THIS----------------------
    half length = _AnimationInfo.textureRegionMax.x - _AnimationInfo.textureRegionMin.x;

    half currFrameTime = time * length;
    half nextFrameTime = fmod(currFrameTime + 1.0, length);
    half subFrame = frac(currFrameTime);

    currFrameTime /= length;
    nextFrameTime /= length;

    half4 min = _AnimationInfo.textureRegionMin.xxyy;
    half4 max = _AnimationInfo.textureRegionMax.xxyy;
    half4 fac = half4(currFrameTime, nextFrameTime, boneCoord, boneCoord + 0.5);

    half4 uv = lerp(min, max, fac) / _Animation_TexelSize.zzww;
    //-----------------------------------

    float3 pos0 = SAMPLE_TEXTURE2D_LOD(_Animation, sampler_Animation, uv.xz, 0).rgb;
    float3 pos1 = SAMPLE_TEXTURE2D_LOD(_Animation, sampler_Animation, uv.yz, 0).rgb;
    float4 rot0 = SAMPLE_TEXTURE2D_LOD(_Animation, sampler_Animation, uv.xw, 0).rgba;
    float4 rot1 = SAMPLE_TEXTURE2D_LOD(_Animation, sampler_Animation, uv.yw, 0).rgba;

    Pose pose;
    pose.position = lerp(pos0, pos1, subFrame);
    pose.rotation = lerp(rot0, rot1, subFrame);

#if defined(ANIMATION_INSTANCING_HIGH_QUALITY_INTERPOLATION)
    pose.rotation = normalize(pose.rotation);
#endif

    return pose;
}

#endif

void Skin(float2 uv2, float2 uv3, inout float4 positionOS, inout float3 normalOS, inout float4 tangentOS)
{
#ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
    // get the bone pose for the frame in object space
    Pose pose = SampleAnimation(_AnimationTime, uv3.x);

    // unapply the bind pose
    float3 bindPose = float3(positionOS.w, uv2.x, uv2.y);
    float3 boneRelativePos = positionOS.xyz - bindPose;

    // apply the bone transformation
    positionOS.xyz = RotatePoint(boneRelativePos, pose.rotation) + pose.position;
    normalOS.xyz = RotatePoint(normalOS.xyz, pose.rotation);
    tangentOS.xyz = RotatePoint(tangentOS.xyz, pose.rotation);
#endif
}

#endif // ANIMATION_INSTANCING_INPUT_INCLUDED


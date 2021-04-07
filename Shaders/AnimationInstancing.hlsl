#ifndef ANIMATION_INSTANCING_INPUT_INCLUDED
#define ANIMATION_INSTANCING_INPUT_INCLUDED

#include "InstancingCore.hlsl"

// Interpolated quaternions should be renormalized for correctness,
// but with a high enough frame rate the effect might not be noticable.
#define ANIMATION_INSTANCING_HIGH_QUALITY_INTERPOLATION


#if defined(UNITY_PROCEDURAL_INSTANCING_ENABLED)
	#define ANIMATION_INSTANCING_ENABLED
#endif


struct SkinningData
{
    float3 bindPose;
    float boneCoord;
};

struct BonePose
{
    float3 position;
    float4 rotation;
};


TEXTURE2D(_Animation);  SAMPLER(sampler_Animation);
float4 _Animation_TexelSize;

uint _DrawArgsOffset;
StructuredBuffer<DrawArgs> _DrawArgs;
StructuredBuffer<AnimationData> _AnimationData;
StructuredBuffer<InstanceProperties> _InstanceProperties;

AnimationData _AnimationInfo;
float _AnimationTime;

#if defined(UNITY_PROCEDURAL_INSTANCING_ENABLED)

void InstanceSetup()
{
#if defined(SHADER_API_METAL)
    uint instanceIndex = unity_InstanceID;
#else
    uint instanceIndex = _DrawArgs[_DrawArgsOffset].instanceStart + unity_InstanceID;
#endif
    
    InstanceProperties props = _InstanceProperties[instanceIndex];

    // set the mode matrix for the current instance
    UNITY_MATRIX_M = MatrixDecompress(props.model);
    UNITY_MATRIX_I_M = MatrixDecompress(props.modelInv);

    // get the properties of the animation used by this instance
    _AnimationInfo = _AnimationData[props.animationIndex];

    // get the animation time of this instance
    _AnimationTime = props.animationTime;
}

#endif // UNITY_PROCEDURAL_INSTANCING_ENABLED

float3 RotatePoint(float3 p, float4 q)
{
    return p + 2.0 * cross(q.xyz, cross(q.xyz, p) + (p * q.w));
}

BonePose SampleAnimation(half time, half boneCoord)
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

    BonePose pose;
    pose.position = lerp(pos0, pos1, subFrame);
    pose.rotation = lerp(rot0, rot1, subFrame);

#if defined(ANIMATION_INSTANCING_HIGH_QUALITY_INTERPOLATION)
    pose.rotation = normalize(pose.rotation);
#endif

    return pose;
}

/**
 * Gets the skinning data from a vertex.
 *
 * position     The POSITION attribute of the vertex.
 * uv2          The TEXCOORD2 attribute of the vertex.
 * uv3          The TEXCOORD3 attribute of the vertex.
 */
SkinningData GetSkinningData(float4 position, float2 uv2, float2 uv3)
{
    SkinningData data;
    data.bindPose = float3(position.w, uv2.x, uv2.y);
    data.boneCoord = uv3.x;
    return data;
}

/**
 * Gets the bone pose of a vertex for the current frame.
 *
 * data         The skinning data of the vertex.
 */
BonePose GetBonePose(SkinningData data)
{
    return SampleAnimation(_AnimationTime, data.boneCoord);
}

/**
 * Applies a bone pose to a vertex position.
 *
 * pose         The bone pose to apply.
 * data         The skinning data of the vertex.
 * positionOS   The object space position of the vertex.
 */
void SkinPosition(BonePose pose, SkinningData data, inout float4 positionOS)
{
#ifdef ANIMATION_INSTANCING_ENABLED
    // unapply the bind pose
    float3 boneRelativePos = positionOS.xyz - data.bindPose;

    // apply the bone transformation
    positionOS.xyz = RotatePoint(boneRelativePos, pose.rotation) + pose.position;
	positionOS.w = 1.0;
#endif
}

/**
 * Applies a bone pose to a normal.
 *
 * pose         The bone pose to apply.
 * normalOS     An object space normal.
 */
void SkinNormal(BonePose pose, inout float3 normalOS)
{
#ifdef ANIMATION_INSTANCING_ENABLED
    normalOS.xyz = RotatePoint(normalOS.xyz, pose.rotation);
#endif
}

/**
 * Applies a bone pose to a tangent.
 *
 * pose         The bone pose to apply.
 * tangentOS    An object space tangent.
 */
void SkinTangent(BonePose pose, inout float4 tangentOS)
{
#ifdef ANIMATION_INSTANCING_ENABLED
    tangentOS.xyz = RotatePoint(tangentOS.xyz, pose.rotation);
#endif
}

/**
 * Applies a bone pose to a vertex.
 *
 * pose         The bone pose to apply.
 * data         The skinning data of the vertex.
 * positionOS   The object space position of the vertex.
 * normalOS     The object space normal of the vertex.
 * tangentOS    The object space tangent of the vertex.
 */
void Skin(BonePose pose, SkinningData data, inout float4 positionOS, inout float3 normalOS, inout float4 tangentOS)
{
    SkinPosition(pose, data, positionOS);
    SkinNormal(pose, normalOS);
    SkinTangent(pose, tangentOS);
}

#endif // ANIMATION_INSTANCING_INPUT_INCLUDED


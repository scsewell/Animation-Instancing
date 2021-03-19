#ifndef ANIMATION_INSTANCING_TYPES_INCLUDED
#define ANIMATION_INSTANCING_TYPES_INCLUDED

struct LodData
{
    uint lodCount;
    float screenHeights[ANIMATION_INSTANCING_MAX_LOD_COUNT];
};

struct Bounds
{
    float3 center;
    float3 extents;
};

struct AnimationData
{
    Bounds bounds;
    float2 textureRegionMin;
    float2 textureRegionMax;
};

struct InstanceData
{
    float3 position;
    float4 rotation;
    float3 scale;
    uint lodIndex; // pack the indices to save space
    uint argsIndex;
    uint animationBase;  // pack the indices to save space
    uint animationIndex;
    float animationTime;
};

struct InstanceProperties
{
    float4x4 model;
    float4x4 modelInv;
    uint animationIndex;
    float animationTime;
};

#endif // ANIMATION_INSTANCING_TYPES_INCLUDED

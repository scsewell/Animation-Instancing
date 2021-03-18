#ifndef ANIMATION_INSTANCING_TYPES_INCLUDED
#define ANIMATION_INSTANCING_TYPES_INCLUDED

struct MeshData
{
    uint argsIndex;
    uint lodCount;
    float lodDistances[ANIMATION_INSTANCING_MAX_LOD_COUNT];
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
    uint meshIndex;
    uint animationStartIndex;
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

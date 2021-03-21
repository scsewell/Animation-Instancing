#ifndef ANIMATION_INSTANCING_TYPES_INCLUDED
#define ANIMATION_INSTANCING_TYPES_INCLUDED

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

struct LodData
{
    uint lodCount;
    float screenHeights[ANIMATION_INSTANCING_MAX_LOD_COUNT];
};

struct DrawArgs
{
    uint indexCount;
    uint instanceCount;
    uint indexStart;
    uint baseVertex;
    uint instanceStart;
};

struct InstanceData
{
    float3 position;
    float4 rotation;
    float3 scale;
    uint lodIndex; // pack the indices to save space
    uint drawCallCount;
    uint drawArgsBaseIndex;
    uint animationBaseIndex;  // pack the indices to save space
    uint animationIndex;
    float animationTime;
};

// uint GetInstanceLodIndex(InstanceData data)
// {
//     return data.packedDrawData & 0x000000FF;
// }
//
// uint GetInstanceDrawCallCount(InstanceData data)
// {
//     return (data.packedDrawData & 0x0000FF00) >> 8;
// }
//
// uint GetInstanceDrawArgsBaseIndex(InstanceData data)
// {
//     return (data/packedDrawData & 0xFFFF0000) >> 16;
// }

struct InstanceProperties
{
    float4x4 model;
    float4x4 modelInv;
    uint animationIndex;
    float animationTime;
};

#endif // ANIMATION_INSTANCING_TYPES_INCLUDED

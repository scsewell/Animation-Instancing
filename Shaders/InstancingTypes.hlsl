#ifndef ANIMATION_INSTANCING_TYPES_INCLUDED
#define ANIMATION_INSTANCING_TYPES_INCLUDED

struct Transform
{
    float3 position;
    float4 rotation;
    float3 scale;
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

struct LodData
{
    uint lodCount;
    float screenHeights[ANIMATION_INSTANCING_MAX_LOD_COUNT];
    uint shadowLodIndices;
};

struct DrawArgs
{
    uint indexCount;
    uint instanceCount;
    uint indexStart;
    uint baseVertex;
    uint instanceStart;
};

struct CompressedQuaternion
{
	uint packedValue;
};

struct CompressedTransform
{
	float3 position;
	CompressedQuaternion rotation;
	float scale;
};

struct InstanceData
{
    CompressedTransform transform;
    uint lodIndex; // pack the indices to save space
    uint countBaseIndex;
    uint animationIndex;
    float animationTime;
};

struct InstanceProperties
{
    float3x4 model;
    float3x4 modelInv;
    uint animationIndex;
    float animationTime;
};

CBUFFER_START(CullingPropertyBuffer)
float4x4 _ViewProj;
float3 _CameraPosition;
float _LodScale; // 1 / (2 * tan((fov / 2) * (pi / 180)))
float _LodBias;
float _ShadowDistance;
int _InstanceCount;
uint _NumInstanceCounts;
CBUFFER_END

#endif // ANIMATION_INSTANCING_TYPES_INCLUDED

﻿#ifndef ANIMATION_INSTANCING_CORE_INCLUDED
#define ANIMATION_INSTANCING_CORE_INCLUDED

#define ANIMATION_INSTANCING_MAX_LOD_COUNT 5
#define ANIMATION_INSTANCING_BITS_NEEDED_FOR_LOD_INDEX 3
#define ANIMATION_INSTANCING_LOD_INDEX_BITS_MASK 0x7
#define ANIMATION_INSTANCING_MAX_SUB_MESH_COUNT 5
#define ANIMATION_INSTANCING_MAX_INSTANCE_TYPES (1 << 11)
#define ANIMATION_INSTANCING_NULL_SORT_KEY 0xffffffff

#include "InstancingTypes.hlsl"
#include "MatrixUtils.hlsl"

uint GetShadowLodIndex(LodData lod, uint lodIndex)
{
   uint shift = lodIndex * ANIMATION_INSTANCING_BITS_NEEDED_FOR_LOD_INDEX; 
   return (lod.shadowLodIndices >> shift) & ANIMATION_INSTANCING_LOD_INDEX_BITS_MASK;
}

uint CreateSortingKey(uint passIndex, uint countIndex, uint instanceIndex)
{
    return (instanceIndex << 12) | ((passIndex & 0x1) << 11)  | (countIndex & 0x7ff);
}

uint GetInstanceIndexFromSortingKey(uint sortingKey)
{
     return sortingKey >> 12;
}

float4 DecompressRotation(CompressedQuaternion q)
{
    uint r = q.packedValue;
    
    float4 values;
    values.x = float(int((r >>  2) & 0x3ff) - 512) / 512.0;
    values.y = float(int((r >> 12) & 0x3ff) - 512) / 512.0;
    values.z = float(int((r >> 22) & 0x3ff) - 512) / 512.0;
    values.w = sqrt(1.0 - dot(values.xyz, values.xyz));
    
    uint key = r & 0x3;

    UNITY_FLATTEN
    switch (key)
    {
        case 0:
            return values.wxyz; 
        case 1:
            return values.xwyz;  
        case 2:
            return values.xywz; 
        default:
            return values.xyzw; 
    }
}

Transform DecompressTransform(CompressedTransform t)
{
    Transform result;
    result.position = t.position.xyz;
    result.rotation = DecompressRotation(t.rotation);
    result.scale = t.scale.xxx;
    return result;
}

#endif // ANIMATION_INSTANCING_CORE_INCLUDED

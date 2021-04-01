#ifndef ANIMATION_INSTANCING_CORE_INCLUDED
#define ANIMATION_INSTANCING_CORE_INCLUDED

#define ANIMATION_INSTANCING_MAX_LOD_COUNT 5
#define ANIMATION_INSTANCING_NULL_SORT_KEY 0xffffffff

uint CreateSortingKey(uint lodIndex, uint instanceTypeIndex, uint instanceIndex)
{
    return ((instanceTypeIndex << 23) & 0xff800000) | ((lodIndex << 20) & 0x00700000) | (instanceIndex & 0x000fffff);
}

uint GetInstanceIndexFromSortingKey(uint sortingKey)
{
     return sortingKey & 0x000fffff;
}

#include "MatrixUtils.hlsl"
#include "InstancingTypes.hlsl"

#endif // ANIMATION_INSTANCING_CORE_INCLUDED

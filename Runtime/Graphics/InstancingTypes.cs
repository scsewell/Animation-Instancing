using System;
using System.Runtime.InteropServices;

using Unity.Mathematics;

using UnityEngine;

namespace AnimationInstancing
{
    // This file is kept in sync with InstancingTypes.hlsl

    [Serializable]
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    unsafe struct LodData
    {
        public static readonly int k_size = Marshal.SizeOf<LodData>();

        public uint lodCount;
        public fixed float screenHeights[Constants.k_MaxLodCount];
    };

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct AnimationData
    {
        public static readonly int k_size = Marshal.SizeOf<AnimationData>();

        public Bounds bounds;
        public float2 textureRegionMin;
        public float2 textureRegionMax;
    };

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct InstanceData
    {
        public static readonly int k_size = Marshal.SizeOf<InstanceData>();

        public float3 position;
        public quaternion rotation;
        public float3 scale;
        public uint lodIndex;
        public uint argsIndex;
        public uint animationBase;
        public uint animationIndex;
        public float animationTime;
    };

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct InstanceProperties
    {
        public static readonly int k_size = Marshal.SizeOf<InstanceProperties>();

        public float4x4 model;
        public float4x4 modelInv;
        public uint animationIndex;
        public float animationTime;
    };
}

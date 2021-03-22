using System;
using System.Runtime.InteropServices;

using Unity.Mathematics;

using UnityEngine;

namespace AnimationInstancing
{
    // This file is kept in sync with InstancingTypes.hlsl

    [Serializable]
    [StructLayout(LayoutKind.Sequential)]
    struct AnimationData
    {
        public static readonly int k_size = Marshal.SizeOf<AnimationData>();

        public Bounds bounds;
        public float2 textureRegionMin;
        public float2 textureRegionMax;
    }

    /// <summary>
    /// A struct that stores the level of detail configuration for an instanced mesh.
    /// </summary>
    [Serializable]
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct LodData
    {
        internal static readonly int k_size = Marshal.SizeOf<LodData>();

        [SerializeField]
        uint lodCount;
        [SerializeField]
        fixed float screenHeights[Constants.k_MaxLodCount];

        /// <summary>
        /// Creates a new <see cref="LodData"/> instance.
        /// </summary>
        /// <param name="lods">The lod levels ordered by decreasing detail. If null or empty no lods will be used.</param>
        public LodData(LodInfo[] lods)
        {
            var count = lods == null ? 0 : Mathf.Min(lods.Length, Constants.k_MaxLodCount);

            if (count > 0)
            {
                lodCount = (uint)count;

                for (var i = 0; i < count; i++)
                {
                    screenHeights[i] = lods[i].ScreenHeight;
                }
            }
            else
            {
                lodCount = 1;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DrawArgs
    {
        public static readonly int k_size = Marshal.SizeOf<DrawArgs>();

        public uint indexCount;
        public uint instanceCount;
        public uint indexStart;
        public uint baseVertex;
        public uint instanceStart;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct InstanceData
    {
        public static readonly int k_size = Marshal.SizeOf<InstanceData>();

        public float3 position;
        public quaternion rotation;
        public float3 scale;
        public uint lodIndex;
        public uint drawCallCount;
        public uint drawArgsBaseIndex;
        public uint animationBaseIndex;
        public uint animationIndex;
        public float animationTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct InstanceProperties
    {
        public static readonly int k_size = Marshal.SizeOf<InstanceProperties>();

        public float4x4 model;
        public float4x4 modelInv;
        public uint animationIndex;
        public float animationTime;
    }
}

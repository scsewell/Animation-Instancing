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
        internal uint lodCount;
        [SerializeField]
        fixed float screenHeights[Constants.k_maxLodCount];

        /// <summary>
        /// Creates a new <see cref="LodData"/> instance.
        /// </summary>
        /// <param name="lods">The lod levels ordered by decreasing detail. If null or empty no lods will be used.</param>
        public LodData(LodInfo[] lods)
        {
            var count = lods == null ? 0 : Mathf.Min(lods.Length, Constants.k_maxLodCount);

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
        public uint instanceTypeIndex;
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

        public float3x4 model;
        public float3x4 modelInv;
        public uint animationIndex;
        public float animationTime;
    }
    
    [StructLayout(LayoutKind.Sequential)]
    struct CullingPropertyBuffer
    {
        public static readonly int k_size = Marshal.SizeOf<CullingPropertyBuffer>();

        public float4x4 _ViewProj;
        public float3 _CameraPosition;
        public float _LodScale; // 1 / (2 * tan((fov / 2) * (pi / 180)))
        public float _LodBias;
        public int _InstanceCount;
        public int _DrawArgsCount;
    }
    
    [StructLayout(LayoutKind.Sequential)]
    struct SortingPropertyBuffer
    {
        public static readonly int k_size = Marshal.SizeOf<SortingPropertyBuffer>();

        public uint _NumKeys;
        public int  _NumBlocksPerThreadGroup;
        public uint _NumThreadGroups;
        public uint _NumThreadGroupsWithAdditionalBlocks;
        public uint _NumReduceThreadGroupPerBin;
        public uint _NumScanValues;
    }
}

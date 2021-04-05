using System;
using System.Runtime.InteropServices;

using Unity.Mathematics;

using UnityEngine;

namespace AnimationInstancing
{
    // This file is kept in sync with InstancingTypes.hlsl

    /// <summary>
    /// A struct that stores a quaternion compressed using the smallest three method.
    /// </summary>
    /// <remarks>
    /// Each component in the rotation has about 9 bits of precision.
    /// </remarks>
    /// <seealso href="http://gafferongames.com/networked-physics/snapshot-compression/"/>
    [StructLayout(LayoutKind.Sequential)]
    public struct CompressedQuaternion
    {
        uint value;
        
        public CompressedQuaternion(quaternion rotation)
        {
            var maxValue = float.MinValue;
            var maxIndex = 0;
            var sign = 1f;

            for (var i = 0; i < 4; i++)
            {
                var val = rotation.value[i];
                var abs = math.abs(val);

                if (maxValue < abs)
                {
                    maxValue = abs;
                    maxIndex = i;
                    sign = math.sign(val);
                }
            }

            var values = (uint4)math.round(((sign * rotation.value) + 1f) * 512) & 0x3ff;

            uint3 compressedValues;
            switch (maxIndex)
            {
                case 0:
                    compressedValues = values.yzw;
                    break;
                case 1:
                    compressedValues = values.xzw;
                    break;
                case 2:
                    compressedValues = values.xyw;
                    break;
                default:
                    compressedValues = values.xyz;
                    break;
            }

            value = (compressedValues.z << 22) |
                    (compressedValues.y << 12) |
                    (compressedValues.x <<  2) |
                    ((uint)maxIndex & 0x3);
        }
    }
    
    /// <summary>
    /// A struct that stores a compressed transform.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CompressedTransform
    {
        /// <summary>
        /// The world space position.
        /// </summary>
        public float3 position;
        
        /// <summary>
        /// The world space rotation.
        /// </summary>
        public CompressedQuaternion rotation;
        
        /// <summary>
        /// The world space scale.
        /// </summary>
        public float scale;
    }

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

        public CompressedTransform transform;
        public uint lodIndex;
        public uint instanceTypeIndex;
        public uint drawCallCount;
        public uint drawArgsBaseIndex;
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

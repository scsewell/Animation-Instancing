using UnityEngine;

namespace AnimationInstancing
{
    /// <summary>
    /// A class containing constant values.
    /// </summary>
    public class Constants
    {
        /// <summary>
        /// The maximum number of LODs an instance can use.
        /// </summary>
        public const int k_maxLodCount = 5;
        
        /// <summary>
        /// The maximum number of sub meshes each instance can use.
        /// </summary>
        public const int k_maxSubMeshCount = 5;

        /// <summary>
        /// The maximum number of instances that can be rendered.
        /// </summary>
        internal const int k_maxInstanceCount = 1 << 20;

        /// <summary>
        /// The maximum number of unique mesh/sub mesh/material combinations that can be active simultaneously.
        /// </summary>
        internal const int k_maxInstanceTypes = 1 << 12;

        /// <summary>
        /// The number of bits in the sorting keys to sort, starting from the least significant bit.
        /// </summary>
        internal const int k_sortKeyBits = k_sortBitsPerPass * 3;
        
        /// <summary>
        /// The number of elements processed by each thread in a sorting pass.
        /// </summary>
        internal const int k_sortElementsPerThread = 4;
        
        /// <summary>
        /// The number of bits of the keys that are processed in a single sorting pass.
        /// </summary>
        internal const int k_sortBitsPerPass = 4;
        
        /// <summary>
        /// The number of sorting bins needed per thread.
        /// </summary>
        internal const int k_sortBinCount = 1 << k_sortBitsPerPass;
    }

    /// <summary>
    /// A class containing shader property IDs.
    /// </summary>
    static class Properties
    {
        public static class Culling
        {
            public static readonly int _ConstantBuffer = Shader.PropertyToID("CullingPropertyBuffer");

            public static readonly int _LodData = Shader.PropertyToID("_LodData");
            public static readonly int _AnimationData = Shader.PropertyToID("_AnimationData");
            public static readonly int _InstanceData = Shader.PropertyToID("_InstanceData");
            public static readonly int _InstanceCounts = Shader.PropertyToID("_InstanceCounts");
            public static readonly int _SortKeys = Shader.PropertyToID("_SortKeys");
        }
        
        public static class Sort
        {
            public static readonly int _ConstantBuffer = Shader.PropertyToID("SortingPropertyBuffer");
            
            public static readonly int _ShiftBit = Shader.PropertyToID("_ShiftBit");
            public static readonly int _SrcBuffer = Shader.PropertyToID("_SrcBuffer");
            public static readonly int _DstBuffer = Shader.PropertyToID("_DstBuffer");
            public static readonly int _SumTable = Shader.PropertyToID("_SumTable");
            public static readonly int _ReduceTable = Shader.PropertyToID("_ReduceTable");
            public static readonly int _Scan = Shader.PropertyToID("_Scan");
            public static readonly int _ScanScratch = Shader.PropertyToID("_ScanScratch");
        }

        public static class Compact
        {
            public static readonly int _ConstantBuffer = Shader.PropertyToID("CullingPropertyBuffer");

            public static readonly int _InstanceData = Shader.PropertyToID("_InstanceData");
            public static readonly int _SortKeys = Shader.PropertyToID("_SortKeys");
            public static readonly int _InstanceProperties = Shader.PropertyToID("_InstanceProperties");
        }

        public static class SetDrawArgs
        {
            public static readonly int _ConstantBuffer = Shader.PropertyToID("CullingPropertyBuffer");

            public static readonly int _InstanceCounts = Shader.PropertyToID("_InstanceCounts");
            public static readonly int _InstanceTypeData = Shader.PropertyToID("_InstanceTypeData");
            public static readonly int _DrawArgs = Shader.PropertyToID("_DrawArgs");
        }

        public static class Main
        {
            public static readonly int _Animation = Shader.PropertyToID("_Animation");
            public static readonly int _DrawArgsOffset = Shader.PropertyToID("_DrawArgsOffset");
            public static readonly int _DrawArgs = Shader.PropertyToID("_DrawArgs");
            public static readonly int _AnimationData = Shader.PropertyToID("_AnimationData");
            public static readonly int _InstanceProperties = Shader.PropertyToID("_InstanceProperties");
        }
    }

    /// <summary>
    /// A class containing compute shader kernel names.
    /// </summary>
    static class Kernels
    {
        public static class Culling
        {
            public const string k_resetCounts = "ResetCounts";
            public const string k_cull = "Cull";
        }
        
        public static class Sort
        {
            public const string k_count = "Count";
            public const string k_countReduce = "CountReduce";
            public const string k_scan = "Scan";
            public const string k_scanAdd = "ScanAdd";
            public const string k_scatter = "Scatter";
        }

        public static class Compact
        {
            public const string k_main = "CSMain";
        }
        
        public static class SetDrawArgs
        {
            public const string k_main = "CSMain";
        }
    }
}


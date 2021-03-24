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
        /// The number of threads per group used by the culling kernel.
        /// </summary>
        internal const int k_cullingThreadsPerGroup = 64;
        
        /// <summary>
        /// The number of threads per group used by the culling kernel.
        /// </summary>
        internal const int k_scanInBucketThreadsPerGroup = k_scanBucketSize / 2;

        /// <summary>
        /// The number of threads per group used by the culling kernel.
        /// </summary>
        internal const int k_scanAcrossBucketThreadsPerGroup = 1024;

        /// <summary>
        /// The number of elements processed in a scan bucket.
        /// </summary>
        internal const int k_compactThreadsPerGroup = 512;
        
        /// <summary>
        /// The number of elements processed in a scan bucket.
        /// </summary>
        internal const int k_scanBucketSize = 512;
    }

    /// <summary>
    /// A class containing shader property IDs.
    /// </summary>
    static class Properties
    {
        // constant buffer properties
        public static readonly int _CullingPropertyBuffer = Shader.PropertyToID("CullingPropertyBuffer");
        
        // culling properties
        public static readonly int _LodData = Shader.PropertyToID("_LodData");
        public static readonly int _AnimationData = Shader.PropertyToID("_AnimationData");
        public static readonly int _InstanceData = Shader.PropertyToID("_InstanceData");
        public static readonly int _DrawArgs = Shader.PropertyToID("_DrawArgs");
        public static readonly int _IsVisible = Shader.PropertyToID("_IsVisible");

        // scan properties
        public static readonly int _ScanIn = Shader.PropertyToID("_ScanIn");
        public static readonly int _ScanOut = Shader.PropertyToID("_ScanOut");
        public static readonly int _ScanIntermediate = Shader.PropertyToID("_ScanIntermediate");

        // compaction properties
        public static readonly int _ScanInBucket = Shader.PropertyToID("_ScanInBucket");
        public static readonly int _ScanAcrossBuckets = Shader.PropertyToID("_ScanAcrossBuckets");
        public static readonly int _InstanceProperties = Shader.PropertyToID("_InstanceProperties");

        // shader properties
        public static readonly int _Animation = Shader.PropertyToID("_Animation");
    }

    /// <summary>
    /// A class containing compute shader kernel names.
    /// </summary>
    static class Kernels
    {
        public const string k_cullingKernel = "CSMain";
        public const string k_scanInBucketKernel = "ScanInBucket";
        public const string k_scanAcrossBucketsKernel = "ScanAcrossBuckets";
        public const string k_compactKernel = "CSMain";
    }
}

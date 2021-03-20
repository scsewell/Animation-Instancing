using UnityEngine;

namespace AnimationInstancing
{
    /// <summary>
    /// A class containing constant values.
    /// </summary>
    class Constants
    {
        /// <summary>
        /// The maximum number of LODs an instance can use.
        /// </summary>
        public const int k_MaxLodCount = 5;
    }

    /// <summary>
    /// A class containing shader property IDs.
    /// </summary>
    static class Properties
    {
        // these use a cbuffer
        //public static readonly int _ViewProj = Shader.PropertyToID("_ViewProj");
        //public static readonly int _CameraPosition = Shader.PropertyToID("_CameraPosition");
        //public static readonly int _LodScale = Shader.PropertyToID("_LodScale");
        //public static readonly int _LodBias = Shader.PropertyToID("_LodBias");

        // culling properties
        public static readonly int _MeshData = Shader.PropertyToID("_MeshData");
        public static readonly int _AnimationData = Shader.PropertyToID("_AnimationData");
        public static readonly int _InstanceData = Shader.PropertyToID("_InstanceData");
        public static readonly int _DrawArgs = Shader.PropertyToID("_DrawArgs");
        public static readonly int _IsVisible = Shader.PropertyToID("_IsVisible");

        // scan properties
        public static readonly int _ScanBucketCount = Shader.PropertyToID("_ScanBucketCount");
        public static readonly int _ScanIn = Shader.PropertyToID("_ScanIn");
        public static readonly int _ScanOut = Shader.PropertyToID("_ScanOut");
        public static readonly int _ScanIntermediate = Shader.PropertyToID("_ScanIntermediate");

        // compaction properties
        public static readonly int _InstanceProperties = Shader.PropertyToID("_InstanceProperties");

        // shader properties
        public static readonly int _Animation = Shader.PropertyToID("_Animation");
    }

    /// <summary>
    /// A class containing compute shader kernel names.
    /// </summary>
    static class Kernels
    {
        public const string k_CullingKernel = "CSMain";
        public const string k_ScanInBucketKernel = "ScanInBucket";
        public const string k_ScanAcrossBucketsKernel = "ScanAcrossBuckets";
        public const string k_CompactKernel = "CSMain";
    }
}

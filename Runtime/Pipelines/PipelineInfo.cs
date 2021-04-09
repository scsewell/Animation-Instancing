using UnityEngine;

namespace AnimationInstancing
{
    struct PipelineInfo
    {
        bool m_shadowsEnabled;
        float m_shadowDistance;

        public bool shadowsEnabled => m_shadowsEnabled && m_shadowDistance > 0f;
        public float shadowDistance => m_shadowDistance;

        public PipelineInfo(bool shadowsEnabled, float shadowDistance)
        {
            m_shadowsEnabled = shadowsEnabled;
            m_shadowDistance = shadowDistance;
        }
        
        public static void GetInfoForCurrentPipeline(out PipelineInfo pipelineInfo)
        {
#if UNITY_URP_11_0_0_OR_NEWER
            if (UniversalPipelineUtils.IsCurrentPipeline())
            {
                UniversalPipelineUtils.GetPipelineInfo(out pipelineInfo);
                return;
            }
#endif
            if (BuiltInPipelineUtils.IsCurrentPipeline())
            {
                BuiltInPipelineUtils.GetPipelineInfo(out pipelineInfo);
                return;
            }
            
            Debug.LogError( "Unknown graphics pipeline!");
            pipelineInfo = default;
        }
    }
}

using UnityEngine;
using UnityEngine.Rendering;

namespace AnimationInstancing
{
    static class BuiltInPipelineUtils
    {
        public static bool IsCurrentPipeline()
        {
            return GraphicsSettings.renderPipelineAsset == null;
        }

        public static void GetPipelineInfo(out PipelineInfo pipelineInfo)
        {
            pipelineInfo = new PipelineInfo(
                QualitySettings.shadows != ShadowQuality.Disable,
                QualitySettings.shadowDistance
            );
        }
    }
}

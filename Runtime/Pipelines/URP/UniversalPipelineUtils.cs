#if UNITY_URP_11_0_0_OR_NEWER
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace AnimationInstancing
{
    static class UniversalPipelineUtils
    {
        public static bool IsCurrentPipeline()
        {
            return GraphicsSettings.renderPipelineAsset is UniversalRenderPipelineAsset;
        }

        public static void GetPipelineInfo(out PipelineInfo pipelineInfo)
        {
            var pipeline = GraphicsSettings.renderPipelineAsset as UniversalRenderPipelineAsset;

            pipelineInfo = new PipelineInfo(
                pipeline.supportsMainLightShadows || pipeline.supportsAdditionalLightShadows,
                pipeline.shadowDistance
            );
        }
    }
}

#endif // UNITY_URP_11_0_0_OR_NEWER

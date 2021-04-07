using UnityEngine;
using UnityEngine.Rendering;

namespace AnimationInstancing
{
    /// <summary>
    /// An asset that contains referenced to required resources.
    /// </summary>
    [CreateAssetMenu(fileName = "New InstancingResources", menuName = "Instanced Animation/Resources", order = 410)]
    class InstancingResources : ScriptableObject
    {
        [SerializeField]
        ComputeShader m_culling;
        [SerializeField]
        ComputeShader m_sort;
        [SerializeField]
        ComputeShader m_sortDXC;
        [SerializeField]
        ComputeShader m_compact;
        [SerializeField]
        ComputeShader m_setDrawArgs;

        public ComputeShader Culling => m_culling;
        
        public ComputeShader Sort
        {
            get
            {
                switch (SystemInfo.graphicsDeviceType)
                {
                    case GraphicsDeviceType.Metal:
                    case GraphicsDeviceType.Vulkan:
                    case GraphicsDeviceType.Direct3D12: return m_sortDXC;
                    default:                            return m_sort;
                }
            }
        }

        public ComputeShader Compact => m_compact;
        
        public ComputeShader SetDrawArgs => m_setDrawArgs;
    }
}

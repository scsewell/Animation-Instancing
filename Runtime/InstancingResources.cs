using UnityEngine;

namespace InstancedAnimation
{
    /// <summary>
    /// An asset that contains referenced to required resources.
    /// </summary>
    [CreateAssetMenu(fileName = "New InstancingResources", menuName = "Framework/Animation/Resources")]
    class InstancingResources : ScriptableObject
    {
        [SerializeField]
        ComputeShader m_culling;
        [SerializeField]
        ComputeShader m_scan;
        [SerializeField]
        ComputeShader m_compact;

        public ComputeShader Culling => m_culling;
        public ComputeShader Scan => m_scan;
        public ComputeShader Compact => m_compact;
    }
}

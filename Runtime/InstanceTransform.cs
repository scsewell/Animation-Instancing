using Unity.Mathematics;

namespace AnimationInstancing
{
    /// <summary>
    /// A struct that stores the transform of a single instance.
    /// </summary>
    public struct InstanceTransform
    {
        float3 position;
        quaternion rotation;
        float3 scale;
    }
}

using System;
using UnityEngine;

namespace AnimationInstancing
{
    /// <summary>
    /// A struct that stores details about an animation baked into an animation atlas texture.
    /// </summary>
    [Serializable]
    public struct InstancedAnimation
    {
        [SerializeField]
        [Tooltip("The animation data used for rendering.")]
        AnimationData m_data;
        
        [SerializeField]
        [Tooltip("The length of the animation in seconds.")]
        float m_length;

        /// <summary>
        /// The animation data used for rendering.
        /// </summary>
        internal AnimationData Data => m_data;

        /// <summary>
        /// The length of the animation in seconds.
        /// </summary>
        public float Length => m_length;

        /// <summary>
        /// Creates a new <see cref="InstancedAnimation"/> instance.
        /// </summary>
        /// <param name="region">The area of the animation texture containing the animation.</param>
        /// <param name="bounds">The bounds of the meshes during this animation.</param>
        /// <param name="length">The length of the animation in seconds. Must be larger than zero.</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="length"/> is not larger than zero.</exception>
        public InstancedAnimation(RectInt region, Bounds bounds, float length)
        {
            if (length <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(length), length, "Must be greater than 0!");
            }

            m_data = new AnimationData
            {
                bounds = bounds,
                textureRegionMin = (Vector2)region.min,
                textureRegionMax = (Vector2)region.max,
            };
            m_length = length;
        }
    }
}

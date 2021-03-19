using System;
using UnityEngine;

namespace AnimationInstancing
{
    /// <summary>
    /// A class that stores details about an animation baked into an animation atlas texture.
    /// </summary>
    [Serializable]
    public struct InstancedAnimation
    {
        [SerializeField]
        [Tooltip("The area of the animation texture containing this animation.")]
        RectInt m_region;

        [SerializeField]
        [Tooltip("The length of the animation in seconds.")]
        float m_length;

        [SerializeField]
        [Tooltip("The bounds of the meshes during this animation.")]
        Bounds m_bounds;

        /// <summary>
        /// The area of the animation texture containing the animation.
        /// </summary>
        public RectInt Region => m_region;

        /// <summary>
        /// The length of the animation in seconds.
        /// </summary>
        public float Length => m_length;

        /// <summary>
        /// The bounds of the meshes during this animation.
        /// </summary>
        public Bounds Bounds => m_bounds;

        /// <summary>
        /// Creates a new <see cref="InstancedAnimation"/> instance.
        /// </summary>
        /// <param name="region">The area of the animation texture containing the animation.</param>
        /// <param name="length">The length of the animation in seconds. Must be larger than zero.</param>
        /// <param name="bounds">The bounds of the meshes during this animation.</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="length"/> is not larger than zero.</exception>
        public InstancedAnimation(RectInt region, float length, Bounds bounds)
        {
            if (length <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(length), length, "Must be greater than 0!");
            }

            m_region = region;
            m_length = length;
            m_bounds = bounds;
        }
    }
}

using System;
using UnityEngine;

namespace InstancedAnimation
{
    /// <summary>
    /// A class that stores a baked animation.
    /// </summary>
    [Serializable]
    public struct Animation
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
        /// Creates a new <see cref="Animation"/> instance.
        /// </summary>
        /// <param name="region">The area of the animation texture containing the animation.</param>
        /// <param name="length">The length of the animation in seconds.</param>
        /// <param name="fps">The frames per second of the animation.</param>
        /// <param name="bounds">The bounds of the meshes during this animation.</param>
        public Animation(RectInt region, float length, Bounds bounds)
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

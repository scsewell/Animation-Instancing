using System;

using UnityEngine;

namespace InstancedAnimation
{
    /// <summary>
    /// An asset that stores content that can be played back using instanced animation.
    /// </summary>
    [CreateAssetMenu(fileName = "New InstancedAnimation", menuName = "Framework/Animation/Instanced Animation")]
    public class InstancedAnimationAsset : ScriptableObject
    {
        [SerializeField]
        [Tooltip("The meshes that can be used when playing the animation.")]
        InstancedMesh[] m_meshes;

        [SerializeField]
        [Tooltip("The texture containing the animation data.")]
        Texture2D m_texture;

        [SerializeField]
        [Tooltip("The animations in the animation texture.")]
        Animation[] m_animations;

        /// <summary>
        /// The meshes that can be used when playing the animation.
        /// </summary>
        public InstancedMesh[] Meshes => m_meshes;

        /// <summary>
        /// The texture containing the animation data.
        /// </summary>
        public Texture2D Texture => m_texture;

        /// <summary>
        /// The animations in the animation texture.
        /// </summary>
        public Animation[] Animations => m_animations;

        /// <summary>
        /// Creates a <see cref="InstancedAnimationAsset"/> instance.
        /// </summary>
        /// <param name="meshes">The meshes that can be used when playing the animations.</param>
        /// <param name="texture">The texture containing the animation data.</param>
        /// <param name="animations">The animations in the animation texture.</param>
        public static InstancedAnimationAsset Create(InstancedMesh[] meshes, Texture2D texture, Animation[] animations)
        {
            if (meshes == null)
            {
                throw new ArgumentNullException(nameof(meshes));
            }
            if (texture == null)
            {
                throw new ArgumentNullException(nameof(texture));
            }
            if (animations == null)
            {
                throw new ArgumentNullException(nameof(animations));
            }

            var asset = CreateInstance<InstancedAnimationAsset>();
            asset.m_meshes = meshes;
            asset.m_texture = texture;
            asset.m_animations = animations;
            return asset;
        }
    }
}

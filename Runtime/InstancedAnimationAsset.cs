using System;

using UnityEngine;

namespace AnimationInstancing
{
    /// <summary>
    /// A class that stores an animation atlas texture and the details about the animation graph.
    /// </summary>
    [Serializable]
    public class InstancedAnimationSet
    {
        [SerializeField]
        [Tooltip("The texture containing the animation data.")]
        Texture2D m_texture;

        [SerializeField]
        [Tooltip("The animations in the animation texture.")]
        InstancedAnimation[] m_animations;

        /// <summary>
        /// The texture containing the animation data.
        /// </summary>
        public Texture2D Texture => m_texture;

        /// <summary>
        /// The animations in the animation texture.
        /// </summary>
        public InstancedAnimation[] Animations => m_animations;

        /// <summary>
        /// Creates a <see cref="InstancedAnimationSet"/> instance.
        /// </summary>
        /// <param name="texture">The texture containing the animation data.</param>
        /// <param name="animations">The animations in the animation texture.</param>
        public InstancedAnimationSet(Texture2D texture, InstancedAnimation[] animations)
        {
            if (texture == null)
            {
                throw new ArgumentNullException(nameof(texture));
            }
            if (animations == null)
            {
                throw new ArgumentNullException(nameof(animations));
            }

            m_texture = texture;
            m_animations = animations;
        }
    }

    /// <summary>
    /// An asset that stores animated content.
    /// </summary>
    [CreateAssetMenu(fileName = "New InstancedAnimation", menuName = "Instanced Animation/Animation", order = 410)]
    public class InstancedAnimationAsset : ScriptableObject
    {
        [SerializeField]
        [Tooltip("The animation set.")]
        InstancedAnimationSet m_animationSet;

        [SerializeField]
        [Tooltip("The meshes that can be used when playing the animations.")]
        InstancedMesh[] m_meshes;

        /// <summary>
        /// The animation set.
        /// </summary>
        public InstancedAnimationSet AnimationSet => m_animationSet;

        /// <summary>
        /// The meshes that can be used when playing the animation.
        /// </summary>
        public InstancedMesh[] Meshes => m_meshes;

        /// <summary>
        /// Creates a <see cref="InstancedAnimationAsset"/> instance.
        /// </summary>
        /// <param name="animationSet">The animation set.</param>
        /// <param name="meshes">The meshes that can be used when playing the animations.</param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="animationSet"/> or <paramref name="meshes"/> is null.</exception>
        public static InstancedAnimationAsset Create(InstancedAnimationSet animationSet, InstancedMesh[] meshes)
        {
            if (animationSet == null)
            {
                throw new ArgumentNullException(nameof(animationSet));
            }
            if (meshes == null)
            {
                throw new ArgumentNullException(nameof(meshes));
            }

            var asset = CreateInstance<InstancedAnimationAsset>();
            asset.m_animationSet = animationSet;
            asset.m_meshes = meshes;
            return asset;
        }
    }
}

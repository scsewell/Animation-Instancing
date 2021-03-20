using System.Collections.Generic;

using UnityEditor;

using UnityEngine;

namespace AnimationInstancing
{
    public enum VertexCompression
    {
        Off,
        Low,
        High,
    }

    public struct BakeConfig
    {
        public Animator animator;
        public LODGroup lod;
        public AnimationClip[] animations;
        public Dictionary<AnimationClip, float> frameRates;
        public SkinnedMeshRenderer[] renderers;
        public VertexCompression vertexMode;
        public Dictionary<Material, Material> materialRemap;
    }

    public partial class Baker
    {
        readonly List<InstancedAnimation> m_animations = new List<InstancedAnimation>();
        readonly BakeConfig m_config;

        readonly List<InstancedMesh> m_meshes = new List<InstancedMesh>();
        Texture2D m_animationTexture;
        Matrix4x4[] m_bindPoses;

        Transform[] m_bones;
        Dictionary<SkinnedMeshRenderer, Dictionary<int, int>> m_renderIndexMaps;

        /// <summary>
        ///     Creates a baker instance.
        /// </summary>
        /// <param name="config">The description of the data to bake.</param>
        public Baker(BakeConfig config)
        {
            m_config = config;
        }

        /// <summary>
        ///     Bakes the animations.
        /// </summary>
        /// <param name="assetPath">
        ///     The path starting from and including the assets folder
        ///     under which to save the animation data.
        /// </param>
        /// <returns>True if the operation was cancelled.</returns>
        public bool Bake(string assetPath)
        {
            PrepareBones();

            if (BakeMeshes())
            {
                return true;
            }

            if (BakeAnimations())
            {
                return true;
            }

            SaveBake(assetPath);
            return false;
        }

        void SaveBake(string assetPath)
        {
            try
            {
                // create the asset
                EditorUtility.DisplayProgressBar("Creating Asset", string.Empty, 1f);

                var asset = InstancedAnimationAsset.Create(
                    new InstancedAnimationSet(m_animationTexture, m_animations.ToArray()),
                    m_meshes.ToArray()
                );

                // Save the generated asset and meshes. The asset file extention is special and is recognized by unity.
                var uniquePath = AssetDatabase.GenerateUniqueAssetPath($"{assetPath}/{m_config.animator.name}.asset");
                AssetDatabase.CreateAsset(asset, uniquePath);

                foreach (var mesh in m_meshes)
                {
                    AssetDatabase.AddObjectToAsset(mesh.Mesh, asset);
                }

                AssetDatabase.AddObjectToAsset(m_animationTexture, asset);

                AssetDatabase.SaveAssets();

                // focus the new asset in the project window
                ProjectWindowUtil.ShowCreatedAsset(asset);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        void PrepareBones()
        {
            var renderers = m_config.renderers;

            // We must find all unique bones used by any renderers, as well as the bind pose
            // for each of those bones.
            var bones = new List<Transform>();
            var bindPoses = new List<Matrix4x4>();

            // Since each renderer might only use a subset of the bones, we must be able to map
            // from indices into the renderer bone list to indices into the combined bone list.
            m_renderIndexMaps = new Dictionary<SkinnedMeshRenderer, Dictionary<int, int>>();

            foreach (var renderer in renderers)
            {
                var boneIndexToCombinedIndex = new Dictionary<int, int>();
                var rendererBones = renderer.bones;
                var rendererBindPoses = renderer.sharedMesh.bindposes;

                for (var i = 0; i < rendererBones.Length; i++)
                {
                    var bone = rendererBones[i];

                    if (!bones.Contains(bone))
                    {
                        bones.Add(bone);
                        bindPoses.Add(rendererBindPoses[i]);
                    }

                    boneIndexToCombinedIndex.Add(i, bones.IndexOf(bone));
                }

                m_renderIndexMaps.Add(renderer, boneIndexToCombinedIndex);
            }

            m_bones = bones.ToArray();
            m_bindPoses = bindPoses.ToArray();
        }
    }
}

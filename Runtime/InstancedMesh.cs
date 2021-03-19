using System;

using UnityEngine;

namespace AnimationInstancing
{
    /// <summary>
    /// A struct describing the lod properties for a sub mesh.
    /// </summary>
    [Serializable]
    public struct LodInfo
    {
        [SerializeField]
        [Tooltip("The screen relative height of the mesh above which the lod is active.")]
        [Range(0f, 1f)]
        float m_screenHeight;

        /// <summary>
        /// Creates a new <see cref="LodInfo"/> instance.
        /// </summary>
        /// <param name="screenSize">
        /// The screen relative height of the mesh above which the lod is active.
        /// Must be in the range [0,1].
        /// </param>
        public LodInfo(float screenSize)
        {
            if (screenSize < 0f || 1f < screenSize)
            {
                throw new ArgumentOutOfRangeException(nameof(screenSize), screenSize, "Must be in range [0,1]!");
            }

            m_screenHeight = screenSize;
        }

        /// <summary>
        /// The screen relative height of the mesh above which the lod is active, in the range [0,1].
        /// </summary>
        public float ScreenHeight => m_screenHeight;
    }

    /// <summary>
    /// A struct containing a mesh prepared for instancing.
    /// </summary>
    [Serializable]
    public struct InstancedMesh
    {
        [SerializeField]
        [Tooltip("The mesh to instance, with lods packed into the sub meshes.")]
        Mesh m_mesh;

        [SerializeField]
        [Tooltip("The number of sub meshes, excluding the lods.")]
        int m_subMeshCount;

        [SerializeField]
        [Tooltip("The lod levels of the mesh.")]
        LodData m_lods;

        /// <summary>
        /// Creates a new <see cref="InstancedMesh"/> instance.
        /// </summary>
        /// <param name="mesh">
        /// The mesh to instance, with the lods for each sub mesh also stored as sub meshes. The lods
        /// for a sub mesh are stored in sequential sub meshes, followed by other sub meshes and their lods.
        /// </param>
        /// <param name="subMeshCount">The number of sub meshes, excluding any lods.</param>
        /// <param name="lods">The lod levels of the mesh ordered by decreasing detail. If null or empty no lods will be used.</param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="mesh"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="subMeshCount"/> is less than zero.</exception>
        /// <exception cref="ArgumentException">
        /// Thrown if the number of sub meshes in the mesh is not a multiple of <paramref name="subMeshCount"/> and the number of
        /// lods in <paramref name="lods"/>.
        /// </exception>
        public unsafe InstancedMesh(Mesh mesh, int subMeshCount, LodInfo[] lods)
        {
            if (mesh == null)
            {
                throw new ArgumentNullException(nameof(mesh));
            }
            if (subMeshCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(subMeshCount), subMeshCount, "Cannot be negative!");
            }
            if (lods != null && lods.Length * subMeshCount != mesh.subMeshCount)
            {
                throw new ArgumentException(
                    nameof(lods),
                    $"The number of sub meshes in mesh \"{mesh.name}\" ({mesh.subMeshCount}) must be the multiple of the sub mesh count ({subMeshCount}) and LODs ({lods.Length})."
                );
            }

            var lod = new LodData
            {
                lodCount = (uint)(lods != null ? Mathf.Min(lods.Length, Constants.k_MaxLodCount) : 0),
            };
            for (var i = 0; i < lod.lodCount; i++)
            {
                lod.screenHeights[i] = lods[i].ScreenHeight;
            }

            m_mesh = mesh;
            m_subMeshCount = subMeshCount;
            m_lods = lod;
        }

        /// <summary>
        /// The mesh to instance, with lods packed into the sub meshes.
        /// </summary>
        internal Mesh Mesh => m_mesh;

        /// <summary>
        /// The number of sub meshes, excluding the lods.
        /// </summary>
        internal int SubMeshCount => m_subMeshCount;

        /// <summary>
        /// The lod levels of the mesh.
        /// </summary>
        internal LodData Lods => m_lods;
    }
}

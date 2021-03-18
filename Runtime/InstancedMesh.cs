using System;

using UnityEngine;

namespace InstancedAnimation
{
    /// <summary>
    /// A struct describing the lod for an instanced mesh.
    /// </summary>
    [Serializable]
    public struct LodInfo
    {
        [SerializeField]
        [Tooltip("The index of the lod sub mesh in the mesh.")]
        int m_subMesh;

        [SerializeField]
        [Tooltip("The screen relative height under which this lod is used.")]
        [Range(0f, 1f)]
        float m_screenHeight;

        /// <summary>
        /// The index of the lod sub mesh in the mesh.
        /// </summary>
        public int SubMesh => m_subMesh;

        /// <summary>
        /// The screen relative height above which this lod is used, in the range [0,1].
        /// </summary>
        public float ScreenHeight => m_screenHeight;

        /// <summary>
        /// Creates a new <see cref="LodInfo"/> instance.
        /// </summary>
        /// <param name="subMesh">The index of the lod sub mesh in the mesh.</param>
        /// <param name="screenSize">The screen height above which this lod is used. Must be in the range [0,1].</param>
        public LodInfo(int subMesh, float screenSize)
        {
            if (subMesh < -1)
            {
                throw new ArgumentOutOfRangeException(nameof(subMesh), subMesh, "Must be -1 or greater!");
            }
            if (screenSize < 0f || 1f < screenSize)
            {
                throw new ArgumentOutOfRangeException(nameof(screenSize), screenSize, "Must be in range [0,1]!");
            }

            m_subMesh = subMesh;
            m_screenHeight = screenSize;
        }
    }

    /// <summary>
    /// A struct containing an instanceable animated mesh.
    /// </summary>
    [Serializable]
    public struct InstancedMesh
    {
        [SerializeField]
        [Tooltip("The mesh to instance, with lods packed into the submeshes.")]
        Mesh m_mesh;

        [SerializeField]
        [Tooltip("The lods to use when rendering the mesh.")]
        LodInfo[] m_lods;

        /// <summary>
        /// The mesh to instance, with lods packed into the submeshes.
        /// </summary>
        public Mesh Mesh => m_mesh;

        /// <summary>
        /// The lods to use when rendering the mesh.
        /// </summary>
        public LodInfo[] Lods => m_lods;

        /// <summary>
        /// Creates a new <see cref="InstancedMesh"/> instance.
        /// </summary>
        /// <param name="mesh">The mesh to instance, with lods packed into the sub meshes.</param>
        /// <param name="lods">The lods to use when rendering the mesh.</param>
        public InstancedMesh(Mesh mesh, LodInfo[] lods)
        {
            if (mesh == null)
            {
                throw new ArgumentNullException(nameof(mesh));
            }
            if (lods == null)
            {
                throw new ArgumentNullException(nameof(lods));
            }
            if (lods.Length != mesh.subMeshCount)
            {
                throw new ArgumentException(nameof(lods), $"{lods.Length} lods are specified but mesh has {mesh.subMeshCount} sub meshes.");
            }

            m_mesh = mesh;
            m_lods = lods;
        }
    }
}

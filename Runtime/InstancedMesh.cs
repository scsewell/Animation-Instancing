using System;

using UnityEngine;

namespace AnimationInstancing
{
    /// <summary>
    /// A struct describing the lod properties for a sub mesh.
    /// </summary>
    public readonly struct LodInfo
    {
        /// <summary>
        /// The screen relative height of the mesh above which the lod is active, in the range [0,1].
        /// </summary>
        public float ScreenHeight { get; }

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

            ScreenHeight = screenSize;
        }
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
        [Tooltip("The number of sub meshes in the mesh, excluding the lods.")]
        int m_subMeshCount;

        [SerializeField]
        [Tooltip("The lod levels of the mesh.")]
        LodData m_lods;

        /// <summary>
        /// The mesh to instance, with lods packed into the sub meshes.
        /// </summary>
        internal Mesh Mesh => m_mesh;

        /// <summary>
        /// The number of sub meshes in the mesh, excluding the lods.
        /// </summary>
        internal int SubMeshCount => m_subMeshCount;

        /// <summary>
        /// The lod levels of the mesh.
        /// </summary>
        internal LodData Lods => m_lods;

        /// <summary>
        /// Creates a new <see cref="InstancedMesh"/> instance.
        /// </summary>
        /// <param name="mesh">
        /// The mesh to instance, with the lods packed in as sub meshes. For meshes with multiple sub meshes and lods,
        /// the sub meshes for LOD0 are stored first, then all the sub meshes for LOD1 and so on.
        /// </param>
        /// <param name="subMeshCount">The number of sub meshes in the mesh, excluding any lods.</param>
        /// <param name="lods">The lod levels of the mesh ordered by decreasing detail. If null or empty no lods will be used.</param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="mesh"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="subMeshCount"/> is less than zero.</exception>
        /// <exception cref="ArgumentException">
        /// Thrown if the number of sub meshes in the mesh is not a multiple of <paramref name="subMeshCount"/> and the number of
        /// lods in <paramref name="lods"/>.
        /// </exception>
        public InstancedMesh(Mesh mesh, int subMeshCount, LodInfo[] lods)
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

            m_mesh = mesh;
            m_subMeshCount = subMeshCount;
            m_lods = new LodData(lods);
        }
    }
}

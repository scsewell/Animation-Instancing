using System;

using UnityEngine;

namespace AnimationInstancing
{
    /// <summary>
    /// Flags used to indicate how a set of instances has changed since they were last rendered.
    /// </summary>
    [Flags]
    public enum DirtyFlags
    {
        /// <summary>
        /// The instances have not changed.
        /// </summary>
        None = 0,

        /// <summary>
        /// The number of instances has changed.
        /// </summary>
        InstanceCount = 1 << 0,

        /// <summary>
        /// The data of at least one instance has changed.
        /// </summary>
        PerInstanceData = 1 << 1,

        /// <summary>
        /// The mesh to use when rendering has been changed.
        /// </summary>
        Mesh = 1 << 10,

        /// <summary>
        /// The sub meshes to use when rendering have been changed.
        /// </summary>
        SubMeshes = 1 << 11,

        /// <summary>
        /// The material instance assigned to a sub mesh has changed.
        /// </summary>
        Materials = 1 << 12,

        /// <summary>
        /// The level of detail configuration of the mesh has been changed.
        /// </summary>
        Lods = 1 << 16,
        
        /// <summary>
        /// The animation used by the instances has been changed.
        /// </summary>
        Animation = 1 << 20,
    }

    /// <summary>
    /// An interface used to provide instance data to the <see cref="InstancingManager"/>.
    /// Use <see cref="InstancingManager.RegisterInstanceProvider"/> to make the renderer
    /// aware of the instances managed by this provider.
    /// </summary>
    public interface IInstanceProvider
    {
        /// <summary>
        /// The number of instances to render.
        /// </summary>
        int InstanceCount { get; }

        /// <summary>
        /// The mesh to render these instances with.
        /// </summary>
        InstancedMesh Mesh { get; }

        /// <summary>
        /// The animation set used for the instances.
        /// </summary>
        InstancedAnimationSet AnimationSet { get; }

        /// <summary>
        /// The flags indicating if the instance data has changed.
        /// </summary>
        DirtyFlags DirtyFlags { get; }

        /// <summary>
        /// Gets the number of draw calls used when rendering these instances.
        /// </summary>
        /// <returns>The number of draw calls.</returns>
        int GetDrawCallCount();

        /// <summary>
        /// Gets the configuration of a draw call used for these instances.
        /// </summary>
        /// <param name="drawCall">The index of the draw call.</param>
        /// <param name="subMesh">The index of the sub mesh to draw in the mesh.</param>
        /// <param name="material">The material to use when drawing the sub mesh.</param>
        /// <returns>True if the draw call index is valid for this provider.</returns>
        bool TryGetDrawCall(int drawCall, out int subMesh, out Material material);

        /// <summary>
        /// Resets the dirty flags so the instance renderer will not update the instances from this
        /// provider the next time it renders.
        /// </summary>
        /// <remarks>
        /// This is called by <see cref="InstancingManager"/> once it has refreshed any data marked
        /// as dirty by the <see cref="DirtyFlags"/>.
        /// </remarks>
        void ClearDirtyFlags();
    }
}

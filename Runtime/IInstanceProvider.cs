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
        /// The animation used by the instances has been changed.
        /// </summary>
        Animation = 1 << 5,

        /// <summary>
        /// The mesh used by the instances has been changed.
        /// </summary>
        Mesh = 1 << 10,

        /// <summary>
        /// The material used by the instances has changed.
        /// </summary>
        Material = 1 << 15,
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
        /// The mesh to render the instances with.
        /// </summary>
        InstancedMesh Mesh { get; }

        /// <summary>
        /// The material to render the instances with.
        /// </summary>
        Material Material { get; }

        /// <summary>
        /// The animation set used for the instances.
        /// </summary>
        InstancedAnimationSet AnimationSet { get; }

        /// <summary>
        /// The flags indicating if the instance data has changed.
        /// </summary>
        DirtyFlags DirtyFlags { get; }

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

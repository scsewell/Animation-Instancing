using System;

using Unity.Collections;
using Unity.Mathematics;

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
        
        /// <summary>
        /// Force a complete refresh of all instance data.
        /// </summary>
        All = ~0,
    }

    /// <summary>
    /// A struct that stores the transform of a single instance.
    /// </summary>
    public struct InstanceTransform
    {
        /// <summary>
        /// The world space position of the instance.
        /// </summary>
        public float3 position;
        
        /// <summary>
        /// The world space rotation of the instance. 
        /// </summary>
        public quaternion rotation;
        
        /// <summary>
        /// The world space scale of the instance. 
        /// </summary>
        public float3 scale;
    }
    
    /// <summary>
    /// A struct that stores the data of a single instance.
    /// </summary>
    public struct Instance
    {
        /// <summary>
        /// The instance transform.
        /// </summary>
        public InstanceTransform transform;

        public int animationIndex;
        public float animationTime;
    }

    /// <summary>
    /// A struct that stores the data about a sub mesh to draw.
    /// </summary>
    public struct SubMesh
    {
        /// <summary>
        /// The index of the sub mesh to draw in the mesh.
        /// </summary>
        public int subMeshIndex;
        
        /// <summary>
        /// The material to use when drawing the sub mesh.
        /// </summary>
        public MaterialHandle materialHandle;
    }

    /// <summary>
    /// A struct that stores the data used to render a set of instances.
    /// </summary>
    public struct InstanceProviderState
    {
        /// <summary>
        /// The mesh to render the instances with.
        /// </summary>
        public MeshHandle mesh;
        
        /// <summary>
        /// The sub meshes of the mesh to draw.
        /// </summary>
        public NativeSlice<SubMesh> subMeshes;

        /// <summary>
        /// The levels of detail to render the instances with.
        /// </summary>
        public LodData lods;

        /// <summary>
        /// The animation set used for the instances.
        /// </summary>
        public AnimationSetHandle animationSet;

        /// <summary>
        /// The instance data.
        /// </summary>
        public NativeSlice<Instance> instances;
    }

    /// <summary>
    /// An interface used to provide instance data to the <see cref="InstancingManager"/>.
    /// Use <see cref="InstancingManager.RegisterInstanceProvider"/> to make the renderer
    /// aware of the instances managed by this provider.
    /// </summary>
    public interface IInstanceProvider
    {
        /// <summary>
        /// The flags indicating if the instance data has changed.
        /// </summary>
        DirtyFlags DirtyFlags { get; }

        /// <summary>
        /// Gets the data used to render the instances.
        /// </summary>
        /// <param name="state">Returns the current instance state.</param>
        void GetState(out InstanceProviderState state);
        
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

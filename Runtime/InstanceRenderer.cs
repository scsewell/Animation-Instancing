using Unity.Collections;
using Unity.Jobs;
using Unity.Burst;
using Unity.Mathematics;

using UnityEngine;

namespace AnimationInstancing
{
    /// <summary>
    /// A component that can be used to render animated instances.
    /// </summary>
    public class InstanceRenderer : MonoBehaviour, IInstanceProvider
    {
        [SerializeField]
        InstancedAnimationAsset m_animationAsset = null;
        [SerializeField]
        Material[] m_materials = null;
        [SerializeField]
        int m_instanceCount = 100;

        Handle<Mesh> m_meshHandle;
        Handle<AnimationSet> m_animationSetHandle;
        NativeArray<Handle<Material>> m_materialHandles;
        NativeArray<SubMesh> m_subMeshes;
        NativeArray<Instance> m_instances;
        NativeArray<float> m_animationLengths;
        bool m_reinitialize;
        
        /// <inheritdoc />
        public DirtyFlags DirtyFlags { get; private set; } = DirtyFlags.All;

        /// <summary>
        /// The number of instances to render.
        /// </summary>
        public int InstanceCount
        {
            get => m_instanceCount;
            set
            {
                var instanceCount = Mathf.Max(value, 0);
                
                if (m_instanceCount != instanceCount)
                {
                    m_instanceCount = instanceCount;
                    EnsureInstanceBufferCapacity(m_instanceCount);
                    DirtyFlags |= DirtyFlags.InstanceCount;
                }   
            }
        }

        void OnValidate()
        {
            m_reinitialize = true;
        }
        
        void OnEnable()
        {
            InstancingManager.RegisterInstanceProvider(this);

            Init();
        }

        void OnDisable()
        {
            InstancingManager.DeregisterInstanceProvider(this);
            
            Deinit();
        }

        void Init()
        {
            var mesh = m_animationAsset.Meshes[0];
            
            m_meshHandle = InstancingManager.RegisterMesh(mesh.Mesh);
            m_animationSetHandle = InstancingManager.RegisterAnimationSet(m_animationAsset.AnimationSet);
         
            m_materialHandles = new NativeArray<Handle<Material>>(m_materials.Length, Allocator.Persistent);
            for (var i = 0; i < m_materialHandles.Length; i++)
            {
                m_materialHandles[i] = InstancingManager.RegisterMaterial(m_materials[i]);
            }
            
            m_subMeshes = new NativeArray<SubMesh>(mesh.SubMeshCount, Allocator.Persistent);
            for (var i = 0; i < m_subMeshes.Length; i++)
            {
                m_subMeshes[i] = new SubMesh
                {
                    materialHandle = m_materialHandles[Mathf.Min(i, m_materialHandles.Length - 1)],
                    subMeshIndex = i,
                };
            }

            EnsureInstanceBufferCapacity(m_instanceCount);

            m_animationLengths = new NativeArray<float>(m_animationAsset.AnimationSet.Animations.Length, Allocator.Persistent);
            for (var i = 0; i < m_animationLengths.Length; i++)
            {
                m_animationLengths[i] =  m_animationAsset.AnimationSet.Animations[i].Length;
            }

            DirtyFlags = DirtyFlags.All;
        }

        void Deinit()
        {
            InstancingManager.DeregisterMesh(m_meshHandle);
            InstancingManager.DeregisterAnimationSet(m_animationSetHandle);

            m_meshHandle = default;
            m_animationSetHandle = default;
            
            for (var i = 0; i < m_materialHandles.Length; i++)
            {
                InstancingManager.DeregisterMaterial(m_materialHandles[i]);
            }
            
            DisposeUtils.Dispose(ref m_materialHandles);
            DisposeUtils.Dispose(ref m_subMeshes);
            DisposeUtils.Dispose(ref m_instances);
            DisposeUtils.Dispose(ref m_animationLengths);
        }

        void LateUpdate()
        {
            if (m_reinitialize)
            {
                Deinit();
                Init();

                m_reinitialize = false;
            }
            
            var configureInstancesJob = new ConfigureInstancesJob
            {
                animationLengths = m_animationLengths,
                instanceCount = m_instanceCount,
                basePosition = transform.position,
                time = Time.time,
                instances = m_instances,
            };
            
            var jobHandle = configureInstancesJob.Schedule(m_instanceCount, 64);
            jobHandle.Complete();
            
            DirtyFlags |= DirtyFlags.PerInstanceData;
        }
        
        /// <inheritdoc />
        public void GetState(out RenderState state, out NativeSlice<SubMesh> subMeshes, out NativeSlice<Instance> instances)
        {
            state = new RenderState
            {
                mesh = m_meshHandle,
                lods = m_animationAsset.Meshes[0].Lods,
                animationSet = m_animationSetHandle,
            };
            subMeshes = m_subMeshes;
            instances = m_instances.Slice(0, m_instanceCount);
        }

        /// <inheritdoc />
        public void ClearDirtyFlags()
        {
            DirtyFlags = DirtyFlags.None;
        }
        
        void EnsureInstanceBufferCapacity(int capacity)
        {
            if (!m_instances.IsCreated || m_instances.Length < capacity)
            {
                DisposeUtils.Dispose(ref m_instances);
            
                m_instances = new NativeArray<Instance>(
                    capacity,
                    Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory
                );
            }
        }

        [BurstCompile(DisableSafetyChecks = true)]
        struct ConfigureInstancesJob : IJobParallelFor
        {
            [ReadOnly, NoAlias]
            public NativeArray<float> animationLengths;

            public int instanceCount;
            public float3 basePosition;
            public float time;

            [WriteOnly, NoAlias]
            public NativeArray<Instance> instances;
            
            public void Execute(int i)
            {
                var edgeLength = Mathf.CeilToInt(Mathf.Sqrt(instanceCount));
                var pos = new float3(-Mathf.Repeat(i, edgeLength), 0, -Mathf.Floor(i / edgeLength));
                var rot = Quaternion.Euler(0, i + time * 10, 0);
                var animationIndex = i % animationLengths.Length;
                
                instances[i] = new Instance
                {
                    transform = new CompressedTransform
                    {
                        position = basePosition + pos,
                        rotation = new CompressedQuaternion(rot),
                        scale = 1f,
                    },
                    animationIndex = animationIndex,
                    animationTime = Mathf.Repeat((time + math.length(pos)) / animationLengths[animationIndex], 1f),
                };
            }
        }
    }
}

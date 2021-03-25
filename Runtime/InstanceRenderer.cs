using System;

using Unity.Collections;
using Unity.Jobs;
using Unity.Burst;
using Unity.Mathematics;

using UnityEngine;

namespace AnimationInstancing
{
    public class InstanceRenderer : MonoBehaviour, IInstanceProvider
    {
        [SerializeField]
        InstancedAnimationAsset m_animationAsset = null;
        [SerializeField]
        Material m_material = null;
        [SerializeField]
        int m_instanceCount = 100;

        MeshHandle m_meshHandle;
        MaterialHandle m_materialHandle;
        AnimationSetHandle m_animationSetHandle;
        NativeArray<Instance> m_instances;
        NativeArray<SubMesh> m_subMeshes;
        NativeArray<float> m_animationLengths;
        
        /// <inheritdoc />
        public DirtyFlags DirtyFlags { get; private set; } = DirtyFlags.All;

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
            m_meshHandle = InstancingManager.RegisterMesh(m_animationAsset.Meshes[0].Mesh);
            m_materialHandle = InstancingManager.RegisterMaterial(m_material);
            m_animationSetHandle = InstancingManager.RegisterAnimationSet(m_animationAsset.AnimationSet);
            
            m_instances = new NativeArray<Instance>(m_instanceCount, Allocator.Persistent);
            
            m_subMeshes = new NativeArray<SubMesh>(1, Allocator.Persistent);
            m_subMeshes[0] = new SubMesh
            {
                materialHandle = m_materialHandle,
                subMeshIndex = 0,
            };
            
            m_animationLengths = new NativeArray<float>(m_animationAsset.AnimationSet.Animations.Length, Allocator.Persistent);
            for (var i = 0; i < m_animationLengths.Length; i++)
            {
                m_animationLengths[i] =  m_animationAsset.AnimationSet.Animations[i].Length;
            }

            DirtyFlags = DirtyFlags.All;
        }

        void Deinit()
        {
            Dispose(ref m_instances);
            Dispose(ref m_subMeshes);
            Dispose(ref m_animationLengths);
         
            InstancingManager.DeregisterMesh(m_meshHandle);
            InstancingManager.DeregisterMaterial(m_materialHandle);
            InstancingManager.DeregisterAnimationSet(m_animationSetHandle);
            
            m_meshHandle = default;
            m_materialHandle = default;
            m_animationSetHandle = default;
        }

        void Dispose<T>(ref NativeArray<T> array) where T : struct
        {
            if (array.IsCreated)
            {
                array.Dispose();
                array = default;
            }
        }

        void Update()
        {
            var configureInstancesJob = new ConfigureInstancesJob
            {
                animationLengths = m_animationLengths,
                basePosition = transform.position,
                time = Time.time,
                instances = m_instances,
            };
            
            var handle = configureInstancesJob.Schedule(m_instances.Length, 64);
            handle.Complete();

            DirtyFlags |= DirtyFlags.PerInstanceData;
        }
        
        [BurstCompile(DisableSafetyChecks = true)]
        struct ConfigureInstancesJob : IJobParallelFor
        {
            [ReadOnly, NoAlias]
            public NativeArray<float> animationLengths;
            
            public float3 basePosition;
            public float time;

            [WriteOnly, NoAlias]
            public NativeArray<Instance> instances;
            
            public void Execute(int i)
            {
                var edgeLength = Mathf.CeilToInt(Mathf.Sqrt(instances.Length));
                var pos = new float3(-Mathf.Repeat(i, edgeLength), 0, -Mathf.Floor(i / edgeLength));
                var animationIndex = i % animationLengths.Length;
                
                instances[i] = new Instance
                {
                    transform = new InstanceTransform
                    {
                        position = basePosition + pos,
                        rotation = quaternion.identity,
                        scale = Vector3.one,
                    },
                    animationIndex = animationIndex,
                    animationTime = Mathf.Repeat((time + math.length(pos)) / animationLengths[animationIndex], 1f),
                };
            }
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
            instances = m_instances;
        }

        /// <inheritdoc />
        public void ClearDirtyFlags()
        {
            DirtyFlags = DirtyFlags.None;
        }
    }
}

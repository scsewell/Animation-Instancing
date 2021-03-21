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

        NativeArray<Instance> m_instances;
        
        /// <inheritdoc />
        public int InstanceCount => m_instanceCount;

        /// <inheritdoc />
        public InstancedMesh Mesh => m_animationAsset.Meshes[0];
        
        /// <inheritdoc />
        public InstancedAnimationSet AnimationSet => m_animationAsset.AnimationSet;

        /// <inheritdoc />
        public DirtyFlags DirtyFlags { get; private set; } = DirtyFlags.All;

        void OnEnable()
        {
            InstancingManager.RegisterInstanceProvider(this);

            m_instances = new NativeArray<Instance>(m_instanceCount, Allocator.Persistent);
        }

        void OnDisable()
        {
            InstancingManager.DeregisterInstanceProvider(this);
            
            if (m_instances.IsCreated)
            {
                m_instances.Dispose();
                m_instances = default;
            }
        }

        void OnValidate()
        {
            if (m_instances.IsCreated)
            {
                m_instances.Dispose();
                m_instances = default;
            }
            
            m_instances = new NativeArray<Instance>(m_instanceCount, Allocator.Persistent);

            DirtyFlags = DirtyFlags.All;
        }

        void Update()
        {
            var animations = new NativeArray<float>(m_animationAsset.AnimationSet.Animations.Length, Allocator.TempJob);

            for (var i = 0; i < animations.Length; i++)
            {
                animations[i] =  m_animationAsset.AnimationSet.Animations[i].Length;
            }

            var configureInstancesJob = new ConfigureInstancesJob
            {
                animationLengths = animations,
                basePosition = transform.position,
                instanceCount = m_instanceCount,
                time = Time.time,
                instances = m_instances,
            };
            
            var handle = configureInstancesJob.Schedule(m_instanceCount, 64);
            handle.Complete();
        }
        
        [BurstCompile]
        struct ConfigureInstancesJob : IJobParallelFor
        {
            [ReadOnly, NoAlias, DeallocateOnJobCompletion]
            public NativeArray<float> animationLengths;
            
            public float3 basePosition;
            public int instanceCount;
            public float time;

            [WriteOnly, NoAlias]
            public NativeArray<Instance> instances;
            
            public void Execute(int i)
            {
                var edgeLength = Mathf.CeilToInt(Mathf.Sqrt(instanceCount));
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
        public int GetDrawCallCount()
        {
            return 1;
        }

        /// <inheritdoc />
        public bool TryGetDrawCall(int drawCall, out int subMesh, out Material material)
        {
            if (drawCall < 0 || 1 < drawCall)
            {
                subMesh = 0;
                material = null;
                return false;
            }
            
            subMesh = 0;
            material = m_material;
            return true;
        }

        /// <inheritdoc />
        public void GetInstances(out NativeSlice<Instance> instances)
        {
            instances = m_instances;
        }

        /// <inheritdoc />
        public void ClearDirtyFlags()
        {
            DirtyFlags = DirtyFlags.None;
        }
    }
}

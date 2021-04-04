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
        Material[] m_materials = null;
        [SerializeField]
        int m_instanceCount = 100;

        MeshHandle m_meshHandle;
        AnimationSetHandle m_animationSetHandle;
        NativeArray<MaterialHandle> m_materialHandles;
        NativeArray<SubMesh> m_subMeshes;
        NativeArray<Instance> m_instances;
        NativeArray<float> m_animationLengths;
        JobHandle m_configureInstancesJob;
        
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
            var mesh = m_animationAsset.Meshes[0];
            
            m_meshHandle = InstancingManager.RegisterMesh(mesh.Mesh);
            m_animationSetHandle = InstancingManager.RegisterAnimationSet(m_animationAsset.AnimationSet);
         
            m_materialHandles = new NativeArray<MaterialHandle>(m_materials.Length, Allocator.Persistent);
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
            
            m_instances = new NativeArray<Instance>(m_instanceCount, Allocator.Persistent);

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
            
            Dispose(ref m_materialHandles);
            Dispose(ref m_subMeshes);
            Dispose(ref m_instances);
            Dispose(ref m_animationLengths);
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
            
            m_configureInstancesJob = configureInstancesJob.Schedule(m_instances.Length, 64);
            
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
            m_configureInstancesJob.Complete();
            m_configureInstancesJob = default;

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

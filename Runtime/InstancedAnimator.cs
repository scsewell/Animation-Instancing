using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using Unity.Collections;
using Unity.Jobs;
using Unity.Burst;

using UnityEngine;
using UnityEngine.Rendering;

namespace InstancedAnimation
{
    public class InstancedAnimator : MonoBehaviour
    {
        static readonly int k_instanceBufferProp = Shader.PropertyToID("_InstanceProperties");
        static readonly int k_animationTextureProp = Shader.PropertyToID("_Animation");
        static readonly int k_animationRegionsBufferProp = Shader.PropertyToID("_AnimationRegions");

        struct MeshData
        {
            public Mesh mesh;
            public int subMeshIndex;
            public Material material;
            public MaterialPropertyBlock propertyBlock;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct InstanceProperties
        {
            public static readonly int k_size = Marshal.SizeOf<InstanceProperties>();

            public Matrix4x4 model;
            public Matrix4x4 modelInv;
            public uint animation;
            public float time;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct SubMeshArgs
        {
            public static readonly int k_size = Marshal.SizeOf<SubMeshArgs>();

            public uint indexCount;
            public uint instanceCount;
            public uint indexStart;
            public uint baseVertex;
            public uint instanceStart;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct AnimationRegion
        {
            public static readonly int k_size = Marshal.SizeOf<AnimationRegion>();

            public Vector2 min;
            public Vector2 max;
        }

        [SerializeField]
        [Tooltip("The animation asset containing the animated content to play.")]
        InstancedAnimationAsset m_animationAsset = null;
        [SerializeField]
        Material m_material = null;

        int m_instanceCount;
        NativeArray<InstanceProperties> m_instanceData;
        ComputeBuffer m_instanceBuffer;

        bool m_buffersCreated;
        MeshData[] m_subMeshes;
        NativeArray<SubMeshArgs> m_argsData;
        ComputeBuffer m_argsBuffer;
        ComputeBuffer m_animationRegionsBuffer;

        /// <summary>
        /// The animation asset containing the animated content to play.
        /// </summary>
        public InstancedAnimationAsset AnimationAsset
        {
            get => m_animationAsset;
            set
            {
                if (m_animationAsset != value)
                {
                    m_animationAsset = value;
                    CreateOrUpdateBuffersIfNeeded();
                    SetInstanceCount(m_instanceCount);
                }
            }
        }

        /// <summary>
        /// The number of animated instances.
        /// </summary>
        public int InstanceCount
        {
            get => m_instanceCount;
            set
            {
                if (value < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), value, "Must be non-negative!");
                }
                if (m_instanceCount != value)
                {
                    m_instanceCount = value;
                    SetInstanceCount(m_instanceCount);
                }
            }
        }

        void OnEnable()
        {
            CreateOrUpdateBuffersIfNeeded();
            SetInstanceCount(m_instanceCount);
        }

        void OnDisable()
        {
            DestroyBuffers();
        }

        void LateUpdate()
        {
            if (!IsActive())
            {
                return;
            }

            var animations = new NativeArray<AnimationInfo>(m_animationAsset.Animations.Length, Allocator.TempJob);

            for (var i = 0; i < animations.Length; i++)
            {
                animations[i] = new AnimationInfo
                {
                    length = m_animationAsset.Animations[i].Length,
                };
            }

            var configureInstancesJob = new ConfigureInstancesJob
            {
                instanceCount = m_instanceCount,
                instanceProperties = m_instanceData,
                animations = animations,
                time = Time.time,
            };

            var handle = configureInstancesJob.Schedule(m_instanceCount, 64);
            handle.Complete();

            m_instanceBuffer.SetData(m_instanceData, 0, 0, m_instanceCount);

            for (var i = 0; i < m_subMeshes.Length; i++)
            {
                var subMesh = m_subMeshes[i];

                Graphics.DrawMeshInstancedIndirect(
                    subMesh.mesh,
                    subMesh.subMeshIndex,
                    subMesh.material,
                    new Bounds(Vector3.zero, 1000f * Vector3.one),
                    m_argsBuffer,
                    i * SubMeshArgs.k_size,
                    null,
                    ShadowCastingMode.On,
                    true,
                    gameObject.layer,
                    null,
                    LightProbeUsage.BlendProbes
                );
            }
        }

        struct AnimationInfo
        {
            public float length;
        }

        struct ConfigureInstancesJob : IJobParallelFor
        {
            public NativeArray<InstanceProperties> instanceProperties;
            [ReadOnly, DeallocateOnJobCompletion]
            public NativeArray<AnimationInfo> animations;
            public int instanceCount;
            public float time;

            public void Execute(int i)
            {
                var edgeLength = Mathf.CeilToInt(Mathf.Sqrt(instanceCount));
                var pos = new Vector3(-Mathf.Repeat(i, edgeLength), 0, -Mathf.Floor(i / edgeLength));
                var matrix = Matrix4x4.TRS(pos, Quaternion.identity, Vector3.one);

                var animationIndex = i % animations.Length;
                
                instanceProperties[i] = new InstanceProperties
                {
                    model = matrix,
                    modelInv = matrix.inverse,
                    animation = (uint)animationIndex,
                    time = Mathf.Repeat((time + pos.magnitude) / animations[animationIndex].length, 1f),
                };
            }
        }

        bool IsActive()
        {
            return isActiveAndEnabled && m_animationAsset != null && m_instanceCount > 0;
        }

        void CreateOrUpdateBuffersIfNeeded()
        {
            DestroyBuffers();

            if (!IsActive())
            {
                return;
            }

            var texture = m_animationAsset.Texture;
            var meshes = m_animationAsset.Meshes;
            var animations = m_animationAsset.Animations;

            // create a buffer describing where to read each animation in the animation texture
            var regions = new NativeArray<AnimationRegion>(animations.Length, Allocator.Temp, NativeArrayOptions.UninitializedMemory);

            for (var i = 0; i < regions.Length; i++)
            {
                var region = animations[i].Region;

                regions[i] = new AnimationRegion
                {
                    min = region.min,
                    max = region.max,
                };
            }

            m_animationRegionsBuffer = new ComputeBuffer(animations.Length, AnimationRegion.k_size)
            {
                name = $"{name}AnimationRegions",
            };
            m_animationRegionsBuffer.SetData(regions);

            // Get the submeshes to render
            var subMeshes = new List<MeshData>();

            for (var i = 0; i < meshes.Length; i++)
            {
                var mesh = meshes[i].Mesh;

                if (mesh == null)
                {
                    continue;
                }

                subMeshes.Add(new MeshData
                {
                    mesh = mesh,
                    subMeshIndex = 0,
                    material = m_material,
                    propertyBlock = new MaterialPropertyBlock(),
                });

                //material.SetTexture(k_animationTextureProp, texture);
                //material.SetBuffer(k_animationRegionsBufferProp, m_animationRegionsBuffer);
            }

            m_subMeshes = subMeshes.ToArray();

            // create and initialize the draw args buffer
            m_argsData = new NativeArray<SubMeshArgs>(m_subMeshes.Length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

            for (var i = 0; i < m_subMeshes.Length; i++)
            {
                var mesh = m_subMeshes[i].mesh;
                var subMesh = m_subMeshes[i].subMeshIndex;

                m_argsData[i] = new SubMeshArgs
                {
                    indexCount = mesh.GetIndexCount(subMesh),
                    instanceCount = 0,
                    indexStart = mesh.GetIndexStart(subMesh),
                    baseVertex = mesh.GetBaseVertex(subMesh),
                    instanceStart = 0,
                };
            }

            m_argsBuffer = new ComputeBuffer(m_subMeshes.Length, SubMeshArgs.k_size, ComputeBufferType.IndirectArguments)
            {
                name = $"{name}IndirectArgs",
            };
            m_argsBuffer.SetData(m_argsData);

            m_buffersCreated = true;
        }

        void DestroyBuffers()
        {
            m_buffersCreated = false;

            if (m_instanceData.IsCreated)
            {
                m_instanceData.Dispose();
                m_instanceData = default;
            }
            if (m_instanceBuffer != null)
            {
                m_instanceBuffer.Release();
                m_instanceBuffer = null;
            }

            m_subMeshes = null;
            if (m_argsData.IsCreated)
            {
                m_argsData.Dispose();
                m_argsData = default;
            }
            if (m_argsBuffer != null)
            {
                m_argsBuffer.Release();
                m_argsBuffer = null;
            }
            if (m_animationRegionsBuffer != null)
            {
                m_animationRegionsBuffer.Release();
                m_animationRegionsBuffer = null;
            }
        }

        void SetInstanceCount(int count)
        {
            if (!IsActive())
            {
                return;
            }

            if (!m_buffersCreated)
            {
                CreateOrUpdateBuffersIfNeeded();
            }

            // ensure the instance data buffers are big enough for all the instance data
            if (m_instanceBuffer != null && m_instanceBuffer.count < count)
            {
                m_instanceBuffer.Dispose();
                m_instanceBuffer = null;
            }
            if (m_instanceBuffer == null)
            {
                m_instanceBuffer = new ComputeBuffer(count, InstanceProperties.k_size, ComputeBufferType.Structured)
                {
                    name = $"{name}InstanceData",
                };

                for (var i = 0; i < m_subMeshes.Length; i++)
                {
                    m_subMeshes[i].material.SetBuffer(k_instanceBufferProp, m_instanceBuffer);
                }
            }

            if (!m_instanceData.IsCreated)
            {
                m_instanceData = new NativeArray<InstanceProperties>(count, Allocator.Persistent);
            }
            else if (m_instanceData.Length < count)
            {
                var oldData = m_instanceData;
                
                m_instanceData = new NativeArray<InstanceProperties>(count, Allocator.Persistent);
                NativeArray<InstanceProperties>.Copy(oldData, m_instanceData, oldData.Length);

                oldData.Dispose();
            }

            // set the instance count for all submeshes
            for (var i = 0; i < m_subMeshes.Length; i++)
            {
                var args = m_argsData[i];

                args.instanceCount = (uint)count;

                m_argsData[i] = args;
            }

            m_argsBuffer.SetData(m_argsData);
        }
    }
}

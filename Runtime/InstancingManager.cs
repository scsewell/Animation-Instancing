using System;
using System.Collections.Generic;

using Unity.Jobs;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;
using UnityEngine.Profiling;

namespace AnimationInstancing
{
    /// <summary>
    /// A class that manages the rendering of all animated instances. 
    /// </summary>
    public class InstancingManager
    {
        struct KernelInfo
        {
            public int kernelID;
            public int threadGroupSizeX;
            public int threadGroupSizeY;
            public int threadGroupSizeZ;
        }
        
        struct ProviderState
        {
            public DirtyFlags dirtyFlags;
            public RenderState renderState;
            public NestableNativeSlice<SubMesh> subMeshes;
            public NestableNativeSlice<Instance> instances;
            public int lodIndex;
            public int countBaseIndex;
            public int animationBaseIndex;
        }

        unsafe struct InstanceType : IEquatable<InstanceType>
        {
            public Handle<Mesh> mesh;
            public Handle<AnimationSet> animation;
            public int subMeshCount;
            public fixed int indices[Constants.k_maxSubMeshCount];
            public fixed int materials[Constants.k_maxSubMeshCount];

            public InstanceType(Handle<Mesh> mesh, Handle<AnimationSet> animation, NativeSlice<SubMesh> subMeshes)
            {
                this.mesh = mesh;
                this.animation = animation;
                
                subMeshCount = Mathf.Min(subMeshes.Length, Constants.k_maxSubMeshCount);
                for (var i = 0; i < subMeshCount; i++)
                {
                    var subMesh = subMeshes[i];
                    indices[i] = subMesh.subMeshIndex;
                    materials[i] = subMesh.materialHandle.m_value;
                }
            }
            
            public bool Equals(InstanceType other)
            {
                if (mesh != other.mesh ||
                    animation != other.animation || 
                    subMeshCount != other.subMeshCount)
                {
                    return false;
                }
                
                for (var i = 0; i < subMeshCount; i++)
                {
                    if (indices[i] != other.indices[i] ||
                        materials[i] != other.materials[i])
                    {
                        return false;
                    }
                }

                return true;
            }

            public override bool Equals(object obj)
            {
                return obj is InstanceType other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    var hash = mesh.GetHashCode();
                    hash = (hash * 397) ^ animation.GetHashCode();
                    hash = (hash * 397) ^ subMeshCount.GetHashCode();
                    return hash;
                }
            }
        }

        struct DrawCall
        {
            public Handle<Mesh> mesh;
            public int subMesh;
            public Handle<Material> material;
            public Handle<AnimationSet> animation;
        }

        // shader resources
        static bool s_resourcesInitialized;
        static InstancingResources s_resources;
        static ComputeShader s_cullShader;
        static ComputeShader s_sortShader;
        static ComputeShader s_compactShader;
        static ComputeShader s_setDrawArgsShader;
        static KernelInfo s_cullResetCountsKernel;
        static KernelInfo s_cullKernel;
        static KernelInfo s_sortCountKernel;
        static KernelInfo s_sortCountReduceKernel;
        static KernelInfo s_sortScanKernel;
        static KernelInfo s_sortScanAddKernel;
        static KernelInfo s_sortScatterKernel;
        static KernelInfo s_compactKernel;
        static KernelInfo s_setDrawArgsKernel;

        // command buffers
        static CommandBuffer s_cullingCmdBuffer;
        
        // constant buffers
        static NativeArray<CullingPropertyBuffer> s_cullingProperties;
        static ComputeBuffer s_cullingConstantBuffer;
        static NativeArray<SortingPropertyBuffer> s_sortingProperties;
        static ComputeBuffer s_sortingConstantBuffer;
        
        // buffers with data from CPU
        static ComputeBuffer s_lodDataBuffer;
        static ComputeBuffer s_animationDataBuffer;
        static ComputeBuffer s_drawArgsSrcBuffer;
        static ComputeBuffer s_instanceTypeDataBuffer;
        static ComputeBuffer s_instanceDataBuffer;
        
        // buffers with data from GPU
        static ComputeBuffer s_instanceCountsBuffer;
        static ComputeBuffer s_sortKeysInBuffer;
        static ComputeBuffer s_sortKeysOutBuffer;
        static ComputeBuffer s_sortScratchBuffer;
        static ComputeBuffer s_sortReducedScratchBuffer;
        static ComputeBuffer s_instancePropertiesBuffer;
        static ComputeBuffer s_drawArgsBuffer;
        
        static int s_lastInstanceBuffersLength;

        // thead group sizes
        static int s_cullResetCountsThreadGroupCount;
        static int s_cullThreadGroupCount;
        static int s_sortThreadGroupCount;
        static int s_sortReducedThreadGroupCount;
        static int s_compactThreadGroupCount;

        static int s_providerIndexCount;
        static NativeArray<int> s_providerIndices;
        static NativeArray<InstanceData> s_instanceData;
        
        // TODO: move to native array, make "baked" copy that references instances
        static readonly List<DrawCall> s_drawCalls = new List<DrawCall>();
        static int s_drawArgsCount;
        static int s_numInstanceCounts;
        static int s_instanceCount;

        static bool s_enabled;
        static DirtyFlags s_dirtyFlags;
        
        static PipelineInfo s_pipelineInfo;
        static readonly MaterialPropertyBlock s_propertyBlock = new MaterialPropertyBlock();
        
        // handle registrars
        static readonly List<IInstanceProvider> s_providers = new List<IInstanceProvider>();
        static readonly HandleManager<Mesh> s_meshHandles = new HandleManager<Mesh>();
        static readonly HandleManager<Material> s_materialHandles = new HandleManager<Material>();
        static readonly HandleManager<AnimationSet> s_animationSetHandles = new HandleManager<AnimationSet>();
        
        static NativeList<ProviderState> s_providerStates;
        static NativeHashMap<Handle<Mesh>, BlittableNativeArray<DrawArgs>> s_meshToSubMeshDrawArgs;
        static NativeHashMap<Handle<AnimationSet>, BlittableNativeArray<AnimationData>> s_animationSetToAnimations;

        /// <summary>
        /// Is the instance renderer enabled.
        /// </summary>
        public static bool Enabled => s_enabled;

        static InstancingManager()
        {
            Application.quitting += OnQuit;
        }

        struct AnimationInstancingUpdate
        {
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Init()
        {
            // In case the domain reload on enter play mode is disabled, we must
            // reset all static fields.
            DisposeResources();
            DisposeRegistrar();

            s_providerStates = new NativeList<ProviderState>(Allocator.Persistent);
            s_meshToSubMeshDrawArgs = new NativeHashMap<Handle<Mesh>, BlittableNativeArray<DrawArgs>>(0, Allocator.Persistent);
            s_animationSetToAnimations = new NativeHashMap<Handle<AnimationSet>, BlittableNativeArray<AnimationData>>(0, Allocator.Persistent);

            // inject the instancing update method into the player loop
            var loop = PlayerLoop.GetCurrentPlayerLoop();

            if (loop.TryFindSubSystem<PostLateUpdate>(out var postLateUpdate))
            {
                postLateUpdate.AddSubSystem<AnimationInstancingUpdate>(1, Update);
                loop.TryUpdate(postLateUpdate);
            }

            PlayerLoop.SetPlayerLoop(loop);

            // enable the instanced renderer by default
            Enable();
        }

        static void OnQuit()
        {
            DisposeResources();
            DisposeRegistrar();
        }

        /// <summary>
        /// Checks if the current platform has support for the features required for the
        /// instancing implementation.
        /// </summary>
        /// <returns>True if the current platform is supported.</returns>
        public bool IsSupported()
        {
            return IsSupported(out _);
        }

        /// <summary>
        /// Enable the instanced renderer.
        /// </summary>
        public static void Enable()
        {
            if (!IsSupported(out var reasons))
            {
                Debug.LogWarning($"Animation instancing is not supported by the current platform: {reasons}");
                return;
            }
            if (!CreateResources())
            {
                return;
            }
            
            s_enabled = true;
            s_dirtyFlags = DirtyFlags.All;
                
            RenderPipelineManager.beginFrameRendering += OnBeginFrameRendering;
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
        }

        /// <summary>
        /// Disable the instanced renderer.
        /// </summary>
        /// <remarks>
        /// Registered providers and resources will not be cleared. 
        /// </remarks>
        /// <param name="disposeResources">Deallocate the resources used by the renderer.</param>
        public static void Disable(bool disposeResources)
        {
            RenderPipelineManager.beginFrameRendering -= OnBeginFrameRendering;
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;

            s_enabled = false;

            if (disposeResources)
            {
                DisposeResources();
            }
        }

        /// <summary>
        /// Registers an instance provider to the instance manager so its instances will be rendered.
        /// </summary>
        /// <param name="provider">The provider to register.</param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="provider"/> is null.</exception>
        public static void RegisterInstanceProvider(IInstanceProvider provider)
        {
            if (provider == null)
            {
                throw new ArgumentNullException(nameof(provider));
            }
            
            // prevent duplicate registration
            for (var i = 0; i < s_providers.Count; i++)
            {
                if (s_providers[i] == provider)
                {
                    return;
                }
            }
            
            s_providers.Add(provider);
            s_providerStates.Add(new ProviderState());
            s_dirtyFlags = DirtyFlags.All;
        }

        /// <summary>
        /// Deregisters an instance provider from the instance manager so its instances are no longer rendered.
        /// </summary>
        /// <param name="provider">The provider to deregister.</param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="provider"/> is null.</exception>
        public static void DeregisterInstanceProvider(IInstanceProvider provider)
        {
            if (provider == null)
            {
                throw new ArgumentNullException(nameof(provider));
            }

            for (var i = 0; i < s_providers.Count; i++)
            {
                if (s_providers[i] == provider)
                {
                    s_providers.RemoveAt(i);
                    s_providerStates.RemoveAt(i);
                    s_dirtyFlags = DirtyFlags.All;
                    return;
                }
            }
        }

        /// <summary>
        /// Registers a mesh to the instance manager.
        /// </summary>
        /// <remarks>
        /// If <paramref name="mesh"/> is already registered, an internal reference count is incremented
        /// and the existing handle is returned. <see cref="DeregisterMesh"/> must be called an equal number of
        /// times before the resources allocated for the registered instance are released. This behaviour allows
        /// different systems to register and deregister the same instances without interfering with each other.
        /// </remarks>
        /// <param name="mesh">The mesh to register.</param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="mesh"/> is null.</exception>
        public static Handle<Mesh> RegisterMesh(Mesh mesh)
        {
            if (mesh == null)
            {
                throw new ArgumentNullException(nameof(mesh));
            }

            if (!s_meshHandles.Register(mesh, out var handle))
            {
                return handle;
            }
            
            var subMeshDrawArgs = new BlittableNativeArray<DrawArgs>(
                mesh.subMeshCount,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory
            );
            
            for (var i = 0; i < subMeshDrawArgs.Length; i++)
            {
                subMeshDrawArgs[i] = new DrawArgs
                {
                    indexCount = mesh.GetIndexCount(i),
                    instanceCount = 0,
                    indexStart = mesh.GetIndexStart(i),
                    baseVertex = mesh.GetBaseVertex(i),
                    instanceStart = 0,
                };
            }
            
            s_meshToSubMeshDrawArgs.Add(handle, subMeshDrawArgs);
            return handle;
        }
        
        /// <summary>
        /// Deregisters a mesh from the instance manager.
        /// </summary>
        /// <param name="handle">The handle of the mesh to deregister.</param>
        public static void DeregisterMesh(Handle<Mesh> handle)
        {
            if (s_meshHandles.Deregister(handle))
            {
                s_meshToSubMeshDrawArgs.Remove(handle);
            }
        }

        /// <summary>
        /// Registers a material to the instance manager.
        /// </summary>
        /// <remarks>
        /// If <paramref name="material"/> is already registered, an internal reference count is incremented
        /// and the existing handle is returned. <see cref="DeregisterMaterial"/> must be called an equal number of
        /// times before the resources allocated for the registered instance are released. This behaviour allows
        /// different systems to register and deregister the same instances without interfering with each other.
        /// </remarks>
        /// <param name="material">The material to register.</param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="material"/> is null.</exception>
        public static Handle<Material> RegisterMaterial(Material material)
        {
            if (material == null)
            {
                throw new ArgumentNullException(nameof(material));
            }

            s_materialHandles.Register(material, out var handle);
            return handle;
        }
        
        /// <summary>
        /// Deregisters a material from the instance manager.
        /// </summary>
        /// <param name="handle">The handle of the material to deregister.</param>
        public static void DeregisterMaterial(Handle<Material> handle)
        {
            s_materialHandles.Deregister(handle);
        }
        
        /// <summary>
        /// Registers an animation set to the instance manager.
        /// </summary>
        /// <remarks>
        /// If <paramref name="animationSet"/> is already registered, an internal reference count is incremented
        /// and the existing handle is returned. <see cref="DeregisterAnimationSet"/> must be called an equal number of
        /// times before the resources allocated for the registered instance are released. This behaviour allows
        /// different systems to register and deregister the same instances without interfering with each other.
        /// </remarks>
        /// <param name="animationSet">The animation set to register.</param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="animationSet"/> is null.</exception>
        public static Handle<AnimationSet> RegisterAnimationSet(AnimationSet animationSet)
        {
            if (animationSet == null)
            {
                throw new ArgumentNullException(nameof(animationSet));
            }

            if (!s_animationSetHandles.Register(animationSet, out var handle))
            {
                return handle;
            }

            var animations = new BlittableNativeArray<AnimationData>(
                animationSet.Animations.Length,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory
            );
            
            for (var i = 0; i < animations.Length; i++)
            {
                animations[i] = animationSet.Animations[i].Data; 
            }
            
            s_animationSetToAnimations.Add(handle, animations);
            return handle;
        }

        /// <summary>
        /// Deregisters an animation set from the instance manager.
        /// </summary>
        /// <param name="handle">The handle of the animation set to deregister.</param>
        public static void DeregisterAnimationSet(Handle<AnimationSet> handle)
        {
            if (s_animationSetHandles.Deregister(handle))
            {
                s_animationSetToAnimations.Remove(handle);
            }
        }

        static void Update()
        {
            if (!s_enabled)
            {
                return;
            }

            // check which buffers need to be updated, if any
            for (var i = 0; i < s_providers.Count; i++)
            {
                var provider = s_providers[i];
                var dirtyFlags = provider.DirtyFlags;

                if (dirtyFlags == DirtyFlags.None)
                {
                    continue;
                }
                
                // get the modified state
                var state = s_providerStates[i];
                
                state.dirtyFlags = dirtyFlags;
                provider.GetState(out state.renderState, out var subMeshes, out var instances);
                state.subMeshes = subMeshes;
                state.instances = instances;
                
                s_providerStates[i] = state;
                
                s_dirtyFlags |= dirtyFlags;
                provider.ClearDirtyFlags();
            }

            // update any buffers whose contents is invalidated
            if (s_dirtyFlags.Intersects(DirtyFlags.Lods))
            {
                UpdateLodBuffers();
            }
            if (s_dirtyFlags.Intersects(DirtyFlags.Animation))
            {
                UpdateAnimationBuffers();
            }
            if (s_dirtyFlags.Intersects(DirtyFlags.Lods | DirtyFlags.Mesh | DirtyFlags.SubMeshes | DirtyFlags.Materials))
            {
                UpdateDrawArgsBuffers();
            }
            if (s_dirtyFlags.Intersects(DirtyFlags.InstanceCount))
            {
                UpdateInstanceBuffers();
            }
            if (s_dirtyFlags.Intersects(DirtyFlags.All))
            {
                UpdateInstances(s_dirtyFlags != DirtyFlags.PerInstanceData);
            }

            s_dirtyFlags = DirtyFlags.None;
        }

        static void OnBeginFrameRendering(ScriptableRenderContext context, Camera[] cameras)
        {
            if (s_instanceCount == 0)
            {
                return;
            }

            PrepareFrameRendering();
        }

        static void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (s_instanceCount == 0)
            {
                return;
            }

            Render(camera);
        }

        static void PrepareFrameRendering()
        {
            Profiler.BeginSample($"{nameof(InstancingManager)}.{nameof(PrepareFrameRendering)}");
            
            PipelineInfo.GetInfoForCurrentPipeline(out s_pipelineInfo);
            
            if (s_pipelineInfo.shadowsEnabled)
            {
                s_cullShader.EnableKeyword(Keywords.Culling.SHADOWS_ENABLED);
                s_setDrawArgsShader.EnableKeyword(Keywords.Culling.SHADOWS_ENABLED);
            }
            else
            {
                s_cullShader.DisableKeyword(Keywords.Culling.SHADOWS_ENABLED);
                s_setDrawArgsShader.DisableKeyword(Keywords.Culling.SHADOWS_ENABLED);
            }

            UpdateBuffers();
            
            Profiler.EndSample();
        }

        static void UpdateBuffers()
        {
            Profiler.BeginSample($"{nameof(InstancingManager)}.{nameof(UpdateBuffers)}");

            var instanceBuffersLength = s_instanceCount;
            
            if (s_pipelineInfo.shadowsEnabled)
            {
                instanceBuffersLength *= 2;
            }
            
            if (s_lastInstanceBuffersLength != instanceBuffersLength)
            {
                s_lastInstanceBuffersLength = instanceBuffersLength;
                
                // find how many thread groups we should use for the culling shaders
                s_compactThreadGroupCount = GetThreadGroupCount(instanceBuffersLength, s_compactKernel.threadGroupSizeX);

                // determine the properties of the sorting pass needed for the current instance count
                var blockSize = Constants.k_sortElementsPerThread * s_sortCountKernel.threadGroupSizeX;
                var numBlocks = GetThreadGroupCount(instanceBuffersLength, blockSize);
                var numReducedBlocks = GetThreadGroupCount(numBlocks, blockSize);
                
                s_sortThreadGroupCount = 800;
                var numThreadGroupsWithAdditionalBlocks = numBlocks % s_sortThreadGroupCount;
                var blocksPerThreadGroup = numBlocks / s_sortThreadGroupCount;

                if (numBlocks < s_sortThreadGroupCount)
                {
                    s_sortThreadGroupCount = numBlocks;
                    numThreadGroupsWithAdditionalBlocks = 0;
                    blocksPerThreadGroup = 1;
                }
                
                var reducedThreadGroupCountPerBin = (blockSize > s_sortThreadGroupCount) ? 1 : GetThreadGroupCount(s_sortThreadGroupCount, blockSize);
                s_sortReducedThreadGroupCount = Constants.k_sortBinCount * reducedThreadGroupCountPerBin;

                Debug.Assert(s_sortReducedThreadGroupCount < blockSize, "Need to account for bigger reduced histogram scan!");

                var sortScratchBufferLength = Constants.k_sortBinCount * numBlocks;
                var sortReducedScratchBufferLength = Constants.k_sortBinCount * numReducedBlocks;

                // update the sorting constant buffer
                s_sortingProperties[0] = new SortingPropertyBuffer
                {
                    _NumKeys = (uint)instanceBuffersLength,
                    _NumBlocksPerThreadGroup = blocksPerThreadGroup,
                    _NumThreadGroups = (uint)s_sortThreadGroupCount,
                    _NumThreadGroupsWithAdditionalBlocks = (uint)numThreadGroupsWithAdditionalBlocks,
                    _NumReduceThreadGroupPerBin = (uint)reducedThreadGroupCountPerBin,
                    _NumScanValues = (uint)s_sortReducedThreadGroupCount,
                };
                
                s_sortingConstantBuffer.SetData(s_sortingProperties, 0, 0, s_sortingProperties.Length);
                
                // update the sorting scratch buffers
                if (s_sortScratchBuffer == null || s_sortScratchBuffer.count < sortScratchBufferLength)
                {
                    DisposeUtils.Dispose(ref s_sortScratchBuffer);
                
                    var count = Mathf.NextPowerOfTwo(sortScratchBufferLength);

                    s_sortScratchBuffer = new ComputeBuffer(count, sizeof(uint))
                    {
                        name = $"{nameof(InstancingManager)}_SortScratchBuffer",
                    };
                }
            
                if (s_sortReducedScratchBuffer == null || s_sortReducedScratchBuffer.count < sortReducedScratchBufferLength)
                {
                    DisposeUtils.Dispose(ref s_sortReducedScratchBuffer);
                
                    var count = Mathf.NextPowerOfTwo(sortReducedScratchBufferLength);

                    s_sortReducedScratchBuffer = new ComputeBuffer(count, sizeof(uint))
                    {
                        name = $"{nameof(InstancingManager)}_SortReducedScratchBuffer",
                    };
                }
            }
            
            Profiler.EndSample();
        }
            
        static void Render(Camera cam)
        {
            Profiler.BeginSample($"{nameof(InstancingManager)}.{nameof(Render)}");

            Cull(cam);
            Draw(cam);

            Profiler.EndSample();
        }
        
        static void Cull(Camera cam)
        {
            Profiler.BeginSample($"{nameof(InstancingManager)}.{nameof(Cull)}");
            
            s_cullingCmdBuffer.Clear();
            
            // update constant buffer
            var vFov = cam.fieldOfView;
            var hFov = Camera.VerticalToHorizontalFieldOfView(vFov, cam.aspect);
            var fov = math.max(vFov, hFov);

            s_cullingProperties[0] = new CullingPropertyBuffer
            {
                _ViewProj = cam.projectionMatrix * cam.worldToCameraMatrix,
                _CameraPosition = cam.transform.position,
                _LodBias = QualitySettings.lodBias,
                _LodScale = 1f / (2f * math.tan(math.radians(fov / 2f))),
                _ShadowDistance = s_pipelineInfo.shadowDistance,
                _PassCount = s_pipelineInfo.shadowsEnabled ? 2u : 1u,
                _InstanceCount = (uint)s_instanceCount,
                _NumInstanceCounts = (uint)s_numInstanceCounts,
                _DrawArgsPerPass = (uint)s_drawArgsCount,
            };
            
            s_cullingCmdBuffer.SetBufferData(
                s_cullingConstantBuffer,
                s_cullingProperties,
                0, 
                0, 
                s_cullingProperties.Length
            );

            // initialize buffers
            s_cullingCmdBuffer.SetComputeConstantBufferParam(
                s_cullShader,
                Properties.Culling._ConstantBuffer,
                s_cullingConstantBuffer,
                0,
                CullingPropertyBuffer.k_size
            );
            s_cullingCmdBuffer.SetComputeBufferParam(
                s_cullShader,
                s_cullResetCountsKernel.kernelID,
                Properties.Culling._InstanceCounts,
                s_instanceCountsBuffer
            );
            
            s_cullingCmdBuffer.DispatchCompute(
                s_cullShader,
                s_cullResetCountsKernel.kernelID, 
                s_cullResetCountsThreadGroupCount, 1, 1
            );
            
            // culling and lod selection
            s_cullingCmdBuffer.SetComputeBufferParam(
                s_cullShader,
                s_cullKernel.kernelID,
                Properties.Culling._LodData,
                s_lodDataBuffer
            );
            s_cullingCmdBuffer.SetComputeBufferParam(
                s_cullShader,
                s_cullKernel.kernelID,
                Properties.Culling._AnimationData,
                s_animationDataBuffer
            );
            s_cullingCmdBuffer.SetComputeBufferParam(
                s_cullShader,
                s_cullKernel.kernelID,
                Properties.Culling._InstanceData,
                s_instanceDataBuffer
            );
            s_cullingCmdBuffer.SetComputeBufferParam(
                s_cullShader,
                s_cullKernel.kernelID,
                Properties.Culling._InstanceCounts,
                s_instanceCountsBuffer
            );
            s_cullingCmdBuffer.SetComputeBufferParam(
                s_cullShader,
                s_cullKernel.kernelID,
                Properties.Culling._SortKeys,
                s_sortKeysInBuffer
            );

            s_cullingCmdBuffer.DispatchCompute(
                s_cullShader,
                s_cullKernel.kernelID, 
                s_cullThreadGroupCount, 1, 1
            );
        
            // sort
            s_cullingCmdBuffer.SetComputeConstantBufferParam(
                s_sortShader,
                Properties.Sort._ConstantBuffer,
                s_sortingConstantBuffer,
                0,
                SortingPropertyBuffer.k_size
            );

            var sortKeysBuffer = s_sortKeysInBuffer;
            var sortKeysTempBuffer = s_sortKeysOutBuffer;
            
            for (var shift = 0; shift < Constants.k_sortKeyBits; shift += Constants.k_sortBitsPerPass)
            {
                s_cullingCmdBuffer.SetComputeIntParam(
                    s_sortShader,
                    Properties.Sort._ShiftBit,
                    shift
                );

                // count
                s_cullingCmdBuffer.SetComputeBufferParam(
                    s_sortShader, 
                    s_sortCountKernel.kernelID,
                    Properties.Sort._SrcBuffer,
                    sortKeysBuffer
                );
                s_cullingCmdBuffer.SetComputeBufferParam(
                    s_sortShader, 
                    s_sortCountKernel.kernelID,
                    Properties.Sort._SumTable,
                    s_sortScratchBuffer
                );
                
                s_cullingCmdBuffer.DispatchCompute(
                    s_sortShader,
                    s_sortCountKernel.kernelID, 
                    s_sortThreadGroupCount, 1, 1
                );
                
                // reduce
                s_cullingCmdBuffer.SetComputeBufferParam(
                    s_sortShader, 
                    s_sortCountReduceKernel.kernelID,
                    Properties.Sort._SumTable,
                    s_sortScratchBuffer
                );
                s_cullingCmdBuffer.SetComputeBufferParam(
                    s_sortShader, 
                    s_sortCountReduceKernel.kernelID,
                    Properties.Sort._ReduceTable,
                    s_sortReducedScratchBuffer
                );
                
                s_cullingCmdBuffer.DispatchCompute(
                    s_sortShader,
                    s_sortCountReduceKernel.kernelID, 
                    s_sortReducedThreadGroupCount, 1, 1
                );
                
                // scan
                s_cullingCmdBuffer.SetComputeBufferParam(
                    s_sortShader, 
                    s_sortScanKernel.kernelID,
                    Properties.Sort._Scan,
                    s_sortReducedScratchBuffer
                );

                s_cullingCmdBuffer.DispatchCompute(
                    s_sortShader,
                    s_sortScanKernel.kernelID, 
                    1, 1, 1
                );

                // scan add
                s_cullingCmdBuffer.SetComputeBufferParam(
                    s_sortShader, 
                    s_sortScanAddKernel.kernelID,
                    Properties.Sort._Scan,
                    s_sortScratchBuffer
                );
                s_cullingCmdBuffer.SetComputeBufferParam(
                    s_sortShader, 
                    s_sortScanAddKernel.kernelID,
                    Properties.Sort._ScanScratch,
                    s_sortReducedScratchBuffer
                );

                s_cullingCmdBuffer.DispatchCompute(
                    s_sortShader,
                    s_sortScanAddKernel.kernelID, 
                    s_sortReducedThreadGroupCount, 1, 1
                );

                // scatter
                s_cullingCmdBuffer.SetComputeBufferParam(
                    s_sortShader, 
                    s_sortScatterKernel.kernelID,
                    Properties.Sort._SrcBuffer,
                    sortKeysBuffer
                );
                s_cullingCmdBuffer.SetComputeBufferParam(
                    s_sortShader, 
                    s_sortScatterKernel.kernelID,
                    Properties.Sort._SumTable,
                    s_sortScratchBuffer
                );
                s_cullingCmdBuffer.SetComputeBufferParam(
                    s_sortShader, 
                    s_sortScatterKernel.kernelID,
                    Properties.Sort._DstBuffer,
                    sortKeysTempBuffer
                );
                
                s_cullingCmdBuffer.DispatchCompute(
                    s_sortShader,
                    s_sortScatterKernel.kernelID, 
                    s_sortThreadGroupCount, 1, 1
                );

                // alternate which buffer we are sorting into
                var lastSortKeysBuffer = sortKeysBuffer;
                sortKeysBuffer = sortKeysTempBuffer;
                sortKeysTempBuffer = lastSortKeysBuffer;
            }
            
            // compact
            s_cullingCmdBuffer.SetComputeConstantBufferParam(
                s_compactShader,
                Properties.Compact._ConstantBuffer,
                s_cullingConstantBuffer,
                0,
                CullingPropertyBuffer.k_size
            );
            s_cullingCmdBuffer.SetComputeBufferParam(
                s_compactShader,
                s_compactKernel.kernelID,
                Properties.Compact._InstanceData,
                s_instanceDataBuffer
            );
            s_cullingCmdBuffer.SetComputeBufferParam(
                s_compactShader,
                s_compactKernel.kernelID,
                Properties.Compact._SortKeys,
                sortKeysBuffer
            );
            s_cullingCmdBuffer.SetComputeBufferParam(
                s_compactShader,
                s_compactKernel.kernelID,
                Properties.Compact._InstanceProperties,
                s_instancePropertiesBuffer
            );
            
            s_cullingCmdBuffer.DispatchCompute(
                s_compactShader,
                s_compactKernel.kernelID, 
                s_compactThreadGroupCount, 1, 1
            );
            
            // set draw args
            s_cullingCmdBuffer.SetComputeConstantBufferParam(
                s_setDrawArgsShader,
                Properties.SetDrawArgs._ConstantBuffer,
                s_cullingConstantBuffer,
                0,
                CullingPropertyBuffer.k_size
            );
            s_cullingCmdBuffer.SetComputeBufferParam(
                s_setDrawArgsShader,
                s_setDrawArgsKernel.kernelID,
                Properties.SetDrawArgs._InstanceCounts,
                s_instanceCountsBuffer
            );
            s_cullingCmdBuffer.SetComputeBufferParam(
                s_setDrawArgsShader,
                s_setDrawArgsKernel.kernelID,
                Properties.SetDrawArgs._InstanceTypeData,
                s_instanceTypeDataBuffer
            );
            s_cullingCmdBuffer.SetComputeBufferParam(
                s_setDrawArgsShader,
                s_setDrawArgsKernel.kernelID,
                Properties.SetDrawArgs._DrawArgsSrc,
                s_drawArgsSrcBuffer
            );
            s_cullingCmdBuffer.SetComputeBufferParam(
                s_setDrawArgsShader,
                s_setDrawArgsKernel.kernelID,
                Properties.SetDrawArgs._DrawArgs,
                s_drawArgsBuffer
            );
            
            s_cullingCmdBuffer.DispatchCompute(
                s_setDrawArgsShader,
                s_setDrawArgsKernel.kernelID, 
                1, 1, 1
            );
            
            Graphics.ExecuteCommandBuffer(s_cullingCmdBuffer);

            Profiler.EndSample();
        }

        static void Draw(Camera cam)
        {
            Profiler.BeginSample($"{nameof(InstancingManager)}.{nameof(Draw)}");

            var bounds = new Bounds
            {
                center = cam.transform.position,
                extents = Vector3.one * cam.farClipPlane,
            };

            var argsLength = s_drawArgsCount * DrawArgs.k_size;
            
            for (var i = 0; i < s_drawArgsCount; i++)
            {
                var drawCall = s_drawCalls[i];
                var mesh = s_meshHandles.GetInstance(drawCall.mesh);
                var material = s_materialHandles.GetInstance(drawCall.material);
                var animationSet = s_animationSetHandles.GetInstance(drawCall.animation);
                var argsIndex = i;
                
                s_propertyBlock.SetInt(Properties.Main._DrawArgsOffset, argsIndex);
                s_propertyBlock.SetTexture(Properties.Main._Animation, animationSet.Texture);

                Graphics.DrawMeshInstancedIndirect(
                    mesh,
                    drawCall.subMesh,
                    material,
                    bounds,
                    s_drawArgsBuffer,
                    argsIndex * DrawArgs.k_size,
                    s_propertyBlock,
                    ShadowCastingMode.Off,
                    true,
                    0,
                    cam,
                    LightProbeUsage.Off
                );

                if (!s_pipelineInfo.shadowsEnabled)
                {
                    continue;
                }
                
                argsIndex += s_drawArgsCount;
                s_propertyBlock.SetInt(Properties.Main._DrawArgsOffset, argsIndex);
                    
                Graphics.DrawMeshInstancedIndirect(
                    mesh,
                    drawCall.subMesh,
                    material,
                    bounds,
                    s_drawArgsBuffer,
                    argsIndex * DrawArgs.k_size,
                    s_propertyBlock,
                    ShadowCastingMode.ShadowsOnly,
                    true,
                    0,
                    cam,
                    LightProbeUsage.Off
                );
            }

            Profiler.EndSample();
        }

        static void UpdateLodBuffers()
        {
            Profiler.BeginSample($"{nameof(InstancingManager)}.{nameof(UpdateLodBuffers)}");

            // get the lod data for all instanced meshes
            var lodData = new NativeArray<LodData>(
                s_providerStates.Length, 
                Allocator.Temp, 
                NativeArrayOptions.UninitializedMemory
            );
            
            for (var i = 0; i < s_providerStates.Length; i++)
            {
                var state = s_providerStates[i];

                lodData[i] = state.renderState.lods;
                state.lodIndex = i;

                s_providerStates[i] = state;
            }
            
            // create a new buffer if the previous one is too small
            if (s_lodDataBuffer == null || s_lodDataBuffer.count < lodData.Length)
            {
                DisposeUtils.Dispose(ref s_lodDataBuffer);
            
                s_lodDataBuffer = new ComputeBuffer(Mathf.NextPowerOfTwo(lodData.Length), LodData.k_size)
                {
                    name = $"{nameof(InstancingManager)}_{nameof(LodData)}",
                };
            }
            
            s_lodDataBuffer.SetData(lodData, 0, 0, lodData.Length);
            lodData.Dispose();
            
            Profiler.EndSample();
        }

        static void UpdateAnimationBuffers()
        {
            Profiler.BeginSample($"{nameof(InstancingManager)}.{nameof(UpdateAnimationBuffers)}");

            // Find all the animation sets that are currently required for rendering, and
            // determine the size required for the animation data buffer
            var animationSets = new NativeList<Handle<AnimationSet>>(
                s_providerStates.Length,
                Allocator.Temp
            );
            var animationSetsToBaseIndex = new NativeHashMap<Handle<AnimationSet>, int>(
                0,
                Allocator.Temp
            );
            var animationCount = 0;
            
            for (var i = 0; i < s_providerStates.Length; i++)
            {
                var state = s_providerStates[i];
                var animationSet = state.renderState.animationSet;

                if (!animationSetsToBaseIndex.TryGetValue(animationSet, out var baseIndex))
                {
                    baseIndex = animationCount;
                    animationSetsToBaseIndex.Add(animationSet, baseIndex);
                    animationSets.Add(animationSet);

                    animationCount += s_animationSetToAnimations[animationSet].Length;
                }
                
                state.animationBaseIndex = baseIndex;

                s_providerStates[i] = state;
            }

            // get all the animation data
            var animationData = new NativeArray<AnimationData>(
                animationCount,
                Allocator.Temp,
                NativeArrayOptions.UninitializedMemory
            );
            
            var animationIndex = 0;
            
            for (var i = 0; i < animationSets.Length; i++)
            {
                var animationSet = animationSets[i];
                var animations = s_animationSetToAnimations[animationSet];

                for (var j = 0; j < animations.Length; j++)
                {
                    animationData[animationIndex++] = animations[j];
                }
            }

            animationSets.Dispose();
            animationSetsToBaseIndex.Dispose();

            // update the animation data buffer
            if (s_animationDataBuffer == null || s_animationDataBuffer.count < animationData.Length)
            {
                DisposeUtils.Dispose(ref s_animationDataBuffer);
            
                s_animationDataBuffer = new ComputeBuffer(Mathf.NextPowerOfTwo(animationData.Length), AnimationData.k_size)
                {
                    name = $"{nameof(InstancingManager)}_{nameof(AnimationData)}",
                };
                
                s_propertyBlock.SetBuffer(Properties.Main._AnimationData, s_animationDataBuffer);
            }
            
            s_animationDataBuffer.SetData(animationData, 0, 0, animationData.Length);
            animationData.Dispose();

            
            Profiler.EndSample();
        }
        
        static void UpdateDrawArgsBuffers()
        {
            Profiler.BeginSample($"{nameof(InstancingManager)}.{nameof(UpdateDrawArgsBuffers)}");

            s_drawCalls.Clear();

            var instanceTypeToIndex = new NativeHashMap<InstanceType, int>(
                s_providerStates.Length,
                Allocator.Temp
            );
            var instanceTypeCountIndices = new NativeArray<int>(
                s_providerStates.Length,
                Allocator.Temp
            );
            var instanceTypeData = new NativeArray<uint>(
                s_providerStates.Length * Constants.k_maxLodCount,
                Allocator.Temp
            );
            var instanceTypeIndexToProviderIndices = new NativeMultiHashMap<int, int>(
                s_providerStates.Length,
                Allocator.Temp
            );

            var numInstanceCounts = 0;
            
            for (var i = 0; i < s_providerStates.Length; i++)
            {
                var state = s_providerStates[i];
                var mesh = state.renderState.mesh;
                var animationSet = state.renderState.animationSet;
                var lodCount = (int)state.renderState.lods.lodCount;
                var subMeshes = (NativeSlice<SubMesh>)state.subMeshes;
                var subMeshCount = Mathf.Min(subMeshes.Length, Constants.k_maxSubMeshCount);

                var instanceType = new InstanceType(mesh, animationSet, subMeshes);
                
                if (!instanceTypeToIndex.TryGetValue(instanceType, out var typeIndex))
                {
                    typeIndex = instanceTypeToIndex.Count();
                    
                    instanceTypeToIndex.Add(instanceType, typeIndex);
                    instanceTypeCountIndices[typeIndex] = numInstanceCounts;
                    
                    for (var j = 0; j < lodCount; j++)
                    {
                        var typeData = ((uint)subMeshCount << 16) | ((uint)s_drawCalls.Count & 0xffff);
                        instanceTypeData[numInstanceCounts++] = typeData;
                        
                        for (var k = 0; k < subMeshCount; k++)
                        {
                            var subMesh = subMeshes[k];
                        
                            var drawCall = new DrawCall
                            {
                                mesh = mesh,
                                subMesh = (subMeshCount * j) + subMesh.subMeshIndex,
                                material = subMesh.materialHandle,
                                animation = animationSet,
                            };
                
                            s_drawCalls.Add(drawCall);
                        }
                    }
                }
                
                instanceTypeIndexToProviderIndices.Add(typeIndex, i);

                state.countBaseIndex = instanceTypeCountIndices[typeIndex];

                s_providerStates[i] = state;
            }

            if (numInstanceCounts > Constants.k_maxInstanceTypes)
            {
                Debug.LogError($"There are more than {Constants.k_maxInstanceTypes} instance types active. " +
                               $"Rendering artifacts are expected. " +
                               $"Reduce the number of unique mesh/sub mesh/material combinations, or use fewer lods to avoid the issue.");
            }

            // flatten the provider index ordering map to an array for fast iteration
            s_providerIndexCount = instanceTypeIndexToProviderIndices.Count();
            
            if (!s_providerIndices.IsCreated || s_providerIndices.Length < s_providerIndexCount)
            {
                DisposeUtils.Dispose(ref s_providerIndices);
                
                s_providerIndices = new NativeArray<int>(
                    s_providerIndexCount,
                    Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory
                );
            }
            
            var instanceTypeCount = instanceTypeToIndex.Count();
            var currentProvider = 0;
            
            for (var i = 0; i < instanceTypeCount; i++)
            {
                foreach (var providerIndex in instanceTypeIndexToProviderIndices.GetValuesForKey(i))
                {
                    s_providerIndices[currentProvider] = providerIndex;
                    currentProvider++;
                }
            }

            // update the draw args data
            var drawCallCount = s_drawCalls.Count;
            s_drawArgsCount = drawCallCount;

            var drawArgs = new NativeArray<DrawArgs>(
                drawCallCount,
                Allocator.Temp,
                NativeArrayOptions.UninitializedMemory
            );
            
            for (var i = 0; i < drawCallCount; i++)
            {
                var drawCall = s_drawCalls[i];
                var subMeshDrawArgs = s_meshToSubMeshDrawArgs[drawCall.mesh];
                drawArgs[i] = subMeshDrawArgs[drawCall.subMesh];
            }
            
            if (s_drawArgsSrcBuffer == null || s_drawArgsSrcBuffer.count < drawCallCount)
            {
                DisposeUtils.Dispose(ref s_drawArgsSrcBuffer);

                var count = Mathf.NextPowerOfTwo(drawCallCount);
            
                s_drawArgsSrcBuffer = new ComputeBuffer(count, DrawArgs.k_size)
                {
                    name = $"{nameof(InstancingManager)}_DrawArgsSrc",
                };
            }
            
            s_drawArgsSrcBuffer.SetData(drawArgs, 0, 0, drawCallCount);
            drawArgs.Dispose();

            // update the per draw arg buffer
            var drawArgsBufferLength = Constants.k_maxPassCount * drawCallCount;
            
            if (s_drawArgsBuffer == null || s_drawArgsBuffer.count < drawArgsBufferLength)
            {
                DisposeUtils.Dispose(ref s_drawArgsBuffer);

                var count = Mathf.NextPowerOfTwo(drawArgsBufferLength);
        
                s_drawArgsBuffer = new ComputeBuffer(count, DrawArgs.k_size, ComputeBufferType.IndirectArguments)
                {
                    name = $"{nameof(InstancingManager)}_{nameof(DrawArgs)}",
                };
                
                s_propertyBlock.SetBuffer(Properties.Main._DrawArgs, s_drawArgsBuffer);
            }
            
            // update the instance draw count buffers
            s_numInstanceCounts = numInstanceCounts;
            s_cullResetCountsThreadGroupCount = GetThreadGroupCount(numInstanceCounts, s_cullResetCountsKernel.threadGroupSizeX);
            
            if (s_instanceTypeDataBuffer == null || s_instanceTypeDataBuffer.count < numInstanceCounts)
            {
                DisposeUtils.Dispose(ref s_instanceTypeDataBuffer);

                var count = Mathf.NextPowerOfTwo(numInstanceCounts);
            
                s_instanceTypeDataBuffer = new ComputeBuffer(count, sizeof(uint))
                {
                    name = $"{nameof(InstancingManager)}_InstanceTypeData",
                };
            }
            
            s_instanceTypeDataBuffer.SetData(instanceTypeData, 0, 0, numInstanceCounts);

            var numInstanceCountsBufferLength = Constants.k_maxPassCount * s_numInstanceCounts;
            
            if (s_instanceCountsBuffer == null || s_instanceCountsBuffer.count < numInstanceCountsBufferLength)
            {
                DisposeUtils.Dispose(ref s_instanceCountsBuffer);

                var count = Mathf.NextPowerOfTwo(numInstanceCountsBufferLength);
        
                s_instanceCountsBuffer = new ComputeBuffer(count, sizeof(uint))
                {
                    name = $"{nameof(InstancingManager)}_InstanceCounts",
                };
            }

            // dispose temp collections
            instanceTypeToIndex.Dispose();
            instanceTypeCountIndices.Dispose();
            instanceTypeData.Dispose();
            instanceTypeIndexToProviderIndices.Dispose();

            Profiler.EndSample();
        }

        static void UpdateInstanceBuffers()
        {
            Profiler.BeginSample($"{nameof(InstancingManager)}.{nameof(UpdateInstanceBuffers)}");

            // get the instance count
            var instanceCount = 0;
            
            for (var i = 0; i < s_providerStates.Length; i++)
            {
                instanceCount += ((NativeSlice<Instance>)s_providerStates[i].instances).Length;
            }

            if (instanceCount > Constants.k_maxInstanceCount)
            {
                Debug.LogError($"There are more than {Constants.k_maxInstanceCount} instances being rendered. " +
                               $"Rendering artifacts are expected.");
            }

            s_instanceCount = instanceCount;
            
            // update the number of threads needed to cull this many instances
            s_cullThreadGroupCount = GetThreadGroupCount(instanceCount, s_cullKernel.threadGroupSizeX);
            
            // update the instance data buffers
            if (!s_instanceData.IsCreated || s_instanceData.Length < instanceCount)
            {
                DisposeUtils.Dispose(ref s_instanceData);
                
                s_instanceData = new NativeArray<InstanceData>(
                    instanceCount,
                    Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory
                );
            }
            
            if (s_instanceDataBuffer == null || s_instanceDataBuffer.count < instanceCount)
            {
                DisposeUtils.Dispose(ref s_instanceDataBuffer);

                var count = Mathf.NextPowerOfTwo(instanceCount);
                
                s_instanceDataBuffer = new ComputeBuffer(count, InstanceData.k_size)
                {
                    name = $"{nameof(InstancingManager)}_{nameof(InstanceData)}",
                };
            }

            var instanceBuffersLength = Constants.k_maxPassCount * instanceCount;
            
            if (s_instancePropertiesBuffer == null || s_instancePropertiesBuffer.count < instanceBuffersLength)
            {
                DisposeUtils.Dispose(ref s_sortKeysInBuffer);
                DisposeUtils.Dispose(ref s_sortKeysOutBuffer);
                DisposeUtils.Dispose(ref s_instancePropertiesBuffer);

                var count = Mathf.NextPowerOfTwo(instanceBuffersLength);
                
                s_sortKeysInBuffer = new ComputeBuffer(count, sizeof(uint))
                {
                    name = $"{nameof(InstancingManager)}_SortKeysInBuffer",
                };
                s_sortKeysOutBuffer = new ComputeBuffer(count, sizeof(uint))
                {
                    name = $"{nameof(InstancingManager)}_SortKeysOutBuffer",
                };
                s_instancePropertiesBuffer = new ComputeBuffer(count, InstanceProperties.k_size)
                {
                    name = $"{nameof(InstancingManager)}_{nameof(InstanceProperties)}",
                };
                
                s_propertyBlock.SetBuffer(Properties.Main._InstanceProperties, s_instancePropertiesBuffer);
            }

            Profiler.EndSample();
        }

        static void UpdateInstances(bool forceUpdate)
        {
            Profiler.BeginSample($"{nameof(InstancingManager)}.{nameof(UpdateInstances)}");

            var updateJobs = new NativeArray<JobHandle>(
                s_providerStates.Length,
                Allocator.Temp,
                NativeArrayOptions.UninitializedMemory
            );
            var jobCount = 0;
            var currentInstances = 0;
            
            // create jobs that update each region of the instance buffer that has changed
            for (var i = 0; i < s_providerIndexCount; i++)
            {
                var providerIndex = s_providerIndices[i];
                var state = s_providerStates[providerIndex];
                var instances = (NativeSlice<Instance>)state.instances;
                
                if (forceUpdate || state.dirtyFlags.Intersects(DirtyFlags.PerInstanceData))
                {
                    var job = new UpdateInstancesJob
                    {
                        lodIndex = (uint)state.lodIndex,
                        countBaseIndex = (uint)state.countBaseIndex,
                        animationBaseIndex = (uint)(state.animationBaseIndex),
                        instanceStart = currentInstances,
                        instances = instances,
                        instanceData = s_instanceData,
                    };

                    updateJobs[jobCount++] = job.Schedule(instances.Length, 64);
                }

                currentInstances += instances.Length;
            }

            // schedule the instance updates and wait for completion.
            var updateInstancesJob = JobHandle.CombineDependencies(updateJobs.Slice(0, jobCount));
            updateInstancesJob.Complete();

            // upload the instance data to the compute buffer
            s_instanceDataBuffer.SetData(s_instanceData, 0, 0, currentInstances);
            
            Profiler.EndSample();
        }
        
        [BurstCompile]
        struct UpdateInstancesJob : IJobParallelFor
        {
            public uint lodIndex;
            public uint countBaseIndex;
            public uint animationBaseIndex;
            public int instanceStart;

            [ReadOnly, NoAlias]
            public NativeSlice<Instance> instances;
            
            [WriteOnly, NoAlias, NativeDisableContainerSafetyRestriction]
            public NativeArray<InstanceData> instanceData;

            /// <inheritdoc />
            public void Execute(int i)
            {
                var instance = instances[i];

                instanceData[instanceStart + i] = new InstanceData
                {
                    transform = instance.transform,
                    lodIndex = lodIndex,
                    countBaseIndex = countBaseIndex,
                    animationIndex = animationBaseIndex + (uint)instance.animationIndex,
                    animationTime = instance.animationTime,
                };
            }
        }

        static bool CreateResources()
        {
            if (s_resourcesInitialized)
            {
                return true;
            }

            // load the compute shaders
            s_resources = Resources.Load<InstancingResources>(nameof(InstancingResources));

            if (s_resources == null)
            {
                Debug.LogError("Failed to load instancing resources!");
                DisposeResources();
                return false;
            }

            s_cullShader = s_resources.Culling;
            s_sortShader = s_resources.Sort;
            s_compactShader = s_resources.Compact;
            s_setDrawArgsShader = s_resources.SetDrawArgs;

            if (s_cullShader == null ||
                s_sortShader == null ||
                s_compactShader == null ||
                s_setDrawArgsShader == null
            )
            {
                Debug.LogError("Required compute shaders have not been assigned to the instancing resources asset!");
                DisposeResources();
                return false;
            }

            if (!TryGetKernel(s_cullShader, Kernels.Culling.k_resetCounts, out s_cullResetCountsKernel) ||
                !TryGetKernel(s_cullShader, Kernels.Culling.k_cull, out s_cullKernel) ||
                !TryGetKernel(s_sortShader, Kernels.Sort.k_count, out s_sortCountKernel) ||
                !TryGetKernel(s_sortShader, Kernels.Sort.k_countReduce, out s_sortCountReduceKernel) ||
                !TryGetKernel(s_sortShader, Kernels.Sort.k_scan, out s_sortScanKernel) ||
                !TryGetKernel(s_sortShader, Kernels.Sort.k_scanAdd, out s_sortScanAddKernel) ||
                !TryGetKernel(s_sortShader, Kernels.Sort.k_scatter, out s_sortScatterKernel) ||
                !TryGetKernel(s_compactShader, Kernels.Compact.k_main, out s_compactKernel) ||
                !TryGetKernel(s_setDrawArgsShader, Kernels.SetDrawArgs.k_main, out s_setDrawArgsKernel)
            )
            {
                DisposeResources();
                return false;
            }

            // create command buffers
            s_cullingCmdBuffer = new CommandBuffer
            {
                name = $"{nameof(InstancingManager)}_Culling",
            };
                
            // create constant buffers
            s_cullingProperties = new NativeArray<CullingPropertyBuffer>(
                1,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory
            );
            
            s_cullingConstantBuffer = new ComputeBuffer(
                s_cullingProperties.Length,
                CullingPropertyBuffer.k_size,
                ComputeBufferType.Constant
            )
            {
                name = $"{nameof(InstancingManager)}_{nameof(CullingPropertyBuffer)}",
            };

            s_sortingProperties = new NativeArray<SortingPropertyBuffer>(
                1,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory
            );
                
            s_sortingConstantBuffer = new ComputeBuffer(
                s_sortingProperties.Length,
                SortingPropertyBuffer.k_size,
                ComputeBufferType.Constant
            )
            {
                name = $"{nameof(InstancingManager)}_{nameof(SortingPropertyBuffer)}",
            };
            
            s_resourcesInitialized = true;
            return true;
        }

        static void DisposeResources()
        {
            RenderPipelineManager.beginFrameRendering -= OnBeginFrameRendering;
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;

            s_enabled = false;
            s_dirtyFlags = DirtyFlags.All;
            
            s_resourcesInitialized = false;

            // clean up compute shaders
            if (s_resources != null)
            {
                Resources.UnloadAsset(s_resources);
                s_resources = null;
            }

            s_cullShader = null;
            s_sortShader = null;
            s_compactShader = null;
            s_setDrawArgsShader = null;

            s_cullResetCountsKernel = default;
            s_cullKernel = default;
            s_sortCountKernel = default;
            s_sortCountReduceKernel = default;
            s_sortScanKernel = default;
            s_sortScanAddKernel = default;
            s_sortScatterKernel = default;
            s_compactKernel = default;
            s_setDrawArgsKernel = default;

            // dispose command buffers
            DisposeUtils.Dispose(ref s_cullingCmdBuffer);

            // dispose constant buffers
            DisposeUtils.Dispose(ref s_cullingProperties);
            DisposeUtils.Dispose(ref s_cullingConstantBuffer);
            DisposeUtils.Dispose(ref s_sortingProperties);
            DisposeUtils.Dispose(ref s_sortingConstantBuffer);

            // dispose compute buffers
            DisposeUtils.Dispose(ref s_lodDataBuffer);
            DisposeUtils.Dispose(ref s_animationDataBuffer);
            DisposeUtils.Dispose(ref s_drawArgsSrcBuffer);
            DisposeUtils.Dispose(ref s_instanceTypeDataBuffer);
            DisposeUtils.Dispose(ref s_instanceDataBuffer);

            DisposeUtils.Dispose(ref s_instanceCountsBuffer);
            DisposeUtils.Dispose(ref s_sortKeysInBuffer);
            DisposeUtils.Dispose(ref s_sortKeysOutBuffer);
            DisposeUtils.Dispose(ref s_sortScratchBuffer);
            DisposeUtils.Dispose(ref s_sortReducedScratchBuffer);
            DisposeUtils.Dispose(ref s_instancePropertiesBuffer);
            DisposeUtils.Dispose(ref s_drawArgsBuffer);

            s_lastInstanceBuffersLength = 0;
            
            // reset thread group sizes
            s_cullResetCountsThreadGroupCount = 0;
            s_cullThreadGroupCount = 0;
            s_sortThreadGroupCount = 0;
            s_sortReducedThreadGroupCount = 0;
            s_compactThreadGroupCount = 0;
            
            // dispose render passes
            s_drawArgsCount = 0;
            s_numInstanceCounts = 0;
            s_instanceCount = 0;

            // dispose main memory buffers
            s_providerIndexCount = 0;
            DisposeUtils.Dispose(ref s_providerIndices);
            DisposeUtils.Dispose(ref s_instanceData);
            
            // clear managed state
            s_drawCalls.Clear();
        }

        static void DisposeRegistrar()
        {
            s_providers.Clear();
            s_meshHandles.Clear();
            s_materialHandles.Clear();
            s_animationSetHandles.Clear();

            DisposeUtils.Dispose(ref s_providerStates);
            DisposeUtils.Dispose(ref s_meshToSubMeshDrawArgs);
            DisposeUtils.Dispose(ref s_animationSetToAnimations);
        }
        
        static bool IsSupported(out string reasons)
        {
            reasons = null;

            if (!SystemInfo.supportsComputeShaders)
            {
                reasons = (reasons ?? string.Empty) + "Compute shaders are not supported!\n";
            }
            if (!SystemInfo.supportsSetConstantBuffer)
            {
                reasons = (reasons ?? string.Empty) + "Set constant buffer is not supported!\n";
            }
            if (!SystemInfo.supportsInstancing)
            {
                reasons = (reasons ?? string.Empty) + "Instancing is not supported!\n";
            }

            return reasons == null;
        }

        static bool TryGetKernel(ComputeShader shader, string name, out KernelInfo kernel)
        {
            kernel = default;
            
            if (!shader.HasKernel(name))
            {
                Debug.LogError($"Kernel \"{name}\" not found in compute shader \"{shader.name}\"!");
                return false;
            }
            
            kernel.kernelID = shader.FindKernel(name);
            
            shader.GetKernelThreadGroupSizes(kernel.kernelID,  out var x, out var y, out var z);
            kernel.threadGroupSizeX = (int)x;
            kernel.threadGroupSizeY = (int)y;
            kernel.threadGroupSizeZ = (int)z;
            
            return true;
        }

        static int GetThreadGroupCount(int threads, int threadsPerGroup)
        {
            return ((threads - 1) / threadsPerGroup) + 1;
        }
    }
}

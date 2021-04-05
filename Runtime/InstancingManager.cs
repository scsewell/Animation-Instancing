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
    /// A class that manages the rendering of all instanced meshes. 
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
            public int instanceTypeIndex;
            public int drawCallCount;
            public int drawArgsBaseIndex;
            public int animationBaseIndex;
        }

        struct DrawCall : IEquatable<DrawCall>
        {
            public MeshHandle mesh;
            public int subMesh;
            public MaterialHandle material;
            public AnimationSetHandle animation;
            public int drawCallCount;

            public bool Equals(DrawCall other)
            {
                return mesh == other.mesh &&
                       subMesh == other.subMesh &&
                       material == other.material;
            }

            public override bool Equals(object obj)
            {
                return obj is DrawCall other && Equals(other);
            }

            public override int GetHashCode()
            {
                return (((mesh.GetHashCode() * 397) ^ material.GetHashCode()) * 397) ^ subMesh;
            }
        }
        
        static bool s_resourcesInitialized;
        static InstancingResources s_resources;
        static ComputeShader s_cullShader;
        static ComputeShader s_sortShader;
        static ComputeShader s_compactShader;
        static KernelInfo s_cullClearDrawArgsKernel;
        static KernelInfo s_cullKernel;
        static KernelInfo s_sortCountKernel;
        static KernelInfo s_sortCountReduceKernel;
        static KernelInfo s_sortScanKernel;
        static KernelInfo s_sortScanAddKernel;
        static KernelInfo s_sortScatterKernel;
        static KernelInfo s_compactKernel;

        static ComputeBuffer s_cullingConstantBuffer;
        static ComputeBuffer s_sortingConstantBuffer;
        static NativeArray<CullingPropertyBuffer> s_cullingProperties;
        
        static CommandBuffer s_cullingCmdBuffer;
        static CommandBuffer s_drawingCmdBuffer;
        
        static ComputeBuffer s_lodDataBuffer;
        static ComputeBuffer s_animationDataBuffer;
        static ComputeBuffer s_drawArgsBuffer;
        static ComputeBuffer s_drawCallCountsBuffer;
        static ComputeBuffer s_instanceDataBuffer;
        static ComputeBuffer s_sortKeysInBuffer;
        static ComputeBuffer s_sortKeysOutBuffer;
        static ComputeBuffer s_sortScratchBuffer;
        static ComputeBuffer s_sortReducedScratchBuffer;
        static ComputeBuffer s_instancePropertiesBuffer;
        static int s_cullClearDrawArgsThreadGroupCount;
        static int s_cullThreadGroupCount;
        static int s_sortThreadGroupCount;
        static int s_sortReducedThreadGroupCount;
        static int s_compactThreadGroupCount;

        static NativeArray<DrawArgs> s_drawArgs;
        static NativeArray<int> s_providerIndices;
        static NativeArray<InstanceData> s_instanceData;
        static int s_providerIndexCount;
        static int s_instanceCount;
        
        // TODO: move to native array, make "baked" copy that references instances
        static readonly List<DrawCall> s_drawCalls = new List<DrawCall>();
        
        static bool s_enabled;
        static DirtyFlags s_dirtyFlags;
        
        static readonly List<IInstanceProvider> s_providers = new List<IInstanceProvider>();
        static readonly HandleManager<MeshHandle, Mesh> s_meshHandles = new HandleManager<MeshHandle, Mesh>();
        static readonly HandleManager<MaterialHandle, Material> s_materialHandles = new HandleManager<MaterialHandle, Material>();
        static readonly HandleManager<AnimationSetHandle, InstancedAnimationSet> s_animationSetHandles = new HandleManager<AnimationSetHandle, InstancedAnimationSet>();
        
        static NativeList<ProviderState> s_providerStates;
        static NativeHashMap<MeshHandle, BlittableNativeArray<DrawArgs>> s_meshToSubMeshDrawArgs;
        static NativeHashMap<AnimationSetHandle, BlittableNativeArray<AnimationData>> s_animationSetToAnimations;

        /// <summary>
        /// Is the instance renderer enabled.
        /// </summary>
        public bool Enabled => s_enabled;

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
            s_meshToSubMeshDrawArgs = new NativeHashMap<MeshHandle, BlittableNativeArray<DrawArgs>>(0, Allocator.Persistent);
            s_animationSetToAnimations = new NativeHashMap<AnimationSetHandle, BlittableNativeArray<AnimationData>>(0, Allocator.Persistent);

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

            if (CreateResources())
            {
                s_enabled = true;
                s_dirtyFlags = DirtyFlags.All;
            }
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
        /// <param name="mesh">The mesh to register.</param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="mesh"/> is null.</exception>
        public static MeshHandle RegisterMesh(Mesh mesh)
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
        public static void DeregisterMesh(MeshHandle handle)
        {
            if (s_meshHandles.Deregister(handle))
            {
                s_meshToSubMeshDrawArgs.Remove(handle);
            }
        }

        /// <summary>
        /// Registers a material to the instance manager.
        /// </summary>
        /// <param name="material">The material to register.</param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="material"/> is null.</exception>
        public static MaterialHandle RegisterMaterial(Material material)
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
        public static void DeregisterMaterial(MaterialHandle handle)
        {
            s_materialHandles.Deregister(handle);
        }
        
        /// <summary>
        /// Registers an animation set to the instance manager.
        /// </summary>
        /// <param name="animationSet">The animation set to register.</param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="animationSet"/> is null.</exception>
        public static AnimationSetHandle RegisterAnimationSet(InstancedAnimationSet animationSet)
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
        public static void DeregisterAnimationSet(AnimationSetHandle handle)
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

            // Check which buffers need to be updated, if any.
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
            if (s_dirtyFlags.Intersects(DirtyFlags.Lods | DirtyFlags.Mesh | DirtyFlags.SubMeshes | DirtyFlags.Materials))
            {
                UpdateDrawArgsBuffers();
            }
            if (s_dirtyFlags.Intersects(DirtyFlags.Animation))
            {
                UpdateAnimationBuffers();
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

            // render the instances for each camera
            if (s_instanceData.Length == 0)
            {
                return;
            }

            //RenderPipelineManager.beginFrameRendering / beginCameraRendering
            Cull(Camera.main);
            Draw(Camera.main);
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
                Dispose(ref s_lodDataBuffer);
            
                s_lodDataBuffer = new ComputeBuffer(Mathf.NextPowerOfTwo(lodData.Length), LodData.k_size)
                {
                    name = $"{nameof(InstancingManager)}_{nameof(LodData)}",
                };
            }
            
            s_lodDataBuffer.SetData(lodData, 0, 0, lodData.Length);
            lodData.Dispose();
            
            Profiler.EndSample();
        }

        static void UpdateDrawArgsBuffers()
        {
            Profiler.BeginSample($"{nameof(InstancingManager)}.{nameof(UpdateDrawArgsBuffers)}");

            // Determine the number of individual draw calls required to render the instances.
            // Each mesh/sub mesh/material combination requires a separate draw call.
            var drawCallToDrawCallIndex = new NativeHashMap<DrawCall, int>(s_drawCalls.Count * 2, Allocator.Temp);

            s_drawCalls.Clear();

            // Find the ordering of the providers' instances in the instance data buffer. Instances using
            // the same mesh must be sequential in the buffer.
            var instanceTypeToIndex = new NativeHashMap<MeshHandle, int>(s_providerStates.Length, Allocator.Temp);
            var instanceTypeIndexToProviderIndices = new NativeMultiHashMap<int, int>(s_providerStates.Length, Allocator.Temp);

            for (var i = 0; i < s_providerStates.Length; i++)
            {
                var state = s_providerStates[i];
                var mesh = state.renderState.mesh;
                var subMeshes = (NativeSlice<SubMesh>)state.subMeshes;
                var lodCount = (int)state.renderState.lods.lodCount;

                if (!instanceTypeToIndex.TryGetValue(mesh, out var typeIndex))
                {
                    typeIndex = instanceTypeToIndex.Count();
                    instanceTypeToIndex.Add(mesh, typeIndex);
                }
                instanceTypeIndexToProviderIndices.Add(typeIndex, i);

                for (var j = 0; j < lodCount; j++)
                {
                    for (var k = 0; k < subMeshes.Length; k++)
                    {
                        var subMesh = subMeshes[k];
                        
                        var drawCall = new DrawCall
                        {
                            mesh = mesh,
                            subMesh = (subMeshes.Length * j) + subMesh.subMeshIndex,
                            material = subMesh.materialHandle,
                            animation = state.renderState.animationSet,
                            drawCallCount = subMeshes.Length,
                        };
                
                        if (!drawCallToDrawCallIndex.TryGetValue(drawCall, out var drawCallIndex))
                        {
                            // If we have not seen this parameter combination before, create a draw call 
                            drawCallIndex = drawCallToDrawCallIndex.Count();
                            drawCallToDrawCallIndex.Add(drawCall, drawCallIndex);

                            s_drawCalls.Add(drawCall);
                        }

                        // We need to track where we can update the instance count/offset in the draw args buffer
                        // instances from this provider from the culling/compact shader.
                        if (j == 0 && k == 0)
                        {
                            state.drawArgsBaseIndex = drawCallIndex;
                            state.instanceTypeIndex = typeIndex;
                        }
                    }
                }

                state.drawCallCount = subMeshes.Length;
                s_providerStates[i] = state;
            }

            drawCallToDrawCallIndex.Dispose();

            // flatten the provider index ordering map to an array for fast iteration
            s_providerIndexCount = instanceTypeIndexToProviderIndices.Count();
            
            if (!s_providerIndices.IsCreated || s_providerIndices.Length < s_providerIndexCount)
            {
                Dispose(ref s_providerIndices);
                
                s_providerIndices = new NativeArray<int>(
                    s_providerIndexCount,
                    Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory
                );
            }
            
            var currentProvider = 0;
            
            for (var i = 0; i < instanceTypeToIndex.Count(); i++)
            {
                foreach (var providerIndex in instanceTypeIndexToProviderIndices.GetValuesForKey(i))
                {
                    s_providerIndices[currentProvider] = providerIndex;
                    currentProvider++;
                }
            }

            instanceTypeToIndex.Dispose();
            instanceTypeIndexToProviderIndices.Dispose();
            
            // Allocate the draw args array. We upload this to the draw args compute buffer each frame to
            // reset the instance counts, so it must be persistent.
            if (!s_drawArgs.IsCreated || s_drawArgs.Length < s_drawCalls.Count)
            {
                Dispose(ref s_drawArgs);
                
                s_drawArgs = new NativeArray<DrawArgs>(
                    s_drawCalls.Count,
                    Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory
                );
            }

            var drawCallCounts = new NativeArray<uint>(
                s_drawCalls.Count,
                Allocator.Temp,
                NativeArrayOptions.UninitializedMemory
            );

            // get the draw args for each draw call
            for (var i = 0; i < s_drawCalls.Count; i++)
            {
                var drawCall = s_drawCalls[i];
                var subMeshDrawArgs = s_meshToSubMeshDrawArgs[drawCall.mesh];
                s_drawArgs[i] = subMeshDrawArgs[drawCall.subMesh];
                drawCallCounts[i] = (uint)drawCall.drawCallCount;
            }

            // create a new buffer if the previous one is too small
            if (s_drawArgsBuffer == null || s_drawArgsBuffer.count < s_drawCalls.Count)
            {
                Dispose(ref s_drawArgsBuffer);
                Dispose(ref s_drawCallCountsBuffer);

                var count = Mathf.NextPowerOfTwo(s_drawCalls.Count);
            
                s_drawArgsBuffer = new ComputeBuffer(count, DrawArgs.k_size, ComputeBufferType.IndirectArguments)
                {
                    name = $"{nameof(InstancingManager)}_{nameof(DrawArgs)}",
                };
                s_drawCallCountsBuffer = new ComputeBuffer(count, sizeof(uint))
                {
                    name = $"{nameof(InstancingManager)}_DrawCallCounts",
                };
            }
            
            s_drawArgsBuffer.SetData(s_drawArgs, 0, 0, s_drawCalls.Count);
            s_drawCallCountsBuffer.SetData(drawCallCounts, 0, 0, s_drawCalls.Count);

            drawCallCounts.Dispose();
            
            s_cullClearDrawArgsThreadGroupCount = GetThreadGroupCount(s_drawCalls.Count, s_cullClearDrawArgsKernel.threadGroupSizeX);
            
            Profiler.EndSample();
        }

        static void UpdateAnimationBuffers()
        {
            Profiler.BeginSample($"{nameof(InstancingManager)}.{nameof(UpdateAnimationBuffers)}");

            // Find all the animation sets that are currently required for rendering, and
            // determine the size required for the animation data buffer
            var animationSets = new NativeList<AnimationSetHandle>(s_providerStates.Length, Allocator.Temp);
            var animationSetsToBaseIndex = new NativeHashMap<AnimationSetHandle, int>(0, Allocator.Temp);
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

            // create a new buffer if the previous one is too small
            if (s_animationDataBuffer == null || s_animationDataBuffer.count < animationData.Length)
            {
                Dispose(ref s_animationDataBuffer);
            
                s_animationDataBuffer = new ComputeBuffer(Mathf.NextPowerOfTwo(animationData.Length), AnimationData.k_size)
                {
                    name = $"{nameof(InstancingManager)}_{nameof(AnimationData)}",
                };
            }
            
            s_animationDataBuffer.SetData(animationData, 0, 0, animationData.Length);
            animationData.Dispose();
            
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

            s_instanceCount = instanceCount;
            
            // find how many thread groups we should use for the culling shaders
            s_cullThreadGroupCount = GetThreadGroupCount(instanceCount, s_cullKernel.threadGroupSizeX);
            s_compactThreadGroupCount = GetThreadGroupCount(instanceCount, s_compactKernel.threadGroupSizeX);

            // determine the properties of the sorting pass needed for the current instance count
            var blockSize = Constants.k_sortElementsPerThread * s_sortCountKernel.threadGroupSizeX;
            var numBlocks = GetThreadGroupCount(instanceCount, blockSize);
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

            var sortingProperties = new NativeArray<SortingPropertyBuffer>(1, Allocator.Temp, NativeArrayOptions.UninitializedMemory)
            {
                [0] = new SortingPropertyBuffer
                {
                    _NumKeys = (uint)instanceCount,
                    _NumBlocksPerThreadGroup = blocksPerThreadGroup,
                    _NumThreadGroups = (uint)s_sortThreadGroupCount,
                    _NumThreadGroupsWithAdditionalBlocks = (uint)numThreadGroupsWithAdditionalBlocks,
                    _NumReduceThreadGroupPerBin = (uint)reducedThreadGroupCountPerBin,
                    _NumScanValues = (uint)s_sortReducedThreadGroupCount,
                },
            };
            
            s_sortingConstantBuffer.SetData(sortingProperties, 0, 0, sortingProperties.Length);
            sortingProperties.Dispose();

            var sortScratchBufferSize = Constants.k_sortBinCount * numBlocks;
            var sortReducedScratchBufferSize = Constants.k_sortBinCount * numReducedBlocks;

            // create new buffers if the previous ones are too small
            if (!s_instanceData.IsCreated || s_instanceData.Length < instanceCount)
            {
                Dispose(ref s_instanceData);
                
                s_instanceData = new NativeArray<InstanceData>(
                    instanceCount,
                    Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory
                );
            }
            
            if (s_instanceDataBuffer == null || s_instanceDataBuffer.count < instanceCount)
            {
                Dispose(ref s_instanceDataBuffer);
                Dispose(ref s_sortKeysInBuffer);
                Dispose(ref s_sortKeysOutBuffer);
                Dispose(ref s_instancePropertiesBuffer);

                var count = Mathf.NextPowerOfTwo(instanceCount);
                
                s_instanceDataBuffer = new ComputeBuffer(count, InstanceData.k_size)
                {
                    name = $"{nameof(InstancingManager)}_{nameof(InstanceData)}",
                };
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
            }

            if (s_sortScratchBuffer == null || s_sortScratchBuffer.count < sortScratchBufferSize)
            {
                Dispose(ref s_sortScratchBuffer);
                
                var count = Mathf.NextPowerOfTwo(sortScratchBufferSize);

                s_sortScratchBuffer = new ComputeBuffer(count, sizeof(uint))
                {
                    name = $"{nameof(InstancingManager)}_SortScratchBuffer",
                };
            }
            
            if (s_sortReducedScratchBuffer == null || s_sortReducedScratchBuffer.count < sortReducedScratchBufferSize)
            {
                Dispose(ref s_sortReducedScratchBuffer);
                
                var count = Mathf.NextPowerOfTwo(sortReducedScratchBufferSize);

                s_sortReducedScratchBuffer = new ComputeBuffer(count, sizeof(uint))
                {
                    name = $"{nameof(InstancingManager)}_SortReducedScratchBuffer",
                };
            }
            
            Profiler.EndSample();
        }

        [BurstCompile]
        struct UpdateInstancesJob : IJobParallelFor
        {
            [ReadOnly, NoAlias]
            public NativeSlice<Instance> instances;
            public ProviderState providerState;
            public int instanceStart;
            
            [WriteOnly, NoAlias, NativeDisableContainerSafetyRestriction]
            public NativeArray<InstanceData> instanceData;

            /// <inheritdoc />
            public void Execute(int i)
            {
                var instance = instances[i];

                instanceData[instanceStart + i] = new InstanceData
                {
                    transform = instance.transform,
                    
                    lodIndex = (uint)providerState.lodIndex,
                    instanceTypeIndex = (uint)providerState.instanceTypeIndex,
                    drawCallCount = (uint)providerState.drawCallCount,
                    drawArgsBaseIndex = (uint)providerState.drawArgsBaseIndex,
                    animationBaseIndex = (uint)providerState.animationBaseIndex,
                    
                    animationIndex = (uint)instance.animationIndex,
                    animationTime = instance.animationTime,
                };
            }
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
            
            for (var i = 0; i < s_providerIndexCount; i++)
            {
                var providerIndex = s_providerIndices[i];
                var state = s_providerStates[providerIndex];
                var instances = (NativeSlice<Instance>)state.instances;
                
                if (forceUpdate || state.dirtyFlags.Intersects(DirtyFlags.PerInstanceData))
                {
                    var job = new UpdateInstancesJob
                    {
                        instances = instances,
                        providerState = state,
                        instanceStart = currentInstances,
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
        
        static void Cull(Camera cam)
        {
            Profiler.BeginSample($"{nameof(InstancingManager)}.{nameof(Cull)}");

            var vFov = cam.fieldOfView;
            var hFov = Camera.VerticalToHorizontalFieldOfView(vFov, cam.aspect);
            var fov = math.max(vFov, hFov);

            s_cullingProperties[0] = new CullingPropertyBuffer
            {
                _ViewProj = cam.projectionMatrix * cam.worldToCameraMatrix,
                _CameraPosition = cam.transform.position,
                _LodBias = QualitySettings.lodBias,
                _LodScale = 1f / (2f * math.tan(math.radians(fov / 2f))),
                _InstanceCount = s_instanceCount,
                _DrawArgsCount = s_drawCalls.Count,
            };

            s_cullingCmdBuffer.Clear();
            
            // initialize buffers
            s_cullingCmdBuffer.SetBufferData(
                s_cullingConstantBuffer,
                s_cullingProperties,
                0,
                0,
                s_cullingProperties.Length
            );
            
            s_cullingCmdBuffer.SetComputeConstantBufferParam(
                s_cullShader,
                Properties.Culling._ConstantBuffer,
                s_cullingConstantBuffer,
                0,
                CullingPropertyBuffer.k_size
            );
            s_cullingCmdBuffer.SetComputeBufferParam(
                s_cullShader,
                s_cullClearDrawArgsKernel.kernelID,
                Properties.Culling._DrawArgs,
                s_drawArgsBuffer
            );
            
            s_cullingCmdBuffer.DispatchCompute(
                s_cullShader,
                s_cullClearDrawArgsKernel.kernelID, 
                s_cullClearDrawArgsThreadGroupCount, 1, 1
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
                Properties.Culling._DrawArgs,
                s_drawArgsBuffer
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

                var maxReducedThreadGroupCount = Constants.k_sortElementsPerThread * s_sortScanKernel.threadGroupSizeX;
                Debug.Assert(s_sortReducedThreadGroupCount < maxReducedThreadGroupCount, "Need to account for bigger reduced histogram scan!");

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
                Properties.Compact._DrawCallCounts,
                s_drawCallCountsBuffer
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
                Properties.Compact._DrawArgs,
                s_drawArgsBuffer
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
            
            Graphics.ExecuteCommandBuffer(s_cullingCmdBuffer);
            
            Profiler.EndSample();
        }

        static void Draw(Camera cam)
        {
            Profiler.BeginSample($"{nameof(InstancingManager)}.{nameof(Draw)}");

            s_cullingCmdBuffer.Clear();

            var bounds = new Bounds(Vector3.zero, float.MaxValue * Vector3.one);
            
            var props = new MaterialPropertyBlock();
            props.SetBuffer(Properties.Main._DrawArgs, s_drawArgsBuffer);
            props.SetBuffer(Properties.Main._AnimationData, s_animationDataBuffer);
            props.SetBuffer(Properties.Main._InstanceProperties, s_instancePropertiesBuffer);
            
            for (var i = 0; i < s_drawCalls.Count; i++)
            {
                var drawCall = s_drawCalls[i];
                var mesh = s_meshHandles.GetInstance(drawCall.mesh);
                var material = s_materialHandles.GetInstance(drawCall.material);
                var animationSet = s_animationSetHandles.GetInstance(drawCall.animation);
                
                props.SetInt(Properties.Main._DrawArgsOffset, i);
                props.SetTexture(Properties.Main._Animation, animationSet.Texture);
                
                Graphics.DrawMeshInstancedIndirect(
                    mesh,
                    drawCall.subMesh,
                    material,
                    bounds,
                    s_drawArgsBuffer,
                    i * DrawArgs.k_size,
                    props,
                    ShadowCastingMode.Off
                );
            }

            Profiler.EndSample();
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

            if (s_cullShader == null || s_sortShader == null || s_compactShader == null)
            {
                Debug.LogError("Required compute shaders have not been assigned to the instancing resources asset!");
                DisposeResources();
                return false;
            }

            if (!TryGetKernel(s_cullShader, Kernels.Culling.k_clearDrawArgs, out s_cullClearDrawArgsKernel) ||
                !TryGetKernel(s_cullShader, Kernels.Culling.k_cull, out s_cullKernel) ||
                !TryGetKernel(s_sortShader, Kernels.Sort.k_count, out s_sortCountKernel) ||
                !TryGetKernel(s_sortShader, Kernels.Sort.k_countReduce, out s_sortCountReduceKernel) ||
                !TryGetKernel(s_sortShader, Kernels.Sort.k_scan, out s_sortScanKernel) ||
                !TryGetKernel(s_sortShader, Kernels.Sort.k_scanAdd, out s_sortScanAddKernel) ||
                !TryGetKernel(s_sortShader, Kernels.Sort.k_scatter, out s_sortScatterKernel) ||
                !TryGetKernel(s_compactShader, Kernels.Compact.k_main, out s_compactKernel))
            {
                DisposeResources();
                return false;
            }

            // create the command buffers
            s_cullingCmdBuffer = new CommandBuffer
            {
                name = $"{nameof(InstancingManager)}_InstanceCulling",
            };
            s_drawingCmdBuffer = new CommandBuffer
            {
                name = $"{nameof(InstancingManager)}_InstanceDrawing",
            };

            // create constant buffers
            s_cullingProperties = new NativeArray<CullingPropertyBuffer>(1, Allocator.Persistent);
            
            s_cullingConstantBuffer = new ComputeBuffer(
                s_cullingProperties.Length,
                CullingPropertyBuffer.k_size,
                ComputeBufferType.Constant
            )
            {
                name = $"{nameof(InstancingManager)}_{nameof(CullingPropertyBuffer)}",
            };
            
            s_sortingConstantBuffer = new ComputeBuffer(
                1,
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

            s_cullClearDrawArgsKernel = default;
            s_cullKernel = default;
            s_sortCountKernel = default;
            s_sortCountReduceKernel = default;
            s_sortScanKernel = default;
            s_sortScanAddKernel = default;
            s_sortScatterKernel = default;
            s_compactKernel = default;

            // dispose command buffers
            Dispose(ref s_cullingCmdBuffer);
            Dispose(ref s_drawingCmdBuffer);

            // dispose constant buffers
            Dispose(ref s_cullingProperties);
            
            Dispose(ref s_cullingConstantBuffer);
            Dispose(ref s_sortingConstantBuffer);

            // dispose compute buffers
            Dispose(ref s_lodDataBuffer);
            Dispose(ref s_animationDataBuffer);
            Dispose(ref s_drawArgsBuffer);
            Dispose(ref s_drawCallCountsBuffer);
            Dispose(ref s_instanceDataBuffer);
            Dispose(ref s_sortKeysInBuffer);
            Dispose(ref s_sortKeysOutBuffer);
            Dispose(ref s_sortScratchBuffer);
            Dispose(ref s_sortReducedScratchBuffer);
            Dispose(ref s_instancePropertiesBuffer);
            
            s_cullClearDrawArgsThreadGroupCount = 0;
            s_cullThreadGroupCount = 0;
            s_sortThreadGroupCount = 0;
            s_sortReducedThreadGroupCount = 0;
            s_compactThreadGroupCount = 0;
            
            // dispose main memory buffers
            Dispose(ref s_drawArgs);
            Dispose(ref s_providerIndices);
            Dispose(ref s_instanceData);
            s_providerIndexCount = 0;
            s_instanceCount = 0;
            
            // clear managed state
            s_drawCalls.Clear();
        }

        static void DisposeRegistrar()
        {
            s_providers.Clear();
            s_meshHandles.Clear();
            s_materialHandles.Clear();
            s_animationSetHandles.Clear();

            Dispose(ref s_providerStates);
            Dispose(ref s_meshToSubMeshDrawArgs);
            Dispose(ref s_animationSetToAnimations);
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

        static void Dispose(ref CommandBuffer buffer)
        {
            if (buffer != null)
            {
                buffer.Release();
                buffer = null;
            }
        }

        static void Dispose(ref ComputeBuffer buffer)
        {
            if (buffer != null)
            {
                buffer.Release();
                buffer = null;
            }
        }

        static void Dispose<T>(ref NativeArray<T> buffer) where T : struct
        {
            if (buffer.IsCreated)
            {
                buffer.Dispose();
                buffer = default;
            }
        }

        static void Dispose<T>(ref NativeList<T> buffer) where T : struct
        {
            if (buffer.IsCreated)
            {
                buffer.Dispose();
                buffer = default;
            }
        }

        static void Dispose<TKey, TValue>(ref NativeHashMap<TKey, BlittableNativeArray<TValue>> map)
            where TKey : struct, IEquatable<TKey>
            where TValue : unmanaged
        {
            if (map.IsCreated)
            {
                foreach (var keyValue in map)
                {
                    keyValue.Value.Dispose();
                }
                
                map.Dispose();
                map = default;
            }
        }
    }
}

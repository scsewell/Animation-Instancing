using System;
using System.Collections.Generic;

using Unity.Jobs;
using Unity.Burst;
using Unity.Collections;
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
        struct ProviderState
        {
            public DirtyFlags dirtyFlags;
            public RenderState renderState;
            public NestableNativeSlice<SubMesh> subMeshes;
            public NestableNativeSlice<Instance> instances;
            public int lodIndex;
            public int drawCallCount;
            public int drawArgsBaseIndex;
            public int animationBaseIndex;
        }

        struct DrawCall : IEquatable<DrawCall>
        {
            public MeshHandle mesh;
            public int subMesh;
            public MaterialHandle material;

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
        //
        // struct DrawCallA : IEquatable<DrawCallA>
        // {
        //     public Mesh mesh;
        //     public int subMesh;
        //     public Material material;
        //
        //     public bool Equals(DrawCallA other)
        //     {
        //         return mesh == other.mesh &&
        //                subMesh == other.subMesh &&
        //                material == other.material;
        //     }
        //
        //     public override bool Equals(object obj)
        //     {
        //         return obj is DrawCallA other && Equals(other);
        //     }
        //
        //     public override int GetHashCode()
        //     {
        //         return (((mesh.GetHashCode() * 397) ^ material.GetHashCode()) * 397) ^ subMesh;
        //     }
        // }
        
        static bool s_resourcesInitialized;
        static InstancingResources s_resources;
        static ComputeShader s_cullingShader;
        static ComputeShader s_scanShader;
        static ComputeShader s_compactShader;
        static int s_cullingKernel;
        static int s_scanInBucketKernel;
        static int s_scanAcrossBucketsKernel;
        static int s_compactKernel;

        static ComputeBuffer s_cullingConstantBuffer;
        static NativeArray<CullingPropertyBuffer> s_cullingProperties;
        
        static CommandBuffer s_cullingCmdBuffer;
        static ComputeBuffer s_lodDataBuffer;
        static ComputeBuffer s_animationDataBuffer;
        static ComputeBuffer s_drawArgsBuffer;
        static ComputeBuffer s_instanceDataBuffer;
        static ComputeBuffer s_isVisibleBuffer;
        static ComputeBuffer s_isVisibleScanInBucketBuffer;
        static ComputeBuffer s_isVisibleScanAcrossBucketsBuffer;
        static ComputeBuffer s_instancePropertiesBuffer;
        
        static int s_cullingThreadGroupCount;
        static int s_scanInBucketThreadGroupCount;
        static int s_compactThreadGroupCount;

        static NativeArray<DrawArgs> s_drawArgs;
        static NativeArray<int> s_providerIndices;
        static NativeArray<InstanceData> s_instanceData;

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

        static void Cull(Camera cam)
        {
            Profiler.BeginSample($"{nameof(InstancingManager)}.{nameof(Cull)}");

            s_cullingCmdBuffer.Clear();
            
            // reset buffers
            s_cullingCmdBuffer.SetBufferData(s_drawArgsBuffer, s_drawArgs);

            // culling and lod selection
            var vFov = cam.fieldOfView;
            var hFov = Camera.VerticalToHorizontalFieldOfView(vFov, cam.aspect);
            var fov = math.max(vFov, hFov);

            s_cullingProperties[0] = new CullingPropertyBuffer
            {
                _ViewProj = cam.projectionMatrix * cam.worldToCameraMatrix,
                _CameraPosition = cam.transform.position,
                _LodBias = QualitySettings.lodBias,
                _LodScale = 1f / (2f * math.tan(math.radians(fov / 2f))),
                _ScanBucketCount = s_scanInBucketThreadGroupCount,
            };
            
            s_cullingCmdBuffer.SetBufferData(s_cullingConstantBuffer, s_cullingProperties);
            s_cullingCmdBuffer.SetComputeConstantBufferParam(
                s_cullingShader,
                Properties._CullingPropertyBuffer,
                s_cullingConstantBuffer,
                0,
                CullingPropertyBuffer.k_size
            );
            s_cullingCmdBuffer.DispatchCompute(
                s_cullingShader,
                s_cullingKernel, 
                s_cullingThreadGroupCount, 1, 1
            );
            
            // scan
            s_cullingCmdBuffer.SetComputeBufferParam(s_scanShader, s_scanInBucketKernel, Properties._ScanIn, s_isVisibleBuffer);
            s_cullingCmdBuffer.SetComputeBufferParam(s_scanShader, s_scanInBucketKernel, Properties._ScanIntermediate, s_isVisibleScanAcrossBucketsBuffer);
            s_cullingCmdBuffer.SetComputeBufferParam(s_scanShader, s_scanInBucketKernel, Properties._ScanOut, s_isVisibleScanInBucketBuffer);
            
            s_cullingCmdBuffer.DispatchCompute(
                s_scanShader,
                s_scanInBucketKernel, 
                s_scanInBucketThreadGroupCount, 1, 1
            );
            
            s_cullingCmdBuffer.SetComputeBufferParam(s_scanShader, s_scanAcrossBucketsKernel, Properties._ScanIn, s_isVisibleScanAcrossBucketsBuffer);
            s_cullingCmdBuffer.SetComputeBufferParam(s_scanShader, s_scanAcrossBucketsKernel, Properties._ScanOut, );

            s_cullingCmdBuffer.DispatchCompute(
                s_scanShader,
                s_scanAcrossBucketsKernel, 
                1, 1, 1
            );
            
            // compact
            
            Graphics.ExecuteCommandBuffer(s_cullingCmdBuffer);
            
            Profiler.EndSample();
        }

        static void Draw(Camera cam)
        {
            Profiler.BeginSample($"{nameof(InstancingManager)}.{nameof(Draw)}");

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

            // Find the ordering of the providers' instances in the instance data buffer. Instances
            // Using the same mesh must be sequential in the buffer.
            var meshToIndex = new NativeHashMap<MeshHandle, int>(s_providerStates.Length, Allocator.Temp);
            var meshIndexToProviderIndices = new NativeMultiHashMap<int, int>(s_providerStates.Length, Allocator.Temp);

            for (var i = 0; i < s_providerStates.Length; i++)
            {
                var state = s_providerStates[i];
                var mesh = state.renderState.mesh;
                var subMeshes = (NativeSlice<SubMesh>)state.subMeshes;
                var lodCount = (int)state.renderState.lods.lodCount;

                if (!meshToIndex.TryGetValue(mesh, out var meshIndex))
                {
                    meshIndex = meshToIndex.Count();
                    meshToIndex.Add(mesh, meshIndex);
                }
                meshIndexToProviderIndices.Add(meshIndex, i);

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
                        }
                    }
                }

                state.drawCallCount = subMeshes.Length;
                s_providerStates[i] = state;
            }

            drawCallToDrawCallIndex.Dispose();

            // flatten the provider index ordering map to an array for fast iteration
            var providerIndexCount = meshIndexToProviderIndices.Count();
            
            if (!s_providerIndices.IsCreated || s_providerIndices.Length < providerIndexCount)
            {
                Dispose(ref s_providerIndices);
                
                s_providerIndices = new NativeArray<int>(
                    providerIndexCount,
                    Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory
                );
            }
            
            var currentProvider = 0;
            
            for (var i = 0; i < meshToIndex.Count(); i++)
            {
                foreach (var providerIndex in meshIndexToProviderIndices.GetValuesForKey(i))
                {
                    s_providerIndices[currentProvider] = providerIndex;
                    currentProvider++;
                }
            }

            meshToIndex.Dispose();
            meshIndexToProviderIndices.Dispose();
            
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

            // get the draw args for each draw call
            for (var i = 0; i < s_drawCalls.Count; i++)
            {
                var drawCall = s_drawCalls[i];
                var subMeshDrawArgs = s_meshToSubMeshDrawArgs[drawCall.mesh];
                s_drawArgs[i] = subMeshDrawArgs[drawCall.subMesh];
            }

            // create a new buffer if the previous one is too small
            if (s_drawArgsBuffer == null || s_drawArgsBuffer.count < s_drawCalls.Count)
            {
                Dispose(ref s_drawArgsBuffer);
            
                s_drawArgsBuffer = new ComputeBuffer(
                    Mathf.NextPowerOfTwo(s_drawCalls.Count),
                    DrawArgs.k_size,
                    ComputeBufferType.IndirectArguments
                )
                {
                    name = $"{nameof(InstancingManager)}_{nameof(DrawArgs)}",
                };
            }

            s_drawArgsBuffer.SetData(s_drawArgs, 0, 0, s_drawCalls.Count);

            Profiler.EndSample();
        }

        static void UpdateAnimationBuffers()
        {
            Profiler.BeginSample($"{nameof(InstancingManager)}.{nameof(UpdateAnimationBuffers)}");

            // Find all the animation sets that are currently required for rendering, and
            // determine the size required for the animation data buffer
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
            var animationSets = animationSetsToBaseIndex.GetKeyArray(Allocator.Temp);
            
            for (var i = 0; i < animationSets.Length; i++)
            {
                var animationSet = animationSets[i];
                var animations = s_animationSetToAnimations[animationSet];

                for (var j = 0; j < animations.Length; j++)
                {
                    animationData[animationIndex] = animations[i];
                    animationIndex++;
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
            
            // find how many buckets we need in the scan rounded up
            s_cullingThreadGroupCount = CeilDivide(instanceCount, Constants.k_cullingThreadsPerGroup);
            s_scanInBucketThreadGroupCount = CeilDivide(instanceCount, Constants.k_scanBucketSize);
            s_compactThreadGroupCount = CeilDivide(instanceCount, Constants.k_compactThreadsPerGroup);

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
                Dispose(ref s_isVisibleBuffer);
                Dispose(ref s_isVisibleScanInBucketBuffer);
                Dispose(ref s_instancePropertiesBuffer);

                var count = Mathf.NextPowerOfTwo(instanceCount);
                
                s_instanceDataBuffer = new ComputeBuffer(
                    count,
                    InstanceData.k_size,
                    ComputeBufferType.Default,
                    ComputeBufferMode.Dynamic
                )
                {
                    name = $"{nameof(InstancingManager)}_{nameof(InstanceData)}",
                };
                s_isVisibleBuffer = new ComputeBuffer(count, sizeof(uint))
                {
                    name = $"{nameof(InstancingManager)}_IsVisible",
                };
                s_isVisibleScanInBucketBuffer = new ComputeBuffer(count, sizeof(uint))
                {
                    name = $"{nameof(InstancingManager)}_isVisibleScanInBucket",
                };
                s_instancePropertiesBuffer = new ComputeBuffer(count, InstanceProperties.k_size)
                {
                    name = $"{nameof(InstancingManager)}_{nameof(InstanceProperties)}",
                };
            }

            if (s_isVisibleScanAcrossBucketsBuffer == null || s_isVisibleScanAcrossBucketsBuffer.count < s_scanInBucketThreadGroupCount)
            {
                Dispose(ref s_isVisibleScanAcrossBucketsBuffer);
                
                var count = Mathf.NextPowerOfTwo(s_scanInBucketThreadGroupCount);

                s_isVisibleScanAcrossBucketsBuffer = new ComputeBuffer(count, sizeof(uint))
                {
                    name = $"{nameof(InstancingManager)}_isVisibleScanAcrossBuckets",
                };
            }
            
            Profiler.EndSample();
        }

        static int CeilDivide(int x, int y)
        {
            return ((x - 1) / y) + 1;
        }

        [BurstCompile(DisableSafetyChecks = true)]
        struct UpdateInstancesJob : IJobParallelFor
        {
            [ReadOnly, NoAlias]
            public NativeSlice<Instance> instances;
            public ProviderState providerState;
            public int instanceStart;
            
            [WriteOnly, NoAlias]
            public NativeArray<InstanceData> instanceData;

            /// <inheritdoc />
            public void Execute(int i)
            {
                var instance = instances[i];

                instanceData[instanceStart + i] = new InstanceData
                {
                    position = instance.transform.position,
                    rotation = instance.transform.rotation,
                    scale = instance.transform.scale,
                    
                    lodIndex = (uint)providerState.lodIndex,
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
            
            for (var i = 0; i < s_providerIndices.Length; i++)
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
            s_instanceDataBuffer.SetData(s_instanceData, 0, 0, s_instanceData.Length);
            
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

            s_cullingShader = s_resources.Culling;
            s_scanShader = s_resources.Scan;
            s_compactShader = s_resources.Compact;

            if (s_cullingShader == null || s_scanShader == null || s_compactShader == null)
            {
                Debug.LogError("Required compute shaders have not been assigned to the instancing resources asset!");
                DisposeResources();
                return false;
            }

            if (!TryGetKernel(s_cullingShader, Kernels.k_cullingKernel, ref s_cullingKernel) ||
                !TryGetKernel(s_scanShader, Kernels.k_scanInBucketKernel, ref s_scanInBucketKernel) ||
                !TryGetKernel(s_scanShader, Kernels.k_scanAcrossBucketsKernel, ref s_scanAcrossBucketsKernel) ||
                !TryGetKernel(s_compactShader, Kernels.k_compactKernel, ref s_compactKernel))
            {
                DisposeResources();
                return false;
            }

            // create the command buffers
            s_cullingCmdBuffer = new CommandBuffer
            {
                name = $"{nameof(InstancingManager)}_InstanceCulling",
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

            s_cullingShader = null;
            s_scanShader = null;
            s_compactShader = null;

            s_cullingKernel = 0;
            s_scanInBucketKernel = 0;
            s_scanAcrossBucketsKernel = 0;
            s_compactKernel = 0;

            // dispose command buffers
            Dispose(ref s_cullingCmdBuffer);

            // dispose constant buffers
            Dispose(ref s_cullingProperties);
            Dispose(ref s_cullingConstantBuffer);

            // dispose compute buffers
            Dispose(ref s_lodDataBuffer);
            Dispose(ref s_animationDataBuffer);
            Dispose(ref s_drawArgsBuffer);
            Dispose(ref s_instanceDataBuffer);
            Dispose(ref s_isVisibleBuffer);
            Dispose(ref s_isVisibleScanInBucketBuffer);
            Dispose(ref s_isVisibleScanAcrossBucketsBuffer);
            Dispose(ref s_instancePropertiesBuffer);
            
            s_cullingThreadGroupCount = 0;
            s_scanInBucketThreadGroupCount = 0;
            s_compactThreadGroupCount = 0;
            
            // dispose main memory buffers
            Dispose(ref s_drawArgs);
            Dispose(ref s_providerIndices);
            Dispose(ref s_instanceData);
            
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

        static bool TryGetKernel(ComputeShader shader, string name, ref int kernelID)
        {
            if (!shader.HasKernel(name))
            {
                Debug.LogError($"Kernel \"{name}\" not found in compute shader \"{shader.name}\"!");
                return false;
            }

            kernelID = shader.FindKernel(name);
            return true;
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

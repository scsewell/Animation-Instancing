using System;
using System.Collections.Generic;

using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

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
            public int lodIndex;
            public int drawCallCount;
            public int drawArgsBaseIndex;
            public int animationBaseIndex;
        }

        struct DrawCall : IDisposable, IEquatable<DrawCall>
        {
            public Mesh mesh;
            public int subMesh;
            public Material material;
            public NativeList<int> providerIndices;

            public void Dispose()
            {
                providerIndices.Dispose();
            }
            
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
        static ComputeShader s_cullingShader;
        static ComputeShader s_scanShader;
        static ComputeShader s_compactShader;
        static int s_cullingKernel;
        static int s_scanInBucketKernel;
        static int s_scanAcrossBucketsKernel;
        static int s_compactKernel;

        static CommandBuffer s_cullingCmdBuffer;
        static ComputeBuffer s_lodDataBuffer;
        static ComputeBuffer s_animationDataBuffer;
        static ComputeBuffer s_drawArgsBuffer;
        static ComputeBuffer s_instanceDataBuffer;
        static ComputeBuffer s_isVisibleBuffer;
        static ComputeBuffer s_isVisibleScanInBucketBuffer;
        static ComputeBuffer s_isVisibleScanAcrossBucketsBuffer;
        static ComputeBuffer s_instancePropertiesBuffer;

        static NativeArray<DrawArgs> s_drawArgs;
        static NativeArray<int> s_providerIndices;
        static NativeArray<InstanceData> s_instanceData;
        
        static readonly List<DrawCall> s_drawCalls = new List<DrawCall>();
        
        static bool s_enabled;
        static DirtyFlags s_dirtyFlags;
        
        static readonly List<IInstanceProvider> s_providers = new List<IInstanceProvider>();
        static NativeList<ProviderState> s_providerStates;
        
        static readonly Dictionary<InstancedAnimationSet, int> s_tempAnimationSetToBaseIndex = new Dictionary<InstancedAnimationSet, int>();
        static readonly List<InstancedAnimationSet> s_tempAnimationSets = new List<InstancedAnimationSet>();

        /// <summary>
        /// Is the instance renderer enabled.
        /// </summary>
        public bool Enabled => s_enabled;

        struct AnimationInstancingUpdate
        {
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Init()
        {
            // In case the domain reload on enter play mode is disabled, we must
            // reset all static fields.
            DisposeResources();

            s_providers.Clear();
            Dispose(ref s_providerStates);
            
            s_tempAnimationSetToBaseIndex.Clear();
            s_tempAnimationSets.Clear();

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
            }
        }

        /// <summary>
        /// Disable the instanced renderer.
        /// </summary>
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
        public void RegisterInstanceProvider(IInstanceProvider provider)
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
        public void DeregisterInstanceProvider(IInstanceProvider provider)
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

        static void Update()
        {
            if (!s_enabled)
            {
                return;
            }

            // Count the total number of instances to render this frame while
            // checking which buffers need to be updated, if any.
            for (var i = 0; i < s_providers.Count; i++)
            {
                var provider = s_providers[i];
                var state = s_providerStates[i];
                var dirtyFlags = provider.DirtyFlags;

                state.dirtyFlags = dirtyFlags;
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
            var lodData = new NativeArray<LodData>(s_providers.Count, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            
            for (var i = 0; i < s_providers.Count; i++)
            {
                var provider = s_providers[i];
                var state = s_providerStates[i];

                lodData[i] = provider.Mesh.Lods;
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

        struct InstanceType : IEquatable<InstanceType>
        {
            public Mesh mesh;

            public bool Equals(InstanceType other)
            {
                return mesh == other.mesh;
            }

            public override bool Equals(object obj)
            {
                return obj is InstanceType other && Equals(other);
            }

            public override int GetHashCode()
            {
                return mesh.GetHashCode();
            }
        }

        static void UpdateDrawArgsBuffers()
        {
            Profiler.BeginSample($"{nameof(InstancingManager)}.{nameof(UpdateDrawArgsBuffers)}");

            // Determine the number of individual draw calls required to render the instances.
            // Each mesh/sub mesh/material combination requires a separate draw call.
            var drawCallToDrawCallIndex = new NativeHashMap<DrawCall, int>(s_drawCalls.Count * 2, Allocator.Temp);

            Clear(s_drawCalls);

            // Find the ordering of the providers' instances in the instance data buffer. Instances
            // Using the same mesh must be sequential in the buffer.
            var instanceTypeToIndex = new NativeHashMap<InstanceType, int>(0, Allocator.Temp);
            var instanceTypeIndexToProviderIndices = new NativeMultiHashMap<int, int>(0, Allocator.Temp);

            for (var i = 0; i < s_providers.Count; i++)
            {
                var provider = s_providers[i];
                var state = s_providerStates[i];
                var drawCallCount = provider.GetDrawCallCount();
                var mesh = provider.Mesh;
                var lodCount = (int)mesh.Lods.lodCount;

                var instanceType = new InstanceType
                {
                    mesh = mesh.Mesh,
                };
                
                if (!instanceTypeToIndex.TryGetValue(instanceType, out var instanceTypeIndex))
                {
                    instanceTypeIndex = instanceTypeToIndex.Count();
                    instanceTypeToIndex.Add(instanceType, instanceTypeIndex);
                }
                instanceTypeIndexToProviderIndices.Add(instanceTypeIndex, i);

                for (var j = 0; j < drawCallCount; j++)
                {
                    if (!provider.TryGetDrawCall(j, out var subMesh, out var material))
                    {
                        continue;
                    }
                    
                    for (var k = 0; k < lodCount; k++)
                    {
                        var drawCall = new DrawCall
                        {
                            mesh = mesh.Mesh,
                            subMesh = (subMesh * lodCount) + k,
                            material = material,
                        };
                
                        if (!drawCallToDrawCallIndex.TryGetValue(drawCall, out var drawCallIndex))
                        {
                            // If we have not seen this parameter combination before, create a draw call 
                            drawCallIndex = drawCallToDrawCallIndex.Count();
                            drawCallToDrawCallIndex.Add(drawCall, drawCallIndex);

                            drawCall.providerIndices = new NativeList<int>(8, Allocator.Persistent);
                            drawCall.providerIndices.Add(i);
                            s_drawCalls.Add(drawCall);
                        }
                        else
                        {
                            // If we have seen this draw call, we need to update the list of providers
                            // that use this draw call to include the current provider.
                            drawCall = s_drawCalls[drawCallIndex];
                            drawCall.providerIndices.Add(i);
                            s_drawCalls[drawCallIndex] = drawCall;
                        }

                        // We need to know where in the draw args buffer instances from this provider
                        // can update their instance count.
                        if (j == 0 && k == 0)
                        {
                            state.drawArgsBaseIndex = drawCallIndex;
                        }
                    }
                }

                state.drawCallCount = drawCallCount;
                s_providerStates[i] = state;
            }

            drawCallToDrawCallIndex.Dispose();
            
            // flatten the provider index ordering map to an array for fast iteration
            var providerIndexCount = instanceTypeIndexToProviderIndices.Count();
            
            if (!s_providerIndices.IsCreated || s_providerIndices.Length < providerIndexCount)
            {
                Dispose(ref s_providerIndices);
                
                s_providerIndices = new NativeArray<int>(providerIndexCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
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

            instanceTypeToIndex.Dispose();;
            instanceTypeIndexToProviderIndices.Dispose();
            
            // Allocate the draw args array. We upload this to the draw args compute buffer each frame to
            // reset the instance counts, so it must be persistent.
            if (!s_drawArgs.IsCreated || s_drawArgs.Length < s_drawCalls.Count)
            {
                Dispose(ref s_drawArgs);
                
                s_drawArgs = new NativeArray<DrawArgs>(s_drawCalls.Count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }

            // get the draw args for each draw call
            for (var i = 0; i < s_drawCalls.Count; i++)
            {
                var drawCall = s_drawCalls[i];
                var mesh = drawCall.mesh;
                var subMesh = drawCall.subMesh;
                
                s_drawArgs[i] = new DrawArgs
                {
                    indexCount = mesh.GetIndexCount(subMesh),
                    instanceCount = 0,
                    indexStart = mesh.GetIndexStart(subMesh),
                    baseVertex = mesh.GetBaseVertex(subMesh),
                    instanceStart = 0,
                };
            }

            // create a new buffer if the previous one is too small
            if (s_drawArgsBuffer == null || s_drawArgsBuffer.count < s_drawCalls.Count)
            {
                Dispose(ref s_drawArgsBuffer);
            
                s_drawArgsBuffer = new ComputeBuffer(Mathf.NextPowerOfTwo(s_drawCalls.Count), DrawArgs.k_size, ComputeBufferType.IndirectArguments)
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

            s_tempAnimationSetToBaseIndex.Clear();
            s_tempAnimationSets.Clear();
            
            // determine the size required for the animation data buffer
            var animationCount = 0;
            
            for (var i = 0; i < s_providers.Count; i++)
            {
                var provider = s_providers[i];
                var state = s_providerStates[i];
                var animationSet = provider.AnimationSet;

                if (!s_tempAnimationSetToBaseIndex.TryGetValue(animationSet, out var baseIndex))
                {
                    baseIndex = animationCount;
                    s_tempAnimationSetToBaseIndex.Add(animationSet, baseIndex);
                    s_tempAnimationSets.Add(animationSet);

                    animationCount += animationSet.Animations.Length;
                }
                
                state.animationBaseIndex = baseIndex;

                s_providerStates[i] = state;
            }

            // get all the animation data 
            var animationData = new NativeArray<AnimationData>(animationCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            var animationIndex = 0;

            for (var i = 0; i < s_tempAnimationSets.Count; i++)
            {
                var animationSet = s_tempAnimationSets[i];
                var animations = animationSet.Animations;

                for (var j = 0; j < animations.Length; j++)
                {
                    animationData[animationIndex] = animations[i].Data;
                    animationIndex++;
                }
            }
            
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
            
            for (var i = 0; i < s_providers.Count; i++)
            {
                instanceCount += s_providers[i].InstanceCount;
            }
            
            // create new buffers if the previous ones are too small
            if (!s_instanceData.IsCreated || s_instanceData.Length < instanceCount)
            {
                Dispose(ref s_instanceData);
                
                s_instanceData = new NativeArray<InstanceData>(instanceCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }
            
            if (s_instanceDataBuffer == null || s_instanceDataBuffer.count < instanceCount)
            {
                Dispose(ref s_instanceDataBuffer);
                Dispose(ref s_isVisibleBuffer);
                Dispose(ref s_isVisibleScanInBucketBuffer);
                Dispose(ref s_instancePropertiesBuffer);

                var count = Mathf.NextPowerOfTwo(instanceCount);
                
                s_instanceDataBuffer = new ComputeBuffer(count, InstanceData.k_size, ComputeBufferType.Default, ComputeBufferMode.Dynamic)
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

            // find how many buckets we need in the scan rounded up
            var scanBucketCount = ((instanceCount + (Constants.k_ScanBucketSize - 1)) / Constants.k_ScanBucketSize) * Constants.k_ScanBucketSize;

            if (s_isVisibleScanAcrossBucketsBuffer == null || s_isVisibleScanAcrossBucketsBuffer.count < scanBucketCount)
            {
                Dispose(ref s_isVisibleScanAcrossBucketsBuffer);
                
                var count = Mathf.NextPowerOfTwo(scanBucketCount);

                s_isVisibleScanAcrossBucketsBuffer = new ComputeBuffer(count, sizeof(uint))
                {
                    name = $"{nameof(InstancingManager)}_isVisibleScanAcrossBuckets",
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

            var updateJobs = new NativeArray<JobHandle>(s_providers.Count, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            var jobCount = 0;
            var currentInstances = 0;
            
            for (var i = 0; i < s_providerIndices.Length; i++)
            {
                var providerIndex = s_providerIndices[i];
                var provider = s_providers[providerIndex];
                var state = s_providerStates[providerIndex];

                if (forceUpdate || state.dirtyFlags.Intersects(DirtyFlags.PerInstanceData))
                {
                    provider.GetInstances(out var instances);

                    var job = new UpdateInstancesJob
                    {
                        instances = instances,
                        providerState = state,
                        instanceStart = currentInstances,
                        instanceData = s_instanceData,
                    };

                    updateJobs[jobCount] = job.Schedule(instances.Length, 64);
                    jobCount++;
                }

                currentInstances += provider.InstanceCount;
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

            if (!TryGetKernel(s_cullingShader, Kernels.k_CullingKernel, ref s_cullingKernel) ||
                !TryGetKernel(s_scanShader, Kernels.k_ScanInBucketKernel, ref s_scanInBucketKernel) ||
                !TryGetKernel(s_scanShader, Kernels.k_ScanAcrossBucketsKernel, ref s_scanAcrossBucketsKernel) ||
                !TryGetKernel(s_compactShader, Kernels.k_CullingKernel, ref s_compactKernel))
            {
                DisposeResources();
                return false;
            }

            // create the command buffers
            s_cullingCmdBuffer = new CommandBuffer
            {
                name = $"{nameof(InstancingManager)}_InstanceCulling",
            };

            s_resourcesInitialized = true;
            return true;
        }

        static void DisposeResources()
        {
            s_enabled = false;
            s_dirtyFlags = DirtyFlags.None;
            
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

            // dispose compute buffers
            Dispose(ref s_lodDataBuffer);
            Dispose(ref s_animationDataBuffer);
            Dispose(ref s_drawArgsBuffer);
            Dispose(ref s_instanceDataBuffer);
            Dispose(ref s_isVisibleBuffer);
            Dispose(ref s_isVisibleScanInBucketBuffer);
            Dispose(ref s_isVisibleScanAcrossBucketsBuffer);
            Dispose(ref s_instancePropertiesBuffer);
            
            // dispose main memory buffers
            Dispose(ref s_drawArgs);
            Dispose(ref s_providerIndices);
            Dispose(ref s_instanceData);

            // clear managed state
            Clear(s_drawCalls);
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
            }
        }

        static void Dispose<T>(ref NativeList<T> buffer) where T : struct
        {
            if (buffer.IsCreated)
            {
                buffer.Dispose();
            }
        }

        static void Clear(List<DrawCall> drawCalls)
        {
            for (var i = 0; i < s_drawCalls.Count; i++)
            {
                drawCalls[i].Dispose();
            }
            drawCalls.Clear();
        }
    }
}

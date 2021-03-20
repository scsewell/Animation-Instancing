using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using Unity.Collections;

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
            public IInstanceProvider provider;
            public int lodIndex;
            public int drawArgsIndex;
            public int animationBase;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct DrawArgs
        {
            public static readonly int k_size = Marshal.SizeOf<DrawArgs>();

            public uint indexCount;
            public uint instanceCount;
            public uint indexStart;
            public uint baseVertex;
            public uint instanceStart;
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
        static int s_drawArgsCount;
        //static NativeArray<InstanceData> s_instanceData;
        
        static bool s_enabled;
        static readonly List<ProviderState> s_providersStates = new List<ProviderState>();
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
            s_providersStates.Clear();
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
            for (var i = 0; i < s_providersStates.Count; i++)
            {
                if (s_providersStates[i].provider == provider)
                {
                    return;
                }
            }
            
            s_providersStates.Add(new ProviderState
            {
                provider = provider,
            });
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

            for (var i = 0; i < s_providersStates.Count; i++)
            {
                if (s_providersStates[i].provider == provider)
                {
                    s_providersStates.RemoveAt(i);
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
            var dirtyFlags = DirtyFlags.None;
            var instanceCount = 0;
            
            for (var i = 0; i < s_providersStates.Count; i++)
            {
                var state = s_providersStates[i];
                var provider = state.provider;
                
                dirtyFlags |= provider.DirtyFlags;
                provider.ClearDirtyFlags();
                
                instanceCount += provider.InstanceCount;
            }

            if (instanceCount == 0)
            {
                return;
            }

            // update any buffers whose data is invalidated
            
            // do updates in parallel jobs???
            if (dirtyFlags.Intersects(DirtyFlags.Mesh))
            {
                UpdateMeshBuffers();
            }
            if (dirtyFlags.Intersects(DirtyFlags.Mesh | DirtyFlags.Material))
            {
                UpdateDrawArgsBuffers();
            }
            if (dirtyFlags.Intersects(DirtyFlags.Animation))
            {
                UpdateAnimationBuffers();
            }
            if (dirtyFlags.Intersects(DirtyFlags.InstanceCount))
            {
            }
            if (dirtyFlags.Intersects(DirtyFlags.PerInstanceData))
            {
            }

            // render the instances for each camera
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

        static void UpdateMeshBuffers()
        {
            Profiler.BeginSample($"{nameof(InstancingManager)}.{nameof(UpdateMeshBuffers)}");

            // get the lod data for all instanced meshes
            var lodData = new NativeArray<LodData>(s_providersStates.Count, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            
            for (var i = 0; i < s_providersStates.Count; i++)
            {
                var state = s_providersStates[i];
                var provider = state.provider;

                lodData[i] = provider.Mesh.Lods;
                state.lodIndex = i;

                s_providersStates[i] = state;
            }
            
            // create a new buffer if the previous one was too small
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

        readonly struct DrawCall : IEquatable<DrawCall>
        {
            public readonly Mesh mesh;
            public readonly Material material;

            public DrawCall(Mesh mesh, Material material)
            {
                this.mesh = mesh;
                this.material = material;
            }

            /// <inheritdoc />
            public bool Equals(DrawCall other)
            {
                return mesh == other.mesh && material == other.material;
            }

            /// <inheritdoc />
            public override bool Equals(object obj)
            {
                return obj is DrawCall other && Equals(other);
            }

            /// <inheritdoc />
            public override int GetHashCode()
            {
                unchecked
                {
                    return ((mesh != null ? mesh.GetHashCode() : 0) * 397) ^ (material != null ? material.GetHashCode() : 0);
                }
            }
        }

        static void UpdateDrawArgsBuffers()
        {
            Profiler.BeginSample($"{nameof(InstancingManager)}.{nameof(UpdateDrawArgsBuffers)}");

            // Determine the number of individual draw calls required to render the instances.
            // Each mesh/material combination requires a separate draw call.
            var drawCallToArgsIndex = new NativeHashMap<DrawCall, int>(s_providersStates.Count, Allocator.Temp);
            
            for (var i = 0; i < s_providersStates.Count; i++)
            {
                var state = s_providersStates[i];
                var provider = state.provider;

                var drawCall = new DrawCall(provider.Mesh.Mesh, provider.Material);
                
                if (drawCallToArgsIndex.TryGetValue(drawCall, out var baseIndex))
                {
                    baseIndex = drawCallToArgsIndex.Count();
                    drawCallToArgsIndex.Add(drawCall, baseIndex);
                }
                
                state.drawArgsIndex = baseIndex;

                s_providersStates[i] = state;
            }
            
            // Allocate the draw args array. We upload this to the draw args compute buffer each frame to
            // reset the instance counts, so it must be persistent.
            s_drawArgsCount = drawCallToArgsIndex.Count();

            if (!s_drawArgs.IsCreated || s_drawArgs.Length < s_drawArgsCount)
            {
                Dispose(ref s_drawArgs);
                
                s_drawArgs = new NativeArray<DrawArgs>(s_drawArgsCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }

            // get the draw args for each draw call
            for (var i = 0; i < s_drawArgsCount; i++)
            {
                var state = s_providersStates[i];
                var provider = state.provider;

                
            }

            // create a new buffer if the previous one was too small
            if (s_drawArgsBuffer == null || s_drawArgsBuffer.count < s_drawArgsCount)
            {
                Dispose(ref s_drawArgsBuffer);
            
                s_drawArgsBuffer = new ComputeBuffer(Mathf.NextPowerOfTwo(s_drawArgsCount), DrawArgs.k_size, ComputeBufferType.IndirectArguments)
                {
                    name = $"{nameof(InstancingManager)}_{nameof(DrawArgs)}",
                };
            }

            s_drawArgsBuffer.SetData(s_drawArgs, 0, 0, s_drawArgsCount);

            Profiler.EndSample();
        }

        static void UpdateAnimationBuffers()
        {
            Profiler.BeginSample($"{nameof(InstancingManager)}.{nameof(UpdateAnimationBuffers)}");

            s_tempAnimationSetToBaseIndex.Clear();
            s_tempAnimationSets.Clear();
            
            // determine the size required for the animation data buffer
            var animationCount = 0;
            
            for (var i = 0; i < s_providersStates.Count; i++)
            {
                var state = s_providersStates[i];
                var provider = state.provider;
                var animationSet = provider.AnimationSet;

                if (!s_tempAnimationSetToBaseIndex.TryGetValue(animationSet, out var baseIndex))
                {
                    baseIndex = animationCount;
                    s_tempAnimationSetToBaseIndex.Add(animationSet, baseIndex);
                    s_tempAnimationSets.Add(animationSet);

                    animationCount += animationSet.Animations.Length;
                }
                
                state.animationBase = baseIndex;

                s_providersStates[i] = state;
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
            
            // create a new buffer if the previous one was too small
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
        
        static void CreateBuffers()
        {
            //s_instanceDataBuffer = new ComputeBuffer(count, InstanceData.k_size)
            //{
            //    name = $"{nameof(InstancingManager)}_InstanceData",
            //};
            //s_isVisibleBuffer = new ComputeBuffer(count, sizeof(uint))
            //{
            //    name = $"{nameof(InstancingManager)}_IsVisible",
            //};
            //s_isVisibleScanInBucketBuffer = new ComputeBuffer(count, sizeof(uint))
            //{
            //    name = $"{nameof(InstancingManager)}_isVisibleScanInBucket",
            //};
            //s_instanceDataBuffer = new ComputeBuffer(count, InstanceProperties.k_size)
            //{
            //    name = $"{nameof(InstancingManager)}_InstanceData",
            //};

            //s_isVisibleScanAcrossBucketsBuffer = new ComputeBuffer(, sizeof(uint))
            //{
            //    name = $"{nameof(InstancingManager)}_isVisibleScanAcrossBuckets",
            //};
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

            s_drawArgsCount = 0;
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
    }
}

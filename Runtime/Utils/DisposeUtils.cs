using System;

using Unity.Collections;

using UnityEngine;
using UnityEngine.Rendering;

namespace AnimationInstancing
{
    static class DisposeUtils
    {
        public static void Dispose<T>(ref T instance) where T : IDisposable 
        {
            if (typeof(T).IsClass)
            {
                if (instance != null)
                {
                    instance.Dispose();
                    instance = default;
                }
            }
            else
            {
                instance.Dispose();
                instance = default;
            }
        }
        
        public static void Dispose(ref CommandBuffer buffer)
        {
            if (buffer != null)
            {
                buffer.Release();
                buffer = null;
            }
        }

        public static void Dispose(ref ComputeBuffer buffer)
        {
            if (buffer != null)
            {
                buffer.Release();
                buffer = null;
            }
        }

        public static void Dispose<T>(ref NativeArray<T> buffer) where T : struct
        {
            if (buffer.IsCreated)
            {
                buffer.Dispose();
                buffer = default;
            }
        }

        public static void Dispose<T>(ref NativeList<T> buffer) where T : struct
        {
            if (buffer.IsCreated)
            {
                buffer.Dispose();
                buffer = default;
            }
        }
        
        public static void Dispose<TKey, TValue>(ref NativeHashMap<TKey, BlittableNativeArray<TValue>> map)
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

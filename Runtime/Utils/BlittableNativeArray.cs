using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace AnimationInstancing
{    
    /// <summary>
    /// A native array implementation that is blittable and can be nested inside of other native containers.
    /// </summary>
    /// <remarks>
    /// It is made blittable by removal of safety checks, so use with caution.
    /// </remarks>
    /// <typeparam name="T">A blittable type.</typeparam>
    [StructLayout(LayoutKind.Sequential)]
    [DebuggerDisplay("Length = {Length}")]
    [DebuggerTypeProxy(typeof(BlittableArrayDebugView<>))]
    public unsafe struct BlittableNativeArray<T> : IDisposable, IEnumerable<T>, IEquatable<BlittableNativeArray<T>> where T : unmanaged
    {
        [NativeDisableUnsafePtrRestriction]
        internal void* m_Buffer;
        internal int m_Length;
        
#if ENABLE_UNITY_COLLECTIONS_CHECKS
        internal int m_MinIndex;
        internal int m_MaxIndex;
        internal AtomicSafetyHandle m_Safety;

        static int s_StaticSafetyId;
        
        [BurstDiscard]
        static void InitStaticSafetyId(ref AtomicSafetyHandle handle)
        {
            if (s_StaticSafetyId == 0)
            {
                s_StaticSafetyId = AtomicSafetyHandle.NewStaticSafetyId<BlittableNativeArray<T>>();
            }
            AtomicSafetyHandle.SetStaticSafetyId(ref handle, s_StaticSafetyId);
        }
#endif
        
        internal Allocator m_AllocatorLabel;

        public BlittableNativeArray(int length, Allocator allocator, NativeArrayOptions options = NativeArrayOptions.ClearMemory)
        {
            var totalSize = UnsafeUtility.SizeOf<T>() * (long)length;
            CheckAllocateArguments(length, allocator, totalSize);

            m_Buffer = UnsafeUtility.Malloc(totalSize, UnsafeUtility.AlignOf<T>(), allocator);
            m_Length = length;
            m_AllocatorLabel = allocator;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            m_MinIndex = 0;
            m_MaxIndex = length - 1;
            m_Safety = AtomicSafetyHandle.Create();
            InitStaticSafetyId(ref m_Safety);
#endif
            
            if ((options & NativeArrayOptions.ClearMemory) == NativeArrayOptions.ClearMemory)
            {
                UnsafeUtility.MemClear(m_Buffer, totalSize);
            }
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        static void CheckAllocateArguments(int length, Allocator allocator, long totalSize)
        {
            if (allocator <= Allocator.None)
            {
                throw new ArgumentException("Allocator must be Temp, TempJob or Persistent", nameof(allocator));
            }
            if (length < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(length), "Length must be >= 0");
            }
            if (!UnsafeUtility.IsUnmanaged<T>())
            {
                throw new InvalidOperationException(
                    $"{typeof(T)} used in BlittableNativeArray<{typeof(T)}> must be unmanaged (contain no managed types).");
            }
        }

        public int Length => m_Length;
        
        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        void CheckElementReadAccess(int index)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (index < m_MinIndex || index > m_MaxIndex)
            {
                FailOutOfRangeError(index);
            }
            
            AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        void CheckElementWriteAccess(int index)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (index < m_MinIndex || index > m_MaxIndex)
            {
                FailOutOfRangeError(index);
            }
            
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
#endif
        }

        public T this[int index]
        {
            get
            {
                CheckElementReadAccess(index);
                return UnsafeUtility.ReadArrayElement<T>(m_Buffer, index);
            }

            [WriteAccessRequired]
            set
            {
                CheckElementWriteAccess(index);
                UnsafeUtility.WriteArrayElement(m_Buffer, index, value);
            }
        }

        public bool IsCreated => m_Buffer != null;

        [WriteAccessRequired]
        public void Dispose()
        {
            if (m_Buffer == null)
            {
                throw new ObjectDisposedException("The BlittableNativeArray is already disposed.");
            }
            if (m_AllocatorLabel == Allocator.Invalid)
            {
                throw new InvalidOperationException("The BlittableNativeArray can not be Disposed because it was not allocated with a valid allocator.");
            }

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.Release(m_Safety);
#endif
            
            if (m_AllocatorLabel > Allocator.None)
            {
                UnsafeUtility.Free(m_Buffer, m_AllocatorLabel);
                m_AllocatorLabel = Allocator.Invalid;
            }

            m_Buffer = null;
            m_Length = 0;
        }

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        void FailOutOfRangeError(int index)
        {
            if (index < Length && (m_MinIndex != 0 || m_MaxIndex != Length - 1))
            {
                throw new IndexOutOfRangeException(
                    $"Index {index} is out of restricted IJobParallelFor range [{m_MinIndex}...{m_MaxIndex}] in ReadWriteBuffer.\n" +
                    "ReadWriteBuffers are restricted to only read & write the element at the job index. " +
                    "You can use double buffering strategies to avoid race conditions due to " +
                    "reading & writing in parallel to the same elements from a job."
                );
            }

            throw new IndexOutOfRangeException($"Index {index} is out of range of '{Length}' Length.");
        }
#endif
        
        public Enumerator GetEnumerator()
        {
            return new Enumerator(ref this);
        }

        IEnumerator<T> IEnumerable<T>.GetEnumerator()
        {
            return new Enumerator(ref this);
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public struct Enumerator : IEnumerator<T>
        {
            BlittableNativeArray<T> m_Array;
            int m_Index;

            public Enumerator(ref BlittableNativeArray<T> array)
            {
                m_Array = array;
                m_Index = -1;
            }

            public void Dispose()
            {
            }

            public bool MoveNext()
            {
                m_Index++;
                return m_Index < m_Array.Length;
            }

            public void Reset()
            {
                m_Index = -1;
            }

            public T Current => m_Array[m_Index];

            object IEnumerator.Current => Current;
        }

        public bool Equals(BlittableNativeArray<T> other)
        {
            return m_Buffer == other.m_Buffer && m_Length == other.m_Length;
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj))
            {
                return false;
            }
            return obj is BlittableNativeArray<T> array && Equals(array);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return ((int)m_Buffer * 397) ^ m_Length;
            }
        }

        public static bool operator==(BlittableNativeArray<T> left, BlittableNativeArray<T> right)
        {
            return left.Equals(right);
        }

        public static bool operator!=(BlittableNativeArray<T> left, BlittableNativeArray<T> right)
        {
            return !left.Equals(right);
        }
    }
 
    class BlittableArrayDebugView<T> where T : unmanaged
    {
        BlittableNativeArray<T> m_Array;
 
        public BlittableArrayDebugView(BlittableNativeArray<T> array)
        {
            m_Array = array;
        }
 
        public T[] Items => m_Array.ToArray();
    }
}

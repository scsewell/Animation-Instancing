using System;
using System.Collections.Generic;

using UnityEngine;

namespace AnimationInstancing
{
    interface IHandle
    {
        int handle { get; set; }
    }

    /// <summary>
    /// A handle that represents a <see cref="Mesh"/> registered with the <see cref="InstancingManager"/>.
    /// </summary>
    public struct MeshHandle : IEquatable<MeshHandle>, IHandle
    {
        int m_handle;

        /// <inheritdoc />
        int IHandle.handle
        {
            get => m_handle;
            set => m_handle = value;
        }

        /// <inheritdoc />
        public bool Equals(MeshHandle other)
        {
            return m_handle == other.m_handle;
        }

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            return obj is MeshHandle other && Equals(other);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            return m_handle;
        }

        public static bool operator ==(MeshHandle a, MeshHandle b) => a.m_handle == b.m_handle;
        public static bool operator !=(MeshHandle a, MeshHandle b) => a.m_handle != b.m_handle;
    }
    
    /// <summary>
    /// A handle that represents a <see cref="Material"/> registered with the <see cref="InstancingManager"/>.
    /// </summary>
    public struct MaterialHandle : IEquatable<MaterialHandle>, IHandle
    {
        int m_handle;

        /// <inheritdoc />
        int IHandle.handle
        {
            get => m_handle;
            set => m_handle = value;
        }

        /// <inheritdoc />
        public bool Equals(MaterialHandle other)
        {
            return m_handle == other.m_handle;
        }

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            return obj is MaterialHandle other && Equals(other);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            return m_handle;
        }

        public static bool operator ==(MaterialHandle a, MaterialHandle b) => a.m_handle == b.m_handle;
        public static bool operator !=(MaterialHandle a, MaterialHandle b) => a.m_handle != b.m_handle;
    }

    /// <summary>
    /// A handle that represents a <see cref="InstancedAnimationSet"/> registered with the <see cref="InstancingManager"/>.
    /// </summary>
    public struct AnimationSetHandle : IEquatable<AnimationSetHandle>, IHandle
    {
        int m_handle;

        /// <inheritdoc />
        int IHandle.handle
        {
            get => m_handle;
            set => m_handle = value;
        }

        /// <inheritdoc />
        public bool Equals(AnimationSetHandle other)
        {
            return m_handle == other.m_handle;
        }

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            return obj is AnimationSetHandle other && Equals(other);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            return m_handle;
        }

        public static bool operator ==(AnimationSetHandle a, AnimationSetHandle b) => a.m_handle == b.m_handle;
        public static bool operator !=(AnimationSetHandle a, AnimationSetHandle b) => a.m_handle != b.m_handle;
    }

    class HandleManager<THandle, TClass>
        where THandle : struct, IHandle
        where TClass : class
    {
        readonly Dictionary<THandle, TClass> m_forward = new Dictionary<THandle, TClass>();
        readonly Dictionary<TClass, THandle> m_reverse = new Dictionary<TClass, THandle>();
        int m_count;

        public THandle Register(TClass instance)
        {
            if (m_reverse.TryGetValue(instance, out var handle))
            {
                return handle;
            }

            handle = new THandle
            {
                handle = m_count++,
            };

            m_forward.Add(handle, instance);
            m_reverse.Add(instance, handle);
            return handle;
        }
        
        public bool Deregister(THandle handle)
        {
            if (!m_forward.TryGetValue(handle, out var instance))
            {
                return false;
            }
            
            m_forward.Remove(handle);
            m_reverse.Remove(instance);
            return true;
        }

        public TClass GetInstance(THandle handle)
        {
            return m_forward[handle];
        }

        public void Clear()
        {
            m_forward.Clear();
            m_reverse.Clear();
            m_count = 0;
        }
    }
}

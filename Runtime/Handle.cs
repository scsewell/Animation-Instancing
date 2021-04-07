using System;
using System.Collections.Generic;

namespace AnimationInstancing
{
    /// <summary>
    /// A handle that represents an instance of a class.
    /// </summary>
    /// <remarks>
    /// This is used to reference a non-blittable type in contexts only blittable types may be used.
    /// </remarks>
    public readonly struct Handle<TClass> : IEquatable<Handle<TClass>> where TClass : class
    {
        /// <summary>
        /// The handle value.
        /// </summary>
        internal readonly int m_value;

        /// <summary>
        /// Creates a new <see cref="Handle{TClass}"/> instance.
        /// </summary>
        /// <param name="value">The handle value.</param>
        internal Handle(int value)
        {
            m_value = value;
        }
        
        /// <inheritdoc />
        public bool Equals(Handle<TClass> other)
        {
            return m_value == other.m_value;
        }

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            return obj is Handle<TClass> other && Equals(other);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            return m_value;
        }

        /// <summary>
        /// Gets if two specified handles of <see cref="Handle{TClass}"/> represent the same instance.
        /// </summary>
        /// <param name="a">The first handle.</param>
        /// <param name="b">The second handle.</param>
        /// <returns><see langword="true"/> if the handles represent the same instance; <see langword="false"/>, otherwise.</returns>
        public static bool operator ==(Handle<TClass> a, Handle<TClass> b) => a.m_value == b.m_value;
        
        /// <summary>
        /// Gets if two specified handles of <see cref="Handle{TClass}"/> do not represent the same instance.
        /// </summary>
        /// <param name="a">The first handle.</param>
        /// <param name="b">The second handle.</param>
        /// <returns><see langword="true"/> if the handles do not represent the same instance; <see langword="false"/>, otherwise.</returns>
        public static bool operator !=(Handle<TClass> a, Handle<TClass> b) => a.m_value != b.m_value;
    }
    
    class HandleManager<TClass> where TClass : class
    {
        struct HandleInfo
        {
            public Handle<TClass> handle;
            public int referenceCount;
        }
        
        readonly Dictionary<Handle<TClass>, TClass> m_handleToInstance = new Dictionary<Handle<TClass>, TClass>();
        readonly Dictionary<TClass, HandleInfo> m_instanceToHandle = new Dictionary<TClass, HandleInfo>();
        int m_count = 1;

        public bool Register(TClass instance, out Handle<TClass> handle)
        {
            // if this instance is already registered increase the reference count return the handle
            if (m_instanceToHandle.TryGetValue(instance, out var handleInfo))
            {
                handleInfo.referenceCount++;
                m_instanceToHandle[instance] = handleInfo;

                handle = handleInfo.handle;
                return false;
            }

            // allocate a new handle
            handle = new Handle<TClass>(m_count++);

            m_handleToInstance.Add(handle, instance);
            m_instanceToHandle.Add(instance, new HandleInfo
            {
                handle = handle,
                referenceCount = 1,
            });
            
            return true;
        }
        
        public bool Deregister(Handle<TClass> handle)
        {
            // check if this handle is valid
            if (!m_handleToInstance.TryGetValue(handle, out var instance))
            {
                return false;
            }

            // decrement the reference count
            var handleInfo = m_instanceToHandle[instance];
            handleInfo.referenceCount--;

            // when there are no users remove the handle
            if (handleInfo.referenceCount == 0)
            {
                m_handleToInstance.Remove(handle);
                m_instanceToHandle.Remove(instance);
                return true;
            }
            
            m_instanceToHandle[instance] = handleInfo;
            return false;
        }

        public TClass GetInstance(Handle<TClass> handle)
        {
            return m_handleToInstance[handle];
        }

        public void Clear()
        {
            m_handleToInstance.Clear();
            m_instanceToHandle.Clear();
            m_count = 1;
        }
    }
}

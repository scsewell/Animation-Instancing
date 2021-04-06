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
        readonly Dictionary<Handle<TClass>, TClass> m_forward = new Dictionary<Handle<TClass>, TClass>();
        readonly Dictionary<TClass, Handle<TClass>> m_reverse = new Dictionary<TClass, Handle<TClass>>();
        int m_count = 1;

        public bool Register(TClass instance, out Handle<TClass> handle)
        {
            if (m_reverse.TryGetValue(instance, out handle))
            {
                return false;
            }

            handle = new Handle<TClass>(m_count++);

            m_forward.Add(handle, instance);
            m_reverse.Add(instance, handle);
            return true;
        }
        
        public bool Deregister(Handle<TClass> handle)
        {
            if (!m_forward.TryGetValue(handle, out var instance))
            {
                return false;
            }
            
            m_forward.Remove(handle);
            m_reverse.Remove(instance);
            return true;
        }

        public TClass GetInstance(Handle<TClass> handle)
        {
            return m_forward[handle];
        }

        public void Clear()
        {
            m_forward.Clear();
            m_reverse.Clear();
            m_count = 1;
        }
    }
}

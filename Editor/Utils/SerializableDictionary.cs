using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

using UnityEngine;

namespace AnimationInstancing
{
    [Serializable]
    class SerializableDictionary<TKey, TValue> : Dictionary<TKey, TValue>, ISerializationCallbackReceiver
    {
        [SerializeField]
        List<TKey> m_keys = new List<TKey>();
        [SerializeField]
        List<TValue> m_values = new List<TValue>();

        public SerializableDictionary()
        {
        }

        public SerializableDictionary(IDictionary<TKey, TValue> input) : base(input)
        {
        }

        void ISerializationCallbackReceiver.OnBeforeSerialize()
        {
            m_keys.Clear();
            m_values.Clear();

            foreach (var pair in this)
            {
                m_keys.Add(pair.Key);
                m_values.Add(pair.Value);
            }
        }

        void ISerializationCallbackReceiver.OnAfterDeserialize()
        {
            Clear();

            if (m_keys.Count != m_values.Count)
            {
                throw new SerializationException($"There are {m_keys.Count} keys and {m_values.Count} values after deserialization. " +
                    $"Make sure that both key and value types are serializable.");
            }

            for (var i = 0; i < m_keys.Count; i++)
            {
                Add(m_keys[i], m_values[i]);
            }
        }
    }
}

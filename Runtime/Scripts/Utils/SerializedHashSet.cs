using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace MagusStudios.Collections
{
    /// <summary>
    /// A Unity-serializable HashSet wrapper. Unity serializes the internal List<T>;
    /// at runtime we use a HashSet<T> for O(1) lookups and set ops.
    /// Implements ISerializationCallbackReceiver to keep both in sync.
    /// </summary>
    [Serializable]
    public class SerializedHashSet<T> : ICollection<T>, IReadOnlyCollection<T>, ISerializationCallbackReceiver
    {
        // Serialized list that Unity will show in the inspector and persist
        [SerializeField] private List<T> serializedList = new List<T>();

        // Runtime-only hashset for fast operations (not serialized)
        [NonSerialized] private HashSet<T> runtimeSet;

        // Optional runtime comparer (not serialized). If null, EqualityComparer<T>.Default is used.
        [NonSerialized] private IEqualityComparer<T> comparer;

        #region Constructors

        public SerializedHashSet() { EnsureSet(); }

        public SerializedHashSet(IEnumerable<T> collection)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));
            EnsureSet();
            runtimeSet.UnionWith(collection);
        }

        public SerializedHashSet(IEqualityComparer<T> comparer)
        {
            this.comparer = comparer;
            EnsureSet();
        }

        public SerializedHashSet(IEnumerable<T> collection, IEqualityComparer<T> comparer)
        {
            if (collection == null) throw new ArgumentNullException(nameof(collection));
            this.comparer = comparer;
            EnsureSet();
            runtimeSet.UnionWith(collection);
        }

        #endregion

        #region Internal helpers

        private void EnsureSet()
        {
            if (runtimeSet != null) return;
            runtimeSet = (comparer != null) ? new HashSet<T>(comparer) : new HashSet<T>();
            if (serializedList != null && serializedList.Count > 0)
            {
                // initialize with capacity to avoid extra rehashes
                if (runtimeSet.Comparer != null)
                    runtimeSet = (comparer != null) ? new HashSet<T>(serializedList, comparer) : new HashSet<T>(serializedList);
                else
                    runtimeSet.UnionWith(serializedList);
            }
        }

        private void SyncSerializedListFromSet()
        {
            if (runtimeSet == null) serializedList = new List<T>();
            else
            {
                // Avoid creating a new list if we can reuse capacity
                serializedList = new List<T>(runtimeSet.Count);
                serializedList.AddRange(runtimeSet);
            }
        }

        #endregion

        #region ISerializationCallbackReceiver

        // Called before Unity serializes this object. Copy runtimeSet -> serializedList.
        public void OnBeforeSerialize()
        {
            // If runtimeSet hasn't been created, ensure we still serialize whatever list exists.
            if (runtimeSet == null)
            {
                // serializedList already contains saved data from previous deserialization or construction.
                return;
            }

            SyncSerializedListFromSet();
        }

        // Called after Unity deserializes this object. Rebuild runtimeSet from serializedList.
        public void OnAfterDeserialize()
        {
            // Create runtime set with an initial capacity to reduce rehashing
            var initialCapacity = (serializedList != null) ? Math.Max(0, serializedList.Count) : 0;
            runtimeSet = (comparer != null)
                ? new HashSet<T>(initialCapacity, comparer)
                : new HashSet<T>(initialCapacity);

            if (serializedList != null && serializedList.Count > 0)
            {
                // HashSet constructor from IEnumerable may be cheaper than repeated adds
                runtimeSet.UnionWith(serializedList);
            }
        }

        #endregion

        #region ICollection<T> + Helpful Set Methods

        public int Count
        {
            get
            {
                EnsureSet();
                return runtimeSet.Count;
            }
        }

        public bool IsReadOnly => false;

        public bool Add(T item)
        {
            EnsureSet();
            var added = runtimeSet.Add(item);
            if (added)
            {
                // keep serializedList in sync lazily (it will be written on next serialization).
                // But also reflect immediately so external callers that inspect serializedList see change.
                serializedList.Add(item);
            }
            return added;
        }

        void ICollection<T>.Add(T item) => Add(item);

        public bool Remove(T item)
        {
            EnsureSet();
            var removed = runtimeSet.Remove(item);
            if (removed && serializedList != null)
            {
                // remove first occurrence from serializedList; List.Remove is O(n) but this keeps inspector data accurate
                serializedList.Remove(item);
            }
            return removed;
        }

        public bool Contains(T item)
        {
            EnsureSet();
            return runtimeSet.Contains(item);
        }

        public void Clear()
        {
            EnsureSet();
            runtimeSet.Clear();
            serializedList?.Clear();
        }

        public void CopyTo(T[] array, int arrayIndex)
        {
            EnsureSet();
            runtimeSet.CopyTo(array, arrayIndex);
        }

        public IEnumerator<T> GetEnumerator()
        {
            EnsureSet();
            return runtimeSet.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        #endregion

        #region Extra set convenience methods (optional, efficient)

        public void UnionWith(IEnumerable<T> other)
        {
            if (other == null) throw new ArgumentNullException(nameof(other));
            EnsureSet();
            runtimeSet.UnionWith(other);
            SyncSerializedListFromSet();
        }

        public void ExceptWith(IEnumerable<T> other)
        {
            if (other == null) throw new ArgumentNullException(nameof(other));
            EnsureSet();
            runtimeSet.ExceptWith(other);
            SyncSerializedListFromSet();
        }

        public void TrimExcess()
        {
            // There's no direct HashSet.TrimExcess in older runtimes; recreate to trim.
            EnsureSet();
            var arr = new T[runtimeSet.Count];
            runtimeSet.CopyTo(arr);
            runtimeSet = (comparer != null) ? new HashSet<T>(arr, comparer) : new HashSet<T>(arr);
            serializedList = new List<T>(runtimeSet);
        }

        /// <summary>Try to get the stored value that equals the provided one (useful when T is a struct or contains extra fields).</summary>
        public bool TryGetValue(T equalValue, out T actualValue)
        {
            EnsureSet();
            // HashSet<T>.TryGetValue exists in .NET but not in all Unity versions; implement manually:
#if UNITY_2021_1_OR_NEWER
            if (runtimeSet.TryGetValue(equalValue, out actualValue)) return true;
            actualValue = default;
            return false;
#else
        foreach (var v in runtimeSet)
        {
            if (EqualityComparer<T>.Default.Equals(v, equalValue))
            {
                actualValue = v;
                return true;
            }
        }
        actualValue = default;
        return false;
#endif
        }

        #endregion

        #region Debug helpers / conversions

        public T[] ToArray()
        {
            EnsureSet();
            var arr = new T[runtimeSet.Count];
            runtimeSet.CopyTo(arr);
            return arr;
        }

        public List<T> ToList()
        {
            EnsureSet();
            return new List<T>(runtimeSet);
        }

        #endregion
    }

}

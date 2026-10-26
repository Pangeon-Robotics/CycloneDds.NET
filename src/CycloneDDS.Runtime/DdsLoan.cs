using System;
using System.Buffers;
using CycloneDDS.Core;
using CycloneDDS.Runtime.Interop;
using CycloneDDS.Runtime.Tracking;

namespace CycloneDDS.Runtime
{
    /// <summary>
    /// Samples returned by one read or take. Holds a reference to each sample's serialized data,
    /// which is deserialized on access; dispose to release them.
    /// </summary>
    public ref struct DdsLoan<T>(
        IntPtr[] serdata,
        DdsApi.DdsSampleInfo[] infos,
        int length,
        SenderRegistry? registry = null,
        Predicate<T>? filter = null)
        where T : IDdsType<T>
    {
        private readonly IntPtr[] _serdata = serdata; // Rented from ArrayPool; [0, length) are owned references
        private readonly DdsApi.DdsSampleInfo[] _infos = infos; // Rented from ArrayPool
        private readonly int _length = length;
        private readonly SenderRegistry? _registry = registry;
        private readonly Predicate<T>? _filter = filter;
        private bool _disposed = false;

        public readonly int Length => _length;
        public readonly int Count => _length;

        public readonly ReadOnlySpan<DdsApi.DdsSampleInfo> Infos => new(_infos, 0, _length);

        public SenderIdentity? GetSender(int index)
        {
            if (_registry == null) return null;
            if ((uint)index >= (uint)_length) throw new IndexOutOfRangeException();

            long handle = _infos[index].PublicationHandle;
            if (_registry.TryGetIdentity(handle, out var identity))
            {
                return identity;
            }
            return null;
        }

        /// <summary>Deserializes sample <paramref name="index"/>, ignoring the reader's filter.</summary>
        /// <exception cref="InvalidOperationException">The sample carries no data (see <see cref="DdsSample{T}.IsValid"/>).</exception>
        public readonly T this[int index]
        {
            get
            {
                if ((uint)index >= (uint)_length) throw new IndexOutOfRangeException();
                return new DdsSample<T>(_serdata[index], in _infos[index]).Data;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            for (int i = 0; i < _length; i++)
                DdsApi.ddsi_serdata_unref(_serdata[i]);

            if (_serdata != null) ArrayPool<IntPtr>.Shared.Return(_serdata);
            if (_infos != null) ArrayPool<DdsApi.DdsSampleInfo>.Shared.Return(_infos);
        }

        /// <summary>
        /// Enumerates the samples that pass the reader's filter. Samples without data always pass,
        /// as the filter has nothing to inspect.
        /// </summary>
        public readonly Enumerator GetEnumerator() => new(this);

        public ref struct Enumerator
        {
            private readonly DdsLoan<T> _loan;
            private int _index;

            // Value the filter accepted, reused by Current instead of deserializing twice
            private T _filtered;
            private bool _hasFiltered;

            public Enumerator(DdsLoan<T> loan)
            {
                _loan = loan;
                _index = -1;
                _filtered = default!;
                _hasFiltered = false;
            }

            public bool MoveNext()
            {
                _hasFiltered = false;
                while (++_index < _loan._length)
                {
                    if (_loan._filter == null || _loan._infos[_index].ValidData == 0) return true;

                    var value = new DdsSample<T>(_loan._serdata[_index], in _loan._infos[_index]).Data;
                    if (_loan._filter(value))
                    {
                        _filtered = value;
                        _hasFiltered = true;
                        return true;
                    }
                }
                return false;
            }

            public readonly DdsSample<T> Current => _hasFiltered
                ? new(_filtered, in _loan._infos[_index])
                : new(_loan._serdata[_index], in _loan._infos[_index]);
        }
    }
}

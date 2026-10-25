using System;
using System.Buffers;
using CycloneDDS.Runtime.Interop;
using CycloneDDS.Runtime.Tracking;

namespace CycloneDDS.Runtime
{
    public ref struct DdsLoan<T>(
        DdsEntityHandle reader,
        IntPtr[] samples,
        DdsApi.DdsSampleInfo[] infos,
        int length,
        SenderRegistry? registry = null,
        Predicate<T>? filter = null)
    {
        private readonly DdsEntityHandle _reader = reader;
        private readonly IntPtr[] _samples = samples; // Rented from ArrayPool
        private readonly DdsApi.DdsSampleInfo[] _infos = infos; // Rented from ArrayPool
        private readonly int _length = length;
        private readonly SenderRegistry? _registry = registry;
        internal readonly Predicate<T>? _filter = filter;
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

        public T this[int index]
        {
            get
            {
                if ((uint)index >= (uint)_length) throw new IndexOutOfRangeException();
                return DdsTypeSupport.FromNative<T>(_samples[index]);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_length > 0)
            {
                // Return loan to DDS
                // We pass the array of pointers (samples) that was filled by dds_take
                _ = DdsApi.dds_return_loan(_reader.NativeHandle.Handle, _samples, _length);
            }

            // Return arrays to pool
            if (_samples != null) ArrayPool<IntPtr>.Shared.Return(_samples);
            if (_infos != null) ArrayPool<DdsApi.DdsSampleInfo>.Shared.Return(_infos);
        }

        public readonly Enumerator GetEnumerator() => new(this);

        public ref struct Enumerator
        {
            private readonly DdsLoan<T> _loan;
            private int _index;

            public Enumerator(DdsLoan<T> loan)
            {
                _loan = loan;
                _index = -1;
            }

            public bool MoveNext()
            {
                while (++_index < _loan._length)
                {
                    if (_loan._filter == null) return true;

                    if (_loan._infos[_index].ValidData == 0) return true;

                    var sample = new DdsSample<T>(_loan._samples[_index], in _loan._infos[_index]);
                    if (_loan._filter(sample.Data)) return true;
                }
                return false;
            }

            public readonly DdsSample<T> Current => new(_loan._samples[_index], in _loan._infos[_index]);
        }
    }
}

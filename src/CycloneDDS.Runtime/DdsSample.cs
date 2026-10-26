using System;
using CycloneDDS.Core;
using CycloneDDS.Runtime.Interop;

namespace CycloneDDS.Runtime
{
    /// <summary>One sample of a <see cref="DdsLoan{T}"/>: its info, and its data deserialized on access.</summary>
    public readonly ref struct DdsSample<T> where T : IDdsType<T>
    {
        private readonly IntPtr _serdata;
        private readonly T _value;
        private readonly bool _hasValue;

        public readonly DdsApi.DdsSampleInfo Info;

        internal DdsSample(IntPtr serdata, ref readonly DdsApi.DdsSampleInfo info)
        {
            _serdata = serdata;
            _value = default!;
            _hasValue = false;
            Info = info;
        }

        internal DdsSample(T value, ref readonly DdsApi.DdsSampleInfo info)
        {
            _serdata = IntPtr.Zero;
            _value = value;
            _hasValue = true;
            Info = info;
        }

        /// <summary>
        /// False for samples that only report an instance state change (e.g. the last writer
        /// went away); those have no <see cref="Data"/>.
        /// </summary>
        public bool IsValid => Info.ValidData != 0;

        /// <summary>Deserializes the sample. Each access deserializes again; keep the result if it is needed twice.</summary>
        /// <exception cref="InvalidOperationException">The sample carries no data (<see cref="IsValid"/> is false).</exception>
        public T Data
        {
            get
            {
                if (_hasValue) return _value;
                if (!IsValid) throw new InvalidOperationException("The sample carries no data, only an instance state change.");
                return DdsApi.DeserializeSerdata<T>(_serdata);
            }
        }
    }
}

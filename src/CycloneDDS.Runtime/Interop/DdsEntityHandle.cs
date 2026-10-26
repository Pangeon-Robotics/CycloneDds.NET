using System;

namespace CycloneDDS.Runtime.Interop
{
    public sealed class DdsEntityHandle(DdsApi.DdsEntity entity) : IDisposable
    {
        private DdsApi.DdsEntity _entity = entity;
        private bool _disposed;

        public DdsApi.DdsEntity NativeHandle => _entity;

        public void Dispose()
        {
            if (!_disposed)
            {
                if (_entity.IsValid)
                {
                    _ = DdsApi.dds_delete(_entity);
                }

                _entity = DdsApi.DdsEntity.Null;
                _disposed = true;
            }
            GC.SuppressFinalize(this);
        }

        ~DdsEntityHandle()
        {
            Dispose();
        }
    }
}

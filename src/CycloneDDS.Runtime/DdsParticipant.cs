using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using CycloneDDS.Core;
using CycloneDDS.Runtime.Interop;
using CycloneDDS.Runtime.Tracking;

namespace CycloneDDS.Runtime
{
    public sealed class DdsParticipant : IDisposable
    {
        private DdsEntityHandle? _handle;
        private readonly uint _domainId;
        private readonly string? _defaultPartition;
        private bool _disposed;

        // Topics are shared per (name, type) and reference counted: every RegisterTopic hands
        // back the same native entity, every ReleaseTopic gives it up, and the last release
        // deletes the topic and frees the descriptor it was created from.
        private readonly Dictionary<(string Name, Type Type), TopicEntry> _topics = [];
        private readonly object _topicLock = new();

        private SenderIdentityConfig? _identityConfig;
        private DdsWriter<SenderIdentity>? _identityWriter;
        private int _activeWriterCount = 0;
        private readonly object _trackingLock = new();
        internal SenderRegistry? _senderRegistry;

        public DdsParticipant(uint domainId = 0, string? defaultPartition = null)
        {
            _domainId = domainId;
            _defaultPartition = defaultPartition;
            var entity = DdsApi.dds_create_participant(domainId, IntPtr.Zero, IntPtr.Zero);

            if (!entity.IsValid)
            {
                // Retrieve error code from the handle value if it's negative
                int handleVal = entity.Handle;
                if (handleVal < 0)
                {
                    DdsApi.DdsReturnCode err = (DdsApi.DdsReturnCode)handleVal;
                    throw new DdsException(err, "Failed to create participant");
                }

                throw new DdsException(DdsApi.DdsReturnCode.Error, "Failed to create participant (Invalid Handle)");
            }

            _handle = new DdsEntityHandle(entity);
        }

        public uint DomainId => _domainId;

        public string? DefaultPartition => _defaultPartition;

        public bool IsDisposed => _disposed;

        internal DdsApi.DdsEntity NativeEntity
        {
            get
            {
                if (_disposed || _handle == null)
                {
                    throw new ObjectDisposedException(nameof(DdsParticipant));
                }
                return _handle.NativeHandle;
            }
        }

        internal DdsEntityHandle HandleWrapper
        {
            get
            {
                if (_disposed || _handle == null)
                {
                    throw new ObjectDisposedException(nameof(DdsParticipant));
                }
                return _handle;
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                // Dispose our own endpoints first so they release their topics normally.
                _senderRegistry?.Dispose();
                _identityWriter?.Dispose();

                lock (_topicLock)
                {
                    // Anything left here is still held by an endpoint the caller never disposed
                    foreach (var entry in _topics.Values)
                    {
                        _ = DdsApi.dds_delete(entry.Entity);
                        entry.Descriptor.Dispose();
                    }
                    _topics.Clear();
                }

                _handle?.Dispose();
                _handle = null;
                _disposed = true;
            }
        }

        /// <summary>
        /// Register a topic for type T. The native topic is created on the first registration of
        /// a (topic name, type) pair and shared by every later registration of that pair; each
        /// call must be balanced by a <see cref="ReleaseTopic{T}"/>. Thread-safe.
        /// </summary>
        internal DdsApi.DdsEntity RegisterTopic<T>(string topicName) where T : IDdsType<T>
        {
            lock (_topicLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);

                var key = (topicName, typeof(T));
                if (_topics.TryGetValue(key, out TopicEntry? existing))
                {
                    existing.RefCount++;
                    return existing.Entity;
                }

                var descriptor = new NativeTopicDescriptor(T.Descriptor);

                // The QoS is deliberately left NULL: a QoS stored on the ktopic makes every later
                // endpoint with a different QoS fail with DDS_RETCODE_INCONSISTENT_POLICY.
                DdsApi.DdsEntity topic = DdsApi.dds_create_topic(
                    NativeEntity,
                    descriptor.Pointer,
                    topicName,
                    IntPtr.Zero,
                    IntPtr.Zero);

                if (!topic.IsValid)
                {
                    descriptor.Dispose();
                    throw new DdsException((DdsApi.DdsReturnCode)topic.Handle,
                        $"Failed to create topic '{topicName}' for type '{T.Descriptor.TypeName}'");
                }

                _topics.Add(key, new TopicEntry(topic, descriptor));
                return topic;
            }
        }

        /// <summary>
        /// Gives back one registration taken by <see cref="RegisterTopic{T}"/>. The last release
        /// deletes the native topic and frees its descriptor. Thread-safe; calls made after the
        /// participant is disposed are ignored, as everything is already gone by then.
        /// </summary>
        internal void ReleaseTopic<T>(string topicName)
        {
            lock (_topicLock)
            {
                if (_disposed) return;

                var key = (topicName, typeof(T));
                if (!_topics.TryGetValue(key, out TopicEntry? entry)) return;

                if (--entry.RefCount > 0) return;

                _ = _topics.Remove(key);
                _ = DdsApi.dds_delete(entry.Entity);
                entry.Descriptor.Dispose();
            }
        }

        private sealed class TopicEntry(DdsApi.DdsEntity entity, NativeTopicDescriptor descriptor)
        {
            public DdsApi.DdsEntity Entity { get; } = entity;
            public NativeTopicDescriptor Descriptor { get; } = descriptor;
            public int RefCount { get; set; } = 1;
        }

        /// <summary>
        /// Unmanaged copy of a <see cref="DdsTypeDescriptor"/> as dds_topic_descriptor_t. Cyclone
        /// keeps pointers into it, so it must outlive the topic.
        /// </summary>
        private sealed class NativeTopicDescriptor : IDisposable
        {
            [StructLayout(LayoutKind.Sequential)]
            private struct dds_topic_descriptor_t
            {
                public uint m_size;
                public uint m_align;
                public uint m_flagset;
                public uint m_nkeys;
                public IntPtr m_typename;
                public IntPtr m_keys;
                public uint m_nops;
                public IntPtr m_ops;
                public IntPtr m_meta;
                public dds_type_meta_ser type_information;
                public dds_type_meta_ser type_mapping;
                public uint restrict_data_representation;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct dds_type_meta_ser
            {
                public IntPtr data;
                public uint sz;
            }

            private IntPtr _typeName;
            private IntPtr _ops;

            public IntPtr Pointer { get; private set; }

            public unsafe NativeTopicDescriptor(DdsTypeDescriptor descriptor)
            {
                _typeName = Marshal.StringToHGlobalAnsi(descriptor.TypeName);

                _ops = Marshal.AllocHGlobal(descriptor.Ops.Length * sizeof(uint));
                descriptor.Ops.CopyTo(new Span<uint>((void*)_ops, descriptor.Ops.Length));

                // No keys (ROS 2 topics are keyless) and no type information: matching is by type name
                var native = new dds_topic_descriptor_t
                {
                    m_size = descriptor.Size,
                    m_align = descriptor.Align,
                    m_flagset = descriptor.Flagset,
                    m_typename = _typeName,
                    m_nops = (uint)descriptor.Ops.Length,
                    m_ops = _ops,
                };

                Pointer = Marshal.AllocHGlobal(sizeof(dds_topic_descriptor_t));
                *(dds_topic_descriptor_t*)Pointer = native;
            }

            public void Dispose()
            {
                Marshal.FreeHGlobal(Pointer);
                Marshal.FreeHGlobal(_ops);
                Marshal.FreeHGlobal(_typeName);
                Pointer = _ops = _typeName = IntPtr.Zero;
            }
        }



        /// <summary>
        /// Enables receiver-only sender tracking for this participant.
        /// Creates the internal <see cref="SenderRegistry"/> without publishing any
        /// identity so that DDS readers can resolve remote sender identities from the
        /// <c>__FcdcSenderIdentity</c> topic without advertising the current process.
        /// Safe to call before or after creating readers.
        /// </summary>
        public void EnableSenderMonitoring()
        {
            lock (_trackingLock)
            {
                if (_senderRegistry != null) return; // already enabled
                _senderRegistry = new SenderRegistry(this);
            }
        }

        /// <summary>
        /// Enable sender tracking for this participant.
        /// MUST be called before creating any DdsWriter or DdsReader.
        /// </summary>
        /// <param name="config">Configuration with AppDomainId, AppInstanceId</param>
        /// <exception cref="InvalidOperationException">If writers already created</exception>
        public void EnableSenderTracking(SenderIdentityConfig config)
        {
            lock (_trackingLock)
            {
                if (_activeWriterCount > 0)
                    throw new InvalidOperationException("EnableSenderTracking must be called before creating writers");

                _identityConfig = config;
                _senderRegistry = new SenderRegistry(this);
            }
        }

        /// <summary>
        /// Provides access to the sender registry (if tracking enabled).
        /// </summary>
        public SenderRegistry? SenderRegistry => _senderRegistry;

        internal void RegisterWriter()
        {
            lock (_trackingLock)
            {
                _activeWriterCount++;
                if (_identityConfig != null && _activeWriterCount == 1)
                {
                    PublishIdentity();
                }
            }
        }

        internal void UnregisterWriter()
        {
            lock (_trackingLock)
            {
                _activeWriterCount--;
                if (_identityConfig != null && _activeWriterCount == 0 && !_identityConfig.KeepAliveUntilParticipantDispose)
                {
                    DisposeIdentityWriter();
                }
            }
        }

        private void PublishIdentity()
        {
            var process = System.Diagnostics.Process.GetCurrentProcess();

            // Get native participant GUID
            _ = DdsApi.dds_get_guid(NativeEntity.Handle, out var myGuid);

            var identity = new SenderIdentity
            {
                ParticipantGuid = myGuid,
                AppDomainId = _identityConfig!.AppDomainId,
                AppInstanceId = _identityConfig.AppInstanceId,
                ProcessId = process.Id,
                ProcessName = _identityConfig.ProcessName ?? process.ProcessName,
                ComputerName = _identityConfig.ComputerName ?? Environment.MachineName,
                ComputerIP = _identityConfig.ComputerIP ?? CycloneDdsXmlConfig.NetworkInterfaceAddress ?? string.Empty
            };

            _identityWriter = new DdsWriter<SenderIdentity>(this, "__FcdcSenderIdentity", DdsQos.Latched);
            _identityWriter.Write(identity);
        }

        private void DisposeIdentityWriter()
        {
            _identityWriter?.Dispose();
            _identityWriter = null;
        }
    }
}

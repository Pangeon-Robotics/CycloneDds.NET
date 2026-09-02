using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
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
                        DdsApi.dds_delete(entry.Entity);
                        entry.Resource.Dispose();
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
        internal DdsApi.DdsEntity RegisterTopic<T>(string topicName)
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

                // 1. Get descriptor ops from static method (via reflection)
                uint[] ops = DdsTypeSupport.GetDescriptorOps<T>();
                DdsKeyDescriptor[] keys = DdsTypeSupport.GetKeyDescriptors<T>();

                // 2. Marshal descriptor to native
                IntPtr descriptorPtr = MarshalDescriptor<T>(ops, keys, DdsTypeSupport.GetTypeName<T>(), out TopicResource resource);

                // 3. Create native topic. The QoS is deliberately left NULL: a QoS stored on the
                // ktopic makes every later endpoint with a different QoS fail with
                // DDS_RETCODE_INCONSISTENT_POLICY.
                DdsApi.DdsEntity topic = DdsApi.dds_create_topic(
                    NativeEntity,
                    descriptorPtr,
                    topicName,
                    IntPtr.Zero,
                    IntPtr.Zero);

                if (!topic.IsValid)
                {
                    resource.Dispose();
                    throw new DdsException(DdsApi.DdsReturnCode.Error,
                        $"Failed to create topic '{topicName}' for type '{DdsTypeSupport.GetTypeName<T>()}'");
                }

                _topics.Add(key, new TopicEntry(topic, resource));
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

                _topics.Remove(key);
                DdsApi.dds_delete(entry.Entity);
                entry.Resource.Dispose();
            }
        }

        private sealed class TopicEntry
        {
            public TopicEntry(DdsApi.DdsEntity entity, TopicResource resource)
            {
                Entity = entity;
                Resource = resource;
            }

            public DdsApi.DdsEntity Entity { get; }
            public TopicResource Resource { get; }
            public int RefCount { get; set; } = 1;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DdsTopicDescriptor
        {
            public uint m_size;
            public uint m_align;
            public uint m_flagset;
            public uint m_nkeys;
            public IntPtr m_typename; // char*
            public IntPtr m_keys;     // dds_key_descriptor_t*
            public uint m_nops;
            public IntPtr m_ops;      // uint32_t*
            public IntPtr m_meta;     // char*
            public DdsTypeMetaSer type_information;
            public DdsTypeMetaSer type_mapping;
            public uint restrict_data_representation;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DdsTypeMetaSer
        {
            public IntPtr data;
            public uint sz;
        }

        private class TopicResource : IDisposable
        {
            private IntPtr _descPtr;
            private IntPtr _typeNamePtr;
            private GCHandle _opsHandle;
            private IntPtr _keysPtr;
            private IntPtr[] _keyNamePtrs;

            public TopicResource(IntPtr descPtr, IntPtr typeNamePtr, GCHandle opsHandle, IntPtr keysPtr, IntPtr[] keyNamePtrs)
            {
                _descPtr = descPtr;
                _typeNamePtr = typeNamePtr;
                _opsHandle = opsHandle;
                _keysPtr = keysPtr;
                _keyNamePtrs = keyNamePtrs;
            }

            public void Dispose()
            {
                if (_descPtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_descPtr);
                    _descPtr = IntPtr.Zero;
                }
                if (_typeNamePtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_typeNamePtr);
                    _typeNamePtr = IntPtr.Zero;
                }
                if (_opsHandle.IsAllocated)
                {
                    _opsHandle.Free();
                }
                if (_keysPtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_keysPtr);
                    _keysPtr = IntPtr.Zero;
                }
                if (_keyNamePtrs != null)
                {
                    foreach (var ptr in _keyNamePtrs)
                    {
                        if (ptr != IntPtr.Zero) Marshal.FreeHGlobal(ptr);
                    }
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DdsKeyDescriptorNative
        {
            public IntPtr Name;
            public uint Offset;
            public uint Index;
        }

        private static int GetRecursiveOffset(Type type, string keyPath)
        {
            try
            {
                string[] parts = keyPath.Split('.');
                int totalOffset = 0;
                Type currentType = type;

                foreach (var part in parts)
                {
                    // Find the field in the current type
                    var field = currentType.GetField(part,
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.IgnoreCase);

                    if (field == null)
                    {
                        // Try backing field for property? <Name>k__BackingField
                        field = currentType.GetField($"<{part}>k__BackingField",
                           System.Reflection.BindingFlags.Instance |
                           System.Reflection.BindingFlags.NonPublic |
                           System.Reflection.BindingFlags.IgnoreCase);
                    }

                    if (field == null)
                    {
                        throw new InvalidOperationException($"Could not find field '{part}' in type '{currentType.Name}' while resolving key '{keyPath}'");
                    }

                    // Add the offset of this field within its parent
                    // Note: Marshal.OffsetOf requires exact case match of the field definition
                    totalOffset += Marshal.OffsetOf(currentType, field.Name).ToInt32();

                    // Drill down
                    currentType = field.FieldType;
                }

                return totalOffset;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error calculating recursive offset for {keyPath} in {type.Name}: {ex}");
                throw;
            }
        }

        private static uint GetAlignment(Type type)
        {
            // Try to get generated alignment first
            try
            {
                var alignMethod = type.GetMethod("GetDescriptorAlign", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (alignMethod != null)
                {
                    return (uint)alignMethod.Invoke(null, null)!;
                }
            }
            catch { }

            if (type.StructLayoutAttribute != null && type.StructLayoutAttribute.Pack != 0)
                return (uint)type.StructLayoutAttribute.Pack;

            return (uint)IntPtr.Size; // Default to machine word size (8 on x64)
        }

        private static IntPtr MarshalDescriptor<T>(uint[] ops, DdsKeyDescriptor[] keys, string typeName, out TopicResource resource)
        {
            // Marshal type name
            IntPtr typeNamePtr = Marshal.StringToHGlobalAnsi(typeName);

            // Pin ops array
            GCHandle opsHandle = GCHandle.Alloc(ops, GCHandleType.Pinned);

            // Handle keys
            IntPtr keysPtr = IntPtr.Zero;
            uint nkeys = 0;
            IntPtr[] keyNamePtrs = null!;

            // if (false) // Diagnostic: Disable keys to check for crash
            if (keys != null && keys.Length > 0)
            {
                int nativeKeySize = Marshal.SizeOf<DdsKeyDescriptorNative>();
                keysPtr = Marshal.AllocHGlobal(nativeKeySize * keys.Length);

                keyNamePtrs = new IntPtr[keys.Length];

                for (int i = 0; i < keys.Length; i++)
                {
                    var nativeKey = new DdsKeyDescriptorNative();
                    nativeKey.Name = Marshal.StringToHGlobalAnsi(keys[i].Name);
                    keyNamePtrs[i] = nativeKey.Name;
                    nativeKey.Index = keys[i].Index;

                    if (keys[i].Offset == 0)
                    {
                        // Use recursive/smart offset calculation for all keys (handles dot notation and case mismatch)
                        nativeKey.Offset = (uint)GetRecursiveOffset(typeof(T), keys[i].Name);
                    }
                    else
                    {
                        nativeKey.Offset = keys[i].Offset;
                    }

                    IntPtr itemPtr = IntPtr.Add(keysPtr, i * nativeKeySize);
                    Marshal.StructureToPtr(nativeKey, itemPtr, false);
                }

                nkeys = (uint)keys.Length;
            }

            // Create descriptor struct
            uint flagset = 0;
            uint sampleSize = 0;
            uint align = 0;

            try
            {
                var flagsMethod = typeof(T).GetMethod("GetDescriptorFlagset", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (flagsMethod != null) flagset = (uint)flagsMethod.Invoke(null, null)!;

                var sizeMethod = typeof(T).GetMethod("GetDescriptorSize", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (sizeMethod != null) sampleSize = (uint)sizeMethod.Invoke(null, null)!;

                var alignMethod = typeof(T).GetMethod("GetDescriptorAlign", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (alignMethod != null) align = (uint)alignMethod.Invoke(null, null)!;
            }
            catch { }

            // Fallback for Size if not generated (for backward compat)
            if (sampleSize == 0)
            {
                try
                {
                    // WARNING: This is dangerous for types with arrays/strings!
                    // Prefer 0 (let middleware guess) or a safe large default if strict size unknown?
                    // CycloneDDS treats 0 as "unknown/let me handle it" for some types, 
                    // but for @final types it might need it.
                    // Using Marshal.SizeOf is better than nothing for simple structs, 
                    // but terrible for arrays. 
                    // Ideally, we always regenerate code.
                    sampleSize = (uint)Marshal.SizeOf<T>();
                }
                catch
                {
                    sampleSize = 4096; // Fallback to avoid 0 size error
                }
            }

            // Fallback for Align
            if (align == 0)
            {
                align = GetAlignment(typeof(T));
            }

            var desc = new DdsTopicDescriptor
            {
                m_size = sampleSize,
                m_align = align,
                m_flagset = flagset,
                m_nkeys = nkeys,
                m_typename = typeNamePtr,
                m_keys = keysPtr,
                m_nops = (uint)ops.Length,
                m_ops = opsHandle.AddrOfPinnedObject(),
                m_meta = IntPtr.Zero,
                type_information = new DdsTypeMetaSer { data = IntPtr.Zero, sz = 0 },
                type_mapping = new DdsTypeMetaSer { data = IntPtr.Zero, sz = 0 },
                restrict_data_representation = 0
            };

            // Alloc descriptor memory
            IntPtr descPtr = Marshal.AllocHGlobal(Marshal.SizeOf<DdsTopicDescriptor>());
            Marshal.StructureToPtr(desc, descPtr, false);

            // Hand the resources back so they live and die with the topic entity
            resource = new TopicResource(descPtr, typeNamePtr, opsHandle, keysPtr, keyNamePtrs);

            return descPtr;
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
            DdsApi.dds_get_guid(NativeEntity.Handle, out var myGuid);

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

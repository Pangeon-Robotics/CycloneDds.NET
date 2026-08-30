using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CycloneDDS.Core;
using CycloneDDS.Runtime.Interop;
using CycloneDDS.Runtime.Memory;
using CycloneDDS.Runtime.Tracking;
using CycloneDDS.Schema;

namespace CycloneDDS.Runtime
{
    public sealed class DdsWriter<T> : IDisposable
    {
        // Cached delegates to prevent allocation per call
        private static readonly Func<DdsApi.DdsEntity, IntPtr, int> _writeOperation = DdsApi.dds_writecdr;
        private static readonly Func<DdsApi.DdsEntity, IntPtr, int> _disposeOperation = DdsApi.dds_dispose_serdata;
        private static readonly Func<DdsApi.DdsEntity, IntPtr, int> _unregisterOperation = DdsApi.dds_unregister_serdata;


        private DdsEntityHandle? _writerHandle;
        private DdsApi.DdsEntity _topicHandle;
        private DdsParticipant? _participant;
        private readonly string _topicName;

        // Async/Events
        private IntPtr _listener = IntPtr.Zero;
        private GCHandle _paramHandle;
        private readonly object _listenerLock = new object();
        private readonly DdsApi.DdsOnPublicationMatched _publicationMatchedHandler;
        private volatile TaskCompletionSource<bool>? _waitForReaderTaskSource;
        private EventHandler<DdsApi.DdsPublicationMatchedStatus>? _publicationMatched;

        // Native Marshaling Delegates
        private delegate int GetNativeSizeDelegate(in T sample);
        private delegate void MarshalToNativeDelegate(in T sample, IntPtr target, ref NativeArena arena);

        private static readonly GetNativeSizeDelegate? _nativeSizer;
        private static readonly MarshalToNativeDelegate? _nativeMarshaller;
        private static readonly GetNativeSizeDelegate? _keyNativeSizer;
        private static readonly MarshalToNativeDelegate? _keyNativeMarshaller;

        private static readonly int _nativeHeadSize;
        private static readonly int _keyNativeHeadSize;

        private static readonly DdsExtensibilityKind _extensibilityKind;

        static DdsWriter()
        {
            var attr = typeof(T).GetCustomAttribute<DdsExtensibilityAttribute>();
            _extensibilityKind = attr?.Kind ?? DdsExtensibilityKind.Appendable;

            try
            {
                // Native Marshaling
                _nativeSizer = CreateNativeSizerDelegate("GetNativeSize");
                _nativeMarshaller = CreateNativeMarshallerDelegate("MarshalToNative");
                var headSizeMethod = typeof(T).GetMethod("GetNativeHeadSize", BindingFlags.Public | BindingFlags.Static);
                if (headSizeMethod != null) _nativeHeadSize = (int)(headSizeMethod.Invoke(null, null) ?? 0);

                // Native Key Marshaling
                _keyNativeSizer = CreateNativeSizerDelegate("GetKeyNativeSize");
                _keyNativeMarshaller = CreateNativeMarshallerDelegate("MarshalKeyToNative");
                var keyHeadSizeMethod = typeof(T).GetMethod("GetKeyNativeHeadSize", BindingFlags.Public | BindingFlags.Static);
                if (keyHeadSizeMethod != null) _keyNativeHeadSize = (int)(keyHeadSizeMethod.Invoke(null, null) ?? 0);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DdsWriter<{typeof(T).Name}>] Failed to create delegates: {ex.Message}");
            }
        }

        /// <summary>
        /// Creates a writer for <typeparamref name="T"/> on <paramref name="participant"/>.
        /// </summary>
        /// <param name="participant">Owning participant. The topic is created or fetched from its cache.</param>
        /// <param name="topicName">
        /// Topic name; when null it is taken from the type's <c>[DdsTopic]</c> attribute.
        /// </param>
        /// <param name="qos">
        /// QoS profile to apply. When null the type's <c>[DdsQos]</c> attribute is used, falling
        /// back to <see cref="DdsQos.SystemDefault"/> — i.e. Cyclone's own defaults — when the
        /// type carries no such attribute.
        /// </param>
        /// <param name="partition">Partition to publish into.</param>
        /// <exception cref="InvalidOperationException">
        /// <typeparamref name="T"/> lacks the generated native marshalling methods, or no topic
        /// name was supplied and the type has no <c>[DdsTopic]</c> attribute.
        /// </exception>
        /// <exception cref="DdsException">Cyclone rejected the writer.</exception>
        public DdsWriter(DdsParticipant participant, string? topicName = null, DdsQos? qos = default, string? partition = null)
        {
            topicName ??= GetTopicNameFromAttribute();
            _participant = participant;
            _topicName = topicName!;
            _publicationMatchedHandler = OnPublicationMatched;

            if (_nativeSizer == null || _nativeMarshaller == null)
            {
                throw new InvalidOperationException($"Type {typeof(T).Name} does not exhibit expected DDS generated native methods (GetNativeSize, MarshalToNative).");
            }

            // Use the provided QoS, one provided from the type attribute, or the default
            DdsQos chosenQos = qos;

            if (chosenQos is null)
            {
                var qosAttr = typeof(T).GetCustomAttribute<DdsQosAttribute>();
                if (qosAttr != null)
                {
                    chosenQos = DdsQos.FromAttribute(qosAttr);
                }
                else
                {
                    // No attribute: leave every policy to Cyclone, matching what an empty
                    // dds_create_qos() produced before QoS profiles existed.
                    chosenQos = DdsQos.SystemDefault;
                }
            }

            nint nativeQos = chosenQos.CreateNative();

            try
            {
                string? activePartition = partition ?? participant.DefaultPartition;
                if (!string.IsNullOrEmpty(activePartition))
                {
                    DdsApi.dds_qset_partition(nativeQos, 1, [activePartition]);
                }

                _topicHandle = participant.GetOrRegisterTopic<T>(topicName, nativeQos);

                DdsApi.DdsEntity writer = DdsApi.dds_create_writer(
                    participant.NativeEntity,
                    _topicHandle,
                    nativeQos,
                    IntPtr.Zero);

                if (!writer.IsValid)
                    throw new DdsException(DdsApi.DdsReturnCode.Error, "Failed to create writer");

                _writerHandle = new DdsEntityHandle(writer);
            }
            finally
            {
                // The native object has been copied into the entity, and we can free the QoS object that we own
                DdsApi.dds_delete_qos(nativeQos);
            }

            // Notify participant (triggers identity publishing if enabled)
            // Skip for the identity writer itself to avoid recursion
            if (typeof(T) != typeof(SenderIdentity))
            {
                _participant.RegisterWriter();
            }
        }

        private static string GetTopicNameFromAttribute()
        {
            var attr = typeof(T).GetCustomAttribute<DdsTopicAttribute>();
            if (attr == null) throw new InvalidOperationException($"Type {typeof(T).Name} is missing [DdsTopic] attribute. You must specify topicName manually.");
            return attr.TopicName;
        }

        public void Write(in T sample)
        {
            if (_nativeSizer != null && _nativeMarshaller != null)
            {
                PerformNativeOperation(sample, DdsApi.dds_write, false);
            }
            else
            {
                throw new InvalidOperationException("Native delegates missing.");
            }
        }

        /// <summary>
        /// Dispose an instance.
        /// Marks the instance as NOT_ALIVE_DISPOSED in the reader.
        /// </summary>
        /// <param name="sample">Sample containing the key to dispose (non-key fields ignored)</param>
        /// <remarks>
        /// For keyed topics only. The key fields identify which instance to dispose.
        /// Non-key fields are serialized but ignored by CycloneDDS.
        /// This operation maintains the zero-allocation guarantee.
        /// </remarks>
        public void DisposeInstance(in T sample)
        {
            if (_keyNativeSizer != null && _keyNativeMarshaller != null)
            {
                PerformNativeOperation(sample, DdsApi.dds_dispose, true);
            }
            else
            {
                throw new InvalidOperationException("Native Key delegates missing.");
            }
        }

        /// <summary>
        /// Unregister an instance (writer releases ownership).
        /// Notifies readers that this writer will no longer update the instance.
        /// Reader instance state will transition to NOT_ALIVE_NO_WRITERS if no other writers exist.
        /// </summary>
        /// <param name="sample">Sample containing the key to unregister (non-key fields ignored)</param>
        /// <remarks>
        /// Useful for graceful shutdown or ownership transfer scenarios.
        /// For keyed topics only. The key fields identify which instance to unregister.
        /// Non-key fields are serialized but ignored by CycloneDDS.
        /// This operation maintains the zero-allocation guarantee.
        /// </remarks>
        public void UnregisterInstance(in T sample)
        {
            if (_keyNativeSizer != null && _keyNativeMarshaller != null)
            {
                PerformNativeOperation(sample, DdsApi.dds_unregister_instance, true);
            }
            else
            {
                throw new InvalidOperationException("Native Key delegates missing.");
            }
        }

        private void PerformNativeOperation(in T sample, Func<DdsApi.DdsEntity, IntPtr, int> operation, bool isKey)
        {
            if (_writerHandle == null) throw new ObjectDisposedException(nameof(DdsWriter<T>));

            var sizer = isKey ? _keyNativeSizer : _nativeSizer;
            var marshaller = isKey ? _keyNativeMarshaller : _nativeMarshaller;
            var headSize = isKey ? _keyNativeHeadSize : _nativeHeadSize;

            // Safety check - should be guaranteed by caller
            if (sizer == null || marshaller == null) return;

            int totalSize = sizer(sample);
            byte[] buffer = Arena.Rent(totalSize);

            try
            {
                unsafe
                {
                    fixed (byte* p = buffer)
                    {
                        IntPtr ptr = (IntPtr)p;

                        if (headSize == 0) headSize = totalSize;

                        var span = buffer.AsSpan(0, totalSize);
                        var arena = new NativeArena(span, ptr, headSize);

                        marshaller(sample, ptr, ref arena);

                        int ret = operation(_writerHandle.NativeHandle, ptr);
                        if (ret < 0) throw new DdsException((DdsApi.DdsReturnCode)ret, $"Native operation failed: {ret}");
                    }
                }
            }
            finally
            {
                Arena.Return(buffer);
            }
        }


        public event EventHandler<DdsApi.DdsPublicationMatchedStatus>? PublicationMatched
        {
            add
            {
                lock (_listenerLock)
                {
                    _publicationMatched += value;
                    EnsureListenerAttached();
                }
            }
            remove
            {
                lock (_listenerLock)
                {
                    _publicationMatched -= value;
                }
            }
        }

        public DdsApi.DdsPublicationMatchedStatus CurrentStatus
        {
            get
            {
                if (_writerHandle == null) throw new ObjectDisposedException(nameof(DdsWriter<T>));
                DdsApi.dds_get_publication_matched_status(_writerHandle.NativeHandle.Handle, out var status);
                return status;
            }
        }

        /// <summary>
        /// OFFERED_INCOMPATIBLE_QOS: a reader was found on this topic whose requested QoS the
        /// writer cannot satisfy, so the two did not match. <c>LastPolicyId</c> identifies the
        /// offending policy.
        /// </summary>
        /// <remarks>
        /// Reading a status resets its <c>*_change</c> counters, so each read reports only what
        /// happened since the previous one.
        /// </remarks>
        public DdsApi.DdsOfferedIncompatibleQosStatus OfferedIncompatibleQosStatus
        {
            get
            {
                if (_writerHandle == null) throw new ObjectDisposedException(nameof(DdsWriter<T>));
                DdsApi.dds_get_offered_incompatible_qos_status(_writerHandle.NativeHandle.Handle, out var status);
                return status;
            }
        }

        /// <summary>
        /// OFFERED_DEADLINE_MISSED: this writer failed to publish an instance within the
        /// DEADLINE period it committed to. Always zero when no deadline is configured.
        /// </summary>
        /// <remarks>
        /// Reading a status resets its <c>*_change</c> counters, so each read reports only what
        /// happened since the previous one.
        /// </remarks>
        public DdsApi.DdsOfferedDeadlineMissedStatus OfferedDeadlineMissedStatus
        {
            get
            {
                if (_writerHandle == null) throw new ObjectDisposedException(nameof(DdsWriter<T>));
                DdsApi.dds_get_offered_deadline_missed_status(_writerHandle.NativeHandle.Handle, out var status);
                return status;
            }
        }

        /// <summary>
        /// LIVELINESS_LOST: this writer failed to assert liveliness within its lease duration
        /// and was declared not-alive to its readers. Always zero when the liveliness lease is
        /// infinite.
        /// </summary>
        /// <remarks>
        /// Reading a status resets its <c>*_change</c> counters, so each read reports only what
        /// happened since the previous one.
        /// </remarks>
        public DdsApi.DdsLivelinessLostStatus LivelinessLostStatus
        {
            get
            {
                if (_writerHandle == null) throw new ObjectDisposedException(nameof(DdsWriter<T>));
                DdsApi.dds_get_liveliness_lost_status(_writerHandle.NativeHandle.Handle, out var status);
                return status;
            }
        }

        public async Task<bool> WaitForReaderAsync(TimeSpan timeout = default)
        {
            if (CurrentStatus.CurrentCount > 0) return true;

            EnsureListenerAttached();

            if (CurrentStatus.CurrentCount > 0) return true;

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waitForReaderTaskSource = tcs;

            if (CurrentStatus.CurrentCount > 0)
            {
                _waitForReaderTaskSource = null;
                return true;
            }

            using var timeoutCts = new CancellationTokenSource(timeout == default ? TimeSpan.FromMilliseconds(-1) : timeout);
            using (timeoutCts.Token.Register(() => tcs.TrySetResult(false)))
            {
                return await tcs.Task;
            }
        }

        private void EnsureListenerAttached()
        {
            if (_listener != IntPtr.Zero) return;

            lock (_listenerLock)
            {
                if (_listener != IntPtr.Zero) return;

                _paramHandle = GCHandle.Alloc(this);
                _listener = DdsApi.dds_create_listener(GCHandle.ToIntPtr(_paramHandle));
                DdsApi.dds_lset_publication_matched(_listener, _publicationMatchedHandler);

                if (_writerHandle != null)
                {
                    DdsApi.dds_writer_set_listener(_writerHandle.NativeHandle, _listener);
                }
            }
        }

        // [MonoPInvokeCallback(typeof(DdsApi.DdsOnPublicationMatched))]
        private static void OnPublicationMatched(int writer, DdsApi.DdsPublicationMatchedStatus status, IntPtr arg)
        {
            if (arg == IntPtr.Zero) return;
            try
            {
                var handle = GCHandle.FromIntPtr(arg);
                if (handle.IsAllocated && handle.Target is DdsWriter<T> self)
                {
                    self._publicationMatched?.Invoke(self, status);

                    if (status.CurrentCount > 0)
                    {
                        self._waitForReaderTaskSource?.TrySetResult(true);
                    }
                }
            }
            catch { }
        }

        public DdsInstanceHandle LookupInstance(in T keySample)
        {
            if (_writerHandle == null) throw new ObjectDisposedException(nameof(DdsWriter<T>));

            if (_keyNativeSizer != null && _keyNativeMarshaller != null)
            {
                int size = _keyNativeSizer(keySample);
                byte[] buffer = Arena.Rent(size);
                try
                {
                    unsafe
                    {
                        fixed (byte* p = buffer)
                        {
                            int headSize = _keyNativeHeadSize;
                            if (headSize == 0) headSize = size;

                            IntPtr ptr = (IntPtr)p;
                            var span = buffer.AsSpan(0, size);
                            var arena = new NativeArena(span, ptr, headSize);

                            _keyNativeMarshaller(keySample, ptr, ref arena);

                            long handle = DdsApi.dds_lookup_instance(_writerHandle.NativeHandle.Handle, ptr);
                            return new DdsInstanceHandle(handle);
                        }
                    }
                }
                finally
                {
                    Arena.Return(buffer);
                }
            }
            throw new InvalidOperationException("Native Key delegates missing.");
        }

        public void Dispose()
        {
            if (_writerHandle == null) return;

            if (typeof(T) != typeof(SenderIdentity))
            {
                _participant?.UnregisterWriter();
            }

            if (_listener != IntPtr.Zero)
            {
                DdsApi.dds_delete_listener(_listener);
                _listener = IntPtr.Zero;
            }
            if (_paramHandle.IsAllocated) _paramHandle.Free();

            _writerHandle?.Dispose();
            _writerHandle = null;
            _topicHandle = DdsApi.DdsEntity.Null;
            _participant = null;
        }

        // --- Delegate Generators ---

        private static GetNativeSizeDelegate? CreateNativeSizerDelegate(string methodName)
        {
            var method = typeof(T).GetMethod(methodName, BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(T).MakeByRefType() }, null);
            if (method == null) return null;

            return (GetNativeSizeDelegate)Delegate.CreateDelegate(typeof(GetNativeSizeDelegate), method);
        }

        private static MarshalToNativeDelegate? CreateNativeMarshallerDelegate(string methodName)
        {
            var method = typeof(T).GetMethod(methodName, BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(T).MakeByRefType(), typeof(IntPtr), typeof(NativeArena).MakeByRefType() }, null);
            if (method == null) return null;

            return (MarshalToNativeDelegate)Delegate.CreateDelegate(typeof(MarshalToNativeDelegate), method);
        }
    }

}

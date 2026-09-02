using System;
using System.Buffers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using CycloneDDS.Core;
using CycloneDDS.Runtime.Interop;
using CycloneDDS.Runtime.Memory;
using CycloneDDS.Runtime.Tracking;
using CycloneDDS.Schema;

namespace CycloneDDS.Runtime
{
    public sealed class DdsReader<T> : IDdsReader, IInternalDdsEntity, IDisposable
        where T : new()
    {
        private SenderRegistry? _registry;
        private DdsEntityHandle? _readerHandle;
        private DdsApi.DdsEntity _topicHandle;
        private readonly string _topicName;
        private DdsParticipant? _participant;

        private IntPtr _listener = IntPtr.Zero;
        private GCHandle _paramHandle;
        private volatile TaskCompletionSource<bool>? _waitTaskSource;
        private readonly DdsApi.DdsOnDataAvailable _dataAvailableHandler;
        private readonly DdsApi.DdsOnSubscriptionMatched _subscriptionMatchedHandler;
        private readonly object _listenerLock = new object();

        private volatile Predicate<T>? _filter;

        private EventHandler<DdsApi.DdsSubscriptionMatchedStatus>? _subscriptionMatched;
        public event EventHandler<DdsApi.DdsSubscriptionMatchedStatus>? SubscriptionMatched
        {
            add
            {
                lock (_listenerLock)
                {
                    _subscriptionMatched += value;
                    EnsureListenerAttached();
                }
            }
            remove
            {
                lock (_listenerLock)
                {
                    _subscriptionMatched -= value;
                }
            }
        }

        public DdsApi.DdsSubscriptionMatchedStatus CurrentStatus
        {
            get
            {
                if (_readerHandle == null) throw new ObjectDisposedException(nameof(DdsReader<T>));
                DdsApi.dds_get_subscription_matched_status(_readerHandle.NativeHandle.Handle, out var status);
                return status;
            }
        }

        /// <summary>
        /// REQUESTED_INCOMPATIBLE_QOS: a writer was found on this topic whose offered QoS does
        /// not satisfy what this reader requested, so the two did not match.
        /// <c>LastPolicyId</c> identifies the offending policy.
        /// </summary>
        /// <remarks>
        /// Reading a status resets its <c>*_change</c> counters, so each read reports only what
        /// happened since the previous one.
        /// </remarks>
        public DdsApi.DdsRequestedIncompatibleQosStatus RequestedIncompatibleQosStatus
        {
            get
            {
                if (_readerHandle == null) throw new ObjectDisposedException(nameof(DdsReader<T>));
                DdsApi.dds_get_requested_incompatible_qos_status(_readerHandle.NativeHandle.Handle, out var status);
                return status;
            }
        }

        /// <summary>
        /// REQUESTED_DEADLINE_MISSED: no sample arrived for an instance within the DEADLINE
        /// period this reader requested. Always zero when no deadline is configured.
        /// </summary>
        /// <remarks>
        /// Reading a status resets its <c>*_change</c> counters, so each read reports only what
        /// happened since the previous one.
        /// </remarks>
        public DdsApi.DdsRequestedDeadlineMissedStatus RequestedDeadlineMissedStatus
        {
            get
            {
                if (_readerHandle == null) throw new ObjectDisposedException(nameof(DdsReader<T>));
                DdsApi.dds_get_requested_deadline_missed_status(_readerHandle.NativeHandle.Handle, out var status);
                return status;
            }
        }

        /// <summary>
        /// LIVELINESS_CHANGED: how many matched writers are currently alive versus not-alive
        /// from this reader's point of view.
        /// </summary>
        /// <remarks>
        /// Reading a status resets its <c>*_change</c> counters, so each read reports only what
        /// happened since the previous one.
        /// </remarks>
        public DdsApi.DdsLivelinessChangedStatus LivelinessChangedStatus
        {
            get
            {
                if (_readerHandle == null) throw new ObjectDisposedException(nameof(DdsReader<T>));
                DdsApi.dds_get_liveliness_changed_status(_readerHandle.NativeHandle.Handle, out var status);
                return status;
            }
        }

        // IDdsReader
        public Type DataType => typeof(T);

        // IInternalDdsEntity (explicit — not visible to public consumers)
        DdsApi.DdsEntity IInternalDdsEntity.NativeEntity =>
            _readerHandle?.NativeHandle ?? throw new ObjectDisposedException(nameof(DdsReader<T>));

        private delegate int GetNativeSizeDelegate(in T sample);
        private delegate void MarshalToNativeDelegate(in T sample, IntPtr target, ref NativeArena arena);

        private static readonly GetNativeSizeDelegate? _nativeSizer;
        private static readonly MarshalToNativeDelegate? _nativeMarshaller;
        private static readonly int _nativeHeadSize;

        static DdsReader()
        {
            try
            {
                var nativeSizeMethod = typeof(T).GetMethod("GetNativeSize", new[] { typeof(T).MakeByRefType() });
                if (nativeSizeMethod != null) _nativeSizer = (GetNativeSizeDelegate)nativeSizeMethod.CreateDelegate(typeof(GetNativeSizeDelegate));

                var toNativeMethod = typeof(T).GetMethod("MarshalToNative", new[] { typeof(T).MakeByRefType(), typeof(IntPtr), typeof(NativeArena).MakeByRefType() });
                if (toNativeMethod != null) _nativeMarshaller = (MarshalToNativeDelegate)toNativeMethod.CreateDelegate(typeof(MarshalToNativeDelegate));

                var headSizeMethod = typeof(T).GetMethod("GetNativeHeadSize", BindingFlags.Public | BindingFlags.Static);
                if (headSizeMethod != null) _nativeHeadSize = (int)(headSizeMethod.Invoke(null, null) ?? 0);
            }
            catch (Exception ex) { Console.WriteLine($"[DdsReader] Initialization failed: {ex}"); throw; }
        }

        /// <summary>
        /// Creates a reader for <typeparamref name="T"/> on <paramref name="participant"/>.
        /// </summary>
        /// <param name="participant">
        /// Owning participant. The native topic is created on the first use of this (topic name,
        /// type) pair, shared with every other endpoint using it, and deleted once the last of
        /// them — this reader included — is disposed.
        /// </param>
        /// <param name="topicName">
        /// Topic name; when null it is taken from the type's <c>[DdsTopic]</c> attribute.
        /// </param>
        /// <param name="qos">
        /// QoS profile to apply. When null the type's <c>[DdsQos]</c> attribute is used, falling
        /// back to <see cref="DdsQos.SystemDefault"/> — i.e. Cyclone's own defaults — when the
        /// type carries no such attribute.
        /// </param>
        /// <param name="partition">Partition to subscribe in.</param>
        /// <exception cref="InvalidOperationException">
        /// No topic name was supplied and the type has no <c>[DdsTopic]</c> attribute.
        /// </exception>
        /// <exception cref="DdsException">Cyclone rejected the reader.</exception>
        public DdsReader(DdsParticipant participant, string? topicName = null, DdsQos? qos = default, string? partition = null)
        {
            _dataAvailableHandler = OnDataAvailable;
            _subscriptionMatchedHandler = OnSubscriptionMatched;

            topicName ??= GetTopicNameFromAttribute();
            _topicName = topicName;
            _participant = participant;

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

                _topicHandle = participant.RegisterTopic<T>(topicName);

                try
                {
                    DdsApi.DdsEntity reader = DdsApi.dds_create_reader(
                        participant.NativeEntity,
                        _topicHandle,
                        nativeQos,
                        IntPtr.Zero);

                    if (!reader.IsValid)
                    {
                        int err = reader.Handle;
                        DdsApi.DdsReturnCode rc = (DdsApi.DdsReturnCode)err;
                        throw new DdsException(rc, $"Failed to create reader for '{topicName}'");
                    }

                    _readerHandle = new DdsEntityHandle(reader);
                }
                catch
                {
                    // No reader took ownership of the registration, so give it straight back
                    participant.ReleaseTopic<T>(topicName);
                    _topicHandle = DdsApi.DdsEntity.Null;
                    throw;
                }
            }
            finally
            {
                // The native object has been copied into the entity, and we can free the QoS object that we own
                DdsApi.dds_delete_qos(nativeQos);
            }
        }

        public void SetFilter(Predicate<T>? filter) => _filter = filter;

        private static string GetTopicNameFromAttribute()
        {
            var attr = typeof(T).GetCustomAttribute<DdsTopicAttribute>();
            if (attr == null) throw new InvalidOperationException($"Type {typeof(T).Name} is missing [DdsTopic] attribute. You must specify topicName manually.");
            return attr.TopicName;
        }

        public DdsLoan<T> Take(int maxSamples = 32) => ReadOrTake(maxSamples, 0xFFFFFFFF, true);
        public DdsLoan<T> Read(int maxSamples = 32) => ReadOrTake(maxSamples, 0xFFFFFFFF, false);

        public DdsLoan<T> Take(int maxSamples, DdsSampleState sampleState, DdsViewState viewState, DdsInstanceState instanceState)
             => ReadOrTake(maxSamples, (uint)sampleState | (uint)viewState | (uint)instanceState, true);

        public DdsLoan<T> Read(int maxSamples, DdsSampleState sampleState, DdsViewState viewState, DdsInstanceState instanceState)
             => ReadOrTake(maxSamples, (uint)sampleState | (uint)viewState | (uint)instanceState, false);

        private DdsLoan<T> ReadOrTake(int maxSamples, uint mask, bool isTake)
        {
            if (_readerHandle == null) throw new ObjectDisposedException(nameof(DdsReader<T>));

            var samples = ArrayPool<IntPtr>.Shared.Rent(maxSamples);
            var infos = ArrayPool<DdsApi.DdsSampleInfo>.Shared.Rent(maxSamples);

            Array.Clear(samples, 0, maxSamples);

            int count;
            if (isTake)
                count = DdsApi.dds_take_mask(_readerHandle.NativeHandle.Handle, samples, infos, (UIntPtr)maxSamples, (uint)maxSamples, mask);
            else
                count = DdsApi.dds_read_mask(_readerHandle.NativeHandle.Handle, samples, infos, (UIntPtr)maxSamples, (uint)maxSamples, mask);

            if (count < 0)
            {
                ArrayPool<IntPtr>.Shared.Return(samples);
                ArrayPool<DdsApi.DdsSampleInfo>.Shared.Return(infos);

                if (count == (int)DdsApi.DdsReturnCode.NoData)
                {
                    return new DdsLoan<T>(_readerHandle, null!, null!, 0, _registry, _filter);
                }
                throw new DdsException((DdsApi.DdsReturnCode)count, $"dds_{(isTake ? "take" : "read")} failed: {count}");
            }

            return new DdsLoan<T>(_readerHandle, samples, infos, count, _registry, _filter);
        }

        private bool HasData()
        {
            try
            {
                using var scope = Read(1);
                return scope.Count > 0;
            }
            catch { return false; }
        }

        public async Task<bool> WaitDataAsync(CancellationToken cancellationToken = default)
        {
            if (_readerHandle == null) throw new ObjectDisposedException(nameof(DdsReader<T>));

            EnsureListenerAttached();

            var tcs = _waitTaskSource;
            if (tcs == null || tcs.Task.IsCompleted)
            {
                tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waitTaskSource = tcs;
            }

            if (HasData()) return true;

            using (cancellationToken.Register(() => tcs.TrySetCanceled()))
            {
                try
                {
                    return await tcs.Task;
                }
                catch (TaskCanceledException)
                {
                    if (cancellationToken.IsCancellationRequested) throw;
                    return true;
                }
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
                DdsApi.dds_lset_data_available(_listener, _dataAvailableHandler);
                DdsApi.dds_lset_subscription_matched(_listener, _subscriptionMatchedHandler);

                if (_readerHandle != null)
                {
                    DdsApi.dds_reader_set_listener(_readerHandle.NativeHandle, _listener);
                }
            }
        }

        private static void OnSubscriptionMatched(int reader, DdsApi.DdsSubscriptionMatchedStatus status, IntPtr arg)
        {
            if (arg == IntPtr.Zero) return;
            try
            {
                var handle = GCHandle.FromIntPtr(arg);
                if (handle.IsAllocated && handle.Target is DdsReader<T> self)
                {
                    self._subscriptionMatched?.Invoke(self, status);
                }
            }
            catch { }
        }

        private static void OnDataAvailable(int reader, IntPtr arg)
        {
            if (arg == IntPtr.Zero) return;
            try
            {
                var handle = GCHandle.FromIntPtr(arg);
                if (handle.IsAllocated && handle.Target is DdsReader<T> self)
                {
                    self._waitTaskSource?.TrySetResult(true);
                }
            }
            catch { }
        }

        private T[] TakeBatch()
        {
            using var scope = Take();
            if (scope.Count == 0) return Array.Empty<T>();
            var batch = new T[scope.Count];
            int i = 0;
            foreach (var item in scope)
            {
                if (item.IsValid)
                    batch[i++] = item.Data;
                else
                    batch[i++] = default;
            }
            return batch;
        }

        public async IAsyncEnumerable<T> StreamAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var batch = TakeBatch();
                if (batch.Length > 0)
                {
                    foreach (var item in batch) yield return item;
                    continue;
                }

                await WaitDataAsync(cancellationToken);

                batch = TakeBatch();
                foreach (var item in batch) yield return item;
            }
        }

        public void Dispose()
        {
            if (_listener != IntPtr.Zero)
            {
                DdsApi.dds_delete_listener(_listener);
                _listener = IntPtr.Zero;
            }
            if (_paramHandle.IsAllocated) _paramHandle.Free();

            _readerHandle?.Dispose();
            _readerHandle = null;

            // Null participant means we already released; keeps a second Dispose harmless
            _participant?.ReleaseTopic<T>(_topicName);
            _topicHandle = DdsApi.DdsEntity.Null;
            _participant = null;
        }

        public DdsInstanceHandle LookupInstance(in T keySample)
        {
            if (_readerHandle == null) throw new ObjectDisposedException(nameof(DdsReader<T>));

            if (_nativeMarshaller != null && _nativeSizer != null)
            {
                int size = _nativeSizer(in keySample);
                byte[] buffer = ArrayPool<byte>.Shared.Rent(size);
                try
                {
                    unsafe
                    {
                        fixed (byte* ptr = buffer)
                        {
                            NativeArena arena = new NativeArena(new Span<byte>(buffer, 0, size), (IntPtr)ptr, _nativeHeadSize);
                            _nativeMarshaller(in keySample, (IntPtr)ptr, ref arena);
                            long handle = DdsApi.dds_lookup_instance(_readerHandle.NativeHandle.Handle, (IntPtr)ptr);
                            return new DdsInstanceHandle(handle);
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            throw new NotSupportedException("Native marshaling delegates missing for this type.");
        }

        public DdsLoan<T> TakeInstance(DdsInstanceHandle handle, int maxSamples = 1)
        {
            if (_readerHandle == null) throw new ObjectDisposedException(nameof(DdsReader<T>));
            var samples = ArrayPool<IntPtr>.Shared.Rent(maxSamples);
            var infos = ArrayPool<DdsApi.DdsSampleInfo>.Shared.Rent(maxSamples);
            Array.Clear(samples, 0, maxSamples);

            int count = DdsApi.dds_take_instance(_readerHandle.NativeHandle.Handle, samples, infos, (UIntPtr)maxSamples, (uint)maxSamples, handle.Value);

            if (count < 0)
            {
                ArrayPool<IntPtr>.Shared.Return(samples);
                ArrayPool<DdsApi.DdsSampleInfo>.Shared.Return(infos);
                if (count == (int)DdsApi.DdsReturnCode.NoData) return new DdsLoan<T>(_readerHandle, null!, null!, 0, _registry, _filter);
                throw new DdsException((DdsApi.DdsReturnCode)count, $"dds_take_instance failed: {count}");
            }
            return new DdsLoan<T>(_readerHandle, samples, infos, count, _registry, _filter);
        }

        public DdsLoan<T> ReadInstance(DdsInstanceHandle handle, int maxSamples = 1)
        {
            if (_readerHandle == null) throw new ObjectDisposedException(nameof(DdsReader<T>));
            var samples = ArrayPool<IntPtr>.Shared.Rent(maxSamples);
            var infos = ArrayPool<DdsApi.DdsSampleInfo>.Shared.Rent(maxSamples);
            Array.Clear(samples, 0, maxSamples);

            int count = DdsApi.dds_read_instance(_readerHandle.NativeHandle.Handle, samples, infos, (UIntPtr)maxSamples, (uint)maxSamples, handle.Value);

            if (count < 0)
            {
                ArrayPool<IntPtr>.Shared.Return(samples);
                ArrayPool<DdsApi.DdsSampleInfo>.Shared.Return(infos);
                if (count == (int)DdsApi.DdsReturnCode.NoData) return new DdsLoan<T>(_readerHandle, null!, null!, 0, _registry, _filter);
                throw new DdsException((DdsApi.DdsReturnCode)count, $"dds_read_instance failed: {count}");
            }
            return new DdsLoan<T>(_readerHandle, samples, infos, count, _registry, _filter);
        }

        public void EnableSenderTracking(SenderRegistry registry)
        {
            _registry = registry;
            this.SubscriptionMatched += OnSenderTrackingSubscriptionMatched;
        }

        private void OnSenderTrackingSubscriptionMatched(object? sender, DdsApi.DdsSubscriptionMatchedStatus e)
        {
            if (e.CurrentCountChange > 0 && _registry != null)
            {
                var handles = GetMatchedPublicationHandles();
                foreach (var handle in handles)
                {
                    var writerGuid = GetMatchedPublicationGuid(handle);
                    _registry.RegisterRemoteWriter(handle, writerGuid);
                }
            }
        }

        private long[] GetMatchedPublicationHandles()
        {
            if (_readerHandle == null) return Array.Empty<long>();
            var handles = new long[64];
            int count = DdsApi.dds_get_matched_publications(
                _readerHandle.NativeHandle.Handle,
                handles,
                (uint)handles.Length);

            if (count < 0) return Array.Empty<long>();

            if (count > handles.Length)
            {
                handles = new long[count];
                count = DdsApi.dds_get_matched_publications(
                   _readerHandle.NativeHandle.Handle,
                   handles,
                   (uint)handles.Length);
            }

            if (count > 0)
            {
                if (count > handles.Length) count = handles.Length;
                var result = new long[count];
                Array.Copy(handles, result, count);
                return result;
            }
            return Array.Empty<long>();
        }

        private DdsGuid GetMatchedPublicationGuid(long publicationHandle)
        {
            if (_readerHandle == null) return default;

            IntPtr ptr = DdsApi.dds_get_matched_publication_data(
                _readerHandle.NativeHandle.Handle,
                publicationHandle);

            if (ptr != IntPtr.Zero)
            {
                try
                {
                    var data = Marshal.PtrToStructure<DdsApi.DdsBuiltinTopicEndpoint>(ptr);
                    return data.Key;
                }
                finally
                {
                    DdsApi.dds_builtintopic_free_endpoint(ptr);
                }
            }
            return default;
        }


    }


}

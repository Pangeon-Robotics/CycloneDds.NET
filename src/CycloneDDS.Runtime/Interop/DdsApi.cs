using System;
using System.Runtime.InteropServices;
using CycloneDDS.Core;

namespace CycloneDDS.Runtime.Interop
{
    public static class DdsApi
    {
        public const string DLL_NAME = "ddsc";

        // Basic types
        [StructLayout(LayoutKind.Sequential)]
        public struct DdsEntity
        {
            public int Handle;
            public readonly bool IsValid => Handle > 0;

            public static readonly DdsEntity Null = new() { Handle = 0 };

            public override readonly string ToString()
            {
                return $"DdsEntity(0x{Handle:x})";
            }
        }

        public enum DdsReturnCode : int
        {
            Ok = 0,
            Error = -1,
            Timeout = -10,
            PreconditionNotMet = -4,
            AlreadyDeleted = -9,
            HandleExpired = -5,
            NoData = -11,
            IllegalOperation = -12,
            NotAllowedBySecurity = -13,
            Unsupported = -2,
            BadParameter = -3,
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DdsSampleInfo
        {
            public DdsSampleState SampleState;
            public DdsViewState ViewState;
            public DdsInstanceState InstanceState;
            public byte ValidData;
            private byte _pad1;
            private byte _pad2;
            private byte _pad3;
            public long SourceTimestamp;
            public long InstanceHandle;
            public long PublicationHandle;
            public uint DisposedGenerationCount;
            public uint NoWritersGenerationCount;
            public uint SampleRank;
            public uint GenerationRank;
            public uint AbsoluteGenerationCount;
            private uint _pad4;
        }

        // Listener Delegate
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void DdsOnDataAvailable(int reader, IntPtr arg);

        // Listener
        [DllImport(DLL_NAME)]
        public static extern IntPtr dds_create_listener(IntPtr arg);

        [DllImport(DLL_NAME)]
        public static extern void dds_delete_listener(IntPtr listener);

        [DllImport(DLL_NAME)]
        public static extern void dds_lset_data_available(IntPtr listener, DdsOnDataAvailable callback);

        [DllImport(DLL_NAME, EntryPoint = "dds_set_listener")]
        public static extern int dds_reader_set_listener(DdsEntity reader, IntPtr listener);

        [DllImport(DLL_NAME, EntryPoint = "dds_set_listener")]
        public static extern int dds_writer_set_listener(DdsEntity writer, IntPtr listener);

        // Participant
        [DllImport(DLL_NAME)]
        public static extern DdsEntity dds_create_participant(
            uint domain_id,
            IntPtr qos,
            IntPtr listener);

        // Topic
        [DllImport(DLL_NAME)]
        public static extern DdsEntity dds_create_topic(
            DdsEntity participant,
            IntPtr desc,
            [MarshalAs(UnmanagedType.LPStr)] string name,
            IntPtr qos,
            IntPtr listener);

        // Writer
        [DllImport(DLL_NAME)]
        public static extern DdsEntity dds_create_writer(
            DdsEntity participant_or_publisher,
            DdsEntity topic,
            IntPtr qos,
            IntPtr listener);

        // Reader
        [DllImport(DLL_NAME)]
        public static extern DdsEntity dds_create_reader(
            DdsEntity participant_or_subscriber,
            DdsEntity topic,
            IntPtr qos,
            IntPtr listener);

        // ---- Serialized data (CDR) --------------------------------------------------
        //
        // Samples cross the boundary as serdata: a reference-counted CDR payload owned by
        // Cyclone. Writers build one from bytes (Cyclone validates them against the topic's
        // ops); readers receive references and read the bytes in place.

        /// <summary>enum ddsi_serdata_kind</summary>
        public enum SerdataKind
        {
            Empty = 0,
            Key = 1,
            Data = 2,
        }

        /// <summary>ddsrt_iovec_t on POSIX (struct iovec).</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct IovecPosix
        {
            public IntPtr Base;
            public nuint Length;
        }

        /// <summary>ddsrt_iovec_t on Windows.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct IovecWindows
        {
            public uint Length;
            public IntPtr Base;
        }

        [DllImport(DLL_NAME)]
        public static extern int dds_get_entity_sertype(DdsEntity entity, out IntPtr sertype);

        // niov is ddsrt_msg_iovlen_t: size_t on Linux, DWORD on Windows. Passing nuint is
        // correct for both under the x64/arm64 calling conventions.
        [DllImport(DLL_NAME, EntryPoint = "ddsi_serdata_from_ser_iov")]
        private static extern unsafe IntPtr ddsi_serdata_from_ser_iov(IntPtr sertype, SerdataKind kind, nuint niov, void* iov, nuint size);

        [DllImport(DLL_NAME, EntryPoint = "ddsi_serdata_to_ser_ref")]
        private static extern unsafe IntPtr ddsi_serdata_to_ser_ref(IntPtr serdata, nuint offset, nuint size, void* iov);

        [DllImport(DLL_NAME, EntryPoint = "ddsi_serdata_to_ser_unref")]
        private static extern unsafe void ddsi_serdata_to_ser_unref(IntPtr serdata, void* iov);

        [DllImport(DLL_NAME)]
        public static extern uint ddsi_serdata_size(IntPtr serdata);

        [DllImport(DLL_NAME)]
        public static extern void ddsi_serdata_unref(IntPtr serdata);

        /// <summary>
        /// Builds a serdata of <paramref name="sertype"/> from a complete CDR payload (header
        /// included). Cyclone copies the bytes, so <paramref name="payload"/> may be reused at once.
        /// </summary>
        /// <returns>The new reference, or zero when Cyclone rejects the payload as not matching the type.</returns>
        public static unsafe IntPtr SerdataFromCdr(IntPtr sertype, SerdataKind kind, ReadOnlySpan<byte> payload)
        {
            fixed (byte* data = payload)
            {
                if (OperatingSystem.IsWindows())
                {
                    var iov = new IovecWindows { Base = (IntPtr)data, Length = (uint)payload.Length };
                    return ddsi_serdata_from_ser_iov(sertype, kind, 1, &iov, (nuint)payload.Length);
                }
                else
                {
                    var iov = new IovecPosix { Base = (IntPtr)data, Length = (nuint)payload.Length };
                    return ddsi_serdata_from_ser_iov(sertype, kind, 1, &iov, (nuint)payload.Length);
                }
            }
        }

        /// <summary>Deserializes the CDR payload of <paramref name="serdata"/> in place, without copying it.</summary>
        /// <exception cref="System.IO.InvalidDataException">The payload does not match <typeparamref name="T"/>.</exception>
        public static unsafe T DeserializeSerdata<T>(IntPtr serdata) where T : IDdsType<T>
        {
            nuint size = ddsi_serdata_size(serdata);

            if (OperatingSystem.IsWindows())
            {
                IovecWindows iov;
                IntPtr reference = ddsi_serdata_to_ser_ref(serdata, 0, size, &iov);
                try
                {
                    var reader = new CdrReader(new ReadOnlySpan<byte>((void*)iov.Base, (int)iov.Length));
                    return T.Deserialize(ref reader);
                }
                finally { ddsi_serdata_to_ser_unref(reference, &iov); }
            }
            else
            {
                IovecPosix iov;
                IntPtr reference = ddsi_serdata_to_ser_ref(serdata, 0, size, &iov);
                try
                {
                    var reader = new CdrReader(new ReadOnlySpan<byte>((void*)iov.Base, (int)iov.Length));
                    return T.Deserialize(ref reader);
                }
                finally { ddsi_serdata_to_ser_unref(reference, &iov); }
            }
        }

        /// <summary>Writes a serdata; consumes the caller's reference whatever the outcome.</summary>
        [DllImport(DLL_NAME)]
        public static extern int dds_writecdr(DdsEntity writer, IntPtr serdata);

        // On success each buf[i] holds a serdata reference the caller must unref
        [DllImport(DLL_NAME)]
        public static extern int dds_readcdr(
            DdsEntity reader_or_condition,
            [In, Out] IntPtr[] buf,
            uint maxs,
            [In, Out] DdsSampleInfo[] infos,
            uint mask);

        [DllImport(DLL_NAME)]
        public static extern int dds_takecdr(
            DdsEntity reader_or_condition,
            [In, Out] IntPtr[] buf,
            uint maxs,
            [In, Out] DdsSampleInfo[] infos,
            uint mask);

        [DllImport(DLL_NAME)]
        public static extern int dds_assert_liveliness(int entity);

        // QoS Management
        [DllImport(DLL_NAME)]
        public static extern IntPtr dds_create_qos();

        [DllImport(DLL_NAME)]
        public static extern void dds_delete_qos(IntPtr qos);

        // Data Representation QoS
        [DllImport(DLL_NAME)]
        public static extern void dds_qset_data_representation(
            IntPtr qos,
            uint n,
            [In] short[] values);

        [DllImport(DLL_NAME)]
        public static extern void dds_qset_history(IntPtr qos, int kind, int depth);

        [DllImport(DLL_NAME)]
        public static extern void dds_qset_resource_limits(IntPtr qos, int max_samples, int max_instances, int max_samples_per_instance);

        [DllImport(DLL_NAME)]
        public static extern void dds_qset_durability(IntPtr qos, int kind);

        public const int DDS_DURABILITY_VOLATILE = 0;
        public const int DDS_DURABILITY_TRANSIENT_LOCAL = 1;

        [DllImport(DLL_NAME)]
        public static extern void dds_qset_reliability(IntPtr qos, int kind, long max_blocking_time);

        public const int DDS_RELIABILITY_BEST_EFFORT = 0;
        public const int DDS_RELIABILITY_RELIABLE = 1;

        /// <summary>Sets DEADLINE. <paramref name="deadline"/> is a dds_duration_t; see <see cref="Duration"/>.</summary>
        [DllImport(DLL_NAME)]
        public static extern void dds_qset_deadline(nint qos, long deadline);

        /// <summary>Sets LIFESPAN. <paramref name="lifespan"/> is a dds_duration_t; see <see cref="Duration"/>.</summary>
        [DllImport(DLL_NAME)]
        public static extern void dds_qset_lifespan(nint qos, long lifespan);

        /// <summary>
        /// Sets LIVELINESS. <paramref name="kind"/> is one of the <c>DDS_LIVELINESS_*</c> constants;
        /// <paramref name="lease_duration"/> is a dds_duration_t; see <see cref="Duration"/>.
        /// </summary>
        [DllImport(DLL_NAME)]
        public static extern void dds_qset_liveliness(nint qos, int kind, long lease_duration);

        /// <summary>Converts seconds to a dds_duration_t. Null means DDS_INFINITY.</summary>
        public static long Duration(double? seconds)
        {
            if (!seconds.HasValue || double.IsPositiveInfinity(seconds.Value))
            {
                return DDS_INFINITY;
            }

            if (seconds.Value < 0.0 || double.IsNaN(seconds.Value))
            {
                throw new ArgumentOutOfRangeException(nameof(seconds), "duration must be >= 0");
            }

            var ns = seconds.Value * 1_000_000_000.0;
            return ns >= DDS_INFINITY ? DDS_INFINITY : (long)ns;
        }

        // Partition QoS
        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern void dds_qset_partition(
            IntPtr qos,
            uint n,
            [In, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)]
            string[] parts);

        public const int DDS_HISTORY_KEEP_LAST = 0;
        public const int DDS_HISTORY_KEEP_ALL = 1;

        // Status Structs
        [StructLayout(LayoutKind.Sequential)]
        public struct DdsPublicationMatchedStatus
        {
            public uint TotalCount;
            public int TotalCountChange;
            public uint CurrentCount;
            public int CurrentCountChange;
            public long LastSubscriptionHandle;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DdsSubscriptionMatchedStatus
        {
            public uint TotalCount;
            public int TotalCountChange;
            public uint CurrentCount;
            public int CurrentCountChange;
            public long LastPublicationHandle;
        }

        /// <summary>
        /// REQUESTED_INCOMPATIBLE_QOS. Mirrors <c>dds_requested_incompatible_qos_status_t</c>:
        /// a writer was found whose offered QoS does not satisfy this reader's request, so the
        /// two did not match. <c>LastPolicyId</c> identifies the offending policy.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct DdsRequestedIncompatibleQosStatus
        {
            public uint TotalCount;
            public int TotalCountChange;
            public uint LastPolicyId;
        }

        /// <summary>
        /// REQUESTED_DEADLINE_MISSED. Mirrors <c>dds_requested_deadline_missed_status_t</c>:
        /// no sample arrived for an instance within the DEADLINE this reader requested.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct DdsRequestedDeadlineMissedStatus
        {
            public uint TotalCount;
            public int TotalCountChange;
            public ulong LastInstanceHandle;
        }

        /// <summary>
        /// LIVELINESS_CHANGED. Mirrors <c>dds_liveliness_changed_status_t</c>: how many matched
        /// writers are currently alive versus not-alive from this reader's point of view.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct DdsLivelinessChangedStatus
        {
            public uint AliveCount;
            public uint NotAliveCount;
            public int AliveCountChange;
            public int NotAliveCountChange;
            public ulong LastPublicationHandle;
        }

        /// <summary>
        /// OFFERED_INCOMPATIBLE_QOS. Mirrors <c>dds_offered_incompatible_qos_status_t</c>:
        /// a reader was found whose requested QoS this writer cannot satisfy, so the two did
        /// not match. <c>LastPolicyId</c> identifies the offending policy.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct DdsOfferedIncompatibleQosStatus
        {
            public uint TotalCount;
            public int TotalCountChange;
            public uint LastPolicyId;
        }

        /// <summary>
        /// OFFERED_DEADLINE_MISSED. Mirrors <c>dds_offered_deadline_missed_status_t</c>:
        /// this writer failed to publish an instance within the DEADLINE it committed to.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct DdsOfferedDeadlineMissedStatus
        {
            public uint TotalCount;
            public int TotalCountChange;
            public ulong LastInstanceHandle;
        }

        /// <summary>
        /// LIVELINESS_LOST. Mirrors <c>dds_liveliness_lost_status_t</c>: this writer failed to
        /// assert liveliness within its lease duration and was declared not-alive to readers.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct DdsLivelinessLostStatus
        {
            public uint TotalCount;
            public int TotalCountChange;
        }

        // NOTE: status is passed BY VALUE, matching the C signature
        //   void (*)(dds_entity_t, const dds_..._matched_status_t status, void *arg)
        // These 24-byte structs are >16 bytes, so the Windows x64 ABI passes them by an
        // implicit pointer (which a `ref` parameter happened to match) while the Linux
        // System V ABI passes them by value on the stack. Declaring the parameter `ref`
        // therefore worked only on Windows; on Linux it shifted `arg` into the wrong
        // register, so GCHandle.FromIntPtr(arg) threw and the callback was silently
        // dropped. Declaring it by value lets the .NET marshaller emit the correct
        // per-platform ABI on both.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void DdsOnPublicationMatched(int writer, DdsPublicationMatchedStatus status, IntPtr arg);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void DdsOnSubscriptionMatched(int reader, DdsSubscriptionMatchedStatus status, IntPtr arg);

        [DllImport(DLL_NAME)]
        public extern static void dds_lset_publication_matched(IntPtr listener, DdsOnPublicationMatched callback);

        [DllImport(DLL_NAME)]
        public extern static void dds_lset_subscription_matched(IntPtr listener, DdsOnSubscriptionMatched callback);

        [DllImport(DLL_NAME)]
        public extern static int dds_get_publication_matched_status(int writer, out DdsPublicationMatchedStatus status);

        [DllImport(DLL_NAME)]
        public extern static int dds_get_subscription_matched_status(int reader, out DdsSubscriptionMatchedStatus status);

        [DllImport(DLL_NAME)]
        public extern static int dds_get_status_changes(int entity, out uint status);

        // NOTE: every dds_get_*_status call resets that status's *_change counters as a side
        // effect, so each call reports only what happened since the previous one. Returns a
        // dds_return_t; a deleted entity yields DDS_RETCODE_ALREADY_DELETED and a zeroed struct.

        /// <summary>Reads and resets the reader's REQUESTED_INCOMPATIBLE_QOS status.</summary>
        [DllImport(DLL_NAME)]
        public static extern int dds_get_requested_incompatible_qos_status(int reader, out DdsRequestedIncompatibleQosStatus status);

        /// <summary>Reads and resets the reader's REQUESTED_DEADLINE_MISSED status.</summary>
        [DllImport(DLL_NAME)]
        public static extern int dds_get_requested_deadline_missed_status(int reader, out DdsRequestedDeadlineMissedStatus status);

        /// <summary>Reads and resets the reader's LIVELINESS_CHANGED status.</summary>
        [DllImport(DLL_NAME)]
        public static extern int dds_get_liveliness_changed_status(int reader, out DdsLivelinessChangedStatus status);

        /// <summary>Reads and resets the writer's OFFERED_INCOMPATIBLE_QOS status.</summary>
        [DllImport(DLL_NAME)]
        public static extern int dds_get_offered_incompatible_qos_status(int writer, out DdsOfferedIncompatibleQosStatus status);

        /// <summary>Reads and resets the writer's OFFERED_DEADLINE_MISSED status.</summary>
        [DllImport(DLL_NAME)]
        public static extern int dds_get_offered_deadline_missed_status(int writer, out DdsOfferedDeadlineMissedStatus status);

        /// <summary>Reads and resets the writer's LIVELINESS_LOST status.</summary>
        [DllImport(DLL_NAME)]
        public static extern int dds_get_liveliness_lost_status(int writer, out DdsLivelinessLostStatus status);

        /// <summary>
        /// Get the GUID of a DDS entity (participant, reader, writer).
        /// </summary>
        /// <param name="entity">Entity handle</param>
        /// <param name="guid">Output GUID</param>
        /// <returns>0 on success, negative error code on failure</returns>
        [DllImport(DLL_NAME)]
        public static extern int dds_get_guid(int entity, out DdsGuid guid);

        /// <summary>
        /// Get list of publication handles currently matched to a reader.
        /// </summary>
        /// <param name="reader">Reader entity</param>
        /// <param name="publication_handles">Output array of handles</param>
        /// <param name="max_handles">Size of array</param>
        /// <returns>Number of handles returned, or negative error code</returns>
        [DllImport(DLL_NAME)]
        public static extern int dds_get_matched_publications(
            int reader,
            [In, Out] long[] publication_handles,
            uint max_handles);

        /// <summary>
        /// Native struct for dds_get_matched_publication_data.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct DdsBuiltinTopicEndpoint
        {
            public DdsGuid Key;
            public DdsGuid ParticipantKey;
            public long ParticipantInstanceHandle;
            public IntPtr TopicName;
            public IntPtr TypeName;
            public IntPtr Qos;
        }

        /// <summary>
        /// Get detailed information about a matched publication.
        /// Caller must free returned pointer using dds_builtintopic_free_endpoint.
        /// </summary>
        /// <param name="reader">Reader entity</param>
        /// <param name="publication_handle">Handle of matched publication</param>
        /// <returns>Pointer to DdsBuiltinTopicEndpoint, or IntPtr.Zero on failure</returns>
        [DllImport(DLL_NAME)]
        public static extern IntPtr dds_get_matched_publication_data(
            int reader,
            long publication_handle); // Takes 2 args only!

        [DllImport(DLL_NAME)]
        public static extern void dds_builtintopic_free_endpoint(IntPtr endpoint);

        public const uint DDS_DATA_AVAILABLE_STATUS = (1u << 8);
        public const uint DDS_PUBLICATION_MATCHED_STATUS = (1u << 11);
        public const uint DDS_SUBSCRIPTION_MATCHED_STATUS = (1u << 12);

        // WaitSet & Conditions
        [DllImport(DLL_NAME)]
        public static extern DdsEntity dds_create_waitset(DdsEntity participant);

        [DllImport(DLL_NAME)]
        public static extern DdsEntity dds_create_readcondition(DdsEntity reader, uint mask);

        [DllImport(DLL_NAME)]
        public static extern DdsEntity dds_create_guardcondition(DdsEntity participant);

        // dds_set_guardcondition(entity, triggered=true) is the correct API in this build
        [DllImport(DLL_NAME)]
        public static extern int dds_set_guardcondition(DdsEntity guardcond, [MarshalAs(UnmanagedType.I1)] bool triggered);

        [DllImport(DLL_NAME)]
        public static extern int dds_waitset_attach(DdsEntity waitset, DdsEntity entity, IntPtr attach_arg);

        [DllImport(DLL_NAME)]
        public static extern int dds_waitset_detach(DdsEntity waitset, DdsEntity entity);

        [DllImport(DLL_NAME)]
        public static extern int dds_waitset_wait(
            DdsEntity waitset,
            [Out] IntPtr[] active_entities,
            UIntPtr n_entities,
            long reltimeout);

        public const long DDS_INFINITY = 0x7FFFFFFFFFFFFFFF;

        // Cleanup
        [DllImport(DLL_NAME)]
        public static extern int dds_delete(DdsEntity entity);
    }

    /// <summary>
    /// Represents a 16-byte DDS GUID (Globally Unique Identifier).
    /// Maps to native dds_guid_t (uint8_t v[16]).
    /// Used for O(1) participant/writer correlation.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    [Schema.DdsStruct]
    public partial struct DdsGuid : IEquatable<DdsGuid>
    {
        /// <summary>
        /// High 64 bits (first 8 bytes of v[16]).
        /// </summary>
        public long High;

        /// <summary>
        /// Low 64 bits (last 8 bytes of v[16]).
        /// </summary>
        public long Low;

        public readonly bool Equals(DdsGuid other)
        {
            return High == other.High && Low == other.Low;
        }

        // Helper to convert Native DdsGuid -> System.Guid
        public unsafe Guid ToManaged()
        {
            fixed (void* ptr = &this)
            {
                return new Guid(new ReadOnlySpan<byte>(ptr, 16));
            }
        }

        // Helper to write System.Guid -> Native DdsGuid
        public static DdsGuid FromManaged(System.Guid guid)
        {
            DdsGuid native = default;
            unsafe
            {
                guid.TryWriteBytes(new Span<byte>(&native, 16));
            }
            return native;
        }

        public static unsafe implicit operator System.Guid(DdsGuid ddsGuid)
        {
            return ddsGuid.ToManaged();
        }
        public override bool Equals(object? obj)
        {
            return obj is DdsGuid other && Equals(other);
        }

        public override readonly int GetHashCode()
        {
            return HashCode.Combine(High, Low);
        }

        public override readonly string ToString()
        {
            return $"{High:X16}{Low:X16}";
        }

        public static bool operator ==(DdsGuid left, DdsGuid right) => left.Equals(right);
        public static bool operator !=(DdsGuid left, DdsGuid right) => !left.Equals(right);
    }
}

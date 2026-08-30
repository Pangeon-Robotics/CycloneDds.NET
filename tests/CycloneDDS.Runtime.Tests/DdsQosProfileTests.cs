using System;
using System.Runtime.InteropServices;
using CycloneDDS.Runtime.Interop;
using CycloneDDS.Schema;
using Xunit;

namespace CycloneDDS.Runtime.Tests
{
    /// <summary>
    /// Reads policies back off a native <c>dds_qos_t</c>. Cyclone's <c>dds_qget_*</c> family
    /// returns false when the policy was never set, which is exactly the distinction
    /// <see cref="DdsQos"/> encodes as a null property, so these are the only calls that can
    /// tell "set to the default value" apart from "left to Cyclone".
    /// </summary>
    /// <remarks>
    /// Declared here rather than in <see cref="DdsApi"/> because nothing in the library needs
    /// to read a QoS back — only these tests do.
    /// </remarks>
    internal static class QosProbe
    {
        private const string DLL = DdsApi.DLL_NAME;

        // C returns _Bool (one byte), not a 4-byte Win32 BOOL, hence the explicit U1.
        [DllImport(DLL)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool dds_qget_reliability(nint qos, out int kind, out long maxBlockingTime);

        [DllImport(DLL)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool dds_qget_durability(nint qos, out int kind);

        [DllImport(DLL)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool dds_qget_history(nint qos, out int kind, out int depth);

        [DllImport(DLL)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool dds_qget_resource_limits(nint qos, out int maxSamples, out int maxInstances, out int maxSamplesPerInstance);

        [DllImport(DLL)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool dds_qget_deadline(nint qos, out long deadline);

        [DllImport(DLL)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool dds_qget_lifespan(nint qos, out long lifespan);

        [DllImport(DLL)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool dds_qget_liveliness(nint qos, out int kind, out long leaseDuration);
    }

    /// <summary>
    /// A flattened view of everything <see cref="DdsQos.CreateNative"/> wrote, so a test can
    /// assert on both the presence and the value of each policy.
    /// </summary>
    internal sealed class NativeQosView
    {
        public bool HasReliability;
        public int ReliabilityKind;
        public long MaxBlockingTime;

        public bool HasDurability;
        public int DurabilityKind;

        public bool HasHistory;
        public int HistoryKind;
        public int HistoryDepth;

        public bool HasResourceLimits;
        public int MaxSamples;
        public int MaxInstances;
        public int MaxSamplesPerInstance;

        public bool HasDeadline;
        public long Deadline;

        public bool HasLifespan;
        public long Lifespan;

        public bool HasLiveliness;
        public int LivelinessKind;
        public long LivelinessLease;

        /// <summary>Builds the profile's native QoS, reads every policy off it, then frees it.</summary>
        public static NativeQosView Of(DdsQos profile)
        {
            nint qos = profile.CreateNative();
            try
            {
                var view = new NativeQosView();
                view.HasReliability = QosProbe.dds_qget_reliability(qos, out view.ReliabilityKind, out view.MaxBlockingTime);
                view.HasDurability = QosProbe.dds_qget_durability(qos, out view.DurabilityKind);
                view.HasHistory = QosProbe.dds_qget_history(qos, out view.HistoryKind, out view.HistoryDepth);
                view.HasResourceLimits = QosProbe.dds_qget_resource_limits(qos, out view.MaxSamples, out view.MaxInstances, out view.MaxSamplesPerInstance);
                view.HasDeadline = QosProbe.dds_qget_deadline(qos, out view.Deadline);
                view.HasLifespan = QosProbe.dds_qget_lifespan(qos, out view.Lifespan);
                view.HasLiveliness = QosProbe.dds_qget_liveliness(qos, out view.LivelinessKind, out view.LivelinessLease);
                return view;
            }
            finally
            {
                DdsApi.dds_delete_qos(qos);
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // The DdsQos record itself: what each preset declares.
    // ─────────────────────────────────────────────────────────────────────────────

    public class DdsQosPresetTests
    {
        [Fact]
        public void SystemDefault_LeavesEveryPolicyUnset()
        {
            var qos = DdsQos.SystemDefault;

            Assert.Null(qos.Reliability);
            Assert.Null(qos.Durability);
            Assert.Null(qos.HistoryKind);
            Assert.Null(qos.HistoryDepth);
            Assert.Null(qos.Deadline);
            Assert.Null(qos.Lifespan);
            Assert.Null(qos.LivelinessLease);
            Assert.Equal(DdsLiveliness.Automatic, qos.Liveliness);
        }

        [Fact]
        public void Default_IsReliableVolatileKeepLastOne()
        {
            var qos = DdsQos.Default;

            Assert.Equal(DdsReliability.Reliable, qos.Reliability);
            Assert.Equal(DdsDurability.Volatile, qos.Durability);
            Assert.Equal(DdsHistoryKind.KeepLast, qos.HistoryKind);
            Assert.Equal(1, qos.HistoryDepth);
        }

        [Fact]
        public void Reliable_IsReliableKeepLastTen()
        {
            Assert.Equal(DdsReliability.Reliable, DdsQos.Reliable.Reliability);
            Assert.Equal(DdsHistoryKind.KeepLast, DdsQos.Reliable.HistoryKind);
            Assert.Equal(10, DdsQos.Reliable.HistoryDepth);
        }

        [Fact]
        public void BestEffort_IsBestEffortKeepLastOne()
        {
            Assert.Equal(DdsReliability.BestEffort, DdsQos.BestEffort.Reliability);
            Assert.Equal(DdsHistoryKind.KeepLast, DdsQos.BestEffort.HistoryKind);
            Assert.Equal(1, DdsQos.BestEffort.HistoryDepth);
        }

        [Fact]
        public void KeepAll_IsReliableKeepAll()
        {
            Assert.Equal(DdsReliability.Reliable, DdsQos.KeepAll.Reliability);
            Assert.Equal(DdsHistoryKind.KeepAll, DdsQos.KeepAll.HistoryKind);
        }

        [Fact]
        public void Latched_IsReliableTransientLocal()
        {
            Assert.Equal(DdsReliability.Reliable, DdsQos.Latched.Reliability);
            Assert.Equal(DdsDurability.TransientLocal, DdsQos.Latched.Durability);
            Assert.Equal(DdsHistoryKind.KeepLast, DdsQos.Latched.HistoryKind);
            Assert.Equal(1, DdsQos.Latched.HistoryDepth);
        }

        /// <summary>
        /// Guards the static-initialiser hazard the presets are deliberately written around:
        /// static fields run in declaration order, so a preset derived from a field declared
        /// later would silently collapse into an all-null (system default) profile. Every
        /// explicit preset must therefore still carry all four policies.
        /// </summary>
        [Fact]
        public void ExplicitPresets_AreNotSilentlyAllNull()
        {
            var presets = new (string Name, DdsQos Qos)[]
            {
                (nameof(DdsQos.Default), DdsQos.Default),
                (nameof(DdsQos.Reliable), DdsQos.Reliable),
                (nameof(DdsQos.BestEffort), DdsQos.BestEffort),
                (nameof(DdsQos.KeepAll), DdsQos.KeepAll),
                (nameof(DdsQos.Latched), DdsQos.Latched),
            };

            foreach (var (name, qos) in presets)
            {
                Assert.True(qos.Reliability.HasValue, $"{name}.Reliability collapsed to null");
                Assert.True(qos.Durability.HasValue, $"{name}.Durability collapsed to null");
                Assert.True(qos.HistoryKind.HasValue, $"{name}.HistoryKind collapsed to null");
                Assert.NotEqual(DdsQos.SystemDefault, qos);
            }
        }

        [Fact]
        public void Presets_AreDistinctFromOneAnother()
        {
            Assert.NotEqual(DdsQos.Default, DdsQos.Reliable);
            Assert.NotEqual(DdsQos.Default, DdsQos.BestEffort);
            Assert.NotEqual(DdsQos.Default, DdsQos.KeepAll);
            Assert.NotEqual(DdsQos.Default, DdsQos.Latched);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // `with` composition and record semantics.
    // ─────────────────────────────────────────────────────────────────────────────

    public class DdsQosCompositionTests
    {
        [Fact]
        public void With_OverridesOnlyTheNamedPolicy()
        {
            var qos = DdsQos.Default with { HistoryDepth = 42 };

            Assert.Equal(42, qos.HistoryDepth);
            Assert.Equal(DdsQos.Default.Reliability, qos.Reliability);
            Assert.Equal(DdsQos.Default.Durability, qos.Durability);
            Assert.Equal(DdsQos.Default.HistoryKind, qos.HistoryKind);
        }

        [Fact]
        public void With_DoesNotMutateThePreset()
        {
            _ = DdsQos.Default with { HistoryDepth = 99, Reliability = DdsReliability.BestEffort };

            Assert.Equal(1, DdsQos.Default.HistoryDepth);
            Assert.Equal(DdsReliability.Reliable, DdsQos.Default.Reliability);
        }

        /// <summary>
        /// The point of <see cref="DdsQos.SystemDefault"/> as a base: overriding one policy
        /// must not drag in explicit values for any of the others.
        /// </summary>
        [Fact]
        public void SystemDefaultWithDeadline_LeavesEveryOtherPolicyUnset()
        {
            var qos = DdsQos.SystemDefault with { Deadline = 0.5 };

            Assert.Equal(0.5, qos.Deadline);
            Assert.Null(qos.Reliability);
            Assert.Null(qos.Durability);
            Assert.Null(qos.HistoryKind);
            Assert.Null(qos.HistoryDepth);
            Assert.Null(qos.Lifespan);
        }

        [Fact]
        public void ValueEquality_SamePoliciesAreEqual()
        {
            var a = DdsQos.SystemDefault with { Reliability = DdsReliability.Reliable, HistoryDepth = 5 };
            var b = DdsQos.SystemDefault with { Reliability = DdsReliability.Reliable, HistoryDepth = 5 };

            Assert.Equal(a, b);
            Assert.Equal(a.GetHashCode(), b.GetHashCode());
            Assert.NotSame(a, b);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // CreateNative: only policies that are actually set may reach the native QoS.
    // ─────────────────────────────────────────────────────────────────────────────

    public class DdsQosNativeProjectionTests
    {
        [Fact]
        public void SystemDefault_WritesNoPolicyAtAll()
        {
            var view = NativeQosView.Of(DdsQos.SystemDefault);

            Assert.False(view.HasReliability, "reliability must be left to Cyclone");
            Assert.False(view.HasDurability, "durability must be left to Cyclone");
            Assert.False(view.HasHistory, "history must be left to Cyclone");
            Assert.False(view.HasResourceLimits);
            Assert.False(view.HasDeadline);
            Assert.False(view.HasLifespan);
            Assert.False(view.HasLiveliness);
        }

        [Fact]
        public void Default_WritesReliabilityDurabilityAndHistory()
        {
            var view = NativeQosView.Of(DdsQos.Default);

            Assert.True(view.HasReliability);
            Assert.Equal(DdsApi.DDS_RELIABILITY_RELIABLE, view.ReliabilityKind);

            Assert.True(view.HasDurability);
            Assert.Equal(DdsApi.DDS_DURABILITY_VOLATILE, view.DurabilityKind);

            Assert.True(view.HasHistory);
            Assert.Equal(DdsApi.DDS_HISTORY_KEEP_LAST, view.HistoryKind);
            Assert.Equal(1, view.HistoryDepth);

            // Nothing beyond the four core policies may be written.
            Assert.False(view.HasDeadline);
            Assert.False(view.HasLifespan);
            Assert.False(view.HasLiveliness);
        }

        [Fact]
        public void BestEffort_WritesBestEffortReliability()
        {
            var view = NativeQosView.Of(DdsQos.BestEffort);

            Assert.True(view.HasReliability);
            Assert.Equal(DdsApi.DDS_RELIABILITY_BEST_EFFORT, view.ReliabilityKind);
        }

        [Fact]
        public void Latched_WritesTransientLocalDurability()
        {
            var view = NativeQosView.Of(DdsQos.Latched);

            Assert.True(view.HasDurability);
            Assert.Equal(DdsApi.DDS_DURABILITY_TRANSIENT_LOCAL, view.DurabilityKind);
        }

        /// <summary>
        /// KeepAll is meaningless without matching resource limits: Cyclone's default
        /// max_samples_per_instance would otherwise still cap the history.
        /// </summary>
        [Fact]
        public void KeepAll_WritesUnlimitedDepthAndUnlimitedResourceLimits()
        {
            var view = NativeQosView.Of(DdsQos.KeepAll);

            Assert.True(view.HasHistory);
            Assert.Equal(DdsApi.DDS_HISTORY_KEEP_ALL, view.HistoryKind);
            Assert.Equal(-1, view.HistoryDepth); // DDS_LENGTH_UNLIMITED

            Assert.True(view.HasResourceLimits);
            Assert.Equal(-1, view.MaxSamples);
            Assert.Equal(-1, view.MaxInstances);
            Assert.Equal(-1, view.MaxSamplesPerInstance);
        }

        [Fact]
        public void HistoryDepthAlone_ImpliesKeepLast()
        {
            var view = NativeQosView.Of(DdsQos.SystemDefault with { HistoryDepth = 7 });

            Assert.True(view.HasHistory);
            Assert.Equal(DdsApi.DDS_HISTORY_KEEP_LAST, view.HistoryKind);
            Assert.Equal(7, view.HistoryDepth);

            // The depth must not have dragged any other policy along with it.
            Assert.False(view.HasReliability);
            Assert.False(view.HasDurability);
        }

        [Fact]
        public void HistoryKindAlone_DefaultsToDepthOne()
        {
            var view = NativeQosView.Of(DdsQos.SystemDefault with { HistoryKind = DdsHistoryKind.KeepLast });

            Assert.True(view.HasHistory);
            Assert.Equal(DdsApi.DDS_HISTORY_KEEP_LAST, view.HistoryKind);
            Assert.Equal(1, view.HistoryDepth);
        }

        [Fact]
        public void MaxBlockingSeconds_IsWrittenAlongsideReliability()
        {
            var view = NativeQosView.Of(DdsQos.Default with { MaxBlockingSeconds = 2.5 });

            Assert.True(view.HasReliability);
            Assert.Equal(2_500_000_000L, view.MaxBlockingTime);
        }

        /// <summary>
        /// MaxBlockingSeconds is not nullable, so it carries a value even in a profile that
        /// sets no reliability. It must not cause the reliability policy to be written.
        /// </summary>
        [Fact]
        public void MaxBlockingSeconds_AloneDoesNotWriteReliability()
        {
            var view = NativeQosView.Of(DdsQos.SystemDefault with { MaxBlockingSeconds = 2.5 });

            Assert.False(view.HasReliability);
        }

        [Fact]
        public void Deadline_IsWrittenOnlyWhenSet()
        {
            Assert.False(NativeQosView.Of(DdsQos.Default).HasDeadline);

            var view = NativeQosView.Of(DdsQos.Default with { Deadline = 0.25 });
            Assert.True(view.HasDeadline);
            Assert.Equal(250_000_000L, view.Deadline);
        }

        [Fact]
        public void Lifespan_IsWrittenOnlyWhenSet()
        {
            Assert.False(NativeQosView.Of(DdsQos.Default).HasLifespan);

            var view = NativeQosView.Of(DdsQos.Default with { Lifespan = 1.5 });
            Assert.True(view.HasLifespan);
            Assert.Equal(1_500_000_000L, view.Lifespan);
        }

        /// <summary>
        /// Automatic with no lease is already Cyclone's own default, so writing it would turn
        /// a "left alone" profile into an explicit one for no gain.
        /// </summary>
        [Fact]
        public void Liveliness_AutomaticWithNoLease_IsNotWritten()
        {
            var view = NativeQosView.Of(DdsQos.SystemDefault with { Liveliness = DdsLiveliness.Automatic });

            Assert.False(view.HasLiveliness);
        }

        [Fact]
        public void Liveliness_ManualByTopic_IsWrittenWithInfiniteLeaseByDefault()
        {
            var view = NativeQosView.Of(DdsQos.Default with { Liveliness = DdsLiveliness.ManualByTopic });

            Assert.True(view.HasLiveliness);
            Assert.Equal((int)DdsLiveliness.ManualByTopic, view.LivelinessKind);
            Assert.Equal(DdsApi.DDS_INFINITY, view.LivelinessLease);
        }

        [Fact]
        public void Liveliness_LeaseAlone_IsWrittenWithAutomaticKind()
        {
            var view = NativeQosView.Of(DdsQos.Default with { LivelinessLease = 0.75 });

            Assert.True(view.HasLiveliness);
            Assert.Equal((int)DdsLiveliness.Automatic, view.LivelinessKind);
            Assert.Equal(750_000_000L, view.LivelinessLease);
        }

        [Fact]
        public void Liveliness_KindAndLease_AreBothWritten()
        {
            var view = NativeQosView.Of(DdsQos.Default with
            {
                Liveliness = DdsLiveliness.ManualByTopic,
                LivelinessLease = 0.4
            });

            Assert.True(view.HasLiveliness);
            Assert.Equal((int)DdsLiveliness.ManualByTopic, view.LivelinessKind);
            Assert.Equal(400_000_000L, view.LivelinessLease);
        }

        [Fact]
        public void CreateNative_ReturnsADistinctHandleEachCall()
        {
            nint a = DdsQos.Default.CreateNative();
            nint b = DdsQos.Default.CreateNative();
            try
            {
                Assert.NotEqual(IntPtr.Zero, a);
                Assert.NotEqual(IntPtr.Zero, b);
                Assert.NotEqual(a, b);
            }
            finally
            {
                DdsApi.dds_delete_qos(a);
                DdsApi.dds_delete_qos(b);
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Seconds → dds_duration_t.
    // ─────────────────────────────────────────────────────────────────────────────

    public class DdsDurationConversionTests
    {
        [Theory]
        [InlineData(1.0, 1_000_000_000L)]
        [InlineData(0.25, 250_000_000L)]
        [InlineData(0.001, 1_000_000L)]
        [InlineData(60.0, 60_000_000_000L)]
        public void Duration_ConvertsSecondsToNanoseconds(double seconds, long expected)
        {
            Assert.Equal(expected, DdsApi.Duration(seconds));
        }

        [Fact]
        public void Duration_NullIsInfinity()
        {
            Assert.Equal(DdsApi.DDS_INFINITY, DdsApi.Duration(null));
        }

        [Fact]
        public void Duration_PositiveInfinityIsInfinity()
        {
            Assert.Equal(DdsApi.DDS_INFINITY, DdsApi.Duration(double.PositiveInfinity));
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-1.0)]
        public void Duration_NonPositiveIsZero(double seconds)
        {
            Assert.Equal(0L, DdsApi.Duration(seconds));
        }

        [Fact]
        public void Duration_OverlargeValueSaturatesAtInfinity()
        {
            Assert.Equal(DdsApi.DDS_INFINITY, DdsApi.Duration(1e12));
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // [DdsQos] attribute → DdsQos profile.
    // ─────────────────────────────────────────────────────────────────────────────

    public class DdsQosFromAttributeTests
    {
        /// <summary>
        /// The attribute's own defaults are Reliable/Volatile/KeepLast(1) and have been since
        /// before QoS profiles existed, so a bare <c>[DdsQos]</c> must still project to exactly
        /// <see cref="DdsQos.Default"/> — never to <see cref="DdsQos.SystemDefault"/>.
        /// </summary>
        [Fact]
        public void BareAttribute_ProjectsToTheExplicitDefaultProfile()
        {
            var qos = DdsQos.FromAttribute(new DdsQosAttribute());

            Assert.Equal(DdsReliability.Reliable, qos.Reliability);
            Assert.Equal(DdsDurability.Volatile, qos.Durability);
            Assert.Equal(DdsHistoryKind.KeepLast, qos.HistoryKind);
            Assert.Equal(1, qos.HistoryDepth);
            Assert.Equal(DdsQos.Default, qos);
        }

        [Fact]
        public void Attribute_MapsEveryPolicy()
        {
            var qos = DdsQos.FromAttribute(new DdsQosAttribute
            {
                Reliability = DdsReliability.BestEffort,
                MaxBlockingSeconds = 3.0,
                Durability = DdsDurability.TransientLocal,
                HistoryKind = DdsHistoryKind.KeepAll,
                HistoryDepth = 8,
                Liveliness = DdsLiveliness.ManualByTopic,
                Deadline = 0.5,
                LivelinessLease = 1.25,
                Lifespan = 2.0
            });

            Assert.Equal(DdsReliability.BestEffort, qos.Reliability);
            Assert.Equal(3.0, qos.MaxBlockingSeconds);
            Assert.Equal(DdsDurability.TransientLocal, qos.Durability);
            Assert.Equal(DdsHistoryKind.KeepAll, qos.HistoryKind);
            Assert.Equal(8, qos.HistoryDepth);
            Assert.Equal(DdsLiveliness.ManualByTopic, qos.Liveliness);
            Assert.Equal(0.5, qos.Deadline);
            Assert.Equal(1.25, qos.LivelinessLease);
            Assert.Equal(2.0, qos.Lifespan);
        }

        /// <summary>
        /// The attribute's duration policies are nullable and default to null, so a decorated
        /// type that says nothing about them must not acquire a deadline or a lifespan.
        /// </summary>
        [Fact]
        public void Attribute_UnsetDurationsStayNull()
        {
            var qos = DdsQos.FromAttribute(new DdsQosAttribute());

            Assert.Null(qos.Deadline);
            Assert.Null(qos.LivelinessLease);
            Assert.Null(qos.Lifespan);

            var view = NativeQosView.Of(qos);
            Assert.False(view.HasDeadline);
            Assert.False(view.HasLifespan);
            Assert.False(view.HasLiveliness);
        }
    }
}

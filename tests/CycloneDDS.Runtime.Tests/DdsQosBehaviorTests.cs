using System;
using System.Threading;
using CycloneDDS.Runtime.Interop;
using CycloneDDS.Schema;
using Xunit;

namespace CycloneDDS.Runtime.Tests
{
    /// <summary>
    /// Shared plumbing for the QoS behaviour tests: every class below gets its own domain so
    /// the timing-sensitive liveliness and deadline cases cannot be perturbed by traffic from
    /// the rest of the suite running in parallel.
    /// </summary>
    internal static class QosTestSupport
    {
        // dds_qos_policy_id_t ordinals, from dds_public_qosdefs.h. Reported by the
        // *_incompatible_qos statuses as LastPolicyId.
        public const uint DeadlinePolicyId = 4;
        public const uint LivelinessPolicyId = 8;
        public const uint ReliabilityPolicyId = 11;

        /// <summary>
        /// Polls <paramref name="condition"/> until it holds or <paramref name="timeout"/>
        /// elapses. Returns whether it held; callers assert on the result so a failure reports
        /// the condition rather than a bare timeout.
        /// </summary>
        public static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                {
                    return true;
                }

                Thread.Sleep(20);
            }

            return condition();
        }

        public static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(5);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Request/offer compatibility. A reader and writer only match when the writer's
    // offer satisfies the reader's request, which makes "did they match?" an
    // observable probe of the QoS each side actually ended up with.
    // ─────────────────────────────────────────────────────────────────────────────

    public class DdsQosMatchingTests : IDisposable
    {
        private readonly DdsParticipant _participant = new(61);

        public void Dispose() => _participant.Dispose();

        private static string Topic(string name) => $"QosMatch_{name}";

        /// <summary>
        /// Regression guard for the undecorated path. <see cref="TestMessage"/> carries no
        /// <c>[DdsQos]</c>, so a reader built without an explicit profile must fall through to
        /// <see cref="DdsQos.SystemDefault"/> and pick up Cyclone's reader default of
        /// BestEffort — which is what a bare <c>dds_create_qos()</c> produced before QoS
        /// profiles existed, and which a BestEffort writer therefore satisfies.
        /// </summary>
        [Fact]
        public void UndecoratedType_DefaultReader_MatchesBestEffortWriter()
        {
            string topic = Topic("SystemDefaultRdr");
            using var reader = new DdsReader<TestMessage>(_participant, topic);
            using var writer = new DdsWriter<TestMessage>(_participant, topic, DdsQos.BestEffort);

            Assert.True(
                QosTestSupport.WaitUntil(() => reader.CurrentStatus.CurrentCount > 0, QosTestSupport.MatchTimeout),
                "An undecorated reader must default to Cyclone's BestEffort and match a BestEffort writer");
            Assert.Equal(0u, reader.RequestedIncompatibleQosStatus.TotalCount);
        }

        [Fact]
        public void SystemDefaultProfile_IsEquivalentToPassingNoProfileAtAll()
        {
            string topic = Topic("SystemDefaultExplicit");
            using var reader = new DdsReader<TestMessage>(_participant, topic, DdsQos.SystemDefault);
            using var writer = new DdsWriter<TestMessage>(_participant, topic, DdsQos.BestEffort);

            Assert.True(
                QosTestSupport.WaitUntil(() => reader.CurrentStatus.CurrentCount > 0, QosTestSupport.MatchTimeout),
                "DdsQos.SystemDefault must leave the reader on Cyclone's BestEffort default");
        }

        /// <summary>
        /// The counterpart to the above: the explicit <see cref="DdsQos.Default"/> profile is
        /// genuinely reliable, so it must refuse a BestEffort writer. Without this the
        /// SystemDefault test above would also pass if Default had silently gone all-null.
        /// </summary>
        [Fact]
        public void DefaultProfileReader_DoesNotMatchBestEffortWriter()
        {
            string topic = Topic("ReliableVsBestEffort");
            using var reader = new DdsReader<TestMessage>(_participant, topic, DdsQos.Default);
            using var writer = new DdsWriter<TestMessage>(_participant, topic, DdsQos.BestEffort);

            Assert.True(
                QosTestSupport.WaitUntil(() => reader.RequestedIncompatibleQosStatus.TotalCount > 0, QosTestSupport.MatchTimeout),
                "A reliable reader must report REQUESTED_INCOMPATIBLE_QOS against a best-effort writer");
            Assert.Equal(0u, reader.CurrentStatus.CurrentCount);
        }

        [Fact]
        public void ReliabilityMismatch_IsReportedAsTheReliabilityPolicy()
        {
            string topic = Topic("ReliabilityPolicyId");
            using var reader = new DdsReader<TestMessage>(_participant, topic, DdsQos.Default);
            using var writer = new DdsWriter<TestMessage>(_participant, topic, DdsQos.BestEffort);

            Assert.True(
                QosTestSupport.WaitUntil(() => reader.RequestedIncompatibleQosStatus.TotalCount > 0, QosTestSupport.MatchTimeout));
            Assert.Equal(QosTestSupport.ReliabilityPolicyId, reader.RequestedIncompatibleQosStatus.LastPolicyId);
        }

        [Fact]
        public void ReliabilityMismatch_IsAlsoReportedOnTheWriterSide()
        {
            string topic = Topic("OfferedIncompatible");
            using var reader = new DdsReader<TestMessage>(_participant, topic, DdsQos.Default);
            using var writer = new DdsWriter<TestMessage>(_participant, topic, DdsQos.BestEffort);

            Assert.True(
                QosTestSupport.WaitUntil(() => writer.OfferedIncompatibleQosStatus.TotalCount > 0, QosTestSupport.MatchTimeout),
                "A best-effort writer must report OFFERED_INCOMPATIBLE_QOS against a reliable reader");
            Assert.Equal(QosTestSupport.ReliabilityPolicyId, writer.OfferedIncompatibleQosStatus.LastPolicyId);
        }

        /// <summary>
        /// DEADLINE is request/offer ordered the other way round from reliability: the reader
        /// asks for samples at least this often, so a writer that promises less frequently
        /// cannot satisfy it.
        /// </summary>
        [Fact]
        public void ReaderDeadlineShorterThanWriters_DoesNotMatch()
        {
            string topic = Topic("DeadlineMismatch");
            using var reader = new DdsReader<TestMessage>(_participant, topic, DdsQos.Default with { Deadline = 0.2 });
            using var writer = new DdsWriter<TestMessage>(_participant, topic, DdsQos.Default with { Deadline = 5.0 });

            Assert.True(
                QosTestSupport.WaitUntil(() => reader.RequestedIncompatibleQosStatus.TotalCount > 0, QosTestSupport.MatchTimeout),
                "A reader demanding a shorter deadline than the writer offers must not match");
            Assert.Equal(QosTestSupport.DeadlinePolicyId, reader.RequestedIncompatibleQosStatus.LastPolicyId);
            Assert.Equal(0u, reader.CurrentStatus.CurrentCount);
        }

        [Fact]
        public void ReaderDeadlineLongerThanWriters_Matches()
        {
            string topic = Topic("DeadlineCompatible");
            using var reader = new DdsReader<TestMessage>(_participant, topic, DdsQos.Default with { Deadline = 5.0 });
            using var writer = new DdsWriter<TestMessage>(_participant, topic, DdsQos.Default with { Deadline = 0.2 });

            Assert.True(
                QosTestSupport.WaitUntil(() => reader.CurrentStatus.CurrentCount > 0, QosTestSupport.MatchTimeout),
                "A writer promising a shorter deadline than the reader needs must match");
            Assert.Equal(0u, reader.RequestedIncompatibleQosStatus.TotalCount);
        }

        /// <summary>
        /// LIVELINESS kinds are ordered Automatic &lt; ManualByParticipant &lt; ManualByTopic,
        /// and the writer's kind must be at least the reader's.
        /// </summary>
        /// <remarks>
        /// Both sides carry an explicit lease so that LIVELINESS is genuinely written into both
        /// native QoS objects. Setting only <c>Liveliness = Automatic</c> on the writer would
        /// write nothing at all — Automatic with no lease is Cyclone's own default — and the
        /// writer would then inherit the policy from the topic; see
        /// <see cref="UnsetPolicies_AreInheritedFromWhicheverEndpointCreatedTheTopic"/>.
        /// </remarks>
        [Fact]
        public void ReaderDemandingManualByTopic_DoesNotMatchAutomaticWriter()
        {
            string topic = Topic("LivelinessKindMismatch");
            using var reader = new DdsReader<TestMessage>(_participant, topic,
                DdsQos.Default with { Liveliness = DdsLiveliness.ManualByTopic, LivelinessLease = 10.0 });
            using var writer = new DdsWriter<TestMessage>(_participant, topic,
                DdsQos.Default with { Liveliness = DdsLiveliness.Automatic, LivelinessLease = 10.0 });

            Assert.True(
                QosTestSupport.WaitUntil(() => reader.RequestedIncompatibleQosStatus.TotalCount > 0, QosTestSupport.MatchTimeout),
                "An Automatic writer cannot satisfy a reader that requires ManualByTopic");
            Assert.Equal(QosTestSupport.LivelinessPolicyId, reader.RequestedIncompatibleQosStatus.LastPolicyId);
        }

        /// <summary>
        /// A shorter lease is the stronger promise, so a writer that renews more often than the
        /// reader requires is compatible.
        /// </summary>
        [Fact]
        public void WriterWithShorterLivelinessLease_MatchesReaderWithLongerOne()
        {
            string topic = Topic("LeaseCompatible");
            using var reader = new DdsReader<TestMessage>(_participant, topic,
                DdsQos.Default with { LivelinessLease = 5.0 });
            using var writer = new DdsWriter<TestMessage>(_participant, topic,
                DdsQos.Default with { LivelinessLease = 1.0 });

            Assert.True(
                QosTestSupport.WaitUntil(() => reader.CurrentStatus.CurrentCount > 0, QosTestSupport.MatchTimeout));
            Assert.Equal(0u, reader.RequestedIncompatibleQosStatus.TotalCount);
        }

        [Fact]
        public void WriterWithLongerLivelinessLease_DoesNotMatchReaderWithShorterOne()
        {
            string topic = Topic("LeaseMismatch");
            using var reader = new DdsReader<TestMessage>(_participant, topic,
                DdsQos.Default with { LivelinessLease = 0.5 });
            using var writer = new DdsWriter<TestMessage>(_participant, topic,
                DdsQos.Default with { LivelinessLease = 10.0 });

            Assert.True(
                QosTestSupport.WaitUntil(() => reader.RequestedIncompatibleQosStatus.TotalCount > 0, QosTestSupport.MatchTimeout));
            Assert.Equal(QosTestSupport.LivelinessPolicyId, reader.RequestedIncompatibleQosStatus.LastPolicyId);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Topic QoS inheritance.
    //
    // DdsParticipant caches one native topic per topic *name* and creates it with the
    // QoS of whichever endpoint asked for it first. Cyclone then merges that topic QoS
    // into every later endpoint on the same name for any policy the endpoint itself
    // left unset (dds_reader.c / dds_writer.c: mergein_missing from tp->m_ktopic->qos,
    // which runs before the entity defaults). A null policy therefore does not always
    // mean "Cyclone's default" — it means "the topic's value, and Cyclone's default
    // only if the topic has none either".
    //
    // These characterise that behaviour rather than endorse it: it makes the effective
    // QoS depend on entity construction order. They are expected to fail once the TODO
    // on DdsParticipant.GetOrRegisterTopic is acted on and the cache distinguishes
    // topics by QoS as well as by name.
    // ─────────────────────────────────────────────────────────────────────────────

    public class DdsTopicQosInheritanceTests : IDisposable
    {
        private readonly DdsParticipant _participant = new(66);

        public void Dispose() => _participant.Dispose();

        /// <summary>
        /// Reader first: the reader creates the topic carrying ManualByTopic, so the writer —
        /// which sets no liveliness of its own — inherits ManualByTopic from it and the two
        /// match, despite the writer profile nominally asking for Automatic.
        /// </summary>
        [Fact]
        public void UnsetPolicies_AreInheritedFromWhicheverEndpointCreatedTheTopic()
        {
            const string topic = "QosTopicInherit_ReaderFirst";
            using var reader = new DdsReader<TestMessage>(_participant, topic,
                DdsQos.Default with { Liveliness = DdsLiveliness.ManualByTopic });
            using var writer = new DdsWriter<TestMessage>(_participant, topic,
                DdsQos.Default with { Liveliness = DdsLiveliness.Automatic });

            Assert.True(
                QosTestSupport.WaitUntil(() => reader.CurrentStatus.CurrentCount > 0, QosTestSupport.MatchTimeout),
                "The writer left LIVELINESS unset and inherits the reader-created topic's ManualByTopic");
            Assert.Equal(0u, reader.RequestedIncompatibleQosStatus.TotalCount);
        }

        /// <summary>
        /// The same two profiles in the opposite order do not match: now the topic carries no
        /// liveliness, so the writer falls through to Cyclone's Automatic default, which cannot
        /// satisfy the reader's ManualByTopic.
        /// </summary>
        [Fact]
        public void SameProfilesInTheOppositeOrder_DoNotMatch()
        {
            const string topic = "QosTopicInherit_WriterFirst";
            using var writer = new DdsWriter<TestMessage>(_participant, topic,
                DdsQos.Default with { Liveliness = DdsLiveliness.Automatic });
            using var reader = new DdsReader<TestMessage>(_participant, topic,
                DdsQos.Default with { Liveliness = DdsLiveliness.ManualByTopic });

            Assert.True(
                QosTestSupport.WaitUntil(() => reader.RequestedIncompatibleQosStatus.TotalCount > 0, QosTestSupport.MatchTimeout),
                "With no liveliness on the topic the writer is Automatic and cannot satisfy the reader");
            Assert.Equal(QosTestSupport.LivelinessPolicyId, reader.RequestedIncompatibleQosStatus.LastPolicyId);
            Assert.Equal(0u, reader.CurrentStatus.CurrentCount);
        }

        /// <summary>
        /// A policy the endpoint sets explicitly always wins over the topic's, whichever
        /// endpoint created the topic.
        /// </summary>
        [Fact]
        public void ExplicitlySetPolicies_AreNotOverriddenByTheTopic()
        {
            const string topic = "QosTopicInherit_Explicit";
            using var reader = new DdsReader<TestMessage>(_participant, topic, DdsQos.Default);
            using var writer = new DdsWriter<TestMessage>(_participant, topic, DdsQos.BestEffort);

            Assert.True(
                QosTestSupport.WaitUntil(() => reader.RequestedIncompatibleQosStatus.TotalCount > 0, QosTestSupport.MatchTimeout),
                "The writer set BestEffort explicitly and must not inherit the topic's Reliable");
            Assert.Equal(QosTestSupport.ReliabilityPolicyId, reader.RequestedIncompatibleQosStatus.LastPolicyId);
        }

        /// <summary>
        /// A distinct topic name gets a distinct native topic, so nothing leaks across names.
        /// </summary>
        [Fact]
        public void DifferentTopicNames_DoNotShareQos()
        {
            using var seeder = new DdsReader<TestMessage>(_participant, "QosTopicInherit_SeedA",
                DdsQos.Default with { Liveliness = DdsLiveliness.ManualByTopic });

            using var writer = new DdsWriter<TestMessage>(_participant, "QosTopicInherit_SeedB",
                DdsQos.Default with { Liveliness = DdsLiveliness.Automatic });
            using var reader = new DdsReader<TestMessage>(_participant, "QosTopicInherit_SeedB",
                DdsQos.Default with { Liveliness = DdsLiveliness.ManualByTopic });

            Assert.True(
                QosTestSupport.WaitUntil(() => reader.RequestedIncompatibleQosStatus.TotalCount > 0, QosTestSupport.MatchTimeout),
                "The ManualByTopic seeded on another topic name must not reach this one");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // DEADLINE
    // ─────────────────────────────────────────────────────────────────────────────

    public class DdsDeadlineTests : IDisposable
    {
        private readonly DdsParticipant _participant = new(62);

        public void Dispose() => _participant.Dispose();

        private static string Topic(string name) => $"QosDeadline_{name}";

        /// <summary>
        /// A writer that commits to a deadline and then goes quiet must accuse itself, once per
        /// missed period, for every instance it has ever written.
        /// </summary>
        [Fact]
        public void Writer_ReportsOfferedDeadlineMissed_WhenItStopsWriting()
        {
            string topic = Topic("OfferedMissed");
            using var reader = new DdsReader<TestMessage>(_participant, topic, DdsQos.Default with { Deadline = 5.0 });
            using var writer = new DdsWriter<TestMessage>(_participant, topic, DdsQos.Default with { Deadline = 0.2 });

            Assert.True(QosTestSupport.WaitUntil(() => writer.CurrentStatus.CurrentCount > 0, QosTestSupport.MatchTimeout));

            writer.Write(new TestMessage { Id = 1, Value = 1 });

            Assert.True(
                QosTestSupport.WaitUntil(() => writer.OfferedDeadlineMissedStatus.TotalCount > 0, TimeSpan.FromSeconds(5)),
                "A writer that stops writing must report OFFERED_DEADLINE_MISSED");
        }

        /// <summary>
        /// The reader side of the same event: it asked for samples at least this often and did
        /// not get them.
        /// </summary>
        [Fact]
        public void Reader_ReportsRequestedDeadlineMissed_WhenNoSampleArrives()
        {
            string topic = Topic("RequestedMissed");
            using var reader = new DdsReader<TestMessage>(_participant, topic, DdsQos.Default with { Deadline = 0.3 });
            using var writer = new DdsWriter<TestMessage>(_participant, topic, DdsQos.Default with { Deadline = 0.3 });

            Assert.True(QosTestSupport.WaitUntil(() => reader.CurrentStatus.CurrentCount > 0, QosTestSupport.MatchTimeout));

            writer.Write(new TestMessage { Id = 1, Value = 1 });

            Assert.True(
                QosTestSupport.WaitUntil(() => reader.RequestedDeadlineMissedStatus.TotalCount > 0, TimeSpan.FromSeconds(5)),
                "A reader starved past its deadline must report REQUESTED_DEADLINE_MISSED");
        }

        /// <summary>
        /// A profile that leaves DEADLINE null must not acquire one, so no amount of silence
        /// can raise the status.
        /// </summary>
        [Fact]
        public void NoDeadlineConfigured_NeverReportsDeadlineMissed()
        {
            string topic = Topic("NoDeadline");
            using var reader = new DdsReader<TestMessage>(_participant, topic, DdsQos.Default);
            using var writer = new DdsWriter<TestMessage>(_participant, topic, DdsQos.Default);

            Assert.True(QosTestSupport.WaitUntil(() => reader.CurrentStatus.CurrentCount > 0, QosTestSupport.MatchTimeout));

            writer.Write(new TestMessage { Id = 1, Value = 1 });
            Thread.Sleep(1000);

            Assert.Equal(0u, writer.OfferedDeadlineMissedStatus.TotalCount);
            Assert.Equal(0u, reader.RequestedDeadlineMissedStatus.TotalCount);
        }

        [Fact]
        public void DeadlineIsMet_WhileTheWriterKeepsWriting()
        {
            string topic = Topic("DeadlineMet");
            using var reader = new DdsReader<TestMessage>(_participant, topic, DdsQos.Default with { Deadline = 1.0 });
            using var writer = new DdsWriter<TestMessage>(_participant, topic, DdsQos.Default with { Deadline = 1.0 });

            Assert.True(QosTestSupport.WaitUntil(() => reader.CurrentStatus.CurrentCount > 0, QosTestSupport.MatchTimeout));

            for (int i = 0; i < 10; i++)
            {
                writer.Write(new TestMessage { Id = 1, Value = i });
                Thread.Sleep(100);
            }

            Assert.Equal(0u, writer.OfferedDeadlineMissedStatus.TotalCount);
            Assert.Equal(0u, reader.RequestedDeadlineMissedStatus.TotalCount);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // LIFESPAN
    // ─────────────────────────────────────────────────────────────────────────────

    public class DdsLifespanTests : IDisposable
    {
        private readonly DdsParticipant _participant = new(63);

        public void Dispose() => _participant.Dispose();

        private static string Topic(string name) => $"QosLifespan_{name}";

        private static int DrainCount(DdsReader<TestMessage> reader)
        {
            using var loan = reader.Take(16);
            return loan.Count;
        }

        [Fact]
        public void SampleOlderThanItsLifespan_IsNeverDelivered()
        {
            string topic = Topic("Expires");
            using var reader = new DdsReader<TestMessage>(_participant, topic, DdsQos.KeepAll);
            using var writer = new DdsWriter<TestMessage>(_participant, topic, DdsQos.KeepAll with { Lifespan = 0.3 });

            Assert.True(QosTestSupport.WaitUntil(() => writer.CurrentStatus.CurrentCount > 0, QosTestSupport.MatchTimeout));

            writer.Write(new TestMessage { Id = 1, Value = 1 });
            Thread.Sleep(1500);

            Assert.Equal(0, DrainCount(reader));
        }

        /// <summary>
        /// Control for the above: the same sequence with no lifespan must still deliver, so a
        /// zero count cannot be blamed on discovery or on the read itself.
        /// </summary>
        [Fact]
        public void SampleWithNoLifespan_SurvivesTheSameDelay()
        {
            string topic = Topic("NoLifespan");
            using var reader = new DdsReader<TestMessage>(_participant, topic, DdsQos.KeepAll);
            using var writer = new DdsWriter<TestMessage>(_participant, topic, DdsQos.KeepAll);

            Assert.True(QosTestSupport.WaitUntil(() => writer.CurrentStatus.CurrentCount > 0, QosTestSupport.MatchTimeout));

            writer.Write(new TestMessage { Id = 1, Value = 1 });
            Thread.Sleep(1500);

            Assert.Equal(1, DrainCount(reader));
        }

        [Fact]
        public void SampleReadWithinItsLifespan_IsDelivered()
        {
            string topic = Topic("WithinLifespan");
            using var reader = new DdsReader<TestMessage>(_participant, topic, DdsQos.KeepAll);
            using var writer = new DdsWriter<TestMessage>(_participant, topic, DdsQos.KeepAll with { Lifespan = 10.0 });

            Assert.True(QosTestSupport.WaitUntil(() => writer.CurrentStatus.CurrentCount > 0, QosTestSupport.MatchTimeout));

            writer.Write(new TestMessage { Id = 1, Value = 1 });

            Assert.True(QosTestSupport.WaitUntil(() => DrainCount(reader) > 0, TimeSpan.FromSeconds(5)));
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // LIVELINESS
    // ─────────────────────────────────────────────────────────────────────────────

    public class DdsLivelinessTests : IDisposable
    {
        private readonly DdsParticipant _participant = new(64);

        public void Dispose() => _participant.Dispose();

        private static string Topic(string name) => $"QosLiveliness_{name}";

        /// <summary>
        /// ManualByTopic counts only writes on the writer itself, so a writer that stops
        /// writing loses its lease and is declared not-alive to its readers.
        /// </summary>
        [Fact]
        public void ManualByTopicWriter_LosesLiveliness_WhenItStopsWriting()
        {
            string topic = Topic("Lost");
            var qos = DdsQos.Default with
            {
                Liveliness = DdsLiveliness.ManualByTopic,
                LivelinessLease = 0.3
            };

            using var reader = new DdsReader<TestMessage>(_participant, topic, qos);
            using var writer = new DdsWriter<TestMessage>(_participant, topic, qos);

            Assert.True(QosTestSupport.WaitUntil(() => writer.CurrentStatus.CurrentCount > 0, QosTestSupport.MatchTimeout));

            writer.Write(new TestMessage { Id = 1, Value = 1 });

            Assert.True(
                QosTestSupport.WaitUntil(() => reader.LivelinessChangedStatus.AliveCount == 1, TimeSpan.FromSeconds(5)),
                "The writer must be alive immediately after a write");

            // Stop writing and let the lease run out.
            Assert.True(
                QosTestSupport.WaitUntil(() => writer.LivelinessLostStatus.TotalCount > 0, TimeSpan.FromSeconds(5)),
                "A ManualByTopic writer that stops writing must report LIVELINESS_LOST");

            Assert.True(
                QosTestSupport.WaitUntil(() => reader.LivelinessChangedStatus.NotAliveCount == 1, TimeSpan.FromSeconds(5)),
                "The reader must see the writer as not-alive once its lease expires");
        }

        /// <summary>
        /// The same configuration, but the writer keeps writing: each write renews the lease,
        /// so liveliness is never lost.
        /// </summary>
        [Fact]
        public void ManualByTopicWriter_StaysAlive_WhileItKeepsWriting()
        {
            string topic = Topic("Renewed");
            var qos = DdsQos.Default with
            {
                Liveliness = DdsLiveliness.ManualByTopic,
                LivelinessLease = 0.5
            };

            using var reader = new DdsReader<TestMessage>(_participant, topic, qos);
            using var writer = new DdsWriter<TestMessage>(_participant, topic, qos);

            Assert.True(QosTestSupport.WaitUntil(() => writer.CurrentStatus.CurrentCount > 0, QosTestSupport.MatchTimeout));

            for (int i = 0; i < 12; i++)
            {
                writer.Write(new TestMessage { Id = 1, Value = i });
                Thread.Sleep(100);
            }

            Assert.Equal(0u, writer.LivelinessLostStatus.TotalCount);
            Assert.Equal(1u, reader.LivelinessChangedStatus.AliveCount);
        }

        /// <summary>
        /// Automatic is asserted by Cyclone for the lifetime of the process, so a lease expires
        /// only when the process dies — never merely because the writer is idle.
        /// </summary>
        [Fact]
        public void AutomaticWriter_StaysAlive_WithoutWritingAtAll()
        {
            string topic = Topic("Automatic");
            var qos = DdsQos.Default with
            {
                Liveliness = DdsLiveliness.Automatic,
                LivelinessLease = 0.3
            };

            using var reader = new DdsReader<TestMessage>(_participant, topic, qos);
            using var writer = new DdsWriter<TestMessage>(_participant, topic, qos);

            Assert.True(QosTestSupport.WaitUntil(() => reader.LivelinessChangedStatus.AliveCount == 1, QosTestSupport.MatchTimeout));

            Thread.Sleep(1500);

            Assert.Equal(0u, writer.LivelinessLostStatus.TotalCount);
            Assert.Equal(1u, reader.LivelinessChangedStatus.AliveCount);
            Assert.Equal(0u, reader.LivelinessChangedStatus.NotAliveCount);
        }

        // ── Manual assertion: renewing the lease without publishing ──────────────

        /// <summary>
        /// The point of <see cref="DdsWriter{T}.AssertLiveliness"/>: a ManualByTopic writer
        /// that is idle but healthy can renew its lease without publishing data it does not
        /// have. Contrast with
        /// <see cref="ManualByTopicWriter_LosesLiveliness_WhenItStopsWriting"/>, which is the
        /// same configuration and the same silence without the assertions.
        /// </summary>
        [Fact]
        public void ManualByTopicWriter_StaysAlive_WhenAssertedWithoutWriting()
        {
            string topic = Topic("Asserted");
            var qos = DdsQos.Default with
            {
                Liveliness = DdsLiveliness.ManualByTopic,
                LivelinessLease = 0.4
            };

            using var reader = new DdsReader<TestMessage>(_participant, topic, qos);
            using var writer = new DdsWriter<TestMessage>(_participant, topic, qos);

            Assert.True(QosTestSupport.WaitUntil(() => writer.CurrentStatus.CurrentCount > 0, QosTestSupport.MatchTimeout));

            // One write to bring the writer up to alive, then never write again.
            writer.Write(new TestMessage { Id = 1, Value = 1 });
            Assert.True(
                QosTestSupport.WaitUntil(() => reader.LivelinessChangedStatus.AliveCount == 1, TimeSpan.FromSeconds(5)),
                "the writer must be alive immediately after its single write");

            // Assert well inside the 400 ms lease, across several lease periods.
            for (int i = 0; i < 15; i++)
            {
                Assert.True(writer.AssertLiveliness(), "AssertLiveliness must succeed on a live writer");
                Thread.Sleep(100);
            }

            Assert.Equal(0u, writer.LivelinessLostStatus.TotalCount);

            var changed = reader.LivelinessChangedStatus;
            Assert.Equal(1u, changed.AliveCount);
            Assert.Equal(0u, changed.NotAliveCount);
        }

        /// <summary>
        /// Asserting liveliness says only "I am still here" — it must not put a sample on the
        /// wire.
        /// </summary>
        [Fact]
        public void AssertLiveliness_DeliversNoSample()
        {
            string topic = Topic("AssertNoData");
            var qos = DdsQos.KeepAll with
            {
                Liveliness = DdsLiveliness.ManualByTopic,
                LivelinessLease = 5.0
            };

            using var reader = new DdsReader<TestMessage>(_participant, topic, qos);
            using var writer = new DdsWriter<TestMessage>(_participant, topic, qos);

            Assert.True(QosTestSupport.WaitUntil(() => writer.CurrentStatus.CurrentCount > 0, QosTestSupport.MatchTimeout));

            for (int i = 0; i < 5; i++)
            {
                Assert.True(writer.AssertLiveliness());
                Thread.Sleep(50);
            }

            Thread.Sleep(300);

            using var loan = reader.Take(16);
            Assert.Equal(0, loan.Count);
        }

        /// <summary>
        /// Harmless on an Automatic writer: DDS defines the operation for every writer and
        /// simply has nothing to renew when Cyclone is already asserting on its behalf.
        /// </summary>
        [Fact]
        public void AssertLiveliness_OnAutomaticWriter_Succeeds()
        {
            string topic = Topic("AssertAutomatic");
            using var writer = new DdsWriter<TestMessage>(_participant, topic, DdsQos.Default);

            Assert.True(writer.AssertLiveliness());
        }

        /// <summary>
        /// Disposed writers throw, consistently with <c>Write</c> and the status properties, so
        /// that a dead entity is never confused with a native call that failed for a real
        /// reason.
        /// </summary>
        [Fact]
        public void AssertLiveliness_AfterDispose_Throws()
        {
            var writer = new DdsWriter<TestMessage>(_participant, Topic("AssertDisposed"), DdsQos.Default);
            writer.Dispose();

            Assert.Throws<ObjectDisposedException>(() => writer.AssertLiveliness());
        }

        /// <summary>
        /// With an infinite lease — the default — liveliness can never be lost, whatever the
        /// kind.
        /// </summary>
        [Fact]
        public void ManualByTopicWriter_WithInfiniteLease_NeverLosesLiveliness()
        {
            string topic = Topic("InfiniteLease");
            var qos = DdsQos.Default with { Liveliness = DdsLiveliness.ManualByTopic };

            using var reader = new DdsReader<TestMessage>(_participant, topic, qos);
            using var writer = new DdsWriter<TestMessage>(_participant, topic, qos);

            Assert.True(QosTestSupport.WaitUntil(() => writer.CurrentStatus.CurrentCount > 0, QosTestSupport.MatchTimeout));

            writer.Write(new TestMessage { Id = 1, Value = 1 });
            Thread.Sleep(1500);

            Assert.Equal(0u, writer.LivelinessLostStatus.TotalCount);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Statuses on a disposed entity.
    // ─────────────────────────────────────────────────────────────────────────────

    public class DdsQosStatusLifetimeTests
    {
        [Fact]
        public void ReaderStatuses_ThrowAfterDispose()
        {
            using var participant = new DdsParticipant(65);
            var reader = new DdsReader<TestMessage>(participant, "QosStatusLifetime_Reader");
            reader.Dispose();

            Assert.Throws<ObjectDisposedException>(() => reader.RequestedIncompatibleQosStatus);
            Assert.Throws<ObjectDisposedException>(() => reader.RequestedDeadlineMissedStatus);
            Assert.Throws<ObjectDisposedException>(() => reader.LivelinessChangedStatus);
        }

        [Fact]
        public void WriterStatuses_ThrowAfterDispose()
        {
            using var participant = new DdsParticipant(65);
            var writer = new DdsWriter<TestMessage>(participant, "QosStatusLifetime_Writer");
            writer.Dispose();

            Assert.Throws<ObjectDisposedException>(() => writer.OfferedIncompatibleQosStatus);
            Assert.Throws<ObjectDisposedException>(() => writer.OfferedDeadlineMissedStatus);
            Assert.Throws<ObjectDisposedException>(() => writer.LivelinessLostStatus);
        }

        [Fact]
        public void FreshEntities_ReportZeroedStatuses()
        {
            using var participant = new DdsParticipant(65);
            using var reader = new DdsReader<TestMessage>(participant, "QosStatusLifetime_Fresh");
            using var writer = new DdsWriter<TestMessage>(participant, "QosStatusLifetime_Fresh");

            Assert.Equal(0u, reader.RequestedIncompatibleQosStatus.TotalCount);
            Assert.Equal(0u, reader.RequestedDeadlineMissedStatus.TotalCount);
            Assert.Equal(0u, writer.OfferedIncompatibleQosStatus.TotalCount);
            Assert.Equal(0u, writer.OfferedDeadlineMissedStatus.TotalCount);
            Assert.Equal(0u, writer.LivelinessLostStatus.TotalCount);
        }
    }
}

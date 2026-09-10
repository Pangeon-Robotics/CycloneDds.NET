using System;
using System.Collections.Generic;
using CycloneDDS.Runtime.Hub;
using Xunit;

namespace CycloneDDS.Runtime.Tests
{
    // Deliberately not [DdsStruct]: no marshalling is generated, so endpoint creation fails.
    public struct NotGeneratedMessage
    {
        public int Value;
    }

    /// <summary>
    /// <see cref="DdsHub"/>: channel sharing, topic/type and QoS conflicts, delivery through
    /// <see cref="DdsHub.Pump"/>, and the publisher's throttle and heartbeat driven by an
    /// injected clock. Runs on its own domain so the rest of the suite cannot add traffic.
    /// </summary>
    public class DdsHubTests
    {
        private const uint Domain = 67;

        private readonly List<(DdsHubLogLevel Level, string Message)> _log = new();
        private double _now;

        private DdsHub CreateHub(bool manualClock = false) => new DdsHub(
            Domain,
            log: (level, message) => _log.Add((level, message)),
            clock: manualClock ? () => _now : null);

        private static bool PumpUntil(DdsHub hub, Func<bool> condition) =>
            QosTestSupport.WaitUntil(() =>
            {
                hub.Pump();
                return condition();
            }, QosTestSupport.MatchTimeout);

        private static bool WaitForReader<T>(DdsPublisher<T> publisher) =>
            publisher.WaitForReaderAsync(QosTestSupport.MatchTimeout).GetAwaiter().GetResult();

        [Fact]
        public void Subscribe_HandlerRunsFromPump()
        {
            using var hub = CreateHub();
            const string topic = "Hub_Deliver";
            var received = new List<TestMessage>();

            using var token = hub.Subscribe<TestMessage>(topic, received.Add, DdsQos.Reliable);
            var publisher = hub.Publisher<TestMessage>(topic, DdsQos.Reliable);
            Assert.NotNull(token);
            Assert.NotNull(publisher);
            Assert.True(WaitForReader(publisher!));

            publisher!.PublishNow(new TestMessage { Id = 1, Value = 42 });

            Assert.True(PumpUntil(hub, () => received.Count > 0), "The handler must run from Pump once the sample arrives");
            Assert.Equal(42, received[0].Value);
            Assert.Equal(1, publisher.WriteCount);
            Assert.Empty(_log);
        }

        [Fact]
        public void TryLatest_IsFalseUntilASampleArrives()
        {
            using var hub = CreateHub();
            const string topic = "Hub_Latest";

            Assert.False(hub.TryLatest<TestMessage>(topic, out _, DdsQos.Reliable));

            var publisher = hub.Publisher<TestMessage>(topic, DdsQos.Reliable)!;
            Assert.True(WaitForReader(publisher));
            publisher.PublishNow(new TestMessage { Id = 2, Value = 7 });

            TestMessage latest = default;
            Assert.True(PumpUntil(hub, () => hub.TryLatest<TestMessage>(topic, out latest, DdsQos.Reliable)));
            Assert.Equal(7, latest.Value);
            Assert.Equal(1, hub.Subscription<TestMessage>(topic)!.ReceiveCount);
        }

        [Fact]
        public void Channels_AreSharedPerTypeAndTopic()
        {
            using var hub = CreateHub();
            const string topic = "Hub_Shared";

            var subscription = hub.Subscription<TestMessage>(topic);
            var publisher = hub.Publisher<TestMessage>(topic);

            Assert.NotNull(subscription);
            Assert.NotNull(publisher);
            Assert.Same(subscription, hub.Subscription<TestMessage>(topic));
            Assert.Same(publisher, hub.Publisher<TestMessage>(topic));
            Assert.Empty(_log);
        }

        [Fact]
        public void DifferentQos_KeepsTheExistingChannelAndWarns()
        {
            using var hub = CreateHub();
            const string topic = "Hub_QosConflict";

            var first = hub.Publisher<TestMessage>(topic, DdsQos.Reliable);
            var second = hub.Publisher<TestMessage>(topic, DdsQos.BestEffort);

            Assert.Same(first, second);
            Assert.Equal(DdsQos.Reliable, second!.Qos);
            Assert.Contains(_log, e => e.Level == DdsHubLogLevel.Warning);
        }

        [Fact]
        public void TopicBoundToAnotherType_ReturnsNullAndLogsError()
        {
            using var hub = CreateHub();
            const string topic = "Hub_TypeClash";

            Assert.NotNull(hub.Publisher<TestMessage>(topic));
            Assert.Null(hub.Subscription<StringMessage>(topic));
            Assert.Contains(_log, e => e.Level == DdsHubLogLevel.Error);
        }

        [Fact]
        public void NullQos_OnLookup_ReturnsTheExistingChannelWithoutWarning()
        {
            using var hub = CreateHub();
            const string topic = "Hub_NullQosLookup";

            var publisher = hub.Publisher<TestMessage>(topic, DdsQos.Reliable);

            Assert.Same(publisher, hub.Publisher<TestMessage>(topic));
            Assert.Empty(_log);
        }

        [Fact]
        public void ChannelCreatedWithoutQos_IsSystemDefault_AndAcceptsItExplicitly()
        {
            using var hub = CreateHub();
            const string topic = "Hub_SystemDefaultLookup";

            var subscription = hub.Subscription<TestMessage>(topic);

            Assert.Equal(DdsQos.SystemDefault, subscription!.Qos);
            Assert.Same(subscription, hub.Subscription<TestMessage>(topic, DdsQos.SystemDefault));
            Assert.Empty(_log);
        }

        [Fact]
        public void ExplicitSystemDefault_OnAConfiguredChannel_Warns()
        {
            using var hub = CreateHub();
            const string topic = "Hub_SystemDefaultConflict";

            var publisher = hub.Publisher<TestMessage>(topic, DdsQos.Reliable);

            Assert.Same(publisher, hub.Publisher<TestMessage>(topic, DdsQos.SystemDefault));
            Assert.Contains(_log, e => e.Level == DdsHubLogLevel.Warning);
        }

        [Fact]
        public void Close_ReleasesTheTopicForAnotherType()
        {
            using var hub = CreateHub();
            const string topic = "Hub_CloseRebind";

            Assert.NotNull(hub.Publisher<TestMessage>(topic));
            Assert.NotNull(hub.Subscription<TestMessage>(topic));

            hub.Close<TestMessage>(topic);

            Assert.NotNull(hub.Subscription<StringMessage>(topic));
            Assert.Empty(_log);
        }

        [Fact]
        public void Close_WithAnotherType_KeepsTheBinding()
        {
            using var hub = CreateHub();
            const string topic = "Hub_CloseWrongType";

            Assert.NotNull(hub.Publisher<TestMessage>(topic));

            hub.Close<StringMessage>(topic);

            Assert.Null(hub.Subscription<StringMessage>(topic));
            Assert.Contains(_log, e => e.Level == DdsHubLogLevel.Error);
        }

        [Fact]
        public void FailedCreation_LeavesTheTopicUnbound()
        {
            using var hub = CreateHub();
            const string topic = "Hub_FailedCreation";

            // No generated marshalling, so DdsWriter's constructor throws.
            Assert.Null(hub.Publisher<NotGeneratedMessage>(topic));
            Assert.Contains(_log, e => e.Level == DdsHubLogLevel.Error);
            _log.Clear();

            Assert.NotNull(hub.Publisher<TestMessage>(topic));
            Assert.Empty(_log);
        }

        [Fact]
        public void Set_WithRateLimit_SendsOnlyTheLatestValue()
        {
            using var hub = CreateHub(manualClock: true);
            const string topic = "Hub_Throttle";
            var received = new List<int>();

            using var token = hub.Subscribe<TestMessage>(topic, m => received.Add(m.Value), DdsQos.Reliable);
            var publisher = hub.Publisher<TestMessage>(topic, DdsQos.Reliable, rateHz: 10)!;
            Assert.True(WaitForReader(publisher));

            publisher.Set(new TestMessage { Value = 1 });
            hub.Pump(); // nothing written yet, so the first pending value goes straight out
            Assert.Equal(1, publisher.WriteCount);

            _now = 0.01;
            publisher.Set(new TestMessage { Value = 2 });
            publisher.Set(new TestMessage { Value = 3 });
            hub.Pump();
            Assert.Equal(1, publisher.WriteCount);
            Assert.Equal(1, publisher.DroppedCount);

            _now = 0.11;
            hub.Pump();
            Assert.Equal(2, publisher.WriteCount);

            Assert.True(PumpUntil(hub, () => received.Count >= 2), "Both throttled writes must arrive");
            Assert.Equal(new[] { 1, 3 }, received);
        }

        [Fact]
        public void Heartbeat_ResendsTheLastValueWhenIdle()
        {
            using var hub = CreateHub(manualClock: true);
            var publisher = hub.Publisher<TestMessage>("Hub_Heartbeat", heartbeatSeconds: 0.25)!;

            publisher.PublishNow(new TestMessage { Value = 5 });
            Assert.Equal(1, publisher.WriteCount);

            _now = 0.2;
            hub.Pump();
            Assert.Equal(1, publisher.WriteCount);

            _now = 0.3;
            hub.Pump();
            Assert.Equal(2, publisher.WriteCount);
        }

        [Fact]
        public void ThrowingHandler_DoesNotStopTheOthers()
        {
            using var hub = CreateHub();
            const string topic = "Hub_ThrowingHandler";
            var received = 0;

            using var bad = hub.Subscribe<TestMessage>(topic, _ => throw new InvalidOperationException("handler boom"), DdsQos.Reliable);
            using var good = hub.Subscribe<TestMessage>(topic, _ => received++, DdsQos.Reliable);
            var publisher = hub.Publisher<TestMessage>(topic, DdsQos.Reliable)!;
            Assert.True(WaitForReader(publisher));

            publisher.PublishNow(new TestMessage { Id = 3 });

            Assert.True(PumpUntil(hub, () => received > 0), "A throwing handler must not stop the next one");
            Assert.Contains(_log, e => e.Level == DdsHubLogLevel.Error && e.Message.Contains("handler boom"));
        }

        [Fact]
        public void Close_DisposesTheChannelsAndAllowsRecreation()
        {
            using var hub = CreateHub();
            const string topic = "Hub_Close";

            var publisher = hub.Publisher<TestMessage>(topic)!;
            var subscription = hub.Subscription<TestMessage>(topic)!;

            hub.Close<TestMessage>(topic);

            publisher.PublishNow(new TestMessage());
            Assert.Equal(0, publisher.WriteCount);
            Assert.Equal(0, subscription.MatchedWriters);
            Assert.NotSame(publisher, hub.Publisher<TestMessage>(topic));
        }

        [Fact]
        public void Dispose_IsIdempotentAndRejectsNewChannels()
        {
            var hub = CreateHub();

            hub.Dispose();
            hub.Dispose();

            Assert.Null(hub.Participant);
            Assert.Null(hub.Publisher<TestMessage>("Hub_Disposed"));
            Assert.Contains(_log, e => e.Level == DdsHubLogLevel.Error);
        }
    }
}

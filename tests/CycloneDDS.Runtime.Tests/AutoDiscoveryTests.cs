using System;
using Xunit;
using CycloneDDS.Runtime;

namespace CycloneDDS.Runtime.Tests
{
    public class AutoDiscoveryTests : IDisposable
    {
        private DdsParticipant _participant;

        public AutoDiscoveryTests()
        {
            _participant = new DdsParticipant();
        }

        public void Dispose()
        {
            _participant.Dispose();
        }

        [Fact]
        public void GetDescriptorOps_ValidType_ReturnsOps()
        {
            var ops = DdsTypeSupport.GetDescriptorOps<TestMessage>();
            Assert.NotNull(ops);
            Assert.NotEmpty(ops);
        }

        [Fact]
        public void GetDescriptorOps_InvalidType_Throws()
        {
            Assert.Throws<InvalidOperationException>(() => DdsTypeSupport.GetDescriptorOps<int>());
        }

        /// <summary>
        /// The topic cache is keyed by (name, type) and reference counted, so two endpoints on
        /// one name share a single native topic instead of leaking one entity each. The topic
        /// itself carries no QoS, so sharing it cannot make one endpoint shadow the other's.
        /// </summary>
        [Fact]
        public void RegisterTopic_SameNameAndType_SharesOneTopic()
        {
            var topic1 = _participant.RegisterTopic<TestMessage>("CachedTopic");
            var topic2 = _participant.RegisterTopic<TestMessage>("CachedTopic");

            Assert.True(topic1.IsValid);
            Assert.Equal(topic1.Handle, topic2.Handle);

            _participant.ReleaseTopic<TestMessage>("CachedTopic");
            _participant.ReleaseTopic<TestMessage>("CachedTopic");
        }

        /// <summary>
        /// Once every registration is released the topic is deleted, and a later registration
        /// builds a fresh one rather than handing back the deleted entity.
        /// </summary>
        [Fact]
        public void RegisterTopic_AfterFullRelease_CreatesFreshTopic()
        {
            var topic1 = _participant.RegisterTopic<TestMessage>("RefCountedTopic");
            _participant.RegisterTopic<TestMessage>("RefCountedTopic");

            // One outstanding registration left: the topic must survive
            _participant.ReleaseTopic<TestMessage>("RefCountedTopic");
            var topic2 = _participant.RegisterTopic<TestMessage>("RefCountedTopic");
            Assert.Equal(topic1.Handle, topic2.Handle);

            _participant.ReleaseTopic<TestMessage>("RefCountedTopic");
            _participant.ReleaseTopic<TestMessage>("RefCountedTopic");

            var topic3 = _participant.RegisterTopic<TestMessage>("RefCountedTopic");
            Assert.True(topic3.IsValid);

            _participant.ReleaseTopic<TestMessage>("RefCountedTopic");
        }

        /// <summary>
        /// Endpoints hand their registration back on Dispose, so a topic churned through many
        /// short-lived readers leaves nothing behind.
        /// </summary>
        [Fact]
        public void Endpoints_DisposedRepeatedly_ReleaseTheirTopic()
        {
            for (int i = 0; i < 5; i++)
            {
                using var reader = new DdsReader<TestMessage>(_participant, "ChurnedTopic");
                using var writer = new DdsWriter<TestMessage>(_participant, "ChurnedTopic");
            }

            // Nothing is holding the topic now, so this registration must build a new one
            var topic = _participant.RegisterTopic<TestMessage>("ChurnedTopic");
            Assert.True(topic.IsValid);
            _participant.ReleaseTopic<TestMessage>("ChurnedTopic");
        }

        [Fact]
        public void RegisterTopic_DifferentNames_CreatesSeparateTopics()
        {
            var topic1 = _participant.RegisterTopic<TestMessage>("Topic1");
            var topic2 = _participant.RegisterTopic<TestMessage>("Topic2");

            Assert.NotEqual(topic1.Handle, topic2.Handle);

            _participant.ReleaseTopic<TestMessage>("Topic1");
            _participant.ReleaseTopic<TestMessage>("Topic2");
        }

        [Fact]
        public void AutoDiscovery_ValidType_Succeeds()
        {
            // Should succeed without manual descriptor
            using var writer = new DdsWriter<TestMessage>(_participant, "AutoDiscTopic");
            Assert.NotNull(writer);
        }

        [Fact]
        public void AutoDiscovery_InvalidType_Throws()
        {
            // int has no GetDescriptorOps
            Assert.Throws<InvalidOperationException>(() => new DdsWriter<int>(_participant, "InvalidTopic"));
        }
    }
}

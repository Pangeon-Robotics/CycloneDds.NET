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
        /// There is no topic cache any more: each call creates its own native topic, so two
        /// endpoints on one name get two handles onto the same underlying ktopic rather than
        /// sharing one entity — and neither can shadow the other's QoS.
        /// </summary>
        [Fact]
        public void RegisterTopic_SameName_CreatesSeparateHandles()
        {
            var topic1 = _participant.RegisterTopic<TestMessage>("CachedTopic");
            var topic2 = _participant.RegisterTopic<TestMessage>("CachedTopic");

            Assert.True(topic1.IsValid);
            Assert.True(topic2.IsValid);
            Assert.NotEqual(topic1.Handle, topic2.Handle);
        }

        [Fact]
        public void RegisterTopic_DifferentNames_CreatesSeparateTopics()
        {
            var topic1 = _participant.RegisterTopic<TestMessage>("Topic1");
            var topic2 = _participant.RegisterTopic<TestMessage>("Topic2");

            Assert.NotEqual(topic1.Handle, topic2.Handle);
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

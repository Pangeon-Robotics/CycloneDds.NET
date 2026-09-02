using System;
using Xunit;
using CycloneDDS.Runtime;

namespace CycloneDDS.Runtime.Tests
{
    public class DdsReaderTests
    {
        [Fact]
        public void CreateReader_Success()
        {
            using var participant = new DdsParticipant(0);
            using var reader = new DdsReader<TestMessage>(participant, "TestTopic_Unique1");
            
            Assert.NotNull(reader);
        }

        [Fact]
        public void Take_NoData_ReturnsEmptyScope()
        {
            // Topic name must not be shared with any writer in the suite: xUnit runs classes as
            // parallel collections on the same domain, so a sample written elsewhere on this
            // topic would land here and break the empty-scope assertion.
            using var participant = new DdsParticipant(0);
            using var reader = new DdsReader<TestMessage>(participant, "TestTopic_ReaderOnly_NoData");
            
            using var scope = reader.Take();
            
            Assert.Equal(0, scope.Count);
        }

        [Fact]
        public void Dispose_Idempotent()
        {
            using var participant = new DdsParticipant(0);
            var reader = new DdsReader<TestMessage>(participant, "TestTopic_Unique3");
            
            reader.Dispose();
            reader.Dispose();
        }

        [Fact]
        public void Take_AfterDispose_Throws()
        {
            using var participant = new DdsParticipant(0);
            var reader = new DdsReader<TestMessage>(participant, "TestTopic_Unique4");
            
            reader.Dispose();
            
            Assert.Throws<ObjectDisposedException>(() => reader.Take());
        }
    }
}

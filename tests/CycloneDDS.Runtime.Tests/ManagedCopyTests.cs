using System;
using System.Collections.Generic;
using Xunit;
using CycloneDDS.Core;
using CycloneDDS.Runtime;
using CycloneDDS.Runtime.Interop;

namespace CycloneDDS.Runtime.Tests
{
    public class ManagedCopyTests : IDisposable
    {
        private readonly DdsParticipant _participant;
        private readonly DdsWriter<TestMessage> _writer;
        private readonly DdsReader<TestMessage> _reader;

        public ManagedCopyTests()
        {
            try 
            {
                _participant = new DdsParticipant();
                var topicName = "ManagedCopyTests_Topic_" + Guid.NewGuid();
                
                _writer = new DdsWriter<TestMessage>(_participant, topicName, DdsQos.KeepAll);
                _reader = new DdsReader<TestMessage>(_participant, topicName, DdsQos.KeepAll);
            }
            catch(Exception)
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            _reader?.Dispose();
            _writer?.Dispose();
            _participant?.Dispose();
        }

        [Fact]
        public void ReadCopied_ReturnsManagedList()
        {
            var msg1 = new TestMessage { Id = 1, Value = 100 };
            var msg2 = new TestMessage { Id = 2, Value = 200 };
            _writer.Write(msg1);
            _writer.Write(msg2);

            // Wait for delivery
            System.Threading.Thread.Sleep(2000);

            // ReadCopied verification
            // With KeepAll, we should get both samples
            List<TestMessage> list = _reader.ReadCopied(10);
            
            Assert.Equal(2, list.Count);
            
            Assert.Contains(list, x => x.Id == 1 && x.Value == 100);
            Assert.Contains(list, x => x.Id == 2 && x.Value == 200);
        }
    }
}

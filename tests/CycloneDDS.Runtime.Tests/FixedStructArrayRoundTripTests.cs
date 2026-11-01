using System;
using System.Threading;
using Xunit;
using CycloneDDS.Runtime;
using FixedStructArrays;

namespace CycloneDDS.Runtime.Tests
{
    /// <summary>
    /// End-to-end round trip for fixed-size arrays of structs, which are backed by a generated
    /// [InlineArray] buffer in the native struct. Verifies the C# layout matches the idlc layout
    /// and that elements (including dynamic string payloads) survive the wire.
    /// </summary>
    public class FixedStructArrayRoundTripTests : IDisposable
    {
        private const string TopicName = "FixedStructArrayTopic";

        private readonly DdsParticipant _participant;
        private readonly DdsWriter<FixedArrayTopic> _writer;
        private readonly DdsReader<FixedArrayTopic> _reader;

        public FixedStructArrayRoundTripTests()
        {
            _participant = new DdsParticipant();
            _writer = new DdsWriter<FixedArrayTopic>(_participant, TopicName);
            _reader = new DdsReader<FixedArrayTopic>(_participant, TopicName);
        }

        public void Dispose()
        {
            _reader?.Dispose();
            _writer?.Dispose();
            _participant?.Dispose();
        }

        private void WaitForData(int timeoutMs = 1000)
        {
            for (int i = 0; i < timeoutMs / 50; i++)
            {
                using var view = _reader.Read();
                if (view.Count > 0) return;
                Thread.Sleep(50);
            }
        }

        [Fact]
        public void RoundTrip_FixedArraysOfStructs_PreservesAllElements()
        {
            var data = new FixedArrayTopic { Id = 42, Trailer = 7 };
            data.Corners[0] = new Vec2 { X = 1.5, Y = 2.5 };
            data.Corners[1] = new Vec2 { X = 3.5, Y = 4.5 };
            data.Corners[2] = new Vec2 { X = 5.5, Y = 6.5 };
            data.Markers[0] = new Marker { Id = 1, Label = "alpha", Active = true };
            data.Markers[1] = new Marker { Id = 2, Label = "beta", Active = false };

            _writer.Write(data);
            WaitForData();

            using var samples = _reader.Take();
            Assert.Equal(1, samples.Count);
            var result = samples[0];

            Assert.Equal(42, result.Id);
            Assert.Equal(7, result.Trailer);

            Assert.Equal(1.5, result.Corners[0].X);
            Assert.Equal(2.5, result.Corners[0].Y);
            Assert.Equal(3.5, result.Corners[1].X);
            Assert.Equal(5.5, result.Corners[2].X);
            Assert.Equal(6.5, result.Corners[2].Y);

            Assert.Equal(1, result.Markers[0].Id);
            Assert.Equal("alpha", result.Markers[0].Label);
            Assert.True(result.Markers[0].Active);
            Assert.Equal(2, result.Markers[1].Id);
            Assert.Equal("beta", result.Markers[1].Label);
            Assert.False(result.Markers[1].Active);
        }

        [Fact]
        public void View_FixedArrayOfStructs_ExposesCountAndElementViews()
        {
            var viewType = typeof(FixedArrayTopicView);

            Assert.NotNull(viewType.GetProperty("CornersCount"));
            Assert.NotNull(viewType.GetMethod("GetCorners"));
            Assert.NotNull(viewType.GetProperty("MarkersCount"));
            Assert.NotNull(viewType.GetMethod("GetMarkers"));
            Assert.Equal(typeof(Vec2View), viewType.GetMethod("GetCorners")!.ReturnType);
        }
    }
}

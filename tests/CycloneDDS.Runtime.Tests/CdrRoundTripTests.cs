using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using CycloneDDS.Core;
using CycloneDDS.Runtime.Interop;
using CycloneDDS.Runtime.Tests.Shapes;
using Xunit;

namespace CycloneDDS.Runtime.Tests
{
    /// <summary>
    /// Writes generated types through Cyclone and reads them back. Every write passes Cyclone's
    /// validation against the idlc ops, so these also check the serializer agrees with the IDL.
    /// </summary>
    public class CdrRoundTripTests : IDisposable
    {
        private readonly DdsParticipant _participant = new(0);

        public void Dispose() => _participant.Dispose();

        private T RoundTrip<T>(T sample, string topic) where T : IDdsType<T>
        {
            using var reader = new DdsReader<T>(_participant, topic, DdsQos.Reliable);
            using var writer = new DdsWriter<T>(_participant, topic, DdsQos.Reliable);
            Assert.True(writer.WaitForReaderAsync(TimeSpan.FromSeconds(5)).Result, "reader never matched");

            writer.Write(sample);

            for (int attempt = 0; attempt < 50; attempt++)
            {
                using var loan = reader.Take();
                if (loan.Count > 0) return loan[0];
                Thread.Sleep(50);
            }
            throw new TimeoutException("sample never arrived");
        }

        private static AllShapes Populated()
        {
            var sample = new AllShapes
            {
                Flag = true, U8 = 250, I8 = -5, I16 = -300, U16 = 60000, I32 = int.MinValue, U32 = uint.MaxValue,
                I64 = long.MinValue, U64 = ulong.MaxValue, F32 = 1.5f, F64 = -2.25,
                Color = Color.Blue, WideEnum = Wide.Large,
                Text = "héllo", BoundedText = "12345678", WideText = "wide \U0001F600",
                Nested = new Inner { Id = 42, Label = "inner" },
                IntList = [1, 2, 3], DoubleSequence = [0.5], BoundedShorts = [-1, 2, 3], Bools = [true, false, true],
                Strings = ["a", "", "c"], BoundedStrings = ["abcd", "x"], WideStrings = ["ü", "\U0001F600"],
                Structs = [new Inner { Id = 1, Label = "one" }, new Inner { Id = 2, Label = "" }], Colors = [Color.Green, Color.Red],
                FixedFloats = [1f, 2f, 3f], FixedStructs = [new Inner { Id = 7, Label = "seven" }, new Inner { Id = 8, Label = "eight" }],
                Property = 99, ListProperty = [long.MaxValue],
            };
            for (int i = 0; i < 4; i++)
            {
                sample.InlineDoubles[i] = i * 1.25;
                sample.InlineColors[i] = (Color)(i % 2);
            }
            return sample;
        }

        [Fact]
        public void AllShapes_RoundTripExactly()
        {
            var sent = Populated();
            var got = RoundTrip(sent, "CdrAllShapes");

            Assert.Equal((sent.Flag, sent.U8, sent.I8, sent.I16, sent.U16, sent.I32, sent.U32, sent.I64, sent.U64, sent.F32, sent.F64),
                         (got.Flag, got.U8, got.I8, got.I16, got.U16, got.I32, got.U32, got.I64, got.U64, got.F32, got.F64));
            Assert.Equal((sent.Color, sent.WideEnum), (got.Color, got.WideEnum));
            Assert.Equal((sent.Text, sent.BoundedText, sent.WideText), (got.Text, got.BoundedText, got.WideText));
            Assert.Equal(sent.Nested, got.Nested);

            Assert.Equal(sent.IntList, got.IntList);
            Assert.Equal(sent.DoubleSequence, got.DoubleSequence);
            Assert.Equal(sent.BoundedShorts, got.BoundedShorts);
            Assert.Equal(sent.Bools, got.Bools);
            Assert.Equal(sent.Strings, got.Strings);
            Assert.Equal(sent.BoundedStrings, got.BoundedStrings);
            Assert.Equal(sent.WideStrings, got.WideStrings);
            Assert.Equal(sent.Structs, got.Structs);
            Assert.Equal(sent.Colors, got.Colors);

            Assert.Equal(sent.FixedFloats, got.FixedFloats);
            Assert.Equal(sent.FixedStructs, got.FixedStructs);
            for (int i = 0; i < 4; i++)
            {
                Assert.Equal(sent.InlineDoubles[i], got.InlineDoubles[i]);
                Assert.Equal(sent.InlineColors[i], got.InlineColors[i]);
            }

            Assert.Equal(sent.Property, got.Property);
            Assert.Equal(sent.ListProperty, got.ListProperty);
        }

        /// <summary>Null strings and collections are written as empty, and null fixed arrays as defaults.</summary>
        [Fact]
        public void NullReferences_RoundTripAsEmptyOrDefault()
        {
            var got = RoundTrip(new AllShapes(), "CdrAllNulls");

            Assert.Equal("", got.Text);
            Assert.Equal("", got.WideText);
            Assert.Equal("", got.Nested.Label);
            Assert.Empty(got.IntList);
            Assert.Empty(got.DoubleSequence);
            Assert.Empty(got.Strings);
            Assert.Equal(new float[3], got.FixedFloats);
            Assert.Equal(2, got.FixedStructs.Length);
            Assert.Empty(got.ListProperty);
        }

        // Cyclone 0.11's normalize_wstring tests the parity of the whole payload instead of the
        // string's byte count, so it rejects these samples
        [Fact(Skip = "Cyclone bug: wstring validation rejects payloads of odd length")]
        public void WideString_InOddLengthPayload_RoundTrips()
        {
            var sent = new WideOdd { W = "ab", B = 7 };
            Assert.Equal(sent, RoundTrip(sent, "CdrWideOdd"));
        }

        [Fact]
        public void RecordStruct_RoundTrips()
        {
            var sent = new RecordMessage { A = 3, B = "record" };
            Assert.Equal(sent, RoundTrip(sent, "CdrRecord"));
        }

        [Fact]
        public void BoundViolations_ThrowBeforeReachingCyclone()
        {
            using var writer = new DdsWriter<AllShapes>(_participant, "CdrBounds");

            var tooLongString = Populated();
            tooLongString.BoundedText = "123456789";
            Assert.Throws<ArgumentException>(() => writer.Write(tooLongString));

            var tooManyElements = Populated();
            tooManyElements.BoundedShorts = [1, 2, 3, 4];
            Assert.Throws<ArgumentException>(() => writer.Write(tooManyElements));

            var tooLongElement = Populated();
            tooLongElement.BoundedStrings = ["abcde"];
            Assert.Throws<ArgumentException>(() => writer.Write(tooLongElement));

            var wrongFixedLength = Populated();
            wrongFixedLength.FixedFloats = [1f, 2f];
            Assert.Throws<ArgumentException>(() => writer.Write(wrongFixedLength));
        }

        [Fact]
        public void NullNestedClass_Throws()
        {
            using var writer = new DdsWriter<Holder>(_participant, "CdrNullNested");
            Assert.Throws<ArgumentNullException>(() => writer.Write(new Holder()));
        }

        [Fact]
        public void LargeSample_GrowsBufferAndRoundTrips()
        {
            var sent = Populated();
            sent.DoubleSequence = Enumerable.Range(0, 100_000).Select(i => (double)i).ToArray();

            var got = RoundTrip(sent, "CdrLarge");
            Assert.Equal(sent.DoubleSequence, got.DoubleSequence);
        }

        /// <summary>
        /// The safety net behind every write: Cyclone refuses to build a serdata from bytes that
        /// do not match the topic's ops.
        /// </summary>
        [Fact]
        public void Cyclone_RejectsPayloadNotMatchingTheType()
        {
            var topic = _participant.RegisterTopic<Inner>("CdrValidation");
            try
            {
                Assert.Equal(0, DdsApi.dds_get_entity_sertype(topic, out IntPtr sertype));

                // Inner is { int32 Id; string Label; }: a valid payload, then the same truncated
                byte[] valid = [0x00, 0x01, 0x00, 0x00, 0x2A, 0, 0, 0, 0x02, 0, 0, 0, (byte)'x', 0x00];
                byte[] truncated = valid[..^2];
                byte[] badLength = [.. valid[..8], 0x10, 0, 0, 0, (byte)'x', 0x00];

                IntPtr serdata = DdsApi.SerdataFromCdr(sertype, DdsApi.SerdataKind.Data, valid);
                Assert.NotEqual(IntPtr.Zero, serdata);
                Assert.Equal(new Inner { Id = 42, Label = "x" }, DdsApi.DeserializeSerdata<Inner>(serdata));
                DdsApi.ddsi_serdata_unref(serdata);

                Assert.Equal(IntPtr.Zero, DdsApi.SerdataFromCdr(sertype, DdsApi.SerdataKind.Data, truncated));
                Assert.Equal(IntPtr.Zero, DdsApi.SerdataFromCdr(sertype, DdsApi.SerdataKind.Data, badLength));
            }
            finally
            {
                _participant.ReleaseTopic<Inner>("CdrValidation");
            }
        }
    }
}

using System.IO;
using CycloneDDS.Core;
using Xunit;

namespace CycloneDDS.Core.Tests
{
    /// <summary>
    /// Byte-exact checks of the XCDR1 encoding. Expected payloads are written out by hand from
    /// the encoding rules, independent of the implementation.
    /// </summary>
    public class CdrWriterTests
    {
        private static readonly byte[] Header = [0x00, 0x01, 0x00, 0x00];

        private delegate void WriteAction(ref CdrWriter writer);

        private static byte[] Write(WriteAction write, int capacityHint = 64)
        {
            var writer = new CdrWriter(capacityHint);
            try
            {
                write(ref writer);
                return writer.WrittenSpan.ToArray();
            }
            finally
            {
                writer.Dispose();
            }
        }

        private static void AssertPayload(byte[] expectedPayload, byte[] actual) =>
            Assert.Equal(Header.Concat(expectedPayload).ToArray(), actual);

        [Fact]
        public void NewWriter_ContainsOnlyXcdr1LittleEndianHeader()
        {
            AssertPayload([], Write((ref CdrWriter w) => { }));
        }

        [Fact]
        public void Primitives_AlignToTheirSize_RelativeToPayloadStart()
        {
            var bytes = Write((ref CdrWriter w) =>
            {
                w.WriteByte(0x11);
                w.WriteInt16(0x2233);
                w.WriteInt32(0x44556677);
                w.WriteByte(0x88);
                w.WriteDouble(1.0);
            });

            AssertPayload(
            [
                0x11, 0x00,                                     // byte, pad to 2
                0x33, 0x22,                                     // int16
                0x77, 0x66, 0x55, 0x44,                         // int32 at 4
                0x88, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // byte, pad to 16
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xF0, 0x3F, // double 1.0 at 16
            ], bytes);
        }

        [Fact]
        public void Bool_IsOneByte()
        {
            AssertPayload([0x01, 0x00], Write((ref CdrWriter w) => { w.WriteBool(true); w.WriteBool(false); }));
        }

        [Fact]
        public void String_IsLengthWithTerminator_ThenUtf8_ThenNul()
        {
            AssertPayload([0x04, 0, 0, 0, (byte)'a', 0xC3, 0xA9, 0x00], Write((ref CdrWriter w) => w.WriteString("aé")));
        }

        [Fact]
        public void NullString_IsWrittenAsEmpty()
        {
            AssertPayload([0x01, 0, 0, 0, 0x00], Write((ref CdrWriter w) => w.WriteString(null)));
        }

        [Fact]
        public void String_OverBound_Throws()
        {
            Write((ref CdrWriter w) => w.WriteString("abc", bound: 3));
            var ex = Assert.Throws<ArgumentException>(() => Write((ref CdrWriter w) => w.WriteString("abcd", bound: 3, member: "Msg.name")));
            Assert.Contains("Msg.name", ex.Message);
        }

        [Fact]
        public void String_BoundCountsUtf8Bytes()
        {
            // "é" is one char but two UTF-8 bytes
            Assert.Throws<ArgumentException>(() => Write((ref CdrWriter w) => w.WriteString("é", bound: 1)));
        }

        [Fact]
        public void WideString_IsByteLength_ThenUtf16CodeUnits()
        {
            // U+1F600 is the surrogate pair D83D DE00, so three code units, six bytes
            AssertPayload(
            [
                0x06, 0, 0, 0,
                0xE9, 0x00,
                0x3D, 0xD8,
                0x00, 0xDE,
            ], Write((ref CdrWriter w) => w.WriteWideString("é\U0001F600")));
        }

        [Fact]
        public void WideString_UnpairedSurrogate_Throws()
        {
            Assert.Throws<ArgumentException>(() => Write((ref CdrWriter w) => w.WriteWideString("a\uD83D")));
        }

        [Fact]
        public void WideString_OverBound_Throws()
        {
            Assert.Throws<ArgumentException>(() => Write((ref CdrWriter w) => w.WriteWideString("abc", bound: 2)));
        }

        [Fact]
        public void PrimitiveSpan_AlignsOnceThenIsContiguous()
        {
            var bytes = Write((ref CdrWriter w) =>
            {
                w.WriteByte(0xAA);
                w.WritePrimitives<ushort>([1, 2, 3]);
            });

            AssertPayload([0xAA, 0x00, 0x01, 0x00, 0x02, 0x00, 0x03, 0x00], bytes);
        }

        [Fact]
        public void Bools_AreCanonicalised()
        {
            AssertPayload([0x01, 0x00, 0x01], Write((ref CdrWriter w) => w.WriteBools([true, false, true])));
        }

        [Fact]
        public void SequenceLength_OverBound_Throws()
        {
            Write((ref CdrWriter w) => w.WriteSequenceLength(3, bound: 3));
            Assert.Throws<ArgumentException>(() => Write((ref CdrWriter w) => w.WriteSequenceLength(4, bound: 3)));
        }

        [Fact]
        public void Buffer_GrowsBeyondCapacityHint_PreservingContent()
        {
            var bytes = Write((ref CdrWriter w) =>
            {
                for (int i = 0; i < 10_000; i++) w.WriteInt32(i);
            }, capacityHint: 1);

            var reader = new CdrReader(bytes);
            for (int i = 0; i < 10_000; i++) Assert.Equal(i, reader.ReadInt32());
            Assert.Equal(0, reader.Remaining);
        }
    }

    public class CdrReaderTests
    {
        private delegate T ReadFunc<T>(ref CdrReader reader);

        private static T Read<T>(byte[] data, ReadFunc<T> read)
        {
            var reader = new CdrReader(data);
            return read(ref reader);
        }

        [Fact]
        public void RoundTrip_AllPrimitivesAndStrings()
        {
            var writer = new CdrWriter(16);
            byte[] bytes;
            try
            {
                writer.WriteBool(true);
                writer.WriteSByte(-5);
                writer.WriteByte(250);
                writer.WriteInt16(-300);
                writer.WriteUInt16(60000);
                writer.WriteInt32(int.MinValue);
                writer.WriteUInt32(uint.MaxValue);
                writer.WriteInt64(long.MinValue);
                writer.WriteUInt64(ulong.MaxValue);
                writer.WriteSingle(1.5f);
                writer.WriteDouble(-2.25);
                writer.WriteString("héllo");
                writer.WriteWideString("wide \U0001F600");
                writer.WritePrimitives<double>([1, 2, 3]);
                bytes = writer.WrittenSpan.ToArray();
            }
            finally
            {
                writer.Dispose();
            }

            var reader = new CdrReader(bytes);
            Assert.True(reader.ReadBool());
            Assert.Equal(-5, reader.ReadSByte());
            Assert.Equal(250, reader.ReadByte());
            Assert.Equal(-300, reader.ReadInt16());
            Assert.Equal(60000, reader.ReadUInt16());
            Assert.Equal(int.MinValue, reader.ReadInt32());
            Assert.Equal(uint.MaxValue, reader.ReadUInt32());
            Assert.Equal(long.MinValue, reader.ReadInt64());
            Assert.Equal(ulong.MaxValue, reader.ReadUInt64());
            Assert.Equal(1.5f, reader.ReadSingle());
            Assert.Equal(-2.25, reader.ReadDouble());
            Assert.Equal("héllo", reader.ReadString());
            Assert.Equal("wide \U0001F600", reader.ReadWideString());
            Assert.Equal(new[] { 1.0, 2.0, 3.0 }, reader.ReadPrimitiveArray<double>(3));
            Assert.Equal(0, reader.Remaining);
        }

        [Fact]
        public void Xcdr2_CapsAlignmentAtFour()
        {
            // CDR2_LE header; the double follows the byte after 3 bytes of padding, not 7
            byte[] data = [0x00, 0x07, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xF0, 0x3F];

            var reader = new CdrReader(data);
            Assert.Equal(1, reader.ReadByte());
            Assert.Equal(1.0, reader.ReadDouble());
        }

        [Theory]
        [InlineData(0x00, 0x00)] // CDR_BE
        [InlineData(0x00, 0x03)] // PL_CDR_LE
        [InlineData(0x00, 0x09)] // D_CDR2_LE
        public void UnsupportedEncapsulation_Throws(byte first, byte second)
        {
            Assert.Throws<InvalidDataException>(() => { _ = new CdrReader(new byte[] { first, second, 0, 0 }); });
        }

        [Fact]
        public void TruncatedPayload_Throws()
        {
            byte[] data = [0x00, 0x01, 0x00, 0x00, 0x01, 0x02];
            Assert.Throws<InvalidDataException>(() => Read(data, (ref CdrReader r) => r.ReadInt32()));
        }

        [Fact]
        public void SequenceCountBeyondRemainingData_Throws()
        {
            // Claims 1000 doubles with no data following
            byte[] data = [0x00, 0x01, 0x00, 0x00, 0xE8, 0x03, 0x00, 0x00];
            Assert.Throws<InvalidDataException>(() => Read(data, (ref CdrReader r) => r.ReadSequenceLength(sizeof(double))));
        }

        [Fact]
        public void WideStringOddByteLength_Throws()
        {
            byte[] data = [0x00, 0x01, 0x00, 0x00, 0x03, 0, 0, 0, 0x61, 0x00, 0x62];
            Assert.Throws<InvalidDataException>(() => Read(data, (ref CdrReader r) => r.ReadWideString()));
        }

        [Fact]
        public void PrimitiveList_HasExactCount()
        {
            byte[] data = [0x00, 0x01, 0x00, 0x00, 0x07, 0x00, 0x09, 0x00];
            var list = Read(data, (ref CdrReader r) => r.ReadPrimitiveList<short>(2));
            Assert.Equal(new List<short> { 7, 9 }, list);
        }
    }
}

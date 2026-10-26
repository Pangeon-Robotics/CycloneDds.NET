using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace CycloneDDS.Core
{
    /// <summary>
    /// Reads a little-endian plain-CDR payload (encapsulation header included) of a final type.
    /// Accepts XCDR1 and XCDR2, which for final types differ only in maximum alignment.
    /// </summary>
    /// <remarks>
    /// Cyclone has already validated received data against the type's ops and converted it to
    /// host byte order, so malformed input indicates a mismatch with the descriptor. It is still
    /// bounds-checked and reported as <see cref="InvalidDataException"/>.
    /// </remarks>
    public ref struct CdrReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private readonly int _maxAlignment;
        private int _position;

        /// <exception cref="InvalidDataException">The header is truncated or names an unsupported encoding.</exception>
        public CdrReader(ReadOnlySpan<byte> data)
        {
            if (!BitConverter.IsLittleEndian)
                throw new PlatformNotSupportedException("CDR deserialization requires a little-endian host.");

            if (data.Length < CdrWriter.HeaderSize)
                throw new InvalidDataException("CDR payload is shorter than its encapsulation header.");

            // Identifier is big-endian on the wire: CDR_LE = 0x0001, CDR2_LE = 0x0007
            int identifier = (data[0] << 8) | data[1];
            _maxAlignment = identifier switch
            {
                0x0001 => 8,
                0x0007 => 4,
                _ => throw new InvalidDataException($"Unsupported CDR encapsulation 0x{identifier:x4}; expected little-endian plain CDR (XCDR1 or XCDR2)."),
            };

            _data = data;
            _position = CdrWriter.HeaderSize;
        }

        /// <summary>Bytes not yet consumed, trailing padding included.</summary>
        public readonly int Remaining => _data.Length - _position;


        // ---- Primitives ------------------------------------------------------------

        public bool ReadBool() => ReadByte() != 0;

        public byte ReadByte() => Take(1)[0];

        public sbyte ReadSByte() => (sbyte)ReadByte();

        public short ReadInt16() => BinaryPrimitives.ReadInt16LittleEndian(TakeAligned(2));
        public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(TakeAligned(2));
        public int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(TakeAligned(4));
        public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(TakeAligned(4));
        public long ReadInt64() => BinaryPrimitives.ReadInt64LittleEndian(TakeAligned(8));
        public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(TakeAligned(8));
        public float ReadSingle() => BinaryPrimitives.ReadSingleLittleEndian(TakeAligned(4));
        public double ReadDouble() => BinaryPrimitives.ReadDoubleLittleEndian(TakeAligned(8));


        // ---- Strings ---------------------------------------------------------------

        /// <summary>Reads a UTF-8 string written as uint32 length (terminator included), bytes, NUL.</summary>
        public string ReadString()
        {
            uint length = ReadUInt32();
            if (length == 0) return string.Empty;
            if (length > (uint)Remaining) throw Truncated();

            ReadOnlySpan<byte> bytes = Take((int)length);
            return Encoding.UTF8.GetString(bytes[..^1]);
        }

        /// <summary>
        /// Reads a wide string in Cyclone's layout: uint32 length in bytes, then the UTF-16 code units.
        /// </summary>
        public string ReadWideString()
        {
            uint byteCount = ReadUInt32();
            if (byteCount == 0) return string.Empty;
            if (byteCount % sizeof(char) != 0) throw new InvalidDataException($"Wide string length {byteCount} is not a whole number of UTF-16 code units.");
            if (byteCount > (uint)Remaining) throw Truncated();

            ReadOnlySpan<byte> bytes = TakeAligned(sizeof(char), (int)byteCount);
            return new string(MemoryMarshal.Cast<byte, char>(bytes));
        }


        // ---- Collections -----------------------------------------------------------

        /// <summary>
        /// Reads a sequence length prefix, rejecting counts the remaining data cannot hold so a
        /// corrupt count cannot trigger a huge allocation.
        /// </summary>
        /// <param name="minElementSize">Smallest serialized size of one element, at least 1.</param>
        public int ReadSequenceLength(int minElementSize = 1) => ReadCount(minElementSize);

        /// <summary>Bulk-reads numeric primitives (never bool) into <paramref name="destination"/>.</summary>
        public void ReadPrimitives<T>(scoped Span<T> destination) where T : unmanaged
        {
            if (destination.IsEmpty) return;

            Span<byte> target = MemoryMarshal.AsBytes(destination);
            TakeAligned(Unsafe.SizeOf<T>(), target.Length).CopyTo(target);
        }

        public T[] ReadPrimitiveArray<T>(int count) where T : unmanaged
        {
            if (count == 0) return [];

            var result = new T[count];
            ReadPrimitives<T>(result);
            return result;
        }

        public List<T> ReadPrimitiveList<T>(int count) where T : unmanaged
        {
            var result = new List<T>(count);
            CollectionsMarshal.SetCount(result, count);
            ReadPrimitives(CollectionsMarshal.AsSpan(result));
            return result;
        }

        public void ReadBools(scoped Span<bool> destination)
        {
            ReadOnlySpan<byte> source = Take(destination.Length);
            for (int i = 0; i < destination.Length; i++)
                destination[i] = source[i] != 0;
        }

        public T ReadStruct<T>() where T : IDdsType<T> => T.Deserialize(ref this);


        // ---- Buffer access ---------------------------------------------------------

        private int ReadCount(int minElementSize)
        {
            uint count = ReadUInt32();
            if (count > (uint)(Remaining / Math.Max(minElementSize, 1))) throw Truncated();
            return (int)count;
        }

        private ReadOnlySpan<byte> TakeAligned(int alignment) => TakeAligned(alignment, alignment);

        /// <summary>Skips padding to <paramref name="alignment"/> (capped by the encoding) and consumes <paramref name="size"/> bytes.</summary>
        private ReadOnlySpan<byte> TakeAligned(int alignment, int size)
        {
            alignment = Math.Min(alignment, _maxAlignment);
            int padding = -(_position - CdrWriter.HeaderSize) & (alignment - 1);
            return Take(padding + size)[padding..];
        }

        private ReadOnlySpan<byte> Take(int size)
        {
            if (size > Remaining) throw Truncated();

            ReadOnlySpan<byte> span = _data.Slice(_position, size);
            _position += size;
            return span;
        }

        private static InvalidDataException Truncated() =>
            new("CDR payload ends before the type does; it does not match the generated serializer.");
    }
}

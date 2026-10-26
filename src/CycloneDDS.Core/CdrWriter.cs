using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace CycloneDDS.Core
{
    /// <summary>
    /// Writes a little-endian XCDR1 payload (encapsulation header included) into a pooled,
    /// growable buffer. Must be disposed to return the buffer to the pool.
    /// </summary>
    /// <remarks>
    /// XCDR1 aligns each primitive to its own size, up to 8, relative to the first byte after
    /// the 4-byte encapsulation header. Final types carry no further headers.
    /// </remarks>
    public ref struct CdrWriter
    {
        /// <summary>Size of the encapsulation header that precedes the payload.</summary>
        public const int HeaderSize = 4;

        private byte[] _buffer;
        private int _position;

        /// <param name="capacityHint">Expected total size in bytes; the buffer grows past it as needed.</param>
        public CdrWriter(int capacityHint)
        {
            if (!BitConverter.IsLittleEndian)
                throw new PlatformNotSupportedException("CDR serialization requires a little-endian host.");

            _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(capacityHint, 64));

            // Identifier CDR_LE (big-endian 0x0001), options 0
            _buffer[0] = 0x00;
            _buffer[1] = 0x01;
            _buffer[2] = 0x00;
            _buffer[3] = 0x00;
            _position = HeaderSize;
        }

        /// <summary>Total bytes written, header included.</summary>
        public readonly int Length => _position;

        /// <summary>Header and payload written so far. Invalidated by further writes and by Dispose.</summary>
        public readonly ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _position);

        public void Dispose()
        {
            if (_buffer is null) return;
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = null!;
        }


        // ---- Primitives ------------------------------------------------------------

        public void WriteBool(bool value) => WriteByte(value ? (byte)1 : (byte)0);

        public void WriteByte(byte value)
        {
            Reserve(1)[0] = value;
        }

        public void WriteSByte(sbyte value) => WriteByte((byte)value);

        public void WriteInt16(short value) => BinaryPrimitives.WriteInt16LittleEndian(ReserveAligned(2), value);
        public void WriteUInt16(ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(ReserveAligned(2), value);
        public void WriteInt32(int value) => BinaryPrimitives.WriteInt32LittleEndian(ReserveAligned(4), value);
        public void WriteUInt32(uint value) => BinaryPrimitives.WriteUInt32LittleEndian(ReserveAligned(4), value);
        public void WriteInt64(long value) => BinaryPrimitives.WriteInt64LittleEndian(ReserveAligned(8), value);
        public void WriteUInt64(ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(ReserveAligned(8), value);
        public void WriteSingle(float value) => BinaryPrimitives.WriteSingleLittleEndian(ReserveAligned(4), value);
        public void WriteDouble(double value) => BinaryPrimitives.WriteDoubleLittleEndian(ReserveAligned(8), value);


        // ---- Strings ---------------------------------------------------------------

        /// <summary>
        /// Writes a UTF-8 string: uint32 length including the terminator, the bytes, then NUL.
        /// Null is written as the empty string.
        /// </summary>
        /// <param name="value">String to write.</param>
        /// <param name="bound">Maximum length in UTF-8 bytes, or 0 for unbounded.</param>
        /// <param name="member">Member name, used only in the exception message.</param>
        /// <exception cref="ArgumentException">The string exceeds <paramref name="bound"/>.</exception>
        public void WriteString(string? value, int bound = 0, string? member = null)
        {
            value ??= string.Empty;

            int byteCount = Encoding.UTF8.GetByteCount(value);
            if (bound > 0 && byteCount > bound)
                throw new ArgumentException($"{member ?? "String"} is {byteCount} UTF-8 bytes, exceeding its bound of {bound}.");

            WriteUInt32((uint)byteCount + 1);

            Span<byte> target = Reserve(byteCount + 1);
            Encoding.UTF8.GetBytes(value, target);
            target[byteCount] = 0;
        }

        /// <summary>
        /// Writes a wide string as Cyclone does: uint32 length in bytes, then the UTF-16 code
        /// units, without terminator. Null is written as the empty string.
        /// </summary>
        /// <remarks>
        /// Not interoperable with ROS 2: rmw_cyclonedds encodes wstring differently.
        /// </remarks>
        /// <param name="value">String to write.</param>
        /// <param name="bound">Maximum length in UTF-16 code units, or 0 for unbounded.</param>
        /// <param name="member">Member name, used only in the exception message.</param>
        /// <exception cref="ArgumentException">
        /// The string exceeds <paramref name="bound"/>, or contains an unpaired surrogate (which Cyclone rejects).
        /// </exception>
        public void WriteWideString(string? value, int bound = 0, string? member = null)
        {
            value ??= string.Empty;

            if (bound > 0 && value.Length > bound)
                throw new ArgumentException($"{member ?? "Wide string"} is {value.Length} UTF-16 code units, exceeding its bound of {bound}.");
            if (HasUnpairedSurrogate(value))
                throw new ArgumentException($"{member ?? "Wide string"} contains an unpaired UTF-16 surrogate.");

            WriteUInt32((uint)(value.Length * sizeof(char)));
            WritePrimitives<char>(value.AsSpan());
        }

        private static bool HasUnpairedSurrogate(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                if (!char.IsSurrogate(value[i])) continue;
                if (!char.IsSurrogatePair(value, i)) return true;
                i++;
            }
            return false;
        }


        // ---- Collections -----------------------------------------------------------

        /// <summary>Writes a sequence length prefix.</summary>
        /// <param name="count">Number of elements that follow.</param>
        /// <param name="bound">Maximum element count, or 0 for unbounded.</param>
        /// <param name="member">Member name, used only in the exception message.</param>
        /// <exception cref="ArgumentException">The count exceeds <paramref name="bound"/>.</exception>
        public void WriteSequenceLength(int count, int bound = 0, string? member = null)
        {
            if (bound > 0 && count > bound)
                throw new ArgumentException($"{member ?? "Sequence"} has {count} elements, exceeding its bound of {bound}.");

            WriteUInt32((uint)count);
        }

        /// <summary>
        /// Bulk-copies numeric primitives (never bool, whose in-memory value need not be 0 or 1).
        /// Aligns once to the element size; elements are then contiguous.
        /// </summary>
        public void WritePrimitives<T>(scoped ReadOnlySpan<T> values) where T : unmanaged
        {
            if (values.IsEmpty) return;

            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(values);
            bytes.CopyTo(ReserveAligned(Unsafe.SizeOf<T>(), bytes.Length));
        }

        public void WriteBools(scoped ReadOnlySpan<bool> values)
        {
            Span<byte> target = Reserve(values.Length);
            for (int i = 0; i < values.Length; i++)
                target[i] = values[i] ? (byte)1 : (byte)0;
        }

        /// <summary>
        /// Writes a nested struct. A null reference (class types) is rejected because an
        /// absent member cannot be expressed in final XCDR1.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
        public void WriteStruct<T>(in T value, string? member = null) where T : IDdsType<T>
        {
            if (value is null)
                throw new ArgumentNullException(member ?? typeof(T).Name, $"{typeof(T).Name} member must not be null.");

            T.Serialize(ref this, in value);
        }

        /// <summary>Throws when a fixed-length array does not have its declared length.</summary>
        public static void ThrowArrayLength(string member, int expected, int actual) =>
            throw new ArgumentException($"{member} has {actual} elements; its fixed length is {expected}.");


        // ---- Buffer management -----------------------------------------------------

        private Span<byte> ReserveAligned(int alignment) => ReserveAligned(alignment, alignment);

        /// <summary>Pads to <paramref name="alignment"/> (a power of two) and reserves <paramref name="size"/> bytes.</summary>
        private Span<byte> ReserveAligned(int alignment, int size)
        {
            int padding = -(_position - HeaderSize) & (alignment - 1);
            Span<byte> span = Reserve(padding + size);
            span[..padding].Clear();
            return span[padding..];
        }

        private Span<byte> Reserve(int size)
        {
            if (_position + size > _buffer.Length) Grow(size);

            Span<byte> span = _buffer.AsSpan(_position, size);
            _position += size;
            return span;
        }

        private void Grow(int size)
        {
            byte[] larger = ArrayPool<byte>.Shared.Rent(Math.Max(_buffer.Length * 2, _position + size));
            _buffer.AsSpan(0, _position).CopyTo(larger);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = larger;
        }
    }
}

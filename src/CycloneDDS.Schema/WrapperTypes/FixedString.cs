using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Unicode;

namespace CycloneDDS.Schema;

/// <summary>
/// Shared encode/decode logic for the FixedStringN types.
/// Content is UTF-8, terminated by the first NUL or by the end of the buffer when full.
/// </summary>
internal static class FixedString
{
    /// <summary>Encodes <paramref name="value"/> into <paramref name="buffer"/>; null encodes as empty.</summary>
    /// <returns>False if the value does not fit or contains unpaired surrogates; the buffer is left zeroed.</returns>
    public static bool TryEncode(string? value, Span<byte> buffer) => Encode(value, buffer) == OperationStatus.Done;

    /// <summary>Encodes <paramref name="value"/> into <paramref name="buffer"/>; null encodes as empty.</summary>
    /// <exception cref="ArgumentException">The value does not fit or contains unpaired surrogates.</exception>
    public static void EncodeOrThrow(string? value, Span<byte> buffer, string typeName)
    {
        switch (Encode(value, buffer))
        {
            case OperationStatus.Done:
                return;

            case OperationStatus.DestinationTooSmall:
                throw new ArgumentException(
                    $"String is too long ({Encoding.UTF8.GetByteCount(value!)} bytes) for {typeName} ({buffer.Length} bytes).",
                    nameof(value));

            default:
                throw new ArgumentException("String contains unpaired surrogates and cannot be encoded as UTF-8.", nameof(value));
        }
    }

    /// <summary>Returns the encoded content, excluding NUL padding.</summary>
    public static ReadOnlySpan<byte> Content(ReadOnlySpan<byte> buffer)
    {
        int nul = buffer.IndexOf((byte)0);
        return nul < 0 ? buffer : buffer[..nul];
    }

    public static string Decode(ReadOnlySpan<byte> buffer) => Encoding.UTF8.GetString(Content(buffer));

    private static OperationStatus Encode(string? value, Span<byte> buffer)
    {
        // Zeroing first provides the NUL padding.
        buffer.Clear();
        if (value is null) return OperationStatus.Done;

        var status = Utf8.FromUtf16(value, buffer, out _, out _, replaceInvalidSequences: false);
        if (status != OperationStatus.Done) buffer.Clear();
        return status;
    }
}

/// <summary>Inline UTF-8 string of at most <see cref="Capacity"/> bytes. Maps to IDL <c>char[Capacity]</c>.</summary>
public struct FixedString32
{
    /// <summary>Maximum encoded length in bytes.</summary>
    public const int Capacity = 32;

    [InlineArray(Capacity)]
    private struct Buffer { private byte _element0; }

    private Buffer _buffer;

    /// <summary>Creates the string from <paramref name="value"/>; null yields an empty string.</summary>
    /// <exception cref="ArgumentException">The value does not fit or contains unpaired surrogates.</exception>
    public FixedString32(string? value) => FixedString.EncodeOrThrow(value, _buffer, nameof(FixedString32));

    /// <summary>Tries to create the string from <paramref name="value"/>; null yields an empty string.</summary>
    public static bool TryFrom(string? value, out FixedString32 result)
    {
        result = default;
        return FixedString.TryEncode(value, result._buffer);
    }

    /// <summary>Encoded length in bytes.</summary>
    public readonly int Length => FixedString.Content(_buffer).Length;

    /// <summary>Encoded UTF-8 content, referencing this instance's storage.</summary>
    [UnscopedRef]
    public readonly ReadOnlySpan<byte> AsUtf8Span() => FixedString.Content(_buffer);

    /// <inheritdoc/>
    public override readonly string ToString() => FixedString.Decode(_buffer);
}

/// <summary>Inline UTF-8 string of at most <see cref="Capacity"/> bytes. Maps to IDL <c>char[Capacity]</c>.</summary>
public struct FixedString64
{
    /// <summary>Maximum encoded length in bytes.</summary>
    public const int Capacity = 64;

    [InlineArray(Capacity)]
    private struct Buffer { private byte _element0; }

    private Buffer _buffer;

    /// <summary>Creates the string from <paramref name="value"/>; null yields an empty string.</summary>
    /// <exception cref="ArgumentException">The value does not fit or contains unpaired surrogates.</exception>
    public FixedString64(string? value) => FixedString.EncodeOrThrow(value, _buffer, nameof(FixedString64));

    /// <summary>Tries to create the string from <paramref name="value"/>; null yields an empty string.</summary>
    public static bool TryFrom(string? value, out FixedString64 result)
    {
        result = default;
        return FixedString.TryEncode(value, result._buffer);
    }

    /// <summary>Encoded length in bytes.</summary>
    public readonly int Length => FixedString.Content(_buffer).Length;

    /// <summary>Encoded UTF-8 content, referencing this instance's storage.</summary>
    [UnscopedRef]
    public readonly ReadOnlySpan<byte> AsUtf8Span() => FixedString.Content(_buffer);

    /// <inheritdoc/>
    public override readonly string ToString() => FixedString.Decode(_buffer);
}

/// <summary>Inline UTF-8 string of at most <see cref="Capacity"/> bytes. Maps to IDL <c>char[Capacity]</c>.</summary>
public struct FixedString128
{
    /// <summary>Maximum encoded length in bytes.</summary>
    public const int Capacity = 128;

    [InlineArray(Capacity)]
    private struct Buffer { private byte _element0; }

    private Buffer _buffer;

    /// <summary>Creates the string from <paramref name="value"/>; null yields an empty string.</summary>
    /// <exception cref="ArgumentException">The value does not fit or contains unpaired surrogates.</exception>
    public FixedString128(string? value) => FixedString.EncodeOrThrow(value, _buffer, nameof(FixedString128));

    /// <summary>Tries to create the string from <paramref name="value"/>; null yields an empty string.</summary>
    public static bool TryFrom(string? value, out FixedString128 result)
    {
        result = default;
        return FixedString.TryEncode(value, result._buffer);
    }

    /// <summary>Encoded length in bytes.</summary>
    public readonly int Length => FixedString.Content(_buffer).Length;

    /// <summary>Encoded UTF-8 content, referencing this instance's storage.</summary>
    [UnscopedRef]
    public readonly ReadOnlySpan<byte> AsUtf8Span() => FixedString.Content(_buffer);

    /// <inheritdoc/>
    public override readonly string ToString() => FixedString.Decode(_buffer);
}

/// <summary>Inline UTF-8 string of at most <see cref="Capacity"/> bytes. Maps to IDL <c>char[Capacity]</c>.</summary>
public struct FixedString256
{
    /// <summary>Maximum encoded length in bytes.</summary>
    public const int Capacity = 256;

    [InlineArray(Capacity)]
    private struct Buffer { private byte _element0; }

    private Buffer _buffer;

    /// <summary>Creates the string from <paramref name="value"/>; null yields an empty string.</summary>
    /// <exception cref="ArgumentException">The value does not fit or contains unpaired surrogates.</exception>
    public FixedString256(string? value) => FixedString.EncodeOrThrow(value, _buffer, nameof(FixedString256));

    /// <summary>Tries to create the string from <paramref name="value"/>; null yields an empty string.</summary>
    public static bool TryFrom(string? value, out FixedString256 result)
    {
        result = default;
        return FixedString.TryEncode(value, result._buffer);
    }

    /// <summary>Encoded length in bytes.</summary>
    public readonly int Length => FixedString.Content(_buffer).Length;

    /// <summary>Encoded UTF-8 content, referencing this instance's storage.</summary>
    [UnscopedRef]
    public readonly ReadOnlySpan<byte> AsUtf8Span() => FixedString.Content(_buffer);

    /// <inheritdoc/>
    public override readonly string ToString() => FixedString.Decode(_buffer);
}

using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using MessagePack;

namespace Beacon.Protocol;

/// <summary>
/// Length-prefixed, HMAC-SHA256-authenticated frame codec for the Beacon IPC
/// pipe. Wire format:
///   [u32 length][u8 type][u8 flags][u32 seq][MessagePack payload][32 bytes hmac]
/// length covers type/flags/seq/payload/hmac. HMAC covers everything from the
/// type byte through end of payload (NOT the length prefix, since the prefix is
/// the framer's responsibility).
/// </summary>
public sealed class FrameCodec
{
    public const int LengthPrefixSize = sizeof(uint);
    public const int HeaderSize = 1 /*type*/ + 1 /*flags*/ + 4 /*seq*/;
    public const int HmacSize = 32;
    public const int MinFrameSize = HeaderSize + HmacSize;
    public const int MaxFrameSize = 1 * 1024 * 1024;

    private readonly byte[] _hmacKey;

    public FrameCodec(byte[] hmacKey)
    {
        if (hmacKey is null || hmacKey.Length < 32)
            throw new ArgumentException("hmacKey must be at least 32 bytes", nameof(hmacKey));
        _hmacKey = hmacKey;
    }

    /// <summary>
    /// Serialize a payload object as a framed byte buffer. Caller owns disposing
    /// the returned buffer back to the pool.
    /// </summary>
    public byte[] Encode<TPayload>(
        FrameType type,
        FrameFlags flags,
        uint sequence,
        TPayload payload)
    {
        var payloadBytes = MessagePackSerializer.Serialize(payload);
        var frameLen = HeaderSize + payloadBytes.Length + HmacSize;
        if (frameLen > MaxFrameSize)
            throw new InvalidOperationException($"frame too large: {frameLen} > {MaxFrameSize}");

        var buffer = new byte[LengthPrefixSize + frameLen];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), (uint)frameLen);
        buffer[4] = (byte)type;
        buffer[5] = (byte)flags;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(6, 4), sequence);
        Buffer.BlockCopy(payloadBytes, 0, buffer, 10, payloadBytes.Length);

        var authedSpan = buffer.AsSpan(LengthPrefixSize, HeaderSize + payloadBytes.Length);
        var hmacSpan = buffer.AsSpan(LengthPrefixSize + HeaderSize + payloadBytes.Length, HmacSize);
        ComputeHmac(authedSpan, hmacSpan);

        return buffer;
    }

    /// <summary>
    /// Parse one frame from the buffer. Returns false if not enough bytes for a
    /// full frame yet. Throws on HMAC failure or malformed frame.
    /// </summary>
    public bool TryDecode(
        ReadOnlySpan<byte> source,
        out int bytesConsumed,
        out FrameType type,
        out FrameFlags flags,
        out uint sequence,
        out byte[] payload)
    {
        bytesConsumed = 0;
        type = default;
        flags = default;
        sequence = 0;
        payload = Array.Empty<byte>();

        if (source.Length < LengthPrefixSize)
            return false;

        var frameLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(source[..4]);
        if (frameLen < MinFrameSize || frameLen > MaxFrameSize)
            throw new InvalidDataException($"frame length out of bounds: {frameLen}");

        if (source.Length < LengthPrefixSize + frameLen)
            return false;

        var authedSpan = source.Slice(LengthPrefixSize, frameLen - HmacSize);
        var hmacSpan = source.Slice(LengthPrefixSize + frameLen - HmacSize, HmacSize);
        Span<byte> expected = stackalloc byte[HmacSize];
        ComputeHmac(authedSpan, expected);
        if (!CryptographicOperations.FixedTimeEquals(expected, hmacSpan))
            throw new InvalidDataException("HMAC mismatch — pipe is being tampered with or wrong key");

        type = (FrameType)source[LengthPrefixSize];
        flags = (FrameFlags)source[LengthPrefixSize + 1];
        sequence = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(LengthPrefixSize + 2, 4));

        var payloadLen = authedSpan.Length - HeaderSize;
        payload = new byte[payloadLen];
        source.Slice(LengthPrefixSize + HeaderSize, payloadLen).CopyTo(payload);

        bytesConsumed = LengthPrefixSize + frameLen;
        return true;
    }

    public T DeserializePayload<T>(byte[] payload) =>
        MessagePackSerializer.Deserialize<T>(payload);

    private void ComputeHmac(ReadOnlySpan<byte> data, Span<byte> destination)
    {
        using var hmac = new HMACSHA256(_hmacKey);
        hmac.TryComputeHash(data, destination, out _);
    }
}

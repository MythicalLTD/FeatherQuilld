using System.Buffers.Binary;
using System.Text;

namespace d0x2a.EmbeddedSsh.Protocol;

/// <summary>
/// Binary writer for SSH protocol types (RFC 4251 §5).
/// </summary>
public ref struct SshWriter
{
    private Span<byte> _buffer;
    private int _position;

    /// <summary>
    /// Creates a new SSH writer over the specified buffer.
    /// </summary>
    public SshWriter(Span<byte> buffer)
    {
        _buffer = buffer;
        _position = 0;
    }

    /// <summary>
    /// Gets the current position in the buffer.
    /// </summary>
    public readonly int Position => _position;

    /// <summary>
    /// Gets the remaining space in the buffer.
    /// </summary>
    public readonly int Remaining => _buffer.Length - _position;

    /// <summary>
    /// Gets the written data as a span.
    /// </summary>
    public readonly ReadOnlySpan<byte> WrittenSpan => _buffer[.._position];

    /// <summary>
    /// Writes a single byte.
    /// </summary>
    public void WriteByte(byte value)
    {
        if (_position >= _buffer.Length)
            throw new SshProtocolException("Buffer overflow writing byte");

        _buffer[_position++] = value;
    }

    /// <summary>
    /// Writes a boolean (0x00 = false, 0x01 = true).
    /// </summary>
    public void WriteBoolean(bool value)
    {
        WriteByte(value ? (byte)1 : (byte)0);
    }

    /// <summary>
    /// Writes a 32-bit unsigned integer (big-endian).
    /// </summary>
    public void WriteUInt32(uint value)
    {
        if (_position + 4 > _buffer.Length)
            throw new SshProtocolException("Buffer overflow writing uint32");

        BinaryPrimitives.WriteUInt32BigEndian(_buffer[_position..], value);
        _position += 4;
    }

    /// <summary>
    /// Writes a 64-bit unsigned integer (big-endian).
    /// </summary>
    public void WriteUInt64(ulong value)
    {
        if (_position + 8 > _buffer.Length)
            throw new SshProtocolException("Buffer overflow writing uint64");

        BinaryPrimitives.WriteUInt64BigEndian(_buffer[_position..], value);
        _position += 8;
    }

    /// <summary>
    /// Writes a binary string (uint32 length + data).
    /// </summary>
    public void WriteBinaryString(ReadOnlySpan<byte> value)
    {
        WriteUInt32((uint)value.Length);

        if (_position + value.Length > _buffer.Length)
            throw new SshProtocolException("Buffer overflow writing string data");

        value.CopyTo(_buffer[_position..]);
        _position += value.Length;
    }

    /// <summary>
    /// Writes a UTF-8 string.
    /// </summary>
    public void WriteString(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteBinaryString(bytes);
    }

    /// <summary>
    /// Writes an mpint (multiple precision integer).
    /// The value should already be in two's complement big-endian format.
    /// </summary>
    public void WriteMpint(ReadOnlySpan<byte> value)
    {
        // Skip leading zeros
        int start = 0;
        while (start < value.Length && value[start] == 0)
            start++;

        if (start == value.Length)
        {
            // Zero value
            WriteUInt32(0);
            return;
        }

        var trimmed = value[start..];

        // If high bit is set, need to prepend 0x00 for positive numbers
        if ((trimmed[0] & 0x80) != 0)
        {
            WriteUInt32((uint)(trimmed.Length + 1));
            WriteByte(0);

            if (_position + trimmed.Length > _buffer.Length)
                throw new SshProtocolException("Buffer overflow writing mpint data");

            trimmed.CopyTo(_buffer[_position..]);
            _position += trimmed.Length;
        }
        else
        {
            WriteBinaryString(trimmed);
        }
    }

    /// <summary>
    /// Writes a name-list (comma-separated ASCII names).
    /// </summary>
    public void WriteNameList(IReadOnlyList<string> names)
    {
        if (names.Count == 0)
        {
            WriteUInt32(0);
            return;
        }

        var joined = string.Join(',', names);
        var bytes = Encoding.ASCII.GetBytes(joined);
        WriteBinaryString(bytes);
    }

    /// <summary>
    /// Writes raw bytes without a length prefix.
    /// </summary>
    public void WriteBytes(ReadOnlySpan<byte> value)
    {
        if (_position + value.Length > _buffer.Length)
            throw new SshProtocolException($"Buffer overflow writing {value.Length} bytes");

        value.CopyTo(_buffer[_position..]);
        _position += value.Length;
    }

    /// <summary>
    /// Calculates the size needed to write a binary string.
    /// </summary>
    public static int GetBinaryStringSize(int dataLength) => 4 + dataLength;

    /// <summary>
    /// Calculates the size needed to write a name list.
    /// </summary>
    public static int GetNameListSize(IReadOnlyList<string> names)
    {
        if (names.Count == 0)
            return 4;

        int total = 0;
        for (int i = 0; i < names.Count; i++)
        {
            if (i > 0) total++; // comma
            total += Encoding.ASCII.GetByteCount(names[i]);
        }
        return 4 + total;
    }

    /// <summary>
    /// Calculates the size needed to write an mpint.
    /// </summary>
    public static int GetMpintSize(ReadOnlySpan<byte> value)
    {
        // Skip leading zeros
        int start = 0;
        while (start < value.Length && value[start] == 0)
            start++;

        if (start == value.Length)
            return 4; // Just the length field for zero

        var trimmed = value[start..];

        // If high bit is set, need extra byte
        if ((trimmed[0] & 0x80) != 0)
            return 4 + 1 + trimmed.Length;

        return 4 + trimmed.Length;
    }
}

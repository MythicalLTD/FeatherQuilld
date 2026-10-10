using System.Buffers.Binary;
using System.Text;

namespace d0x2a.EmbeddedSsh.Protocol;

/// <summary>
/// Binary reader for SSH protocol types (RFC 4251 §5).
/// </summary>
public ref struct SshReader
{
    private ReadOnlySpan<byte> _buffer;
    private int _position;

    /// <summary>
    /// Creates a new SSH reader over the specified buffer.
    /// </summary>
    public SshReader(ReadOnlySpan<byte> buffer)
    {
        _buffer = buffer;
        _position = 0;
    }

    /// <summary>
    /// Gets the current position in the buffer.
    /// </summary>
    public readonly int Position => _position;

    /// <summary>
    /// Gets the remaining bytes in the buffer.
    /// </summary>
    public readonly int Remaining => _buffer.Length - _position;

    /// <summary>
    /// Gets the remaining data as a span.
    /// </summary>
    public readonly ReadOnlySpan<byte> RemainingSpan => _buffer[_position..];

    /// <summary>
    /// Reads a single byte.
    /// </summary>
    public byte ReadByte()
    {
        if (_position >= _buffer.Length)
            throw new SshProtocolException("Unexpected end of data reading byte");

        return _buffer[_position++];
    }

    /// <summary>
    /// Reads a boolean (0x00 = false, 0x01 = true).
    /// </summary>
    public bool ReadBoolean()
    {
        return ReadByte() != 0;
    }

    /// <summary>
    /// Reads a 32-bit unsigned integer (big-endian).
    /// </summary>
    public uint ReadUInt32()
    {
        if (_position + 4 > _buffer.Length)
            throw new SshProtocolException("Unexpected end of data reading uint32");

        var value = BinaryPrimitives.ReadUInt32BigEndian(_buffer[_position..]);
        _position += 4;
        return value;
    }

    /// <summary>
    /// Reads a 64-bit unsigned integer (big-endian).
    /// </summary>
    public ulong ReadUInt64()
    {
        if (_position + 8 > _buffer.Length)
            throw new SshProtocolException("Unexpected end of data reading uint64");

        var value = BinaryPrimitives.ReadUInt64BigEndian(_buffer[_position..]);
        _position += 8;
        return value;
    }

    /// <summary>
    /// Reads a binary string (uint32 length + data).
    /// Returns a span over the original buffer.
    /// </summary>
    public ReadOnlySpan<byte> ReadBinaryString()
    {
        var length = ReadUInt32();

        if (length > int.MaxValue)
            throw new SshProtocolException("String length too large");

        var len = (int)length;

        if (_position + len > _buffer.Length)
            throw new SshProtocolException("Unexpected end of data reading string");

        var value = _buffer.Slice(_position, len);
        _position += len;
        return value;
    }

    /// <summary>
    /// Reads a string as UTF-8 text.
    /// </summary>
    public string ReadString()
    {
        var bytes = ReadBinaryString();
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// Reads an mpint (multiple precision integer).
    /// Returns the raw bytes in two's complement big-endian format.
    /// </summary>
    public ReadOnlySpan<byte> ReadMpint()
    {
        return ReadBinaryString();
    }

    /// <summary>
    /// Reads a name-list (comma-separated ASCII names).
    /// </summary>
    public string[] ReadNameList()
    {
        var data = ReadBinaryString();

        if (data.IsEmpty)
            return [];

        var str = Encoding.ASCII.GetString(data);
        return str.Split(',');
    }

    /// <summary>
    /// Reads exactly the specified number of bytes.
    /// </summary>
    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        if (_position + count > _buffer.Length)
            throw new SshProtocolException($"Unexpected end of data reading {count} bytes");

        var value = _buffer.Slice(_position, count);
        _position += count;
        return value;
    }

    /// <summary>
    /// Skips the specified number of bytes.
    /// </summary>
    public void Skip(int count)
    {
        if (_position + count > _buffer.Length)
            throw new SshProtocolException($"Unexpected end of data skipping {count} bytes");

        _position += count;
    }

    /// <summary>
    /// Peeks at the next byte without advancing the position.
    /// </summary>
    public readonly byte Peek()
    {
        if (_position >= _buffer.Length)
            throw new SshProtocolException("Unexpected end of data peeking byte");

        return _buffer[_position];
    }

    /// <summary>
    /// Returns true if all data has been consumed.
    /// </summary>
    public readonly bool IsAtEnd => _position >= _buffer.Length;
}

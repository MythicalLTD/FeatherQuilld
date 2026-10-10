using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace d0x2a.EmbeddedSsh.Crypto;

/// <summary>
/// ChaCha20 stream cipher implementation (RFC 7539).
/// </summary>
public static class ChaCha20
{
    /// <summary>
    /// Encrypts or decrypts data using ChaCha20.
    /// </summary>
    /// <param name="key">32-byte key.</param>
    /// <param name="nonce">12-byte nonce.</param>
    /// <param name="counter">Initial block counter.</param>
    /// <param name="input">Input data.</param>
    /// <param name="output">Output buffer (same size as input).</param>
    public static void Process(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> nonce,
        uint counter,
        ReadOnlySpan<byte> input,
        Span<byte> output)
    {
        if (key.Length != 32)
            throw new ArgumentException("Key must be 32 bytes", nameof(key));
        if (nonce.Length != 12)
            throw new ArgumentException("Nonce must be 12 bytes", nameof(nonce));
        if (output.Length < input.Length)
            throw new ArgumentException("Output buffer too small", nameof(output));

        Span<uint> state = stackalloc uint[16];
        Span<uint> working = stackalloc uint[16];
        Span<byte> block = stackalloc byte[64];

        // Initialize state
        InitializeState(state, key, nonce, counter);

        var offset = 0;
        while (offset < input.Length)
        {
            // Copy state to working
            state.CopyTo(working);

            // 20 rounds (10 double rounds)
            for (int i = 0; i < 10; i++)
            {
                // Column rounds
                QuarterRound(ref working[0], ref working[4], ref working[8], ref working[12]);
                QuarterRound(ref working[1], ref working[5], ref working[9], ref working[13]);
                QuarterRound(ref working[2], ref working[6], ref working[10], ref working[14]);
                QuarterRound(ref working[3], ref working[7], ref working[11], ref working[15]);

                // Diagonal rounds
                QuarterRound(ref working[0], ref working[5], ref working[10], ref working[15]);
                QuarterRound(ref working[1], ref working[6], ref working[11], ref working[12]);
                QuarterRound(ref working[2], ref working[7], ref working[8], ref working[13]);
                QuarterRound(ref working[3], ref working[4], ref working[9], ref working[14]);
            }

            // Add original state
            for (int i = 0; i < 16; i++)
                working[i] += state[i];

            // Serialize to bytes (little-endian)
            for (int i = 0; i < 16; i++)
                BinaryPrimitives.WriteUInt32LittleEndian(block[(i * 4)..], working[i]);

            // XOR with input
            var toCopy = Math.Min(64, input.Length - offset);
            for (int i = 0; i < toCopy; i++)
                output[offset + i] = (byte)(input[offset + i] ^ block[i]);

            offset += 64;
            state[12]++; // Increment counter
        }
    }

    /// <summary>
    /// Generates a keystream block (for Poly1305 key derivation).
    /// </summary>
    /// <param name="key">32-byte key.</param>
    /// <param name="nonce">12-byte nonce.</param>
    /// <param name="counter">Block counter.</param>
    /// <param name="output">Output buffer (64 bytes).</param>
    public static void Block(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> nonce,
        uint counter,
        Span<byte> output)
    {
        if (output.Length < 64)
            throw new ArgumentException("Output must be at least 64 bytes", nameof(output));

        Span<uint> state = stackalloc uint[16];
        Span<uint> working = stackalloc uint[16];

        InitializeState(state, key, nonce, counter);
        state.CopyTo(working);

        // 20 rounds
        for (int i = 0; i < 10; i++)
        {
            QuarterRound(ref working[0], ref working[4], ref working[8], ref working[12]);
            QuarterRound(ref working[1], ref working[5], ref working[9], ref working[13]);
            QuarterRound(ref working[2], ref working[6], ref working[10], ref working[14]);
            QuarterRound(ref working[3], ref working[7], ref working[11], ref working[15]);
            QuarterRound(ref working[0], ref working[5], ref working[10], ref working[15]);
            QuarterRound(ref working[1], ref working[6], ref working[11], ref working[12]);
            QuarterRound(ref working[2], ref working[7], ref working[8], ref working[13]);
            QuarterRound(ref working[3], ref working[4], ref working[9], ref working[14]);
        }

        // Add original state and serialize
        for (int i = 0; i < 16; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(output[(i * 4)..], working[i] + state[i]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void InitializeState(
        Span<uint> state,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> nonce,
        uint counter)
    {
        // "expand 32-byte k" in little-endian
        state[0] = 0x61707865; // "expa"
        state[1] = 0x3320646e; // "nd 3"
        state[2] = 0x79622d32; // "2-by"
        state[3] = 0x6b206574; // "te k"

        // Key (8 words, little-endian)
        for (int i = 0; i < 8; i++)
            state[4 + i] = BinaryPrimitives.ReadUInt32LittleEndian(key[(i * 4)..]);

        // Counter
        state[12] = counter;

        // Nonce (3 words, little-endian)
        for (int i = 0; i < 3; i++)
            state[13 + i] = BinaryPrimitives.ReadUInt32LittleEndian(nonce[(i * 4)..]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void QuarterRound(ref uint a, ref uint b, ref uint c, ref uint d)
    {
        a += b; d ^= a; d = RotateLeft(d, 16);
        c += d; b ^= c; b = RotateLeft(b, 12);
        a += b; d ^= a; d = RotateLeft(d, 8);
        c += d; b ^= c; b = RotateLeft(b, 7);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint RotateLeft(uint value, int bits)
        => (value << bits) | (value >> (32 - bits));

    #region OpenSSH variant (8-byte nonce)

    /// <summary>
    /// Encrypts or decrypts data using ChaCha20 with 8-byte nonce (OpenSSH variant).
    /// Used by chacha20-poly1305@openssh.com.
    /// </summary>
    /// <param name="key">32-byte key.</param>
    /// <param name="nonce">8-byte nonce (sequence number as big-endian uint64).</param>
    /// <param name="counter">Initial 64-bit block counter.</param>
    /// <param name="input">Input data.</param>
    /// <param name="output">Output buffer (same size as input).</param>
    public static void ProcessOpenSsh(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> nonce,
        ulong counter,
        ReadOnlySpan<byte> input,
        Span<byte> output)
    {
        if (key.Length != 32)
            throw new ArgumentException("Key must be 32 bytes", nameof(key));
        if (nonce.Length != 8)
            throw new ArgumentException("Nonce must be 8 bytes", nameof(nonce));
        if (output.Length < input.Length)
            throw new ArgumentException("Output buffer too small", nameof(output));

        Span<uint> state = stackalloc uint[16];
        Span<uint> working = stackalloc uint[16];
        Span<byte> block = stackalloc byte[64];

        // Initialize state
        InitializeStateOpenSsh(state, key, nonce, counter);

        var offset = 0;
        while (offset < input.Length)
        {
            // Copy state to working
            state.CopyTo(working);

            // 20 rounds (10 double rounds)
            for (int i = 0; i < 10; i++)
            {
                // Column rounds
                QuarterRound(ref working[0], ref working[4], ref working[8], ref working[12]);
                QuarterRound(ref working[1], ref working[5], ref working[9], ref working[13]);
                QuarterRound(ref working[2], ref working[6], ref working[10], ref working[14]);
                QuarterRound(ref working[3], ref working[7], ref working[11], ref working[15]);

                // Diagonal rounds
                QuarterRound(ref working[0], ref working[5], ref working[10], ref working[15]);
                QuarterRound(ref working[1], ref working[6], ref working[11], ref working[12]);
                QuarterRound(ref working[2], ref working[7], ref working[8], ref working[13]);
                QuarterRound(ref working[3], ref working[4], ref working[9], ref working[14]);
            }

            // Add original state
            for (int i = 0; i < 16; i++)
                working[i] += state[i];

            // Serialize to bytes (little-endian)
            for (int i = 0; i < 16; i++)
                BinaryPrimitives.WriteUInt32LittleEndian(block[(i * 4)..], working[i]);

            // XOR with input
            var toCopy = Math.Min(64, input.Length - offset);
            for (int i = 0; i < toCopy; i++)
                output[offset + i] = (byte)(input[offset + i] ^ block[i]);

            offset += 64;

            // Increment 64-bit counter
            state[12]++;
            if (state[12] == 0)
                state[13]++;
        }
    }

    /// <summary>
    /// Generates a keystream block using OpenSSH variant (for Poly1305 key derivation).
    /// </summary>
    public static void BlockOpenSsh(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> nonce,
        ulong counter,
        Span<byte> output)
    {
        if (output.Length < 64)
            throw new ArgumentException("Output must be at least 64 bytes", nameof(output));

        Span<uint> state = stackalloc uint[16];
        Span<uint> working = stackalloc uint[16];

        InitializeStateOpenSsh(state, key, nonce, counter);
        state.CopyTo(working);

        // 20 rounds
        for (int i = 0; i < 10; i++)
        {
            QuarterRound(ref working[0], ref working[4], ref working[8], ref working[12]);
            QuarterRound(ref working[1], ref working[5], ref working[9], ref working[13]);
            QuarterRound(ref working[2], ref working[6], ref working[10], ref working[14]);
            QuarterRound(ref working[3], ref working[7], ref working[11], ref working[15]);
            QuarterRound(ref working[0], ref working[5], ref working[10], ref working[15]);
            QuarterRound(ref working[1], ref working[6], ref working[11], ref working[12]);
            QuarterRound(ref working[2], ref working[7], ref working[8], ref working[13]);
            QuarterRound(ref working[3], ref working[4], ref working[9], ref working[14]);
        }

        // Add original state and serialize
        for (int i = 0; i < 16; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(output[(i * 4)..], working[i] + state[i]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void InitializeStateOpenSsh(
        Span<uint> state,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> nonce,
        ulong counter)
    {
        // "expand 32-byte k" in little-endian
        state[0] = 0x61707865; // "expa"
        state[1] = 0x3320646e; // "nd 3"
        state[2] = 0x79622d32; // "2-by"
        state[3] = 0x6b206574; // "te k"

        // Key (8 words, little-endian)
        for (int i = 0; i < 8; i++)
            state[4 + i] = BinaryPrimitives.ReadUInt32LittleEndian(key[(i * 4)..]);

        // Counter (2 words, little-endian)
        state[12] = (uint)counter;
        state[13] = (uint)(counter >> 32);

        // Nonce (2 words, little-endian from bytes)
        state[14] = BinaryPrimitives.ReadUInt32LittleEndian(nonce);
        state[15] = BinaryPrimitives.ReadUInt32LittleEndian(nonce[4..]);
    }

    #endregion
}

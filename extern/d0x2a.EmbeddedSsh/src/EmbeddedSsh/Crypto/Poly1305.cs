using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace d0x2a.EmbeddedSsh.Crypto;

/// <summary>
/// Poly1305 message authentication code implementation (RFC 7539).
/// Uses 130-bit arithmetic with 5 limbs of 26 bits each.
/// Optimized with inline 64-bit arithmetic (no BigInteger).
/// </summary>
public static class Poly1305
{
    private const int TagSize = 16;

    /// <summary>
    /// Computes a Poly1305 tag for the given message.
    /// </summary>
    /// <param name="key">32-byte key (r || s).</param>
    /// <param name="message">Message to authenticate.</param>
    /// <returns>16-byte authentication tag.</returns>
    public static byte[] ComputeTag(ReadOnlySpan<byte> key, ReadOnlySpan<byte> message)
    {
        if (key.Length != 32)
            throw new ArgumentException("Key must be 32 bytes", nameof(key));

        var tag = new byte[TagSize];
        ComputeTag(key, message, tag);
        return tag;
    }

    /// <summary>
    /// Computes a Poly1305 tag for the given message.
    /// </summary>
    /// <param name="key">32-byte key (r || s).</param>
    /// <param name="message">Message to authenticate.</param>
    /// <param name="tag">Output buffer for 16-byte tag.</param>
    public static void ComputeTag(ReadOnlySpan<byte> key, ReadOnlySpan<byte> message, Span<byte> tag)
    {
        if (key.Length != 32)
            throw new ArgumentException("Key must be 32 bytes", nameof(key));
        if (tag.Length < TagSize)
            throw new ArgumentException("Tag buffer must be at least 16 bytes", nameof(tag));

        // Parse and clamp r
        ulong r0 = BinaryPrimitives.ReadUInt64LittleEndian(key[0..8]);
        ulong r1 = BinaryPrimitives.ReadUInt64LittleEndian(key[8..16]);

        // Clamp r: clear top 4 bits of each 32-bit half, clear bottom 2 bits of r[4,8,12]
        r0 &= 0x0FFFFFFC0FFFFFFF;
        r1 &= 0x0FFFFFFC0FFFFFFC;

        // Split r into 5 limbs of 26 bits
        ulong r_0 = r0 & 0x3FFFFFF;
        ulong r_1 = (r0 >> 26) & 0x3FFFFFF;
        ulong r_2 = ((r0 >> 52) | (r1 << 12)) & 0x3FFFFFF;
        ulong r_3 = (r1 >> 14) & 0x3FFFFFF;
        ulong r_4 = (r1 >> 40) & 0x3FFFFFF;

        // Precompute 5*r for reduction
        ulong s1 = r_1 * 5;
        ulong s2 = r_2 * 5;
        ulong s3 = r_3 * 5;
        ulong s4 = r_4 * 5;

        // Parse s
        ulong s_lo = BinaryPrimitives.ReadUInt64LittleEndian(key[16..24]);
        ulong s_hi = BinaryPrimitives.ReadUInt64LittleEndian(key[24..32]);

        // Accumulator (5 limbs)
        ulong h0 = 0, h1 = 0, h2 = 0, h3 = 0, h4 = 0;

        // Process full 16-byte blocks
        int offset = 0;
        while (offset + 16 <= message.Length)
        {
            // Read block
            ulong m_lo = BinaryPrimitives.ReadUInt64LittleEndian(message.Slice(offset, 8));
            ulong m_hi = BinaryPrimitives.ReadUInt64LittleEndian(message.Slice(offset + 8, 8));

            // Add block to accumulator with high bit
            h0 += m_lo & 0x3FFFFFF;
            h1 += (m_lo >> 26) & 0x3FFFFFF;
            h2 += ((m_lo >> 52) | (m_hi << 12)) & 0x3FFFFFF;
            h3 += (m_hi >> 14) & 0x3FFFFFF;
            h4 += (m_hi >> 40) | (1UL << 24); // High bit = 2^128

            // Multiply and reduce
            MultiplyReduce(ref h0, ref h1, ref h2, ref h3, ref h4,
                          r_0, r_1, r_2, r_3, r_4, s1, s2, s3, s4);

            offset += 16;
        }

        // Process final partial block
        if (offset < message.Length)
        {
            Span<byte> block = stackalloc byte[16];
            block.Clear();
            message[offset..].CopyTo(block);
            block[message.Length - offset] = 1; // Pad with 0x01

            ulong m_lo = BinaryPrimitives.ReadUInt64LittleEndian(block[0..8]);
            ulong m_hi = BinaryPrimitives.ReadUInt64LittleEndian(block[8..16]);

            h0 += m_lo & 0x3FFFFFF;
            h1 += (m_lo >> 26) & 0x3FFFFFF;
            h2 += ((m_lo >> 52) | (m_hi << 12)) & 0x3FFFFFF;
            h3 += (m_hi >> 14) & 0x3FFFFFF;
            h4 += m_hi >> 40;

            MultiplyReduce(ref h0, ref h1, ref h2, ref h3, ref h4,
                          r_0, r_1, r_2, r_3, r_4, s1, s2, s3, s4);
        }

        // Final reduction
        FinalReduce(ref h0, ref h1, ref h2, ref h3, ref h4);

        // Convert to 128-bit value
        ulong h_lo = h0 | (h1 << 26) | (h2 << 52);
        ulong h_hi = (h2 >> 12) | (h3 << 14) | (h4 << 40);

        // Add s
        ulong carry;
        h_lo += s_lo;
        carry = h_lo < s_lo ? 1UL : 0;
        h_hi += s_hi + carry;

        // Write tag
        BinaryPrimitives.WriteUInt64LittleEndian(tag[0..8], h_lo);
        BinaryPrimitives.WriteUInt64LittleEndian(tag[8..16], h_hi);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void MultiplyReduce(
        ref ulong h0, ref ulong h1, ref ulong h2, ref ulong h3, ref ulong h4,
        ulong r0, ulong r1, ulong r2, ulong r3, ulong r4,
        ulong s1, ulong s2, ulong s3, ulong s4)
    {
        // Optimized 130-bit multiply and reduce using 64-bit arithmetic
        // Uses the identity: x * 2^130 ≡ x * 5 (mod 2^130 - 5)
        // s1..s4 are precomputed as r1*5, r2*5, r3*5, r4*5

        // Full schoolbook multiplication with 64-bit products
        // Each product is at most 26+26=52 bits, fits in 64 bits
        ulong d0 = h0 * r0 + h1 * s4 + h2 * s3 + h3 * s2 + h4 * s1;
        ulong d1 = h0 * r1 + h1 * r0 + h2 * s4 + h3 * s3 + h4 * s2;
        ulong d2 = h0 * r2 + h1 * r1 + h2 * r0 + h3 * s4 + h4 * s3;
        ulong d3 = h0 * r3 + h1 * r2 + h2 * r1 + h3 * r0 + h4 * s4;
        ulong d4 = h0 * r4 + h1 * r3 + h2 * r2 + h3 * r1 + h4 * r0;

        // Carry propagation
        ulong c;
        c = d0 >> 26; d1 += c; d0 &= 0x3FFFFFF;
        c = d1 >> 26; d2 += c; d1 &= 0x3FFFFFF;
        c = d2 >> 26; d3 += c; d2 &= 0x3FFFFFF;
        c = d3 >> 26; d4 += c; d3 &= 0x3FFFFFF;
        c = d4 >> 26; d0 += c * 5; d4 &= 0x3FFFFFF;  // Reduction: 2^130 ≡ 5
        c = d0 >> 26; d1 += c; d0 &= 0x3FFFFFF;

        h0 = d0; h1 = d1; h2 = d2; h3 = d3; h4 = d4;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FinalReduce(ref ulong h0, ref ulong h1, ref ulong h2, ref ulong h3, ref ulong h4)
    {
        // Carry propagation
        ulong c;
        c = h0 >> 26; h0 &= 0x3FFFFFF; h1 += c;
        c = h1 >> 26; h1 &= 0x3FFFFFF; h2 += c;
        c = h2 >> 26; h2 &= 0x3FFFFFF; h3 += c;
        c = h3 >> 26; h3 &= 0x3FFFFFF; h4 += c;
        c = h4 >> 26; h4 &= 0x3FFFFFF; h0 += c * 5;
        c = h0 >> 26; h0 &= 0x3FFFFFF; h1 += c;

        // Compute h - p = h - (2^130 - 5) = h + 5 - 2^130
        ulong g0 = h0 + 5;
        c = g0 >> 26; g0 &= 0x3FFFFFF;
        ulong g1 = h1 + c; c = g1 >> 26; g1 &= 0x3FFFFFF;
        ulong g2 = h2 + c; c = g2 >> 26; g2 &= 0x3FFFFFF;
        ulong g3 = h3 + c; c = g3 >> 26; g3 &= 0x3FFFFFF;
        ulong g4 = h4 + c - (1UL << 26);

        // Select h if h < p, else g (h - p)
        // If g4 has high bit set (negative when viewed as signed), h < p, so use h
        // mask = 0xFFFF... if g4 negative (h < p, use h), 0 if g4 non-negative (h >= p, use g)
        ulong mask = (ulong)((long)g4 >> 63);
        h0 = (h0 & mask) | (g0 & ~mask);
        h1 = (h1 & mask) | (g1 & ~mask);
        h2 = (h2 & mask) | (g2 & ~mask);
        h3 = (h3 & mask) | (g3 & ~mask);
        h4 = (h4 & mask) | (g4 & ~mask);
    }

    /// <summary>
    /// Verifies a Poly1305 tag in constant time.
    /// </summary>
    public static bool Verify(ReadOnlySpan<byte> key, ReadOnlySpan<byte> message, ReadOnlySpan<byte> tag)
    {
        Span<byte> computed = stackalloc byte[16];
        ComputeTag(key, message, computed);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(computed, tag);
    }
}

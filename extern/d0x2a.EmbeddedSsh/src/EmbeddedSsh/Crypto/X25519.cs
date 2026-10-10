using System.Security.Cryptography;

namespace d0x2a.EmbeddedSsh.Crypto;

/// <summary>
/// X25519 Elliptic Curve Diffie-Hellman key exchange (RFC 7748).
/// Pure managed implementation using Montgomery ladder.
/// </summary>
public static class X25519
{
    public const int KeySize = 32;

    // X25519 base point (little-endian)
    private static readonly byte[] BasePoint = new byte[32]
    {
        9, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
    };

    /// <summary>
    /// Generates a new X25519 key pair.
    /// </summary>
    /// <returns>Tuple of (private key, public key).</returns>
    public static (byte[] PrivateKey, byte[] PublicKey) GenerateKeyPair()
    {
        var privateKey = RandomNumberGenerator.GetBytes(KeySize);
        ClampPrivateKey(privateKey);
        var publicKey = ScalarMult(privateKey, BasePoint);
        return (privateKey, publicKey);
    }

    /// <summary>
    /// Computes the public key from a private key.
    /// </summary>
    public static byte[] GetPublicKey(ReadOnlySpan<byte> privateKey)
    {
        if (privateKey.Length != KeySize)
            throw new ArgumentException("Private key must be 32 bytes", nameof(privateKey));

        Span<byte> clamped = stackalloc byte[KeySize];
        privateKey.CopyTo(clamped);
        ClampPrivateKey(clamped);
        return ScalarMult(clamped, BasePoint);
    }

    /// <summary>
    /// Computes the shared secret using X25519.
    /// </summary>
    /// <param name="privateKey">32-byte private key.</param>
    /// <param name="peerPublicKey">32-byte peer public key.</param>
    /// <returns>32-byte shared secret.</returns>
    public static byte[] ComputeSharedSecret(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> peerPublicKey)
    {
        if (privateKey.Length != KeySize)
            throw new ArgumentException("Private key must be 32 bytes", nameof(privateKey));
        if (peerPublicKey.Length != KeySize)
            throw new ArgumentException("Peer public key must be 32 bytes", nameof(peerPublicKey));

        Span<byte> clamped = stackalloc byte[KeySize];
        privateKey.CopyTo(clamped);
        ClampPrivateKey(clamped);

        return ScalarMult(clamped, peerPublicKey);
    }

    private static void ClampPrivateKey(Span<byte> key)
    {
        key[0] &= 248;
        key[31] &= 127;
        key[31] |= 64;
    }

    /// <summary>
    /// Scalar multiplication on Curve25519.
    /// Computes scalar * point using Montgomery ladder.
    /// </summary>
    private static byte[] ScalarMult(ReadOnlySpan<byte> scalar, ReadOnlySpan<byte> point)
    {
        // Decode u-coordinate from little-endian bytes (mask high bit per RFC 7748)
        Span<long> u = stackalloc long[10];
        Span<byte> maskedPoint = stackalloc byte[32];
        point.CopyTo(maskedPoint);
        maskedPoint[31] &= 0x7F; // Clear high bit
        FeFromBytes(u, maskedPoint);

        // Montgomery ladder state
        Span<long> x1 = stackalloc long[10];
        Span<long> x2 = stackalloc long[10];
        Span<long> z2 = stackalloc long[10];
        Span<long> x3 = stackalloc long[10];
        Span<long> z3 = stackalloc long[10];

        // Initialize: x1 = u, (x2:z2) = (1:0), (x3:z3) = (u:1)
        u.CopyTo(x1);
        Fe1(x2);           // x2 = 1
        Fe0(z2);           // z2 = 0
        u.CopyTo(x3);      // x3 = u
        Fe1(z3);           // z3 = 1

        // Temporaries for ladder step
        Span<long> a = stackalloc long[10];
        Span<long> b = stackalloc long[10];
        Span<long> c = stackalloc long[10];
        Span<long> d = stackalloc long[10];
        Span<long> e = stackalloc long[10];
        Span<long> aa = stackalloc long[10];
        Span<long> bb = stackalloc long[10];
        Span<long> da = stackalloc long[10];
        Span<long> cb = stackalloc long[10];

        int swap = 0;

        // Montgomery ladder - process bits from 254 down to 0
        for (int pos = 254; pos >= 0; pos--)
        {
            int bit = (scalar[pos >> 3] >> (pos & 7)) & 1;
            swap ^= bit;
            FeCSwap(x2, x3, swap);
            FeCSwap(z2, z3, swap);
            swap = bit;

            // Montgomery ladder step (RFC 7748 formulas)
            FeAdd(a, x2, z2);    // A = x_2 + z_2
            FeSub(b, x2, z2);    // B = x_2 - z_2
            FeAdd(c, x3, z3);    // C = x_3 + z_3
            FeSub(d, x3, z3);    // D = x_3 - z_3
            FeSquare(aa, a);     // AA = A^2
            FeSquare(bb, b);     // BB = B^2
            FeSub(e, aa, bb);    // E = AA - BB
            FeMul(da, d, a);     // DA = D * A
            FeMul(cb, c, b);     // CB = C * B
            FeAdd(x3, da, cb);   // x_3 = DA + CB
            FeSquare(x3, x3);    // x_3 = (DA + CB)^2
            FeSub(z3, da, cb);   // z_3 = DA - CB
            FeSquare(z3, z3);    // z_3 = (DA - CB)^2
            FeMul(z3, z3, x1);   // z_3 = x_1 * (DA - CB)^2
            FeMul(x2, aa, bb);   // x_2 = AA * BB
            FeMul121665(z2, e);  // z_2 = a24 * E (a24 = 121665)
            FeAdd(z2, z2, aa);   // z_2 = AA + a24 * E
            FeMul(z2, z2, e);    // z_2 = E * (AA + a24 * E)
        }

        FeCSwap(x2, x3, swap);
        FeCSwap(z2, z3, swap);

        // Compute result = x2 * z2^(-1)
        FeInvert(z2, z2);
        FeMul(x2, x2, z2);

        // Encode result
        var result = new byte[32];
        FeToBytes(result, x2);
        return result;
    }

    #region Field Element Operations (mod 2^255 - 19)

    // Field element: 10 limbs, each holding ~25.5 bits
    // Limbs 0,2,4,6,8 hold 26 bits; limbs 1,3,5,7,9 hold 25 bits

    private static void Fe0(Span<long> h)
    {
        h.Clear();
    }

    private static void Fe1(Span<long> h)
    {
        h.Clear();
        h[0] = 1;
    }

    private static void FeFromBytes(Span<long> h, ReadOnlySpan<byte> s)
    {
        // Load 32 bytes into 10 limbs (ref10 approach)
        long h0 = Load4(s);
        long h1 = Load3(s.Slice(4)) << 6;
        long h2 = Load3(s.Slice(7)) << 5;
        long h3 = Load3(s.Slice(10)) << 3;
        long h4 = Load3(s.Slice(13)) << 2;
        long h5 = Load4(s.Slice(16));
        long h6 = Load3(s.Slice(20)) << 7;
        long h7 = Load3(s.Slice(23)) << 5;
        long h8 = Load3(s.Slice(26)) << 4;
        long h9 = (Load3(s.Slice(29)) & 0x7FFFFF) << 2;

        // Carry chain to reduce to proper form
        long c;
        c = (h0 + (1L << 25)) >> 26; h1 += c; h0 -= c << 26;
        c = (h4 + (1L << 25)) >> 26; h5 += c; h4 -= c << 26;
        c = (h1 + (1L << 24)) >> 25; h2 += c; h1 -= c << 25;
        c = (h5 + (1L << 24)) >> 25; h6 += c; h5 -= c << 25;
        c = (h2 + (1L << 25)) >> 26; h3 += c; h2 -= c << 26;
        c = (h6 + (1L << 25)) >> 26; h7 += c; h6 -= c << 26;
        c = (h3 + (1L << 24)) >> 25; h4 += c; h3 -= c << 25;
        c = (h7 + (1L << 24)) >> 25; h8 += c; h7 -= c << 25;
        c = (h4 + (1L << 25)) >> 26; h5 += c; h4 -= c << 26;
        c = (h8 + (1L << 25)) >> 26; h9 += c; h8 -= c << 26;
        c = (h9 + (1L << 24)) >> 25; h0 += c * 19; h9 -= c << 25;
        c = (h0 + (1L << 25)) >> 26; h1 += c; h0 -= c << 26;

        h[0] = h0; h[1] = h1; h[2] = h2; h[3] = h3; h[4] = h4;
        h[5] = h5; h[6] = h6; h[7] = h7; h[8] = h8; h[9] = h9;
    }

    private static long Load3(ReadOnlySpan<byte> s)
    {
        return (long)s[0] | ((long)s[1] << 8) | ((long)s[2] << 16);
    }

    private static long Load4(ReadOnlySpan<byte> s)
    {
        return (long)s[0] | ((long)s[1] << 8) | ((long)s[2] << 16) | ((long)s[3] << 24);
    }

    private static void FeToBytes(Span<byte> s, ReadOnlySpan<long> h)
    {
        Span<long> t = stackalloc long[10];
        h.CopyTo(t);

        // Full reduction mod p = 2^255 - 19
        FeReduce(t);

        // Canonical reduction: ensure result is in [0, p)
        // First, compute t - p and check if it would be negative
        long q = (19 * t[9] + (1L << 24)) >> 25;
        q = (t[0] + q) >> 26;
        q = (t[1] + q) >> 25;
        q = (t[2] + q) >> 26;
        q = (t[3] + q) >> 25;
        q = (t[4] + q) >> 26;
        q = (t[5] + q) >> 25;
        q = (t[6] + q) >> 26;
        q = (t[7] + q) >> 25;
        q = (t[8] + q) >> 26;
        q = (t[9] + q) >> 25;

        // Now q is 0 or 1. If q=1, subtract p (add 19 and propagate carry)
        t[0] += 19 * q;

        long c;
        c = t[0] >> 26; t[1] += c; t[0] -= c << 26;
        c = t[1] >> 25; t[2] += c; t[1] -= c << 25;
        c = t[2] >> 26; t[3] += c; t[2] -= c << 26;
        c = t[3] >> 25; t[4] += c; t[3] -= c << 25;
        c = t[4] >> 26; t[5] += c; t[4] -= c << 26;
        c = t[5] >> 25; t[6] += c; t[5] -= c << 25;
        c = t[6] >> 26; t[7] += c; t[6] -= c << 26;
        c = t[7] >> 25; t[8] += c; t[7] -= c << 25;
        c = t[8] >> 26; t[9] += c; t[8] -= c << 26;
        t[9] &= (1L << 25) - 1; // Clear high bits

        // Canonical output (ref10 approach)
        s[0] = (byte)t[0];
        s[1] = (byte)(t[0] >> 8);
        s[2] = (byte)(t[0] >> 16);
        s[3] = (byte)((t[0] >> 24) | (t[1] << 2));
        s[4] = (byte)(t[1] >> 6);
        s[5] = (byte)(t[1] >> 14);
        s[6] = (byte)((t[1] >> 22) | (t[2] << 3));
        s[7] = (byte)(t[2] >> 5);
        s[8] = (byte)(t[2] >> 13);
        s[9] = (byte)((t[2] >> 21) | (t[3] << 5));
        s[10] = (byte)(t[3] >> 3);
        s[11] = (byte)(t[3] >> 11);
        s[12] = (byte)((t[3] >> 19) | (t[4] << 6));
        s[13] = (byte)(t[4] >> 2);
        s[14] = (byte)(t[4] >> 10);
        s[15] = (byte)(t[4] >> 18);
        s[16] = (byte)t[5];
        s[17] = (byte)(t[5] >> 8);
        s[18] = (byte)(t[5] >> 16);
        s[19] = (byte)((t[5] >> 24) | (t[6] << 1));
        s[20] = (byte)(t[6] >> 7);
        s[21] = (byte)(t[6] >> 15);
        s[22] = (byte)((t[6] >> 23) | (t[7] << 3));
        s[23] = (byte)(t[7] >> 5);
        s[24] = (byte)(t[7] >> 13);
        s[25] = (byte)((t[7] >> 21) | (t[8] << 4));
        s[26] = (byte)(t[8] >> 4);
        s[27] = (byte)(t[8] >> 12);
        s[28] = (byte)((t[8] >> 20) | (t[9] << 6));
        s[29] = (byte)(t[9] >> 2);
        s[30] = (byte)(t[9] >> 10);
        s[31] = (byte)(t[9] >> 18);
    }

    private static void FeReduce(Span<long> h)
    {
        // First round of carry propagation
        long c;
        c = (h[0] + (1L << 25)) >> 26; h[1] += c; h[0] -= c << 26;
        c = (h[4] + (1L << 25)) >> 26; h[5] += c; h[4] -= c << 26;
        c = (h[1] + (1L << 24)) >> 25; h[2] += c; h[1] -= c << 25;
        c = (h[5] + (1L << 24)) >> 25; h[6] += c; h[5] -= c << 25;
        c = (h[2] + (1L << 25)) >> 26; h[3] += c; h[2] -= c << 26;
        c = (h[6] + (1L << 25)) >> 26; h[7] += c; h[6] -= c << 26;
        c = (h[3] + (1L << 24)) >> 25; h[4] += c; h[3] -= c << 25;
        c = (h[7] + (1L << 24)) >> 25; h[8] += c; h[7] -= c << 25;
        c = (h[4] + (1L << 25)) >> 26; h[5] += c; h[4] -= c << 26;
        c = (h[8] + (1L << 25)) >> 26; h[9] += c; h[8] -= c << 26;
        c = (h[9] + (1L << 24)) >> 25; h[0] += c * 19; h[9] -= c << 25;
        c = (h[0] + (1L << 25)) >> 26; h[1] += c; h[0] -= c << 26;

        // Second round
        c = (h[0] + (1L << 25)) >> 26; h[1] += c; h[0] -= c << 26;
        c = (h[1] + (1L << 24)) >> 25; h[2] += c; h[1] -= c << 25;
        c = (h[2] + (1L << 25)) >> 26; h[3] += c; h[2] -= c << 26;
        c = (h[3] + (1L << 24)) >> 25; h[4] += c; h[3] -= c << 25;
        c = (h[4] + (1L << 25)) >> 26; h[5] += c; h[4] -= c << 26;
        c = (h[5] + (1L << 24)) >> 25; h[6] += c; h[5] -= c << 25;
        c = (h[6] + (1L << 25)) >> 26; h[7] += c; h[6] -= c << 26;
        c = (h[7] + (1L << 24)) >> 25; h[8] += c; h[7] -= c << 25;
        c = (h[8] + (1L << 25)) >> 26; h[9] += c; h[8] -= c << 26;
        c = (h[9] + (1L << 24)) >> 25; h[0] += c * 19; h[9] -= c << 25;

        // Final carry
        c = (h[0] + (1L << 25)) >> 26; h[1] += c; h[0] -= c << 26;
    }

    private static void FeAdd(Span<long> h, ReadOnlySpan<long> f, ReadOnlySpan<long> g)
    {
        for (int i = 0; i < 10; i++)
            h[i] = f[i] + g[i];
    }

    private static void FeSub(Span<long> h, ReadOnlySpan<long> f, ReadOnlySpan<long> g)
    {
        for (int i = 0; i < 10; i++)
            h[i] = f[i] - g[i];
    }

    private static void FeMul(Span<long> h, ReadOnlySpan<long> f, ReadOnlySpan<long> g)
    {
        long f0 = f[0], f1 = f[1], f2 = f[2], f3 = f[3], f4 = f[4];
        long f5 = f[5], f6 = f[6], f7 = f[7], f8 = f[8], f9 = f[9];
        long g0 = g[0], g1 = g[1], g2 = g[2], g3 = g[3], g4 = g[4];
        long g5 = g[5], g6 = g[6], g7 = g[7], g8 = g[8], g9 = g[9];

        long g1_19 = 19 * g1, g2_19 = 19 * g2, g3_19 = 19 * g3, g4_19 = 19 * g4;
        long g5_19 = 19 * g5, g6_19 = 19 * g6, g7_19 = 19 * g7, g8_19 = 19 * g8, g9_19 = 19 * g9;

        long f1_2 = 2 * f1, f3_2 = 2 * f3, f5_2 = 2 * f5, f7_2 = 2 * f7, f9_2 = 2 * f9;

        long h0 = f0 * g0 + f1_2 * g9_19 + f2 * g8_19 + f3_2 * g7_19 + f4 * g6_19 + f5_2 * g5_19 + f6 * g4_19 + f7_2 * g3_19 + f8 * g2_19 + f9_2 * g1_19;
        long h1 = f0 * g1 + f1 * g0 + f2 * g9_19 + f3 * g8_19 + f4 * g7_19 + f5 * g6_19 + f6 * g5_19 + f7 * g4_19 + f8 * g3_19 + f9 * g2_19;
        long h2 = f0 * g2 + f1_2 * g1 + f2 * g0 + f3_2 * g9_19 + f4 * g8_19 + f5_2 * g7_19 + f6 * g6_19 + f7_2 * g5_19 + f8 * g4_19 + f9_2 * g3_19;
        long h3 = f0 * g3 + f1 * g2 + f2 * g1 + f3 * g0 + f4 * g9_19 + f5 * g8_19 + f6 * g7_19 + f7 * g6_19 + f8 * g5_19 + f9 * g4_19;
        long h4 = f0 * g4 + f1_2 * g3 + f2 * g2 + f3_2 * g1 + f4 * g0 + f5_2 * g9_19 + f6 * g8_19 + f7_2 * g7_19 + f8 * g6_19 + f9_2 * g5_19;
        long h5 = f0 * g5 + f1 * g4 + f2 * g3 + f3 * g2 + f4 * g1 + f5 * g0 + f6 * g9_19 + f7 * g8_19 + f8 * g7_19 + f9 * g6_19;
        long h6 = f0 * g6 + f1_2 * g5 + f2 * g4 + f3_2 * g3 + f4 * g2 + f5_2 * g1 + f6 * g0 + f7_2 * g9_19 + f8 * g8_19 + f9_2 * g7_19;
        long h7 = f0 * g7 + f1 * g6 + f2 * g5 + f3 * g4 + f4 * g3 + f5 * g2 + f6 * g1 + f7 * g0 + f8 * g9_19 + f9 * g8_19;
        long h8 = f0 * g8 + f1_2 * g7 + f2 * g6 + f3_2 * g5 + f4 * g4 + f5_2 * g3 + f6 * g2 + f7_2 * g1 + f8 * g0 + f9_2 * g9_19;
        long h9 = f0 * g9 + f1 * g8 + f2 * g7 + f3 * g6 + f4 * g5 + f5 * g4 + f6 * g3 + f7 * g2 + f8 * g1 + f9 * g0;

        // Carry chain
        long c;
        c = (h0 + (1L << 25)) >> 26; h1 += c; h0 -= c << 26;
        c = (h4 + (1L << 25)) >> 26; h5 += c; h4 -= c << 26;
        c = (h1 + (1L << 24)) >> 25; h2 += c; h1 -= c << 25;
        c = (h5 + (1L << 24)) >> 25; h6 += c; h5 -= c << 25;
        c = (h2 + (1L << 25)) >> 26; h3 += c; h2 -= c << 26;
        c = (h6 + (1L << 25)) >> 26; h7 += c; h6 -= c << 26;
        c = (h3 + (1L << 24)) >> 25; h4 += c; h3 -= c << 25;
        c = (h7 + (1L << 24)) >> 25; h8 += c; h7 -= c << 25;
        c = (h4 + (1L << 25)) >> 26; h5 += c; h4 -= c << 26;
        c = (h8 + (1L << 25)) >> 26; h9 += c; h8 -= c << 26;
        c = (h9 + (1L << 24)) >> 25; h0 += c * 19; h9 -= c << 25;
        c = (h0 + (1L << 25)) >> 26; h1 += c; h0 -= c << 26;

        h[0] = h0; h[1] = h1; h[2] = h2; h[3] = h3; h[4] = h4;
        h[5] = h5; h[6] = h6; h[7] = h7; h[8] = h8; h[9] = h9;
    }

    private static void FeSquare(Span<long> h, ReadOnlySpan<long> f)
    {
        long f0 = f[0], f1 = f[1], f2 = f[2], f3 = f[3], f4 = f[4];
        long f5 = f[5], f6 = f[6], f7 = f[7], f8 = f[8], f9 = f[9];

        long f0_2 = 2 * f0, f1_2 = 2 * f1, f2_2 = 2 * f2, f3_2 = 2 * f3, f4_2 = 2 * f4;
        long f5_2 = 2 * f5, f6_2 = 2 * f6, f7_2 = 2 * f7;
        long f5_38 = 38 * f5, f6_19 = 19 * f6, f7_38 = 38 * f7, f8_19 = 19 * f8, f9_38 = 38 * f9;

        long h0 = f0 * f0 + f1_2 * f9_38 + f2_2 * f8_19 + f3_2 * f7_38 + f4_2 * f6_19 + f5 * f5_38;
        long h1 = f0_2 * f1 + f2 * f9_38 + f3_2 * f8_19 + f4 * f7_38 + f5_2 * f6_19;
        long h2 = f0_2 * f2 + f1_2 * f1 + f3_2 * f9_38 + f4_2 * f8_19 + f5_2 * f7_38 + f6 * f6_19;
        long h3 = f0_2 * f3 + f1_2 * f2 + f4 * f9_38 + f5_2 * f8_19 + f6 * f7_38;
        long h4 = f0_2 * f4 + f1_2 * f3_2 + f2 * f2 + f5_2 * f9_38 + f6_2 * f8_19 + f7 * f7_38;
        long h5 = f0_2 * f5 + f1_2 * f4 + f2_2 * f3 + f6 * f9_38 + f7_2 * f8_19;
        long h6 = f0_2 * f6 + f1_2 * f5_2 + f2_2 * f4 + f3_2 * f3 + f7_2 * f9_38 + f8 * f8_19;
        long h7 = f0_2 * f7 + f1_2 * f6 + f2_2 * f5 + f3_2 * f4 + f8 * f9_38;
        long h8 = f0_2 * f8 + f1_2 * f7_2 + f2_2 * f6 + f3_2 * f5_2 + f4 * f4 + f9 * f9_38;
        long h9 = f0_2 * f9 + f1_2 * f8 + f2_2 * f7 + f3_2 * f6 + f4_2 * f5;

        // Carry chain
        long c;
        c = (h0 + (1L << 25)) >> 26; h1 += c; h0 -= c << 26;
        c = (h4 + (1L << 25)) >> 26; h5 += c; h4 -= c << 26;
        c = (h1 + (1L << 24)) >> 25; h2 += c; h1 -= c << 25;
        c = (h5 + (1L << 24)) >> 25; h6 += c; h5 -= c << 25;
        c = (h2 + (1L << 25)) >> 26; h3 += c; h2 -= c << 26;
        c = (h6 + (1L << 25)) >> 26; h7 += c; h6 -= c << 26;
        c = (h3 + (1L << 24)) >> 25; h4 += c; h3 -= c << 25;
        c = (h7 + (1L << 24)) >> 25; h8 += c; h7 -= c << 25;
        c = (h4 + (1L << 25)) >> 26; h5 += c; h4 -= c << 26;
        c = (h8 + (1L << 25)) >> 26; h9 += c; h8 -= c << 26;
        c = (h9 + (1L << 24)) >> 25; h0 += c * 19; h9 -= c << 25;
        c = (h0 + (1L << 25)) >> 26; h1 += c; h0 -= c << 26;

        h[0] = h0; h[1] = h1; h[2] = h2; h[3] = h3; h[4] = h4;
        h[5] = h5; h[6] = h6; h[7] = h7; h[8] = h8; h[9] = h9;
    }

    private static void FeMul121665(Span<long> h, ReadOnlySpan<long> f)
    {
        // Multiply by a24 = (A-2)/4 = 121665 for curve25519 (RFC 7748)
        long h0 = f[0] * 121665;
        long h1 = f[1] * 121665;
        long h2 = f[2] * 121665;
        long h3 = f[3] * 121665;
        long h4 = f[4] * 121665;
        long h5 = f[5] * 121665;
        long h6 = f[6] * 121665;
        long h7 = f[7] * 121665;
        long h8 = f[8] * 121665;
        long h9 = f[9] * 121665;

        // Carry chain
        long c;
        c = (h9 + (1L << 24)) >> 25; h0 += c * 19; h9 -= c << 25;
        c = (h0 + (1L << 25)) >> 26; h1 += c; h0 -= c << 26;
        c = (h1 + (1L << 24)) >> 25; h2 += c; h1 -= c << 25;
        c = (h2 + (1L << 25)) >> 26; h3 += c; h2 -= c << 26;
        c = (h3 + (1L << 24)) >> 25; h4 += c; h3 -= c << 25;
        c = (h4 + (1L << 25)) >> 26; h5 += c; h4 -= c << 26;
        c = (h5 + (1L << 24)) >> 25; h6 += c; h5 -= c << 25;
        c = (h6 + (1L << 25)) >> 26; h7 += c; h6 -= c << 26;
        c = (h7 + (1L << 24)) >> 25; h8 += c; h7 -= c << 25;
        c = (h8 + (1L << 25)) >> 26; h9 += c; h8 -= c << 26;

        h[0] = h0; h[1] = h1; h[2] = h2; h[3] = h3; h[4] = h4;
        h[5] = h5; h[6] = h6; h[7] = h7; h[8] = h8; h[9] = h9;
    }

    private static void FeInvert(Span<long> o, ReadOnlySpan<long> z)
    {
        // Compute z^(p-2) = z^(2^255 - 21) using addition chain
        Span<long> t0 = stackalloc long[10];
        Span<long> t1 = stackalloc long[10];
        Span<long> t2 = stackalloc long[10];
        Span<long> t3 = stackalloc long[10];

        FeSquare(t0, z);          // t0 = z^2
        FeSquare(t1, t0);         // t1 = z^4
        FeSquare(t1, t1);         // t1 = z^8
        FeMul(t1, z, t1);         // t1 = z^9
        FeMul(t0, t0, t1);        // t0 = z^11
        FeSquare(t2, t0);         // t2 = z^22
        FeMul(t1, t1, t2);        // t1 = z^(2^5-1) = z^31

        FeSquare(t2, t1);
        for (int i = 1; i < 5; i++) FeSquare(t2, t2);
        FeMul(t1, t2, t1);        // t1 = z^(2^10-1)

        FeSquare(t2, t1);
        for (int i = 1; i < 10; i++) FeSquare(t2, t2);
        FeMul(t2, t2, t1);        // t2 = z^(2^20-1)

        FeSquare(t3, t2);
        for (int i = 1; i < 20; i++) FeSquare(t3, t3);
        FeMul(t2, t3, t2);        // t2 = z^(2^40-1)

        FeSquare(t2, t2);
        for (int i = 1; i < 10; i++) FeSquare(t2, t2);
        FeMul(t1, t2, t1);        // t1 = z^(2^50-1)

        FeSquare(t2, t1);
        for (int i = 1; i < 50; i++) FeSquare(t2, t2);
        FeMul(t2, t2, t1);        // t2 = z^(2^100-1)

        FeSquare(t3, t2);
        for (int i = 1; i < 100; i++) FeSquare(t3, t3);
        FeMul(t2, t3, t2);        // t2 = z^(2^200-1)

        FeSquare(t2, t2);
        for (int i = 1; i < 50; i++) FeSquare(t2, t2);
        FeMul(t1, t2, t1);        // t1 = z^(2^250-1)

        FeSquare(t1, t1);
        for (int i = 1; i < 5; i++) FeSquare(t1, t1);
        FeMul(o, t1, t0);         // o = z^(2^255-21)
    }

    private static void FeCSwap(Span<long> a, Span<long> b, int swap)
    {
        long mask = -(long)swap;
        for (int i = 0; i < 10; i++)
        {
            long t = mask & (a[i] ^ b[i]);
            a[i] ^= t;
            b[i] ^= t;
        }
    }

    #endregion
}

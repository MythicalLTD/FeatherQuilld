using System.Security.Cryptography;

namespace d0x2a.EmbeddedSsh.Crypto;

/// <summary>
/// Ed25519 digital signature algorithm (RFC 8032).
/// </summary>
public static class Ed25519
{
    public const int PublicKeySize = 32;
    public const int PrivateKeySize = 32;
    public const int SignatureSize = 64;
    public const int ExpandedKeySize = 64;

    /// <summary>
    /// Generates a new Ed25519 key pair.
    /// </summary>
    public static (byte[] PrivateKey, byte[] PublicKey) GenerateKeyPair()
    {
        var privateKey = RandomNumberGenerator.GetBytes(PrivateKeySize);
        var publicKey = GetPublicKey(privateKey);
        return (privateKey, publicKey);
    }

    /// <summary>
    /// Derives the public key from a private key seed.
    /// </summary>
    public static byte[] GetPublicKey(ReadOnlySpan<byte> privateKey)
    {
        if (privateKey.Length != PrivateKeySize)
            throw new ArgumentException("Private key must be 32 bytes", nameof(privateKey));

        // Hash the private key
        Span<byte> h = stackalloc byte[64];
        SHA512.HashData(privateKey, h);

        // Clamp
        h[0] &= 248;
        h[31] &= 127;
        h[31] |= 64;

        // Scalar multiply with base point
        var point = ScalarMultBase(h[..32]);

        var publicKey = new byte[32];
        PointEncode(point, publicKey);
        return publicKey;
    }

    /// <summary>
    /// Signs a message using Ed25519.
    /// </summary>
    public static byte[] Sign(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> message)
    {
        if (privateKey.Length != PrivateKeySize)
            throw new ArgumentException("Private key must be 32 bytes", nameof(privateKey));

        // Hash private key
        Span<byte> h = stackalloc byte[64];
        SHA512.HashData(privateKey, h);

        // Clamp scalar
        Span<byte> s = stackalloc byte[32];
        h[..32].CopyTo(s);
        s[0] &= 248;
        s[31] &= 127;
        s[31] |= 64;

        // Compute public key
        var A = ScalarMultBase(s);
        Span<byte> publicKey = stackalloc byte[32];
        PointEncode(A, publicKey);

        // r = H(prefix || M) mod L
        Span<byte> rHash = stackalloc byte[64];
        using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA512))
        {
            sha.AppendData(h[32..64]);
            sha.AppendData(message);
            sha.GetHashAndReset(rHash);
        }
        ScalarReduce(rHash);

        // R = r * B
        var R = ScalarMultBase(rHash[..32]);
        Span<byte> encodedR = stackalloc byte[32];
        PointEncode(R, encodedR);

        // k = H(R || A || M) mod L
        Span<byte> kHash = stackalloc byte[64];
        using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA512))
        {
            sha.AppendData(encodedR);
            sha.AppendData(publicKey);
            sha.AppendData(message);
            sha.GetHashAndReset(kHash);
        }
        ScalarReduce(kHash);

        // S = (r + k * s) mod L
        Span<byte> S = stackalloc byte[32];
        ScalarMulAdd(S, kHash[..32], s, rHash[..32]);

        // Signature = R || S
        var signature = new byte[64];
        encodedR.CopyTo(signature);
        S.CopyTo(signature.AsSpan(32));

        return signature;
    }

    /// <summary>
    /// Verifies an Ed25519 signature.
    /// </summary>
    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != PublicKeySize)
            return false;
        if (signature.Length != SignatureSize)
            return false;

        // Decode R
        if (!PointDecode(signature[..32], out var R))
            return false;

        // Decode A (public key)
        if (!PointDecode(publicKey, out var A))
            return false;

        // Check S < L
        var S = signature[32..64];
        if (!IsCanonicalScalar(S))
            return false;

        // k = H(R || A || M) mod L
        Span<byte> kHash = stackalloc byte[64];
        using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA512))
        {
            sha.AppendData(signature[..32]);
            sha.AppendData(publicKey);
            sha.AppendData(message);
            sha.GetHashAndReset(kHash);
        }
        ScalarReduce(kHash);

        // Check: S * B = R + k * A
        var sB = ScalarMultBase(S);
        var kA = ScalarMult(kHash[..32], A);
        var RkA = PointAdd(R, kA);

        // Compare points
        Span<byte> check1 = stackalloc byte[32];
        Span<byte> check2 = stackalloc byte[32];
        PointEncode(sB, check1);
        PointEncode(RkA, check2);

        return CryptographicOperations.FixedTimeEquals(check1, check2);
    }

    #region Edwards Curve Point Operations

    // Extended coordinates (X, Y, Z, T) where x = X/Z, y = Y/Z, x*y = T/Z
    private struct ExtendedPoint
    {
        public long[] X, Y, Z, T;

        public ExtendedPoint()
        {
            X = new long[10];
            Y = new long[10];
            Z = new long[10];
            T = new long[10];
        }

        public static ExtendedPoint Neutral()
        {
            var p = new ExtendedPoint();
            p.Y[0] = 1;
            p.Z[0] = 1;
            return p;
        }
    }

    // Base point B encoded bytes (RFC 8032 generator point)
    // y = 4/5 mod p, x is the positive root
    private static readonly byte[] BasePointBytes = [
        0x58, 0x66, 0x66, 0x66, 0x66, 0x66, 0x66, 0x66,
        0x66, 0x66, 0x66, 0x66, 0x66, 0x66, 0x66, 0x66,
        0x66, 0x66, 0x66, 0x66, 0x66, 0x66, 0x66, 0x66,
        0x66, 0x66, 0x66, 0x66, 0x66, 0x66, 0x66, 0x66
    ];

    private static ExtendedPoint? _basePointCache;
    private static ExtendedPoint[]? _basePointTable;  // [2^i]B for i = 0..255

    private static ExtendedPoint GetBasePoint()
    {
        if (_basePointCache.HasValue)
            return _basePointCache.Value;

        if (!PointDecode(BasePointBytes, out var B))
            throw new InvalidOperationException("Failed to decode base point");

        _basePointCache = B;
        return B;
    }

    /// <summary>
    /// Gets or computes the precomputed table of [2^i]B for i = 0..255.
    /// This allows ScalarMultBase to use only additions, no doublings.
    /// </summary>
    private static ExtendedPoint[] GetBasePointTable()
    {
        if (_basePointTable != null)
            return _basePointTable;

        var table = new ExtendedPoint[256];
        var B = GetBasePoint();
        table[0] = B;

        // Compute [2^i]B by repeated doubling
        for (int i = 1; i < 256; i++)
        {
            table[i] = PointDouble(table[i - 1]);
        }

        _basePointTable = table;
        return table;
    }

    private static ExtendedPoint ScalarMultBase(ReadOnlySpan<byte> scalar)
    {
        var table = GetBasePointTable();
        var result = ExtendedPoint.Neutral();

        // For each bit i, if scalar bit i is set, add [2^i]B
        for (int i = 0; i < 256; i++)
        {
            int bit = (scalar[i >> 3] >> (i & 7)) & 1;
            if (bit == 1)
                result = PointAdd(result, table[i]);
        }

        return result;
    }

    private static ExtendedPoint ScalarMult(ReadOnlySpan<byte> scalar, ExtendedPoint point)
    {
        var result = ExtendedPoint.Neutral();

        for (int i = 255; i >= 0; i--)
        {
            result = PointDouble(result);
            int bit = (scalar[i >> 3] >> (i & 7)) & 1;
            if (bit == 1)
                result = PointAdd(result, point);
        }

        return result;
    }

    private static ExtendedPoint PointAdd(ExtendedPoint p, ExtendedPoint q)
    {
        var r = new ExtendedPoint();

        Span<long> a = stackalloc long[10];
        Span<long> b = stackalloc long[10];
        Span<long> c = stackalloc long[10];
        Span<long> d = stackalloc long[10];
        Span<long> e = stackalloc long[10];
        Span<long> f = stackalloc long[10];
        Span<long> g = stackalloc long[10];
        Span<long> h = stackalloc long[10];
        Span<long> t = stackalloc long[10];

        // A = (Y1-X1)*(Y2-X2)
        FieldSub(a, p.Y, p.X);
        FieldSub(t, q.Y, q.X);
        FieldMul(a, a, t);

        // B = (Y1+X1)*(Y2+X2)
        FieldAdd(b, p.Y, p.X);
        FieldAdd(t, q.Y, q.X);
        FieldMul(b, b, t);

        // C = T1*2*d*T2
        FieldMul(c, p.T, q.T);
        FieldMul(c, c, D2);

        // D = Z1*2*Z2
        FieldMul(d, p.Z, q.Z);
        FieldAdd(d, d, d);

        // E = B-A, F = D-C, G = D+C, H = B+A
        FieldSub(e, b, a);
        FieldSub(f, d, c);
        FieldAdd(g, d, c);
        FieldAdd(h, b, a);

        // X3 = E*F, Y3 = G*H, T3 = E*H, Z3 = F*G
        FieldMul(r.X, e, f);
        FieldMul(r.Y, g, h);
        FieldMul(r.T, e, h);
        FieldMul(r.Z, f, g);

        return r;
    }

    private static ExtendedPoint PointDouble(ExtendedPoint p)
    {
        var r = new ExtendedPoint();

        Span<long> a = stackalloc long[10];
        Span<long> b = stackalloc long[10];
        Span<long> c = stackalloc long[10];
        Span<long> d = stackalloc long[10];
        Span<long> e = stackalloc long[10];
        Span<long> f = stackalloc long[10];
        Span<long> g = stackalloc long[10];
        Span<long> h = stackalloc long[10];
        Span<long> t = stackalloc long[10];

        // A = X1^2
        FieldSquare(a, p.X);
        // B = Y1^2
        FieldSquare(b, p.Y);
        // C = 2*Z1^2
        FieldSquare(c, p.Z);
        FieldAdd(c, c, c);
        // D = -A (for twisted curve a=-1)
        FieldNeg(d, a);
        // E = (X1+Y1)^2 - A - B
        FieldAdd(e, p.X, p.Y);
        FieldSquare(e, e);
        FieldSub(e, e, a);
        FieldSub(e, e, b);
        // G = D+B
        FieldAdd(g, d, b);
        // F = G-C
        FieldSub(f, g, c);
        // H = D-B
        FieldSub(h, d, b);
        // X3 = E*F
        FieldMul(r.X, e, f);
        // Y3 = G*H
        FieldMul(r.Y, g, h);
        // T3 = E*H
        FieldMul(r.T, e, h);
        // Z3 = F*G
        FieldMul(r.Z, f, g);

        return r;
    }

    private static void PointEncode(ExtendedPoint p, Span<byte> output)
    {
        Span<long> recip = stackalloc long[10];
        Span<long> x = stackalloc long[10];
        Span<long> y = stackalloc long[10];

        FieldInvert(recip, p.Z);
        FieldMul(x, p.X, recip);
        FieldMul(y, p.Y, recip);

        FieldToBytes(output, y);
        output[31] ^= (byte)(IsNegative(x) << 7);
    }

    private static bool PointDecode(ReadOnlySpan<byte> input, out ExtendedPoint point)
    {
        point = new ExtendedPoint();

        // Extract sign
        int sign = input[31] >> 7;

        // Decode y
        Span<byte> yCopy = stackalloc byte[32];
        input.CopyTo(yCopy);
        yCopy[31] &= 0x7F;

        Span<long> y = stackalloc long[10];
        FieldFromBytes(y, yCopy);

        // Compute x^2 = (y^2 - 1) / (d*y^2 + 1)
        Span<long> y2 = stackalloc long[10];
        Span<long> u = stackalloc long[10];
        Span<long> v = stackalloc long[10];
        Span<long> x = stackalloc long[10];

        FieldSquare(y2, y);
        FieldSub(u, y2, One);           // y^2 - 1
        FieldMul(v, y2, D);
        FieldAdd(v, v, One);            // d*y^2 + 1

        // x = sqrt(u/v)
        if (!FieldSqrtRatio(x, u, v))
            return false;

        // Adjust sign
        if (IsNegative(x) != sign)
            FieldNeg(x, x);

        // Set point
        x.CopyTo(point.X.AsSpan());
        y.CopyTo(point.Y.AsSpan());
        point.Z[0] = 1;
        FieldMul(point.T, x, y);

        return true;
    }

    #endregion

    #region Field Arithmetic (mod 2^255 - 19)

    private static readonly long[] One = [1, 0, 0, 0, 0, 0, 0, 0, 0, 0];
    private static readonly long[] D = [
        -10913610, 13857413, -15372611, 6949391, 114729,
        -8787816, -6275908, -3247719, -18696448, -12055116
    ];
    private static readonly long[] D2 = [
        -21827239, -5839606, -30745221, 13898782, 229458,
        15978800, -12551817, -6495438, 29715968, 9444199
    ];
    private static readonly long[] SqrtM1 = [
        -32595792, -7943725, 9377950, 3500415, 12389472,
        -272473, -25146209, -2005654, 326686, 11406482
    ];

    private static void FieldFromBytes(Span<long> h, ReadOnlySpan<byte> s)
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

    private static void FieldToBytes(Span<byte> s, ReadOnlySpan<long> h)
    {
        // Follow ref10's fe_tobytes exactly
        long h0 = h[0], h1 = h[1], h2 = h[2], h3 = h[3], h4 = h[4];
        long h5 = h[5], h6 = h[6], h7 = h[7], h8 = h[8], h9 = h[9];

        // First carry chain to get values closer to canonical form
        long c;
        c = (h0 + (1L << 25)) >> 26; h1 += c; h0 -= c << 26;
        c = (h1 + (1L << 24)) >> 25; h2 += c; h1 -= c << 25;
        c = (h2 + (1L << 25)) >> 26; h3 += c; h2 -= c << 26;
        c = (h3 + (1L << 24)) >> 25; h4 += c; h3 -= c << 25;
        c = (h4 + (1L << 25)) >> 26; h5 += c; h4 -= c << 26;
        c = (h5 + (1L << 24)) >> 25; h6 += c; h5 -= c << 25;
        c = (h6 + (1L << 25)) >> 26; h7 += c; h6 -= c << 26;
        c = (h7 + (1L << 24)) >> 25; h8 += c; h7 -= c << 25;
        c = (h8 + (1L << 25)) >> 26; h9 += c; h8 -= c << 26;
        c = (h9 + (1L << 24)) >> 25; h0 += c * 19; h9 -= c << 25;
        c = (h0 + (1L << 25)) >> 26; h1 += c; h0 -= c << 26;

        // Compute q for canonical reduction
        long q = (19 * h9 + (1L << 24)) >> 25;
        q = (h0 + q) >> 26;
        q = (h1 + q) >> 25;
        q = (h2 + q) >> 26;
        q = (h3 + q) >> 25;
        q = (h4 + q) >> 26;
        q = (h5 + q) >> 25;
        q = (h6 + q) >> 26;
        q = (h7 + q) >> 25;
        q = (h8 + q) >> 26;
        q = (h9 + q) >> 25;

        // Subtract q*p (equivalently, add q*19)
        h0 += 19 * q;

        // Final carry chain
        c = h0 >> 26; h1 += c; h0 -= c << 26;
        c = h1 >> 25; h2 += c; h1 -= c << 25;
        c = h2 >> 26; h3 += c; h2 -= c << 26;
        c = h3 >> 25; h4 += c; h3 -= c << 25;
        c = h4 >> 26; h5 += c; h4 -= c << 26;
        c = h5 >> 25; h6 += c; h5 -= c << 25;
        c = h6 >> 26; h7 += c; h6 -= c << 26;
        c = h7 >> 25; h8 += c; h7 -= c << 25;
        c = h8 >> 26; h9 += c; h8 -= c << 26;
        h9 &= (1L << 25) - 1;

        s[0] = (byte)h0;
        s[1] = (byte)(h0 >> 8);
        s[2] = (byte)(h0 >> 16);
        s[3] = (byte)((h0 >> 24) | (h1 << 2));
        s[4] = (byte)(h1 >> 6);
        s[5] = (byte)(h1 >> 14);
        s[6] = (byte)((h1 >> 22) | (h2 << 3));
        s[7] = (byte)(h2 >> 5);
        s[8] = (byte)(h2 >> 13);
        s[9] = (byte)((h2 >> 21) | (h3 << 5));
        s[10] = (byte)(h3 >> 3);
        s[11] = (byte)(h3 >> 11);
        s[12] = (byte)((h3 >> 19) | (h4 << 6));
        s[13] = (byte)(h4 >> 2);
        s[14] = (byte)(h4 >> 10);
        s[15] = (byte)(h4 >> 18);
        s[16] = (byte)h5;
        s[17] = (byte)(h5 >> 8);
        s[18] = (byte)(h5 >> 16);
        s[19] = (byte)((h5 >> 24) | (h6 << 1));
        s[20] = (byte)(h6 >> 7);
        s[21] = (byte)(h6 >> 15);
        s[22] = (byte)((h6 >> 23) | (h7 << 3));
        s[23] = (byte)(h7 >> 5);
        s[24] = (byte)(h7 >> 13);
        s[25] = (byte)((h7 >> 21) | (h8 << 4));
        s[26] = (byte)(h8 >> 4);
        s[27] = (byte)(h8 >> 12);
        s[28] = (byte)((h8 >> 20) | (h9 << 6));
        s[29] = (byte)(h9 >> 2);
        s[30] = (byte)(h9 >> 10);
        s[31] = (byte)(h9 >> 18);
    }

    private static void FieldReduce(Span<long> h)
    {
        // First round with interleaved carries (same as ref10)
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

    private static void FieldAdd(Span<long> h, ReadOnlySpan<long> f, ReadOnlySpan<long> g)
    {
        for (int i = 0; i < 10; i++) h[i] = f[i] + g[i];
    }

    private static void FieldSub(Span<long> h, ReadOnlySpan<long> f, ReadOnlySpan<long> g)
    {
        for (int i = 0; i < 10; i++) h[i] = f[i] - g[i];
    }

    private static void FieldNeg(Span<long> h, ReadOnlySpan<long> f)
    {
        for (int i = 0; i < 10; i++) h[i] = -f[i];
    }

    private static void FieldMul(Span<long> h, ReadOnlySpan<long> f, ReadOnlySpan<long> g)
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

        long carry;
        carry = (h0 + (1L << 25)) >> 26; h1 += carry; h0 -= carry << 26;
        carry = (h4 + (1L << 25)) >> 26; h5 += carry; h4 -= carry << 26;
        carry = (h1 + (1L << 24)) >> 25; h2 += carry; h1 -= carry << 25;
        carry = (h5 + (1L << 24)) >> 25; h6 += carry; h5 -= carry << 25;
        carry = (h2 + (1L << 25)) >> 26; h3 += carry; h2 -= carry << 26;
        carry = (h6 + (1L << 25)) >> 26; h7 += carry; h6 -= carry << 26;
        carry = (h3 + (1L << 24)) >> 25; h4 += carry; h3 -= carry << 25;
        carry = (h7 + (1L << 24)) >> 25; h8 += carry; h7 -= carry << 25;
        carry = (h4 + (1L << 25)) >> 26; h5 += carry; h4 -= carry << 26;
        carry = (h8 + (1L << 25)) >> 26; h9 += carry; h8 -= carry << 26;
        carry = (h9 + (1L << 24)) >> 25; h0 += carry * 19; h9 -= carry << 25;
        carry = (h0 + (1L << 25)) >> 26; h1 += carry; h0 -= carry << 26;

        h[0] = h0; h[1] = h1; h[2] = h2; h[3] = h3; h[4] = h4;
        h[5] = h5; h[6] = h6; h[7] = h7; h[8] = h8; h[9] = h9;
    }

    private static void FieldSquare(Span<long> h, ReadOnlySpan<long> f)
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

        long carry;
        carry = (h0 + (1L << 25)) >> 26; h1 += carry; h0 -= carry << 26;
        carry = (h4 + (1L << 25)) >> 26; h5 += carry; h4 -= carry << 26;
        carry = (h1 + (1L << 24)) >> 25; h2 += carry; h1 -= carry << 25;
        carry = (h5 + (1L << 24)) >> 25; h6 += carry; h5 -= carry << 25;
        carry = (h2 + (1L << 25)) >> 26; h3 += carry; h2 -= carry << 26;
        carry = (h6 + (1L << 25)) >> 26; h7 += carry; h6 -= carry << 26;
        carry = (h3 + (1L << 24)) >> 25; h4 += carry; h3 -= carry << 25;
        carry = (h7 + (1L << 24)) >> 25; h8 += carry; h7 -= carry << 25;
        carry = (h4 + (1L << 25)) >> 26; h5 += carry; h4 -= carry << 26;
        carry = (h8 + (1L << 25)) >> 26; h9 += carry; h8 -= carry << 26;
        carry = (h9 + (1L << 24)) >> 25; h0 += carry * 19; h9 -= carry << 25;
        carry = (h0 + (1L << 25)) >> 26; h1 += carry; h0 -= carry << 26;

        h[0] = h0; h[1] = h1; h[2] = h2; h[3] = h3; h[4] = h4;
        h[5] = h5; h[6] = h6; h[7] = h7; h[8] = h8; h[9] = h9;
    }

    private static void FieldInvert(Span<long> o, ReadOnlySpan<long> z)
    {
        Span<long> t0 = stackalloc long[10];
        Span<long> t1 = stackalloc long[10];
        Span<long> t2 = stackalloc long[10];
        Span<long> t3 = stackalloc long[10];

        FieldSquare(t0, z);
        FieldSquare(t1, t0);
        FieldSquare(t1, t1);
        FieldMul(t1, z, t1);
        FieldMul(t0, t0, t1);
        FieldSquare(t2, t0);
        FieldMul(t1, t1, t2);
        FieldSquare(t2, t1);
        for (int i = 0; i < 4; i++) FieldSquare(t2, t2);
        FieldMul(t1, t2, t1);
        FieldSquare(t2, t1);
        for (int i = 0; i < 9; i++) FieldSquare(t2, t2);
        FieldMul(t2, t2, t1);
        FieldSquare(t3, t2);
        for (int i = 0; i < 19; i++) FieldSquare(t3, t3);
        FieldMul(t2, t3, t2);
        for (int i = 0; i < 10; i++) FieldSquare(t2, t2);
        FieldMul(t1, t2, t1);
        FieldSquare(t2, t1);
        for (int i = 0; i < 49; i++) FieldSquare(t2, t2);
        FieldMul(t2, t2, t1);
        FieldSquare(t3, t2);
        for (int i = 0; i < 99; i++) FieldSquare(t3, t3);
        FieldMul(t2, t3, t2);
        for (int i = 0; i < 50; i++) FieldSquare(t2, t2);
        FieldMul(t1, t2, t1);
        for (int i = 0; i < 5; i++) FieldSquare(t1, t1);
        FieldMul(o, t1, t0);
    }

    private static bool FieldSqrtRatio(Span<long> x, ReadOnlySpan<long> u, ReadOnlySpan<long> v)
    {
        Span<long> v3 = stackalloc long[10];
        Span<long> v7 = stackalloc long[10];
        Span<long> r = stackalloc long[10];
        Span<long> check = stackalloc long[10];

        FieldSquare(v3, v);
        FieldMul(v3, v3, v);           // v^3
        FieldSquare(v7, v3);
        FieldMul(v7, v7, v);           // v^7

        FieldMul(r, u, v3);            // u*v^3
        Span<long> uv7 = stackalloc long[10];
        FieldMul(uv7, u, v7);          // u*v^7

        // r = (u*v^3) * (u*v^7)^((p-5)/8)
        FieldPow22523(r, uv7);
        FieldMul(r, r, u);
        FieldMul(r, r, v3);

        // Check: v*r^2 == u or v*r^2 == -u
        FieldSquare(check, r);
        FieldMul(check, check, v);

        Span<long> negU = stackalloc long[10];
        FieldNeg(negU, u);

        if (FieldEquals(check, u))
        {
            r.CopyTo(x);
            return true;
        }

        if (FieldEquals(check, negU))
        {
            FieldMul(x, r, SqrtM1);
            return true;
        }

        return false;
    }

    private static void FieldPow22523(Span<long> o, ReadOnlySpan<long> z)
    {
        Span<long> t0 = stackalloc long[10];
        Span<long> t1 = stackalloc long[10];
        Span<long> t2 = stackalloc long[10];

        FieldSquare(t0, z);
        FieldSquare(t1, t0);
        FieldSquare(t1, t1);
        FieldMul(t1, z, t1);
        FieldMul(t0, t0, t1);
        FieldSquare(t0, t0);
        FieldMul(t0, t1, t0);
        FieldSquare(t1, t0);
        for (int i = 0; i < 4; i++) FieldSquare(t1, t1);
        FieldMul(t0, t1, t0);
        FieldSquare(t1, t0);
        for (int i = 0; i < 9; i++) FieldSquare(t1, t1);
        FieldMul(t1, t1, t0);
        FieldSquare(t2, t1);
        for (int i = 0; i < 19; i++) FieldSquare(t2, t2);
        FieldMul(t1, t2, t1);
        for (int i = 0; i < 10; i++) FieldSquare(t1, t1);
        FieldMul(t0, t1, t0);
        FieldSquare(t1, t0);
        for (int i = 0; i < 49; i++) FieldSquare(t1, t1);
        FieldMul(t1, t1, t0);
        FieldSquare(t2, t1);
        for (int i = 0; i < 99; i++) FieldSquare(t2, t2);
        FieldMul(t1, t2, t1);
        for (int i = 0; i < 50; i++) FieldSquare(t1, t1);
        FieldMul(t0, t1, t0);
        FieldSquare(t0, t0);
        FieldSquare(t0, t0);
        FieldMul(o, t0, z);
    }

    private static bool FieldEquals(ReadOnlySpan<long> a, ReadOnlySpan<long> b)
    {
        Span<long> diff = stackalloc long[10];
        FieldSub(diff, a, b);
        FieldReduce(diff);

        long acc = 0;
        for (int i = 0; i < 10; i++)
            acc |= diff[i];
        return acc == 0;
    }

    private static int IsNegative(ReadOnlySpan<long> f)
    {
        Span<byte> s = stackalloc byte[32];
        FieldToBytes(s, f);
        return s[0] & 1;
    }

    #endregion

    #region Scalar Arithmetic (mod L)

    // L = 2^252 + 27742317777372353535851937790883648493
    private static readonly byte[] L = [
        0xed, 0xd3, 0xf5, 0x5c, 0x1a, 0x63, 0x12, 0x58,
        0xd6, 0x9c, 0xf7, 0xa2, 0xde, 0xf9, 0xde, 0x14,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x10
    ];

    private static void ScalarReduce(Span<byte> s)
    {
        // Use BigInteger for simplicity
        var value = new System.Numerics.BigInteger(s, isUnsigned: true, isBigEndian: false);
        var l = new System.Numerics.BigInteger(L, isUnsigned: true, isBigEndian: false);
        value %= l;

        var result = value.ToByteArray(isUnsigned: true, isBigEndian: false);
        s.Clear();
        result.AsSpan().CopyTo(s);
    }

    private static void ScalarMulAdd(Span<byte> s, ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, ReadOnlySpan<byte> c)
    {
        // s = (a * b + c) mod L
        var aVal = new System.Numerics.BigInteger(a, isUnsigned: true, isBigEndian: false);
        var bVal = new System.Numerics.BigInteger(b, isUnsigned: true, isBigEndian: false);
        var cVal = new System.Numerics.BigInteger(c, isUnsigned: true, isBigEndian: false);
        var l = new System.Numerics.BigInteger(L, isUnsigned: true, isBigEndian: false);

        var result = (aVal * bVal + cVal) % l;

        s.Clear();
        var bytes = result.ToByteArray(isUnsigned: true, isBigEndian: false);
        bytes.AsSpan().CopyTo(s);
    }

    private static bool IsCanonicalScalar(ReadOnlySpan<byte> s)
    {
        var value = new System.Numerics.BigInteger(s, isUnsigned: true, isBigEndian: false);
        var l = new System.Numerics.BigInteger(L, isUnsigned: true, isBigEndian: false);
        return value < l;
    }

    #endregion
}

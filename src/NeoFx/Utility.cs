﻿using NeoFx.Models;
using NeoFx.Storage;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Security.Cryptography;

namespace NeoFx
{
    public static class Utility
    {
        private static readonly Lazy<SHA256> _sha256 = new Lazy<SHA256>(() => SHA256.Create());
        private static readonly Lazy<RIPEMD160> _ripemd160 = new Lazy<RIPEMD160>(() => RIPEMD160.Create());
        private static readonly Lazy<Random> _random = new Lazy<Random>(() => new Random());

        public static int GetVarSize(ulong value)
        {
            if (value < 0xfd)
            {
                return sizeof(byte);
            }

            if (value < 0xffff)
            {
                return sizeof(byte) + sizeof(ushort);
            }

            if (value < 0xffffffff)
            {
                return sizeof(byte) + sizeof(uint);
            }

            return sizeof(byte) + sizeof(ulong);
        }

        public static int GetVarSize(this ReadOnlyMemory<byte> value)
            => value.Span.GetVarSize();

        public static int GetVarSize(this ReadOnlySpan<byte> value)
            => GetVarSize((ulong)value.Length) + value.Length;

        public static int GetVarSize(this string value)
        {
            int size = System.Text.Encoding.UTF8.GetByteCount(value);
            return GetVarSize((ulong)size) + size;
        }

        public static int GetVarSize<T>(this ReadOnlyMemory<T> memory, Func<T, int> getSize)
            => memory.Span.GetVarSize(getSize);

        public static int GetVarSize<T>(this ReadOnlySpan<T> span, Func<T, int> getSize)
        {
            int size = 0;
            for (int i = 0; i < span.Length; i++)
            {
                size += getSize(span[i]);
            }
            return GetVarSize((ulong)span.Length) + size;
        }

        public static int GetVarSize<T>(this ReadOnlyMemory<T> memory, int size)
            => memory.Span.GetVarSize(size);

        public static int GetVarSize<T>(this ReadOnlySpan<T> span, int size)
            => GetVarSize((ulong)span.Length) + (span.Length * size);

        public static byte[] Base58CheckDecode(this string input)
        {
            var buffer = SimpleBase.Base58.Bitcoin.Decode(input);
            if (buffer.Length < 4) throw new FormatException();

            Span<byte> checksumPrime = stackalloc byte[32];
            if (_sha256.Value.TryComputeHash(buffer.Slice(0, buffer.Length - 4), checksumPrime, out var written))
            {
                Debug.Assert(written == 32);

                Span<byte> checksum = stackalloc byte[32];
                if (_sha256.Value.TryComputeHash(checksumPrime, checksum, out written))
                {
                    Debug.Assert(written == 32);

                    if (buffer.Slice(buffer.Length - 4).SequenceEqual(checksum.Slice(0, 4)))
                    {
                        return buffer.Slice(0, buffer.Length - 4).ToArray();
                    }
                }
            }
            throw new FormatException();
        }

        public static UInt160 ToScriptHash(this string address)
        {
            byte[] data = address.Base58CheckDecode();
            if (data.Length != 21)
                throw new FormatException();
            return new UInt160(data.AsSpan().Slice(1));
        }

        public static bool TryHash256(ReadOnlySpan<byte> message, Span<byte> hash)
        {
            Span<byte> tempBuffer = stackalloc byte[32];
            if (_sha256.Value.TryComputeHash(message, tempBuffer, out var written1)
                && _sha256.Value.TryComputeHash(tempBuffer, hash, out var written2))
            {
                Debug.Assert(written1 == 32 && written2 == 32);
                return true;
            }
            return false;
        }

        public static bool TryHash160(ReadOnlySpan<byte> message, Span<byte> hash)
        {
            Span<byte> tempBuffer = stackalloc byte[32];
            if (_sha256.Value.TryComputeHash(message, tempBuffer, out var written1)
                && _ripemd160.Value.TryComputeHash(tempBuffer, hash, out var written2))
            {
                Debug.Assert(written1 == 32 && written2 == 20);
                return true;
            }
            return false;
        }

        public static bool TryWriteHashData(in Transaction tx, Span<byte> span, out int bytesWritten)
        {
            var writer = new SpanWriter<byte>(span);
            if (writer.TryWrite((byte)tx.Type)
                && writer.TryWrite(tx.Version)
                && writer.TryWrite(tx.TransactionData.Span)
                && writer.TryWriteVarArray(tx.Attributes, BinaryFormat.TryWrite)
                && writer.TryWriteVarArray(tx.Inputs, BinaryFormat.TryWrite)
                && writer.TryWriteVarArray(tx.Outputs, BinaryFormat.TryWrite))
            {
                bytesWritten = writer.Contents.Length;
                return true;
            }

            bytesWritten = default;
            return false;
        }

        public static bool TryHash(in Transaction tx, out UInt256 hash)
        {
            using (var memBlock = MemoryPool<byte>.Shared.Rent(tx.GetSize()))
            {
                Span<byte> hashBuffer = stackalloc byte[32];

                if (TryWriteHashData(tx, memBlock.Memory.Span, out var bytesWritten)
                    && TryHash256(memBlock.Memory.Span.Slice(0, bytesWritten), hashBuffer))
                {
                    hash = new UInt256(hashBuffer);
                    return true;
                }
            }

            hash = default;
            return false;
        }

        #region Helper Functions for TryDecompressPoint
        private static bool TestBit(this BigInteger bigInteger, int index)
        {
            return (bigInteger & (BigInteger.One << index)) > BigInteger.Zero;
        }

        private static BigInteger Mod(this BigInteger x, BigInteger y)
        {
            x %= y;
            if (x.Sign < 0)
                x += y;
            return x;
        }

        private static int GetBitLength(this BigInteger bigInt)
        {
            Span<byte> buffer = stackalloc byte[bigInt.GetByteCount()];
            if (bigInt.TryWriteBytes(buffer, out var written))
            {
                Debug.Assert(written <= buffer.Length);
                return GetBigIntegerBitLength(buffer, bigInt.Sign);
            }

            throw new ArgumentException(nameof(bigInt));
        }

        private static int GetBigIntegerBitLength(Span<byte> b, int sign)
        {
            static int BitLen(int w)
            {
                return (w < 1 << 15 ? (w < 1 << 7
                    ? (w < 1 << 3 ? (w < 1 << 1
                    ? (w < 1 << 0 ? (w < 0 ? 32 : 0) : 1)
                    : (w < 1 << 2 ? 2 : 3)) : (w < 1 << 5
                    ? (w < 1 << 4 ? 4 : 5)
                    : (w < 1 << 6 ? 6 : 7)))
                    : (w < 1 << 11
                    ? (w < 1 << 9 ? (w < 1 << 8 ? 8 : 9) : (w < 1 << 10 ? 10 : 11))
                    : (w < 1 << 13 ? (w < 1 << 12 ? 12 : 13) : (w < 1 << 14 ? 14 : 15)))) : (w < 1 << 23 ? (w < 1 << 19
                    ? (w < 1 << 17 ? (w < 1 << 16 ? 16 : 17) : (w < 1 << 18 ? 18 : 19))
                    : (w < 1 << 21 ? (w < 1 << 20 ? 20 : 21) : (w < 1 << 22 ? 22 : 23))) : (w < 1 << 27
                    ? (w < 1 << 25 ? (w < 1 << 24 ? 24 : 25) : (w < 1 << 26 ? 26 : 27))
                    : (w < 1 << 29 ? (w < 1 << 28 ? 28 : 29) : (w < 1 << 30 ? 30 : 31)))));
            }

            return ((b.Length - 1) * 8) + BitLen(sign > 0 ? b[b.Length - 1] : 255 - b[b.Length - 1]);
        }

        private static (BigInteger U, BigInteger V) FastLucasSequence(BigInteger p, BigInteger P, BigInteger Q, BigInteger k)
        {
            static (int bitLength, int lowestBitSet) GetBitLengthAndLowestSetBit(BigInteger bigInt)
            {
                Span<byte> buffer = stackalloc byte[bigInt.GetByteCount()];
                if (bigInt.TryWriteBytes(buffer, out var written))
                {
                    Debug.Assert(written <= buffer.Length);
                    var bitLength = GetBigIntegerBitLength(buffer, bigInt.Sign);

                    if (bigInt.Sign == 0)
                    {
                        return (bitLength, -1);
                    }

                    int w = 0;
                    while (buffer[w] == 0)
                    {
                        w++;
                    }

                    for (int x = 0; x < 8; x++)
                    {
                        if ((buffer[w] & 1 << x) > 0)
                        {
                            return (bitLength, x + (w * 8));
                        }
                    }
                }

                throw new ArgumentException(nameof(bigInt));
            }

            var (n, s) = GetBitLengthAndLowestSetBit(k);

            Debug.Assert(k.TestBit(s));

            BigInteger Uh = 1;
            BigInteger Vl = 2;
            BigInteger Vh = P;
            BigInteger Ql = 1;
            BigInteger Qh = 1;

            for (int j = n - 1; j >= s + 1; --j)
            {
                Ql = (Ql * Qh).Mod(p);

                if (k.TestBit(j))
                {
                    Qh = (Ql * Q).Mod(p);
                    Uh = (Uh * Vh).Mod(p);
                    Vl = ((Vh * Vl) - (P * Ql)).Mod(p);
                    Vh = ((Vh * Vh) - (Qh << 1)).Mod(p);
                }
                else
                {
                    Qh = Ql;
                    Uh = (Uh * Vl - Ql).Mod(p);
                    Vh = (Vh * Vl - (P * Ql)).Mod(p);
                    Vl = ((Vl * Vl) - (Ql << 1)).Mod(p);
                }
            }

            Ql = (Ql * Qh).Mod(p);
            Qh = (Ql * Q).Mod(p);
            Uh = ((Uh * Vl) - Ql).Mod(p);
            Vl = ((Vh * Vl) - (P * Ql)).Mod(p);
            Ql = (Ql * Qh).Mod(p);

            for (int j = 1; j <= s; ++j)
            {
                Uh = Uh * Vl * p;
                Vl = ((Vl * Vl) - (Ql << 1)).Mod(p);
                Ql = (Ql * Ql).Mod(p);
            }

            return (Uh, Vl);
        }

        private static BigInteger NextBigInteger(int sizeInBits)
        {
            if (sizeInBits < 0)
                throw new ArgumentException("sizeInBits must be non-negative");
            if (sizeInBits == 0)
                return 0;
            Span<byte> span = stackalloc byte[(sizeInBits / 8) + 1];
            _random.Value.NextBytes(span);
            if (sizeInBits % 8 == 0)
                span[span.Length - 1] = 0;
            else
                span[span.Length - 1] &= (byte)((1 << sizeInBits % 8) - 1);
            return new BigInteger(span);
        }

        private static bool TrySqrt(BigInteger value, BigInteger q, out BigInteger result)
        {
            static bool TryCheckSqrt(BigInteger z, BigInteger q, BigInteger value, out BigInteger result)
            {
                if (Mod(z * z, q) == value)
                {
                    result = z;
                    return true;
                }
                else
                {
                    result = default;
                    return false;
                }
            }

            if (q.TestBit(1))
            {
                var sqrt = BigInteger.ModPow(value, (q >> 2) + BigInteger.One, q);
                return TryCheckSqrt(sqrt, q, value, out result);
            }

            BigInteger qMinusOne = q - BigInteger.One;
            BigInteger legendreExponent = qMinusOne >> 1;
            if (BigInteger.ModPow(value, legendreExponent, q) != 1)
            {
                result = default;
                return false;
            }

            BigInteger u = qMinusOne >> 2;
            BigInteger k = (u << 1) + 1;
            BigInteger Q = value;
            BigInteger fourQ = Mod(Q << 2, q);
            BigInteger U, V;
            var qBitLength = q.GetBitLength();

            do
            {
                BigInteger P;
                do
                {
                    P = NextBigInteger(qBitLength);
                }
                while (P >= q || BigInteger.ModPow((P * P) - fourQ, legendreExponent, q) != qMinusOne);

                (U, V) = FastLucasSequence(q, P, Q, k);
                if (Mod(V * V, q) == fourQ)
                {
                    if (V.TestBit(0))
                    {
                        V += q;
                    }
                    V >>= 1;
                    Debug.Assert((V * V).Mod(q) == value);

                    result = V;
                    return true;
                }
            }
            while (U.Equals(BigInteger.One) || U.Equals(qMinusOne));

            result = default;
            return false;
        }

        private static bool TryToByteArray(this BigInteger bigInteger, int expectedLength, [NotNullWhen(true)] out byte[]? buffer)
        {
            var array = new byte[expectedLength];
            var byteCount = bigInteger.GetByteCount(true);

            if (byteCount <= expectedLength
                && bigInteger.TryWriteBytes(array.AsSpan().Slice(expectedLength - byteCount), out var written, true, true))
            {
                Debug.Assert(written == byteCount);
                buffer = array;
                return true;
            }

            buffer = null;
            return false;
        }
        #endregion

        private static bool TryDecompressPoint(ReadOnlySpan<byte> xSpan, int yTilde, int expectedLength, ECCurve curve, out ECPoint point)
        {
            Debug.Assert(curve.IsExplicit);

            var x = new BigInteger(xSpan, isUnsigned: true, isBigEndian: true);
            var a = new BigInteger(curve.A, isUnsigned: true, isBigEndian: true);
            var b = new BigInteger(curve.B, isUnsigned: true, isBigEndian: true);
            var prime = new BigInteger(curve.Prime, isUnsigned: true, isBigEndian: true);

            var alpha = Mod((x * ((x * x) + a)) + b, prime);
            if (TrySqrt(alpha, prime, out var beta))
            {
                if ((beta.IsEven ? 0 : 1) != yTilde)
                {
                    beta = prime - beta;
                }

                if (x.TryToByteArray(expectedLength, out var xArray)
                    && beta.TryToByteArray(expectedLength, out var betaArray))
                {
                    point = new ECPoint()
                    {
                        X = xArray,
                        Y = betaArray
                    };
                    return true;
                }
            }

            point = default;
            return false;
        }

        internal static bool TryDecodePoint(ReadOnlySpan<byte> span, ECCurve curve, out ECPoint point)
        {
            if (!curve.IsExplicit && curve.IsNamed)
            {
                using var ecdsa = ECDsa.Create(curve);
                curve = ecdsa.ExportExplicitParameters(false).Curve;
            }
            var prime = new BigInteger(curve.Prime, isUnsigned: true, isBigEndian: true);
            int expectedLength = (prime.GetBitLength() + 7) / 8;

            switch (span[0])
            {
                case 0x00:
                    point = new ECPoint();
                    return true;
                case 0x02:
                case 0x03:
                    Debug.Assert(span.Length == expectedLength + 1);
                    int yTilde = span[0] & 1;
                    return TryDecompressPoint(span.Slice(1), yTilde, expectedLength, curve, out point);
                case 0x04:
                case 0x06:
                case 0x07:
                    Debug.Assert(span.Length == (2 * expectedLength) + 1);
                    point = new ECPoint()
                    {
                        X = span.Slice(1, expectedLength).ToArray(),
                        Y = span.Slice(1 + expectedLength, expectedLength).ToArray()
                    };
                    return true;
                default:
                    point = default;
                    return false;
            }
        }

        internal static bool TryEncodePoint(this ECPoint point, Span<byte> span, bool compressed, out int bytesWritten)
        {
            if (point.X == null
                && point.Y == null
                && span.Length >= 1)
            {
                span[0] = 0;
                bytesWritten = 1;
                return true;
            }
            else if (compressed
                && span.Length >= 33
                && point.X.AsSpan().TryCopyTo(span.Slice(1, 32)))
            {
                var y = new BigInteger(point.Y, true, true);
                span[0] = y.IsEven ? (byte)0x02 : (byte)0x03;
                bytesWritten = 33;
                return true;
            }
            else if (compressed
                && span.Length >= 65
                && point.X.AsSpan().TryCopyTo(span.Slice(1, 32))
                && point.Y.AsSpan().TryCopyTo(span.Slice(33, 32)))
            {
                span[0] = 0x04;
                bytesWritten = 65;
                return true;
            }

            bytesWritten = default;
            return false;
        }
    }
}

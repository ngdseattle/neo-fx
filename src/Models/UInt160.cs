﻿using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;

namespace NeoFx.Models
{
    public readonly struct UInt160 : IEquatable<UInt160>, IComparable<UInt160>
    {
        public static readonly UInt160 Zero = new UInt160(0, 0, 0);

        private readonly ulong data1;
        private readonly ulong data2;
        private readonly uint data3;

        public const int Size = sizeof(ulong) + sizeof(ulong) + sizeof(uint);

        internal UInt160(ulong data1, ulong data2, uint data3)
        {
            this.data1 = data1;
            this.data2 = data2;
            this.data3 = data3;
        }

        public UInt160(ReadOnlySpan<byte> span)
        {
            if (!TryReadBytes(span, out this))
            {
                throw new ArgumentException(nameof(span));
            }
        }

        public static bool TryReadBytes(ReadOnlySpan<byte> buffer, out UInt160 result)
        {
            if (buffer.Length >= Size
                && BinaryPrimitives.TryReadUInt64LittleEndian(buffer, out var data1)
                && BinaryPrimitives.TryReadUInt64LittleEndian(buffer.Slice(8), out var data2)
                && BinaryPrimitives.TryReadUInt32LittleEndian(buffer.Slice(16), out var data3))
            {
                result = new UInt160(data1, data2, data3);
                return true;
            }

            result = default;
            return false;
        }

        public static bool TryRead(ref SequenceReader<byte> reader, out UInt160 value) =>
            reader.TryRead(Size, TryReadBytes, out value);

        public bool TryWriteBytes(Span<byte> buffer)
        {
            return buffer.Length >= Size
                && BinaryPrimitives.TryWriteUInt64LittleEndian(buffer, data1)
                && BinaryPrimitives.TryWriteUInt64LittleEndian(buffer.Slice(8), data2)
                && BinaryPrimitives.TryWriteUInt32LittleEndian(buffer.Slice(16), data3);
        }

        public override string ToString()
        {
            return string.Create(2 + (Size * 2), this, (buffer, that) =>
            {
                bool result = that.TryFormat(buffer, out var charWritten);
                Debug.Assert(result && charWritten == (2 + (Size * 2)));
            });
        }

        // TODO: ReadOnlySpan<char> format && IFormatProvider arguments
        public bool TryFormat(Span<char> destination, out int charsWritten)
        {
            if (destination.Length >= ((Size * 2) + 2)
                && data3.TryFormat(destination.Slice(2), out var d3, "x8")
                && data2.TryFormat(destination.Slice(10), out var d2, "x16")
                && data1.TryFormat(destination.Slice(26), out var d1, "x16"))
            {
                Debug.Assert(d1 == 16);
                Debug.Assert(d2 == 16);
                Debug.Assert(d3 == 8);

                destination[0] = '0';
                destination[1] = 'x';
                charsWritten = ((Size * 2) + 2);
                return true;
            }

            charsWritten = 0;
            return false;
        }

        // TODO:
        //      IFormattable
        //      public static bool TryParse(ReadOnlySpan<char> @string, out UInt160 result)

        public override bool Equals(object obj)
        {
            return (obj is UInt160 value) && (Equals(value));
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(data1, data2, data3);
        }

        public bool Equals(in UInt160 other)
        {
            return (data1 == other.data1)
                && (data2 == other.data2)
                && (data3 == other.data3);
        }

        public int CompareTo(in UInt160 other)
        {
            var result = data1.CompareTo(other.data1);
            if (result != 0)
                return result;

            result = data2.CompareTo(other.data2);
            if (result != 0)
                return result;

            return data3.CompareTo(other.data3);
        }

        int IComparable<UInt160>.CompareTo(UInt160 other)
        {
            return CompareTo(other);
        }

        bool IEquatable<UInt160>.Equals(UInt160 other)
        {
            return Equals(other);
        }

        public static bool operator ==(in UInt160 left, in UInt160 right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(in UInt160 left, in UInt160 right)
        {
            return !left.Equals(right);
        }

        public static bool operator >(in UInt160 left, in UInt160 right)
        {
            return left.CompareTo(right) > 0;
        }

        public static bool operator >=(in UInt160 left, in UInt160 right)
        {
            return left.CompareTo(right) >= 0;
        }

        public static bool operator <(in UInt160 left, in UInt160 right)
        {
            return left.CompareTo(right) < 0;
        }

        public static bool operator <=(in UInt160 left, in UInt160 right)
        {
            return left.CompareTo(right) <= 0;
        }
    }
}

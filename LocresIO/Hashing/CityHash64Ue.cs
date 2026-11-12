// Independent C# implementation of the CityHash64 algorithm (Google CityHash
// v1.1 semantics, little-endian), which is the variant Unreal Engine uses for
// Optimized_CityHash64_UTF16 .locres key hashing.
//
// CityHash was created by Geoff Pike and Jyrki Alakuijala,
// Copyright (c) 2011 Google, Inc., released under the MIT License.
// This file implements the published algorithm; see THIRD-PARTY-NOTICES.

using System.Buffers.Binary;
using System.Numerics;

namespace Locres.IO.Hashing;

internal static class CityHash64Ue
{
    // Primes between 2^63 and 2^64.
    private const ulong K0 = 0xc3a5c85c97cb3127UL;
    private const ulong K1 = 0xb492b66fbe98f273UL;
    private const ulong K2 = 0x9ae16a3b2f90404fUL;

    internal static ulong Hash(ReadOnlySpan<byte> s)
    {
        int len = s.Length;
        if (len <= 32)
            return len <= 16 ? HashLen0To16(s) : HashLen17To32(s);
        if (len <= 64)
            return HashLen33To64(s);

        // For inputs over 64 bytes: hash the tail first, then walk 64-byte chunks.
        ulong x = Fetch64(s, len - 40);
        ulong y = Fetch64(s, len - 16) + Fetch64(s, len - 56);
        ulong z = HashLen16(Fetch64(s, len - 48) + (ulong)len, Fetch64(s, len - 24));
        (ulong vLo, ulong vHi) = WeakHashLen32WithSeeds(s, len - 64, (ulong)len, z);
        (ulong wLo, ulong wHi) = WeakHashLen32WithSeeds(s, len - 32, y + K1, x);
        x = x * K1 + Fetch64(s, 0);

        int remaining = (len - 1) & ~63;
        int offset = 0;
        do
        {
            x = BitOperations.RotateRight(x + y + vLo + Fetch64(s, offset + 8), 37) * K1;
            y = BitOperations.RotateRight(y + vHi + Fetch64(s, offset + 48), 42) * K1;
            x ^= wHi;
            y += vLo + Fetch64(s, offset + 40);
            z = BitOperations.RotateRight(z + wLo, 33) * K1;
            (vLo, vHi) = WeakHashLen32WithSeeds(s, offset, vHi * K1, x + wLo);
            (wLo, wHi) = WeakHashLen32WithSeeds(s, offset + 32, z + wHi, y + Fetch64(s, offset + 16));
            (z, x) = (x, z);
            offset += 64;
            remaining -= 64;
        } while (remaining != 0);

        return HashLen16(HashLen16(vLo, wLo) + ShiftMix(y) * K1 + z,
                         HashLen16(vHi, wHi) + x);
    }

    private static ulong HashLen0To16(ReadOnlySpan<byte> s)
    {
        int len = s.Length;
        if (len >= 8)
        {
            ulong mul = K2 + (ulong)len * 2;
            ulong a = Fetch64(s, 0) + K2;
            ulong b = Fetch64(s, len - 8);
            ulong c = BitOperations.RotateRight(b, 37) * mul + a;
            ulong d = (BitOperations.RotateRight(a, 25) + b) * mul;
            return HashLen16(c, d, mul);
        }
        if (len >= 4)
        {
            ulong mul = K2 + (ulong)len * 2;
            ulong a = Fetch32(s, 0);
            return HashLen16((ulong)len + (a << 3), Fetch32(s, len - 4), mul);
        }
        if (len > 0)
        {
            byte a = s[0];
            byte b = s[len >> 1];
            byte c = s[len - 1];
            uint y = a + ((uint)b << 8);
            uint z = (uint)len + ((uint)c << 2);
            return ShiftMix(y * K2 ^ z * K0) * K2;
        }
        return K2;
    }

    private static ulong HashLen17To32(ReadOnlySpan<byte> s)
    {
        int len = s.Length;
        ulong mul = K2 + (ulong)len * 2;
        ulong a = Fetch64(s, 0) * K1;
        ulong b = Fetch64(s, 8);
        ulong c = Fetch64(s, len - 8) * mul;
        ulong d = Fetch64(s, len - 16) * K2;
        return HashLen16(
            BitOperations.RotateRight(a + b, 43) + BitOperations.RotateRight(c, 30) + d,
            a + BitOperations.RotateRight(b + K2, 18) + c,
            mul);
    }

    private static ulong HashLen33To64(ReadOnlySpan<byte> s)
    {
        int len = s.Length;
        ulong mul = K2 + (ulong)len * 2;
        ulong a = Fetch64(s, 0) * K2;
        ulong b = Fetch64(s, 8);
        ulong c = Fetch64(s, len - 24);
        ulong d = Fetch64(s, len - 32);
        ulong e = Fetch64(s, 16) * K2;
        ulong f = Fetch64(s, 24) * 9;
        ulong g = Fetch64(s, len - 8);
        ulong h = Fetch64(s, len - 16) * mul;
        ulong u = BitOperations.RotateRight(a + g, 43) + (BitOperations.RotateRight(b, 30) + c) * 9;
        ulong v = ((a + g) ^ d) + f + 1;
        ulong w = BinaryPrimitives.ReverseEndianness((u + v) * mul) + h;
        ulong x = BitOperations.RotateRight(e + f, 42) + c;
        ulong y = (BinaryPrimitives.ReverseEndianness((v + w) * mul) + g) * mul;
        ulong z = e + f + c;
        a = BinaryPrimitives.ReverseEndianness((x + z) * mul + y) + b;
        b = ShiftMix((z + a) * mul + d + h) * mul;
        return b + x;
    }

    private static (ulong Lo, ulong Hi) WeakHashLen32WithSeeds(
        ulong w, ulong x, ulong y, ulong z, ulong a, ulong b)
    {
        a += w;
        b = BitOperations.RotateRight(b + a + z, 21);
        ulong c = a;
        a += x;
        a += y;
        b += BitOperations.RotateRight(a, 44);
        return (a + z, b + c);
    }

    private static (ulong Lo, ulong Hi) WeakHashLen32WithSeeds(
        ReadOnlySpan<byte> s, int offset, ulong a, ulong b)
    {
        return WeakHashLen32WithSeeds(
            Fetch64(s, offset),
            Fetch64(s, offset + 8),
            Fetch64(s, offset + 16),
            Fetch64(s, offset + 24),
            a, b);
    }

    // Murmur-inspired 128 -> 64 bit mix.
    private static ulong HashLen16(ulong u, ulong v)
    {
        const ulong kMul = 0x9ddfea08eb382d69UL;
        ulong a = (u ^ v) * kMul;
        a ^= a >> 47;
        ulong b = (v ^ a) * kMul;
        b ^= b >> 47;
        b *= kMul;
        return b;
    }

    private static ulong HashLen16(ulong u, ulong v, ulong mul)
    {
        ulong a = (u ^ v) * mul;
        a ^= a >> 47;
        ulong b = (v ^ a) * mul;
        b ^= b >> 47;
        b *= mul;
        return b;
    }

    private static ulong ShiftMix(ulong val) => val ^ (val >> 47);

    private static ulong Fetch64(ReadOnlySpan<byte> s, int index)
        => BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(index));

    private static uint Fetch32(ReadOnlySpan<byte> s, int index)
        => BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(index));
}

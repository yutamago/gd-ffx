using System;
using System.Buffers.Binary;
using System.Text;
using Godot;

namespace GdFfx.Api;

/// <summary>
/// Reads the game-state API's encoding (ffx-rust's docs/state-api.md):
/// little-endian numbers, bool one byte, string a u16 byte count and UTF-8,
/// vectors and matrices as floats. The generated classes (GameStateApi.g.cs)
/// read their fields through it.
/// </summary>
public ref struct StateReader
{
    private readonly ReadOnlySpan<byte> _b;
    private int _at;

    public StateReader(ReadOnlySpan<byte> bytes)
    {
        _b = bytes;
        _at = 0;
    }

    private ReadOnlySpan<byte> Take(int n)
    {
        if (_at + n > _b.Length)
            throw new FormatException("game-state snapshot cut short");
        var s = _b.Slice(_at, n);
        _at += n;
        return s;
    }

    public byte U8() => Take(1)[0];
    public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public short I16() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));
    public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    public float F32() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));
    public double F64() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));
    public bool Bool() => U8() != 0;
    public string Str() => Encoding.UTF8.GetString(Take(U16()));
    public Vector3 Vec3() => new(F32(), F32(), F32());
    public byte[] Bytes() => Take(U16()).ToArray();
    public byte[] Rgb() => Take(3).ToArray();

    public float[] Floats(int n) => Floats(null, n);

    // ---- reading into what a snapshot read before holds (StateApi.Decode's `into`) ----

    /// <summary>A string; <paramref name="was"/> itself when the text is the same (no new string).</summary>
    public string Str(string? was)
    {
        var b = Take(U16());
        if (was != null && was.Length == b.Length)
        {
            int i = 0;
            while (i < b.Length && b[i] < 0x80 && b[i] == was[i])
                i++;
            if (i == b.Length)
                return was;
        }
        return Encoding.UTF8.GetString(b);
    }

    /// <summary>Bytes, into <paramref name="was"/> when it has the length.</summary>
    public byte[] Bytes(byte[]? was) => Fill(was, U16());

    /// <summary>Three bytes, into <paramref name="was"/> when it has three.</summary>
    public byte[] Rgb(byte[]? was) => Fill(was, 3);

    private byte[] Fill(byte[]? was, int n)
    {
        var b = Take(n);
        if (was == null || was.Length != n)
            return b.ToArray();
        b.CopyTo(was);
        return was;
    }

    /// <summary><paramref name="n"/> floats, into <paramref name="was"/> when it has that many.</summary>
    public float[] Floats(float[]? was, int n)
    {
        var f = was != null && was.Length == n ? was : new float[n];
        for (int i = 0; i < n; i++)
            f[i] = F32();
        return f;
    }
}

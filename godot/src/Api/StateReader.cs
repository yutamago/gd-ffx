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

    public float[] Floats(int n)
    {
        var f = new float[n];
        for (int i = 0; i < n; i++)
            f[i] = F32();
        return f;
    }
}

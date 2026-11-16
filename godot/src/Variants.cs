using System;
using System.Collections.Concurrent;
using Godot;

namespace GdFfx;

/// <summary>
/// Godot's C# wrappers that own native memory have finalizers: a <see cref="Variant"/> of anything
/// but a plain value (a string, a packed array, an array, a dictionary, an object), the
/// collections (<see cref="Godot.Collections.Array"/>, <see cref="Godot.Collections.Dictionary"/>),
/// <see cref="StringName"/> and <see cref="NodePath"/>; each also registers a
/// <see cref="WeakReference{T}"/> with Godot's tracker. A finalizable object outlives the collection
/// that finds it dead (it waits for its finalizer), so a few made a frame filled every young
/// collection with tens of thousands of them and made it long (docs/running.md, "Benchmark").
/// What is made often goes through here: keys made once, empty arguments shared, variants
/// disposed as soon as Godot has copied them (no finalizer left to run).
/// </summary>
public static class Variants
{
    private static readonly ConcurrentDictionary<string, Variant> Keys = new();

    /// <summary>A dictionary key (or any string argument) as a variant made once and kept.</summary>
    public static Variant Key(string name) => Keys.GetOrAdd(name, static n => Variant.CreateFrom(n));

    private static readonly ConcurrentDictionary<string, StringName> Names = new();

    /// <summary>A <see cref="StringName"/> (a meta's, a method's) made once and kept: one from a string each call is a new finalizable object.</summary>
    public static StringName Name(string name) => Names.GetOrAdd(name, static n => new StringName(n));

    /// <summary><paramref name="d"/>'s value at <paramref name="key"/> (nil when missing), without a new variant for the key.</summary>
    public static Variant Get(Godot.Collections.Dictionary d, string key) =>
        d.TryGetValue(Key(key), out var v) ? v : default;

    /// <summary>
    /// <see cref="ArrayMesh.AddSurfaceFromArrays"/> without blend shapes or LODs: Godot's binding
    /// makes a new empty array and dictionary for each one left out.
    /// </summary>
    public static void AddSurface(ArrayMesh mesh, Mesh.PrimitiveType primitive, Godot.Collections.Array arrays) =>
        mesh.AddSurfaceFromArrays(primitive, arrays, NoBlendShapes, NoLods);

    private static readonly Godot.Collections.Array<Godot.Collections.Array> NoBlendShapes = new();
    private static readonly Godot.Collections.Dictionary NoLods = new();

    /// <summary>A shader global set to a <see cref="Basis"/> (a variant with native memory), the variant disposed.</summary>
    public static void SetGlobal(StringName name, Basis value)
    {
        using var v = Variant.CreateFrom(value);
        RenderingServer.GlobalShaderParameterSet(name, v);
    }

    /// <summary>A shader parameter set to a resource (a variant with a finalizer), the variant disposed; the name made once.</summary>
    public static void SetParameter(ShaderMaterial m, string name, GodotObject? value)
    {
        if (value == null)
        {
            m.SetShaderParameter(Name(name), default);
            return;
        }
        using var v = Variant.CreateFrom(value);
        m.SetShaderParameter(Name(name), v);
    }

    /// <summary>The dictionary <paramref name="node"/>'s <paramref name="method"/> returns, its variant disposed (the caller disposes the dictionary).</summary>
    public static Godot.Collections.Dictionary CallDictionary(GodotObject node, StringName method)
    {
        using var v = node.Call(method);
        return v.AsGodotDictionary();
    }

    /// <summary><paramref name="a"/>[<paramref name="i"/>] set to a packed copy of <paramref name="s"/>, the variant between disposed.</summary>
    public static void Set(Godot.Collections.Array a, int i, Span<Vector3> s)
    {
        using var v = Variant.CreateFrom(s);
        a[i] = v;
    }

    /// <inheritdoc cref="Set(Godot.Collections.Array, int, Span{Vector3})"/>
    public static void Set(Godot.Collections.Array a, int i, Span<Vector2> s)
    {
        using var v = Variant.CreateFrom(s);
        a[i] = v;
    }

    /// <inheritdoc cref="Set(Godot.Collections.Array, int, Span{Vector3})"/>
    public static void Set(Godot.Collections.Array a, int i, Span<Color> s)
    {
        using var v = Variant.CreateFrom(s);
        a[i] = v;
    }

    /// <inheritdoc cref="Set(Godot.Collections.Array, int, Span{Vector3})"/>
    public static void Set(Godot.Collections.Array a, int i, Span<int> s)
    {
        using var v = Variant.CreateFrom(s);
        a[i] = v;
    }
}

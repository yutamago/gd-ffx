using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Godot;

namespace GdFfx;

/// <summary>
/// What the classic content's glTF JSON says beyond Godot's import (the import's <c>extras</c>,
/// docs/importer.md): each material's effect variant, parameters, passes, switches and texture
/// paths, each mesh's object, instance, morph and sort fields. Read once from the .glb's JSON chunk
/// with System.Text.Json into plain C# objects: reading it through <see cref="GltfState.Json"/>
/// or a mesh's <c>extras</c> metadata made a Godot dictionary, array or string variant for each
/// value, each finalizable, tens of thousands a background load (docs/running.md, "Garbage
/// collections").
/// </summary>
public sealed class GltfExtras
{
    /// <summary>glTF material i's (as many as the file has).</summary>
    public required MaterialExtras[] Materials { get; init; }

    /// <summary>glTF mesh i's extras (as many as the file has).</summary>
    public required MeshExtras[] Meshes { get; init; }

    /// <summary>The JSON of the .glb (or .gltf) at <paramref name="path"/> (an OS path); null when it cannot be read.</summary>
    public static GltfExtras? Read(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            Span<byte> head = stackalloc byte[20];
            f.ReadExactly(head);
            // a .glb: "glTF", version, length; then the JSON chunk: its length, "JSON"
            if (BinaryPrimitives.ReadUInt32LittleEndian(head) != 0x46546C67)
            {
                f.Position = 0;
                using var text = JsonDocument.Parse(f);
                return From(text.RootElement);
            }
            if (BinaryPrimitives.ReadUInt32LittleEndian(head[16..]) != 0x4E4F534A)
                return null;
            int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(head[12..]));
            var buffer = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                f.ReadExactly(buffer, 0, length);
                using var doc = JsonDocument.Parse(buffer.AsMemory(0, length));
                return From(doc.RootElement);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or OverflowException)
        {
            GD.PrintErr($"ffx: cannot read the glTF JSON of {path}: {e.Message}");
            return null;
        }
    }

    private static GltfExtras From(JsonElement root)
    {
        var mats = new List<MaterialExtras>();
        if (root.TryGetProperty("materials", out var mv) && mv.ValueKind == JsonValueKind.Array)
            foreach (var m in mv.EnumerateArray())
                mats.Add(MaterialExtras.From(m));
        var meshes = new List<MeshExtras>();
        if (root.TryGetProperty("meshes", out var ms) && ms.ValueKind == JsonValueKind.Array)
            foreach (var m in ms.EnumerateArray())
                meshes.Add(m.ValueKind == JsonValueKind.Object && m.TryGetProperty("extras", out var x)
                    ? MeshExtras.From(x) : MeshExtras.None);
        return new GltfExtras { Materials = [.. mats], Meshes = [.. meshes] };
    }

    /// <summary>A JSON number as Godot's JSON reads it (a double), else null.</summary>
    internal static double? Number(JsonElement e) => e.ValueKind == JsonValueKind.Number ? e.GetDouble() : null;

    /// <summary>A JSON value as Godot's <c>AsString</c> of its variant would give it for the values the import writes (strings).</summary>
    internal static string? Str(JsonElement e) => e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    /// <summary>The object's string-keyed members (empty for anything else).</summary>
    internal static IEnumerable<JsonProperty> Members(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object ? e.EnumerateObject() : [];

    /// <summary>An array's elements (empty for anything else, as Godot's <c>AsGodotArray</c> of a non-array).</summary>
    internal static IEnumerable<JsonElement> Items(JsonElement e) =>
        e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : [];
}

/// <summary>A material's <c>alphaMode</c> and <c>extras</c> (the import's <c>ffx_*</c> fields).</summary>
public sealed class MaterialExtras
{
    /// <summary><c>alphaMode</c> (null: none).</summary>
    public string? AlphaMode;

    /// <summary><c>extras.ffx_effect</c>: the effect variant (null: none).</summary>
    public string? Effect;

    /// <summary><c>extras.ffx_parameters</c>: each parameter's floats (empty when not an array of numbers).</summary>
    public readonly Dictionary<string, float[]> Parameters = new();

    /// <summary><c>extras.ffx_passes</c> (null: none).</summary>
    public PassStates[]? Passes;

    /// <summary>The names in <c>extras.ffx_switches</c>.</summary>
    public readonly HashSet<string> Switches = new();

    /// <summary><c>extras.ffx_texcoords</c>, <c>ffx_colors</c>: the glTF set each vertex program input reads.</summary>
    public readonly Dictionary<string, int> Texcoords = new(), Colors = new();

    /// <summary><c>extras.ffx_wrap</c>: each sampler's address modes.</summary>
    public readonly Dictionary<string, string[]> Wrap = new();

    /// <summary><c>extras.ffx_textures</c>: each sampler's texture path.</summary>
    public readonly Dictionary<string, string> Textures = new();

    /// <summary>Whether the extras have <c>ffx_textures</c> at all.</summary>
    public bool HasTextures;

    /// <summary><see cref="Parameters"/>' floats of <paramref name="name"/>, or <paramref name="fallback"/> when missing or shorter.</summary>
    public float[] Floats(string name, float[] fallback) =>
        Parameters.TryGetValue(name, out var f) && f.Length >= fallback.Length ? f : fallback;

    internal static MaterialExtras From(JsonElement m)
    {
        var r = new MaterialExtras();
        if (m.ValueKind != JsonValueKind.Object)
            return r;
        if (m.TryGetProperty("alphaMode", out var am))
            r.AlphaMode = GltfExtras.Str(am);
        if (!m.TryGetProperty("extras", out var x) || x.ValueKind != JsonValueKind.Object)
            return r;
        foreach (var p in x.EnumerateObject())
            switch (p.Name)
            {
                case "ffx_effect":
                    r.Effect = GltfExtras.Str(p.Value);
                    break;
                case "ffx_parameters":
                    foreach (var q in GltfExtras.Members(p.Value))
                    {
                        var f = new List<float>();
                        foreach (var v in GltfExtras.Items(q.Value))
                            f.Add((float)(GltfExtras.Number(v) ?? 0));
                        r.Parameters[q.Name] = [.. f];
                    }
                    break;
                case "ffx_passes":
                    var passes = new List<PassStates>();
                    foreach (var v in GltfExtras.Items(p.Value))
                        passes.Add(PassStates.From(v));
                    r.Passes = [.. passes];
                    break;
                case "ffx_switches":
                    foreach (var q in GltfExtras.Members(p.Value))
                        r.Switches.Add(q.Name);
                    break;
                case "ffx_texcoords":
                case "ffx_colors":
                    var sets = p.Name == "ffx_texcoords" ? r.Texcoords : r.Colors;
                    foreach (var q in GltfExtras.Members(p.Value))
                        sets[q.Name] = (int)(GltfExtras.Number(q.Value) ?? 0);
                    break;
                case "ffx_wrap":
                    foreach (var q in GltfExtras.Members(p.Value))
                    {
                        var modes = new List<string>();
                        foreach (var v in GltfExtras.Items(q.Value))
                            if (GltfExtras.Str(v) is { } s)
                                modes.Add(s);
                        r.Wrap[q.Name] = [.. modes];
                    }
                    break;
                case "ffx_textures":
                    r.HasTextures = true;
                    foreach (var q in GltfExtras.Members(p.Value))
                        if (GltfExtras.Str(q.Value) is { } s)
                            r.Textures[q.Name] = s;
                    break;
            }
        return r;
    }
}

/// <summary>A render pass's D3D11 states of an effect variant (one of the import's <c>extras.ffx_passes</c>).</summary>
public sealed class PassStates
{
    /// <summary><c>pass</c>: the pass's name (null: none).</summary>
    public string? Name;

    /// <summary><c>cull</c>, <c>depth_func</c> (null: none, or not a string).</summary>
    public string? Cull, DepthFunc;

    /// <summary><c>front_ccw</c>, <c>depth_write</c>, <c>depth_test</c> (null: none, or not a bool).</summary>
    public bool? FrontCcw, DepthWrite, DepthTest;

    /// <summary><c>blend</c>: render target 0's factors and operation (null: none or not an object); each null when not a string.</summary>
    public (string? Src, string? Dst, string? Op)? Blend;

    /// <summary><c>ps_reads</c>: the constants the pixel program reads (null: none).</summary>
    public string[]? PsReads;

    internal static PassStates From(JsonElement e)
    {
        var r = new PassStates();
        static bool? Bool(JsonElement v) => v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
        foreach (var p in GltfExtras.Members(e))
            switch (p.Name)
            {
                case "pass": r.Name = GltfExtras.Str(p.Value); break;
                case "cull": r.Cull = GltfExtras.Str(p.Value); break;
                case "depth_func": r.DepthFunc = GltfExtras.Str(p.Value); break;
                case "front_ccw": r.FrontCcw = Bool(p.Value); break;
                case "depth_write": r.DepthWrite = Bool(p.Value); break;
                case "depth_test": r.DepthTest = Bool(p.Value); break;
                case "blend" when p.Value.ValueKind == JsonValueKind.Object:
                    string? src = null, dst = null, op = null;
                    foreach (var q in p.Value.EnumerateObject())
                        switch (q.Name)
                        {
                            case "src": src = GltfExtras.Str(q.Value); break;
                            case "dst": dst = GltfExtras.Str(q.Value); break;
                            case "op": op = GltfExtras.Str(q.Value); break;
                        }
                    r.Blend = (src, dst, op);
                    break;
                case "ps_reads":
                    var reads = new List<string>();
                    foreach (var v in GltfExtras.Items(p.Value))
                        if (GltfExtras.Str(v) is { } s)
                            reads.Add(s);
                    r.PsReads = [.. reads];
                    break;
            }
        return r;
    }
}

/// <summary>
/// A mesh's extras from the import (<c>ffx_obj_id</c>, <c>ffx_instance</c>, ...; null: none).
/// <see cref="ClassicLibrary.Load"/> sets them on the Godot mesh as plain integer metadata of the
/// same names (<see cref="Of"/> reads them back), and a mesh without that (a remade model's) by
/// its <c>extras</c> dictionary.
/// </summary>
public readonly record struct MeshExtras(long? ObjId, long? Instance, long? AnimCt, long? MimeCt,
    long? DobjFlag, long? ModelId, long? RenderOrder, long? DobjKind)
{
    public static readonly MeshExtras None = default;

    private static readonly string[] Keys =
        ["ffx_obj_id", "ffx_instance", "ffx_anim_ct", "ffx_mime_ct", "ffx_dobj_flag", "ffx_model_id", "ffx_render_order", "ffx_dobj_kind"];

    /// <summary>The metadata that marks a mesh's extras as set by <see cref="SetOn"/>.</summary>
    private const string Marker = "ffx_extras";

    private long?[] Values() => [ObjId, Instance, AnimCt, MimeCt, DobjFlag, ModelId, RenderOrder, DobjKind];

    private static MeshExtras FromValues(long?[] v) => new(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7]);

    internal static MeshExtras From(JsonElement x)
    {
        var v = new long?[Keys.Length];
        foreach (var p in GltfExtras.Members(x))
        {
            int i = Array.IndexOf(Keys, p.Name);
            // Godot's JSON reads every number as a double; AsInt64 truncates it
            if (i >= 0)
                v[i] = p.Value.ValueKind == JsonValueKind.Null ? 0 : (long)(GltfExtras.Number(p.Value) ?? 0);
        }
        return FromValues(v);
    }

    /// <summary>Sets these on <paramref name="mesh"/> as integer metadata (plain variants: nothing finalizable).</summary>
    public void SetOn(Mesh mesh)
    {
        var v = Values();
        for (int i = 0; i < Keys.Length; i++)
            if (v[i] is { } x)
                mesh.SetMeta(Variants.Name(Keys[i]), x);
        mesh.SetMeta(Variants.Name(Marker), true);
    }

    /// <summary>
    /// <paramref name="mesh"/>'s extras: from <see cref="SetOn"/>'s metadata, else (a mesh not loaded
    /// by <see cref="ClassicLibrary.Load"/>) from Godot's <c>extras</c> dictionary, its variants disposed.
    /// </summary>
    public static MeshExtras Of(Mesh mesh)
    {
        var v = new long?[Keys.Length];
        if (mesh.HasMeta(Variants.Name(Marker)))
        {
            for (int i = 0; i < Keys.Length; i++)
                if (mesh.HasMeta(Variants.Name(Keys[i])))
                    v[i] = mesh.GetMeta(Variants.Name(Keys[i])).AsInt64();
            return FromValues(v);
        }
        if (!mesh.HasMeta(Variants.Name("extras")))
            return None;
        using var meta = mesh.GetMeta(Variants.Name("extras"));
        if (meta.VariantType != Variant.Type.Dictionary)
            return None;
        using var d = meta.AsGodotDictionary();
        for (int i = 0; i < Keys.Length; i++)
            if (d.TryGetValue(Variants.Key(Keys[i]), out var x))
                using (x)
                    v[i] = x.AsInt64();
        return FromValues(v);
    }
}

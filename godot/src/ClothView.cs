using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Godot;

namespace GdFfx;

/// <summary>
/// The characters' simulated hair and cloth (docs/architecture.md, "Cloth"): ffx-rust's cloth
/// capture (<c>ffx_host::cloth</c>, switched on at boot; <c>FFX_NO_CLOTH</c> leaves it off) gives,
/// per character and cloth segment (its PS2 model and piece), each vertex's render-space position
/// (Godot's world) and weight as the game hands them to its draw. The import marks a character's
/// cloth surfaces (<see cref="ClothSurface"/>) and numbers their vertices; <see cref="Prepare"/>
/// gives those surfaces the cloth shader (<c>ClassicMaterials.Cloth</c>). Once an update this
/// writes the previous and the current update's positions and weights into one float texture
/// (<c>ffx_cloth</c>, two texels a vertex) at each mesh's place (its instance's
/// <c>ffx_cloth_base</c>); the shader mixes the skinned position with them by the weight, between
/// the two updates by <c>ffx_cloth_alpha</c> (the bones' interpolation). The frames are copied
/// into kept buffers (<see cref="FfxGame.ClothInto"/>) and nothing finalizable is made an update
/// (docs/running.md, "Garbage collections").
/// </summary>
public sealed class ClothView
{
    /// <summary>Whether the capture is on (<c>FFX_NO_CLOTH</c> turns it off in the extension too).</summary>
    public static readonly bool Enabled = System.Environment.GetEnvironmentVariable("FFX_NO_CLOTH") == null;

    /// <summary>The texture's width in texels (two a vertex).</summary>
    public const int Width = 512;

    /// <summary>The metadata a prepared mesh carries: its cloth surfaces (count), each as two integers.</summary>
    private const string MetaCount = "ffx_cloth", MetaVertices = "ffx_cloth_vertices";

    private static string MetaA(int j) => $"ffx_cloth_{j}a";
    private static string MetaB(int j) => $"ffx_cloth_{j}b";

    /// <summary>The margin added to a cloth mesh's bounds (the cloth can swing outside the skinned box).</summary>
    public const float CullMargin = 8f;

    private static readonly StringName BaseParam = "ffx_cloth_base", G_cloth = "ffx_cloth", G_alpha = "ffx_cloth_alpha";

    // ---- the load (any thread) ------------------------------------------------------

    /// <summary>
    /// A loaded classic scene's cloth surfaces (<paramref name="extras"/>' <see cref="GltfExtras.Cloth"/>):
    /// each glTF mesh with some gets them as integer metadata (plain variants), and each
    /// <see cref="MeshInstance3D"/> showing it the cloth material on those surfaces and a wider
    /// cull margin.
    /// </summary>
    public static void Prepare(GltfState state, GltfExtras extras, Node scene)
    {
        bool any = false;
        foreach (var c in extras.Cloth)
            any |= c.Length > 0;
        if (!any)
            return;
        var meshes = state.GetMeshes();
        for (int i = 0; i < meshes.Count && i < extras.Cloth.Length; i++)
        {
            var cloth = extras.Cloth[i];
            if (cloth.Length == 0)
                continue;
            using var gltf = meshes[i];
            using var importer = gltf.Mesh;
            if (importer?.GetMesh() is not { } mesh)
                continue;
            int vertices = 0;
            for (int j = 0; j < cloth.Length; j++)
            {
                var c = cloth[j];
                mesh.SetMeta(Variants.Name(MetaA(j)), (long)c.Surface | (long)c.Texcoord << 8 | (long)c.Model << 16 | (long)c.Piece << 32);
                mesh.SetMeta(Variants.Name(MetaB(j)), (long)c.First | (long)c.Count << 32);
                vertices = Math.Max(vertices, c.First + c.Count);
            }
            mesh.SetMeta(Variants.Name(MetaCount), cloth.Length);
            mesh.SetMeta(Variants.Name(MetaVertices), vertices);
        }
        void Walk(Node n)
        {
            if (n is MeshInstance3D { Mesh: { } m } mi && Surfaces(m) is { Length: > 0 } list)
            {
                var made = new Dictionary<(Material, int), Material>();
                foreach (var c in list)
                {
                    if (c.Surface >= m.GetSurfaceCount())
                        continue;
                    var src = mi.GetSurfaceOverrideMaterial(c.Surface) ?? m.SurfaceGetMaterial(c.Surface);
                    if (src == null)
                        continue;
                    if (!made.TryGetValue((src, c.Texcoord), out var cm))
                    {
                        if (ClassicMaterials.Cloth(src, c.Texcoord) is not { } fresh)
                            continue;
                        made[(src, c.Texcoord)] = cm = fresh;
                    }
                    mi.SetSurfaceOverrideMaterial(c.Surface, cm);
                }
                mi.ExtraCullMargin = CullMargin;
            }
            // by index: GetChildren makes a Godot array (finalizable) a node
            for (int i = 0, k = n.GetChildCount(); i < k; i++)
                Walk(n.GetChild(i));
        }
        Walk(scene);
    }

    /// <summary>A prepared mesh's cloth surfaces (<see cref="Prepare"/>'s metadata); empty for none.</summary>
    private static ClothSurface[] Surfaces(Mesh mesh)
    {
        if (!mesh.HasMeta(Variants.Name(MetaCount)))
            return [];
        int n = (int)mesh.GetMeta(Variants.Name(MetaCount)).AsInt64();
        var list = new ClothSurface[n];
        for (int j = 0; j < n; j++)
        {
            long a = mesh.GetMeta(Variants.Name(MetaA(j))).AsInt64(), b = mesh.GetMeta(Variants.Name(MetaB(j))).AsInt64();
            list[j] = new ClothSurface((int)(a & 0xff), (int)(a >> 16 & 0xffff), (int)(a >> 32 & 0xffff),
                (int)(b & 0xffffffff), (int)(b >> 32), (int)(a >> 8 & 0xff));
        }
        return list;
    }

    // ---- a character's cloth --------------------------------------------------------

    /// <summary>A shown model's cloth meshes (<see cref="Of"/>).</summary>
    public sealed class Model
    {
        public required Mesh3D[] Meshes;
    }

    /// <summary>
    /// A cloth mesh: its node, its instance in the model (the import's <c>ffx_instance</c>; -1
    /// unknown: any), its cloth vertices in all, its cloth surfaces, the base set last.
    /// </summary>
    public sealed class Mesh3D
    {
        public required MeshInstance3D Node;
        public required int Instance;
        public required int Vertices;
        public required ClothSurface[] Surfaces;
        public float Base = -1f;
    }

    /// <summary>
    /// The cloth meshes of a character's model (null: none), on attaching it (main thread): their
    /// bases back to -1 (a model kept from another character keeps the last one it had). Skinned
    /// models and rigid ones (a weapon's parts: w012's ribbon is its instance 1) alike.
    /// </summary>
    public static Model? Of(Node3D model)
    {
        List<Mesh3D>? list = null;
        void Walk(Node n)
        {
            if (n is MeshInstance3D { Mesh: { } m } mi && Surfaces(m) is { Length: > 0 } s)
            {
                mi.SetInstanceShaderParameter(BaseParam, -1f);
                (list ??= new()).Add(new Mesh3D
                {
                    Node = mi,
                    Instance = MeshExtras.Of(m).Instance is { } inst ? (int)inst : -1,
                    Vertices = (int)m.GetMeta(Variants.Name(MetaVertices)).AsInt64(),
                    Surfaces = s,
                });
            }
            for (int i = 0, k = n.GetChildCount(); i < k; i++)
                Walk(n.GetChild(i));
        }
        Walk(model);
        return list == null ? null : new Model { Meshes = [.. list] };
    }

    // ---- the frames -------------------------------------------------------------------

    /// <summary>A captured segment: where its positions and weights lie in its frame's bytes.</summary>
    private readonly record struct Seg(short Actor, int Instance, int Model, int Piece, int Vertices, int PosAt, int WeightAt);

    /// <summary>One cloth frame (<c>CLTH</c>) copied out of the extension and indexed (buffers kept).</summary>
    private sealed class Frame
    {
        public byte[] Bytes = [];
        public uint Update;
        public bool Valid;
        public readonly List<Seg> Segs = new();

        /// <summary>Indexes <paramref name="len"/> bytes (ffx-rust's docs/state-api.md, "Cloth", version 1); false when they do not read.</summary>
        public bool Index(int len)
        {
            Segs.Clear();
            Valid = false;
            var b = Bytes.AsSpan(0, len);
            if (len < 16 || b[0] != (byte)'C' || b[1] != (byte)'L' || b[2] != (byte)'T' || b[3] != (byte)'H'
                || BinaryPrimitives.ReadUInt16LittleEndian(b[4..]) != 1)
                return false;
            Update = BinaryPrimitives.ReadUInt32LittleEndian(b[8..]);
            uint chars = BinaryPrimitives.ReadUInt32LittleEndian(b[12..]);
            int at = 16;
            for (uint c = 0; c < chars; c++)
            {
                if (at + 8 > len)
                    return false;
                short actor = BinaryPrimitives.ReadInt16LittleEndian(b[at..]);
                int segs = BinaryPrimitives.ReadUInt16LittleEndian(b[(at + 6)..]);
                at += 8;
                for (int s = 0; s < segs; s++)
                {
                    if (at + 80 > len)
                        return false;
                    int instance = BinaryPrimitives.ReadUInt16LittleEndian(b[at..]);
                    int model = BinaryPrimitives.ReadUInt16LittleEndian(b[(at + 4)..]);
                    int piece = BinaryPrimitives.ReadUInt16LittleEndian(b[(at + 6)..]);
                    long n = BinaryPrimitives.ReadUInt32LittleEndian(b[(at + 12)..]);
                    at += 80; // the header and the world (16 floats)
                    if (at + 32 * n > len)
                        return false;
                    int local = at, count = (int)n;
                    Segs.Add(new Seg(actor, instance, model, piece, count, local + 16 * count, local + 28 * count));
                    at += 32 * count;
                }
            }
            Valid = at == len;
            return Valid;
        }

        /// <summary>The segment of <paramref name="actor"/>'s instance <paramref name="instance"/> (-1: any) drawn by a surface (model, piece, vertex count); -1 none.</summary>
        public int Find(int actor, int instance, int model, int piece, int vertices)
        {
            for (int i = 0; i < Segs.Count; i++)
                if (Segs[i] is var s && s.Actor == actor && (instance < 0 || s.Instance == instance)
                    && s.Model == model && s.Piece == piece && s.Vertices == vertices)
                    return i;
            return -1;
        }
    }

    private Frame _a = new(), _b = new();
    private Frame? _cur, _prev;
    private uint _seen = uint.MaxValue;

    /// <summary>Snapshot updates seen while a character had cloth, and those whose cloth frame was not there yet on the first frame showing them.</summary>
    public int Updates { get; private set; }
    public int Late { get; private set; }

    /// <summary>Whether the texture is rebuilt this frame (new frames, or a model came).</summary>
    private bool _dirty;

    /// <summary>Marks the texture for a rebuild (a model with cloth entered the scene).</summary>
    public void Touch() => _dirty = true;

    /// <summary>Whether a shown character has cloth (else nothing is fetched).</summary>
    public bool Wanted { get; set; }

    /// <summary>
    /// The frames of the snapshots shown (current and previous update), fetched when missing (a
    /// frame not captured yet is asked again next frame); sets the shaders' interpolation.
    /// Returns whether the texture is to be rebuilt this frame (<see cref="Place"/>,
    /// <see cref="Upload"/>).
    /// </summary>
    public bool Refresh(FfxGame? game, GameStateView view)
    {
        if (!Enabled || game == null || !Wanted)
            return false;
        var cur = view.Current;
        if (cur == null)
            return false;
        var prev = view.Previous is { } p && !ReferenceEquals(p, cur) ? p : null;
        _cur = Ensure(game, cur.Update, prev?.Update);
        _prev = prev != null ? Ensure(game, prev.Update, cur.Update) : null;
        // the frame of update n is captured in the render after update n, so it can come a little
        // after snapshot n: until it does, the previous update's cloth at alpha 1, where the bones
        // are at the start of the interval (the texture holds it as its pair's newer frame, or
        // twice when rebuilt meanwhile: Place)
        if (cur.Update != _seen)
        {
            _seen = cur.Update;
            Updates++;
            if (_cur == null)
                Late++;
        }
        RenderingServer.GlobalShaderParameterSet(G_alpha, _cur == null ? 1f : view.Alpha);
        bool d = _dirty;
        _dirty = false;
        return d;
    }

    /// <summary>The kept frame of update <paramref name="u"/>, fetched into the slot not holding <paramref name="keep"/> when missing.</summary>
    private Frame? Ensure(FfxGame game, uint u, uint? keep)
    {
        if (_a.Valid && _a.Update == u)
            return _a;
        if (_b.Valid && _b.Update == u)
            return _b;
        var slot = _a.Valid && keep == _a.Update ? _b : _b.Valid && keep == _b.Update ? _a : !_a.Valid ? _a : _b;
        int len = game.ClothInto(u, ref slot.Bytes);
        if (len <= 0 || !slot.Index(len) || slot.Update != u)
        {
            slot.Valid = false;
            return null;
        }
        _dirty = true;
        Fetched++;
        return slot;
    }

    // ---- the texture --------------------------------------------------------------------

    private Image? _image;
    private ImageTexture? _texture;
    private byte[] _bytes = [];
    private int _rows;
    /// <summary>The vertex numbers placed so far in this rebuild.</summary>
    private int _next;

    /// <summary>What the last rebuild placed (for the checks and the overlay).</summary>
    public readonly record struct Stats(int Characters, int Segments, int Vertices, int Weighted, int Moving, float MostMoved, int Unmatched, int OnWeapons)
    {
        public override string ToString() =>
            $"{Characters} character(s), {Segments} segment(s) ({OnWeapons} on weapons), {Vertices} vertices ({Weighted} simulated, {Moving} moving relative to their character, at most {MostMoved:F3} units an update), {Unmatched} segment(s) without a surface";
    }

    public Stats Shown { get; private set; }

    /// <summary>Rebuilds so far whose simulated vertices moved relative to their character, and frames fetched.</summary>
    public int MovingUpdates { get; private set; }
    public int Fetched { get; private set; }

    /// <summary>Segments of a shown character's frame matched by no surface of its model, once each (logged).</summary>
    private readonly HashSet<(string, int, int, int, int)> _unmatchedLogged = new();

    private int _chars, _segs, _verts, _weighted, _moving, _unmatched, _weapons;
    private float _most;

    /// <summary>Starts a rebuild (before the characters' <see cref="Place"/>).</summary>
    public void Begin()
    {
        _next = 0;
        _chars = _segs = _verts = _weighted = _moving = _unmatched = _weapons = 0;
        _most = 0;
    }

    private Span<float> Texels => MemoryMarshal.Cast<byte, float>(_bytes.AsSpan());

    /// <summary>Room for <paramref name="vertices"/> placed vertices (grown by whole rows, the texture made again).</summary>
    private void Room(int vertices)
    {
        int rows = Math.Max(1, (2 * vertices + Width - 1) / Width);
        if (rows <= _rows)
            return;
        int grown = Math.Max(rows, _rows * 2);
        var old = _bytes;
        _bytes = new byte[Width * grown * 16];
        old.AsSpan().CopyTo(_bytes);
        _rows = grown;
    }

    /// <summary>
    /// Places character <paramref name="slot"/>'s (model <paramref name="name"/>) cloth meshes for this
    /// rebuild: each mesh with a surface in the current frame gets its range of texels (the previous
    /// frame's positions, or the current ones again where it has none; weight 0 where no segment
    /// matched) and its base; the others -1 (skinned only). <paramref name="root"/>,
    /// <paramref name="prevRoot"/>: the character's root in the two snapshots (the movement check);
    /// <paramref name="weapon"/>: a weapon (kind 4, counted apart).
    /// </summary>
    public void Place(ushort slot, string name, Model model, Transform3D root, Transform3D? prevRoot, bool weapon = false)
    {
        // the current update's frame not there yet: the previous one alone (Refresh)
        var cur = _cur ?? _prev;
        var prevFrame = _cur != null ? _prev : null;
        bool any = false;
        foreach (var m in model.Meshes)
        {
            float b = -1f;
            if (cur != null && GodotObject.IsInstanceValid(m.Node))
            {
                int start = _next;
                bool placed = false;
                foreach (var s in m.Surfaces)
                {
                    int ci = cur.Find(slot, m.Instance, s.Model, s.Piece, s.Count);
                    if (ci < 0)
                        continue;
                    if (!placed)
                    {
                        placed = true;
                        Room(start + m.Vertices);
                        Texels.Slice(8 * start, 8 * m.Vertices).Clear();
                        _next = start + m.Vertices;
                    }
                    int pi = prevFrame?.Find(slot, m.Instance, s.Model, s.Piece, s.Count) ?? -1;
                    Write(cur, cur.Segs[ci], pi >= 0 ? prevFrame : null, pi >= 0 ? prevFrame!.Segs[pi] : default, start + s.First, root, prevRoot);
                    _segs++;
                    if (weapon)
                        _weapons++;
                }
                if (placed)
                {
                    b = start;
                    any = true;
                }
            }
            if (m.Base != b && GodotObject.IsInstanceValid(m.Node))
            {
                m.Base = b;
                m.Node.SetInstanceShaderParameter(BaseParam, b);
            }
        }
        if (any)
            _chars++;
        // the frame's segments of this character that found no surface (logged once a kind)
        if (cur != null)
            foreach (var s in cur.Segs)
                if (s.Actor == slot && !Matches(model, s) && _unmatchedLogged.Add((name, s.Instance, s.Model, s.Piece, s.Vertices)))
                {
                    _unmatched++;
                    GD.PrintErr($"ffx cloth: {name}#{slot}: segment of PS2 model {s.Model} piece {s.Piece} ({s.Vertices} vertices, instance {s.Instance}) matches no surface");
                }
    }

    private static bool Matches(Model model, Seg s)
    {
        foreach (var m in model.Meshes)
            if (m.Instance < 0 || m.Instance == s.Instance)
                foreach (var c in m.Surfaces)
                    if (c.Model == s.Model && c.Piece == s.Piece && c.Count == s.Vertices)
                        return true;
        return false;
    }

    /// <summary>One segment's vertices from vertex number <paramref name="first"/>: previous (or current) and current position and weight.</summary>
    private void Write(Frame cur, Seg c, Frame? prev, Seg p, int first, Transform3D root, Transform3D? prevRoot)
    {
        var t = Texels;
        var cb = cur.Bytes.AsSpan();
        var cpos = MemoryMarshal.Cast<byte, float>(cb.Slice(c.PosAt, 12 * c.Vertices));
        var cw = MemoryMarshal.Cast<byte, float>(cb.Slice(c.WeightAt, 4 * c.Vertices));
        ReadOnlySpan<float> ppos = default, pw = default;
        if (prev != null)
        {
            var pb = prev.Bytes.AsSpan();
            ppos = MemoryMarshal.Cast<byte, float>(pb.Slice(p.PosAt, 12 * p.Vertices));
            pw = MemoryMarshal.Cast<byte, float>(pb.Slice(p.WeightAt, 4 * p.Vertices));
        }
        var inv = root.AffineInverse();
        var prevInv = prevRoot?.AffineInverse();
        for (int k = 0; k < c.Vertices; k++)
        {
            var now = new Vector3(cpos[3 * k], cpos[3 * k + 1], cpos[3 * k + 2]);
            float w = cw[k];
            var before = prev != null ? new Vector3(ppos[3 * k], ppos[3 * k + 1], ppos[3 * k + 2]) : now;
            float wb = prev != null ? pw[k] : w;
            if (!now.IsFinite() || !before.IsFinite())
                continue; // weight 0: skinned
            int i = 8 * (first + k);
            t[i] = before.X; t[i + 1] = before.Y; t[i + 2] = before.Z; t[i + 3] = wb;
            t[i + 4] = now.X; t[i + 5] = now.Y; t[i + 6] = now.Z; t[i + 7] = w;
            _verts++;
            if (w <= 0)
                continue;
            _weighted++;
            if (prev != null && prevInv is { } pi && pw[k] > 0)
            {
                float d = (inv * now).DistanceTo(pi * before);
                if (d > 1e-4f)
                    _moving++;
                _most = Math.Max(_most, d);
            }
        }
    }

    /// <summary>Ends a rebuild: the texels go to the texture (made, or made again larger, here).</summary>
    public void Upload()
    {
        Shown = new Stats(_chars, _segs, _verts, _weighted, _moving, _most, _unmatched, _weapons);
        if (_moving > 0)
            MovingUpdates++;
        if (_next == 0 || _rows == 0)
            return;
        if (_image == null || _texture == null || _image.GetHeight() != _rows)
        {
            _image = Image.CreateFromData(Width, _rows, false, Image.Format.Rgbaf, _bytes);
            if (_texture == null)
                _texture = ImageTexture.CreateFromImage(_image);
            else
                _texture.SetImage(_image);
            using var v = Variant.CreateFrom(_texture);
            RenderingServer.GlobalShaderParameterSet(G_cloth, v);
            return;
        }
        _image.SetData(Width, _rows, false, Image.Format.Rgbaf, _bytes);
        _texture.Update(_image);
    }
}

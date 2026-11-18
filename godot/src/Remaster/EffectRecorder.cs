using System.Collections.Generic;
using Godot;

namespace GdFfx.Remaster;

/// <summary>
/// The classic effects as the game showed them, for the editor (R7; docs/remaster-editor.md,
/// "Effects"): like R5's capture, the game writes each effect id it drew once a run into
/// <c>user://remaster/effects/</c> (<c>--remaster-effects=DIR</c>; a check writes only there):
/// <c>&lt;id&gt;.fxrec</c>, its draw list over its first <see cref="MaxFrames"/> updates (positions
/// relative to its place, colours, uvs, triangles, texture names, the look each is drawn with),
/// and <c>index.json</c> (every id with what it is, where and when it was seen, its frames, its
/// textures). The editor replays it next to a remake (<see cref="Read"/>). The player's, never in
/// git: it holds the game's geometry.
/// </summary>
public static class EffectRecorder
{
    /// <summary>Updates recorded per effect: 4 s.</summary>
    public const int MaxFrames = 120;

    /// <summary>Vertices recorded per effect at most (about 15 MB: a large effect is cut short).</summary>
    public const int MaxVertices = 400_000;

    public static string Dir => GameSettings.Arg("remaster-effects") is { Length: > 0 } d ? d.TrimEnd('/') : DirOverride ?? "user://remaster/effects";

    public static string? DirOverride { get; set; }

    /// <summary>A check writes only into an explicit folder, never the player's.</summary>
    public static bool Writes => !UserSettings.IsCheck() || GameSettings.Arg("remaster-effects") is { Length: > 0 } || DirOverride != null;

    public static string PathOf(string id) => $"{Dir}/{id}.fxrec";

    /// <summary>One recorded draw.</summary>
    public sealed class Draw
    {
        public string Texture = "";
        public EffectDrawView.Look Look;
        public Vector3[] Positions = [];
        public Color[] Colours = [];
        public Vector2[] Uvs = [];
        public int[] Indices = [];
    }

    /// <summary>A recording: the effect's place (render space) and level, its frames' draws.</summary>
    public sealed class Recording
    {
        public string Id = "", Level = "";
        public Vector3 Place;
        public readonly List<List<Draw>> Frames = new();
        /// <summary>Vertices recorded so far.</summary>
        public int Vertices;
        /// <summary>Each frame's place while recording (the place written is their median; positions are kept relative to it).</summary>
        public readonly List<Vector3> Places = new();
    }

    private static readonly Dictionary<string, Recording> Open = new();
    private static readonly HashSet<string> Done = new();
    private static readonly Dictionary<string, int> LastSeenFrame = new();
    private static int _frame;

    /// <summary>Ids written this run (for tests).</summary>
    public static IReadOnlyCollection<string> Written => Done;

    /// <summary>Whether effect <paramref name="id"/> was written this run.</summary>
    public static bool IsWritten(string id) => Done.Contains(id);

    /// <summary>A frame of draws with their ids (<see cref="EffectRemakeView.Frame"/>).</summary>
    public static void Frame(string level, List<EffectDrawView.Draw> draws, EffectDrawView.Geometry geo, string?[] ids, Dictionary<string, Vector3> places)
    {
        if (!Writes)
            return;
        _frame++;
        foreach (var (id, place) in places)
        {
            if (Done.Contains(id))
                continue;
            if (!Open.TryGetValue(id, out var rec))
                Open[id] = rec = new Recording { Id = id, Level = PartIds.LevelKey(level) };
            var list = new List<Draw>();
            for (int i = 0; i < draws.Count; i++)
            {
                if (ids[i] != id)
                    continue;
                var d = draws[i];
                if (d.Vertices == 0 || d.IndexCount == 0 || !EffectDrawView.Drawn(d.Pass))
                    continue;
                var look = EffectDrawView.LookOf(d);
                if (look.Skip)
                    continue;
                var r = new Draw { Texture = look.Textured ? d.Texture : "", Look = look };
                r.Positions = new Vector3[d.Vertices];
                r.Colours = new Color[d.Vertices];
                r.Uvs = new Vector2[d.Vertices];
                for (int k = 0; k < d.Vertices; k++)
                {
                    r.Positions[k] = geo.Positions[d.First + k];
                    r.Colours[k] = geo.Colours[d.First + k];
                    r.Uvs[k] = geo.Uvs[d.First + k];
                }
                var tris = new List<int>(d.IndexCount);
                for (int k = 0; k + 2 < d.IndexCount; k += 3)
                {
                    int a = geo.Indices[d.IndexFirst + k], b = geo.Indices[d.IndexFirst + k + 1], c = geo.Indices[d.IndexFirst + k + 2];
                    if (a >= d.Vertices || b >= d.Vertices || c >= d.Vertices || a == b || b == c || a == c)
                        continue;
                    if (EffectDrawView.Unset(r.Colours[a]) || EffectDrawView.Unset(r.Colours[b]) || EffectDrawView.Unset(r.Colours[c]))
                        continue;
                    tris.Add(a);
                    tris.Add(b);
                    tris.Add(c);
                }
                r.Indices = tris.ToArray();
                if (r.Indices.Length > 0)
                    list.Add(r);
            }
            rec.Frames.Add(list);
            rec.Places.Add(place);
            foreach (var d in list)
                rec.Vertices += d.Positions.Length;
            LastSeenFrame[id] = _frame;
            if (rec.Frames.Count >= MaxFrames || rec.Vertices >= MaxVertices)
                Finish(id);
        }
        // effects that ended (no draw for a few updates) are written as they are
        List<string>? ended = null;
        foreach (var id in Open.Keys)
            if (_frame - LastSeenFrame.GetValueOrDefault(id) > 8)
                (ended ??= new()).Add(id);
        if (ended != null)
            foreach (var id in ended)
                Finish(id);
    }

    /// <summary>Writes every open recording (the game quits).</summary>
    public static void FlushAll()
    {
        foreach (var id in new List<string>(Open.Keys))
            Finish(id);
    }

    private static void Finish(string id)
    {
        if (!Open.Remove(id, out var rec) || rec.Frames.Count == 0)
            return;
        Done.Add(id);
        // the place: the median of the frames' places (the first frames of a map can lack it)
        if (rec.Places.Count > 0)
        {
            static float Median(List<float> v) { v.Sort(); return v[v.Count / 2]; }
            rec.Place = new Vector3(Median(rec.Places.ConvertAll(p => p.X)), Median(rec.Places.ConvertAll(p => p.Y)), Median(rec.Places.ConvertAll(p => p.Z)));
        }
        foreach (var fr in rec.Frames)
            foreach (var d in fr)
                for (int k = 0; k < d.Positions.Length; k++)
                    d.Positions[k] -= rec.Place;
        try
        {
            DirAccess.MakeDirRecursiveAbsolute(GameSettings.Globalize(Dir));
            Write(rec, PathOf(id));
            UpdateIndex(rec);
        }
        catch (System.Exception e)
        {
            GD.PrintErr($"ffx: effects: cannot record {id}: {e.Message}");
        }
    }

    private const uint Magic = 0x43525846; // "FXRC"

    /// <summary>Writes <paramref name="rec"/> to <paramref name="path"/>.</summary>
    public static void Write(Recording rec, string path)
    {
        using var f = FileAccess.Open(path, FileAccess.ModeFlags.Write) ?? throw new System.IO.IOException($"cannot write {path}");
        f.Store32(Magic);
        f.Store16(1);
        f.Store16(0);
        f.StorePascalString(rec.Id);
        f.StorePascalString(rec.Level);
        f.StoreFloat(rec.Place.X);
        f.StoreFloat(rec.Place.Y);
        f.StoreFloat(rec.Place.Z);
        f.Store32((uint)rec.Frames.Count);
        foreach (var frame in rec.Frames)
        {
            f.Store32((uint)frame.Count);
            foreach (var d in frame)
            {
                var l = d.Look;
                f.StorePascalString(d.Texture);
                f.Store8((byte)l.Blend);
                f.Store8((byte)l.Form);
                f.Store8((byte)((l.AlphaOne ? 1 : 0) | (l.ZeroColour ? 2 : 0) | (l.Textured ? 4 : 0) | (l.NoDepthTest ? 8 : 0) | (l.DepthWrite ? 16 : 0)));
                f.Store8((byte)l.AddressU);
                f.Store8((byte)l.AddressV);
                f.Store32((uint)d.Positions.Length);
                f.Store32((uint)d.Indices.Length);
                foreach (var p in d.Positions)
                {
                    f.StoreFloat(p.X);
                    f.StoreFloat(p.Y);
                    f.StoreFloat(p.Z);
                }
                foreach (var c in d.Colours)
                {
                    f.StoreFloat(c.R);
                    f.StoreFloat(c.G);
                    f.StoreFloat(c.B);
                    f.StoreFloat(c.A);
                }
                foreach (var u in d.Uvs)
                {
                    f.StoreFloat(u.X);
                    f.StoreFloat(u.Y);
                }
                foreach (var i in d.Indices)
                    f.Store16((ushort)i);
            }
        }
    }

    /// <summary>Reads a recording; null when it is not one.</summary>
    public static Recording? Read(string path)
    {
        using var f = FileAccess.Open(path, FileAccess.ModeFlags.Read);
        if (f == null || f.GetLength() < 16 || f.Get32() != Magic || f.Get16() != 1)
            return null;
        f.Get16();
        var rec = new Recording { Id = f.GetPascalString(), Level = f.GetPascalString() };
        rec.Place = new Vector3(f.GetFloat(), f.GetFloat(), f.GetFloat());
        uint frames = f.Get32();
        for (uint fi = 0; fi < frames && !f.EofReached(); fi++)
        {
            uint n = f.Get32();
            var list = new List<Draw>((int)System.Math.Min(n, 4096));
            for (uint di = 0; di < n; di++)
            {
                var d = new Draw { Texture = f.GetPascalString() };
                int blend = f.Get8(), form = f.Get8(), bits = f.Get8(), au = f.Get8(), av = f.Get8();
                d.Look = new EffectDrawView.Look(blend, form, (bits & 1) != 0, (bits & 2) != 0, false)
                {
                    Textured = (bits & 4) != 0,
                    NoDepthTest = (bits & 8) != 0,
                    DepthWrite = (bits & 16) != 0,
                    AddressU = au,
                    AddressV = av,
                };
                int v = (int)f.Get32(), ic = (int)f.Get32();
                if (v > 1 << 20 || ic > 1 << 22)
                    return null;
                d.Positions = new Vector3[v];
                d.Colours = new Color[v];
                d.Uvs = new Vector2[v];
                d.Indices = new int[ic];
                for (int k = 0; k < v; k++)
                    d.Positions[k] = new Vector3(f.GetFloat(), f.GetFloat(), f.GetFloat());
                for (int k = 0; k < v; k++)
                    d.Colours[k] = new Color(f.GetFloat(), f.GetFloat(), f.GetFloat(), f.GetFloat());
                for (int k = 0; k < v; k++)
                    d.Uvs[k] = new Vector2(f.GetFloat(), f.GetFloat());
                for (int k = 0; k < ic; k++)
                    d.Indices[k] = f.Get16();
                list.Add(d);
            }
            rec.Frames.Add(list);
        }
        return rec;
    }

    public static string IndexPath => $"{Dir}/index.json";

    /// <summary>The index: id -> its entry (kind, level, describe, frames, draws, textures, seen).</summary>
    public static Godot.Collections.Dictionary ReadIndex(string? dir = null)
    {
        var path = dir == null ? IndexPath : $"{dir.TrimEnd('/')}/index.json";
        if (!FileAccess.FileExists(path))
            return new();
        return Json.ParseString(FileAccess.GetFileAsString(path)).AsGodotDictionary() ?? new();
    }

    private static void UpdateIndex(Recording rec)
    {
        var index = ReadIndex();
        var textures = new Godot.Collections.Array();
        var seenTex = new HashSet<string>();
        int maxDraws = 0;
        foreach (var fr in rec.Frames)
        {
            maxDraws = System.Math.Max(maxDraws, fr.Count);
            foreach (var d in fr)
                if (d.Texture.Length > 0 && seenTex.Add(d.Texture))
                    textures.Add(d.Texture);
        }
        index[rec.Id] = new Godot.Collections.Dictionary
        {
            ["kind"] = rec.Id.StartsWith("magic-") ? "magic" : "field",
            ["level"] = rec.Level,
            ["what"] = EffectOwners.Describe(rec.Id),
            ["frames"] = rec.Frames.Count,
            ["draws"] = maxDraws,
            ["textures"] = textures,
            ["place"] = new Godot.Collections.Array { rec.Place.X, rec.Place.Y, rec.Place.Z },
            ["seen"] = Time.GetDatetimeStringFromSystem(true),
        };
        using var f = FileAccess.Open(IndexPath, FileAccess.ModeFlags.Write);
        f?.StoreString(Json.Stringify(index, "  ", true));
    }
}

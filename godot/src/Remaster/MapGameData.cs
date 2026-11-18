using System.Collections.Generic;
using System.Text.Json;
using Godot;

namespace GdFfx.Remaster;

/// <summary>
/// A walk-mesh page of a map (docs/remaster-editor.md, "The game's data"; ffx-rust's
/// docs/formats/field.md, "The walk mesh on disk"): the triangles the game's characters stand
/// on, in render space, with each triangle's map id word.
/// </summary>
public sealed class WalkMesh
{
    /// <summary>The page (<c>yiSetIDPage</c>; 0 is the one a map starts with, scripts switch to others; 7: section 9's mesh).</summary>
    public required int Page { get; init; }

    /// <summary>The <c>MAP1</c> file it comes from (an archive path).</summary>
    public required string File { get; init; }

    public required Vector3[] Vertices { get; init; }

    /// <summary>Three vertex numbers a triangle.</summary>
    public required int[] Triangles { get; init; }

    /// <summary>Three neighbours a triangle (across the edges 0-1, 1-2, 2-0; -1: none).</summary>
    public required int[] Neighbours { get; init; }

    /// <summary>The map id word a triangle (chara.md, "The map id word").</summary>
    public required uint[] Words { get; init; }

    public int Count => Words.Length;

    /// <summary>
    /// A triangle's kind (bits 0-6 of its word; <c>Sg_IdMatchChr</c>): 0 open, 1 closed to
    /// everyone, 2 to all but the player, 13 to chocobos, 14 to the player; 0x30..0x3f as a
    /// table the event scripts set (an event's switch).
    /// </summary>
    public int Kind(int t) => (int)(Words[t] & 0x7f);

    /// <summary>The encounter bits (7-8).</summary>
    public int Encounter(int t) => (int)((Words[t] >> 7) & 3);

    public (Vector3 A, Vector3 B, Vector3 C) Corners(int t) =>
        (Vertices[Triangles[3 * t]], Vertices[Triangles[3 * t + 1]], Vertices[Triangles[3 * t + 2]]);

    public Vector3 Centre(int t)
    {
        var (a, b, c) = Corners(t);
        return (a + b + c) / 3f;
    }

    /// <summary>What a kind means, in words.</summary>
    public static string KindName(int kind) => kind switch
    {
        0 => "open",
        1 => "closed to everyone",
        2 => "closed to all but the player",
        13 => "closed to chocobos",
        14 => "closed to the player",
        >= 0x30 and <= 0x3f => $"switched by the scripts (0x{kind:x2})",
        _ => $"kind {kind} (blocks no one)",
    };

    /// <summary>Whether the player may stand on a triangle of this kind (the switched ones as the map starts: open).</summary>
    public static bool Walkable(int kind) => kind is not (1 or 14);

    /// <summary>
    /// The height of the walk mesh under (or over) <paramref name="p"/>: the triangle whose
    /// x-z projection holds the point and whose height is nearest to <paramref name="p"/>'s;
    /// -1 when none holds it.
    /// </summary>
    public int Under(Vector3 p, out float height)
    {
        height = float.NaN;
        int best = -1;
        float bestD = float.MaxValue;
        for (int t = 0; t < Count; t++)
        {
            var (a, b, c) = Corners(t);
            if (HeightIn(a, b, c, p.X, p.Z) is not { } y)
                continue;
            var d = Mathf.Abs(y - p.Y);
            if (d < bestD)
            {
                bestD = d;
                best = t;
                height = y;
            }
        }
        return best;
    }

    /// <summary>The height of triangle <c>a b c</c> at (x, z) when its x-z projection holds the point (edges included).</summary>
    public static float? HeightIn(Vector3 a, Vector3 b, Vector3 c, float x, float z)
    {
        float d = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
        if (Mathf.Abs(d) < 1e-9f)
            return null;
        float u = ((b.Z - c.Z) * (x - c.X) + (c.X - b.X) * (z - c.Z)) / d;
        float v = ((c.Z - a.Z) * (x - c.X) + (a.X - c.X) * (z - c.Z)) / d;
        float w = 1f - u - v;
        const float e = -1e-5f;
        if (u < e || v < e || w < e)
            return null;
        return u * a.Y + v * b.Y + w * c.Y;
    }
}

/// <summary>An entrance of a map's event script: where the player arrives by that entrance number.</summary>
public sealed record GameEntrance(int Index, int MapIndex, int Camera, float Yaw, Vector3 Position);

/// <summary>A map change in a worker's code with constant arguments: the target map number and entrance, the entry it is in.</summary>
public sealed record GameExit(int Map, int Entrance, int Entry);

/// <summary>
/// A worker of a map's event script as its code sets it up with constant arguments
/// (ffx-rust's docs/formats/script.md, "Worker types", "Natives"): its type, position, model,
/// trigger line or box, the map group or display object it binds, the maps it changes to.
/// Positions are in render space; null when the script does not set them with constants.
/// </summary>
public sealed class GameWorker
{
    public required string Event { get; init; }
    public required int Index { get; init; }

    /// <summary>0 function, 1 actor, 2 line trigger, 3 box trigger, 4 logic, 5 line sensor, 6 box sensor.</summary>
    public required int Type { get; init; }

    public Vector3? Position { get; init; }
    public float? Yaw { get; init; }
    public string? Model { get; init; }

    /// <summary>A line trigger's (sensor's) ends, and how far they move out (back, front: <c>LineExtend</c>).</summary>
    public (Vector3 A, Vector3 B)? Line { get; init; }
    public Vector2? LineExtend { get; init; }

    /// <summary>A box trigger's (sensor's) centre, half extents (x, z, height: 0 or less, any height) and turn.</summary>
    public Vector3? Point { get; init; }
    public Vector3? Box { get; init; }
    public float BoxYaw { get; init; }

    public float? TalkRange { get; init; }
    public float? TouchRange { get; init; }
    public int? Group { get; init; }
    public int? Dobj { get; init; }
    public IReadOnlyList<int> Triggers { get; init; } = [];
    public IReadOnlyList<GameExit> Exits { get; init; } = [];

    public bool IsTrigger => Type is 2 or 3 or 5 or 6;

    public static string TypeName(int type) => type switch
    {
        0 => "function",
        1 => "actor",
        2 => "line trigger",
        3 => "box trigger",
        4 => "logic",
        5 => "line sensor",
        6 => "box sensor",
        _ => $"type {type}",
    };

    /// <summary>The line's ends moved out by <see cref="LineExtend"/> (back past A, front past B), as the game extends it.</summary>
    public (Vector3 A, Vector3 B)? ExtendedLine()
    {
        if (Line is not { } l)
            return null;
        var d = l.B - l.A;
        if (LineExtend is not { } e || d.LengthSquared() < 1e-6f)
            return l;
        d = d.Normalized();
        return (l.A - d * e.X, l.B + d * e.Y);
    }

    /// <summary>
    /// The box as a transform of the unit cube (render space): centred on <see cref="Point"/>,
    /// its axes the game's (the trigger's x and z turned by its yaw; field space mirrors x and
    /// y), sized by twice its half extents (a box of any height drawn <paramref name="anyHeight"/> high).
    /// </summary>
    public Transform3D? BoxTransform(float anyHeight)
    {
        if (Point is not { } p || Box is not { } b)
            return null;
        float c = Mathf.Cos(BoxYaw), s = Mathf.Sin(BoxYaw);
        // field: local x along (c, 0, s), local z along (-s, 0, c); render mirrors x (and y)
        var u = new Vector3(-c, 0, s);
        var w = -new Vector3(s, 0, c);
        float h = b.Z > 0 ? 2 * b.Z : anyHeight;
        var basis = new Basis(u * (2 * b.X), Vector3.Up * h, w * (2 * b.Y));
        return new Transform3D(basis, p);
    }
}

/// <summary>A <c>YNSC</c> scene's camera block: a 2D-layer map's fixed camera, in render space.</summary>
public sealed record SceneCamera(int Scene, int Layers, Vector3 Eye, Vector3 Target, Vector3 Up, float FovDegrees, int YOffset, string File)
{
    /// <summary>Whether the block sets a camera (a field of view; 3D maps mostly have a zeroed one).</summary>
    public bool Sets => FovDegrees > 0 && Eye != Target;

    /// <summary>
    /// The game's vertical field of view from the block's (<c>rcbgSceneSetMatrix</c>:
    /// <c>2 atan(0.75 tan(fov / 2))</c>; ffx-rust's docs/formats/scene-maps.md).
    /// </summary>
    public float VerticalFovDegrees => Mathf.RadToDeg(2f * Mathf.Atan(0.75f * Mathf.Tan(Mathf.DegToRad(FovDegrees) / 2f)));
}

/// <summary>One event of a level with its script's entrances and workers.</summary>
public sealed record GameEvent(string Name, int Map, string? MapFile, IReadOnlyList<GameEntrance> Entrances, IReadOnlyList<GameWorker> Workers, int? CameraWorker);

/// <summary>
/// A map's game data, read from the player's archive without running the game
/// (<see cref="FfxGame.ClassicMapGameData"/>; docs/remaster-editor.md, "The game's data"): the
/// walk meshes, the 2D scenes' cameras and, per event of the level, its entrances and workers.
/// Everything in render space (field <c>(x, y, z)</c> is <c>(-x, -y, z)</c>; the scene cameras'
/// blocks are a tenth of field units).
/// </summary>
public sealed class MapGameData
{
    public required string Level { get; init; }
    public required string Key { get; init; }
    public required IReadOnlyList<WalkMesh> WalkMeshes { get; init; }
    public required IReadOnlyList<SceneCamera> Cameras { get; init; }
    public required IReadOnlyList<GameEvent> Events { get; init; }

    /// <summary>The walk mesh the map starts with (page 0 of the first event's file), else the first; null: none.</summary>
    public WalkMesh? Main
    {
        get
        {
            var file = Events.Count > 0 ? Events[0].MapFile : null;
            foreach (var m in WalkMeshes)
                if (m.Page == 0 && (file == null || m.File == file))
                    return m;
            return WalkMeshes.Count > 0 ? WalkMeshes[0] : null;
        }
    }

    public IEnumerable<GameWorker> Workers()
    {
        foreach (var e in Events)
            foreach (var w in e.Workers)
                yield return w;
    }

    public static Vector3 FieldPoint(float x, float y, float z) => new(-x, -y, z);

    /// <summary>
    /// The way a character with the game's facing <paramref name="yaw"/> (radians: an entrance's,
    /// a worker's <c>SetYaw</c>, <c>Actor.yaw</c>) looks, in render space: field
    /// <c>(cos yaw, 0, sin yaw)</c> (the check walks the player and compares).
    /// </summary>
    public static Vector3 Facing(float yaw) => FieldPoint(Mathf.Cos(yaw), 0, Mathf.Sin(yaw));

    /// <summary><paramref name="level"/>'s data (main thread, parsing only); null without the archive or the level.</summary>
    public static MapGameData? Read(FfxGame game, string level)
    {
        var json = game.ClassicMapGameData(level);
        return json.Length == 0 ? null : Parse(json);
    }

    public static MapGameData? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var walk = new List<WalkMesh>();
            var cameras = new List<SceneCamera>();
            if (r.TryGetProperty("mapFiles", out var files) && files.ValueKind == JsonValueKind.Object)
                foreach (var f in files.EnumerateObject())
                {
                    foreach (var m in f.Value.GetProperty("walkMeshes").EnumerateArray())
                        walk.Add(ReadWalk(f.Name, m));
                    foreach (var c in f.Value.GetProperty("cameras").EnumerateArray())
                        cameras.Add(new SceneCamera(
                            c.GetProperty("scene").GetInt32(), c.GetProperty("layers").GetInt32(),
                            Camera(c.GetProperty("eye")), Camera(c.GetProperty("target")), Up(c.GetProperty("up")),
                            c.GetProperty("fov").GetSingle(), c.GetProperty("yOffset").GetInt32(), f.Name));
                }
            var events = new List<GameEvent>();
            foreach (var e in r.GetProperty("events").EnumerateArray())
            {
                var name = e.GetProperty("name").GetString() ?? "";
                var entrances = new List<GameEntrance>();
                var workers = new List<GameWorker>();
                int? camera = null;
                if (e.GetProperty("script") is { ValueKind: JsonValueKind.Object } s)
                {
                    if (s.GetProperty("cameraWorker") is { ValueKind: JsonValueKind.Number } cw)
                        camera = cw.GetInt32();
                    foreach (var x in s.GetProperty("entrances").EnumerateArray())
                        entrances.Add(new GameEntrance(x.GetProperty("index").GetInt32(), x.GetProperty("mapIndex").GetInt32(),
                            x.GetProperty("camera").GetInt32(), x.GetProperty("yaw").GetSingle(), Point(x.GetProperty("position"))));
                    foreach (var w in s.GetProperty("workers").EnumerateArray())
                        workers.Add(ReadWorker(name, w));
                }
                events.Add(new GameEvent(name, e.GetProperty("map").GetInt32(),
                    e.GetProperty("mapFile") is { ValueKind: JsonValueKind.String } mf ? mf.GetString() : null,
                    entrances, workers, camera));
            }
            return new MapGameData
            {
                Level = r.GetProperty("level").GetString() ?? "",
                Key = r.GetProperty("key").GetString() ?? "",
                WalkMeshes = walk,
                Cameras = cameras,
                Events = events,
            };
        }
        catch (System.Exception e) when (e is JsonException or System.InvalidOperationException or KeyNotFoundException)
        {
            GD.PrintErr($"ffx: game data: not readable ({e.Message})");
            return null;
        }
    }

    private static Vector3 Point(JsonElement e) => FieldPoint(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle());

    /// <summary>A scene camera's point: a tenth of field units (the scene's scale of 10), then render space.</summary>
    private static Vector3 Camera(JsonElement e) => Point(e) * 10f;

    private static Vector3 Up(JsonElement e) => FieldPoint(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle());

    private static WalkMesh ReadWalk(string file, JsonElement m)
    {
        var v = m.GetProperty("vertices");
        var verts = new Vector3[v.GetArrayLength() / 3];
        for (int i = 0; i < verts.Length; i++)
            verts[i] = FieldPoint(v[3 * i].GetSingle(), v[3 * i + 1].GetSingle(), v[3 * i + 2].GetSingle());
        var t = m.GetProperty("triangles");
        int n = t.GetArrayLength() / 7;
        var tris = new int[3 * n];
        var nb = new int[3 * n];
        var words = new uint[n];
        for (int i = 0; i < n; i++)
        {
            for (int k = 0; k < 3; k++)
            {
                tris[3 * i + k] = t[7 * i + k].GetInt32();
                nb[3 * i + k] = t[7 * i + 3 + k].GetInt32();
            }
            words[i] = (uint)t[7 * i + 6].GetInt64();
        }
        return new WalkMesh { Page = m.GetProperty("page").GetInt32(), File = file, Vertices = verts, Triangles = tris, Neighbours = nb, Words = words };
    }

    private static GameWorker ReadWorker(string ev, JsonElement w)
    {
        static float? F(JsonElement o, string name) =>
            o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : null;
        static int? I(JsonElement o, string name) =>
            o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (int)v.GetDouble() : null;
        Vector3? position = w.TryGetProperty("position", out var p) ? Point(p) : null;
        (Vector3, Vector3)? line = w.TryGetProperty("line", out var l) ? (Point(l[0]), Point(l[1])) : null;
        Vector2? ext = w.TryGetProperty("lineExtend", out var le) ? new Vector2(le[0].GetSingle(), le[1].GetSingle()) : null;
        Vector3? box = null;
        if (w.TryGetProperty("box", out var b))
            box = new Vector3(b[0].GetSingle(), b[1].GetSingle(), b[2].ValueKind == JsonValueKind.Number ? b[2].GetSingle() : 0f);
        var triggers = new List<int>();
        if (w.TryGetProperty("triggers", out var tr))
            foreach (var x in tr.EnumerateArray())
                triggers.Add((int)x.GetDouble());
        var exits = new List<GameExit>();
        if (w.TryGetProperty("exits", out var ex))
            foreach (var x in ex.EnumerateArray())
                exits.Add(new GameExit(x.GetProperty("map").GetInt32(), x.GetProperty("entrance").GetInt32(), x.GetProperty("entry").GetInt32()));
        return new GameWorker
        {
            Event = ev,
            Index = w.GetProperty("index").GetInt32(),
            Type = w.GetProperty("type").GetInt32(),
            Position = position,
            Yaw = F(w, "yaw"),
            Model = w.TryGetProperty("model", out var m) ? m.GetString() : null,
            Line = line,
            LineExtend = ext,
            Point = w.TryGetProperty("point", out var pt) ? Point(pt) : null,
            Box = box,
            BoxYaw = F(w, "boxYaw") ?? 0f,
            TalkRange = F(w, "talkRange"),
            TouchRange = F(w, "touchRange"),
            Group = I(w, "group"),
            Dobj = I(w, "dobj"),
            Triggers = triggers,
            Exits = exits,
        };
    }
}

using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Godot;

namespace GdFfx.Remaster;

/// <summary>
/// A map as the game last showed it (docs/remaster-editor.md, "The game's data"): where the
/// game's scripts put the map's objects (each placed part's world, whether it was drawn), the
/// display objects' morph values, the camera, the characters. The game writes it a few seconds
/// after it shows a map (once a run), the editor reads it to show script-moved parts (Besaid's
/// birds) where the game has them and the game's camera; it falls back to the file's places
/// without one. <c>user://remaster/captures/&lt;level&gt;.json</c> (or
/// <c>--remaster-captures=DIR</c>): on the player's machine, never in git (it is read from the
/// running game). Everything in render space.
/// </summary>
public sealed class MapCapture
{
    public static string Dir => GameSettings.Arg("remaster-captures") is { Length: > 0 } d ? d.TrimEnd('/') : DirOverride ?? "user://remaster/captures";

    /// <summary>A check's folder (set by the check; never the player's).</summary>
    public static string? DirOverride { get; set; }

    /// <summary>Whether this run writes captures: a player's run, or a check that names its own folder.</summary>
    public static bool Writes => !UserSettings.IsCheck() || GameSettings.Arg("remaster-captures") is { Length: > 0 } || DirOverride != null;

    public static string PathOf(string level) => $"{Dir}/{PartIds.LevelKey(level)}.json";

    /// <summary>A placed part: its ID, the object id and mesh index the game placed it by, its world, whether the game drew it.</summary>
    public sealed record Placed(string Id, uint Object, int Mesh, Transform3D World, bool Drawn);

    /// <summary>A character: its slot, model, place, facing (radians, the game's), hidden bits, whether it is the player.</summary>
    public sealed record Character(int Slot, string Model, Vector3 Position, float Yaw, int Hidden, bool Player, Transform3D World);

    public string Level { get; init; } = "";
    public int Map { get; init; }
    public int Entrance { get; init; }
    public string Event { get; init; } = "";
    public int Story { get; init; }
    public bool CameraValid { get; init; }
    public Transform3D Camera { get; init; } = Transform3D.Identity;
    public float FovY { get; init; }
    public float Aspect { get; init; }
    public float Far { get; init; }
    public List<Placed> Parts { get; init; } = new();

    /// <summary>A display object's morph values (<c>SceneObject.morph</c>, <c>morph_v</c>) by object id.</summary>
    public Dictionary<uint, Vector2> Morphs { get; init; } = new();
    public List<Character> Characters { get; init; } = new();

    private static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static string V(Vector3 v) => $"[{F(v.X)}, {F(v.Y)}, {F(v.Z)}]";

    private static string T(Transform3D t) =>
        $"[{F(t.Basis.Column0.X)}, {F(t.Basis.Column0.Y)}, {F(t.Basis.Column0.Z)}, {F(t.Basis.Column1.X)}, {F(t.Basis.Column1.Y)}, {F(t.Basis.Column1.Z)}, "
        + $"{F(t.Basis.Column2.X)}, {F(t.Basis.Column2.Y)}, {F(t.Basis.Column2.Z)}, {F(t.Origin.X)}, {F(t.Origin.Y)}, {F(t.Origin.Z)}]";

    private static string S(string s) => JsonSerializer.Serialize(s);

    /// <summary>The capture as text (parts sorted by ID).</summary>
    public string Text()
    {
        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append($"  \"level\": {S(Level)},\n  \"map\": {Map},\n  \"entrance\": {Entrance},\n  \"event\": {S(Event)},\n  \"story\": {Story},\n");
        sb.Append($"  \"camera\": {{\"valid\": {(CameraValid ? "true" : "false")}, \"world\": {T(Camera)}, \"fovY\": {F(FovY)}, \"aspect\": {F(Aspect)}, \"far\": {F(Far)}}},\n");
        sb.Append("  \"parts\": [");
        var parts = new List<Placed>(Parts);
        parts.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        for (int i = 0; i < parts.Count; i++)
        {
            var p = parts[i];
            sb.Append(i == 0 ? "\n" : ",\n");
            sb.Append($"    {{\"id\": {S(p.Id)}, \"object\": {p.Object}, \"mesh\": {p.Mesh}, \"drawn\": {(p.Drawn ? "true" : "false")}, \"world\": {T(p.World)}}}");
        }
        sb.Append(parts.Count > 0 ? "\n  ],\n" : "],\n");
        sb.Append("  \"morphs\": [");
        var ids = new List<uint>(Morphs.Keys);
        ids.Sort();
        for (int i = 0; i < ids.Count; i++)
            sb.Append($"{(i == 0 ? "" : ", ")}{{\"object\": {ids[i]}, \"value\": [{F(Morphs[ids[i]].X)}, {F(Morphs[ids[i]].Y)}]}}");
        sb.Append("],\n  \"characters\": [");
        for (int i = 0; i < Characters.Count; i++)
        {
            var c = Characters[i];
            sb.Append(i == 0 ? "\n" : ",\n");
            sb.Append($"    {{\"slot\": {c.Slot}, \"model\": {S(c.Model)}, \"position\": {V(c.Position)}, \"yaw\": {F(c.Yaw)}, \"hidden\": {c.Hidden}, "
                + $"\"player\": {(c.Player ? "true" : "false")}, \"world\": {T(c.World)}}}");
        }
        sb.Append(Characters.Count > 0 ? "\n  ]\n" : "]\n");
        sb.Append("}\n");
        return sb.ToString();
    }

    /// <summary>Writes the capture (a check writes it only into <c>--remaster-captures=DIR</c>, never the player's); true when written.</summary>
    public bool Write()
    {
        if (Level.Length == 0)
            return false;
        var path = PathOf(Level);
        var text = Text();
        if (FileAccess.FileExists(path) && FileAccess.GetFileAsString(path) == text)
            return false;
        DirAccess.MakeDirRecursiveAbsolute(GameSettings.Globalize(Dir));
        using var f = FileAccess.Open(path, FileAccess.ModeFlags.Write);
        if (f == null)
            return false;
        f.StoreString(text);
        return true;
    }

    /// <summary><paramref name="level"/>'s capture (null: the game has not shown the map here yet).</summary>
    public static MapCapture? Read(string level)
    {
        var path = PathOf(level);
        return FileAccess.FileExists(path) ? Parse(FileAccess.GetFileAsString(path), path) : null;
    }

    public static MapCapture? Parse(string text, string where = "")
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var r = doc.RootElement;
            static Vector3 Vec(JsonElement e) => new(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle());
            static Transform3D Xf(JsonElement e) => new(
                new Basis(new Vector3(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle()),
                    new Vector3(e[3].GetSingle(), e[4].GetSingle(), e[5].GetSingle()),
                    new Vector3(e[6].GetSingle(), e[7].GetSingle(), e[8].GetSingle())),
                new Vector3(e[9].GetSingle(), e[10].GetSingle(), e[11].GetSingle()));
            var cam = r.GetProperty("camera");
            var c = new MapCapture
            {
                Level = r.GetProperty("level").GetString() ?? "",
                Map = r.GetProperty("map").GetInt32(),
                Entrance = r.GetProperty("entrance").GetInt32(),
                Event = r.GetProperty("event").GetString() ?? "",
                Story = r.GetProperty("story").GetInt32(),
                CameraValid = cam.GetProperty("valid").GetBoolean(),
                Camera = Xf(cam.GetProperty("world")),
                FovY = cam.GetProperty("fovY").GetSingle(),
                Aspect = cam.GetProperty("aspect").GetSingle(),
                Far = cam.GetProperty("far").GetSingle(),
            };
            foreach (var p in r.GetProperty("parts").EnumerateArray())
                c.Parts.Add(new Placed(p.GetProperty("id").GetString() ?? "", p.GetProperty("object").GetUInt32(),
                    p.GetProperty("mesh").GetInt32(), Xf(p.GetProperty("world")), p.GetProperty("drawn").GetBoolean()));
            foreach (var m in r.GetProperty("morphs").EnumerateArray())
            {
                var v = m.GetProperty("value");
                c.Morphs[m.GetProperty("object").GetUInt32()] = new Vector2(v[0].GetSingle(), v[1].GetSingle());
            }
            foreach (var a in r.GetProperty("characters").EnumerateArray())
                c.Characters.Add(new Character(a.GetProperty("slot").GetInt32(), a.GetProperty("model").GetString() ?? "",
                    Vec(a.GetProperty("position")), a.GetProperty("yaw").GetSingle(), a.GetProperty("hidden").GetInt32(),
                    a.GetProperty("player").GetBoolean(), Xf(a.GetProperty("world"))));
            return c;
        }
        catch (System.Exception e) when (e is JsonException or System.InvalidOperationException or KeyNotFoundException or System.FormatException)
        {
            GD.PrintErr($"ffx: game data: {where} is not a capture ({e.Message})");
            return null;
        }
    }
}

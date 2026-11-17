using System.Globalization;
using System.Text.Json;
using Godot;

namespace GdFfx.Remaster;

/// <summary>
/// The game's look per map as the game last showed it (docs/remaster-editor.md, "Lighting"): the
/// key light (<c>Look.lights[0]</c>: direction, colour, level, the map's turn), the map's and the
/// characters' ambient, the background and the fog. The game writes it when it shows a map (once a
/// run, when it changed), the editor reads it to show the game's lights and fog on a map it opens
/// (the editor runs no game). <c>user://remaster/looks/&lt;level&gt;.json</c> (or
/// <c>--remaster-looks=DIR</c>): on the player's machine, never in git (it is read from the game).
/// </summary>
public static class LookCache
{
    public static string Dir => GameSettings.Arg("remaster-looks") is { Length: > 0 } d ? d.TrimEnd('/') : "user://remaster/looks";

    public static string PathOf(string level) => $"{Dir}/{PartIds.LevelKey(level)}.json";

    private static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static string V(Vector3 v) => $"[{F(v.X)}, {F(v.Y)}, {F(v.Z)}]";

    /// <summary>The look as the cache's text (stable: the same look gives the same bytes).</summary>
    public static string Text(GdFfx.Api.Look look)
    {
        var key = look.Lights.Count > 0 ? look.Lights[0] : null;
        var k = key == null ? "null"
            : $"{{\"direction\": {V(key.Direction)}, \"colour\": {V(key.Colour)}, \"level\": {F(key.Level)}, \"pitch\": {F(key.Pitch)}, \"yaw\": {F(key.Yaw)}}}";
        return "{\n"
            + $"  \"key\": {k},\n"
            + $"  \"ambient\": {V(look.Ambient)},\n"
            + $"  \"characterAmbient\": {V(look.CharacterAmbient)},\n"
            + $"  \"background\": {V(look.Background)},\n"
            + $"  \"fogColour\": {V(look.FogColour)},\n"
            + $"  \"fog\": [{F(look.FogNear)}, {F(look.FogFar)}, {F(look.FogCurve)}, {F(look.FogLimit)}]\n"
            + "}\n";
    }

    /// <summary>Writes <paramref name="level"/>'s look when it differs from the cached one; true when written.</summary>
    public static bool Write(string level, GdFfx.Api.Look look)
    {
        if (!look.Valid || level.Length == 0)
            return false;
        var path = PathOf(level);
        var text = Text(look);
        if (FileAccess.FileExists(path) && FileAccess.GetFileAsString(path) == text)
            return false;
        DirAccess.MakeDirRecursiveAbsolute(GameSettings.Globalize(Dir));
        using var f = FileAccess.Open(path, FileAccess.ModeFlags.Write);
        if (f == null)
            return false;
        f.StoreString(text);
        return true;
    }

    /// <summary><paramref name="level"/>'s cached look (null: the game has not shown the map here yet).</summary>
    public static GdFfx.Api.Look? Read(string level)
    {
        var path = PathOf(level);
        if (!FileAccess.FileExists(path))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(FileAccess.GetFileAsString(path));
            var r = doc.RootElement;
            static Vector3 Vec(JsonElement e) => new(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle());
            var look = new GdFfx.Api.Look
            {
                Valid = true,
                Ambient = Vec(r.GetProperty("ambient")),
                CharacterAmbient = Vec(r.GetProperty("characterAmbient")),
                Background = Vec(r.GetProperty("background")),
                FogColour = Vec(r.GetProperty("fogColour")),
            };
            var fog = r.GetProperty("fog");
            (look.FogNear, look.FogFar, look.FogCurve, look.FogLimit) = (fog[0].GetSingle(), fog[1].GetSingle(), fog[2].GetSingle(), fog[3].GetSingle());
            if (r.GetProperty("key") is { ValueKind: JsonValueKind.Object } k)
                look.Lights.Add(new GdFfx.Api.Light
                {
                    Direction = Vec(k.GetProperty("direction")),
                    Colour = Vec(k.GetProperty("colour")),
                    Level = k.GetProperty("level").GetSingle(),
                    Pitch = k.GetProperty("pitch").GetSingle(),
                    Yaw = k.GetProperty("yaw").GetSingle(),
                });
            return look;
        }
        catch (System.Exception e) when (e is JsonException or System.InvalidOperationException or System.Collections.Generic.KeyNotFoundException)
        {
            GD.PrintErr($"ffx: lighting: {path} is not a look ({e.Message})");
            return null;
        }
    }
}

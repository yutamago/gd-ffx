using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace GdFfx.Remake;

/// <summary>
/// Mod packs (docs/remake.md), an autoload listed before <c>GameHost</c> so the packs are
/// mounted before any scene loads. At start-up it reads the base registry
/// (<c>res://remake/remake.json</c>), then mounts every <c>*.pck</c> of <c>user://mods/</c>,
/// then of each <c>--mods=DIR</c>, in each folder's load order (<see cref="Order"/>), with
/// <c>ProjectSettings.LoadResourcePack</c> (a later pack's files replace an earlier one's),
/// and reads each pack's registry fragment <c>res://mods/&lt;pack&gt;/remake.json</c>.
/// <c>--no-mods</c> mounts none.
/// </summary>
public partial class ModLoader : Node
{
    /// <summary>The players' mod folder.</summary>
    public const string UserMods = "user://mods";

    /// <summary>A folder's load order: one pack file name a line.</summary>
    public const string LoadOrderFile = "load_order.txt";

    /// <summary>A pack mounted: its file and the registry entries its fragment added.</summary>
    public readonly record struct Pack(string Name, string File, int Entries);

    private static readonly List<Pack> Mounted = new();

    /// <summary>The packs mounted, in load order.</summary>
    public static IReadOnlyList<Pack> Packs => Mounted;

    public override void _EnterTree()
    {
        var n = RemakeRegistry.LoadJson(RemakeRegistry.BaseJson, "base");
        if (n > 0)
            GD.Print($"ffx: remake: {n} entries from {RemakeRegistry.BaseJson}");
        if (GameSettings.Flag("no-mods"))
        {
            GD.Print("ffx: mods: off (--no-mods)");
            return;
        }
        var dirs = new List<string> { UserMods };
        dirs.AddRange(GameSettings.Args("mods"));
        foreach (var dir in dirs)
            LoadDir(dir);
        GD.Print(Mounted.Count == 0
            ? "ffx: mods: none"
            : $"ffx: mods: {Mounted.Count} pack(s): {string.Join(", ", Mounted.Select(p => p.Name))}");
    }

    public override void _Ready()
    {
        if (GameSettings.Flag("selftest-mods"))
            ModSelfTest.Run(this);
    }

    /// <summary>Mounts the packs of <paramref name="dir"/> in its load order; the packs mounted.</summary>
    public static int LoadDir(string dir)
    {
        var global = dir.StartsWith("res://", StringComparison.Ordinal) || dir.StartsWith("user://", StringComparison.Ordinal)
            ? ProjectSettings.GlobalizePath(dir)
            : dir;
        if (!DirAccess.DirExistsAbsolute(global))
        {
            if (dir != UserMods)
                GD.PrintErr($"ffx: mods: no folder {dir}");
            return 0;
        }
        var files = DirAccess.GetFilesAt(global);
        var orderPath = System.IO.Path.Combine(global, LoadOrderFile);
        string? order = FileAccess.FileExists(orderPath) ? FileAccess.GetFileAsString(orderPath) : null;
        var ordered = Order(files, order, out var missing);
        foreach (var m in missing)
            GD.PrintErr($"ffx: mods: {dir}/{LoadOrderFile} names {m}, which is not there");
        int count = 0;
        foreach (var f in ordered)
            if (Mount(System.IO.Path.Combine(global, f)))
                count++;
        return count;
    }

    /// <summary>
    /// Mounts one pack and reads its registry fragment <c>res://mods/&lt;pack&gt;/remake.json</c>
    /// (pack = the file name without <c>.pck</c>); false when it cannot be mounted.
    /// </summary>
    public static bool Mount(string file)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(file);
        if (!ProjectSettings.LoadResourcePack(file, true))
        {
            GD.PrintErr($"ffx: mods: cannot load {file}");
            return false;
        }
        // the pack may bring or replace convention files
        RemakeRegistry.Invalidate();
        var fragment = $"res://mods/{name}/remake.json";
        var n = RemakeRegistry.LoadJson(fragment, name);
        Mounted.Add(new Pack(name, file, Math.Max(n, 0)));
        GD.Print($"ffx: mods: loaded {name} ({file}){(n > 0 ? $", {n} remake entries" : "")}");
        return true;
    }

    /// <summary>
    /// The load order of a folder's packs: those named in <paramref name="loadOrder"/> (one
    /// file name a line; blank lines and <c>#</c> comments skipped; a name without
    /// <c>.pck</c> gets it; names match in any case) in that order, then the others
    /// alphabetically (ordinal). Later packs win. <paramref name="missing"/>: names listed but
    /// not there.
    /// </summary>
    public static List<string> Order(IEnumerable<string> files, string? loadOrder, out List<string> missing)
    {
        var all = files
            .Select(f => System.IO.Path.GetFileName(f))
            .Where(f => f.EndsWith(".pck", StringComparison.OrdinalIgnoreCase))
            .ToList();
        // a listed name → the file (the first in ordinal order when two differ only in case)
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in all.OrderBy(f => f, StringComparer.Ordinal))
            byName.TryAdd(f, f);
        var result = new List<string>();
        missing = new List<string>();
        foreach (var raw in (loadOrder ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            if (!line.EndsWith(".pck", StringComparison.OrdinalIgnoreCase))
                line += ".pck";
            if (!byName.TryGetValue(line, out var file))
                missing.Add(line);
            else if (!result.Contains(file))
                result.Add(file);
        }
        result.AddRange(all.Where(p => !result.Contains(p)).OrderBy(p => p, StringComparer.Ordinal));
        return result;
    }
}

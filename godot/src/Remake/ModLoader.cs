using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace GdFfx.Remake;

/// <summary>
/// Mod packs (docs/remake.md), an autoload listed before <c>GameHost</c>. At start-up it reads
/// the base registry (<c>res://remake/remake.json</c>); the packs are mounted by
/// <see cref="MountAll"/>, which <c>GameHost.Start</c> calls when the launcher hands over (or
/// at once when the launcher is skipped), before <c>scenes/main.tscn</c> loads. So what gets
/// mounted is what the launcher chose (the enabled packs, in its order): a mounted pack
/// cannot be unmounted. It mounts every enabled <c>*.pck</c> of <c>user://mods/</c>, then of
/// each <c>--mods=DIR</c> (<see cref="Dirs"/>), in each folder's load order
/// (<see cref="Entries"/>), with <c>ProjectSettings.LoadResourcePack</c> (a later pack's files
/// replace an earlier one's), and reads each pack's registry fragment
/// <c>res://mods/&lt;pack&gt;/remake.json</c>. <c>--no-mods</c> mounts none.
/// </summary>
public partial class ModLoader : Node
{
    /// <summary>The players' mod folder.</summary>
    public const string UserMods = "user://mods";

    /// <summary>A folder's load order: one pack file name a line.</summary>
    public const string LoadOrderFile = "load_order.txt";

    /// <summary>The line prefix in <see cref="LoadOrderFile"/> of a pack kept but not mounted.</summary>
    public const string DisabledPrefix = "# disabled:";

    /// <summary>A pack mounted: its file and the registry entries its fragment added.</summary>
    public readonly record struct Pack(string Name, string File, int Entries);

    /// <summary>A pack of a folder, in load order, and whether it is mounted.</summary>
    public readonly record struct Entry(string File, bool Enabled);

    private static readonly List<Pack> Mounted = new();
    private static List<string>? _dirs;

    /// <summary>The packs mounted, in load order.</summary>
    public static IReadOnlyList<Pack> Packs => Mounted;

    /// <summary>Whether <see cref="MountAll"/> ran.</summary>
    public static bool MountedAll { get; private set; }

    /// <summary>
    /// The mod folders, in order: <c>user://mods</c>, then each <c>--mods=DIR</c> (none with
    /// <c>--no-mods</c>). Settable before <see cref="MountAll"/> (the launcher's self-test).
    /// </summary>
    public static List<string> Dirs
    {
        get
        {
            if (_dirs == null)
            {
                _dirs = new List<string>();
                if (!GameSettings.Flag("no-mods"))
                {
                    _dirs.Add(UserMods);
                    _dirs.AddRange(GameSettings.Args("mods"));
                }
            }
            return _dirs;
        }
        set => _dirs = value;
    }

    public override void _EnterTree()
    {
        var n = RemakeRegistry.LoadJson(RemakeRegistry.BaseJson, "base");
        if (n > 0)
            GD.Print($"ffx: remake: {n} entries from {RemakeRegistry.BaseJson}");
    }

    public override void _Ready()
    {
        if (GameSettings.Flag("selftest-mods"))
            ModSelfTest.Run(this);
        else if (GameSettings.Flag("selftest-saves"))
            SaveFilesSelfTest.Run(this);
        else if (GameSettings.Flag("selftest-materialize") && GameSettings.Flag("no-game"))
            Remaster.Materialize.MaterializeTest.RunAlone(this);
        else if (GameSettings.Flag("selftest-wizard-classifier") && GameSettings.Flag("no-game"))
            Remaster.Wizard.ClassifierCheck.RunAlone(this);
    }

    /// <summary>Mounts the enabled packs of every folder of <see cref="Dirs"/>, once.</summary>
    public static void MountAll()
    {
        if (MountedAll)
            return;
        MountedAll = true;
        if (GameSettings.Flag("no-mods"))
        {
            GD.Print("ffx: mods: off (--no-mods)");
            return;
        }
        int before = Mounted.Count;
        foreach (var dir in Dirs)
            LoadDir(dir);
        var mine = Mounted.Skip(before).ToList();
        GD.Print(mine.Count == 0
            ? "ffx: mods: none"
            : $"ffx: mods: {mine.Count} pack(s): {string.Join(", ", mine.Select(p => p.Name))}");
    }

    /// <summary>A folder as an absolute path (<c>res://</c> and <c>user://</c> globalized).</summary>
    public static string Globalize(string dir) =>
        dir.StartsWith("res://", StringComparison.Ordinal) || dir.StartsWith("user://", StringComparison.Ordinal)
            ? ProjectSettings.GlobalizePath(dir)
            : dir;

    /// <summary>
    /// The packs of <paramref name="dir"/> in its load order, enabled or not (none when the
    /// folder is not there); <paramref name="missing"/>: names listed but not there.
    /// </summary>
    public static List<Entry> Scan(string dir, out List<string> missing)
    {
        var global = Globalize(dir);
        if (!DirAccess.DirExistsAbsolute(global))
        {
            missing = new List<string>();
            return new List<Entry>();
        }
        var orderPath = System.IO.Path.Combine(global, LoadOrderFile);
        string? order = FileAccess.FileExists(orderPath) ? FileAccess.GetFileAsString(orderPath) : null;
        return Entries(DirAccess.GetFilesAt(global), order, out missing);
    }

    /// <summary>Writes <paramref name="entries"/> as <paramref name="dir"/>'s load order (<see cref="Format"/>).</summary>
    public static void Save(string dir, IEnumerable<Entry> entries)
    {
        var global = Globalize(dir);
        System.IO.Directory.CreateDirectory(global);
        System.IO.File.WriteAllText(System.IO.Path.Combine(global, LoadOrderFile), Format(entries));
    }

    /// <summary>
    /// A load order's text: a comment, then one file name a line, a disabled pack as
    /// <c># disabled: NAME</c> at its place (only a comment to a reader that does not know it).
    /// </summary>
    public static string Format(IEnumerable<Entry> entries)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("# gd-ffx mod packs, first to last (later packs win); \"")
            .Append(DisabledPrefix).Append(" NAME\" keeps a pack but does not load it\n");
        foreach (var e in entries)
            sb.Append(e.Enabled ? "" : DisabledPrefix + " ").Append(e.File).Append('\n');
        return sb.ToString();
    }

    /// <summary>Mounts the enabled packs of <paramref name="dir"/> in its load order; the packs mounted.</summary>
    public static int LoadDir(string dir)
    {
        var global = Globalize(dir);
        if (!DirAccess.DirExistsAbsolute(global))
        {
            if (dir != UserMods)
                GD.PrintErr($"ffx: mods: no folder {dir}");
            return 0;
        }
        var entries = Scan(dir, out var missing);
        foreach (var m in missing)
            GD.PrintErr($"ffx: mods: {dir}/{LoadOrderFile} names {m}, which is not there");
        int count = 0;
        foreach (var e in entries)
        {
            if (!e.Enabled)
            {
                GD.Print($"ffx: mods: {e.File} is disabled");
                continue;
            }
            if (Mount(System.IO.Path.Combine(global, e.File)))
                count++;
        }
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
        // the pack may bring or replace convention files, and material profiles
        RemakeRegistry.Invalidate();
        Remaster.MaterialProfiles.Invalidate();
        var fragment = $"res://mods/{name}/remake.json";
        var n = RemakeRegistry.LoadJson(fragment, name);
        Mounted.Add(new Pack(name, file, Math.Max(n, 0)));
        GD.Print($"ffx: mods: loaded {name} ({file}){(n > 0 ? $", {n} remake entries" : "")}");
        return true;
    }

    /// <summary>The load order of a folder's enabled packs (<see cref="Entries"/> without the disabled ones).</summary>
    public static List<string> Order(IEnumerable<string> files, string? loadOrder, out List<string> missing) =>
        Entries(files, loadOrder, out missing).Where(e => e.Enabled).Select(e => e.File).ToList();

    /// <summary>
    /// The load order of a folder's packs: those named in <paramref name="loadOrder"/> (one
    /// file name a line; blank lines and <c>#</c> comments skipped, except
    /// <c># disabled: NAME</c>: the pack keeps its place but is not mounted; a name without
    /// <c>.pck</c> gets it; names match in any case) in that order, then the others
    /// alphabetically (ordinal), enabled. Later packs win. A name listed twice keeps its first
    /// place and is disabled when either line disables it. <paramref name="missing"/>: names
    /// listed but not there.
    /// </summary>
    public static List<Entry> Entries(IEnumerable<string> files, string? loadOrder, out List<string> missing)
    {
        var all = files
            .Select(f => System.IO.Path.GetFileName(f))
            .Where(f => f.EndsWith(".pck", StringComparison.OrdinalIgnoreCase))
            .ToList();
        // a listed name → the file (the first in ordinal order when two differ only in case)
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in all.OrderBy(f => f, StringComparer.Ordinal))
            byName.TryAdd(f, f);
        var listed = new List<string>();
        var disabled = new HashSet<string>(StringComparer.Ordinal);
        missing = new List<string>();
        foreach (var raw in (loadOrder ?? "").Split('\n'))
        {
            var line = raw.Trim();
            bool off = false;
            if (line.StartsWith(DisabledPrefix, StringComparison.OrdinalIgnoreCase))
            {
                line = line[DisabledPrefix.Length..].Trim();
                off = true;
            }
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            if (!line.EndsWith(".pck", StringComparison.OrdinalIgnoreCase))
                line += ".pck";
            if (!byName.TryGetValue(line, out var file))
            {
                if (!off)
                    missing.Add(line);
                continue;
            }
            if (!listed.Contains(file))
                listed.Add(file);
            if (off)
                disabled.Add(file);
        }
        listed.AddRange(all.Where(p => !listed.Contains(p)).OrderBy(p => p, StringComparer.Ordinal));
        return listed.Select(f => new Entry(f, !disabled.Contains(f))).ToList();
    }
}

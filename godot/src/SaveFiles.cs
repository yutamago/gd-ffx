using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GdFfx;

/// <summary>
/// The game's save files in gd-ffx's documents sandbox, read for the native save and load
/// screens (ffx-rust's docs/formats/save.md, "Files"): <c>ffx_000</c> the autosave,
/// <c>ffx_001</c>..<c>ffx_199</c> the manual saves, each <c>0x6900</c> bytes; the header as
/// <c>MenuSave_PackSave</c> fills it (the party, the language, the play time, gil, the
/// location, the player's name) and the characters' names from the file's own block. Only
/// read; the game writes them.
/// </summary>
public static class SaveFiles
{
    public const int FileSize = 0x6900;
    public const int Block = 0x40;
    public const int Names = Block + 0x634c;
    public const int NameSize = 20;

    /// <summary>One save file's header.</summary>
    public sealed record Header(int Slot, System.DateTime Written, byte[] Party, byte Language, uint PlayTime, uint Gil,
        ushort Location, byte[] PlayerName, byte[][] Names)
    {
        /// <summary>Character <paramref name="i"/>'s name bytes from this file (empty if none).</summary>
        public byte[] Name(int i) => i >= 0 && i < Names.Length ? Names[i] : [];
    }

    /// <summary>The save folder under <paramref name="documents"/>, as the game builds it.</summary>
    public static string Folder(string documents) =>
        Path.Combine(documents, "Square Enix", "FINAL FANTASY X&X-2 HD Remaster", "FINAL FANTASY X");

    private static byte[] Text(byte[] b, int at, int max)
    {
        int end = at;
        while (end < at + max && end < b.Length && b[end] != 0)
            end++;
        return b[at..end];
    }

    /// <summary>A file's header, or null when it is missing or not a save's size.</summary>
    public static Header? Read(string documents, int slot)
    {
        var path = Path.Combine(Folder(documents), $"ffx_{slot:000}");
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != FileSize)
                return null;
            var b = File.ReadAllBytes(path);
            uint U32(int o) => System.BitConverter.ToUInt32(b, o);
            var names = Enumerable.Range(0, 18).Select(i => Text(b, Names + NameSize * i, NameSize)).ToArray();
            return new Header(slot, info.LastWriteTimeUtc, b[5..12], b[0xc], U32(0x10), U32(0x14),
                System.BitConverter.ToUInt16(b, 0x18), Text(b, 0x20, Block - 0x20), names);
        }
        catch (System.Exception e) when (e is IOException or System.UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The list the game shows (<c>SaveDataMachine_BuildList</c>): loading, the autosave when
    /// there is one, then the manual saves newest first; saving, a new file's row (null) unless
    /// all 199 are used, then the manual saves newest first.
    /// </summary>
    public static List<Header?> List(string documents, bool saving)
    {
        var manual = Enumerable.Range(1, 199).Select(i => Read(documents, i)).Where(h => h != null)
            .OrderByDescending(h => h!.Written).ThenBy(h => h!.Slot).ToList();
        var list = new List<Header?>();
        if (saving)
        {
            if (manual.Count < 199)
                list.Add(null);
        }
        else if (Read(documents, 0) is { } auto)
            list.Add(auto);
        list.AddRange(manual);
        return list;
    }
}

/// <summary>
/// The save list's check (<c>--selftest-saves --no-game</c>, no game needed): writes made-up
/// save files (zeros but the header fields and two names, in the layout of ffx-rust's
/// docs/formats/save.md) into a temporary folder with chosen modification times, reads them
/// back with <see cref="SaveFiles"/> and checks the fields and the list's order (loading: the
/// autosave, then newest first; saving: a new file's row, then newest first). Prints
/// <c>ffx selftest: PASS</c> or <c>FAIL</c> and quits with 0 or 1.
/// </summary>
public static class SaveFilesSelfTest
{
    public static void Run(Godot.Node node)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"gd-ffx-selftest-saves-{Godot.OS.GetProcessId()}");
        var fails = new List<string>();
        try
        {
            var dir = SaveFiles.Folder(tmp);
            Directory.CreateDirectory(dir);
            var t0 = new System.DateTime(2026, 1, 1, 12, 0, 0, System.DateTimeKind.Utc);
            void Write(int slot, uint gil, uint time, System.DateTime written)
            {
                var b = new byte[SaveFiles.FileSize];
                byte[] party = [0, 4, 0xff, 0xff, 0xff, 0xff, 0xff];
                party.CopyTo(b, 5);
                b[0xc] = 1;
                System.BitConverter.GetBytes(time).CopyTo(b, 0x10);
                System.BitConverter.GetBytes(gil).CopyTo(b, 0x14);
                System.BitConverter.GetBytes((ushort)0x1f).CopyTo(b, 0x18);
                byte[] player = [0x63, 0x58, 0x53];
                player.CopyTo(b, 0x20);
                player.CopyTo(b, SaveFiles.Names);
                byte[] second = [0x66, 0x50];
                second.CopyTo(b, SaveFiles.Names + 4 * SaveFiles.NameSize);
                var path = Path.Combine(dir, $"ffx_{slot:000}");
                File.WriteAllBytes(path, b);
                File.SetLastWriteTimeUtc(path, written);
            }
            Write(0, 10, 60, t0);
            Write(3, 30, 3600, t0.AddHours(1));
            Write(5, 50, 7260, t0.AddHours(3));
            Write(7, 70, 90, t0.AddHours(2));
            File.WriteAllBytes(Path.Combine(dir, "ffx_009"), new byte[100]); // not a save's size: skipped

            void Check(bool ok, string what)
            {
                Godot.GD.Print($"ffx selftest: {(ok ? "ok  " : "FAIL")} {what}");
                if (!ok)
                    fails.Add(what);
            }
            var load = SaveFiles.List(tmp, saving: false);
            Check(string.Join(",", load.Select(h => h?.Slot)) == "0,5,7,3", $"load list: the autosave, then newest first ({string.Join(",", load.Select(h => h?.Slot))})");
            var save = SaveFiles.List(tmp, saving: true);
            Check(save.Count == 4 && save[0] == null && save[1]?.Slot == 5, "save list: a new file's row, then newest first, no autosave");
            var h = load[1]!;
            Check(h.Gil == 50 && h.PlayTime == 7260 && h.Location == 0x1f && h.Language == 1, "the header's gil, play time, location, language");
            Check(h.Party.SequenceEqual(new byte[] { 0, 4, 0xff, 0xff, 0xff, 0xff, 0xff }), "the header's party");
            Check(h.PlayerName.SequenceEqual(new byte[] { 0x63, 0x58, 0x53 }) && h.Name(4).SequenceEqual(new byte[] { 0x66, 0x50 }) && h.Name(1).Length == 0,
                "the player's name and the characters' names from the block");
            Check(FieldMenuView.Time(h.PlayTime) == "2:01", "play time as the menu shows it");
        }
        catch (System.Exception e)
        {
            fails.Add(e.Message);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch (IOException) { }
        }
        var pass = fails.Count == 0;
        Godot.GD.Print(pass ? "ffx selftest: PASS (saves: header fields, names, load and save list order)" : $"ffx selftest: FAIL ({string.Join("; ", fails)})");
        node.GetTree().Quit(pass ? 0 : 1);
    }
}

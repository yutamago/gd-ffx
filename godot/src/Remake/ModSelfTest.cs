using System.Collections.Generic;
using System.Linq;
using Godot;

namespace GdFfx.Remake;

/// <summary>
/// <c>--selftest-mods</c> (no game needed; with <c>--no-game</c>): the load order of a folder
/// from its <c>load_order.txt</c>, then three real packs built at run time with
/// <see cref="PckPacker"/> from the example stand-in (res://remake/examples/), mounted from a
/// temporary folder through <see cref="ModLoader.LoadDir"/>: a later pack's registry entry
/// and files win, a pack's <c>res://remake/chr/&lt;model&gt;.tscn</c> registers by the
/// convention, and the scenes resolve and instantiate. Prints <c>ffx selftest: PASS</c> or
/// <c>FAIL</c> and quits with 0 or 1.
/// </summary>
public static class ModSelfTest
{
    public const string Example = "res://remake/examples/stand_in_character.tscn";

    public static void Run(Node node)
    {
        var fails = new List<string>();
        var notes = new List<string>();
        void Check(bool ok, string what)
        {
            if (!ok)
                fails.Add(what);
            GD.Print($"ffx selftest: {(ok ? "ok  " : "FAIL")} {what}");
        }

        // 1. the order alone
        var order = ModLoader.Order(
            ["gamma.pck", "alpha.pck", "beta.pck", "notes.txt", "Zeta.PCK"],
            "# mine first\nBETA\n\nalpha.pck\nmissing.pck\nbeta.pck\nzeta.pck\n", out var missing);
        Check(order.SequenceEqual(["beta.pck", "alpha.pck", "Zeta.PCK", "gamma.pck"]),
            $"load order: {string.Join(", ", order)} (listed first, names in any case, then the rest alphabetically)");
        Check(missing.SequenceEqual(["missing.pck"]), $"listed but missing: {string.Join(", ", missing)}");
        var none = ModLoader.Order(["b.pck", "a.pck"], null, out _);
        Check(none.SequenceEqual(["a.pck", "b.pck"]), $"without load_order.txt: {string.Join(", ", none)}");
        // disabled packs keep their place, are not mounted, and survive a round trip
        var withOff = ModLoader.Entries(["c.pck", "a.pck", "b.pck", "d.pck"],
            "# a comment\nb\n# DISABLED: a.pck\n#notes.pck\n# disabled: gone.pck\n", out var missing2);
        Check(withOff.SequenceEqual([new ModLoader.Entry("b.pck", true), new ModLoader.Entry("a.pck", false),
                new ModLoader.Entry("c.pck", true), new ModLoader.Entry("d.pck", true)]),
            $"entries with a disabled pack: {string.Join(", ", withOff)}");
        Check(missing2.Count == 0, "a disabled pack that is gone is not reported missing");
        var orderOff = ModLoader.Order(["c.pck", "a.pck", "b.pck", "d.pck"], ModLoader.Format(withOff), out _);
        Check(orderOff.SequenceEqual(["b.pck", "c.pck", "d.pck"]),
            $"the formatted order mounts the enabled ones only: {string.Join(", ", orderOff)}");
        var back = ModLoader.Entries(["c.pck", "a.pck", "b.pck", "d.pck"], ModLoader.Format(withOff), out _);
        Check(back.SequenceEqual(withOff), "a load order written and read back is the same");

        // 2. real packs in a temporary folder
        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"gd-ffx-selftest-mods-{OS.GetProcessId()}");
        var src = System.IO.Path.Combine(tmp, "src");
        var mods = System.IO.Path.Combine(tmp, "mods");
        System.IO.Directory.CreateDirectory(src);
        System.IO.Directory.CreateDirectory(mods);
        try
        {
            var scene = FileAccess.GetFileAsString(Example);
            Check(scene.Length > 0, $"the example {Example} is there");
            string Src(string name, string text)
            {
                var p = System.IO.Path.Combine(src, name);
                System.IO.File.WriteAllText(p, text);
                return p;
            }
            var sceneSrc = Src("stand_in.tscn", scene);
            const string rootLine = "[node name=\"StandInCharacter\" type=\"Node3D\"";
            var at = scene.IndexOf('\n', scene.IndexOf(rootLine, System.StringComparison.Ordinal));
            var movedSrc = Src("moved.tscn", scene.Insert(at + 1, "transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 0, -5, 0)\n"));
            Build(System.IO.Path.Combine(mods, "alpha.pck"), new()
            {
                ["res://mods/alpha/remake.json"] = Src("alpha.json",
                    "{ \"chr\": { \"c999\": \"res://mods/alpha/stand_in.tscn\", \"c995\": \"res://mods/alpha/moved.tscn\",\n"
                    + "    \"c994\": \"res://mods/alpha/control.tscn\", \"c993\": \"res://mods/alpha/remake.json\" },\n"
                    + "  \"map\": { \"/map/zzzz/zzzz00/\": \"res://mods/alpha/stand_in.tscn\" } }"),
                ["res://mods/alpha/stand_in.tscn"] = sceneSrc,
                ["res://mods/alpha/moved.tscn"] = movedSrc,
                ["res://mods/alpha/control.tscn"] = Src("control.tscn", "[gd_scene format=3]\n\n[node name=\"NotA3DScene\" type=\"Control\"]\n"),
                ["res://mods/selftest_shared.txt"] = Src("alpha.txt", "alpha"),
            });
            Build(System.IO.Path.Combine(mods, "beta.pck"), new()
            {
                ["res://mods/beta/remake.json"] = Src("beta.json",
                    "{ \"chr\": { \"c999\": \"res://mods/beta/stand_in.tscn\", \"c997\": \"res://mods/beta/stand_in.tscn\" } }"),
                ["res://mods/beta/stand_in.tscn"] = sceneSrc,
                ["res://mods/selftest_shared.txt"] = Src("beta.txt", "beta"),
            });
            // no fragment: the convention's file only
            Build(System.IO.Path.Combine(mods, "gamma.pck"), new()
            {
                ["res://remake/chr/c998.tscn"] = sceneSrc,
            });
            // disabled: its files and entries must not appear
            Build(System.IO.Path.Combine(mods, "delta.pck"), new()
            {
                ["res://mods/delta/remake.json"] = Src("delta.json",
                    "{ \"chr\": { \"c999\": \"res://mods/delta/none.tscn\" } }"),
                ["res://mods/selftest_shared.txt"] = Src("delta.txt", "delta"),
            });
            System.IO.File.WriteAllText(System.IO.Path.Combine(mods, ModLoader.LoadOrderFile),
                "Beta.PCK\nalpha\n" + ModLoader.DisabledPrefix + " delta.pck\n");

            var mounted = ModLoader.LoadDir(mods);
            Check(mounted == 3, $"{mounted} packs mounted from a --mods folder (one disabled)");
            Check(!FileAccess.FileExists("res://mods/delta/remake.json"), "a disabled pack is not mounted");
            var names = ModLoader.Packs.Select(p => p.Name).ToList();
            Check(names.SequenceEqual(["beta", "alpha", "gamma"]), $"mounted in order: {string.Join(", ", names)}");
            var shared = FileAccess.GetFileAsString("res://mods/selftest_shared.txt");
            Check(shared == "alpha", $"a file in two packs comes from the later one: \"{shared}\"");
            Check(RemakeRegistry.Find("chr", "c999") == "res://mods/alpha/stand_in.tscn",
                $"chr c999 from the later pack: {RemakeRegistry.Find("chr", "c999")}");
            Check(RemakeRegistry.Find("chr", "c997") == "res://mods/beta/stand_in.tscn",
                $"chr c997 from the earlier pack stays: {RemakeRegistry.Find("chr", "c997")}");
            Check(RemakeRegistry.Find("chr", "c998") == "res://remake/chr/c998.tscn",
                $"chr c998 by the convention: {RemakeRegistry.Find("chr", "c998")}");
            Check(RemakeRegistry.Find("map", "/map/zzzz/zzzz00/") == "res://mods/alpha/stand_in.tscn"
                && RemakeRegistry.Find("map", "zzzz00") == RemakeRegistry.Find("map", "/map/zzzz/zzzz00/"),
                "map by level and by name");
            Check(RemakeRegistry.Find("chr", "c996") == null, "an id without a remake stays classic");
            RemakeRegistry.Register("chr", "c998", "", "selftest");
            Check(RemakeRegistry.Find("chr", "c998") == null, "an empty entry pins an id to classic");
            RemakeRegistry.Unregister("chr", "c998");

            foreach (var id in new[] { "c999", "c998" })
            {
                var content = ContentResolver.Resolve(null, "chr", id);
                var inst = content?.Node;
                var skel = inst?.FindChildren("*", nameof(Skeleton3D), true, false).FirstOrDefault() as Skeleton3D;
                Check(content is { Remade: true } && inst != null && skel != null && skel.FindBone("SkeletonNode_0") >= 0
                    && inst.GetMeta("ffx_source").AsString() == "remade",
                    $"chr {id} resolves to a remade scene with a SkeletonNode_ skeleton ({content?.Path})");
                inst?.Free();
            }

            // the scene's root keeps its own transform inside the wrapper
            var moved = ContentResolver.Resolve(null, "chr", "c995");
            var movedRoot = moved?.Node?.GetChildOrNull<Node3D>(0);
            Check(moved is { Remade: true } && movedRoot != null && Mathf.IsEqualApprox(movedRoot.Position.Y, -5f),
                $"a remade scene comes wrapped, its root's transform kept ({movedRoot?.Position})");
            moved?.Node?.Free();
            // bad entries fall back to classic (none here), without an exception
            foreach (var id in new[] { "c994", "c993" })
            {
                var bad = ContentResolver.Resolve(null, "chr", id);
                Check(bad == null, $"chr {id} ({RemakeRegistry.Find("chr", id)}) does not load: classic instead");
            }
            // a registry file applies whole or not at all
            int before = RemakeRegistry.Entries.Count;
            try
            {
                RemakeRegistry.ApplyJson("{ \"chr\": { \"c990\": \"res://x.tscn\", \"c991\": 5 } }", "selftest");
            }
            catch (System.Text.Json.JsonException)
            {
            }
            Check(RemakeRegistry.Entries.Count == before && RemakeRegistry.Find("chr", "c990") == null,
                "a registry file with a bad entry registers nothing");
            notes.Add($"{mounted} packs built with PckPacker and mounted");
        }
        catch (System.Exception e)
        {
            Check(false, $"exception: {e.Message}");
        }
        finally
        {
            try
            {
                System.IO.Directory.Delete(tmp, true);
            }
            catch (System.IO.IOException)
            {
            }
        }
        var pass = fails.Count == 0;
        GD.Print(pass
            ? $"ffx selftest: PASS (mods: load order, {string.Join(", ", notes)}, registry overrides, convention, instantiation)"
            : $"ffx selftest: FAIL ({string.Join("; ", fails)})");
        GameHost.Instance?.Game?.Stop();
        node.GetTree().Quit(pass ? 0 : 1);
    }

    /// <summary>Writes a pack <paramref name="pck"/> of <paramref name="files"/> (res:// path → source file).</summary>
    internal static void Build(string pck, Dictionary<string, string> files)
    {
        var p = new PckPacker();
        if (p.PckStart(pck) != Error.Ok)
            throw new System.InvalidOperationException($"cannot start {pck}");
        foreach (var (target, source) in files)
            if (p.AddFile(target, source) != Error.Ok)
                throw new System.InvalidOperationException($"cannot add {target} to {pck}");
        if (p.Flush() != Error.Ok)
            throw new System.InvalidOperationException($"cannot write {pck}");
    }
}

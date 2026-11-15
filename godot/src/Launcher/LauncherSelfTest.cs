using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GdFfx.Remake;
using Godot;

namespace GdFfx.Launcher;

/// <summary>
/// <c>--selftest-launcher --no-game</c> (no game needed): a temporary folder with a fake game
/// folder (empty files named <c>FFX.exe</c> and <c>data/FFX_Data.vbf</c>; nothing of the game),
/// one without the archive, three small packs built with <see cref="PckPacker"/>, a game
/// settings file and a classic cache; the settings file and the mod folder point there
/// (<see cref="Prepare"/>, before the launcher reads them). Then it drives the launcher's
/// controls: a wrong folder, the right one (remembered in the settings file), a pack
/// disabled, one moved up (the folder's <c>load_order.txt</c>), the language shown, the
/// cache cleared, and Start: the scene becomes <c>scenes/main.tscn</c>, the enabled packs
/// are mounted in the chosen order, the disabled one is not, and with <c>--no-game</c> no
/// game runs. Prints <c>ffx selftest: PASS</c> or <c>FAIL</c> and quits with 0 or 1.
/// </summary>
public partial class LauncherSelfTest : Node
{
    private static string _tmp = "";
    private static string Tmp(params string[] parts) => System.IO.Path.Combine([_tmp, .. parts]);

    private readonly Launcher _launcher;
    private readonly List<string> _fails = new();

    public LauncherSelfTest() => _launcher = null!;

    public LauncherSelfTest(Launcher launcher)
    {
        _launcher = launcher;
        Name = "LauncherSelfTest";
    }

    /// <summary>Makes the temporary folders and points the settings file and the mod folders there.</summary>
    public static void Prepare()
    {
        _tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"gd-ffx-selftest-launcher-{OS.GetProcessId()}");
        System.IO.Directory.CreateDirectory(Tmp("game", "data"));
        System.IO.File.WriteAllBytes(Tmp("game", "FFX.exe"), []);
        System.IO.File.WriteAllBytes(Tmp("game", "data", "FFX_Data.vbf"), []);
        System.IO.Directory.CreateDirectory(Tmp("not-game"));
        System.IO.File.WriteAllBytes(Tmp("not-game", "FFX.exe"), []);
        System.IO.Directory.CreateDirectory(Tmp("mods"));
        System.IO.Directory.CreateDirectory(Tmp("src"));
        foreach (var n in new[] { "alpha", "beta", "gamma" })
        {
            var id = Tmp("src", $"{n}.txt");
            System.IO.File.WriteAllText(id, n);
            ModSelfTest.Build(Tmp("mods", $"{n}.pck"), new()
            {
                [$"res://mods/{n}/id.txt"] = id,
                ["res://mods/selftest_launcher.txt"] = id,
            });
        }
        var ini = GameSettings.GameSettingIni(Tmp("docs"));
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ini)!);
        System.IO.File.WriteAllText(ini, "Language=de\r\nResolution=1280*720\r\n");
        System.IO.Directory.CreateDirectory(Tmp("classic", "chr"));
        System.IO.File.WriteAllBytes(Tmp("classic", "chr", "c999.glb"), new byte[3000]);
        System.IO.File.WriteAllText(Tmp("classic", "keep.txt"), "not the importer's");
        GameSettings.SettingsPath = Tmp("settings.cfg");
        ModLoader.Dirs = [Tmp("mods")];
    }

    public override void _Ready() => _ = Run();

    private void Check(bool ok, string what)
    {
        if (!ok)
            _fails.Add(what);
        GD.Print($"ffx selftest: {(ok ? "ok  " : "FAIL")} {what}");
    }

    private async Task Frames(int n)
    {
        for (int i = 0; i < n; i++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private async Task Run()
    {
        try
        {
            await Frames(2);
            var l = _launcher;
            l.DocumentsDir = Tmp("docs");
            l.ClassicDir = Tmp("classic");
            l.Refresh();

            // the game folder
            Check(GameSettings.SavedGameDir() == "", "no game folder remembered at first");
            l.GameDirEdit.Text = Tmp("not-game");
            l.GameDirEdit.EmitSignal(LineEdit.SignalName.TextSubmitted, l.GameDirEdit.Text);
            Check(l.StartButton.Disabled && l.GameDirStatus.Text.Contains("FFX_Data.vbf"),
                $"a folder without the archive is refused: \"{l.GameDirStatus.Text}\"");
            Check(GameSettings.SavedGameDir() == "", "a refused folder is not remembered");
            l.GameDirEdit.Text = Tmp("game");
            l.GameDirEdit.EmitSignal(LineEdit.SignalName.TextSubmitted, l.GameDirEdit.Text);
            Check(!l.StartButton.Disabled && !l.DevSaveButton.Disabled && l.ValidGameDir == Tmp("game"),
                $"the fake game folder is accepted: \"{l.GameDirStatus.Text}\"");
            var cfg = new ConfigFile();
            Check(cfg.Load(Tmp("settings.cfg")) == Error.Ok && cfg.GetValue("game", "dir", "").AsString() == Tmp("game"),
                "the game folder is remembered in the settings file");
            // the player's pick wins over the project setting (a development default)
            var projectDir = ProjectSettings.GetSetting("ffx/game/game_dir", "");
            ProjectSettings.SetSetting("ffx/game/game_dir", Tmp("not-game"));
            var (from, source) = GameSettings.GameDirFrom();
            ProjectSettings.SetSetting("ffx/game/game_dir", projectDir);
            Check(OS.GetEnvironment("FFX_GAME_DIR").Length > 0
                || (from == Tmp("game") && source == GameSettings.SettingsPath),
                $"the next start finds it there, over the project setting ({source})");
            Check(!System.IO.File.Exists(Tmp("mods", ModLoader.LoadOrderFile)), "no load_order.txt written before a change");

            // the packs: alphabetical without a load order
            Check(l.Folders.Count == 1 && l.Folders[0].Entries.Select(e => e.File).SequenceEqual(["alpha.pck", "beta.pck", "gamma.pck"])
                && l.Folders[0].Entries.All(e => e.Enabled),
                $"the packs listed: {string.Join(", ", l.Folders.SelectMany(f => f.Entries))}");
            var beta = l.PackRow(0, "beta.pck")?.GetNode<CheckBox>("Enabled");
            Check(beta != null, "beta's row has a check box");
            if (beta != null)
                beta.ButtonPressed = false;
            var gammaUp = l.PackRow(0, "gamma.pck")?.GetNode<Button>("Up");
            Check(gammaUp is { Disabled: false }, "gamma's row has an Up button");
            gammaUp?.EmitSignal(BaseButton.SignalName.Pressed);
            await Frames(1);
            var orderFile = Tmp("mods", ModLoader.LoadOrderFile);
            var text = System.IO.File.Exists(orderFile) ? System.IO.File.ReadAllText(orderFile) : "";
            var entries = ModLoader.Entries(System.IO.Directory.GetFiles(Tmp("mods")), text, out var missing);
            Check(entries.SequenceEqual([new ModLoader.Entry("alpha.pck", true), new ModLoader.Entry("gamma.pck", true),
                    new ModLoader.Entry("beta.pck", false)]) && missing.Count == 0,
                $"load_order.txt: {string.Join(", ", entries)}");
            Check(text.Contains($"{ModLoader.DisabledPrefix} beta.pck\n"), "the disabled pack is a \"# disabled:\" line");
            var rows = l.ModList.GetChildren().OfType<HBoxContainer>().Where(r => !r.IsQueuedForDeletion())
                .Select(r => r.GetMeta("pack").AsString()).ToList();
            Check(rows.SequenceEqual(["alpha.pck", "gamma.pck", "beta.pck"]), $"the list shows the new order: {string.Join(", ", rows)}");
            l.ScanMods();
            Check(l.Folders[0].Entries.SequenceEqual(entries), "read back from the folder, the same");

            // the language and the cache
            Check(l.LanguageLabel.Text.Contains("German (de)"), $"the language: \"{l.LanguageLabel.Text}\"");
            Check(l.CacheBytes() == 3000 && l.CacheLabel.Text.Contains("2.9 KB"), $"the cache: \"{l.CacheLabel.Text}\"");
            // only a safe folder is cleared
            var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
            foreach (var bad in new[] { "", "relative/classic", "/", home, OS.GetUserDataDir() })
                Check(ClassicLibrary.WhyNotClearable(bad) != null, $"refused: \"{bad}\" ({ClassicLibrary.WhyNotClearable(bad)})");
            var userCache = ProjectSettings.GlobalizePath("user://classic");
            Check(ClassicLibrary.WhyNotClearable(userCache) == null, "a cache under user:// may be cleared (not cleared here)");
            Check(l.ClearCacheButton.Disabled && !l.ClearCache() && l.CacheBytes() == 3000,
                $"a folder outside user:// without the marker is not cleared: \"{l.CacheLabel.Text}\"");
            ClassicLibrary.WriteMarker(Tmp("classic"));
            l.Refresh();
            Check(!l.ClearCacheButton.Disabled, "with the marker, Clear cache is on");
            l.ClearCacheConfirm.EmitSignal(AcceptDialog.SignalName.Confirmed);
            Check(l.CacheBytes() == 0 && !System.IO.Directory.Exists(Tmp("classic", "chr"))
                && System.IO.File.Exists(Tmp("classic", "keep.txt")),
                $"the cache cleared, other files kept: \"{l.CacheLabel.Text}\"");

            // Start
            Check(GameHost.Instance is { Started: false } && !ModLoader.MountedAll, "nothing started before Start");
            var orderTime = System.IO.File.GetLastWriteTimeUtc(orderFile);
            await Frames(2);
            l.StartButton.EmitSignal(BaseButton.SignalName.Pressed);
            await Frames(5);
            Check(System.IO.File.ReadAllText(orderFile) == text && System.IO.File.GetLastWriteTimeUtc(orderFile) == orderTime,
                "Start does not write load_order.txt again");
            var scene = GetTree().CurrentScene;
            Check(scene?.SceneFilePath == Launcher.MainScene, $"handed over to {scene?.SceneFilePath}");
            Check(GameHost.Instance is { Started: true } && GameHost.Instance.Game?.IsRunning != true,
                "GameHost started, no game running (--no-game)");
            var names = ModLoader.Packs.Select(p => p.Name).ToList();
            Check(names.SequenceEqual(["alpha", "gamma"]), $"mounted: {string.Join(", ", names)}");
            var shared = FileAccess.GetFileAsString("res://mods/selftest_launcher.txt");
            Check(shared == "gamma", $"the later pack's file wins: \"{shared}\"");
            Check(!FileAccess.FileExists("res://mods/beta/id.txt"), "the disabled pack is not mounted");
        }
        catch (System.Exception e)
        {
            Check(false, $"exception: {e}");
        }
        finally
        {
            try
            {
                System.IO.Directory.Delete(_tmp, true);
            }
            catch (System.IO.IOException)
            {
            }
        }
        var pass = _fails.Count == 0;
        GD.Print(pass
            ? "ffx selftest: PASS (launcher: game folder checked and remembered, packs disabled and reordered in load_order.txt, language, cache cleared, handed over to main.tscn with the packs mounted)"
            : $"ffx selftest: FAIL ({string.Join("; ", _fails)})");
        GameHost.Instance?.Game?.Stop();
        GetTree().Quit(pass ? 0 : 1);
    }
}

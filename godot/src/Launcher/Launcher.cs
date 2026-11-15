using System;
using System.Collections.Generic;
using System.Linq;
using GdFfx.Remake;
using Godot;

namespace GdFfx.Launcher;

/// <summary>
/// The launcher (<c>scenes/launcher.tscn</c>, the project's main scene; docs/launcher.md):
/// the game folder (checked, chosen with a folder dialog, remembered in
/// <see cref="GameSettings.SettingsPath"/>), the mod packs of each mod folder (enabled or not,
/// in order; written to the folder's <c>load_order.txt</c> at each change), the game's
/// language, the classic cache with a button to clear it, and Start (or Start at a developer
/// save), which hands over to <see cref="GameHost.Start"/> and <c>scenes/main.tscn</c>.
/// Skipped (the game starts at once) with <c>--game-dir</c>, <c>FFX_GAME_DIR</c>,
/// <c>--devsave</c>, any <c>--selftest*</c> but <c>--selftest-launcher</c>, or
/// <c>--no-launcher</c> (<see cref="ShouldSkip"/>).
/// </summary>
public partial class Launcher : Control
{
    public const string MainScene = "res://scenes/main.tscn";

    /// <summary>One mod folder and its packs, in load order.</summary>
    public sealed class ModFolder
    {
        public required string Dir { get; init; }
        public required List<ModLoader.Entry> Entries { get; init; }
    }

    // the controls (public for the self-test, which drives them as a player would)
    public LineEdit GameDirEdit { get; private set; } = null!;
    public Label GameDirStatus { get; private set; } = null!;
    public Button BrowseButton { get; private set; } = null!;
    public FileDialog FolderDialog { get; private set; } = null!;
    public Label LanguageLabel { get; private set; } = null!;
    public VBoxContainer ModList { get; private set; } = null!;
    public Label CacheLabel { get; private set; } = null!;
    public Button ClearCacheButton { get; private set; } = null!;
    public ConfirmationDialog ClearCacheConfirm { get; private set; } = null!;
    public Button StartButton { get; private set; } = null!;
    public SpinBox DevSaveSpin { get; private set; } = null!;
    public Button DevSaveButton { get; private set; } = null!;
    public Label StartError { get; private set; } = null!;

    /// <summary>The mod folders shown (<see cref="ModLoader.Dirs"/>).</summary>
    public List<ModFolder> Folders { get; } = new();

    /// <summary>The game's documents folder (for its language) and the classic cache; settable before <see cref="Refresh"/>.</summary>
    public string DocumentsDir { get; set; } = "";
    public string ClassicDir { get; set; } = "";

    /// <summary>The checked game folder, or null.</summary>
    public string? ValidGameDir { get; private set; }

    /// <summary>Whether the launcher hands over to the game at once.</summary>
    public static bool ShouldSkip()
    {
        if (GameSettings.Flag("selftest-launcher"))
            return false;
        var args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        return !string.IsNullOrEmpty(GameSettings.Arg("game-dir"))
            || !string.IsNullOrEmpty(OS.GetEnvironment("FFX_GAME_DIR"))
            || GameSettings.Arg("devsave") != null
            || args.Any(a => a.StartsWith("--selftest", StringComparison.Ordinal))
            || GameSettings.Flag("no-launcher");
    }

    public override void _Ready()
    {
        if (ShouldSkip())
        {
            GD.Print("ffx: launcher skipped");
            HandOver(null, 0, always: true);
            return;
        }
        if (GameSettings.Flag("selftest-launcher"))
            LauncherSelfTest.Prepare();
        DocumentsDir = GameSettings.DocumentsDir();
        ClassicDir = GameSettings.ClassicDir();
        Build();
        var (dir, source) = GameSettings.GameDirFrom();
        GameDirEdit.Text = dir;
        if (source.Length > 0)
            GD.Print($"ffx: launcher: game folder from {source}");
        Refresh();
        if (GameSettings.Flag("selftest-launcher"))
            GetTree().Root.CallDeferred(Node.MethodName.AddChild, new LauncherSelfTest(this));
    }

    /// <summary>Reads everything again: the game folder's check, the language, the packs, the cache.</summary>
    public void Refresh()
    {
        CheckGameDir(GameDirEdit.Text);
        ShowLanguage();
        ScanMods();
        ShowCache();
    }

    private void Build()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        var bg = new Panel();
        bg.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(bg);
        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (var side in new[] { "left", "right", "top", "bottom" })
            margin.AddThemeConstantOverride($"margin_{side}", 24);
        AddChild(margin);
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        margin.AddChild(scroll);
        var col = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        col.AddThemeConstantOverride("separation", 10);
        scroll.AddChild(col);

        var title = new Label { Text = "gd-ffx" };
        title.AddThemeFontSizeOverride("font_size", 28);
        col.AddChild(title);
        col.AddChild(new Label
        {
            Text = "Needs your own copy of FINAL FANTASY X/X-2 HD Remaster. The game folder is only read.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        });

        // the game folder
        col.AddChild(Heading("Game folder (the one with FFX.exe and data/)"));
        var row = new HBoxContainer();
        GameDirEdit = new LineEdit { SizeFlagsHorizontal = SizeFlags.ExpandFill, PlaceholderText = "path to the game folder" };
        GameDirEdit.TextSubmitted += t => CheckGameDir(t, save: true);
        GameDirEdit.FocusExited += () => CheckGameDir(GameDirEdit.Text, save: true);
        row.AddChild(GameDirEdit);
        BrowseButton = new Button { Text = "Browse..." };
        BrowseButton.Pressed += () => FolderDialog.PopupCentered(new Vector2I(800, 500));
        row.AddChild(BrowseButton);
        col.AddChild(row);
        GameDirStatus = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        col.AddChild(GameDirStatus);
        FolderDialog = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.OpenDir,
            Access = FileDialog.AccessEnum.Filesystem,
            Title = "The game folder (with FFX.exe)",
            UseNativeDialog = true,
        };
        FolderDialog.DirSelected += d =>
        {
            GameDirEdit.Text = d;
            CheckGameDir(d, save: true);
        };
        AddChild(FolderDialog);

        LanguageLabel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        col.AddChild(LanguageLabel);

        // the mod packs
        col.AddChild(Heading("Mod packs (first to last: later packs win)"));
        ModList = new VBoxContainer();
        col.AddChild(ModList);
        col.AddChild(new Label
        {
            Text = "Install only packs you trust: a pack can replace any of gd-ffx's files and run scripts.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        });

        // the classic cache
        col.AddChild(Heading("Classic cache (maps and characters imported from your copy)"));
        var cacheRow = new HBoxContainer();
        CacheLabel = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        cacheRow.AddChild(CacheLabel);
        ClearCacheButton = new Button { Text = "Clear cache" };
        ClearCacheButton.Pressed += () => ClearCacheConfirm.PopupCentered();
        cacheRow.AddChild(ClearCacheButton);
        col.AddChild(cacheRow);
        ClearCacheConfirm = new ConfirmationDialog
        {
            DialogText = "Delete the imported maps and characters? They are imported again when needed.",
        };
        ClearCacheConfirm.Confirmed += () => ClearCache();
        AddChild(ClearCacheConfirm);

        // start
        col.AddChild(new HSeparator());
        var startRow = new HBoxContainer();
        StartButton = new Button { Text = "Start", CustomMinimumSize = new Vector2(160, 40) };
        StartButton.Pressed += () => StartGame(0);
        startRow.AddChild(StartButton);
        startRow.AddChild(new Control { CustomMinimumSize = new Vector2(32, 0) });
        DevSaveButton = new Button { Text = "Start at developer save" };
        DevSaveButton.TooltipText = "For testing: the saves the developers left in the archive (1..110; 7 is Besaid beach)";
        DevSaveButton.Pressed += () => StartGame((int)DevSaveSpin.Value);
        startRow.AddChild(DevSaveButton);
        DevSaveSpin = new SpinBox { MinValue = 1, MaxValue = 110, Value = 7, Step = 1 };
        startRow.AddChild(DevSaveSpin);
        col.AddChild(startRow);
        StartError = new Label { Visible = false, AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = new Color(1, 0.5f, 0.5f) };
        col.AddChild(StartError);
    }

    private static Label Heading(string text)
    {
        var l = new Label { Text = text };
        l.AddThemeFontSizeOverride("font_size", 18);
        return l;
    }

    /// <summary>Checks <paramref name="dir"/> as the game folder; remembers it when it is one and <paramref name="save"/>.</summary>
    public bool CheckGameDir(string dir, bool save = false)
    {
        dir = dir.Trim();
        var why = GameSettings.CheckGameDir(dir);
        ValidGameDir = why == null ? dir : null;
        var (setDir, source) = GameSettings.GameDirFrom();
        GameDirStatus.Text = why == null
            ? "OK: FFX.exe and data/FFX_Data.vbf found."
            : $"Not a game folder: {why}.";
        // the project setting is the only source above the settings file the launcher shows with
        if (why == null && source.Length > 0 && source != GameSettings.SettingsPath && setDir != dir)
            GameDirStatus.Text += $" (Used instead of {source} when started from here.)";
        StartButton.Disabled = why != null;
        DevSaveButton.Disabled = why != null;
        if (why == null && save && GameSettings.SavedGameDir() != dir && !GameSettings.SaveGameDir(dir))
            GD.PrintErr($"ffx: launcher: cannot write {GameSettings.SettingsPath}");
        return why == null;
    }

    private void ShowLanguage()
    {
        var lang = GameSettings.GameLanguage(DocumentsDir);
        LanguageLabel.Text = lang == null
            ? "Language: not set yet (the game starts in Japanese and writes its settings; change it in the game's PC menu, Esc)."
            : $"Language: {LanguageName(lang)} ({lang}), from the game's settings (the game's PC menu, Esc, changes it).";
    }

    /// <summary>The name of a <c>GameSetting.ini</c> language.</summary>
    public static string LanguageName(string code) => code.ToLowerInvariant() switch
    {
        "jp" => "Japanese",
        "en" => "English",
        "fr" => "French",
        "de" => "German",
        "it" => "Italian",
        "es" => "Spanish",
        "kr" => "Korean",
        "ch" => "Chinese",
        _ => "unknown",
    };

    /// <summary>Reads the mod folders' packs and load orders again and rebuilds the list.</summary>
    public void ScanMods()
    {
        Folders.Clear();
        foreach (var dir in ModLoader.Dirs)
        {
            var entries = ModLoader.Scan(dir, out var missing);
            foreach (var m in missing)
                GD.PrintErr($"ffx: launcher: {dir}/{ModLoader.LoadOrderFile} names {m}, which is not there");
            Folders.Add(new ModFolder { Dir = dir, Entries = entries });
        }
        RebuildModList();
    }

    private void RebuildModList()
    {
        foreach (var c in ModList.GetChildren())
        {
            ModList.RemoveChild(c);
            c.QueueFree();
        }
        if (GameSettings.Flag("no-mods"))
        {
            ModList.AddChild(new Label { Text = "None (--no-mods)." });
            return;
        }
        for (int fi = 0; fi < Folders.Count; fi++)
        {
            var folder = Folders[fi];
            ModList.AddChild(new Label
            {
                Text = $"{folder.Dir}  ({ModLoader.Globalize(folder.Dir)})",
                Modulate = new Color(1, 1, 1, 0.7f),
            });
            if (folder.Entries.Count == 0)
                ModList.AddChild(new Label { Text = "    no packs (*.pck)" });
            for (int i = 0; i < folder.Entries.Count; i++)
            {
                var (f, file) = (fi, folder.Entries[i].File);
                var row = new HBoxContainer { Name = $"Pack_{fi}_{file.Replace('.', '_')}" };
                row.SetMeta("pack", file);
                var check = new CheckBox
                {
                    Name = "Enabled",
                    Text = file,
                    ButtonPressed = folder.Entries[i].Enabled,
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                };
                check.Toggled += on => SetEnabled(f, file, on);
                row.AddChild(check);
                var up = new Button { Name = "Up", Text = "Up", Disabled = i == 0 };
                up.Pressed += () => Move(f, file, -1);
                row.AddChild(up);
                var down = new Button { Name = "Down", Text = "Down", Disabled = i == folder.Entries.Count - 1 };
                down.Pressed += () => Move(f, file, 1);
                row.AddChild(down);
                ModList.AddChild(row);
            }
        }
    }

    /// <summary>The list's row of a pack (its <c>Enabled</c> check box, <c>Up</c> and <c>Down</c> buttons).</summary>
    public HBoxContainer? PackRow(int folder, string file) =>
        ModList.GetChildren().OfType<HBoxContainer>()
            .FirstOrDefault(r => r.Name == $"Pack_{folder}_{file.Replace('.', '_')}" && !r.IsQueuedForDeletion());

    /// <summary>Enables or disables a pack and writes the folder's load order.</summary>
    public void SetEnabled(int folder, string file, bool on)
    {
        var entries = Folders[folder].Entries;
        var i = entries.FindIndex(e => e.File == file);
        if (i < 0 || entries[i].Enabled == on)
            return;
        entries[i] = entries[i] with { Enabled = on };
        SaveMods(folder);
    }

    /// <summary>Moves a pack <paramref name="delta"/> places (negative: earlier) and writes the folder's load order.</summary>
    public void Move(int folder, string file, int delta)
    {
        var entries = Folders[folder].Entries;
        var i = entries.FindIndex(e => e.File == file);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= entries.Count)
            return;
        (entries[i], entries[j]) = (entries[j], entries[i]);
        SaveMods(folder);
        // rebuilt after the button's signal returns
        CallDeferred(MethodName.RebuildModList);
    }

    /// <summary>Writes one folder's load order (<see cref="ModLoader.Save"/>).</summary>
    public void SaveMods(int folder)
    {
        var f = Folders[folder];
        try
        {
            ModLoader.Save(f.Dir, f.Entries);
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
        {
            GD.PrintErr($"ffx: launcher: cannot write {f.Dir}/{ModLoader.LoadOrderFile}: {e.Message}");
        }
    }

    /// <summary>The cache's folders the launcher may clear: the importer's <c>map/</c> and <c>chr/</c>.</summary>
    private IEnumerable<string> CacheDirs() =>
        new[] { "map", "chr" }.Select(k => System.IO.Path.Combine(ClassicDir, k)).Where(System.IO.Directory.Exists);

    /// <summary>The bytes of the classic cache's imports.</summary>
    public long CacheBytes()
    {
        long n = 0;
        foreach (var d in CacheDirs())
            foreach (var f in System.IO.Directory.EnumerateFiles(d, "*", System.IO.SearchOption.AllDirectories))
                n += new System.IO.FileInfo(f).Length;
        return n;
    }

    private void ShowCache()
    {
        var why = ClassicLibrary.WhyNotClearable(ClassicDir);
        CacheLabel.Text = $"{ClassicDir}: {ByteSize(CacheBytes())}"
            + (why == null ? "" : $" (not cleared from here: {why})");
        ClearCacheButton.Disabled = why != null;
    }

    /// <summary>
    /// Deletes the classic cache's imports (its <c>map/</c> and <c>chr/</c> folders, nothing
    /// else), only when the folder is safe to clear (<see cref="ClassicLibrary.WhyNotClearable"/>);
    /// false when refused.
    /// </summary>
    public bool ClearCache()
    {
        if (ClassicLibrary.WhyNotClearable(ClassicDir) is { } why)
        {
            GD.PrintErr($"ffx: launcher: the classic cache {ClassicDir} is not cleared: {why}");
            ShowCache();
            return false;
        }
        foreach (var d in CacheDirs())
        {
            try
            {
                System.IO.Directory.Delete(d, true);
            }
            catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
            {
                GD.PrintErr($"ffx: launcher: cannot clear {d}: {e.Message}");
            }
        }
        GD.Print($"ffx: launcher: classic cache cleared ({ClassicDir})");
        ShowCache();
        return true;
    }

    /// <summary>A byte count for people.</summary>
    public static string ByteSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.0} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.00} GB",
    };

    /// <summary>Starts the game from the checked folder (at developer save <paramref name="devsave"/> when above 0).</summary>
    public void StartGame(int devsave)
    {
        if (!CheckGameDir(GameDirEdit.Text, save: true) || ValidGameDir == null)
            return;
        // load_order.txt is written at each change of the list, not here
        GD.Print($"ffx: launcher: start{(devsave > 0 ? $" at developer save {devsave}" : "")}");
        if (!HandOver(ValidGameDir, devsave))
        {
            // stay: the reason shown, the folder can be changed and Start pressed again
            StartError.Text = $"The game did not start: {GameHost.Instance?.Error}";
            StartError.Visible = true;
        }
    }

    /// <summary>
    /// Mounts the packs and boots the game (<see cref="GameHost.Start"/>), then loads
    /// <see cref="MainScene"/>; from the launcher's screen only when the game started
    /// (skipped, the main scene shows the error as before).
    /// </summary>
    private bool HandOver(string? gameDir, int devsave, bool always = false)
    {
        var ok = GameHost.Instance?.Start(gameDir, devsave) ?? false;
        if (ok || always)
            // deferred: not while this scene is in its _Ready or a button's signal
            GetTree().CallDeferred(SceneTree.MethodName.ChangeSceneToFile, MainScene);
        return ok;
    }
}

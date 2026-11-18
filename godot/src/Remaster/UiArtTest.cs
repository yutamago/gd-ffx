using System.Linq;
using GdFfx.Api;
using Godot;

namespace GdFfx.Remaster;

/// <summary>
/// R7b's check of replaced UI art (<c>--selftest-ui-art --devsave=7</c>; with <c>--shot-dir=DIR</c>
/// under a renderer, pictures): on Besaid beach the save sphere opens the game's save screen
/// (<see cref="SaveLoadView"/>, drawn natively from <c>saveload_*.swf</c>); then a UI art layer is
/// registered (generated test images, original: the cursor <see cref="SaveLoadView.ArrowClass"/> and
/// Tidus's portrait <c>face_565@_1</c> for every language's movie, <c>saveload</c>; the font of role
/// <c>ui</c> when <c>--ui-font=FILE</c> is given), or the editor's file (<c>--ui-art-file=FILE</c>
/// with <c>--ui-art-expect=FILE</c>). Checked: the screen draws the replacements (and the rest
/// classic), F8 draws all of it classic and the font the system's, F8 again the replacements; then
/// cancel closes the screen.
/// </summary>
public sealed class UiArtTest(double timeout, string? shotDir) : ScriptedTest<UiArtTest.Stage>(timeout, shotDir, "ui-art")
{
    public enum Stage { Boot, Settle, Choice, Open, Question, Saving, Classic, Replace, Replaced, Off, On, Close, Done }

    private readonly string _dir = ProjectSettings.GlobalizePath("user://selftest-ui-art");
    private string[] _expect = [];
    private bool _acted;
    private int _saves;

    private static Message? ChoiceWindow(Snapshot s) =>
        s.Messages.FirstOrDefault(m => m.State != 0 && (m.Flags >> 8) > 0 && m.Choice >= 0);

    protected override bool? Run(FfxGame game, Snapshot s)
    {
        var view = Find<StateView3D>("StateView3D");
        var sv = Find<SaveLoadView>("SaveLoadView");
        if (view == null || sv == null)
            return null;
        var st = FlashScreens.SaveLoad;
        switch (Phase)
        {
            case Stage.Boot when s.World.Map == BesaidTest.MapBeach && s.World.PlayerControl && view.Posed.Models > 0:
                Next(Stage.Settle);
                break;
            case Stage.Settle when PhaseT > 2:
                Press(Key.Enter);
                Next(Stage.Choice);
                break;
            case Stage.Choice when ChoiceWindow(s) is { } && PhaseT > 0.8:
                Press(Key.Enter);
                Next(Stage.Open);
                break;
            case Stage.Choice when PhaseT > 8:
                return Finish(false, "no choice at the save sphere");
            case Stage.Open when SaveLoadView.Active && st.Items.Count > 0 && st.Message == null && PhaseT > 1:
                // a saved file's row has the party's portraits: one saved first when there is none
                if (st.Items.Values.Any(i => i.Date.Length > 0))
                    Next(Stage.Classic);
                else
                {
                    _saves = SaveFiles.List(GameSettings.DocumentsDir(), false).Count(h => h != null);
                    Press(Key.Enter);
                    Next(Stage.Question);
                }
                break;
            case Stage.Question when st.Question is { } && PhaseT > 0.5:
                // "Save?" starts on Yes
                Press(Key.Enter);
                Next(Stage.Saving);
                break;
            case Stage.Saving when SaveFiles.List(GameSettings.DocumentsDir(), false).Count(h => h != null) > _saves
                && st.Question == null && st.Message == null && SaveLoadView.Active && st.Items.Values.Any(i => i.Date.Length > 0) && PhaseT > 1:
                Ok(true, "a file saved first (its row has the party's portraits)");
                Next(Stage.Classic);
                break;
            case Stage.Question or Stage.Saving when PhaseT > 20:
                return Finish(false, $"no save ({Phase}: question {st.Question}, message {st.Message})");
            case Stage.Classic when PhaseT > 0.5:
            {
                Shot("classic");
                var replaced = sv.Drawn.Where(d => d.Value).Select(d => d.Key).ToList();
                // the editor's file is a layer from the start (the command line): its classic look is F8's
                if (GameSettings.Arg("ui-art-file") != null)
                    Ok(sv.Drawn.Count > 0, $"the save screen ({st.Movie}) drawn with the editor's layer from the start: {sv.Drawn.Count} piece(s) of the movie's art, {replaced.Count} replaced");
                else
                    Ok(replaced.Count == 0 && sv.Drawn.Count > 0, $"the save screen ({st.Movie}) drawn classic first: {sv.Drawn.Count} piece(s) of the movie's art");
                Next(Stage.Replace);
                break;
            }
            case Stage.Replace:
                if (Setup() is { } f)
                    return Finish(false, f);
                Next(Stage.Replaced);
                break;
            case Stage.Replaced when PhaseT > 0.5:
            {
                Shot("replaced");
                var replaced = sv.Drawn.Where(d => d.Value).Select(d => d.Key).OrderBy(k => k).ToList();
                var classic = sv.Drawn.Where(d => !d.Value).Select(d => d.Key).ToList();
                bool all = _expect.All(replaced.Contains);
                Ok(all && classic.Count > 0, $"replaced: {string.Join(", ", replaced)} (expected {string.Join(", ", _expect)}); {classic.Count} other piece(s) classic");
                if (UiArt.Fonts.ContainsKey("ui"))
                    Ok(MessageView.UiFont.BaseFont != MessageView.SystemUiFont, $"the ui font replaced ({UiArt.Fonts["ui"].File})");
                Next(Stage.Off);
                break;
            }
            case Stage.Off when !_acted:
                _acted = true;
                Press(Replacements.Key);
                break;
            case Stage.Off when PhaseT > 0.6:
            {
                Shot("f8-classic");
                var replaced = sv.Drawn.Where(d => d.Value).Select(d => d.Key).ToList();
                Ok(!Replacements.On && replaced.Count == 0 && MessageView.UiFont.BaseFont == MessageView.SystemUiFont,
                    $"F8: everything classic ({replaced.Count} replaced piece(s), the system font {(MessageView.UiFont.BaseFont == MessageView.SystemUiFont ? "back" : "not back")})");
                _acted = false;
                Next(Stage.On);
                break;
            }
            case Stage.On when !_acted:
                _acted = true;
                Press(Replacements.Key);
                break;
            case Stage.On when PhaseT > 0.6:
            {
                var replaced = sv.Drawn.Where(d => d.Value).Select(d => d.Key).ToList();
                Ok(Replacements.On && _expect.All(replaced.Contains), $"F8 again: the replacements drawn ({replaced.Count})");
                Press(Key.Backspace);
                Next(Stage.Close);
                break;
            }
            case Stage.Close when !SaveLoadView.Active && PhaseT > 0.5:
                Next(Stage.Done);
                break;
            case Stage.Close when PhaseT > 6:
                Press(Key.Backspace);
                PhaseT = 0;
                break;
            case Stage.Done:
                UiArt.Unregister("selftest-ui-art");
                foreach (var made in new[] { "cursor.png", "portrait.png", "ui.tres" })
                    DirAccess.RemoveAbsolute($"{_dir}/{made}");
                return Finish(true, "UI art");
        }
        return null;
    }

    /// <summary>The test layer (generated images) or the editor's, registered.</summary>
    private string? Setup()
    {
        if (GameSettings.Arg("ui-art-file") is { } file)
        {
            // the editor's file is read from the command line by UiArt itself
            if (GameSettings.Arg("ui-art-expect") is not { } expect || !FileAccess.FileExists(expect))
                return "--ui-art-file needs --ui-art-expect=FILE (the art ids the editor assigned, one a line)";
            _expect = FileAccess.GetFileAsString(expect).Split('\n', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries);
            UiArt.Invalidate();
            Ok(true, $"the editor's UI art ({file}): {string.Join(", ", _expect)}");
            return null;
        }
        DirAccess.MakeDirRecursiveAbsolute(_dir);
        TestImage(64, 48, new Color(1f, 0.3f, 0.1f), new Color(1f, 0.9f, 0.2f)).SavePng($"{_dir}/cursor.png");
        TestImage(128, 128, new Color(0.2f, 0.6f, 1f), new Color(0.1f, 0.2f, 0.4f)).SavePng($"{_dir}/portrait.png");
        var face = UiArt.ArtId(SaveLoadView.FaceClass, "_1");
        var set = new RemasterUiArtSet();
        set.Art.Add(new RemasterUiArt { Movie = "saveload", Art = SaveLoadView.ArrowClass, Image = "cursor.png", Note = "a generated test image (UiArtTest)" });
        set.Art.Add(new RemasterUiArt { Movie = "saveload", Art = face, Image = "portrait.png", Note = "a generated test image (UiArtTest)" });
        if (GameSettings.Arg("ui-font") is { } font)
            set.Fonts.Add(new RemasterUiFont { Role = "ui", File = font });
        var path = $"{_dir}/ui.tres";
        if (ResourceSaver.Save(set, path) != Error.Ok)
            return $"cannot write {path}";
        _expect = [SaveLoadView.ArrowClass, face];
        UiArt.Register(path, "selftest-ui-art");
        Ok(true, $"a UI art layer registered ({path}): the cursor and portrait _1 for every language's save screen");
        return null;
    }

    /// <summary>A test image: diagonal stripes with a frame (original).</summary>
    public static Image TestImage(int w, int h, Color a, Color b)
    {
        var img = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                img.SetPixel(x, y, x < 3 || y < 3 || x >= w - 3 || y >= h - 3 ? Colors.White : ((x + y) / 8 % 2 == 0 ? a : b));
        return img;
    }
}

using System.Collections.Generic;
using System.Linq;
using GdFfx.Api;
using Godot;

namespace GdFfx;

/// <summary>
/// G5's check of the save screen (<c>--selftest-save --devsave=7</c>; with
/// <c>--shot-dir=DIR</c> under a renderer, a picture of each step): on Besaid beach at the save
/// sphere, confirm and the sphere's first option open the game's save screen; the native
/// <see cref="SaveLoadView"/> draws what the game's save machine sets up on
/// <c>saveload_*.swf</c> (rows, help, count, focus); confirm on a row brings the game's
/// question, Right moves the pop-up's cursor (<c>changeSelectedBtn</c>) and Left back, Yes saves
/// (a file appears in the sandbox: <see cref="SaveFiles"/>); the sphere again, the saved row
/// shown, confirm on it brings the overwrite question, No keeps the file, cancel closes the
/// screen. Every call the game made on the movie is reported.
/// </summary>
/// <summary>
/// A check that drives the game through Godot's input, step by step (<typeparamref name="TPhase"/>):
/// keys pressed and let go 0.15 s later, the steps' results and pictures (<c>--shot-dir</c>).
/// </summary>
public abstract class ScriptedTest<TPhase>(double timeout, string? shotDir, string name) : ITest where TPhase : struct, System.Enum
{
    protected TPhase Phase;
    protected double T, PhaseT;
    private readonly List<string> _notes = new(), _fails = new();
    private readonly List<(Key Key, double At, bool Down)> _keys = new();

    public string Note { get; private set; } = "";

    protected static T? Find<T>(string node) where T : Node =>
        (Engine.GetMainLoop() as SceneTree)?.Root.FindChild(node, true, false) as T;

    protected void Press(Key key)
    {
        _keys.Add((key, T, true));
        _keys.Add((key, T + 0.15, false));
    }

    protected void Next(TPhase p)
    {
        GD.Print($"ffx selftest: {Phase} -> {p} at {T:F1} s");
        Phase = p;
        PhaseT = 0;
    }

    protected void Ok(bool ok, string what)
    {
        GD.Print($"ffx selftest: {(ok ? "ok  " : "FAIL")} {what}");
        (ok ? _notes : _fails).Add(what);
    }

    protected void Shot(string picture)
    {
        if (shotDir == null || DisplayServer.GetName() == "headless")
            return;
        System.IO.Directory.CreateDirectory(shotDir);
        var file = System.IO.Path.Combine(shotDir, $"{name}-{picture}.png");
        (Engine.GetMainLoop() as SceneTree)?.Root.GetViewport().GetTexture().GetImage().SavePng(file);
        GD.Print($"ffx selftest: picture {file}");
    }

    protected bool? Finish(bool pass, string why)
    {
        var calls = string.Join(", ", FlashScreens.CallCounts.Where(k => k.Key.StartsWith("saveload")).Select(k => $"{k.Key.Split(' ')[1]} x{k.Value}"));
        Note = $"{why}; {string.Join("; ", _notes)}{(_fails.Count > 0 ? $"; FAILED: {string.Join("; ", _fails)}" : "")}; the game's save screen calls: {calls}";
        return pass && _fails.Count == 0;
    }

    public bool? Step(FfxGame game, GameState gs, GameStateView v, double delta)
    {
        T += delta;
        PhaseT += delta;
        for (int i = 0; i < _keys.Count; i++)
        {
            var (key, at, down) = _keys[i];
            if (T < at)
                continue;
            Godot.Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = key, Keycode = key, Pressed = down });
            _keys.RemoveAt(i--);
        }
        if (T > timeout)
            return Finish(false, $"timed out in {Phase}");
        return v.Current is { } s ? Run(game, s) : null;
    }

    /// <summary>One frame of the script, with the snapshot.</summary>
    protected abstract bool? Run(FfxGame game, Snapshot s);
}

public sealed class SaveTest(double timeout, string? shotDir) : ScriptedTest<SaveTest.Stage>(timeout, shotDir, "save")
{
    public enum Stage { Boot, Settle, Choice, Open, List, Question, Right, Left, Saving, Saved, Choice2, Open2, List2, Question2, No, Close, Done }

    private int _round;
    private int _savesBefore = -1;
    private string _saved = "";

    private string _fingerprint = "";

    /// <summary>The saves' slots and play times (an overwrite changes the time).</summary>
    private static string Fingerprint() =>
        string.Join(" ", SaveFiles.List(GameSettings.DocumentsDir(), false).Where(h => h != null).Select(h => $"{h!.Slot}:{h.PlayTime}"));

    private static int Saves() => SaveFiles.List(GameSettings.DocumentsDir(), false).Count(h => h != null);

    private static Message? ChoiceWindow(Snapshot s) =>
        s.Messages.FirstOrDefault(m => m.State != 0 && (m.Flags >> 8) > 0 && m.Choice >= 0);

    private static string Rows(SaveLoadView v) => string.Join(" / ", v.Shown);

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
                _savesBefore = Saves();
                Next(Stage.Settle);
                break;
            case Stage.Settle when PhaseT > 2:
            case Stage.Saved when PhaseT > 2 && s.World.PlayerControl && s.Menus.SaveState == 0:
                Press(Key.Enter);
                Next(Phase == Stage.Settle ? Stage.Choice : Stage.Choice2);
                break;
            case Stage.Choice or Stage.Choice2 when ChoiceWindow(s) is { } m && PhaseT > 0.8:
                // the sphere's first option: the save screen
                Ok(m.Choice == 0, $"the sphere's choice on option 0");
                Press(Key.Enter);
                Next(Phase == Stage.Choice ? Stage.Open : Stage.Open2);
                break;
            case Stage.Choice or Stage.Choice2 when PhaseT > 8:
                return Finish(false, "no choice at the save sphere");
            case Stage.Open or Stage.Open2 when SaveLoadView.Active && st.Items.Count > 0 && st.Message == null && PhaseT > 1:
                _round++;
                Ok(sv.Visible && sv.Shown.Count > 0, $"save screen {_round} (save state {s.Menus.SaveState:x}): help \"{st.Help}\", count {st.Count}, focus {st.Focus}, rows: {Rows(sv)}");
                Shot($"list-{_round}");
                Next(Phase == Stage.Open ? Stage.List : Stage.List2);
                break;
            case Stage.Open or Stage.Open2 when PhaseT > 15:
                return Finish(false, $"the save screen did not come (save state {s.Menus.SaveState:x}, movie set up {st.Shown}, items {st.Items.Count}, message {st.Message})");
            case Stage.List when PhaseT > 0.5:
                // the focused row (the first: a new file's)
                Press(Key.Enter);
                Next(Stage.Question);
                break;
            case Stage.List2 when PhaseT > 0.5:
            {
                // the row of the file saved in round 1
                var saved = st.Items.Values.FirstOrDefault(i => i.Date.Length > 0 && i.Label.Length == 0);
                Ok(saved != null, $"the saved file listed: {saved?.Slot} {saved?.Place} {saved?.Date}");
                if (saved == null)
                    return Finish(false, "no saved row in round 2");
                if (st.Focus < saved.Index)
                    Press(Key.Down);
                else if (st.Focus > saved.Index)
                    Press(Key.Up);
                else
                {
                    Press(Key.Enter);
                    Next(Stage.Question2);
                }
                PhaseT = 0;
                break;
            }
            case Stage.Question when st.Question is { } && PhaseT > 0.5:
                Ok(sv.Shown.Any(r => r.StartsWith("[question]")) && st.Button == 0, $"question {_round}, the cursor on {st.Question?.Yes}: {sv.Shown.Last()}");
                Shot($"question-{_round}");
                Press(Key.Right);
                Next(Stage.Right);
                break;
            case Stage.Question2 when st.Question is { } && PhaseT > 0.5:
                // the overwrite question starts on No
                Ok(sv.Shown.Any(r => r.StartsWith("[question]")) && st.Button == 1, $"question {_round}, the cursor on {st.Question?.No}: {sv.Shown.Last()}");
                Shot($"question-{_round}");
                Next(Stage.No);
                break;
            case Stage.Question or Stage.Question2 when PhaseT > 6:
                return Finish(false, $"no question after confirm (rows: {Rows(sv)}, message {st.Message})");
            case Stage.Right when PhaseT > 0.6:
                Ok(st.Button == 1, $"Right moves the pop-up's cursor to {st.Question?.No} (button {st.Button})");
                Press(Key.Left);
                Next(Stage.Left);
                break;
            case Stage.Left when PhaseT > 0.6:
                Ok(st.Button == 0, $"Left moves it back to {st.Question?.Yes} (button {st.Button})");
                Press(Key.Enter);
                Next(Stage.Saving);
                break;
            case Stage.Saving:
                if (st.Message != null && _saved.Length == 0)
                {
                    _saved = st.Message;
                    Shot("saving");
                }
                if (Saves() > _savesBefore && st.Question == null && st.Message == null && PhaseT > 1)
                {
                    Ok(true, $"Yes saved: {Saves()} file(s), was {_savesBefore} (message shown: \"{_saved}\")");
                    if (SaveLoadView.Active)
                        Press(Key.Backspace);
                    Next(Stage.Saved);
                }
                else if (PhaseT > 20)
                    return Finish(false, $"no file after Yes (message {st.Message}, question {st.Question})");
                break;
            case Stage.Saved when PhaseT > 6 && SaveLoadView.Active:
                Press(Key.Backspace);
                PhaseT = 0;
                break;
            case Stage.No when PhaseT > 0.6:
            {
                _fingerprint = Fingerprint();
                Press(Key.Enter);
                Next(Stage.Close);
                break;
            }
            case Stage.Close when PhaseT > 1.5 && st.Question == null:
                Ok(Fingerprint() == _fingerprint, $"No keeps the file as it was ({Fingerprint()})");
                Press(Key.Backspace);
                Next(Stage.Done);
                break;
            case Stage.Done when !SaveLoadView.Active && PhaseT > 1:
                return Finish(true, $"cancel closed the save screen (save state {s.Menus.SaveState:x})");
            case Stage.Done when PhaseT > 4 && SaveLoadView.Active:
                Press(Key.Backspace);
                PhaseT = 0;
                break;
        }
        return null;
    }
}

using System.Collections.Generic;
using System.Linq;
using GdFfx.Api;
using Godot;

namespace GdFfx;

/// <summary>
/// G5's check of the playtest's UX requests (<c>--selftest-ux --devsave=7</c>; with
/// <c>--shot-dir=DIR</c> under a renderer, a picture of each screen): on Besaid beach at the
/// save sphere,
/// <list type="number">
/// <item>the choice: confirm opens the sphere's two-way choice; the native box draws the
/// options with the cursor on the game's live choice (API 11's <c>Message.choice</c>), which
/// follows Down, Up, Down before the confirm, and the confirm picks 1 (<c>picked</c>);</item>
/// <item>the ESC menu: Esc opens the game's PC menu (<c>Menus.esc_menu</c>) and the native
/// ESC menu shows its entries; System setting opens the options overlay, which changes the
/// frame cap (applied and saved), the glow and the game's speed (x2: the updates come about
/// twice as often, which F9's FPS overlay shows); Esc closes it; under the menu play halts (the
/// event clock and the characters' motions stand still) and the view's shader clock is held, as
/// the original's picture stands still;</item>
/// <item>Change parameters: the entry is enabled as the game's page enables it; its page shows
/// the game's three rows; "MAX gil"'s Get brings the game's confirmation, No leaves the gil, Yes
/// gives 999,999,999 (the game's <c>MsDebugFullGill</c>, through its own callbacks); Esc twice
/// resumes the game;</item>
/// <item>the boosters: F3 steps the encounter mode (the badge and the game's tip shown), F1
/// the speed-up, each pressed back to the start;</item>
/// <item>the pause: Start (B) where the game pauses shows the pause screen (reported, not
/// required: the field may not pause);</item>
/// <item>a treasure's message as the snapshot now gives it (its <c>{VAR}</c> expanded) keeps
/// its text through <see cref="MessageView.Clean"/>.</item>
/// </list>
/// The settings go to a temporary file (unless <c>--settings=PATH</c>), removed at the end.
/// </summary>
public sealed class UxTest : ITest
{
    private readonly double _timeout;
    private readonly string? _shots;
    private readonly string? _tmpSettings;

    public UxTest(double timeout, string? shotDir)
    {
        _timeout = timeout;
        _shots = shotDir;
        if (GameSettings.Arg("settings") == null)
        {
            _tmpSettings = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"gd-ffx-selftest-ux-{OS.GetProcessId()}.cfg");
            GameSettings.SettingsPath = _tmpSettings;
        }
    }

    private enum Phase
    {
        Boot, Settle, ChoiceOpen, ChoiceDown, ChoiceUp, ChoiceDown2, ChoicePick, EscOpen, EscMenu, Options, OptionsClosed,
        ParamsOpen, ParamsConfirm, ParamsNo, ParamsConfirm2, ParamsYes, ParamsBack, Resumed, Speed, F3, F3Back, F1, F1Back, Pause, Unpause, Done,
    }

    private Phase _phase = Phase.Boot;
    private double _t, _phaseT;
    private readonly List<string> _notes = new();
    private readonly List<string> _fails = new();
    private readonly List<(Key Key, double At, bool Down)> _keys = new();
    private int _f3Start = -1, _f3Presses;
    private uint _updatesAt;
    private double _speedAt;
    private uint _escUpdate;
    private double _escTime;
    private (uint EventFrame, uint PlayTime, float Motions, float GlobalTime, float ViewTime) _escHeld;
    private uint _gilBefore;
    private double _x1At = -1, _x1Rate;
    private uint _x1Update;

    public string Note { get; private set; } = "";

    private static T? Find<T>(string name) where T : Node =>
        (Engine.GetMainLoop() as SceneTree)?.Root.FindChild(name, true, false) as T;

    /// <summary>A key pressed now through Godot's input and let go 0.15 s later.</summary>
    private void Press(Key key)
    {
        _keys.Add((key, _t, true));
        _keys.Add((key, _t + 0.15, false));
    }

    private void Keys()
    {
        for (int i = 0; i < _keys.Count; i++)
        {
            var (key, at, down) = _keys[i];
            if (_t < at)
                continue;
            Godot.Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = key, Keycode = key, Pressed = down });
            _keys.RemoveAt(i--);
        }
    }

    private void Next(Phase p)
    {
        GD.Print($"ffx selftest: {_phase} -> {p} at {_t:F1} s");
        _phase = p;
        _phaseT = 0;
    }

    private void Ok(bool ok, string what)
    {
        GD.Print($"ffx selftest: {(ok ? "ok  " : "FAIL")} {what}");
        if (ok)
            _notes.Add(what);
        else
            _fails.Add(what);
    }

    private void Shot(string name)
    {
        if (_shots == null || DisplayServer.GetName() == "headless")
            return;
        System.IO.Directory.CreateDirectory(_shots);
        var file = System.IO.Path.Combine(_shots, name + ".png");
        (Engine.GetMainLoop() as SceneTree)!.Root.GetViewport().GetTexture().GetImage().SavePng(file);
        GD.Print($"ffx selftest: picture {file}");
    }

    private static Message? ChoiceWindow(Snapshot s) =>
        s.Messages.FirstOrDefault(m => m.State != 0 && (m.Flags >> 8) > 0 && m.Choice >= 0);

    private bool _shotPending;
    private string _shotName = "";

    /// <summary>A picture a few frames from now (the views laid out).</summary>
    private void ShotSoon(string name)
    {
        _shotPending = true;
        _shotName = name;
        _shotFrames = 6;
    }

    private int _shotFrames;

    public bool? Step(FfxGame game, GameState gs, GameStateView v, double delta)
    {
        _t += delta;
        _phaseT += delta;
        Keys();
        if (_shotPending && --_shotFrames <= 0)
        {
            _shotPending = false;
            Shot(_shotName);
        }
        if (!gs.Running && gs.Seconds > 1)
            return Finish(false, $"the game ended: {gs.Outcome}");
        if (_t > _timeout)
            return Finish(false, $"timed out in phase {_phase} ({DebugView.SnapshotLine(v)})");
        var s = v.Current;
        if (s == null)
            return null;
        var mv = Find<MessageView>("MessageView");
        var pv = Find<PauseView>("PauseView");
        var ov = Find<OptionsView>("OptionsView");
        var bv = Find<BoosterView>("BoosterView");
        var view = Find<StateView3D>("StateView3D");
        if (mv == null || pv == null || ov == null || bv == null || view == null)
            return Finish(false, "a view is missing from main.tscn");
        switch (_phase)
        {
            case Phase.Boot when s.World.Map == BesaidTest.MapBeach && s.World.PlayerControl && view.Posed.Models > 0:
                Next(Phase.Settle);
                break;
            case Phase.Settle when _x1At < 0:
                (_x1At, _x1Update) = (_t, s.Update);
                break;
            case Phase.Settle when _phaseT > 2:
                _x1Rate = (s.Update - _x1Update) / (_t - _x1At);
                Ok(s.Boosters is { SpeedLevel: 0, Powerup: false, Encounters: 1, AutoBattle: false },
                    $"a new game's boosters (speed {s.Boosters.SpeedLevel}, power-up {s.Boosters.Powerup}, encounters {s.Boosters.Encounters}, auto {s.Boosters.AutoBattle})");
                Press(Key.Enter);
                Next(Phase.ChoiceOpen);
                break;
            case Phase.ChoiceOpen:
                if (ChoiceWindow(s) is { } m0 && mv.Choices.Count > 0 && _phaseT > 0.6)
                {
                    Ok(m0.Choice == 0 && mv.Choices[0].Contains("▶ "), $"the save sphere's choice opens on its first option: {mv.Choices[0]}");
                    ShotSoon("choice-0");
                    Press(Key.Down);
                    Next(Phase.ChoiceDown);
                }
                else if (_phaseT > 8)
                    return Finish(false, $"no choice opened at the save sphere (messages: {string.Join("; ", mv.Shown)})");
                break;
            case Phase.ChoiceDown when _phaseT > 0.6:
            {
                var m = ChoiceWindow(s);
                var row = mv.Choices.FirstOrDefault() ?? "";
                Ok(m?.Choice == 1 && m.Picked == -1 && Marked(row) == 1, $"Down moves the live cursor before any confirm: {row}");
                Shot("choice-1");
                Press(Key.Up);
                Next(Phase.ChoiceUp);
                break;
            }
            case Phase.ChoiceUp when _phaseT > 0.6:
            {
                var m = ChoiceWindow(s);
                var row = mv.Choices.FirstOrDefault() ?? "";
                Ok(m?.Choice == 0 && Marked(row) == 0, $"Up moves it back: {row}");
                Press(Key.Down);
                Next(Phase.ChoiceDown2);
                break;
            }
            case Phase.ChoiceDown2 when _phaseT > 0.6:
                Ok(ChoiceWindow(s)?.Choice == 1, "Down again: option 1");
                Press(Key.Enter);
                Next(Phase.ChoicePick);
                break;
            case Phase.ChoicePick:
                if (s.Messages.Any(m => m.Picked == 1))
                    _picked = true;
                if (ChoiceWindow(s) == null && _phaseT > 1.0 && s.World.PlayerControl)
                {
                    Ok(_picked, $"the confirm picks option 1 (picked seen: {_picked}); the choice closed");
                    Press(Key.Escape);
                    Next(Phase.EscOpen);
                }
                else if (_phaseT > 10)
                    return Finish(false, "the choice did not close after the pick");
                break;
            case Phase.EscOpen:
                if (pv.MenuOpen && s.Menus.EscMenu && _phaseT > 0.5)
                {
                    Ok(pv.Shown.Count == 5 && pv.Shown[0].StartsWith("▶ "), $"Esc: the game's PC menu is open and the native ESC menu shows [{string.Join(" | ", pv.Shown)}]");
                    Ok(GameHost.Instance?.Captured == true, "the ESC menu has the input (the game gets none)");
                    _escUpdate = s.Update;
                    _escTime = s.GameTime;
                    _escHeld = Held(s, view);
                    ShotSoon("esc-menu");
                    Next(Phase.EscMenu);
                }
                else if (_phaseT > 6)
                    return Finish(false, $"Esc did not open the game's PC menu (esc_menu {s.Menus.EscMenu}, paused {s.World.Paused})");
                break;
            case Phase.EscMenu when _phaseT > 1.0:
                if (!_menuKeys)
                {
                    _menuKeys = true;
                    _notes.Add($"{s.Update - _escUpdate} snapshot(s) in the 1 s under the game's ESC menu (game time {s.GameTime - _escTime:F2} s: the game's clock, which the original's runs on too)");
                    var held = Held(s, view);
                    // the play time and GlobalTime are the game's wall-clock counters (Atel_PlayTimeUpdate
                    // counts graphicGetTime): they run on as in the original, whose picture stands still
                    Ok(held.EventFrame == _escHeld.EventFrame && held.Motions == _escHeld.Motions && held.ViewTime == _escHeld.ViewTime,
                        $"play halts under the ESC menu: event frame {_escHeld.EventFrame} -> {held.EventFrame}, motion frames {_escHeld.Motions:F2} -> {held.Motions:F2}, the view's shader clock held {_escHeld.ViewTime:F3} -> {held.ViewTime:F3} (the game's GlobalTime {_escHeld.GlobalTime:F3} -> {held.GlobalTime:F3}, play time {_escHeld.PlayTime} -> {held.PlayTime} s: its wall-clock counters, as the original's)");
                    Press(Key.Down);
                    _keys.Add((Key.Enter, _t + 0.4, true));
                    _keys.Add((Key.Enter, _t + 0.55, false));
                }
                if (ov.IsOpen)
                {
                    Ok(true, "System setting opens the options overlay");
                    ShotSoon("options");
                    Next(Phase.Options);
                }
                else if (_phaseT > 4)
                    return Finish(false, $"System setting did not open the options (menu [{string.Join(" | ", pv.Shown)}])");
                break;
            case Phase.Options when _phaseT > 0.8 && _phaseT < 1.0:
            {
                var p = ov.Panel;
                Select(p, "fps", 2);
                var cfg = new ConfigFile();
                cfg.Load(GameSettings.Globalize(GameSettings.SettingsPath));
                Ok(Engine.MaxFps == 60 && cfg.GetValue("video", "max_fps", -1).AsInt32() == 60,
                    $"the frame cap set to 60: applied (Engine.MaxFps {Engine.MaxFps}) and saved ({GameSettings.SettingsPath})");
                var glow = (CheckBox)p.Controls["glow"];
                glow.ButtonPressed = false;
                Ok(GameSettings.Flag("no-glow") && cfg.GetValue("video", "glow", true).AsBool() == true,
                    "the glow switched off (the views' no-glow flag)");
                glow.ButtonPressed = true;
                Ok(!GameSettings.Flag("no-glow"), "and on again");
                Select(p, "speed", System.Array.IndexOf(UserSettings.Speeds, 2.0));
                Ok(System.Math.Abs(game.Speed - 2) < 1e-9, $"the game's speed set to x2 (the game's clock: x{game.Speed})");
                Select(p, "fps", 0);
                Ok(p.Focused == "preset", $"the options open with the focus on the first control, its caption lit ({p.Focused})");
                // the keyboard (or a pad's D-pad: the same ui_down action) moves the focus
                Press(Key.Down);
                _phaseT = 1.0;
                break;
            }
            case Phase.Options when _phaseT > 1.5 && !_focusShot:
            {
                var p = ov.Panel;
                Ok(p.Focused == "window", $"Down moves the focus to the next control, its caption lit ({p.Focused})");
                ((Control)p.Controls["fps_overlay"]).GrabFocus();
                Ok(p.Focused == "fps_overlay", "an unchecked box with the focus: framed in gold, its caption lit");
                _focusShot = true;
                ShotSoon("options-focus");
                break;
            }
            case Phase.Options when _phaseT > 2.2:
                Press(Key.Escape);
                Next(Phase.OptionsClosed);
                break;
            case Phase.OptionsClosed when _phaseT > 0.6:
                Ok(!ov.IsOpen && pv.MenuOpen && pv.PageName == "Home", $"Esc closes the options, back on the ESC menu ({pv.PageName})");
                Ok(pv.Esc is { Seen: true, Open: true, Parameters: true }, $"the game's page enables Change parameters here ({pv.Esc})");
                // from System setting (row 1) down to Change parameters (row 3)
                Press(Key.Down);
                _keys.Add((Key.Down, _t + 0.3, true));
                _keys.Add((Key.Down, _t + 0.45, false));
                _keys.Add((Key.Enter, _t + 0.7, true));
                _keys.Add((Key.Enter, _t + 0.85, false));
                _gilBefore = s.Save.Gil;
                Next(Phase.ParamsOpen);
                break;
            case Phase.ParamsOpen when _phaseT > 1.2:
                Ok(pv.PageName == "Parameters" && pv.Shown.Count == 3 && pv.Shown[0].StartsWith("▶ "),
                    $"Change parameters opens its page: {pv.Title} [{string.Join(" | ", pv.Shown)}]");
                ShotSoon("parameters");
                // MAX gil (row 2), Get
                Press(Key.Down);
                _keys.Add((Key.Down, _t + 0.3, true));
                _keys.Add((Key.Down, _t + 0.45, false));
                _keys.Add((Key.Enter, _t + 0.7, true));
                _keys.Add((Key.Enter, _t + 0.85, false));
                Next(Phase.ParamsConfirm);
                break;
            case Phase.ParamsConfirm:
                if (pv.PageName == "Confirm" && _phaseT > 1.0)
                {
                    Ok(pv.Esc.PopUp && pv.Esc.Row == 2 && pv.Shown.Count == 2 && pv.Shown[1].StartsWith("▶ "),
                        $"MAX gil's Get: the game set up its confirmation (row {pv.Esc.Row}, pop-up {pv.Esc.PopUp}): [{string.Join(" | ", pv.Shown)}], the cursor on No");
                    ShotSoon("parameters-confirm");
                    _keys.Add((Key.Enter, _t + 0.6, true));
                    _keys.Add((Key.Enter, _t + 0.75, false));
                    Next(Phase.ParamsNo);
                }
                else if (_phaseT > 5)
                    return Finish(false, $"no confirmation for MAX gil (page {pv.PageName}, note \"{pv.Note}\", {pv.Esc})");
                break;
            case Phase.ParamsNo when _phaseT > 1.6:
                Ok(pv.PageName == "Parameters" && !pv.Esc.PopUp && s.Save.Gil == _gilBefore,
                    $"No: back on the page, the game's pop-up answered, the gil unchanged ({_gilBefore} -> {s.Save.Gil})");
                Press(Key.Enter);
                Next(Phase.ParamsConfirm2);
                break;
            case Phase.ParamsConfirm2:
                if (pv.PageName == "Confirm" && _phaseT > 0.8)
                {
                    Press(Key.Up);
                    _keys.Add((Key.Enter, _t + 0.4, true));
                    _keys.Add((Key.Enter, _t + 0.55, false));
                    Next(Phase.ParamsYes);
                }
                else if (_phaseT > 5)
                    return Finish(false, $"no second confirmation (page {pv.PageName}, note \"{pv.Note}\")");
                break;
            case Phase.ParamsYes:
                if (s.Save.Gil == 999_999_999 && pv.PageName == "Parameters" && _phaseT > 0.8)
                {
                    Ok(true, $"Yes: the game's MsDebugFullGill ran: gil {_gilBefore} -> {s.Save.Gil}");
                    Press(Key.Escape);
                    Next(Phase.ParamsBack);
                }
                else if (_phaseT > 5)
                    return Finish(false, $"Yes did not give the gil (gil {s.Save.Gil}, page {pv.PageName}, note \"{pv.Note}\", {pv.Esc})");
                break;
            case Phase.ParamsBack when _phaseT > 0.6:
                Ok(pv.PageName == "Home" && pv.Shown[3].StartsWith("▶ "), $"Esc: back on the home page at Change parameters [{string.Join(" | ", pv.Shown)}]");
                Press(Key.Escape);
                Next(Phase.Resumed);
                break;
            case Phase.Resumed:
                if (!s.Menus.EscMenu && !pv.MenuOpen && _phaseT > 0.5)
                {
                    Ok(GameHost.Instance?.Captured == false, "Esc resumes: the game's menu closed, the input the game's again");
                    _updatesAt = s.Update;
                    _speedAt = _t;
                    Press(GameHost.FpsKey);
                    Next(Phase.Speed);
                }
                else if (_phaseT > 6)
                    return Finish(false, "Esc did not close the game's PC menu");
                break;
            case Phase.Speed when _phaseT > 3:
            {
                double rate = (s.Update - _updatesAt) / (_t - _speedAt);
                // against x1 in this run (a busy machine slows both): about twice, or above the game's 30
                Ok(rate > 40 || rate > 1.5 * _x1Rate, $"at x2 the game ran {rate:F1} updates a second (x1 here: {_x1Rate:F1}; its pace is 30)");
                var fps = Find<FpsView>("FpsView");
                Ok(fps is { Visible: true } && fps.Shown.Contains("updates/s") && fps.UpdatesPerSecond > 0 && UserSettings.Current.FpsOverlay,
                    $"F9 shows the FPS overlay: \"{fps?.Shown.Replace('\n', ' ')}\"");
                Shot("fps");
                Press(GameHost.FpsKey);
                UserSettings.Current.Speed = 1;
                UserSettings.Current.Apply();
                Ok(System.Math.Abs(game.Speed - 1) < 1e-9, "the speed back to x1");
                _f3Start = s.Boosters.Encounters;
                Press(Key.F3);
                Next(Phase.F3);
                break;
            }
            case Phase.F3:
                if (s.Boosters.Encounters != _f3Start && _phaseT > 0.3)
                {
                    Ok(bv.Toasts.Count > 0 && bv.Shown.Any(b => b.StartsWith("F3")),
                        $"F3 reaches the game: encounters {_f3Start} -> {s.Boosters.Encounters}; badge [{string.Join(", ", bv.Shown)}], tip \"{bv.Toasts.LastOrDefault()}\"");
                    ShotSoon("booster");
                    _f3Presses = 1;
                    Next(Phase.F3Back);
                }
                else if (_phaseT > 4)
                    return Finish(false, $"F3 changed nothing (encounters {s.Boosters.Encounters})");
                break;
            case Phase.F3Back when _phaseT > 1.0:
                if (s.Boosters.Encounters == _f3Start)
                {
                    Ok(true, $"F3 pressed {_f3Presses} times in all: encounters back to {_f3Start}");
                    Press(Key.F1);
                    Next(Phase.F1);
                }
                else if (_f3Presses < 4)
                {
                    Press(Key.F3);
                    _f3Presses++;
                    _phaseT = 0;
                }
                else
                    return Finish(false, $"F3 did not come back to {_f3Start}");
                break;
            case Phase.F1:
                if (s.Boosters.SpeedLevel == 1 && _phaseT > 0.3)
                {
                    Ok(true, $"F1 reaches the game (no longer the debug overlay's): Turbo x2 (speeding {s.Boosters.Speeding}, x{s.Boosters.Speed}); badge [{string.Join(", ", bv.Shown)}]");
                    Press(Key.F1);
                    _keys.Add((Key.F1, _t + 0.6, true));
                    _keys.Add((Key.F1, _t + 0.75, false));
                    Next(Phase.F1Back);
                }
                else if (_phaseT > 4)
                    return Finish(false, $"F1 changed nothing (speed level {s.Boosters.SpeedLevel})");
                break;
            case Phase.F1Back when _phaseT > 1.5:
                Ok(s.Boosters.SpeedLevel == 0, $"F1 twice more: back to Normal (level {s.Boosters.SpeedLevel})");
                Press(Key.B);
                Next(Phase.Pause);
                break;
            case Phase.Pause when _phaseT > 1.5:
                if (s.World.Paused)
                {
                    Ok(pv.PageName == "Pause", "Start pauses the game: the pause screen shown");
                    ShotSoon("pause");
                    _keys.Add((Key.B, _t + 0.5, true));
                    _keys.Add((Key.B, _t + 0.65, false));
                }
                else
                    _notes.Add("Start on the field did not pause the game (no pause screen to show)");
                Next(Phase.Unpause);
                break;
            case Phase.Unpause when _phaseT > 1.5:
            {
                // a field pickup's message as API 11 gives it: the item's name in place of {VAR:0}
                var chest = MessageView.Clean("{COLOR:41}Obtained {COLOR:44}Potion{COLOR:41} x2!{TIME:0}");
                Ok(chest == "Obtained Potion x2!", $"a treasure's message keeps its expanded name: \"{chest}\"");
                var unset = MessageView.Clean("Obtained {VAR:0}!");
                _notes.Add($"an unexpanded variable is left out: \"{unset}\"");
                var lay = MessageView.Layout("Save?\nYes\nNo", 2, 1);
                Ok(lay.Body == "Save?" && lay.Options.SequenceEqual(["Yes", "No"]) && lay.Cursor == 1, "a choice's layout: the text, the options, the cursor");
                return Finish(_fails.Count == 0, "");
            }
        }
        return null;
    }

    private bool _picked, _menuKeys, _focusShot;

    /// <summary>What moves while the game plays: the event clock, the play time, the characters' motion frames, the game's and the view's shader clock.</summary>
    private static (uint EventFrame, uint PlayTime, float Motions, float GlobalTime, float ViewTime) Held(Snapshot s, StateView3D view) =>
        (s.World.EventFrame, s.Save.PlayTime, s.Actors.Sum(a => a.MotionFrame), s.Look.GlobalTime, view.GlobalTime);

    /// <summary>The marked row of a choice line (<see cref="MessageView.Choices"/>), -1 none.</summary>
    private static int Marked(string line)
    {
        var start = line.IndexOf(": ", System.StringComparison.Ordinal);
        var end = line.LastIndexOf(" (choice", System.StringComparison.Ordinal);
        if (start < 0 || end < start)
            return -1;
        var rows = line[(start + 2)..end].Split(" | ");
        for (int i = 0; i < rows.Length; i++)
            if (rows[i].StartsWith("▶ ", System.StringComparison.Ordinal))
                return i;
        return -1;
    }

    private static void Select(OptionsPanel p, string key, int index)
    {
        var o = (OptionButton)p.Controls[key];
        o.Select(index);
        o.EmitSignal(OptionButton.SignalName.ItemSelected, index);
    }

    private bool Finish(bool pass, string why)
    {
        if (why.Length > 0)
            _fails.Add(why);
        Note = (pass ? "" : $"failed: {string.Join("; ", _fails)}; ") + string.Join("; ", _notes);
        if (_tmpSettings != null)
            System.IO.File.Delete(_tmpSettings);
        return pass && _fails.Count == 0;
    }
}

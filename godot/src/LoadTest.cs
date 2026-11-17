using System.Linq;
using GdFfx.Api;
using Godot;

namespace GdFfx;

/// <summary>
/// G5's check of the load screen (<c>--selftest-load --documents=DIR</c>, a sandbox with a save,
/// e.g. the one <c>--selftest-save</c> left; no <c>--devsave</c>): the start-up opening plays,
/// Start goes to the title, confirm on the title's menu (its cursor starts on Load when there are
/// saves) opens the game's load screen; the
/// native <see cref="SaveLoadView"/> draws the rows the game's save machine adds; confirm on the
/// focused row brings "Load this file?" with the cursor on Yes, Right moves it to No and confirm
/// on No keeps the list; confirm and Yes load the save: its map opens.
/// </summary>
public sealed class LoadTest(double timeout, string? shotDir) : ScriptedTest<LoadTest.Stage>(timeout, shotDir, "load")
{
    public enum Stage { Boot, Title, Menu, Open, List, Question, No, Kept, Question2, Loading, Done }

    private int _expected = -1;
    private int _presses;

    protected override bool? Run(FfxGame game, Snapshot s)
    {
        var sv = Find<SaveLoadView>("SaveLoadView");
        if (sv == null)
            return null;
        var st = FlashScreens.SaveLoad;
        var rows = string.Join(" / ", sv.Shown);
        switch (Phase)
        {
            // the opening's event (the campfire) runs: Start goes to the title
            case Stage.Boot when s.World.Map == DebugView.MapOpening && s.World.Event.Length > 0 && PhaseT > 3:
                Press(Key.B);
                Next(Stage.Title);
                break;
            case Stage.Title when s.World.Map == DebugView.MapTitle && PhaseT > 6:
                // the title's menu: its cursor starts on Load when there are saves
                Next(Stage.Menu);
                break;
            case Stage.Title when PhaseT > 8 && s.World.Map != DebugView.MapTitle && _presses++ < 4:
                Press(Key.B);
                PhaseT = 0;
                break;
            case Stage.Menu when PhaseT > 1:
                Press(Key.Enter);
                Next(Stage.Open);
                break;
            case Stage.Open when SaveLoadView.Active && st.Items.Count > 0 && st.Message == null && PhaseT > 1:
                Ok(sv.Visible, $"load screen (save state {s.Menus.SaveState:x}): help \"{st.Help}\", count {st.Count}, focus {st.Focus}, rows: {rows}");
                Shot("list");
                _expected = st.Items.TryGetValue(st.Focus, out var it) ? it.Index : -1;
                Press(Key.Enter);
                Next(Stage.Question);
                break;
            case Stage.Open when PhaseT > 15:
                return Finish(false, $"the load screen did not come (map {s.World.Map:x}, save state {s.Menus.SaveState:x}, items {st.Items.Count})");
            case Stage.Question when st.Question is { } q && PhaseT > 0.5:
                Ok(st.Button == 0, $"\"{q.Text}\" with the cursor on {q.Yes}: {sv.Shown.Last()}");
                Shot("question");
                Press(Key.Right);
                Next(Stage.No);
                break;
            case Stage.Question when PhaseT > 6:
                return Finish(false, $"no question after confirm (rows {rows})");
            case Stage.No when PhaseT > 0.6:
                Ok(st.Button == 1, $"Right: the cursor on {st.Question?.No}");
                Press(Key.Enter);
                Next(Stage.Kept);
                break;
            case Stage.Kept when PhaseT > 1:
                Ok(st.Question == null && SaveLoadView.Active, $"No keeps the list: {rows}");
                Press(Key.Enter);
                Next(Stage.Question2);
                break;
            case Stage.Question2 when st.Question is { } && PhaseT > 0.5:
                Press(Key.Enter);
                Next(Stage.Loading);
                break;
            case Stage.Loading when !SaveLoadView.Active && s.World.Map != DebugView.MapTitle && s.World.Map != 0 && s.World.Level.Length > 0
                && !s.World.Level.Contains("/titl/") && Find<StateView3D>("StateView3D") is { } view && view.MapLevel == s.World.Level && view.Posed.Models > 0:
                // the loaded map shown, its characters posed: 3 s more, as the map check
                Next(Stage.Done);
                break;
            case Stage.Done when PhaseT > 3:
                return Finish(true, $"Yes loaded row {_expected}: map {s.World.Map:x} ({s.World.Level}) shown");
            case Stage.Loading when PhaseT > 30:
                return Finish(false, $"no map after Yes (map {s.World.Map:x}, save state {s.Menus.SaveState:x}, message {st.Message})");
        }
        return null;
    }
}

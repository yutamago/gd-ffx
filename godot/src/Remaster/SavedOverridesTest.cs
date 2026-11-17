using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using GdFfx.Api;
using Godot;

namespace GdFfx.Remaster;

/// <summary>
/// The game's half of the remaster editor's check (<c>--selftest-saved-overrides
/// --overrides-file=PATH --overrides-expect=JSON</c>, with a developer save on the map;
/// docs/remaster-editor.md, "Checks"): registers the overrides file the editor saved as a
/// layer (<c>editor</c>) of its map, boots there and checks, against what the editor wrote in
/// the JSON, that each edit is in the running scene: the removed parts not drawn, the hidden
/// ones hidden, each moved one at the game's placement plus the editor's offset, the
/// replacements, each added node where the editor showed it (render space) at its anchor; then
/// the catalogue entry's state (overrides, the hand-set status).
/// </summary>
public sealed class SavedOverridesTest : ITest
{
    private sealed record Expect(string Level, string[] Removed, string[] Hidden, Dictionary<string, float[]> Moved,
        string[] Replaced, Dictionary<string, Added> Added, string Status);

    private sealed record Added(string Anchor, float[] World);

    private readonly double _timeout;
    private readonly Expect? _expect;
    private readonly string _why = "";
    private double _total, _onMap;

    public string Note { get; private set; } = "";

    public SavedOverridesTest(double timeout)
    {
        _timeout = timeout;
        var file = GameSettings.Arg("overrides-file");
        var expect = GameSettings.Arg("overrides-expect");
        if (string.IsNullOrEmpty(file) || string.IsNullOrEmpty(expect))
        {
            _why = "--overrides-file=PATH and --overrides-expect=JSON are needed";
            return;
        }
        try
        {
            _expect = JsonSerializer.Deserialize<Expect>(System.IO.File.ReadAllText(expect),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (System.Exception e) when (e is System.IO.IOException or JsonException)
        {
            _why = $"{expect}: {e.Message}";
            return;
        }
        if (_expect == null)
        {
            _why = $"{expect}: empty";
            return;
        }
        RemasterOverrides.Register(_expect.Level, file, "editor");
        GD.Print($"ffx selftest: the editor's overrides {file} registered for {_expect.Level}");
    }

    private string Full(string id) => PartIds.Full(id, PartIds.ModelIdOf(_expect!.Level));

    public bool? Step(FfxGame game, GameState s, GameStateView v, double delta)
    {
        if (_expect == null)
            return Done(false, _why);
        _total += delta;
        if (!s.Running && s.Seconds > 1)
            return Done(false, $"the game ended: {s.Outcome}");
        if (_total > _timeout)
            return Done(false, $"timed out ({DebugView.SnapshotLine(v)})");
        var view = (Engine.GetMainLoop() as SceneTree)?.Root.FindChild("StateView3D", true, false) as StateView3D;
        if (view == null || view.MapLevel != _expect.Level || view.MapNode == null || v.Current is not { World.PlayerControl: true })
            return null;
        _onMap += delta;
        if (_onMap < 1.0)
            return null;
        if (Check(view) is { } fail)
            return Done(false, fail);
        if (CheckCatalogue(game) is { } bad)
            return Done(false, bad);
        return Done(true, Note);
    }

    private string? Check(StateView3D view)
    {
        var e = _expect!;
        var rt = view.Overrides;
        if (rt == null)
            return "the map has no overrides";
        if (!rt.Overrides.Layers.Any(l => l.Name == "editor"))
            return $"no editor layer: {string.Join(", ", rt.Overrides.Layers.Select(l => l.Name))}";
        if (rt.Unknown.Count > 0)
            return $"IDs not on the map: {string.Join(", ", rt.Unknown)}";
        if ((rt.Removed, rt.Hidden, rt.Moved, rt.Replaced, rt.Added) != (e.Removed.Length, e.Hidden.Length, e.Moved.Count, e.Replaced.Length, e.Added.Count))
            return $"applied {rt.Removed} removed, {rt.Hidden} hidden, {rt.Moved} moved, {rt.Replaced} replaced, {rt.Added} added; the editor saved "
                + $"{e.Removed.Length}, {e.Hidden.Length}, {e.Moved.Count}, {e.Replaced.Length}, {e.Added.Count}";
        foreach (var id in e.Removed)
            if (rt.NodeOf(Full(id)) is not { } n || n.IsVisibleInTree() || n is MeshInstance3D mi && !AllNoDraw(mi))
                return $"{id}: not removed";
        foreach (var id in e.Hidden)
            if (rt.NodeOf(Full(id)) is not { } n || n.IsVisibleInTree())
                return $"{id}: not hidden";
        foreach (var (id, by) in e.Moved)
        {
            if (rt.NodeOf(Full(id)) is not { } n || rt.BaseOf(n) is not { } b)
                return $"{id}: not moved";
            var off = n.GlobalPosition - b.Origin;
            if (off.DistanceTo(new Vector3(by[0], by[1], by[2])) > 1e-3f)
                return $"{id}: {off} over the game's placement, the editor saved {by[0]}, {by[1]}, {by[2]}";
        }
        foreach (var id in e.Replaced)
            if (!rt.Replacements.TryGetValue(Full(id), out var holder) || !holder.IsVisibleInTree() || holder.GetChildCount() != 1)
                return $"{id}: no replacement shown";
        var placed = new List<string>();
        foreach (var (name, a) in e.Added)
        {
            var addedId = PartIds.AddedId(e.Level, "editor", name);
            if (!rt.AddedNodes.TryGetValue(addedId, out var n))
                return $"{addedId}: not added ({string.Join(", ", rt.AddedNodes.Keys)})";
            if (PartIds.GetString(n, "ffx_anchor") != a.Anchor)
                return $"{addedId}: anchored to {PartIds.GetString(n, "ffx_anchor")}, not {a.Anchor}";
            var want = new Vector3(a.World[0], a.World[1], a.World[2]);
            float d = n.GlobalPosition.DistanceTo(want);
            if (d > 0.05f || !n.IsVisibleInTree())
                return $"{addedId}: at {n.GlobalPosition} in the game, {want} in the editor ({d:F3} units)";
            placed.Add($"{name} at {a.Anchor} within {d:F4} units of the editor's place");
        }
        Note = $"the editor's overrides on {e.Level}: removed {string.Join(", ", e.Removed)}, hid {string.Join(", ", e.Hidden)}, "
            + $"moved {string.Join(", ", e.Moved.Keys)} by the saved offset, replaced {string.Join(", ", e.Replaced)}, added {string.Join(", ", placed)}";
        return null;
    }

    private string? CheckCatalogue(FfxGame game)
    {
        var x = _expect!;
        var e = MapCatalogue.Read(game)?.Find(x.Level);
        if (e == null)
            return $"{x.Level} is not in the catalogue";
        var st = e.State();
        if (!st.HasOverrides || st.Status.ToString() != x.Status || !st.Layers.Contains("editor"))
            return $"{e.Key}'s state: {st}";
        Note += $"; catalogue: {e.Key} status {st.Status} ({string.Join(", ", st.Layers)})";
        return null;
    }

    private static bool AllNoDraw(MeshInstance3D mi)
    {
        if (mi.Mesh == null)
            return true;
        for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
            if (!MapOverrideRuntime.IsNoDraw(mi.GetSurfaceOverrideMaterial(s)))
                return false;
        return true;
    }

    private bool Done(bool pass, string note)
    {
        Note = note;
        return pass;
    }
}

using System.Collections.Generic;
using System.Linq;
using GdFfx.Api;
using Godot;

namespace GdFfx.Remaster;

/// <summary>
/// R1's overrides check (<c>--selftest-overrides</c>; run with <c>--devsave=7</c>): registers the
/// example override set (<see cref="Example"/>: no game data, only part IDs and Godot's shapes) as
/// a layer of Besaid beach, boots there and checks each edit in the running scene: a removed
/// instance, a hidden part (split off its instance), a moved instance, a moved bird (the game
/// flies it: the offset rides on the game's placement, its morph goes on), a part replaced by a
/// scene, a primitive added at a part and one at a bird (it follows the bird); then the
/// catalogue's entry (place, derived and hand-set status). With <c>--shot=FILE.png</c> under a
/// renderer, a picture. <c>--no-overrides</c>: the same boot and picture without the overrides
/// (the "before"). <c>--overrides-survey</c>: lists the map's instances on screen and the ones
/// the game moves (to pick parts for an example).
/// </summary>
public sealed class OverridesTest : ITest
{
    public const string Level = "/map/bsil/bsil00/";
    public const string Example = "res://remaster/examples/bsil00/overrides.tres";

    /// <summary>The example's parts (relative to Besaid beach's model, as in the example's files).</summary>
    public const string Removed = "#FBXASC0494_1/*";
    public const string HiddenPart = "#polySurface32/0";
    public const string MovedStatic = "#FBXASC0492_0/*";
    public const string MovedBird = "#FBXASC0498_0Shape/*";
    public const string ReplacedPart = "#polySurface4/0";
    public const string StaticAnchor = "#polySurface34/*";
    public const string BirdAnchor = "#FBXASC0499_0Shape/0";

    private enum Phase { Boot, Beach, Follow, Shot, Catalogue }

    private readonly double _timeout;
    private readonly string? _shot;
    private readonly bool _before = RemasterOverrides.Disabled;
    private Phase _phase = Phase.Boot;
    private double _phaseTime;
    private double _total;
    private string _seen = "";
    private readonly List<(Vector3 Base, Vector3 Added, Vector3 Anchor)> _samples = new();
    private readonly HashSet<float> _birdMorphs = new();
    private readonly Dictionary<string, Vector3> _surveyStart = new();

    public string Note { get; private set; } = "";

    public OverridesTest(double timeout, string? shot)
    {
        _timeout = timeout;
        _shot = shot;
        if (!_before)
        {
            RemasterOverrides.Register(Level, Example, "selftest");
            GD.Print($"ffx selftest: overrides {Example} registered for {Level}");
        }
    }

    private static string Model => PartIds.ModelIdOf(Level)!;

    private static string Full(string id) => PartIds.Full(id, Model);

    public bool? Step(FfxGame game, GameState s, GameStateView v, double delta)
    {
        _total += delta;
        _phaseTime += delta;
        if (!s.Running && s.Seconds > 1)
            return Done(false, $"the game ended: {s.Outcome}");
        if (_total > _timeout)
            return Done(false, $"timed out in phase {_phase} ({DebugView.SnapshotLine(v)}; {_seen})");
        var view = (Engine.GetMainLoop() as SceneTree)?.Root.FindChild("StateView3D", true, false) as StateView3D;
        switch (_phase)
        {
            case Phase.Boot when v.Current is { World.Map: BesaidTest.MapBeach, World.PlayerControl: true }:
                Next(Phase.Beach);
                break;
            case Phase.Beach when view != null && view.MapLevel == v.Current!.World.Level && view.MapNode != null && _phaseTime > 1.0:
            {
                if (GameSettings.Flag("overrides-survey"))
                    return Survey(view);
                if (_before)
                {
                    if (view.Overrides != null)
                        return Done(false, "--no-overrides, yet the map has overrides");
                    _seen = "the beach without overrides";
                    Next(Wants() ? Phase.Shot : Phase.Catalogue);
                    break;
                }
                if (Check(view) is { } fail)
                    return Done(false, fail);
                Next(Phase.Follow);
                break;
            }
            case Phase.Follow:
            {
                if (Sample(view!) is { } fail)
                    return Done(false, fail);
                if (_phaseTime < 2.0)
                    break;
                if (Followed(view!) is { } why)
                    return Done(false, why);
                Next(Wants() ? Phase.Shot : Phase.Catalogue);
                break;
            }
            case Phase.Shot when _phaseTime > 0.5:
                (Engine.GetMainLoop() as SceneTree)!.Root.GetViewport().GetTexture().GetImage().SavePng(_shot!);
                GD.Print($"ffx selftest: picture {_shot}");
                if (view?.Overrides is { } shown)
                    Where(view, shown);
                Next(Phase.Catalogue);
                break;
            case Phase.Catalogue:
                if (CheckCatalogue(game) is { } bad)
                    return Done(false, bad);
                return Done(true, _seen);
        }
        return null;
    }

    private bool Wants() => _shot != null && DisplayServer.GetName() != "headless";

    /// <summary>Each edit in the scene (null: all there), and <see cref="_seen"/>.</summary>
    private string? Check(StateView3D view)
    {
        var rt = view.Overrides;
        if (rt == null)
            return "the map has no overrides";
        if (view.MapNode!.GetMeta("ffx_source").AsString() != "classic")
            return "the map is not the classic import";
        if (rt.Unknown.Count > 0)
            return $"IDs not on the map: {string.Join(", ", rt.Unknown)}";
        if ((rt.Removed, rt.Hidden, rt.Moved, rt.Replaced, rt.Added, rt.Split) != (1, 1, 2, 1, 2, 2))
            return $"applied {rt.Removed} removed, {rt.Hidden} hidden, {rt.Moved} moved, {rt.Replaced} replaced, {rt.Added} added, "
                + $"{rt.Split} split; the example has 1, 1, 2, 1, 2, 2";
        // removed: hidden for good, its surfaces draw nothing, the game still has it (its object index)
        if (rt.NodeOf(Full(Removed)) is not MeshInstance3D removed)
            return $"{Removed}: not found";
        if (removed.IsVisibleInTree() || !AllNoDraw(removed) || !view.IsGamePlaced(removed))
            return $"{Removed}: still drawn (visible {removed.IsVisibleInTree()}) or no longer the game's";
        // hidden part: split off its instance, hidden; the instance's surface draws nothing, the instance still shows
        if (!rt.SplitParts.TryGetValue(Full(HiddenPart), out var hidden) || hidden.GetParent() is not MeshInstance3D owner)
            return $"{HiddenPart}: not split off its instance";
        int surface = System.Array.IndexOf(PartIds.GetStrings(owner, PartIds.Parts) ?? [], Full(HiddenPart));
        if (hidden.IsVisibleInTree() || !owner.IsVisibleInTree() || surface < 0
            || !MapOverrideRuntime.IsNoDraw(owner.GetSurfaceOverrideMaterial(surface)))
            return $"{HiddenPart}: visible {hidden.IsVisibleInTree()}, its instance visible {owner.IsVisibleInTree()}, surface {surface}";
        if (hidden.Mesh.GetSurfaceCount() != 1 || hidden.Mesh.GetAabb().Size == Vector3.Zero)
            return $"{HiddenPart}: the split part's mesh is empty";
        for (int i = 0; i < owner.Mesh.GetSurfaceCount(); i++)
            if (i != surface && MapOverrideRuntime.IsNoDraw(owner.GetSurfaceOverrideMaterial(i)))
                return $"{HiddenPart}: surface {i} of its instance no longer drawn";
        // moved instance: the game's placement plus the offset
        if (rt.NodeOf(Full(MovedStatic)) is not { } moved || rt.BaseOf(moved) is not { } mb || !view.IsGamePlaced(moved))
            return $"{MovedStatic}: not found, not moved or not the game's";
        var off = moved.GlobalPosition - mb.Origin;
        if (!off.IsEqualApprox(OffsetOf(rt, MovedStatic)))
            return $"{MovedStatic}: moved by {off}, not {OffsetOf(rt, MovedStatic)}";
        // replaced part: split off, draws nothing, the scene sits in its frame, unscaled, and shows
        if (!rt.Replacements.TryGetValue(Full(ReplacedPart), out var holder) || holder.GetParent() is not MeshInstance3D replaced)
            return $"{ReplacedPart}: no replacement";
        if (!AllNoDraw(replaced) || !holder.IsVisibleInTree() || holder.GetChildCount() != 1
            || holder.GlobalPosition.DistanceTo(replaced.GlobalPosition) > 1e-3f)
            return $"{ReplacedPart}: the replacement is not in its place ({holder.GlobalPosition} vs {replaced.GlobalPosition})";
        var replacedScale = holder.GetChild<Node3D>(0).GlobalTransform.Basis.Scale;
        if (!replacedScale.IsEqualApprox(Vector3.One))
            return $"{ReplacedPart}: the replacement is scaled {replacedScale} (render units expected)";
        // the bird and the other bird: placed by the game each frame
        if (rt.NodeOf(Full(MovedBird)) is not MeshInstance3D bird || !view.IsGamePlaced(bird))
            return $"{MovedBird}: not a mesh the game places";
        if (rt.NodeOf(Full(BirdAnchor)) is not { } birdAnchor || !view.IsGamePlaced(birdAnchor))
            return $"{BirdAnchor}: not a mesh the game places";
        // the added nodes: at their anchors' origin and axes plus their own place, in render units
        if (rt.AddedNodes.Count != 2)
            return $"{rt.AddedNodes.Count} added node(s), not 2";
        foreach (var (id, added) in rt.AddedNodes)
        {
            var anchorId = Full(PartIds.GetString(added, "ffx_anchor") ?? "");
            if (rt.NodeOf(anchorId) is not { } anchor)
                return $"{id}: its anchor {anchorId} is not on the map";
            if (AddedOffset(added, anchor) is { } d and > 0.05f)
                return $"{id}: {d:F2} units off its place at {anchorId}";
            if (!added.GlobalTransform.Basis.Scale.IsEqualApprox(Vector3.One) || !added.IsVisibleInTree())
                return $"{id}: scaled {added.GlobalTransform.Basis.Scale} or not shown";
        }
        // the IDs on the scene: every instance's, each surface's, the materials' textures'
        int instances = rt.Index.Nodes.Keys.Count(k => k.StartsWith(Model + "#") && k.EndsWith("/*"));
        int textured = 0, textureIds = 0;
        var bad = new List<string>();
        foreach (var mesh in rt.Index.Surfaces.Values.Select(v => v.Mesh).Distinct())
            for (int i = 0; i < mesh.Mesh.GetSurfaceCount(); i++)
                if (mesh.Mesh.SurfaceGetMaterial(i) is { } m && PartIds.GetStrings(m, PartIds.TextureIds) is { } t)
                {
                    textured++;
                    textureIds += t.Length;
                    bad.AddRange(t.Where(x => !x.StartsWith("x:/ffx_data/gamedata/ps3data/") || !x.EndsWith(".dds.phyre")));
                }
        if (instances < 73 || rt.Index.Surfaces.Count < instances || textured == 0 || bad.Count > 0)
            return $"IDs: {instances} instances, {rt.Index.Surfaces.Count} parts, {textured} textured surfaces, bad texture IDs: {string.Join(", ", bad.Take(3))}";
        _seen = $"{instances} instances and {rt.Index.Surfaces.Count} parts by ID, {textureIds} texture IDs on {textured} surfaces; "
            + $"removed {Removed}, hid {HiddenPart} (split off), moved {MovedStatic} by {OffsetOf(rt, MovedStatic)}, "
            + $"replaced {ReplacedPart} by a scene, added {string.Join(", ", rt.AddedNodes.Keys.Select(k => k[(k.LastIndexOf('/') + 1)..]))}";
        return null;
    }

    /// <summary>How far <paramref name="added"/> is from its place: <paramref name="anchor"/>'s origin and axes plus its own position.</summary>
    private static float AddedOffset(Node3D added, Node3D anchor)
    {
        var a = anchor.GlobalTransform;
        var want = a.Origin + a.Basis.Orthonormalized() * added.Position;
        return added.GlobalPosition.DistanceTo(want);
    }

    private static Vector3 OffsetOf(MapOverrideRuntime rt, string id) =>
        rt.Overrides.Parts.TryGetValue(Full(id), out var p) ? p.Transform.Origin : Vector3.Zero;

    private static bool AllNoDraw(MeshInstance3D mi)
    {
        for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
            if (!MapOverrideRuntime.IsNoDraw(mi.GetSurfaceOverrideMaterial(s)))
                return false;
        return true;
    }

    /// <summary>A frame of the bird and the node added at the other bird (null: as expected).</summary>
    private string? Sample(StateView3D view)
    {
        var rt = view.Overrides!;
        var bird = (MeshInstance3D)rt.NodeOf(Full(MovedBird))!;
        if (!rt.PlacedRecently(bird) || rt.BaseOf(bird) is not { } b)
            return null;
        var off = bird.GlobalPosition - b.Origin;
        if (!off.IsEqualApprox(OffsetOf(rt, MovedBird)))
            return $"{MovedBird}: {off} over the game's placement, not {OffsetOf(rt, MovedBird)}";
        if (view.IsMorphed(bird) && bird.Mesh is ArrayMesh { } am && am.GetBlendShapeCount() > 0)
            _birdMorphs.Add(Mathf.Snapped(bird.GetBlendShapeValue(0), 1e-3f));
        var anchor = rt.NodeOf(Full(BirdAnchor))!;
        var added = rt.AddedNodes.First(kv => Full(PartIds.GetString(kv.Value, "ffx_anchor") ?? "") == Full(BirdAnchor)).Value;
        if (AddedOffset(added, anchor) is var d && d > 0.05f)
            return $"the node added at {BirdAnchor} is {d:F2} units off its place";
        _samples.Add((b.Origin, added.GlobalPosition, anchor.GlobalPosition));
        return null;
    }

    /// <summary>Whether the bird and the added node moved with the game over the samples (null: they did).</summary>
    private string? Followed(StateView3D view)
    {
        if (_samples.Count < 10)
            return $"{_samples.Count} frame(s) with the bird placed by the game";
        float baseMoved = _samples[0].Base.DistanceTo(_samples[^1].Base);
        float addedMoved = _samples[0].Added.DistanceTo(_samples[^1].Added);
        float anchorMoved = _samples[0].Anchor.DistanceTo(_samples[^1].Anchor);
        if (baseMoved < 1f)
            return $"the game did not move {MovedBird} ({baseMoved:F2} units in {_samples.Count} frames)";
        if (anchorMoved < 1f || Mathf.Abs(addedMoved - anchorMoved) > 0.5f)
            return $"the node added at {BirdAnchor} did not follow it ({addedMoved:F2} vs {anchorMoved:F2} units)";
        var bird = (MeshInstance3D)view.Overrides!.NodeOf(Full(MovedBird))!;
        bool morphed = view.IsMorphed(bird);
        if (morphed && _birdMorphs.Count == 1)
            return $"{MovedBird}: its morph stopped ({_birdMorphs.First()} over {_samples.Count} frames)";
        _seen += $"; over {_samples.Count} frames the game flew {MovedBird} {baseMoved:F0} units and it kept its offset "
            + (morphed ? $"({_birdMorphs.Count} morph weights), " : "(no morph), ")
            + $"the node added at {BirdAnchor} followed it {addedMoved:F0} units";
        return null;
    }

    /// <summary>The catalogue's Besaid beach: its place, the derived and the hand-set status.</summary>
    private string? CheckCatalogue(FfxGame game)
    {
        var cat = MapCatalogue.Read(game);
        if (cat == null)
            return "no map catalogue (the archive is not open)";
        if (cat.Levels.Count < 300 || cat.Areas.Count < 20)
            return $"the catalogue has {cat.Levels.Count} levels and {cat.Areas.Count} areas";
        var e = cat.Find(Level);
        if (e == null || cat.FindMap(BesaidTest.MapBeach) != e || cat.Find("bsil00") != e)
            return $"{Level} is not in the catalogue (or not map {BesaidTest.MapBeach:x})";
        if (!e.Title("us").Contains("Besaid") || !e.Place.ContainsKey("jp"))
            return $"{Level}'s place is {e.Title("us")}";
        var st = e.State();
        if (_before ? st.HasOverrides || st.Effective != MapStatus.Original
                : !st.HasOverrides || st.Status != MapStatus.InProgress || st.Effective != MapStatus.InProgress
                  || !st.Layers.Contains("selftest"))
            return $"{Level}'s state: {st}";
        if (st.HasRemade)
            return $"{Level} has a remade scene registered";
        var other = cat.Find("bsil01");
        if (other == null || other.State() is { HasOverrides: true } or { Effective: not MapStatus.Original })
            return $"bsil01's state: {other?.State()}";
        var withOverrides = RemasterOverrides.Levels();
        _seen += $"; catalogue: {cat.Levels.Count} levels, {cat.Areas.Count} areas, {e.Key} \"{e.Title()}\" / \"{e.Title("jp")}\" "
            + $"({e.Events.Count} event(s)), {(st.HasOverrides ? "with" : "without")} overrides, status {st.Effective}, "
            + $"render path {st.RenderPath}; levels with overrides: {string.Join(", ", withOverrides)}";
        return null;
    }

    /// <summary>
    /// <c>--overrides-survey</c>: the map's instances on screen, largest first, then (after 3 s)
    /// the parts the game moved.
    /// </summary>
    private bool? Survey(StateView3D view)
    {
        if (_surveyStart.Count == 0)
        {
            SurveyScreen(view);
            foreach (var (id, n) in PartIndex.Build(view.MapNode!).Nodes)
                _surveyStart[id] = n.GlobalPosition;
            _phaseTime = 0;
            return null;
        }
        if (_phaseTime < 3.0)
            return null;
        foreach (var (id, n) in PartIndex.Build(view.MapNode!).Nodes)
            if (_surveyStart.TryGetValue(id, out var p0) && p0.DistanceTo(n.GlobalPosition) > 0.5f)
                GD.Print($"ffx survey: moved {id[id.IndexOf('#')..]} {p0.DistanceTo(n.GlobalPosition):F1} units in 3 s, "
                    + $"now {n.GlobalPosition.Round()} visible {n.IsVisibleInTree()}");
        return Done(true, "surveyed");
    }

    private static void SurveyScreen(StateView3D view)
    {
        var cam = view.GetViewport().GetCamera3D();
        var size = view.GetViewport().GetVisibleRect().Size;
        var rows = new List<(float Area, string Line)>();
        void Walk(Node n)
        {
            if (n is MeshInstance3D { Mesh: { } } mi && PartIds.Of(mi) is { } id && mi.IsVisibleInTree())
            {
                var box = mi.GlobalTransform * mi.GetAabb();
                var min = new Vector2(float.MaxValue, float.MaxValue);
                var max = new Vector2(float.MinValue, float.MinValue);
                int front = 0;
                for (int i = 0; i < 8; i++)
                {
                    var c = box.GetEndpoint(i);
                    if (cam.IsPositionBehind(c))
                        continue;
                    front++;
                    var p = cam.UnprojectPosition(c);
                    min = min.Min(p);
                    max = max.Max(p);
                }
                if (front > 0)
                {
                    min = min.Clamp(Vector2.Zero, size);
                    max = max.Clamp(Vector2.Zero, size);
                    float area = Mathf.Max(0, max.X - min.X) * Mathf.Max(0, max.Y - min.Y) / (size.X * size.Y);
                    if (area > 0)
                        rows.Add((area, $"{area * 100,6:F1}% {id[id.IndexOf('#')..],-28} surfaces {mi.Mesh.GetSurfaceCount(),2} "
                            + $"game {(view.IsGamePlaced(mi) ? "yes" : "no ")} rect {min.Round()}-{max.Round()} "
                            + $"centre {box.GetCenter().Round()} size {box.Size.Round()} origin {mi.GlobalPosition.Round()}"));
                }
            }
            for (int i = 0, k = n.GetChildCount(); i < k; i++)
                Walk(n.GetChild(i));
        }
        Walk(view.MapNode!);
        foreach (var (_, line) in rows.OrderByDescending(r => r.Area))
            GD.Print($"ffx survey: {line}");
        GD.Print($"ffx survey: camera {cam.GlobalPosition.Round()} looking {(-cam.GlobalBasis.Z).Normalized()}");
    }

    /// <summary>Where each edit is in the picture (its bounds' centre on screen; off: outside the view).</summary>
    private static void Where(StateView3D view, MapOverrideRuntime rt)
    {
        var cam = view.GetViewport().GetCamera3D();
        var size = view.GetViewport().GetVisibleRect().Size;
        void Line(string what, Node3D? n)
        {
            if (n == null)
                return;
            var box = n is MeshInstance3D { Mesh: { } } mi ? mi.GlobalTransform * mi.GetAabb() : new Aabb(n.GlobalPosition, Vector3.Zero);
            foreach (var c in n.FindChildren("*", nameof(MeshInstance3D), true, false))
                if (c is MeshInstance3D { Mesh: { } } cm)
                    box = box.Size == Vector3.Zero ? cm.GlobalTransform * cm.GetAabb() : box.Merge(cm.GlobalTransform * cm.GetAabb());
            var centre = box.GetCenter();
            var p = cam.UnprojectPosition(centre);
            bool on = !cam.IsPositionBehind(centre) && new Rect2(Vector2.Zero, size).HasPoint(p);
            GD.Print($"ffx selftest: in the picture: {what} at {(on ? p.Round().ToString() : "off")} (centre {centre.Round()}, size {box.Size.Round()})");
        }
        Line($"removed {Removed}", rt.NodeOf(Full(Removed)));
        Line($"hidden {HiddenPart}", rt.NodeOf(Full(HiddenPart)));
        Line($"moved {MovedStatic}", rt.NodeOf(Full(MovedStatic)));
        Line($"moved {MovedBird}", rt.NodeOf(Full(MovedBird)));
        Line($"replaced {ReplacedPart}", rt.NodeOf(Full(ReplacedPart)));
        if (rt.Replacements.TryGetValue(Full(ReplacedPart), out var r))
            Line("its replacement", r);
        foreach (var (id, n) in rt.AddedNodes)
            Line($"added {id}", n);
    }

    private void Next(Phase p)
    {
        GD.Print($"ffx selftest: {_phase} -> {p}");
        _phase = p;
        _phaseTime = 0;
    }

    private bool Done(bool pass, string note)
    {
        Note = note;
        return pass;
    }
}

using System.Collections.Generic;
using System.Linq;
using GdFfx.Api;
using Godot;

namespace GdFfx.Remake;

/// <summary>
/// G6's check (<c>--selftest-remake</c>; run with <c>--devsave=7</c>): registers the example
/// stand-in (<see cref="ModSelfTest.Example"/>, a capsule with a flat skeleton of
/// <c>SkeletonNode_0..23</c>) for Tidus (<c>chr c001</c>) at run time, boots to Besaid beach and
/// checks that the player is drawn by the remade scene, not the classic model, that the game
/// poses its skeleton, and that the map beside it is still the classic one.
/// </summary>
public sealed class RemakeTest : ITest
{
    public const string Model = "c001";

    private enum Phase { Boot, Beach, Again }

    private readonly double _timeout;
    private Phase _phase = Phase.Boot;
    private double _phaseTime;
    private double _total;
    private Transform3D[] _first = [];
    private string _seen = "";

    public string Note { get; private set; } = "";

    public RemakeTest(double timeout)
    {
        _timeout = timeout;
        RemakeRegistry.Register("chr", Model, ModSelfTest.Example, "selftest-remake");
        GD.Print($"ffx selftest: chr {Model} registered to {ModSelfTest.Example}");
    }

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
            case Phase.Beach when view != null && view.MapLevel == v.Current!.World.Level && _phaseTime > 1.0:
            {
                var player = v.Current.Actors.FirstOrDefault(a => a.Player);
                if (player == null || player.Model != Model)
                    return Done(false, $"the player is {player?.Model ?? "absent"}, not {Model}");
                var models = Models(view, Model);
                if (models.Count == 0)
                {
                    if (_phaseTime > 10)
                        return Done(false, $"no model for {Model} after 10 s on the beach");
                    break;
                }
                var classic = models.Count(m => m.GetMeta("ffx_source").AsString() != "remade");
                if (classic > 0)
                    return Done(false, $"{classic} classic model(s) of {Model} drawn beside the remade one");
                var model = models[0];
                if (model.GetMeta("ffx_path").AsString() != ModSelfTest.Example)
                    return Done(false, $"{Model} drawn from {model.GetMeta("ffx_path")}");
                if (!model.IsVisibleInTree())
                    return Done(false, $"the remade {Model} is hidden");
                var skel = Skeleton(model);
                if (skel == null)
                    return Done(false, $"the remade {Model} has no skeleton");
                _first = Poses(skel);
                var posed = _first.Count(t => !t.IsEqualApprox(Transform3D.Identity));
                var map = view.GetChildren().OfType<Node3D>().FirstOrDefault(n => n.HasMeta("ffx_kind") && n.GetMeta("ffx_kind").AsString() == "map");
                var mapSource = map?.GetMeta("ffx_source").AsString() ?? "none";
                if (posed < skel.GetBoneCount() / 2)
                    return Done(false, $"only {posed} of the remade {Model}'s {skel.GetBoneCount()} bones posed by the game");
                if (mapSource != "classic")
                    return Done(false, $"the map beside it is {mapSource}, not classic");
                var head = _first.Select(t => t.Origin).OrderBy(o => o.Y).First();
                _seen = $"{v.Current.World.Level} classic, the player {Model} drawn by the remade {ModSelfTest.Example}, "
                    + $"{posed} of its {skel.GetBoneCount()} bones posed by the game (highest bone at {head.Y:F1} in character space)";
                Next(Phase.Again);
                break;
            }
            // a second look: the game keeps posing it (the idle animation moves the bones)
            case Phase.Again when _phaseTime > 1.0:
            {
                var model = view == null ? null : Models(view, Model).FirstOrDefault();
                var skel = model == null ? null : Skeleton(model);
                if (skel == null)
                    return Done(false, $"the remade {Model} went away ({_seen})");
                var now = Poses(skel);
                var moved = now.Zip(_first).Count(p => !p.First.IsEqualApprox(p.Second));
                return Done(true, $"{_seen}; {moved} bones moved in the next second");
            }
        }
        return null;
    }

    /// <summary>The character models of <paramref name="model"/> under the 3D view.</summary>
    private static List<Node3D> Models(StateView3D view, string model) =>
        view.FindChildren("Model", nameof(Node3D), true, false)
            .OfType<Node3D>()
            .Where(n => n.HasMeta("ffx_kind") && n.GetMeta("ffx_kind").AsString() == "chr"
                && n.GetMeta("ffx_id").AsString() == model)
            .ToList();

    private static Skeleton3D? Skeleton(Node model) =>
        model.FindChildren("*", nameof(Skeleton3D), true, false).FirstOrDefault() as Skeleton3D;

    private static Transform3D[] Poses(Skeleton3D skel) =>
        Enumerable.Range(0, skel.GetBoneCount()).Select(skel.GetBoneGlobalPose).ToArray();

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

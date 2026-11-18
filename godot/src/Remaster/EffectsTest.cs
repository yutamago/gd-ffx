using System.Collections.Generic;
using System.Linq;
using GdFfx.Remake;
using Godot;

namespace GdFfx.Remaster;

/// <summary>
/// R7's effects check (<c>--selftest-effects --devsave=7</c>; docs/remaster-editor.md, "Checks"):
/// the original example (<c>res://remake/examples/save_sphere_sparkle.tscn</c>, or the editor's
/// scene with <c>--effect-scene=FILE</c>) registered for Besaid beach's save sphere glow
/// (<c>bsil00-fp15</c>, or <c>--effect-id=</c>); on the beach: every classic effect has its id
/// (the beach's field programs), the remake plays where the classic effect is (within 12 units
/// of its draws' centre) while it plays, its classic draws are left out and the others still
/// drawn (the fallback); F8 (<see cref="Replacements"/>) brings the classic effect back and the
/// remake stops, F8 again remakes it; the effect's recording for the editor is written
/// (<c>--remaster-effects=DIR</c>) and reads back. With <c>--shot=FILE.png</c> under a renderer:
/// <c>FILE.png</c> (the remake) and <c>FILE-classic.png</c>, the camera at the save sphere.
/// </summary>
public sealed class EffectsTest(double timeout, string? shot) : ITest
{
    public const string DefaultId = "bsil00-fp15";
    public const string Example = "res://remake/examples/save_sphere_sparkle.tscn";

    private enum Phase { Setup, Boot, Ids, Remade, ShotRemade, Off, ShotClassic, On, Record, Done }

    private Phase _phase = Phase.Setup;
    private double _phaseTime, _total;
    private bool _acted;
    private readonly List<string> _seen = new();
    private readonly string _id = GameSettings.Arg("effect-id") is { Length: > 0 } i ? i : DefaultId;
    private readonly string _scene = GameSettings.Arg("effect-scene") is { Length: > 0 } f ? f : Example;
    private int _remadeFrames, _skippedMax, _otherDrawn;
    private float _worst;

    public string Note { get; private set; } = "";

    private static StateView3D? View => (Engine.GetMainLoop() as SceneTree)?.Root.FindChild("StateView3D", true, false) as StateView3D;

    public bool? Step(FfxGame game, GameState s, GameStateView v, double delta)
    {
        _total += delta;
        _phaseTime += delta;
        if (!s.Running && s.Seconds > 1)
            return Done(false, $"the game ended: {s.Outcome}");
        if (_total > timeout)
            return Done(false, $"timed out in phase {_phase} ({DebugView.SnapshotLine(v)}; {string.Join("; ", _seen)})");
        var view = View;
        var remakes = view?.GetNodeOrNull<EffectRemakeView>("EffectRemakeView");
        var draws = view?.GetNodeOrNull<EffectDrawView>("EffectDrawView");
        switch (_phase)
        {
            case Phase.Setup:
                if (!ResourceLoader.Exists(_scene) && !FileAccess.FileExists(_scene))
                    return Done(false, $"{_scene} not found");
                RemakeRegistry.Register("effect", _id, _scene, "selftest-effects");
                _seen.Add($"{_id} registered: {_scene}");
                Next(Phase.Boot);
                break;
            case Phase.Boot when v.Current is { World.Map: BesaidTest.MapBeach, World.PlayerControl: true }
                && view?.MapNode != null && remakes != null && draws != null && _phaseTime > 1.0:
                Next(Phase.Ids);
                break;
            case Phase.Ids when _phaseTime > 0.5:
            {
                var ids = remakes!.Seen.Keys.OrderBy(x => x).ToList();
                int owned = 0, total = 0;
                for (int i = 0; i < remakes.Ids.Length; i++)
                    if (remakes.Ids[i] != null)
                        owned++;
                total = draws!.Shown.Draws;
                if (!ids.Contains(_id))
                    return Done(false, $"{_id} not among the effects drawn ({string.Join(", ", ids)})");
                if (ids.Any(x => !x.StartsWith("bsil00-fp")))
                    return Done(false, $"an effect id not of the beach's field programs: {string.Join(", ", ids)}");
                _seen.Add($"{ids.Count} effect ids on the beach ({string.Join(", ", ids)}); {owned} of {total} draws owned");
                if (owned < total)
                    return Done(false, $"{total - owned} draw(s) without an owner");
                Next(Phase.Remade);
                break;
            }
            case Phase.Remade:
            {
                var node = remakes!.Remake(_id);
                if (node == null)
                {
                    if (_phaseTime > 3)
                        return Done(false, $"no remake playing for {_id} (started: {string.Join(", ", remakes.Started.Select(kv => $"{kv.Key} x{kv.Value}"))})");
                    break;
                }
                if (!remakes.Seen.TryGetValue(_id, out var place))
                    break;
                // the classic draws' centre of this frame, against the remake's place
                var centre = Centre(remakes, draws!, _id);
                if (centre is { } c)
                    _worst = Mathf.Max(_worst, node.GlobalPosition.DistanceTo(c));
                _skippedMax = System.Math.Max(_skippedMax, draws!.RemadeSkipped);
                _otherDrawn = System.Math.Max(_otherDrawn, draws.Parts);
                _remadeFrames++;
                if (_phaseTime < 2.0)
                    break;
                if (!remakes.Hidden.Contains(_id) || _skippedMax == 0)
                    return Done(false, $"the classic draws of {_id} are still drawn (skipped {_skippedMax})");
                if (_otherDrawn == 0)
                    return Done(false, "no other classic effect drawn (the fallback)");
                if (_worst > 12f)
                    return Done(false, $"the remake stood {_worst:F1} units from the classic effect");
                var particles = node.FindChildren("*", "GPUParticles3D", true, false).Count;
                _seen.Add($"remade: {node.Name} at {place} ({_worst:F2} units from the classic draws' centre at most, {_remadeFrames} frames), "
                    + $"{particles} particle system(s), {_skippedMax} classic draw(s) left out, other effects drawn ({_otherDrawn} part(s))");
                Next(Wants() ? Phase.ShotRemade : Phase.Off);
                break;
            }
            case Phase.ShotRemade when !_acted:
                _acted = true;
                Aim(view!, remakes!);
                break;
            case Phase.ShotRemade when _phaseTime > 1.0:
                Shot(shot!);
                Next(Phase.Off);
                break;
            case Phase.Off when !_acted:
                _acted = true;
                Replacements.Toggle();
                break;
            case Phase.Off when _phaseTime > 1.0:
                if (Replacements.On)
                    return Done(false, "F8 did not turn the replacements off");
                if (remakes!.Remake(_id) != null || remakes.Hidden.Contains(_id))
                    return Done(false, "the remake still plays with the replacements off");
                if (draws!.RemadeSkipped != 0)
                    return Done(false, $"{draws.RemadeSkipped} classic draw(s) still left out");
                _seen.Add("F8: classic (the remake stopped, its classic draws back)");
                Next(Wants() ? Phase.ShotClassic : Phase.On);
                break;
            case Phase.ShotClassic when _phaseTime > 1.0:
                Shot(shot![..^4] + "-classic.png");
                StateView3D.LookAtPoint = null;
                Next(Phase.On);
                break;
            case Phase.On when !_acted:
                _acted = true;
                Replacements.Toggle();
                break;
            case Phase.On when _phaseTime > 1.0:
                if (remakes!.Remake(_id) == null || draws!.RemadeSkipped == 0)
                    return Done(false, "F8 again did not bring the remake back");
                _seen.Add("F8 again: remade");
                Next(Phase.Record);
                break;
            case Phase.Record when _phaseTime > 0.5:
            {
                if (!EffectRecorder.Writes)
                    return Done(false, "no --remaster-effects=DIR: the recording is not checked");
                EffectRecorder.FlushAll();
                var rec = EffectRecorder.Read(EffectRecorder.PathOf(_id));
                if (rec == null || rec.Frames.Count == 0)
                    return Done(false, $"no recording of {_id} in {EffectRecorder.Dir}");
                int maxDraws = rec.Frames.Max(f => f.Count);
                var index = EffectRecorder.ReadIndex();
                if (!index.ContainsKey(_id))
                    return Done(false, $"{_id} not in the index");
                var tex = rec.Frames.SelectMany(f => f).Select(d => d.Texture).Where(t => t.Length > 0).Distinct().Count();
                _seen.Add($"recorded: {rec.Frames.Count} frames, up to {maxDraws} draws, {tex} textures, place {rec.Place}; the index lists {index.Count} effect(s)");
                Next(Phase.Done);
                break;
            }
            case Phase.Done:
                StateView3D.LookAtPoint = null;
                return Done(true, string.Join("; ", _seen));
        }
        return null;
    }

    /// <summary>The centre of the bounds of <paramref name="id"/>'s classic draws in the last frame.</summary>
    private static Vector3? Centre(EffectRemakeView remakes, EffectDrawView draws, string id) =>
        remakes.Bounds.TryGetValue(id, out var b) && b.Min.X <= b.Max.X ? (b.Min + b.Max) * 0.5f : null;

    /// <summary>The camera at the effect (for the pictures).</summary>
    private void Aim(StateView3D view, EffectRemakeView remakes)
    {
        if (remakes.Seen.TryGetValue(_id, out var at))
            StateView3D.LookAtPoint = at;
    }

    private bool Wants() => shot != null && DisplayServer.GetName() != "headless";

    private static void Shot(string file)
    {
        (Engine.GetMainLoop() as SceneTree)!.Root.GetViewport().GetTexture().GetImage().SavePng(file);
        GD.Print($"ffx selftest: picture {file}");
    }

    private void Next(Phase p)
    {
        GD.Print($"ffx selftest: {_phase} -> {p} ({_total:0.0} s)");
        _phase = p;
        _phaseTime = 0;
        _acted = false;
    }

    private bool Done(bool pass, string note)
    {
        Note = note;
        return pass;
    }
}

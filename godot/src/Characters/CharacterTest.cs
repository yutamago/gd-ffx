using System;
using System.Collections.Generic;
using System.Linq;
using GdFfx.Api;
using GdFfx.Remake;
using Godot;

namespace GdFfx.Characters;

/// <summary>
/// R6's game check (<c>--selftest-character</c>; with <c>--devsave=7</c>): the original stand-in
/// humanoid (<see cref="StandInHumanoid"/>, a skeleton of its own) as the remake of Tidus
/// (<c>c001</c>), retargeted to the game's skeleton and posed by the game while he walks on Besaid
/// beach. Once the beach is shown it builds the rig against the classic import (the automatic
/// mapping, alignment and rest offsets, the hood following the game's cloth, the ponytail on
/// Godot's spring bones, the mouth on the game's jaw), saves the scene and rig (to
/// <c>--character-dir=DIR</c>, else <c>user://r6-check</c>), registers it and has the view take
/// it. Or, with <c>--character-scene=FILE</c>, it takes a scene the editor saved (its rig as it is).
/// Then, walking:
/// <list type="bullet">
/// <item>with the default positions (the hips the game's, the others the remade bones'): the
/// angle between each mapped bone's direction to the bone it aims at and the game's, read back
/// from the skeleton, below <see cref="AngleTolerance"/>;</item>
/// <item>with every mapped bone on the game's joint: the distance of each mapped joint from the
/// game's (character units), below <see cref="PositionTolerance"/>;</item>
/// <item>the hood turned by the game's cloth and moving, the ponytail moved by Godot's spring bones
/// (read after Godot's modifiers ran), the face table driven each frame (the blend shape set to
/// the row's output; a jaw opened by hand opens the mouth);</item>
/// <item>with <c>--battle=...</c> (the deck battle) no walk: the directions, and every object the game
/// binds to the character (its weapons) following the remade one (the root on the socket);</item>
/// <item>the material presets loaded; with <c>--shot=FILE.png</c> under a renderer pictures:
/// FILE.png (the classic map), FILE-remastered.png, FILE-closeup.png (the camera at the remade
/// character).</item>
/// </list>
/// </summary>
public sealed class CharacterTest : ITest
{
    public const string Model = "c001";
    public const float AngleTolerance = 3f;
    public const float PositionTolerance = 0.05f;

    private enum Phase { Boot, Build, Attach, WalkRotation, WalkGame, Battle, ShotClassic, ShotRemastered, ShotClose, Done }

    /// <summary>In a battle (<c>--battle=...</c>): no walk; the weapon in the remade hand instead.</summary>
    private readonly bool _inBattle = GameSettings.Arg("battle") is { Length: > 0 };
    private int _attachedAt, _weaponFrames;
    private float _weaponGap, _weaponGapMax;
    private string _weapon = "";
    private double _battleSeen;
    private readonly List<string> _weapons = new();

    private readonly double _timeout;
    private readonly string? _shot;
    private readonly string? _scene = GameSettings.Arg("character-scene");
    private readonly string _dir = GameSettings.Arg("character-dir") ?? "user://r6-check";
    private Phase _phase = Phase.Boot;
    private double _phaseTime, _total;
    private readonly List<string> _seen = new();

    public string Note { get; private set; } = "";

    // measures
    private float _maxAngle, _sumAngle, _maxPos, _sumPos;
    private int _angleFrames, _posFrames, _angleBones, _posBones;
    private int _clothFrames, _faceFrames, _faceMismatch;
    private float _hoodMoved, _tailMoved, _jawMin = float.MaxValue, _jawMax = float.MinValue, _outMin = float.MaxValue, _outMax = float.MinValue;
    private Quaternion? _hoodLast;
    private float _hoodStep, _hoodTurn, _hoodTurnSum, _tailRest, _tailStep;
    private Transform3D? _tailLast;
    private readonly List<float> _tailAngles = new();
    private Vector3? _hipsFirst;
    private float _hipsMoved;
    private Skeleton3D? _springSkel;
    private SpringBoneSimulator3D? _springs;
    private int _springFrames;
    private string _syntheticFace = "";
    private string _worstBone = "";

    public CharacterTest(double timeout, string? shot)
    {
        _timeout = timeout;
        _shot = shot;
    }

    public bool? Step(FfxGame game, GameState s, GameStateView v, double delta)
    {
        _total += delta;
        _phaseTime += delta;
        if (!s.Running && s.Seconds > 1)
            return Done(false, $"the game ended: {s.Outcome}");
        if (_total > _timeout)
        {
            Keys([]);
            return Done(false, $"timed out in phase {_phase} ({DebugView.SnapshotLine(v)}; {string.Join("; ", _seen)})");
        }
        var host = GameHost.Instance;
        var view = (Engine.GetMainLoop() as SceneTree)?.Root.FindChild("StateView3D", true, false) as StateView3D;
        switch (_phase)
        {
            case Phase.Boot when !_inBattle && v.Current is { World.Map: BesaidTest.MapBeach, World.PlayerControl: true } && view != null && view.MapLevel == v.Current.World.Level:
                Next(Phase.Build);
                break;
            case Phase.Boot when _inBattle && v.Current is { World.Battle: true } && view != null && view.MapLevel == v.Current.World.Level
                && v.Current.Actors.Any(a => a.Model == Model && GameStateView.Drawn(a)) && _phaseTime > 0:
                if (_battleSeen == 0)
                    _battleSeen = _total;
                if (_total - _battleSeen > 3)
                    Next(Phase.Build);
                break;
            case Phase.Build:
            {
                if (host?.Classic is not { Open: true } lib)
                    return Done(false, "no classic content (the archive did not open)");
                if (_scene is { Length: > 0 })
                {
                    RemakeRegistry.Register("chr", Model, _scene, "selftest-character");
                    _seen.Add($"the editor's scene {_scene}");
                }
                else
                {
                    if (lib.Path("chr", Model) is not { } path)
                        break; // importing
                    if (Build(game, path) is { } fail)
                        return Done(false, fail);
                }
                int dropped = view!.Reload(Model);
                GD.Print($"ffx selftest: {Model} registered to its remake, {dropped} classic model(s) dropped");
                Next(Phase.Attach);
                break;
            }
            case Phase.Attach:
            {
                var list = view!.Remade(Model);
                if (list.Count == 0)
                {
                    if (_phaseTime > 10)
                        return Done(false, $"the remade {Model} was not shown after 10 s");
                    break;
                }
                var (drive, g, has, model, _) = list[0];
                var rig = drive.Rig;
                if (drive.Applied < 3)
                    break;
                var hood = rig.Cloth.FirstOrDefault(c => c.Name.StartsWith("Hood", StringComparison.Ordinal));
                var tail = rig.Cloth.FirstOrDefault(c => c.Name.StartsWith("Tail", StringComparison.Ordinal));
                foreach (var line in _seen)
                    GD.Print($"ffx selftest: {line}");
                if (hood is not { Mode: ClothMode.Game })
                    return Done(false, $"the hood is not on the game's cloth ({hood?.Mode.ToString() ?? "no chain"})");
                if (tail is not { Mode: ClothMode.Godot } || drive.Springs == null)
                    return Done(false, $"the ponytail is not on Godot's spring bones ({tail?.Mode.ToString() ?? "no chain"})");
                _seen.Add($"{rig.Bones.Count(b => b.Element >= 0)} bones mapped, the hood on the game's cloth segment (model {hood.Model}, piece {hood.Piece}), "
                    + $"the ponytail on Godot's spring bones, {rig.Face.Count} face row(s), {rig.Attach.Count} attachment(s)");
                // the editor's scene: the row it mapped by hand, as it saved it
                if (GameSettings.Arg("character-expect") is { Length: > 0 } expectFile)
                {
                    var expect = Json.ParseString(Godot.FileAccess.GetFileAsString(expectFile)).AsGodotDictionary();
                    var bone = expect["hand_bone"].AsString();
                    int element = expect["hand_element"].AsInt32();
                    if (rig.Find(bone) is not { Element: var el, ByHand: true } || el != element)
                        return Done(false, $"the editor's rig does not map {bone} to {element} by hand");
                    _seen.Add($"the editor's rig: {bone} on element {element} by hand");
                }
                _springSkel = drive.Skeleton;
                _springs = drive.Springs;
                drive.Springs.ModificationProcessed += OnSkeletonUpdated;
                _seen.Add($"spring bones: {drive.Springs.GetSettingCount()} chain(s), {drive.Springs.GetJointCount(0)} joints, active {drive.Springs.Active}, in the tree {drive.Springs.IsInsideTree()}");
                if (SyntheticFace(drive, g, has) is { } face)
                    return Done(false, face);
                if (Materials() is { } mat)
                    return Done(false, mat);
                if (_inBattle)
                {
                    _attachedAt = view!.AttachedToRemade;
                    Next(Phase.Battle);
                    break;
                }
                Keys([Key.Left]);
                Next(Phase.WalkRotation);
                break;
            }
            case Phase.WalkRotation:
            {
                if (Measure(view!, angles: true) is { } fail)
                    return Done(false, fail);
                if (_phaseTime > 3.0)
                {
                    if (_angleFrames == 0)
                        return Done(false, "no frame measured");
                    _seen.Add($"remade bones' own proportions: the directions within {_maxAngle:F2}° of the game's (mean {_sumAngle / Math.Max(1, _angleBones):F3}°, {_angleFrames} frames)");
                    foreach (var (d, _, _, _, _) in view!.Remade(Model))
                        foreach (var b in d.Rig.Bones)
                            b.Positions = BonePositions.Game;
                    Keys([Key.Right]);
                    Next(Phase.WalkGame);
                }
                break;
            }
            case Phase.WalkGame:
            {
                if (Measure(view!, angles: false) is { } fail)
                    return Done(false, fail);
                if (_phaseTime > 3.0)
                {
                    Keys([]);
                    foreach (var (d, _, _, _, _) in view!.Remade(Model))
                        foreach (var b in d.Rig.Bones)
                            b.Positions = b.Label == "hips" ? BonePositions.Game : BonePositions.Rotation;
                    if (Verdict() is { } fail2)
                        return Done(false, fail2);
                    Next(Wants() ? Phase.ShotClassic : Phase.Done);
                }
                break;
            }
            case Phase.Battle:
            {
                if (Measure(view!, angles: true) is { } fail)
                    return Done(false, fail);
                Weapon(view!, v);
                if (_phaseTime > 4.0)
                {
                    int moved = view!.AttachedToRemade - _attachedAt;
                    _seen.Add($"in battle: the directions within {_maxAngle:F2}° of the game's ({_angleFrames} frames); {_weapon}; "
                        + $"moved onto the remade hand in {moved} frame(s), its root {_weaponGap:F2} units from the socket (at most {_weaponGapMax:F2}, {_weaponFrames} frames)");
                    if (_maxAngle > AngleTolerance)
                        return Done(false, $"a remade bone points {_maxAngle:F2}° away from the game's: {_worstBone}");
                    if (moved == 0 || _weaponFrames == 0)
                        return Done(false, $"no weapon followed the remade hand ({_weapon})");
                    if (_weaponGapMax > 3f)
                        return Done(false, $"the weapon stood {_weaponGapMax:F2} units from the remade socket");
                    Next(Wants() ? Phase.ShotClassic : Phase.Done);
                }
                break;
            }
            case Phase.ShotClassic when _phaseTime > 1.0:
                Shot(_shot!);
                Remaster.RenderPaths.Set("remastered");
                Next(Phase.ShotRemastered);
                break;
            case Phase.ShotRemastered when _phaseTime > 1.5:
                Shot(_shot![..^4] + "-remastered.png");
                StateView3D.LookAtModel = Model;
                Next(Phase.ShotClose);
                break;
            case Phase.ShotClose when _phaseTime > 1.0:
                Shot(_shot![..^4] + "-closeup.png");
                StateView3D.LookAtModel = null;
                Remaster.RenderPaths.Set(null);
                Next(Phase.Done);
                break;
            case Phase.Done:
                if (_springs != null && GodotObject.IsInstanceValid(_springs))
                    _springs.ModificationProcessed -= OnSkeletonUpdated;
                return Done(true, string.Join("; ", _seen));
        }
        return null;
    }

    /// <summary>The rig against the classic import, the stand-in, saved and registered; an error or null.</summary>
    private string? Build(FfxGame game, string classicPath)
    {
        var classic = ClassicLibrary.Load(classicPath);
        if (classic == null)
            return $"the classic {Model} did not load";
        try
        {
            var cskel = RemadeCharacter.FindSkeleton(classic);
            if (cskel == null)
                return $"the classic {Model} has no skeleton";
            var place = PlaceIn(cskel, classic);
            var gameInfo = SkeletonInfo.Of(cskel, place);
            var cat = CharacterCatalogue.Read(game, "us");
            var entry = cat?.Find(Model);
            if (entry == null)
                return $"{Model} is not in the character list";
            _seen.Add($"{Model} ({entry.Title()}, {entry.Kind}, {entry.Points.Count} attachment points, of {cat!.Entries.Count} characters)");
            var model = StandInHumanoid.Build();
            var skel = RemadeCharacter.FindSkeleton(model)!;
            var rig = new CharacterRig { Character = Model };
            var meshes = model.FindChildren("*", nameof(MeshInstance3D), true, false).OfType<MeshInstance3D>().ToList();
            var cloth = ClothView.Vertices(classic);
            var bind = SkeletonInfo.FromBind(game.ClassicCharacterSkeleton(Model));
            if (bind == null)
                return $"no PS2 skeleton for {Model}";
            var r = RigBuilder.Build(rig, gameInfo, entry.Points, skel, PlaceIn(skel, model), meshes, cloth, bind);
            if (r == null)
            {
                var sInfo = SkeletonInfo.Of(skel, Transform3D.Identity);
                return $"the rig builder did not map the stand-in (game labels: {(RigBuilder.GameLabels(gameInfo, entry.Points) is { } gl ? gl.How : "none")}; "
                    + $"remade labels: {(RigBuilder.Label(sInfo, RigBuilder.GuessUp(sInfo)) is { } rl ? rl.How : "none")}, up {RigBuilder.GuessUp(sInfo)})";
            }
            foreach (var n in r.Notes)
                _seen.Add(n);
            // labels both found: every body part
            var missing = RigBuilder.Parts.Where(p => p is not ("jaw" or "toes.L" or "toes.R") && (r.Game.Of(p) < 0 || r.Remade.Of(p) < 0)).ToList();
            if (missing.Count > 0)
                return $"body parts not found: {string.Join(", ", missing)} (game, {r.Game.How}: {Show(r.Game, gameInfo)}; remade, {r.Remade.How}: {Show(r.Remade, SkeletonInfo.Of(skel, Transform3D.Identity))})";
            // the hierarchy alone finds the stand-in's parts as its names do
            var topo = RigBuilder.Label(Renamed(SkeletonInfo.Of(skel, Transform3D.Identity)), RigBuilder.GuessUp(SkeletonInfo.Of(skel, Transform3D.Identity)));
            var disagree = topo == null ? ["(none found)"] : RigBuilder.Parts.Where(p => p is not ("jaw" or "toes.L" or "toes.R") && topo.Of(p) != r.Remade.Of(p)).ToList();
            _seen.Add(disagree.Count == 0 ? "the hierarchy alone (names hidden) finds the stand-in's parts as its names do"
                : $"the hierarchy alone disagrees with the names on {string.Join(", ", disagree)}");
            if (disagree.Count > 0)
                return $"the mapping by hierarchy and rest positions disagrees with the names on {string.Join(", ", disagree)}";
            // the ponytail on Godot's spring bones (selectable per part), whatever the automatic choice
            foreach (var c in rig.Cloth)
                if (c.Name.StartsWith("Tail", StringComparison.Ordinal))
                    c.Mode = ClothMode.Godot;
            // (the save frees the composed scene, the model with it)
            var path = CharacterScene.Save(_dir, Model, rig, null, model, out var err);
            if (path == null)
                return err;
            RemakeRegistry.Register("chr", Model, path, "selftest-character");
            _seen.Add($"saved {path}");
            return null;
        }
        finally
        {
            classic.Free();
        }
    }

    private static string Show(BodyLabels l, SkeletonInfo s) =>
        string.Join(" ", l.Bones.OrderBy(x => x.Key).Select(x => $"{x.Key}={s.Names[x.Value]}"))
        + $" spine=[{string.Join(",", l.Spine.Select(b => s.Names[b]))}] neck=[{string.Join(",", l.Neck.Select(b => s.Names[b]))}] fwd={l.Forward} right={l.Right}";

    /// <summary>The skeleton's transform in <paramref name="model"/>'s space.</summary>
    private static Transform3D PlaceIn(Node3D skel, Node3D model)
    {
        Transform3D t = Transform3D.Identity;
        for (Node? p = skel; p != null && p != model; p = p.GetParent())
            if (p is Node3D p3)
                t = p3.Transform * t;
        return t;
    }

    /// <summary>A copy of <paramref name="s"/> with its names hidden (the mapping by hierarchy and rest positions alone).</summary>
    private static SkeletonInfo Renamed(SkeletonInfo s) => new()
    {
        Names = Enumerable.Range(0, s.Count).Select(i => $"b{i}").ToArray(),
        Parent = s.Parent, Rest = s.Rest, Element = s.Element,
    };

    /// <summary>A jaw opened by hand opens the mouth: the row's output and the blend shape follow; an error or null.</summary>
    private string? SyntheticFace(Retargeter drive, Transform3D[] g, bool[] has)
    {
        var row = drive.Rig.Face.FirstOrDefault(f => f.Name == "mouth");
        if (row == null)
            return "no mouth row in the face table";
        if (row.Element >= has.Length || !has[row.Element] || row.Relative < 0 || !has[row.Relative])
            return $"the game does not drive the jaw element {row.Element} (or its parent {row.Relative})";
        var game = (Transform3D[])g.Clone();
        var flags = (bool[])has.Clone();
        var local = game[row.Relative].AffineInverse() * game[row.Element];
        float before = Retargeter.FaceInput(local, row.Source);
        var opened = local * new Transform3D(new Basis(Vector3.Right, Mathf.DegToRad(5f)), Vector3.Zero);
        game[row.Element] = game[row.Relative] * opened;
        drive.Apply(game, flags, null);
        float after = Retargeter.FaceInput(opened, row.Source);
        float output = drive.FaceOutputs[drive.Rig.Face.IndexOf(row)];
        float expect = row.Map(after);
        var head = drive.Root.FindChild("Head", true, false) as MeshInstance3D;
        int shape = head?.FindBlendShapeByName(row.TargetName) ?? -1;
        float set = shape >= 0 ? head!.GetBlendShapeValue(shape) : float.NaN;
        drive.Apply(g, has, null);
        _syntheticFace = $"the jaw opened 5° by hand: input {before:F2}° -> {after:F2}°, {row.TargetName} {row.Map(before):F2} -> {output:F2}";
        _seen.Add(_syntheticFace);
        if (Mathf.Abs(output - expect) > 1e-4f || Mathf.Abs(set - output) > 1e-4f || output <= row.Map(before))
            return $"the face table did not follow a jaw opened by hand ({_syntheticFace}; blend shape {set:F3}, expected {expect:F3})";
        return null;
    }

    /// <summary>The presets load as the materials they are (their shaders compile under a renderer: the log has no shader error).</summary>
    private string? Materials()
    {
        var made = new List<string>();
        foreach (var name in CharacterMaterials.Presets)
        {
            var path = $"{CharacterMaterials.Dir}/{name}.tres";
            if (ResourceLoader.Load<Material>(path) is not { } m)
                return $"the preset {path} did not load";
            if (m is ShaderMaterial { Shader: { } sh } && sh.Code.Length == 0)
                return $"the preset {path}'s shader is empty";
            made.Add(m is ShaderMaterial ? $"{name} (shader)" : name);
        }
        var skin = CharacterMaterials.Skin() as StandardMaterial3D;
        if (skin is not { SubsurfScatterEnabled: true, SubsurfScatterSkinMode: true, SubsurfScatterTransmittanceEnabled: true })
            return "the skin preset has no subsurface scattering in skin mode with transmittance";
        _seen.Add($"material presets: {string.Join(", ", made)}");
        return null;
    }

    private void OnSkeletonUpdated()
    {
        // after Godot's modifiers (the spring bones) ran: the ponytail against the head
        if (_springSkel == null || !GodotObject.IsInstanceValid(_springSkel))
            return;
        int head = _springSkel.FindBone("Head"), tail = _springSkel.FindBone("Tail_2");
        if (head < 0 || tail < 0)
            return;
        var rel = _springSkel.GetBoneGlobalPose(head).AffineInverse() * _springSkel.GetBoneGlobalPose(tail);
        var rest = _springSkel.GetBoneGlobalRest(head).AffineInverse() * _springSkel.GetBoneGlobalRest(tail);
        float fromRest = Mathf.RadToDeg(rest.Basis.GetRotationQuaternion().AngleTo(rel.Basis.GetRotationQuaternion()));
        _tailRest = Math.Max(_tailRest, fromRest);
        _tailAngles.Add(fromRest);
        if (_tailLast is { } last)
        {
            float step = Mathf.RadToDeg(last.Basis.GetRotationQuaternion().AngleTo(rel.Basis.GetRotationQuaternion()));
            _tailMoved += step;
            _tailStep = Math.Max(_tailStep, step);
        }
        _tailLast = rel;
        _springFrames++;
    }

    /// <summary>One frame's measures of the shown remade character; an error or null.</summary>
    private string? Measure(StateView3D view, bool angles)
    {
        var list = view.Remade(Model);
        if (list.Count == 0)
            return $"the remade {Model} went away";
        var (drive, g, has, model, _) = list[0];
        var skel = drive.Skeleton;
        var k = drive.K;
        float worst = 0, sum = 0;
        int n = 0;
        foreach (var m in drive.Rig.Bones)
        {
            int b = skel.FindBone(m.Bone);
            if (b < 0 || m.Element < 0 || m.Element >= has.Length || !has[m.Element])
                continue;
            var pose = skel.GetBoneGlobalPose(b);
            if (angles)
            {
                int c = m.Aim.Length > 0 ? skel.FindBone(m.Aim) : -1;
                var mc = c >= 0 ? drive.Rig.Find(m.Aim) : null;
                if (mc == null || mc.Element >= has.Length || !has[mc.Element])
                    continue;
                var dr = k.Basis * (skel.GetBoneGlobalPose(c).Origin - pose.Origin);
                var dg = g[mc.Element].Origin - g[m.Element].Origin;
                if (dr.LengthSquared() < 1e-10f || dg.LengthSquared() < 1e-10f)
                    continue;
                float a = Mathf.RadToDeg(dr.AngleTo(dg));
                if (a > _maxAngle && a > worst)
                    _worstBone = $"{m.Bone} (element {m.Element}) to {m.Aim} (element {mc.Element}): {a:F2}°";
                worst = Math.Max(worst, a);
                sum += a;
            }
            else
            {
                float d = (k * pose.Origin).DistanceTo(g[m.Element].Origin);
                worst = Math.Max(worst, d);
                sum += d;
            }
            n++;
        }
        if (n == 0)
            return "no mapped bone driven by the game";
        if (angles)
        {
            _maxAngle = Math.Max(_maxAngle, worst);
            _sumAngle += sum;
            _angleBones += n;
            _angleFrames++;
        }
        else
        {
            _maxPos = Math.Max(_maxPos, worst);
            _sumPos += sum;
            _posBones += n;
            _posFrames++;
        }
        // the walk: the hips in the world
        int hips = drive.Rig.Bones.FirstOrDefault(b => b.Label == "hips") is { } hb ? skel.FindBone(hb.Bone) : -1;
        if (hips >= 0)
        {
            var w = (skel.GlobalTransform * skel.GetBoneGlobalPose(hips)).Origin;
            _hipsFirst ??= w;
            _hipsMoved = Math.Max(_hipsMoved, w.DistanceTo(_hipsFirst.Value));
        }
        // the hood on the game's cloth
        if (drive.ClothTurned > 0)
            _clothFrames++;
        int hood = skel.FindBone("Hood_1");
        if (hood >= 0)
        {
            var q = skel.GetBonePoseRotation(hood);
            if (_hoodLast is { } last)
            {
                float step = Mathf.RadToDeg(last.AngleTo(q));
                _hoodMoved += step;
                _hoodStep = Math.Max(_hoodStep, step);
            }
            _hoodLast = q;
            _hoodTurn = Math.Max(_hoodTurn, drive.ClothAngle);
            _hoodTurnSum += drive.ClothAngle;
        }
        // the face table: the row's output on the blend shape each frame
        var row = drive.Rig.Face.FirstOrDefault(f => f.Name == "mouth");
        if (row != null && row.Element < has.Length && has[row.Element] && row.Relative >= 0 && has[row.Relative])
        {
            float input = Retargeter.FaceInput(g[row.Relative].AffineInverse() * g[row.Element], row.Source);
            float output = drive.FaceOutputs[drive.Rig.Face.IndexOf(row)];
            _jawMin = Math.Min(_jawMin, input);
            _jawMax = Math.Max(_jawMax, input);
            _outMin = Math.Min(_outMin, output);
            _outMax = Math.Max(_outMax, output);
            var head = drive.Root.FindChild("Head", true, false) as MeshInstance3D;
            int shape = head?.FindBlendShapeByName(row.TargetName) ?? -1;
            if (shape < 0 || Mathf.Abs(head!.GetBlendShapeValue(shape) - output) > 1e-4f || Mathf.Abs(row.Map(input) - output) > 1e-3f)
                _faceMismatch++;
            _faceFrames++;
        }
        return null;
    }

    /// <summary>A weapon bound to the remade character (kind 4, its parent the character): its root against the remade socket bone the rig attaches its element to.</summary>
    private void Weapon(StateView3D view, GameStateView v)
    {
        var list = view.Remade(Model);
        if (list.Count == 0 || v.Current == null)
            return;
        var (drive, _, _, _, slot) = list[0];
        foreach (var a in v.Current.Actors)
        {
            if (a.Parent != slot || !GameStateView.Drawn(a) || view.ActorRoot(a.Slot) is not { } root)
                continue;
            var att = drive.Rig.Attach.FirstOrDefault(x => x.Element == a.ParentBone);
            var mapped = drive.Rig.Bones.FirstOrDefault(x => x.Element == a.ParentBone);
            var line = $"{a.Model} bound to element {a.ParentBone}" + (att != null ? $" (attached to {att.Bone})" : mapped != null ? $" (on its mapped bone {mapped.Bone})" : " (nothing remade there)");
            if (!_weapons.Contains(line))
            {
                _weapons.Add(line);
                _weapon = string.Join(", ", _weapons);
            }
            if (att == null || drive.Skeleton.FindBone(att.Bone) is var b && b < 0)
                continue;
            var socket = (drive.Skeleton.GlobalTransform * drive.Skeleton.GetBoneGlobalPose(b)).Origin;
            _weaponGap = root.GlobalPosition.DistanceTo(socket);
            _weaponGapMax = Math.Max(_weaponGapMax, _weaponGap);
            _weaponFrames++;
        }
    }

    /// <summary>The walk's verdict; an error or null.</summary>
    private string? Verdict()
    {
        _seen.Add($"the worst direction: {_worstBone}");
        _seen.Add($"every mapped joint on the game's: within {_maxPos:F4} units (mean {_sumPos / Math.Max(1, _posBones):F5}, {_posFrames} frames)");
        _seen.Add($"the remade hips walked {_hipsMoved:F1} units");
        _seen.Add($"the hood turned by the game's cloth in {_clothFrames} frame(s) (from its skinned pose up to {_hoodTurn:F1}°, mean {_hoodTurnSum / Math.Max(1, _clothFrames):F1}°), "
            + $"moving {_hoodMoved:F1}° in all, at most {_hoodStep:F2}° a frame; the ponytail on Godot's spring bones moving {_tailMoved:F1}° in all "
            + $"(at most {_tailStep:F2}° a frame, {_tailRest:F1}° from its rest at most; {_springFrames} updates)");
        if (_tailAngles.Count > 0)
        {
            var sorted = _tailAngles.OrderBy(x => x).ToList();
            _seen.Add($"the ponytail from its rest: median {sorted[sorted.Count / 2]:F1}°, 95 % under {sorted[(int)(sorted.Count * 0.95)]:F1}°, mean {sorted.Average():F1}°");
        }
        _seen.Add($"the face table driven in {_faceFrames} frame(s) ({_faceMismatch} off): the jaw {_jawMin:F2}..{_jawMax:F2}°, mouth_open {_outMin:F2}..{_outMax:F2}");
        if (_maxAngle > AngleTolerance)
            return $"a remade bone points {_maxAngle:F2}° away from the game's (tolerance {AngleTolerance}°): {_worstBone}";
        if (_maxPos > PositionTolerance)
            return $"a remade joint stands {_maxPos:F3} units from the game's (tolerance {PositionTolerance})";
        if (_hipsMoved < 2f)
            return $"the remade character did not walk (its hips moved {_hipsMoved:F2} units)";
        if (_clothFrames == 0 || _hoodMoved < 0.5f)
            return $"the hood did not follow the game's cloth ({_clothFrames} frames, {_hoodMoved:F3}°)";
        if (_hoodTurn > 60f)
            return $"the game's cloth turned the hood {_hoodTurn:F1}° from its skinned pose (the cloth points do not follow the chain)";
        if (_springFrames == 0 || _tailMoved < 0.5f)
            return $"the ponytail did not move on Godot's spring bones ({_springFrames} updates, {_tailMoved:F2}°)";
        // stable: swinging with the walk (a flipping simulation is half a turn off most of the time);
        // a turn about of the character swings it further for a moment
        var tail = _tailAngles.OrderBy(x => x).ToList();
        if (tail.Count > 0 && tail[(int)(tail.Count * 0.95)] > 60f)
            return $"the ponytail hung more than 60° from its rest in over 5 % of the frames (the spring bones are unstable)";
        if (_faceFrames == 0 || _faceMismatch > 0)
            return $"the face table was not driven ({_faceFrames} frames, {_faceMismatch} off)";
        return null;
    }

    private static readonly HashSet<Key> Held = new();

    private static void Keys(Key[] keys)
    {
        foreach (var k in Held)
            if (Array.IndexOf(keys, k) < 0)
                Godot.Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = k, Keycode = k, Pressed = false });
        foreach (var k in keys)
            if (!Held.Contains(k))
                Godot.Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = k, Keycode = k, Pressed = true });
        Held.Clear();
        foreach (var k in keys)
            Held.Add(k);
    }

    private bool Wants() => _shot != null && DisplayServer.GetName() != "headless";

    private static void Shot(string file)
    {
        (Engine.GetMainLoop() as SceneTree)!.Root.GetViewport().GetTexture().GetImage().SavePng(file);
        GD.Print($"ffx selftest: picture {file}");
    }

    private void Next(Phase p)
    {
        GD.Print($"ffx selftest: {_phase} -> {p} ({_total:F1} s)");
        _phase = p;
        _phaseTime = 0;
    }

    private bool Done(bool pass, string note)
    {
        Note = pass ? note : $"{note} [{string.Join("; ", _seen)}]";
        return pass;
    }
}

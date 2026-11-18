#if TOOLS
using System.Collections.Generic;
using System.Linq;
using GdFfx.Characters;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>
/// A character opened in the editor (docs/remaster-editor.md, "Characters"): the scene
/// <c>res://remaster/edit/chr/&lt;id&gt;.tscn</c> holds this root and the id only (gitignored, not
/// exported); in the editor it builds, under a node in the game's character space (half a turn
/// about z, as the game places characters):
/// <list type="bullet">
/// <item><c>Classic</c>: the classic import with its motions (<c>chr-motions</c>: the game's
/// motion groups as animations, <see cref="Player"/>), the game's skeleton and its attachment
/// points;</item>
/// <item><c>Remade</c>: the remade scene when one is assigned (<see cref="CharacterStore"/>), beside
/// the classic one (<see cref="SideBySide"/>) or on it, driven each frame by the same
/// <see cref="Retargeter"/> as in the game, from the classic skeleton's animated pose.</item>
/// </list>
/// Nothing it builds is saved with the scene.
/// </summary>
[Tool]
public partial class CharacterEditRoot : Node3D
{
    public const string EditDir = "res://remaster/edit/chr";

    public static string EditScenePath(string id) => $"{EditDir}/{id}.tscn";

    [Export] public string Character { get; set; } = "";

    public bool Built { get; private set; }

    public string Status { get; private set; } = "waiting for the archive";

    /// <summary>The game's character space (half a turn about z).</summary>
    public Node3D? Space { get; private set; }

    public Node3D? Classic { get; private set; }
    public Skeleton3D? ClassicSkeleton { get; private set; }
    public AnimationPlayer? Player { get; private set; }
    /// <summary>The classic skeleton's place in the model (character space).</summary>
    public Transform3D ClassicPlace { get; private set; } = Transform3D.Identity;

    public RemadeCharacter? Remade { get; private set; }
    public Retargeter? Driver { get; private set; }

    /// <summary>The game's element globals this frame (from the classic skeleton's pose) and which there are.</summary>
    public Transform3D[] Game { get; private set; } = [];
    public bool[] Has { get; private set; } = [];

    /// <summary>The bone of each element in the classic skeleton (-1 none).</summary>
    private int[] _boneOf = [];

    /// <summary>How far beside the classic model the remade one stands (character units; 0: on it).</summary>
    public float SideBySide { get; set; } = 14f;

    private Node3D? _offset;

    /// <summary>The motion playing (empty: none), whether it plays, and its time.</summary>
    public string Motion { get; private set; } = "";
    public bool Playing { get; private set; }
    public double Time { get; private set; }

    public override void _Process(double delta)
    {
        if (!Engine.IsEditorHint())
            return;
        if (!Built)
        {
            TryBuild();
            return;
        }
        if (Player != null && Motion.Length > 0)
        {
            if (Playing)
            {
                Time += delta;
                var len = Player.GetAnimation(Motion)?.Length ?? 0;
                if (len > 0)
                    Time %= len;
            }
            Player.Seek(Time, true);
        }
        Drive();
    }

    private void TryBuild()
    {
        if (RemasterPlugin.Instance?.Session is not { IsOpen: true } session || session.Classic is not { } lib)
            return;
        if (lib.Failed("chr-motions", Character))
        {
            Status = $"{Character}: the classic import failed";
            return;
        }
        if (lib.Path("chr-motions", Character) is not { } path)
        {
            Status = $"importing {Character} with its motions";
            return;
        }
        var model = ClassicLibrary.Load(path);
        if (model == null)
        {
            Status = $"{Character}: {path} did not load";
            Built = true;
            return;
        }
        Space = new Node3D { Name = "GameSpace", Transform = new Transform3D(new Basis(new Vector3(-1, 0, 0), new Vector3(0, -1, 0), new Vector3(0, 0, 1)), Vector3.Zero) };
        Space.SetMeta(MapEditRoot.EditorNode, true);
        AddChild(Space);
        model.Name = "Classic";
        Space.AddChild(model);
        Classic = model;
        ClassicSkeleton = RemadeCharacter.FindSkeleton(model);
        Player = model.FindChildren("*", nameof(AnimationPlayer), true, false).FirstOrDefault() as AnimationPlayer;
        if (ClassicSkeleton != null)
        {
            Transform3D t = Transform3D.Identity;
            for (Node? p = ClassicSkeleton; p != null && p != model; p = p.GetParent())
                if (p is Node3D p3)
                    t = p3.Transform * t;
            ClassicPlace = t;
            int max = 0;
            for (int b = 0; b < ClassicSkeleton.GetBoneCount(); b++)
                if (Element(ClassicSkeleton.GetBoneName(b)) is var e && e > max)
                    max = e;
            _boneOf = Enumerable.Repeat(-1, max + 1).ToArray();
            for (int b = 0; b < ClassicSkeleton.GetBoneCount(); b++)
                if (Element(ClassicSkeleton.GetBoneName(b)) is var e && e >= 0)
                    _boneOf[e] = b;
        }
        LoadRemade();
        Built = true;
        Status = $"{Character}: classic with {Motions().Count} motion(s){(Remade != null ? ", remade" : ", no remake assigned")}";
        RemasterPlugin.Instance?.CharacterBuilt(this);
    }

    private static int Element(string name) =>
        name.StartsWith("SkeletonNode_", System.StringComparison.Ordinal) && int.TryParse(name["SkeletonNode_".Length..], out var e) ? e : -1;

    /// <summary>The motions (animation names: <c>resident1/12 (0001)</c>, group, clip and the motions that play it).</summary>
    public List<string> Motions() =>
        Player == null ? [] : Player.GetAnimationList().Where(n => n.Length > 0).OrderBy(n => n, System.StringComparer.Ordinal).ToList();

    public void Play(string motion)
    {
        if (Player == null || !Player.HasAnimation(motion))
            return;
        Motion = motion;
        Time = 0;
        Playing = true;
        Player.Play(motion);
        Player.Pause();
        Player.Seek(0, true);
    }

    public void Pause() => Playing = false;

    public void Resume() => Playing = Motion.Length > 0;

    public void Scrub(double t)
    {
        Time = t;
        Playing = false;
        if (Player != null && Motion.Length > 0)
            Player.Seek(t, true);
        Drive();
    }

    public double Length => Player != null && Motion.Length > 0 ? Player.GetAnimation(Motion)?.Length ?? 0 : 0;

    /// <summary>(Re)loads the remade scene assigned to the character (the project's or the check's folder), driven by its rig.</summary>
    public void LoadRemade()
    {
        _offset?.QueueFree();
        _offset = null;
        Remade = null;
        Driver = null;
        if (Space == null || CharacterStore.Scene(Character) is not { } path)
            return;
        if (ResourceLoader.Load(path, null, ResourceLoader.CacheMode.Replace) is not PackedScene ps || ps.Instantiate() is not RemadeCharacter rc)
        {
            Status = $"{Character}: {path} is not a remade character scene";
            return;
        }
        // the rig as the editor holds it (edits not saved yet), else the scene's
        if (CharacterStore.Rig(Character) is { } rig)
            rc.Rig = rig;
        _offset = new Node3D { Name = "Remade", Position = new Vector3(-SideBySide, 0, 0) };
        _offset.SetMeta(MapEditRoot.EditorNode, true);
        Space.AddChild(_offset);
        _offset.AddChild(rc);
        Remade = rc;
        Driver = rc.Rig != null ? rc.Drive() : null;
        Game = new Transform3D[System.Math.Max(_boneOf.Length, (Driver?.MaxElement ?? -1) + 1)];
        Has = new bool[Game.Length];
        Drive();
    }

    /// <summary>The game's element globals from the classic skeleton's pose into the remade driver.</summary>
    public void Drive()
    {
        if (ClassicSkeleton == null)
            return;
        for (int e = 0; e < Game.Length; e++)
        {
            int b = e < _boneOf.Length ? _boneOf[e] : -1;
            Has[e] = b >= 0;
            if (b >= 0)
                Game[e] = ClassicPlace * ClassicSkeleton.GetBoneGlobalPose(b);
        }
        Driver?.Apply(Game, Has, null);
    }

    public override void _Notification(int what)
    {
        // the scene saves the root and the id only
        if (what == NotificationEditorPreSave)
            foreach (var c in GetChildren())
                if (c.Owner == this)
                    c.Owner = null;
    }
}
#endif

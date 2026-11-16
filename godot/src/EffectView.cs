using System.Collections.Generic;
using GdFfx.Remake;
using Godot;

namespace GdFfx;

/// <summary>
/// G3's effects as events (docs/architecture.md, "Effects"): each effect record the game
/// starts (the game-state API's <c>Effects.Running</c>, keyed by slot) is played where the
/// game plays it, on each of its targets or else on its character, for as long as the game
/// keeps the record:
/// <list type="bullet">
/// <item>a remade effect registered for its id (kind <c>effect</c>, id the effect id as eight
/// hex digits, e.g. <c>00030102</c>; docs/remake.md), instanced there;</item>
/// <item>otherwise the game's own draws (<see cref="EffectDrawView"/>), or, when those are
/// not captured, a stand-in: a short burst of particles in a colour of its id.</item>
/// </list>
/// Who a character index is: in battle the battler (<see cref="BattleHudView.ActorOf"/>), out
/// of battle the actor in that slot.
/// </summary>
public partial class EffectView : Node3D
{
    private sealed class Playing
    {
        public uint Id;
        public readonly List<Node3D> Nodes = new();
        public double Age;
    }

    private readonly Dictionary<ushort, Playing> _playing = new();

    /// <summary>Seconds a stand-in burst lasts.</summary>
    public const double StandInTime = 1.0;

    /// <summary>Effects started so far, and how many of them were remade (for tests).</summary>
    public int Started { get; private set; }
    public int RemadeStarted { get; private set; }

    /// <summary>The registry id of an effect id.</summary>
    public static string EffectKey(uint id) => id.ToString("x8");

    private static Vector3? Where(GdFfx.Api.Snapshot s, int character)
    {
        GdFfx.Api.Actor? a = null;
        if (s.World.Battle)
            a = BattleHudView.ActorOf(s, character);
        else
            foreach (var x in s.Actors)
                if (x.Slot == character)
                    a = x;
        return a == null ? null : GameStateView.FieldPoint(a.Position) + Vector3.Up * 8f;
    }

    private static Node3D StandIn(uint id)
    {
        var hue = (id * 2654435761u >> 8) % 360 / 360f;
        var colour = Color.FromHsv(hue, 0.6f, 1f);
        var p = new CpuParticles3D
        {
            Emitting = true,
            OneShot = true,
            Amount = 32,
            Lifetime = StandInTime * 0.8,
            Explosiveness = 0.9f,
            Direction = Vector3.Up,
            Spread = 180f,
            InitialVelocityMin = 6f,
            InitialVelocityMax = 14f,
            Gravity = new Vector3(0, -6f, 0),
            ScaleAmountMin = 0.6f,
            ScaleAmountMax = 1.4f,
            Color = colour,
            Mesh = new SphereMesh
            {
                Radius = 0.5f,
                Height = 1f,
                RadialSegments = 6,
                Rings = 3,
                Material = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    VertexColorUseAsAlbedo = true,
                    AlbedoColor = colour,
                },
            },
        };
        var n = new Node3D { Name = "StandInEffect" };
        n.AddChild(p);
        n.SetMeta("ffx_source", "stand-in");
        return n;
    }

    public override void _Process(double delta)
    {
        var t0 = Perf.Start();
        Tick(delta);
        Perf.End(Perf.Section.EffectView, t0);
    }

    private readonly HashSet<ushort> _seen = new();
    private readonly List<ushort> _slots = new();

    private void Tick(double delta)
    {
        var s = GameHost.Instance?.View.Current;
        // kept between frames (a new set and list a frame otherwise)
        var seen = _seen;
        seen.Clear();
        if (s != null)
            foreach (var e in s.Effects.Running)
            {
                if (e.Ended)
                    continue;
                seen.Add(e.Slot);
                if (_playing.TryGetValue(e.Slot, out var p) && p.Id == e.Id)
                    continue;
                if (p != null)
                    Stop(e.Slot);
                Start(s, e);
            }
        _slots.Clear();
        _slots.AddRange(_playing.Keys);
        foreach (var slot in _slots)
        {
            var p = _playing[slot];
            p.Age += delta;
            // the game's record ended: a remade effect stops with it, a stand-in burst plays out
            if (!seen.Contains(slot) && (p.Nodes.Count == 0 || p.Nodes[0].GetMeta("ffx_source").AsString() != "stand-in" || p.Age > StandInTime))
                Stop(slot);
        }
    }

    /// <summary>Whether the game's own effect draws are being drawn (<see cref="EffectDrawView"/>).</summary>
    private bool ClassicDrawn() =>
        GetParent()?.GetNodeOrNull<EffectDrawView>("EffectDrawView") is { } v && System.Environment.GetEnvironmentVariable("FFX_NO_VFX") == null
        && v.Shown.Draws > 0;

    private void Start(GdFfx.Api.Snapshot s, GdFfx.Api.EffectInstance e)
    {
        var p = new Playing { Id = e.Id };
        var at = new List<Vector3>();
        foreach (var t in e.Targets)
            if (Where(s, t) is { } w)
                at.Add(w);
        if (at.Count == 0 && Where(s, e.Character) is { } c)
            at.Add(c);
        if (at.Count == 0)
            at.Add(Vector3.Zero);
        bool remade = false;
        foreach (var w in at)
        {
            Node3D node;
            if (ContentResolver.Resolve(null, "effect", EffectKey(e.Id)) is { Node: { } n, Remade: true })
            {
                node = n;
                remade = true;
            }
            else if (ClassicDrawn())
                continue; // the game's own draws show it (EffectDrawView)
            else
                node = StandIn(e.Id);
            node.Position = w;
            AddChild(node);
            p.Nodes.Add(node);
        }
        _playing[e.Slot] = p;
        Started++;
        if (remade)
            RemadeStarted++;
    }

    private void Stop(ushort slot)
    {
        if (!_playing.Remove(slot, out var p))
            return;
        foreach (var n in p.Nodes)
            n.QueueFree();
    }
}

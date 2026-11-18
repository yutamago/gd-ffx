using System.Collections.Generic;
using GdFfx.Remake;
using Godot;

namespace GdFfx.Remaster;

/// <summary>
/// Remade effects in the game (R7; docs/remake.md, "Effects"): each frame
/// <see cref="EffectDrawView"/> hands over the classic draws with their owners
/// (<see cref="EffectOwners"/>); every effect id with a remade scene (kind <c>effect</c>,
/// <see cref="RemakeRegistry"/>) gets the scene instanced at the effect's place while the
/// classic effect has draws, and the classic draws of that id are left out
/// (<see cref="Hidden"/>), unless the remake's root says otherwise
/// (<see cref="RemadeEffect.HideClassic"/>) or replacements are off (<see cref="Replacements"/>,
/// F8). The place: a field program's own (its table entry, render space), or the centre of the
/// effect's draws (an entry stuck to an object, one its program places itself, a spell). Every effect seen is also
/// recorded for the editor (<see cref="EffectRecorder"/>).
/// </summary>
public partial class EffectRemakeView : Node3D
{
    /// <summary>Seconds without a draw after which an effect counts as ended (a frame or two can lack one).</summary>
    public const double Hold = 0.25;

    private sealed class Playing
    {
        public string Id = "";
        public Node3D? Node;
        public Vector3 Place;
        public double Since, LastSeen;
        public bool HideClassic = true, Follow = true;
        public Vector3 Offset;
        public double Linger;
        /// <summary>When stopped: the time it is freed.</summary>
        public double FreeAt = -1;
    }

    private readonly Dictionary<string, Playing> _playing = new();
    private readonly List<Playing> _stopping = new();
    private double _clock;

    /// <summary>The id of each draw of the last frame (null: no owner).</summary>
    public string?[] Ids { get; private set; } = [];

    /// <summary>The ids whose classic draws are left out this frame.</summary>
    public HashSet<string> Hidden { get; } = new();

    /// <summary>The ids with draws in the last frame, and their places (for tests and the overlay).</summary>
    public Dictionary<string, Vector3> Seen { get; } = new();

    /// <summary>Since the start: remakes started, per id; classic draws left out.</summary>
    public Dictionary<string, int> Started { get; } = new();
    public int HiddenDraws { get; private set; }

    /// <summary>The remade effect playing for <paramref name="id"/> (null none), for tests.</summary>
    public Node3D? Remake(string id) => _playing.TryGetValue(id, out var p) ? p.Node : null;

    private readonly EffectOwners.Frame _owners = new();
    private readonly HashSet<string> _logged = new();
    private readonly Dictionary<string, (Vector3 Min, Vector3 Max)> _bounds = new();

    /// <summary>The bounds of each effect's draws in the last frame (render space).</summary>
    public IReadOnlyDictionary<string, (Vector3 Min, Vector3 Max)> Bounds => _bounds;

    /// <summary>
    /// A new frame of classic draws (<paramref name="draws"/> over <paramref name="geo"/>) of
    /// update <paramref name="update"/> on map <paramref name="level"/>: owners resolved, remakes
    /// started, moved and stopped, the recordings fed.
    /// </summary>
    public void Frame(FfxGame game, uint update, string level, List<EffectDrawView.Draw> draws, EffectDrawView.Geometry geo)
    {
        Hidden.Clear();
        Seen.Clear();
        _bounds.Clear();
        if (Ids.Length < draws.Count)
            Ids = new string?[System.Math.Max(draws.Count, Ids.Length * 2)];
        System.Array.Clear(Ids, 0, Ids.Length);
        bool have = EffectOwners.Decode(game.EffectDrawOwners(update), _owners) && _owners.Count == draws.Count;
        if (have)
            for (int i = 0; i < draws.Count; i++)
            {
                var id = EffectOwners.IdOf(_owners.Draws[i], level);
                Ids[i] = id;
                if (id == null)
                    continue;
                // the bounds only where needed: an effect still being recorded, or one with a remake
                if (EffectRecorder.IsWritten(id) && !_playing.ContainsKey(id) && !Remadeable(id))
                    continue;
                var d = draws[i];
                if (d.Vertices == 0)
                    continue;
                var (mn, mx) = _bounds.TryGetValue(id, out var b) ? b : (Vector3.Inf, -Vector3.Inf);
                for (int k = 0; k < d.Vertices; k++)
                {
                    var p = geo.Positions[d.First + k];
                    if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z))
                        continue;
                    mn = mn.Min(p);
                    mx = mx.Max(p);
                }
                _bounds[id] = (mn, mx);
            }
        foreach (var (id, (mn, mx)) in _bounds)
        {
            var place = mn.X <= mx.X ? (mn + mx) * 0.5f : Vector3.Zero;
            uint owner = 0;
            for (int i = 0; i < draws.Count && owner == 0; i++)
                if (Ids[i] == id)
                    owner = _owners.Draws[i];
            // a field program's own place, unless it sticks to an object or its program places
            // its draws itself (the entry at the origin, or far from its draws: the save sphere's)
            if (_owners.Places.TryGetValue(owner, out var fp) && !fp.Stuck && fp.Place != Vector3.Zero
                && fp.Place.DistanceTo(place) < 60f)
                place = fp.Place;
            Seen[id] = place;
            if (_logged.Add(id))
            {
                int n = 0;
                string tex = "";
                for (int i = 0; i < draws.Count; i++)
                    if (Ids[i] == id)
                    {
                        n++;
                        if (tex.Length == 0 && draws[i].Texture.Length > 0)
                            tex = draws[i].Texture;
                    }
                GD.Print($"ffx: effect {id} seen ({EffectOwners.Describe(id)}): {n} draw(s) at {place}, texture \"{tex}\"");
            }
            Touch(id, place);
        }
        if (have)
            EffectRecorder.Frame(level, draws, geo, Ids, Seen);
        int hidden = 0;
        for (int i = 0; i < draws.Count; i++)
            if (Ids[i] is { } id && Hidden.Contains(id))
                hidden++;
        HiddenDraws += hidden;
    }

    private readonly Dictionary<string, bool> _remadeable = new();
    private int _registryVersion = -1, _lookups;

    /// <summary>Whether <paramref name="id"/> has a remade scene (cached a while).</summary>
    private bool Remadeable(string id)
    {
        // asked again after a flip and every 2 s (a registration at run time is seen)
        if (_registryVersion != Replacements.Changed || ++_lookups > 60 * _remadeable.Count + 60)
        {
            _registryVersion = Replacements.Changed;
            _lookups = 0;
            _remadeable.Clear();
        }
        if (!_remadeable.TryGetValue(id, out var r))
            _remadeable[id] = r = RemakeRegistry.Find("effect", id) != null;
        return r;
    }

    /// <summary>Forgets which ids have remakes (the registry changed).</summary>
    public void RegistryChanged() => _remadeable.Clear();

    /// <summary>The classic effect <paramref name="id"/> drew at <paramref name="place"/> now.</summary>
    private void Touch(string id, Vector3 place)
    {
        if (!Replacements.On)
            return;
        if (!_playing.TryGetValue(id, out var p))
        {
            if (RemakeRegistry.Find("effect", id) == null)
                return;
            if (ContentResolver.Resolve(null, "effect", id) is not { Node: { } node, Remade: true })
                return;
            var (hide, follow, offset, linger) = RemadeEffect.Options(node);
            p = new Playing { Id = id, Node = node, Place = place, Since = _clock, HideClassic = hide, Follow = follow, Offset = offset, Linger = linger };
            node.Name = "Remade_" + id;
            node.Position = place + offset;
            AddChild(node);
            _playing[id] = p;
            Started[id] = Started.GetValueOrDefault(id) + 1;
            GD.Print($"ffx: effect {id} remade by {RemakeRegistry.Find("effect", id)} at {place}");
        }
        p.LastSeen = _clock;
        p.Place = place;
        if (p.Follow && p.Node != null)
            p.Node.Position = place + p.Offset;
        if (p.HideClassic)
            Hidden.Add(id);
    }

    public override void _Process(double delta)
    {
        _clock += delta;
        List<string>? ended = null;
        foreach (var (id, p) in _playing)
            if (_clock - p.LastSeen > Hold || !Replacements.On)
                (ended ??= new()).Add(id);
        if (ended != null)
            foreach (var id in ended)
                Stop(id, !Replacements.On);
        for (int i = _stopping.Count - 1; i >= 0; i--)
            if (_clock >= _stopping[i].FreeAt)
            {
                _stopping[i].Node?.QueueFree();
                _stopping.RemoveAt(i);
            }
    }

    private void Stop(string id, bool now)
    {
        if (!_playing.Remove(id, out var p) || p.Node == null)
            return;
        var dies = RemadeEffect.StopEmitting(p.Node);
        p.FreeAt = _clock + (now ? 0 : p.Linger > 0 ? p.Linger : dies);
        _stopping.Add(p);
    }

    /// <summary>The effects playing and stopping (for tests).</summary>
    public int Count => _playing.Count;
}

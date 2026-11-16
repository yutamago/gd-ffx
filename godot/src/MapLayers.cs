using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Godot;

namespace GdFfx;

/// <summary>
/// A map's 2D layers, the remaster's painted backgrounds (ffx-rust's
/// docs/formats/scene-maps.md, "How the game draws the 2D layers"): the classic import
/// <c>map2d</c> (<c>.../&lt;map&gt;/2d/mdl/d3d11/&lt;map&gt;.dae.phyre</c>), one quad per layer
/// (mesh <c>scene&lt;s&gt;layer&lt;l&gt;Shape</c>) in PS2 screen pixels at its depth, drawn by
/// <see cref="ClassicMaterials"/>' layer shader over the whole window whatever the 3D
/// camera, at the main camera's depth for 10 x its z, so the characters go behind nearer
/// layers.
/// <para>
/// What the game decides at run time is read from its memory each frame (<see cref="FfxGame.Peek"/>;
/// these belong in the game-state API, ffx-state, later): the 2D scene shown (scene
/// <c>+0x11d8</c>; a map starts on scene 0, scripts change it with
/// <c>PhyreScene_change2DSceneNum</c>), the layers' shown flags (<c>PhyreScene_show2DLayer</c>'s
/// table at <c>+0x1140</c>, <c>+0x1138</c> entries of 8 bytes: shown, scene, layer id (i16),
/// mesh), the pan <c>b</c> (<c>+0xf9bc</c>) and shift (<c>+0xfa34</c>, <c>+0xfa38</c>) of the
/// 16:9 window, whether the window is squeezed (not when <c>+0xf9c7</c> or
/// <c>g_PhyreScene3D</c>, <c>0x00ccb9c0</c>, is set), and whether the pass runs at all (the 2D
/// level loaded, <c>+0x93c</c>, and <c>+0x93d</c> clear).
/// </para>
/// </summary>
public partial class MapLayers : Node3D
{
    /// <summary><c>g_PhyreScene</c>: a pointer to the scene.</summary>
    public const uint PhyreScene = 0x00ccb9d8;
    /// <summary><c>g_PhyreScene3D</c> (a byte): no squeeze while set.</summary>
    public const uint PhyreScene3D = 0x00ccb9c0;
    /// <summary>The maps whose layers blend (<c>preTransparent</c>) instead of being cut out (<c>preTransparentATC</c>).</summary>
    public static readonly int[] BlendedMaps = [0xa0, 0xa1, 0x101, 0x16c];

    private static readonly Regex LayerName = new(@"scene(\d+)layer(\d+)", RegexOptions.CultureInvariant);

    private sealed record Layer(MeshInstance3D Mesh, int Scene, int Id, float Depth);

    private readonly List<Layer> _layers = new();
    private Node3D? _model;
    private string _level = "";
    private bool _loaded;
    // the layers' model, imported on a worker as the maps and characters are (ContentResolver)
    private System.Threading.Tasks.Task<Node3D?>? _loading;
    private string _loadingLevel = "";
    private int _map = -1;

    /// <summary>The level whose layers are loaded ("" none; also set when the level has none).</summary>
    public string Level => _loaded ? _level : "";

    /// <summary>Layers loaded, shown now, the 2D scene shown and the pan (for the checks and the overlay).</summary>
    public (int Layers, int Shown, int Scene, float Pan, bool Live) State { get; private set; }

    /// <summary>Loads <paramref name="level"/>'s layers when their import is ready (once a frame).</summary>
    public void Load(ClassicLibrary? lib, string level, int map)
    {
        if (level != _level)
        {
            _model?.QueueFree();
            _model = null;
            _layers.Clear();
            _level = level;
            _loaded = false;
        }
        // a load finished: for this level it is shown, for an earlier one dropped
        if (_loading is { IsCompleted: true } done)
        {
            _loading = null;
            var node = done.IsCompletedSuccessfully ? done.Result : null;
            if (done.Exception is { } e)
                GD.PrintErr($"ffx: 2D layers {_loadingLevel}: {e.InnerException?.Message ?? e.Message}");
            if (_loadingLevel == level && !_loaded)
            {
                _loaded = true;
                Show(node, level, map);
            }
            else
                node?.QueueFree();
        }
        if (_loaded || _loading != null || lib == null || level.Length == 0)
            return;
        if (lib.Failed("map2d", level))
        {
            _loaded = true; // none (most 3D maps) or not importable
            return;
        }
        if (lib.Path("map2d", level) is not { } path)
            return;
        _loadingLevel = level;
        _loading = System.Threading.Tasks.Task.Run(() => ClassicLibrary.Load(path));
        Pending = _loading;
    }

    private static System.Threading.Tasks.Task? Pending;

    /// <summary>Waits up to <paramref name="ms"/> for a layer import under way (Godot quits).</summary>
    public static void WaitPending(int ms)
    {
        try { Pending?.Wait(ms); } catch (System.AggregateException) { }
    }

    private void Show(Node3D? model, string level, int map)
    {
        _model = model;
        if (_model == null)
            return;
        _model.Name = "Layers2D";
        ClassicMaterials.FixLayers(_model);
        AddChild(_model);
        foreach (var n in _model.FindChildren("*", nameof(MeshInstance3D), true, false))
        {
            if (n is not MeshInstance3D mi || LayerName.Match(mi.Name) is not { Success: true } m)
                continue;
            // the shader places it on the screen whatever the node's transform: never culled
            mi.CustomAabb = new Aabb(new Vector3(-1e6f, -1e6f, -1e6f), new Vector3(2e6f, 2e6f, 2e6f));
            mi.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            var depth = mi.Mesh?.GetAabb().GetCenter().Z ?? 0f;
            _layers.Add(new Layer(mi, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), depth));
        }
        _map = -1;
        SetBlend(map);
        GD.Print($"ffx: map {level}: {_layers.Count} 2D layers");
    }

    /// <summary>
    /// The pass: <c>preTransparentATC</c>, or <c>preTransparent</c> on <see cref="BlendedMaps"/>
    /// (<see cref="ClassicMaterials.LayerPass"/>). Every layer blends, so they draw back to front
    /// (farther first, by render priority) after the opaque world, the depth test keeping
    /// them behind nearer characters.
    /// </summary>
    private void SetBlend(int map)
    {
        if (map == _map)
            return;
        _map = map;
        var atc = Array.IndexOf(BlendedMaps, map) < 0;
        var order = new List<Layer>(_layers);
        order.Sort((a, b) => b.Depth.CompareTo(a.Depth));
        for (int i = 0; i < order.Count; i++)
        {
            var mi = order[i].Mesh;
            for (int s = 0; s < (mi.Mesh?.GetSurfaceCount() ?? 0); s++)
                if ((mi.GetSurfaceOverrideMaterial(s) ?? mi.Mesh!.SurfaceGetMaterial(s)) is ShaderMaterial sm && ClassicMaterials.IsLayer(sm))
                {
                    ClassicMaterials.LayerPass(sm, atc);
                    sm.RenderPriority = Math.Clamp(i - 128, -128, 127);
                }
        }
    }

    /// <summary>Shows the layers the game draws now and sets the window (once a frame).</summary>
    public void Tick(FfxGame? game, int map, float cameraFar)
    {
        RenderingServer.GlobalShaderParameterSet("ffx_layer_far", cameraFar * 0.99f);
        if (_layers.Count == 0)
        {
            State = default;
            return;
        }
        SetBlend(map);
        var live = game != null ? Read(game) : null;
        int scene = live?.Scene ?? 0;
        int shown = 0;
        foreach (var l in _layers)
        {
            bool on = l.Scene == scene && (live == null || (live.Value.Drawn && live.Value.Shown(l.Scene, l.Id)));
            l.Mesh.Visible = on;
            if (on)
                shown++;
        }
        float b = live?.Pan ?? 0.035f;
        var window = live is { Squeezed: false }
            ? new Vector4(0, 1, live.Value.Shift.X, live.Value.Shift.Y)
            : new Vector4(1f / 3f - 2f * b, 4f / 3f, live?.Shift.X ?? 0, live?.Shift.Y ?? 0);
        RenderingServer.GlobalShaderParameterSet("ffx_layer_window", window);
        State = (_layers.Count, shown, scene, b, live != null);
    }

    /// <summary>The scene's 2D-layer state from the game's memory (null when no scene is up).</summary>
    private readonly record struct Live(bool Drawn, int Scene, float Pan, Vector2 Shift, bool Squeezed, Dictionary<(int, int), bool>? Table)
    {
        /// <summary>The layer's shown flag; shown when the table does not list it.</summary>
        public bool Shown(int scene, int id) => Table == null || !Table.TryGetValue((scene, id), out var on) || on;
    }

    private static uint U32(byte[] b, int at) => b.Length >= at + 4 ? BitConverter.ToUInt32(b, at) : 0;

    private static Live? Read(FfxGame game)
    {
        var p = game.Peek(PhyreScene, 4);
        uint scene = U32(p, 0);
        if (scene == 0)
            return null;
        var level = game.Peek(scene + 0x93c, 2);
        var a = game.Peek(scene + 0x1128, 0xb4); // +0x1128 .. +0x11dc
        var w = game.Peek(scene + 0xf9bc, 0x80); // +0xf9bc .. +0xfa3c
        var s3d = game.Peek(PhyreScene3D, 1);
        if (level.Length < 2 || a.Length < 0xb4 || w.Length < 0x80 || s3d.Length < 1)
            return null;
        bool drawn = level[0] != 0 && level[1] == 0;
        int current = (int)U32(a, 0x11d8 - 0x1128);
        Dictionary<(int, int), bool>? table = null;
        uint count = U32(a, 0x1138 - 0x1128), at = U32(a, 0x1140 - 0x1128);
        if (U32(a, 0) != 0 && at != 0 && count > 0 && count < 256)
        {
            var t = game.Peek(at, (int)count * 8);
            table = new();
            for (int i = 0; i + 8 <= t.Length; i += 8)
                table[((sbyte)t[i + 1], BitConverter.ToInt16(t, i + 2))] = t[i] != 0;
        }
        float pan = BitConverter.ToSingle(w, 0);
        bool squeezed = w[0xf9c7 - 0xf9bc] == 0 && s3d[0] == 0;
        var shift = new Vector2(BitConverter.ToSingle(w, 0xfa34 - 0xf9bc), BitConverter.ToSingle(w, 0xfa38 - 0xf9bc));
        return new Live(drawn, current, pan, shift, squeezed, table);
    }
}

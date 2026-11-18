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
/// What the game decides at run time comes from the game-state API's <c>Snapshot.layers</c>
/// (API 10; scene is <c>*g_PhyreScene</c>): the 2D scene shown (scene
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

    /// <summary>The loaded layers' import (null: none, or not loaded yet): a rebuilt room takes its pictures from it.</summary>
    public Node3D? Model => _loaded ? _model : null;

    /// <summary>
    /// Hidden while a rebuilt room draws the map instead (R7b: <see cref="Remaster.RebuiltRoom"/>);
    /// the game's choices (<see cref="State"/>, <see cref="IsShown"/>) are still followed.
    /// </summary>
    public bool Hidden { get; set; }

    /// <summary>Whether the map's pass blends its layers instead of cutting them out (<see cref="BlendedMaps"/>).</summary>
    public bool Atc => Array.IndexOf(BlendedMaps, _map) < 0;

    private GdFfx.Api.Layers? _live;

    /// <summary>Whether the game draws layer <paramref name="id"/> of 2D scene <paramref name="scene"/> this frame (the scene shown, its shown flag, the pass on).</summary>
    public bool IsShown(int scene, int id) =>
        scene == State.Scene && (_live == null || (_live.Drawn && Shown(_live, scene, id)));

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
        _loading = Background.Run(() => GcTrace.Load($"map2d {level}", () => ClassicLibrary.Load(path)));
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

    // made once: a string passed for a StringName makes a new (finalizable) one each call
    private static readonly StringName G_ffx_layer_far = "ffx_layer_far", G_ffx_layer_window = "ffx_layer_window";

    /// <summary>Shows the layers the game draws now and sets the window (once a frame).</summary>
    public void Tick(GdFfx.Api.Snapshot? snapshot, int map, float cameraFar, GameStateView.CameraFrame? camera = null)
    {
        ClassicMaterials.EnsureGlobals();
        RenderingServer.GlobalShaderParameterSet(G_ffx_layer_far, cameraFar * 0.99f);
        if (_layers.Count == 0)
        {
            State = default;
            return;
        }
        SetBlend(map);
        var live = snapshot?.Layers is { Valid: true } l ? l : null;
        _live = live;
        int scene = live?.Scene ?? 0;
        int shown = 0;
        foreach (var layer in _layers)
        {
            bool on = layer.Scene == scene && (live == null || (live.Drawn && Shown(live, layer.Scene, layer.Id)));
            layer.Mesh.Visible = on && !Hidden;
            if (on)
                shown++;
        }
        // the window: the same scene terms as the main camera's adjustment, interpolated with it
        // (GameStateView.Camera), so the characters stay on the picture while it pans; the
        // layer pass leaves the map's own term (map_pan) out
        float b = camera?.Pan ?? live?.Pan ?? 0.035f;
        bool squeezed = camera?.Squeezed ?? live?.Squeezed ?? true;
        var shift = camera?.Shift ?? new Vector2(live?.ShiftX ?? 0, live?.ShiftY ?? 0);
        var window = squeezed
            ? new Vector4(0.33333337f - 2f * b, GameProjection.Squeeze, shift.X, shift.Y)
            : new Vector4(0, 1, shift.X, shift.Y);
        RenderingServer.GlobalShaderParameterSet(G_ffx_layer_window, window);
        State = (_layers.Count, shown, scene, b, live != null);
    }

    /// <summary>A layer's shown flag in <c>PhyreScene_show2DLayer</c>'s table; shown when the table does not list it.</summary>
    private static bool Shown(GdFfx.Api.Layers live, int scene, int id)
    {
        // the last entry for the layer wins, as a dictionary filled in order kept it
        bool on = true;
        foreach (var e in live.Entries)
            if (e.Scene == scene && e.Id == id)
                on = e.Shown;
        return on;
    }
}

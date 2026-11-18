#if TOOLS
using System.Collections.Generic;
using System.Linq;
using GdFfx.Remaster;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>
/// Picking textures in the open map (docs/remaster-materials.md, phase 5, "Several textures"):
/// Alt+click on a surface in the 3D view (a click without a drag: Godot's Alt+drag still moves),
/// or a click while <b>Pick texture</b> is on in the 3D view's toolbar, puts the texture under the
/// cursor into the Materials tab's queue (Shift: every texture of that part's segment). The ray
/// meets the map's own geometry only (the classic map: not the edit scene's helpers, the game's
/// data of R5, additions or hidden parts); the nearest surface wins, so the texture is the one
/// seen. A toast and the tab's status say what was queued.
/// </summary>
public partial class RemasterPlugin
{
    private Button? _pickButton;
    private Vector2? _altDown;

    /// <summary>What the last pick did (the checks).</summary>
    public string? LastPick { get; private set; }

    /// <summary>A pick's result: the texture under the cursor, every texture of its mesh (the segment), the part, the point.</summary>
    public sealed record TexturePick(string Id, List<string> SegmentIds, string Part, Vector3 Point);

    private void EnterPick()
    {
        _pickButton = new Button
        {
            Text = "Pick texture", ToggleMode = true, Flat = true,
            TooltipText = "Click a surface of the open map: its texture goes into the Materials tab's queue (Shift+click: every texture of that part). Alt+click does the same without this on.",
        };
        AddControlToContainer(CustomControlContainer.SpatialEditorMenu, _pickButton);
        // the 3D view's clicks come here whatever is selected (a pick is a ray into the map)
        SetInputEventForwardingAlwaysEnabled();
    }

    private void ExitPick()
    {
        if (_pickButton == null)
            return;
        RemoveControlFromContainer(CustomControlContainer.SpatialEditorMenu, _pickButton);
        _pickButton.QueueFree();
        _pickButton = null;
    }

    /// <summary>Whether Pick texture is on (the checks set it).</summary>
    public bool PickMode
    {
        get => _pickButton?.ButtonPressed == true;
        set => _pickButton?.SetPressedNoSignal(value);
    }

    public override int _Forward3DGuiInput(Camera3D viewportCamera, InputEvent @event)
    {
        if (Current is not { Built: true } || @event is not InputEventMouseButton { ButtonIndex: MouseButton.Left } b)
            return (int)AfterGuiInput.Pass;
        if (PickMode)
        {
            if (b.Pressed)
                LastPick = PickTexture(viewportCamera, b.Position, b.ShiftPressed);
            return (int)AfterGuiInput.Stop;
        }
        if (b.AltPressed && !b.CtrlPressed && !b.MetaPressed)
        {
            // a click, not a drag (Alt+drag moves the selection in Godot's 3D view)
            if (b.Pressed)
                _altDown = b.Position;
            else if (_altDown is { } at && at.DistanceTo(b.Position) < 4)
            {
                _altDown = null;
                LastPick = PickTexture(viewportCamera, b.Position, b.ShiftPressed);
            }
            else
                _altDown = null;
        }
        return (int)AfterGuiInput.Pass;
    }

    /// <summary>Queues the texture under <paramref name="pos"/> of <paramref name="cam"/>'s view (<paramref name="all"/>: its segment's every texture) in the Materials tab; what was done.</summary>
    public string PickTexture(Camera3D cam, Vector2 pos, bool all)
    {
        string msg;
        if (_materials == null || TextureAt(cam, pos) is not { } hit)
            msg = "Materials: no map surface with a texture under the cursor";
        else
        {
            var ids = all ? hit.SegmentIds : [hit.Id];
            int before = _materials.Queue.Count;
            _materials.Add(ids);
            int added = _materials.Queue.Count - before;
            var what = all && ids.Count > 1 ? $"{ids.Count} textures of {hit.Part}" : $"{RemasterTextures.ShortName(hit.Id)} (from {hit.Part})";
            msg = added > 0 ? $"Materials: queued {what}; {_materials.Queue.Count} in the queue" : $"Materials: {what} is in the queue already";
            _materials.Say(msg);
        }
        GD.Print($"ffx remaster: {msg}");
        EditorInterface.Singleton.GetEditorToaster().PushToast(msg, EditorToaster.Severity.Info);
        return msg;
    }

    /// <summary>The map's surface under <paramref name="pos"/> of <paramref name="cam"/>'s view: its texture, its segment's, its part (null: none, or no texture).</summary>
    public TexturePick? TextureAt(Camera3D cam, Vector2 pos)
    {
        if (Current is not { Built: true, Map: { } map })
            return null;
        var origin = cam.ProjectRayOrigin(pos);
        var ray = cam.ProjectRayNormal(pos);
        var look = MapLook.Find(map);
        float best = float.MaxValue;
        (MeshInstance3D Mesh, int Surface, Vector3 Point)? found = null;
        void Walk(Node n)
        {
            if (n.HasMeta(MapEditRoot.EditorNode) || n is Node3D { Visible: false })
                return;
            if (n is MeshInstance3D { Mesh: { } mesh } mi && Slab(origin, ray, mi.GlobalTransform * mesh.GetAabb()) is { } near && near < best)
            {
                var inv = mi.GlobalTransform.AffineInverse();
                var from = inv * origin;
                var dir = (inv.Basis * ray).Normalized();
                for (int s = 0; s < mesh.GetSurfaceCount(); s++)
                {
                    if (mesh is ArrayMesh am && am.SurfaceGetPrimitiveType(s) != Mesh.PrimitiveType.Triangles)
                        continue;
                    var (v, idx) = Arrays(mesh, s);
                    int count = idx?.Length ?? v.Length;
                    for (int i = 0; i + 2 < count; i += 3)
                    {
                        int a = idx?[i] ?? i, bb = idx?[i + 1] ?? i + 1, c = idx?[i + 2] ?? i + 2;
                        if (Hit(from, dir, v[a], v[bb], v[c]) is not { } t)
                            continue;
                        var p = mi.GlobalTransform * (from + dir * t);
                        float d = p.DistanceTo(origin);
                        if (d < best)
                            (best, found) = (d, (mi, s, p));
                    }
                }
            }
            for (int i = 0, k = n.GetChildCount(); i < k; i++)
                Walk(n.GetChild(i));
        }
        Walk(map);
        if (found is not { } f)
            return null;
        string? Id(MeshInstance3D mi, int s)
        {
            var active = mi.GetActiveMaterial(s);
            var classic = look?.ClassicOf(mi, s) ?? active;
            var id = RemasterTextures.ColourId(classic) ?? (active != null ? PartIds.GetString(active, RemasterLook.LitTextureMeta) : null);
            return id is { Length: > 0 } ? id.ToLowerInvariant() : null;
        }
        if (Id(f.Mesh, f.Surface) is not { } hitId)
            return null;
        var segment = new List<string>();
        for (int s = 0; s < f.Mesh.Mesh!.GetSurfaceCount(); s++)
            if (Id(f.Mesh, s) is { } sid && !segment.Contains(sid))
                segment.Add(sid);
        // the part: the nearest node up the tree with an ID
        Node? n = f.Mesh;
        while (n != null && PartIds.Of(n) == null)
            n = n.GetParent();
        var part = n != null && PartIds.Of(n) is { } pid ? pid[(pid.IndexOf('#') + 1)..] : f.Mesh.Name.ToString();
        return new TexturePick(hitId, segment, part, f.Point);
    }

    private readonly Dictionary<(Mesh, int), (Vector3[] V, int[]? I)> _rayArrays = new();

    private (Vector3[] V, int[]? I) Arrays(Mesh mesh, int s)
    {
        if (_rayArrays.TryGetValue((mesh, s), out var a))
            return a;
        if (_rayArrays.Count > 4096)
            _rayArrays.Clear();
        var arrays = mesh.SurfaceGetArrays(s);
        var idx = arrays[(int)Mesh.ArrayType.Index];
        return _rayArrays[(mesh, s)] = (arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array(), idx.VariantType == Variant.Type.Nil ? null : idx.AsInt32Array());
    }

    /// <summary>Where the ray enters <paramref name="box"/> (its distance; 0 inside), or null.</summary>
    private static float? Slab(Vector3 o, Vector3 d, Aabb box)
    {
        float t0 = 0, t1 = float.MaxValue;
        for (int k = 0; k < 3; k++)
        {
            float lo = box.Position[k], hi = box.End[k];
            if (Mathf.Abs(d[k]) < 1e-9f)
            {
                if (o[k] < lo || o[k] > hi)
                    return null;
                continue;
            }
            float a = (lo - o[k]) / d[k], b = (hi - o[k]) / d[k];
            if (a > b)
                (a, b) = (b, a);
            t0 = Mathf.Max(t0, a);
            t1 = Mathf.Min(t1, b);
            if (t0 > t1)
                return null;
        }
        return t0;
    }

    /// <summary>Möller-Trumbore: the ray's distance to the triangle (both sides), or null.</summary>
    private static float? Hit(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c)
    {
        var e1 = b - a;
        var e2 = c - a;
        var p = d.Cross(e2);
        float det = e1.Dot(p);
        if (Mathf.Abs(det) < 1e-12f)
            return null;
        float f = 1 / det;
        var s = o - a;
        float u = f * s.Dot(p);
        if (u < 0 || u > 1)
            return null;
        var q = s.Cross(e1);
        float v = f * d.Dot(q);
        if (v < 0 || u + v > 1)
            return null;
        float t = f * e2.Dot(q);
        return t > 1e-5f ? t : null;
    }
}
#endif

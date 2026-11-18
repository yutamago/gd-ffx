#if TOOLS
using System.Linq;
using GdFfx.Remaster;
using GdFfx.Remaster.Texturing;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>
/// Colour picking (docs/remaster-materials.md, phase 5): every colour setting (the height's and
/// roughness's samples, the metal's and the mask steps' colours, the tints) takes its colour from
/// a click or a drag in the 2D map or on the object in the 3D preview (the In map view too): the
/// cursor a cross with the pick's disc and the colour it would take, the colour averaged over the
/// pick radius, live while dragging (one undo step at the release); a sample's mask shown while
/// picking it (the isolate view).
/// </summary>
public partial class MaterialsTab
{
    /// <summary>The pick's radius in map pixels (0: one pixel).</summary>
    public int PickRadius
    {
        get => _pickRadius;
        set
        {
            _pickRadius = Mathf.Clamp(value, 0, 16);
            _map2d.PickRadius = _pickRadius;
            _radiusBox?.SetValueNoSignal(_pickRadius);
        }
    }

    private int _pickRadius = 1;
    private SpinBox? _radiusBox;

    /// <summary>The image picks read from while a pick goes on (read once at its start).</summary>
    private RgbaImage? _pickImage;

    /// <summary>The colour setting being picked (null: none).</summary>
    public string? Picking => _pickFor;

    /// <summary>The pick radius's box, beside the 2D map's options.</summary>
    private Control BuildPickRadius()
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = "Pick radius", TooltipText = "A pick takes the mean colour of a disc this many map pixels wide around the cursor (0: one pixel)" });
        _radiusBox = new SpinBox { MinValue = 0, MaxValue = 16, Step = 1, Value = _pickRadius, TooltipText = "Map pixels (0: one pixel)" };
        _radiusBox.ValueChanged += v => PickRadius = (int)v;
        row.AddChild(_radiusBox);
        return row;
    }

    /// <summary>Starts picking <paramref name="prop"/>: the next click or drag in the 2D map or the preview takes its colour.</summary>
    public void StartPick(string prop)
    {
        _pickFor = prop;
        _map2d.Picking = true;
        _preview.Picking = true;
        _map2d.Sampler = uv => Sample(PickSource(prop), uv);
        // the eyedropper as the cross cursor while picking
        var icon = EditorInterface.Singleton.GetEditorTheme().GetIcon("ColorPick", "EditorIcons");
        if (icon != null)
            Input.SetCustomMouseCursor(icon, Input.CursorShape.Cross, new Vector2(1, icon.GetHeight() - 1));
        Say($"{LabelOf(prop)}: click or drag in the 2D map or on the object (radius {_pickRadius} px)");
    }

    /// <summary>Stops picking (the Pick buttons up, the cursor back).</summary>
    public void EndPick()
    {
        _pickFor = null;
        _pickImage = null;
        _map2d.Picking = false;
        _preview.Picking = false;
        Input.SetCustomMouseCursor(null, Input.CursorShape.Cross);
        foreach (var b in _settingsBox.FindChildren("*", "Button", true, false).OfType<Button>().Where(b => b.ToggleMode && b.Text == "Pick"))
            b.SetPressedNoSignal(false);
    }

    /// <summary>A pick in the 2D map or the preview: pressed (0), dragged (1), released (2).</summary>
    private void PickDragged(Vector2 uv, int phase)
    {
        if (_pickFor is not { } prop)
            return;
        // the colour of a region by colour (R2b phase 6)
        if (prop == RegionPick)
        {
            RegionPicked(uv, phase);
            return;
        }
        if (phase == 0)
        {
            _pickImage = PickSource(prop);
            BeginDrag(prop);
            // a sample's mask shown while it is picked
            if (IsolateOf(prop) is { } iso && iso.Step == Step && _isolate != iso)
            {
                SetIsolate(iso);
                RebuildSettings();
            }
        }
        if (Sample(_pickImage, uv) is { } c)
        {
            SetLive(prop, c);
            Changed([prop]);
        }
        if (phase != 2)
            return;
        EndDrag(prop);
        EndPick();
        Say($"{LabelOf(prop)}: picked #{Recipe.Get(prop).AsColor().ToHtml(false)} (radius {_pickRadius} px)");
    }

    /// <summary>The image a colour setting is picked from: the source for the height's samples when the height comes from it, else the albedo.</summary>
    private RgbaImage? PickSource(string prop)
    {
        if (prop == RegionPick)
            return _original;
        var from = prop.StartsWith("Height", System.StringComparison.Ordinal) && Recipe.HeightFrom == HeightInput.Source ? MaterialStep.Source : MaterialStep.Albedo;
        return Pixels(from);
    }

    /// <summary>The mean colour of <paramref name="img"/> over the pick's disc at <paramref name="uv"/> (wrapping when the texture tiles), or null.</summary>
    public Color? Sample(RgbaImage? img, Vector2 uv)
    {
        if (img == null)
            return null;
        int cx = Mathf.Clamp((int)(uv.X * img.Width), 0, img.Width - 1), cy = Mathf.Clamp((int)(uv.Y * img.Height), 0, img.Height - 1);
        int r = _pickRadius;
        double sr = 0, sg = 0, sb = 0;
        int n = 0;
        for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
            {
                if (dx * dx + dy * dy > r * r + r)
                    continue;
                int x = cx + dx, y = cy + dy;
                if (Recipe.Wrap)
                    (x, y) = (Mathf.PosMod(x, img.Width), Mathf.PosMod(y, img.Height));
                else if (x < 0 || y < 0 || x >= img.Width || y >= img.Height)
                    continue;
                int i = (y * img.Width + x) * 4;
                sr += img.Rgba[i];
                sg += img.Rgba[i + 1];
                sb += img.Rgba[i + 2];
                n++;
            }
        return n == 0 ? null : new Color((float)(sr / n / 255), (float)(sg / n / 255), (float)(sb / n / 255), 1);
    }

    /// <summary>The isolate view of a colour setting (its step and sample), or null (a tint has none).</summary>
    private static (MaterialStep Step, int Sample)? IsolateOf(string prop)
    {
        if (prop.StartsWith("HeightSample", System.StringComparison.Ordinal) && prop[^1] is >= '1' and <= '3')
            return (MaterialStep.Height, prop[^1] - '0');
        if (prop.StartsWith("RoughnessSample", System.StringComparison.Ordinal) && prop[^1] is >= '1' and <= '3')
            return (MaterialStep.Roughness, prop[^1] - '0');
        if (prop == nameof(PbrRecipe.MetallicColour))
            return (MaterialStep.Metallic, 1);
        foreach (var s in MaterialPipeline.MaskSteps)
            if (prop == $"{s}Colour")
                return (s, 1);
        return null;
    }

    /// <summary>
    /// The colour of the step's input at <paramref name="uv"/> (averaged over the pick radius) set
    /// as <paramref name="prop"/> (one undo step); the colour, or null.
    /// </summary>
    public Color? PickColour(string prop, Vector2 uv)
    {
        if (Sample(PickSource(prop), uv) is not { } c)
            return null;
        SetSetting(prop, c);
        return c;
    }
}
#endif

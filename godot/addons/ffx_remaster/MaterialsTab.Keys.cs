#if TOOLS
using System.Collections.Generic;
using System.Linq;
using GdFfx.Remaster;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>
/// The Materials tab's keyboard (docs/remaster-materials.md, phase 5, "Keyboard"): Godot editor
/// shortcuts of their own ("FFX Materials" in Editor Settings, Shortcuts: rebindable), answered
/// only while the tab is shown and the keyboard focus is inside it (so the 3D view, the docks and
/// the script editor keep theirs). They are read in <see cref="_Input"/>, before the editor's
/// menus, so Ctrl+S saves the texture, not the scene, while the tab has the focus.
/// </summary>
public partial class MaterialsTab
{
    /// <summary>A shortcut: its path in the editor settings, what it does, its default keys.</summary>
    public sealed record KeyBinding(string Path, string Label, InputEventKey[] Keys);

    private static InputEventKey K(Key key, bool ctrl = false, bool shift = false) =>
        new() { Keycode = key, CtrlPressed = ctrl, ShiftPressed = shift, CommandOrControlAutoremap = ctrl };

    /// <summary>The section of the editor's shortcut settings the tab's keys are listed in.</summary>
    public const string ShortcutSection = "FFX Materials";

    /// <summary>Every shortcut of the tab (the help overlay lists them with their current keys).</summary>
    public static readonly KeyBinding[] Bindings = BuildBindings();

    private static KeyBinding[] BuildBindings()
    {
        var list = new List<KeyBinding>();
        for (int i = 0; i <= 9; i++)
            list.Add(new($"{ShortcutSection}/Step {i}", $"Go to the step numbered {i} in the strip (0 Source ... 7 Emission; 8, 9: the first added maps)", [K(Key.Key0 + i)]));
        list.Add(new($"{ShortcutSection}/Previous step", "The step before in the strip", [K(Key.Bracketleft)]));
        list.Add(new($"{ShortcutSection}/Next step", "The step after in the strip (also the added maps)", [K(Key.Bracketright)]));
        list.Add(new($"{ShortcutSection}/Previous texture", "The texture before in the queue (the settings kept)", [K(Key.Up), K(Key.Left)]));
        list.Add(new($"{ShortcutSection}/Next texture", "The texture after in the queue (the settings kept)", [K(Key.Down), K(Key.Right)]));
        list.Add(new($"{ShortcutSection}/Compare", "The compare on or off (the wipe where it was, or all material)", [K(Key.Tab)]));
        list.Add(new($"{ShortcutSection}/Swap sides", "The whole preview the compare's side, then the material's (A/B)", [K(Key.Tab, shift: true)]));
        list.Add(new($"{ShortcutSection}/Save texture", "Save the current texture's material (not the scene)", [K(Key.S, ctrl: true)]));
        list.Add(new($"{ShortcutSection}/Reset setting", "The setting under the mouse back to its default (one undo step)", [K(Key.R)]));
        list.Add(new($"{ShortcutSection}/Frame", "Frame the object (the mouse over the 2D map: the whole map)", [K(Key.F)]));
        list.Add(new($"{ShortcutSection}/In map", "The In map preview on or off (the map that uses the texture)", [K(Key.M)]));
        list.Add(new($"{ShortcutSection}/Shortcuts", "This list", [K(Key.Slash, shift: true)]));
        return list.ToArray();
    }

    /// <summary>Registers the tab's shortcuts with the editor (each once: a binding changed in Editor Settings is kept).</summary>
    public static void RegisterShortcuts()
    {
        var es = EditorInterface.Singleton.GetEditorSettings();
        foreach (var b in Bindings)
            if (!es.HasShortcut(b.Path))
            {
                var sc = new Shortcut();
                sc.Events = new Godot.Collections.Array(b.Keys.Select(k => (Variant)k.Duplicate()));
                es.AddShortcut(b.Path, sc);
            }
    }

    /// <summary>A shortcut's keys now, as text (the help).</summary>
    public static string KeysOf(KeyBinding b)
    {
        var es = EditorInterface.Singleton.GetEditorSettings();
        var sc = es.HasShortcut(b.Path) ? es.GetShortcut(b.Path) : null;
        var events = sc?.Events.Select(e => e.As<InputEvent>()).Where(e => e != null).ToList() ?? b.Keys.Cast<InputEvent>().ToList();
        return events.Count == 0 ? "(none)" : string.Join(", ", events.Select(e => e.AsText()));
    }

    private static bool Is(string name, InputEvent e)
    {
        var path = $"{ShortcutSection}/{name}";
        var es = EditorInterface.Singleton.GetEditorSettings();
        if (es.HasShortcut(path))
            return es.IsShortcut(path, e);
        return Bindings.First(b => b.Path == path).Keys.Any(k => k.IsMatch(e, true));
    }

    /// <summary>The mouse's last place over the editor (the setting under it for Reset).</summary>
    private Vector2 _mouse = new(-1, -1);

    /// <summary>Whether the tab answers keys: shown, and the keyboard focus inside it.</summary>
    public bool HasKeyFocus
    {
        get
        {
            if (!IsVisibleInTree())
                return false;
            var focus = GetViewport().GuiGetFocusOwner();
            return focus != null && IsAncestorOf(focus);
        }
    }

    /// <summary>What the last shortcut did (the checks), or null.</summary>
    public string? LastShortcut { get; private set; }

    public override void _Input(InputEvent e)
    {
        if (e is InputEventMouseMotion mm)
        {
            _mouse = mm.Position;
            return;
        }
        if (e is not InputEventKey { Pressed: true } k || !HasKeyFocus)
            return;
        if (Shortcut(k))
            GetViewport().SetInputAsHandled();
    }

    /// <summary>Runs the shortcut <paramref name="k"/> matches (focus checked by the caller); whether one did.</summary>
    public bool Shortcut(InputEventKey k)
    {
        var focus = GetViewport().GuiGetFocusOwner();
        // typing in a field: its keys are the field's (Ctrl+S still saves the texture)
        bool typing = focus is LineEdit or TextEdit;
        // a slider's and a list's own arrows stay theirs
        bool arrows = focus is Range or ItemList or OptionButton;
        string? did = null;
        if (Is("Save texture", k))
            did = "save: " + (Save() ?? "saving...");
        else if (typing)
            return false;
        else if (Is("Shortcuts", k) || k.Unicode == '?')
            did = ShowShortcuts();
        else if (k.Keycode == Key.Escape && _help is { Visible: true })
        {
            _help.Hide();
            did = "shortcuts hidden";
        }
        else if (Is("Compare", k))
            did = ToggleCompare();
        else if (Is("Swap sides", k))
            did = SwapSides();
        else if (Is("Reset setting", k))
            did = ResetUnderMouse();
        else if (Is("Frame", k))
        {
            if (_map2d.GetGlobalRect().HasPoint(_mouse))
                _map2d.Reset();
            else
                _preview.Frame();
            did = "frame";
        }
        else if (Is("In map", k))
            did = ToggleInMap();
        else if (Is("Previous step", k))
            did = StepBy(-1);
        else if (Is("Next step", k))
            did = StepBy(1);
        else if (!arrows && Is("Previous texture", k))
        {
            Next(-1);
            did = $"texture {Current}";
        }
        else if (!arrows && Is("Next texture", k))
        {
            Next(1);
            did = $"texture {Current}";
        }
        else
            for (int i = 0; i <= 9 && did == null; i++)
                if (Is($"Step {i}", k))
                    did = GoToStrip(i);
        if (did == null)
            return false;
        LastShortcut = did;
        return true;
    }

    /// <summary>The steps in the strip now (the core ones, then the added maps), in order.</summary>
    public List<MaterialStep> StripSteps() => MaterialPipeline.Steps.Where(s => MaterialPipeline.On(Recipe, s)).ToList();

    private string GoToStrip(int i)
    {
        var steps = StripSteps();
        if (i >= steps.Count)
            return $"no step {i}";
        SelectStep(steps[i]);
        return $"step {Label(steps[i])}";
    }

    private string StepBy(int by)
    {
        var steps = StripSteps();
        int i = Mathf.Max(0, steps.IndexOf(Step));
        SelectStep(steps[((i + by) % steps.Count + steps.Count) % steps.Count]);
        return $"step {Label(Step)}";
    }

    private float _wipeKept = 0.5f;

    /// <summary>The compare on (the wipe where it was) or off (all material).</summary>
    public string ToggleCompare()
    {
        if (_wipe.Value > 0.001 && _wipe.Value < 0.999)
        {
            _wipeKept = (float)_wipe.Value;
            _wipe.Value = 0;
            return "compare off";
        }
        _wipe.Value = _wipeKept;
        return "compare on";
    }

    /// <summary>The whole preview the compare's side, then the material's (A/B).</summary>
    public string SwapSides()
    {
        if (_wipe.Value > 0.001 && _wipe.Value < 0.999)
            _wipeKept = (float)_wipe.Value;
        _wipe.Value = _wipe.Value >= 0.999 ? 0 : 1;
        return _wipe.Value >= 0.999 ? "the compare's side" : "the material's side";
    }

    /// <summary>The wipe as it is (0: all material, 1: all the compare).</summary>
    public float WipeValue => (float)_wipe.Value;

    /// <summary>The setting whose row is under the mouse back to its default (one undo step).</summary>
    private string ResetUnderMouse()
    {
        foreach (var (prop, ctl) in _controls)
            if (ctl.GetParent() is Control row && row.IsVisibleInTree() && row.GetGlobalRect().HasPoint(_mouse))
            {
                ResetSetting(prop);
                return $"reset {prop}";
            }
        return "no setting under the mouse";
    }

    /// <summary>The setting row of <paramref name="prop"/> (the checks: where to put the mouse), or null.</summary>
    public Control? RowOf(string prop) => _controls.TryGetValue(prop, out var c) ? c.GetParent() as Control : null;

    private PanelContainer? _help;
    private RichTextLabel? _helpText;

    /// <summary>The shortcuts with their keys now, as an overlay on the preview (? again, Esc or its × closes it; Editor Settings, Shortcuts, "FFX Materials" to change them). Shown again: hidden.</summary>
    public string ShowShortcuts()
    {
        if (_help is { Visible: true })
        {
            _help.Hide();
            return "shortcuts hidden";
        }
        if (_help == null)
        {
            _help = new PanelContainer { MouseFilter = MouseFilterEnum.Stop };
            _help.SetAnchorsPreset(LayoutPreset.FullRect);
            (_help.OffsetLeft, _help.OffsetTop, _help.OffsetRight, _help.OffsetBottom) = (24, 24, -24, -24);
            var col = new VBoxContainer();
            _help.AddChild(col);
            var head = new HBoxContainer();
            col.AddChild(head);
            head.AddChild(new Label { Text = "Materials: shortcuts", ThemeTypeVariation = "HeaderSmall", SizeFlagsHorizontal = SizeFlags.ExpandFill });
            var close = new Button { Text = "×", Flat = true, TooltipText = "Close (? or Esc)", FocusMode = FocusModeEnum.None };
            close.Pressed += () => _help.Hide();
            head.AddChild(close);
            _helpText = new RichTextLabel { BbcodeEnabled = true, SizeFlagsVertical = SizeFlags.ExpandFill, ScrollActive = true };
            col.AddChild(_helpText);
            _preview.AddChild(_help);
        }
        var sb = new System.Text.StringBuilder("Keys while the Materials tab has the focus (Editor Settings > Shortcuts > " + ShortcutSection + " to change them)\n\n");
        foreach (var b in Bindings)
        {
            var name = b.Path[(ShortcutSection.Length + 1)..];
            if (name.StartsWith("Step ", System.StringComparison.Ordinal) && name != "Step 0")
                continue;
            if (name == "Step 0")
                sb.Append("[b]0 ... 9[/b]   Go to the step numbered so in the strip (0 Source ... 7 Emission; 8, 9: the first added maps)\n");
            else
                sb.Append($"[b]{KeysOf(b)}[/b]   {b.Label}\n");
        }
        sb.Append("\n[b]Mouse[/b]: drag to orbit, wheel to zoom, middle drag to pan, Shift+drag turns the light, Alt+drag the environment; drag the yellow line to move the wipe. "
            + "Pick (beside a colour): click or drag in the 2D map or on the object, the colour averaged over the pick radius. "
            + "In the 3D view: Alt+click a surface (or Pick texture) to queue its texture here.");
        _helpText!.Text = sb.ToString();
        _help.Show();
        return "shortcuts shown";
    }

    /// <summary>Whether the shortcut list is shown (the checks).</summary>
    public bool ShortcutsShown => _help?.Visible == true;

    /// <summary>Hides the shortcut list (the checks).</summary>
    public void HideShortcuts() => _help?.Hide();
}
#endif

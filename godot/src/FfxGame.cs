using Godot;
using Godot.Collections;

namespace GdFfx;

/// <summary>
/// The game, as the scenes see it: a typed wrapper over the GDExtension's
/// <c>FfxGame</c> node (rust/ffx-gd). ffx-rust runs the game on its own
/// thread at 30 updates a second; this side passes it Godot's window
/// events and input and reads its state.
/// </summary>
public partial class FfxGame : RefCounted
{
    /// <summary>The extension's node (in the scene tree: it polls the pads each frame).</summary>
    public Node Node { get; }

    public FfxGame()
    {
        if (!ClassDB.ClassExists("FfxGame"))
        {
            throw new System.InvalidOperationException(
                "the ffx-gd GDExtension is not loaded (build rust/ffx-gd first: docs/building.md)");
        }
        Node = (Node)ClassDB.Instantiate("FfxGame");
        Node.Name = "FfxGame";
    }

    /// <summary>Boots the game from the player's folder; false when it could not start.</summary>
    public bool Start(string gameDir, string documentsDir) =>
        Node.Call("start", gameDir, documentsDir).AsBool();

    /// <summary>Ends the game, waiting up to <paramref name="timeout"/> seconds.</summary>
    public void Stop(double timeout = 6.0) => Node.Call("stop", timeout);

    public bool IsRunning => Node.Call("is_running").AsBool();

    public string LastError => Node.Call("last_error").AsString();

    /// <summary>The state now (G0: updates, game time, map, story progress, ...).</summary>
    public GameState State => new(Node.Call("state").AsGodotDictionary());

    /// <summary>The newest snapshot's update number (-1 none yet).</summary>
    public long SnapshotUpdate => Node.Call("snapshot_update").AsInt64();

    /// <summary>
    /// The newest two snapshots, encoded (<c>previous</c>, <c>current</c>), with
    /// <c>update</c>, <c>age</c> and <c>interval</c> (see <see cref="GameStateView"/>).
    /// </summary>
    public Dictionary Snapshots => Node.Call("snapshots").AsGodotDictionary();

    /// <summary>Opens the player's archive for the classic importer, with <paramref name="cacheDir"/> as its output.</summary>
    public bool ClassicOpen(string gameDir, string cacheDir) =>
        Node.Call("classic_open", gameDir, cacheDir).AsBool();

    /// <summary>Imports a map (<c>map</c>, by level name) or character (<c>chr</c>, by model name) in the background.</summary>
    public bool ClassicRequest(string kind, string name) =>
        Node.Call("classic_request", kind, name).AsBool();

    /// <summary>The imports finished since the last call: kind, name, path, error.</summary>
    public Godot.Collections.Array<Dictionary> ClassicFinished() =>
        Node.Call("classic_finished").AsGodotArray<Dictionary>();

    /// <summary>The game's encoded <paramref name="text"/> decoded in <paramref name="language"/>'s character set (<c>us</c>, <c>jp</c>...).</summary>
    public string DecodeText(byte[] text, string language) =>
        Node.Call("decode_text", text, language).AsString();

    /// <summary>A battle command's, item's or monster ability's name (the battle menu's ids) in <paramref name="language"/>.</summary>
    public string KernelName(int id, string language) => Node.Call("kernel_name", id, language).AsString();

    /// <summary>A developer's look at <paramref name="len"/> bytes of game memory (read only; empty while no game runs).</summary>
    public byte[] Peek(long address, int len) => Node.Call("peek", address, len).AsByteArray();

    /// <summary>A material texture's face (0, or 0..6 for a cube map) as PNG bytes; empty without the archive.</summary>
    public byte[] ClassicTexture(string path, int face) => Node.Call("classic_texture", path, face).AsByteArray();

    /// <summary>Whether the material texture at <paramref name="path"/> is a cube map.</summary>
    public bool ClassicTextureIsCube(string path) => Node.Call("classic_texture_is_cube", path).AsBool();

    /// <summary>Sphere grid node kind <paramref name="kind"/>'s name, decoded; empty without the archive.</summary>
    public string GridNodeName(int kind, string language) => Node.Call("grid_node_name", kind, language).AsString();

    /// <summary>The save list's place name <paramref name="index"/> (a save header's location), decoded; empty without the archive.</summary>
    public string PlaceName(int index, string language) => Node.Call("place_name", index, language).AsString();

    /// <summary>Asks the game to start battle <paramref name="field"/>/<paramref name="group"/>/<paramref name="formation"/> as an encounter would.</summary>
    public bool RequestBattle(int field, int group, int formation) =>
        Node.Call("request_battle", field, group, formation).AsBool();

    /// <summary>Asks the game to load developer save <paramref name="n"/> (1..110) at its next update.</summary>
    public bool RequestDevSave(int n) => Node.Call("request_devsave", n).AsBool();

    /// <summary>The requests' replies since the last call.</summary>
    public string[] RequestResults() => Node.Call("request_results").AsStringArray();

    /// <summary>A key event; true when the game has a code for the key.</summary>
    public bool KeyInput(InputEventKey e) => Node.Call("key_input", e).AsBool();

    /// <summary>A key by its DirectInput scan code.</summary>
    public void ScanInput(int scan, bool down) => Node.Call("scan_input", scan, down);

    public void MouseInput(InputEvent e) => Node.Call("mouse_input", e);

    public void MouseExited() => Node.Call("mouse_exited");

    public void SetWindow(Vector2I size, Vector2I screen, Vector2I origin) =>
        Node.Call("set_window", size, screen, origin);

    public void SetFocus(bool focused) => Node.Call("set_focus", focused);
}

/// <summary>One reading of the game's state (G0's small API; G1 brings the typed one).</summary>
public readonly struct GameState
{
    private readonly Dictionary _d;

    public GameState(Dictionary d) => _d = d;

    private Variant Get(string key) => _d.TryGetValue(key, out var v) ? v : default;

    public bool Running => Get("running").AsBool();
    public bool Booted => Get("booted").AsBool();
    public string Outcome => Get("outcome").AsString();
    public string Stopped => Get("stopped").AsString();
    public double Seconds => Get("seconds").AsDouble();
    public long Updates => Get("updates").AsInt64();
    public long Flips => Get("flips").AsInt64();
    public double GameTime => Get("game_time").AsDouble();
    public int Map => Get("map").AsInt32();
    public int PreviousMap => Get("previous_map").AsInt32();
    public int Entrance => Get("entrance").AsInt32();
    public int Story => Get("story").AsInt32();
    public long EventFrame => Get("event_frame").AsInt64();
    public bool Focused => Get("focused").AsBool();
    public int[] Keys => Get("keys").AsInt32Array();
    public string[] MessageBoxes => Get("message_boxes").AsStringArray();
    /// <summary>The game's sound plays through Godot (started, and a player pulls).</summary>
    public bool AudioRunning => Get("audio_running").AsBool();
    /// <summary>Frames of the game's mix queued for Godot's player.</summary>
    public long AudioQueued => Get("audio_queued").AsInt64();
    /// <summary>The queue's mean over time (the mean delay of a frame, in frames).</summary>
    public long AudioMeanQueued => Get("audio_mean_queued").AsInt64();
    /// <summary>Times Godot's mix asked for more than was queued (the game's mixer fell behind): gaps heard.</summary>
    public long AudioUnderruns => Get("audio_underruns").AsInt64();
    /// <summary>The queue now, in milliseconds.</summary>
    public double AudioQueuedMs => Get("audio_queued_ms").AsDouble();
    /// <summary>The queue's mean over time, in milliseconds: all of gd-ffx's buffering.</summary>
    public double AudioMeanQueuedMs => Get("audio_mean_queued_ms").AsDouble();
    /// <summary>Godot's output latency (its driver's buffer), in milliseconds.</summary>
    public double AudioOutputLatencyMs => Get("audio_output_latency_ms").AsDouble();
    /// <summary>The mixer to the speakers: the mean queue and the driver, in milliseconds.</summary>
    public double AudioLatencyMs => Get("audio_latency_ms").AsDouble();
    /// <summary>Frames handed to Godot's player so far.</summary>
    public long AudioPulled => Get("audio_pulled").AsInt64();
}

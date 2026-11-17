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
    /// <summary>
    /// The extension's method names, made once: a string passed for a <see cref="StringName"/>
    /// makes a new one each call, a finalizable object that outlives its first collection (several
    /// calls a frame; docs/running.md, "Benchmark").
    /// </summary>
    private static class M
    {
        public static readonly StringName cloth_address = "cloth_address";
        public static readonly StringName cloth_held = "cloth_held";
        public static readonly StringName classic_effect_switches = "classic_effect_switches";
        public static readonly StringName classic_finished = "classic_finished";
        public static readonly StringName classic_open = "classic_open";
        public static readonly StringName classic_request = "classic_request";
        public static readonly StringName classic_texture = "classic_texture";
        public static readonly StringName classic_texture_is_cube = "classic_texture_is_cube";
        public static readonly StringName decode_text = "decode_text";
        public static readonly StringName effect_draws = "effect_draws";
        public static readonly StringName effect_draws_address = "effect_draws_address";
        public static readonly StringName effect_draws_held = "effect_draws_held";
        public static readonly StringName gear_record = "gear_record";
        public static readonly StringName grid_node_name = "grid_node_name";
        public static readonly StringName is_running = "is_running";
        public static readonly StringName kernel_name = "kernel_name";
        public static readonly StringName key_input = "key_input";
        public static readonly StringName last_error = "last_error";
        public static readonly StringName loc_text = "loc_text";
        public static readonly StringName loc_text_utf8 = "loc_text_utf8";
        public static readonly StringName map_exits = "map_exits";
        public static readonly StringName mouse_exited = "mouse_exited";
        public static readonly StringName mouse_input = "mouse_input";
        public static readonly StringName peek = "peek";
        public static readonly StringName place_name = "place_name";
        public static readonly StringName request_battle = "request_battle";
        public static readonly StringName request_devsave = "request_devsave";
        public static readonly StringName request_results = "request_results";
        public static readonly StringName runtime_texture = "runtime_texture";
        public static readonly StringName scan_input = "scan_input";
        public static readonly StringName set_focus = "set_focus";
        public static readonly StringName set_speed = "set_speed";
        public static readonly StringName speed = "speed";
        public static readonly StringName set_window = "set_window";
        public static readonly StringName snapshot_update = "snapshot_update";
        public static readonly StringName snapshots = "snapshots";
        public static readonly StringName snapshots_held = "snapshots_held";
        public static readonly StringName start = "start";
        public static readonly StringName weapon_name = "weapon_name";
        public static readonly StringName state = "state";
        public static readonly StringName stop = "stop";
        public static readonly StringName texture_now = "texture_now";
        public static readonly StringName texture_request = "texture_request";
        public static readonly StringName textures_finished = "textures_finished";
    }

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
        Node.Call(M.start, gameDir, documentsDir).AsBool();

    /// <summary>Ends the game, waiting up to <paramref name="timeout"/> seconds.</summary>
    public void Stop(double timeout = 6.0) => Node.Call(M.stop, timeout);

    public bool IsRunning => Node.Call(M.is_running).AsBool();

    public string LastError => Node.Call(M.last_error).AsString();

    /// <summary>The state now (G0: updates, game time, map, story progress, ...).</summary>
    public GameState State => new(Node.Call(M.state).AsGodotDictionary());

    /// <summary>The newest snapshot's update number (-1 none yet).</summary>
    public long SnapshotUpdate => Node.Call(M.snapshot_update).AsInt64();

    /// <summary>
    /// The newest two snapshots, encoded (<c>previous</c>, <c>current</c>), with
    /// <c>update</c>, <c>age</c> and <c>interval</c> (see <see cref="GameStateView"/>).
    /// </summary>
    public Dictionary Snapshots => Node.Call(M.snapshots).AsGodotDictionary();

    /// <summary>Opens the player's archive for the classic importer, with <paramref name="cacheDir"/> as its output.</summary>
    public bool ClassicOpen(string gameDir, string cacheDir) =>
        Node.Call(M.classic_open, gameDir, cacheDir).AsBool();

    /// <summary>Imports a map (<c>map</c>, by level name), a map's 2D layers (<c>map2d</c>) or a character (<c>chr</c>, by model name) in the background.</summary>
    public bool ClassicRequest(string kind, string name) =>
        Node.Call(M.classic_request, kind, name).AsBool();

    /// <summary>The imports finished since the last call: kind, name, path, error.</summary>
    public Godot.Collections.Array<Dictionary> ClassicFinished() =>
        Node.Call(M.classic_finished).AsGodotArray<Dictionary>();

    /// <summary>The game's encoded <paramref name="text"/> decoded in <paramref name="language"/>'s character set (<c>us</c>, <c>jp</c>...).</summary>
    public string DecodeText(byte[] text, string language) =>
        Node.Call(M.decode_text, text, language).AsString();

    /// <summary>A battle command's, item's or monster ability's name (the battle menu's ids) in <paramref name="language"/>.</summary>
    public string KernelName(int id, string language) => Node.Call(M.kernel_name, id, language).AsString();

    /// <summary>
    /// What names gear <paramref name="id"/> (a battle spoil's or inventory slot's id,
    /// <c>MsGetSaveWeapon</c>'s), read from the game now: its <c>w_name</c> record, its character
    /// <c>&lt;&lt; 16</c> and the config's hiragana <c>&lt;&lt; 24</c>; -1 for none or a record not in use.
    /// </summary>
    public long GearRecord(int id) => Node.Call(M.gear_record, id).AsInt64();

    /// <summary>A piece of gear's name (<see cref="GearRecord"/>'s value) in <paramref name="language"/>; empty without the archive.</summary>
    public string WeaponName(long record, string language) => Node.Call(M.weapon_name, record, language).AsString();

    /// <summary>Line <paramref name="index"/> of the remaster's localisation kit in <paramref name="language"/>, decoded; empty without the archive.</summary>
    public string LocText(int index, string language) => Node.Call(M.loc_text, index, language).AsString();

    /// <summary>
    /// Line <paramref name="index"/> of the localisation kit as UTF-8: the lines the game hands
    /// its Flash pages and tips (the Esc menu's entries, the boosters' tips), which the kit
    /// stores as UTF-8, not in the game's encoding (<see cref="LocText"/>).
    /// </summary>
    public string LocTextUtf8(int index, string language) => Node.Call(M.loc_text_utf8, index, language).AsString();

    /// <summary>A Flash or tip line for display (<see cref="LocTextUtf8"/>): its tags (<c>{..}</c>, <c>@el@</c> line breaks) made plain; "" without the archive.</summary>
    public string FlashText(int index, string language) =>
        MessageView.Clean(LocTextUtf8(index, language).Replace("@el@", " ").Replace("\n", " "));

    /// <summary>A developer's look at <paramref name="len"/> bytes of game memory (read only; empty while no game runs).</summary>
    public byte[] Peek(long address, int len) => Node.Call(M.peek, address, len).AsByteArray();

    /// <summary>A material texture's face (0, or 0..6 for a cube map) as PNG bytes; empty without the archive.</summary>
    public byte[] ClassicTexture(string path, int face) => Node.Call(M.classic_texture, path, face).AsByteArray();

    /// <summary>The effects' draws for the frame of <paramref name="update"/> (else the newest), encoded (<c>FXDR</c>); empty without a game.</summary>
    public byte[] EffectDraws(uint update) => Node.Call(M.effect_draws, update).AsByteArray();

    /// <summary>
    /// The effects' draws for the frame of <paramref name="update"/> (else the newest), as
    /// <see cref="EffectDraws"/>, copied into <paramref name="buffer"/> (grown when short, by
    /// half again: kept by the caller, so no new array an update); returns the length, 0 without
    /// a frame. The extension holds the encoded frame until its next call (main thread only).
    /// </summary>
    public int EffectDrawsInto(uint update, ref byte[] buffer)
    {
        var len = Node.Call(M.effect_draws_held, update).AsInt64();
        if (len <= 0)
            return 0;
        var at = Node.Call(M.effect_draws_address).AsInt64();
        return at == 0 ? 0 : Held.CopyInto(at, len, ref buffer);
    }

    /// <summary>
    /// The characters' cloth of the frame that drew <paramref name="update"/> (that update's only),
    /// encoded (<c>CLTH</c>, ffx-rust's docs/state-api.md "Cloth"), copied into
    /// <paramref name="buffer"/> as <see cref="EffectDrawsInto"/> does; returns the length, 0 without
    /// such a frame (not captured yet, or the capture is off: <c>FFX_NO_CLOTH</c>).
    /// </summary>
    public int ClothInto(uint update, ref byte[] buffer)
    {
        var len = Node.Call(M.cloth_held, update).AsInt64();
        if (len <= 0)
            return 0;
        var at = Node.Call(M.cloth_address).AsInt64();
        return at == 0 ? 0 : Held.CopyInto(at, len, ref buffer);
    }

    /// <summary>
    /// The newest two snapshots as <see cref="Snapshots"/> without Godot arrays: the extension
    /// holds the pair until its next call and gives each one's address and length
    /// (<c>current_address</c>, <c>current_len</c>, <c>previous_address</c>,
    /// <c>previous_len</c>); <see cref="Held.CopyInto"/> copies them out before the next call.
    /// </summary>
    /// <remarks>Its variant is disposed here; the caller disposes the dictionary (each is finalizable otherwise: <see cref="Variants"/>).</remarks>
    public Dictionary SnapshotsHeld() => Variants.CallDictionary(Node, M.snapshots_held);

    /// <summary>
    /// Map number <paramref name="map"/>'s likely level and its exits (the extension's
    /// <c>map_exits</c>: its event from <c>eventid.bin</c>, the maps its event script jumps to
    /// with constant arguments; docs/architecture.md, "Map transitions"): null while they are
    /// read in the background (ask again) and without the archive.
    /// </summary>
    public (string Level, int[] Maps, string[] Levels)? MapExits(int map)
    {
        using var v = Node.Call(M.map_exits, map);
        using var d = v.AsGodotDictionary();
        if (!Variants.Get(d, "ready").AsBool())
            return null;
        using var maps = Variants.Get(d, "maps");
        using var levels = Variants.Get(d, "levels");
        using var level = Variants.Get(d, "level");
        return (level.AsString(), maps.AsInt32Array(), levels.AsStringArray());
    }

    /// <summary>A texture by the name the game holds at run time (an effect draw's) as PNG bytes; empty when not in the archive.</summary>
    public byte[] RuntimeTexture(string name) => Node.Call(M.runtime_texture, name).AsByteArray();

    /// <summary>
    /// Asks for a texture's pixels (<paramref name="kind"/> <c>face</c>: a material texture's face;
    /// <c>runtime</c>: by the run-time name), read on the extension's own thread;
    /// <see cref="TexturesFinished"/> hands them over with <paramref name="id"/> (main thread only).
    /// </summary>
    public bool TextureRequest(long id, string kind, string name, int face) =>
        Node.Call(M.texture_request, id, kind, name, face).AsBool();

    /// <summary>The textures read since the last call: id, found, width, height, faces, rgba.</summary>
    public Godot.Collections.Array<Dictionary> TexturesFinished() =>
        Node.Call(M.textures_finished).AsGodotArray<Dictionary>();

    /// <summary>A texture's pixels now (main thread only), as <see cref="TexturesFinished"/>'s entries.</summary>
    public Dictionary TextureNow(string kind, string name, int face) =>
        Node.Call(M.texture_now, kind, name, face).AsGodotDictionary();

    /// <summary>Whether the material texture at <paramref name="path"/> is a cube map.</summary>
    public bool ClassicTextureIsCube(string path) => Node.Call(M.classic_texture_is_cube, path).AsBool();

    /// <summary>Which of <paramref name="names"/> a material's effect variant (<c>extras.ffx_effect</c>) switches on; empty without the archive.</summary>
    public string[] ClassicEffectSwitches(string effect, string[] names) =>
        Node.Call(M.classic_effect_switches, effect, names).AsStringArray();

    /// <summary>Sphere grid node kind <paramref name="kind"/>'s name, decoded; empty without the archive.</summary>
    public string GridNodeName(int kind, string language) => Node.Call(M.grid_node_name, kind, language).AsString();

    /// <summary>The save list's place name <paramref name="index"/> (a save header's location), decoded; empty without the archive.</summary>
    public string PlaceName(int index, string language) => Node.Call(M.place_name, index, language).AsString();

    /// <summary>Asks the game to start battle <paramref name="field"/>/<paramref name="group"/>/<paramref name="formation"/> as an encounter would.</summary>
    public bool RequestBattle(int field, int group, int formation) =>
        Node.Call(M.request_battle, field, group, formation).AsBool();

    /// <summary>Asks the game to load developer save <paramref name="n"/> (1..110) at its next update.</summary>
    public bool RequestDevSave(int n) => Node.Call(M.request_devsave, n).AsBool();

    /// <summary>The requests' replies since the last call.</summary>
    public string[] RequestResults() => Node.Call(M.request_results).AsStringArray();

    /// <summary>A key event; true when the game has a code for the key.</summary>
    public bool KeyInput(InputEventKey e) => Node.Call(M.key_input, e).AsBool();

    /// <summary>A key by its DirectInput scan code.</summary>
    public void ScanInput(int scan, bool down) => Node.Call(M.scan_input, scan, down);

    public void MouseInput(InputEvent e) => Node.Call(M.mouse_input, e);

    public void MouseExited() => Node.Call(M.mouse_exited);

    public void SetWindow(Vector2I size, Vector2I screen, Vector2I origin) =>
        Node.Call(M.set_window, size, screen, origin);

    public void SetFocus(bool focused) => Node.Call(M.set_focus, focused);

    /// <summary>
    /// The game's speed on the real clock, for debugging (0.25 .. 8, ffx-host's
    /// <c>Clock::set_speed</c>): before <see cref="Start"/> for the boot, then at once; returns
    /// the speed in use.
    /// </summary>
    public double SetSpeed(double speed) => Node.Call(M.set_speed, speed).AsDouble();

    /// <summary>The game's speed (<see cref="SetSpeed"/>; 1 unless set).</summary>
    public double Speed => Node.Call(M.speed).AsDouble();
}

/// <summary>Bytes the extension holds, copied out into buffers kept by C#.</summary>
public static class Held
{
    /// <summary>
    /// Copies <paramref name="len"/> bytes at <paramref name="address"/> (memory the extension
    /// holds until its next call, main thread only) into <paramref name="buffer"/>, grown when
    /// short (by half again, so a slowly growing frame does not make a new array each time);
    /// returns <paramref name="len"/>, 0 for none.
    /// </summary>
    public static int CopyInto(long address, long len, ref byte[] buffer)
    {
        if (address == 0 || len <= 0 || len > int.MaxValue / 2)
            return 0;
        int n = (int)len;
        if (buffer.Length < n)
            buffer = new byte[n + n / 2];
        System.Runtime.InteropServices.Marshal.Copy((System.IntPtr)address, buffer, 0, n);
        return n;
    }
}

/// <summary>One reading of the game's state (G0's small API; G1 brings the typed one).</summary>
public readonly struct GameState
{
    private readonly Dictionary _d;

    public GameState(Dictionary d) => _d = d;

    // keys made once (a string key is a new finalizable variant each read: Variants)
    private Variant Get(string key) => Variants.Get(_d, key);

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

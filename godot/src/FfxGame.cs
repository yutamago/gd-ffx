using System.Linq;
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
        public static readonly StringName classic_map_catalogue = "classic_map_catalogue";
        public static readonly StringName classic_characters = "classic_characters";
        public static readonly StringName classic_character_skeleton = "classic_character_skeleton";
        public static readonly StringName classic_texture_uses = "classic_texture_uses";
        public static readonly StringName classic_map_game_data = "classic_map_game_data";
        public static readonly StringName classic_dev_saves = "classic_dev_saves";
        public static readonly StringName decode_text = "decode_text";
        public static readonly StringName effect_draws = "effect_draws";
        public static readonly StringName effect_draws_address = "effect_draws_address";
        public static readonly StringName effect_draws_held = "effect_draws_held";
        public static readonly StringName effect_draw_owners = "effect_draw_owners";
        public static readonly StringName audio_events = "audio_events";
        public static readonly StringName set_audio_replaced = "set_audio_replaced";
        public static readonly StringName audio_seen = "audio_seen";
        public static readonly StringName movie_events = "movie_events";
        public static readonly StringName movie_state = "movie_state";
        public static readonly StringName set_movie_muted = "set_movie_muted";
        public static readonly StringName audio_mix_level = "audio_mix_level";
        public static readonly StringName audio_holds = "audio_holds";
        public static readonly StringName classic_sounds = "classic_sounds";
        public static readonly StringName classic_list = "classic_list";
        public static readonly StringName classic_sound_pcm = "classic_sound_pcm";
        public static readonly StringName encounter_maps = "encounter_maps";
        public static readonly StringName esc_button = "esc_button";
        public static readonly StringName esc_popup_answer = "esc_popup_answer";
        public static readonly StringName esc_state = "esc_state";
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
        public static readonly StringName set_native_opening = "set_native_opening";
        public static readonly StringName opening_state = "opening_state";
        public static readonly StringName opening_finish = "opening_finish";
        public static readonly StringName flash_movies = "flash_movies";
        public static readonly StringName flash_calls_pending = "flash_calls_pending";
        public static readonly StringName flash_calls = "flash_calls";
        public static readonly StringName flash_info = "flash_info";
        public static readonly StringName flash_symbols = "flash_symbols";
        public static readonly StringName flash_render = "flash_render";
        public static readonly StringName flash_bounds = "flash_bounds";
        public static readonly StringName flash_class = "flash_class";
        public static readonly StringName flash_label = "flash_label";
        public static readonly StringName flash_alphas = "flash_alphas";
        public static readonly StringName flash_bitmap = "flash_bitmap";
        public static readonly StringName place_name = "place_name";
        public static readonly StringName request_battle = "request_battle";
        public static readonly StringName set_battle_party = "set_battle_party";
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
    /// What names a piece of gear (API 13's <c>PartyMember.weapon_gear</c>, the results'
    /// <c>BattleEnd.gear_spoils</c>): its <c>w_name</c> record, its character <c>&lt;&lt; 16</c> and the
    /// config's hiragana <c>&lt;&lt; 24</c>, as <see cref="WeaponName"/> takes it; -1 for a record not in use.
    /// </summary>
    public static long GearRecord(GdFfx.Api.GearSpoil g) =>
        g.Used ? g.NameId | (long)g.Character << 16 | (g.Kana ? 1L : 0L) << 24 : -1;

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

    /// <summary>
    /// A Flash line with its <c>@el@</c> line breaks kept (as the game's
    /// <c>LocText_ConvertLineBreaks</c> makes them for its pop-ups), its tags made plain.
    /// </summary>
    public string FlashLines(int index, string language) =>
        string.Join("\n", LocTextUtf8(index, language).Replace("\n", "@el@").Split("@el@").Select(MessageView.Clean));

    /// <summary>
    /// The ESC menu page's button: the game's Flash callback <c>EscMenuButtonClick([page,
    /// index])</c>, called at its next <c>FFEscMenu_update</c> (the extension's <c>esc</c>; page
    /// 13 is Change parameters). False while no game runs.
    /// </summary>
    public bool EscButton(int page, int index) => Node.Call(M.esc_button, page, index).AsBool();

    /// <summary>Answers the ESC menu's open pop-up (<c>EscMenuPopUpConfirmed</c>): yes or no.</summary>
    public bool EscPopUpAnswer(bool yes) => Node.Call(M.esc_popup_answer, yes).AsBool();

    /// <summary>What the extension saw at the game's last <c>FFEscMenu_update</c> (<see cref="EscMenuState"/>).</summary>
    public EscMenuState EscState()
    {
        using var v = Node.Call(M.esc_state);
        using var d = v.AsGodotDictionary();
        bool B(string k)
        {
            using var x = Variants.Get(d, k);
            return x.AsBool();
        }
        long L(string k)
        {
            using var x = Variants.Get(d, k);
            return x.AsInt64();
        }
        return new EscMenuState(B("seen"), B("open"), B("parameters"), B("popup"), B("failed"), L("done"), L("row"));
    }

    /// <summary>
    /// The random encounters of the map the game looks them up for now (the extension's
    /// <c>encounter_maps</c>, read from the game's table): that map number and each group's
    /// battle map word, encounter rate (0: none) and level; null before the table is read or
    /// for a map without encounters.
    /// </summary>
    public (int Map, (int Map, int Rate, string Level)[] Groups)? EncounterMaps()
    {
        using var v = Node.Call(M.encounter_maps);
        using var d = v.AsGodotDictionary();
        using var ready = Variants.Get(d, "ready");
        if (!ready.AsBool())
            return null;
        using var map = Variants.Get(d, "map");
        using var groups = Variants.Get(d, "groups");
        using var list = groups.AsGodotArray();
        var out_ = new (int, int, string)[list.Count];
        for (int i = 0; i < list.Count; i++)
        {
            using var gv = list[i];
            using var g = gv.AsGodotDictionary();
            using var m = Variants.Get(g, "map");
            using var r = Variants.Get(g, "rate");
            using var l = Variants.Get(g, "level");
            out_[i] = (m.AsInt32(), r.AsInt32(), l.AsString());
        }
        return (map.AsInt32(), out_);
    }

    // ---- the Flash screens (docs/architecture.md, "The Flash screens") ----------------------

    /// <summary>Before <see cref="Start"/>: the start-up logos play natively (the game waits for their <c>Finish</c>, <see cref="OpeningFinish"/>).</summary>
    public void SetNativeOpening(bool on) => Node.Call(M.set_native_opening, on);

    /// <summary>The start-up opening: the start-up loop waited for it, its <c>Finish</c> was called, the loop went on, it ended without <see cref="OpeningFinish"/>.</summary>
    public (bool Waiting, bool FinishSent, bool Ended, bool TimedOut) OpeningState()
    {
        using var v = Node.Call(M.opening_state);
        using var d = v.AsGodotDictionary();
        bool B(string k)
        {
            using var x = Variants.Get(d, k);
            return x.AsBool();
        }
        return (B("waiting"), B("finish_sent"), B("ended"), B("timed_out"));
    }

    /// <summary>Ends the start-up opening (its <c>Finish</c>, at the start-up loop's next check).</summary>
    public void OpeningFinish() => Node.Call(M.opening_finish);

    /// <summary>The Flash movies the game opened, oldest first: name, open order, how often the game drew it.</summary>
    public System.Collections.Generic.List<(string Name, int Seq, long Draws)> FlashMovies()
    {
        var list = new System.Collections.Generic.List<(string, int, long)>();
        foreach (var e in Node.Call(M.flash_movies).AsStringArray())
        {
            var p = e.Split('\u001f');
            if (p.Length == 3)
                list.Add((p[0], int.Parse(p[1]), long.Parse(p[2])));
        }
        return list;
    }

    /// <summary>How many AS3 calls of the game on its movies wait (<see cref="FlashCalls"/>).</summary>
    public int FlashCallsPending() => (int)Node.Call(M.flash_calls_pending).AsInt64();

    /// <summary>The AS3 calls the game made on its movies since the last time, oldest first.</summary>
    public System.Collections.Generic.List<FlashCall> FlashCalls()
    {
        var list = new System.Collections.Generic.List<FlashCall>();
        foreach (var e in Node.Call(M.flash_calls).AsStringArray())
        {
            var p = e.Split('\u001f');
            if (p.Length >= 3)
                list.Add(new FlashCall(p[0], int.TryParse(p[1], out var q) ? q : 0, p[2], p[3..]));
        }
        return list;
    }

    /// <summary>A Flash movie's stage (pixels), frame rate and frames; null without it (the archive: <c>classic_open</c>).</summary>
    public (float Width, float Height, float Fps, int Frames)? FlashInfo(string movie)
    {
        using var v = Node.Call(M.flash_info, movie);
        using var d = v.AsGodotDictionary();
        if (d.Count == 0)
            return null;
        using var w = Variants.Get(d, "width");
        using var h = Variants.Get(d, "height");
        using var f = Variants.Get(d, "fps");
        using var n = Variants.Get(d, "frames");
        return ((float)w.AsDouble(), (float)h.AsDouble(), (float)f.AsDouble(), (int)n.AsInt64());
    }

    /// <summary>
    /// Character <paramref name="id"/> (0: the main timeline) of a Flash movie at
    /// <paramref name="frame"/>, drawn at <paramref name="scale"/> pixels a stage pixel into a
    /// <paramref name="width"/> x <paramref name="height"/> image whose corner is stage point
    /// <paramref name="origin"/>; null without the movie.
    /// </summary>
    public Image? FlashRender(string movie, int id, int frame, Vector2 origin, int width, int height, float scale)
    {
        var rgba = Node.Call(M.flash_render, movie, id, frame, origin.X, origin.Y, width, height, scale).AsByteArray();
        return rgba.Length == width * height * 4 ? Image.CreateFromData(width, height, false, Image.Format.Rgba8, rgba) : null;
    }

    /// <summary>One character of a Flash movie (<see cref="FlashSymbols"/>): id, kind (<c>shape</c>, <c>morph</c>, <c>sprite</c>, <c>bitmap</c>), class name ("" none), frames, frame labels.</summary>
    public sealed record FlashSymbol(int Id, string Kind, string Class, int Frames, (string Label, int Frame)[] Labels);

    /// <summary>Every character a Flash movie defines (the UI art browser); empty without the movie.</summary>
    public System.Collections.Generic.List<FlashSymbol> FlashSymbols(string movie)
    {
        var list = new System.Collections.Generic.List<FlashSymbol>();
        foreach (var line in Node.Call(M.flash_symbols, movie).AsStringArray())
        {
            var f = line.Split('\u001f');
            if (f.Length < 5 || !int.TryParse(f[0], out var id))
                continue;
            var labels = f[4].Length == 0 ? [] : System.Array.ConvertAll(f[4].Split(','), l =>
            {
                int eq = l.LastIndexOf('=');
                return eq > 0 && int.TryParse(l[(eq + 1)..], out var fr) ? (l[..eq], fr) : (l, 1);
            });
            list.Add(new FlashSymbol(id, f[1], f[2], int.TryParse(f[3], out var n) ? n : 1, labels));
        }
        return list;
    }

    /// <summary>Where a Flash movie's character draws at a frame (stage pixels); empty when nothing.</summary>
    public Rect2 FlashBounds(string movie, int id, int frame) => Node.Call(M.flash_bounds, movie, id, frame).AsRect2();

    /// <summary>The character a Flash movie exports as class <paramref name="name"/>, -1 for none.</summary>
    public int FlashClass(string movie, string name) => (int)Node.Call(M.flash_class, movie, name).AsInt64();

    /// <summary>The frame of label <paramref name="label"/> in sprite <paramref name="id"/> of a Flash movie, -1 for none.</summary>
    public int FlashLabel(string movie, int id, string label) => (int)Node.Call(M.flash_label, movie, id, label).AsInt64();

    /// <summary>The alpha of the main timeline's instance at <paramref name="depth"/>, frame by frame (index 0: frame 1).</summary>
    public float[] FlashAlphas(string movie, int depth) => Node.Call(M.flash_alphas, movie, depth).AsFloat32Array();

    /// <summary>Bitmap <paramref name="id"/> of a Flash movie; null without it.</summary>
    public Image? FlashBitmap(string movie, int id)
    {
        using var v = Node.Call(M.flash_bitmap, movie, id);
        using var d = v.AsGodotDictionary();
        if (d.Count == 0)
            return null;
        using var w = Variants.Get(d, "width");
        using var h = Variants.Get(d, "height");
        using var px = Variants.Get(d, "rgba");
        return Image.CreateFromData((int)w.AsInt64(), (int)h.AsInt64(), false, Image.Format.Rgba8, px.AsByteArray());
    }

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
    /// Who made each draw of the frame that drew <paramref name="update"/> (R7: the extension's
    /// <c>effect_draw_owners</c>): the draw count, an owner word per draw, then per field-program
    /// owner its word, place and stuck flag (<see cref="EffectOwners.Decode"/>); empty when no
    /// longer kept.
    /// </summary>
    public int[] EffectDrawOwners(uint update)
    {
        using var v = Node.Call(M.effect_draw_owners, update);
        return v.AsInt32Array();
    }

    /// <summary>The sound commands the game queued since the last call, eight words each (<see cref="GdFfx.Remaster.AudioReplacements"/>).</summary>
    public int[] AudioEvents()
    {
        using var v = Node.Call(M.audio_events);
        return v.AsInt32Array();
    }

    /// <summary>The ids of sound kind <paramref name="kind"/> (0 music, 1 sound effects, 2 voice) Godot plays itself; the game's own are silenced.</summary>
    public void SetAudioReplaced(int kind, int[] ids) => Node.Call(M.set_audio_replaced, kind, ids);

    /// <summary>Every id of sound kind <paramref name="kind"/> seen so far with its start count, as pairs.</summary>
    public int[] AudioSeen(int kind)
    {
        using var v = Node.Call(M.audio_seen, kind);
        return v.AsInt32Array();
    }

    /// <summary>The movies the game opened or ended since the last call (<c>opened</c>, <c>name</c>, <c>path</c>, <c>player</c>, <c>looping</c>).</summary>
    public Array<Dictionary> MovieEvents() => Node.Call(M.movie_events).AsGodotArray<Dictionary>();

    /// <summary>Where the game's movie is: <c>playing</c>, <c>frame</c>, <c>paused</c>, <c>ended</c>, <c>player</c>.</summary>
    public Dictionary MovieState() => Node.Call(M.movie_state).AsGodotDictionary();

    /// <summary>The game's own mix level: the RMS of its last half second (R7's checks).</summary>
    public double AudioMixLevel() => Node.Call(M.audio_mix_level).AsDouble();

    /// <summary>The game's FMOD volumes (<c>master</c>, <c>music</c>, <c>voice</c>, <c>sfx</c>, 0..1) and whether the music and voice are held silent (<c>music_held</c>, <c>voice_held</c>).</summary>
    public Dictionary AudioHolds() => Node.Call(M.audio_holds).AsGodotDictionary();

    /// <summary>The game's sounds of <paramref name="kind"/> (<c>music</c>, <c>sfx</c>, <c>voice</c>) by id: (id, name, bank, sample); empty without the archive.</summary>
    public System.Collections.Generic.List<(int Id, string Name, string Bank, int Sample)> ClassicSounds(string kind, string language)
    {
        var list = new System.Collections.Generic.List<(int, string, string, int)>();
        foreach (var line in Node.Call(M.classic_sounds, kind, language).AsStringArray())
        {
            var p = line.Split('\t');
            if (p.Length == 4 && int.TryParse(p[0], out var id) && int.TryParse(p[3], out var sample))
                list.Add((id, p[1], p[2], sample));
        }
        return list;
    }

    /// <summary>The archive's paths containing <paramref name="needle"/> (lower case); empty without the archive.</summary>
    public string[] ClassicList(string needle) => Node.Call(M.classic_list, needle).AsStringArray();

    /// <summary>A sample of the archive's bank as a stream (16-bit, its loop points), null when it is not there.</summary>
    public AudioStreamWav? ClassicSound(string bank, int sample)
    {
        var d = Node.Call(M.classic_sound_pcm, bank, sample).AsGodotDictionary();
        if (!d.TryGetValue("found", out var f) || !f.AsBool())
            return null;
        var wav = new AudioStreamWav
        {
            Format = AudioStreamWav.FormatEnum.Format16Bits,
            MixRate = d["rate"].AsInt32(),
            Stereo = d["channels"].AsInt32() == 2,
            Data = d["pcm"].AsByteArray(),
        };
        int lb = d["loop_begin"].AsInt32(), le = d["loop_end"].AsInt32();
        if (lb >= 0 && le > lb)
        {
            wav.LoopMode = AudioStreamWav.LoopModeEnum.Forward;
            wav.LoopBegin = lb;
            wav.LoopEnd = le;
        }
        return wav;
    }

    /// <summary>Silences the game's movie sound while a replacement plays.</summary>
    public void SetMovieMuted(bool on) => Node.Call(M.set_movie_muted, on);

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

    /// <summary>
    /// The map catalogue as JSON (docs/importer.md, "The map catalogue"; main thread only): every
    /// field level and battle stage with its events, map numbers, place names in
    /// <paramref name="languages"/> (empty: all eight) and area; empty without the archive.
    /// <see cref="Remaster.MapCatalogue"/> reads it.
    /// </summary>
    public string ClassicMapCatalogue(params string[] languages)
    {
        using var v = Node.Call(M.classic_map_catalogue, languages);
        return v.AsString();
    }

    /// <summary>
    /// The archive's characters as JSON (docs/remaster-editor.md, "Characters"; main thread only):
    /// model, kind, whether it has motions, the game's names in <paramref name="languages"/> where
    /// it gives one, the attachment points. Empty without the archive.
    /// <see cref="Characters.CharacterCatalogue"/> reads it.
    /// </summary>
    public string ClassicCharacters(params string[] languages)
    {
        using var v = Node.Call(M.classic_characters, languages);
        return v.AsString();
    }

    /// <summary>
    /// Model <paramref name="name"/>'s PS2 skeleton as JSON (every element's parent and bind pose,
    /// also those its HD model has no node for); empty without the archive or its <c>.chr</c>.
    /// <see cref="Characters.SkeletonInfo.FromBind"/> reads it.
    /// </summary>
    public string ClassicCharacterSkeleton(string name)
    {
        using var v = Node.Call(M.classic_character_skeleton, name);
        return v.AsString();
    }

    /// <summary>
    /// The developers' saves in the player's archive: (save number 1..110, the map number its
    /// load jumps to, entrance, scenario); empty without the archive.
    /// </summary>
    public (int Save, int Map, int Entrance, int Scenario)[] ClassicDevSaves()
    {
        var a = Node.Call(M.classic_dev_saves).AsInt32Array();
        var r = new (int, int, int, int)[a.Length / 4];
        for (int i = 0; i < r.Length; i++)
            r[i] = (a[4 * i], a[4 * i + 1], a[4 * i + 2], a[4 * i + 3]);
        return r;
    }

    /// <summary>Whether the material texture at <paramref name="path"/> is a cube map.</summary>
    /// <summary>
    /// The textures <paramref name="level"/>'s 3D model samples and the parts drawn with each, as
    /// JSON (<c>{"model", "textures": [{"id", "samplers", "parts"}]}</c>, parts relative to the
    /// model): the remaster's where-used index. Empty without the archive or a 3D model.
    /// </summary>
    public string ClassicTextureUses(string level)
    {
        using var v = Node.Call(M.classic_texture_uses, level);
        return v.AsString();
    }

    /// <summary>
    /// <paramref name="level"/>'s game data as JSON (docs/remaster-editor.md, "The game's data";
    /// parsing only): per event its script's entrances and workers, its <c>MAP1</c> file's walk
    /// meshes and scene cameras. Empty without the archive. <see cref="Remaster.MapGameData"/> reads it.
    /// </summary>
    public string ClassicMapGameData(string level)
    {
        using var v = Node.Call(M.classic_map_game_data, level);
        return v.AsString();
    }

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

    /// <summary>
    /// Writes the battle party (<c>g_SaveInParty</c>: 8 party indices, 0xff empty) before a battle is
    /// asked for (the checks' <c>--party=HEX</c>, as ffx-rust's <c>cloth-probe --party</c>).
    /// </summary>
    public bool SetBattleParty(byte[] party) => Node.Call(M.set_battle_party, party).AsBool();

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

/// <summary>
/// The game's ESC menu as the extension's wrapper of <c>FFEscMenu_update</c> saw it last: it ran
/// (<c>Seen</c>), the menu is open, the Change parameters entry is enabled (the game's own
/// condition: no movie fading, the scenario flag set, not an Eternal Calm scene), the page's
/// confirmation waits for its answer (<c>PopUp</c>), a callback could not be called, the calls
/// run so far and the row of the last button the game took.
/// </summary>
public readonly record struct EscMenuState(bool Seen, bool Open, bool Parameters, bool PopUp, bool Failed, long Done, long Row);

/// <summary>An AS3 call the game made on one of its Flash movies (<see cref="FfxGame.FlashCalls"/>): the movie's file name and open order, the function, its arguments as text.</summary>
public sealed record FlashCall(string Movie, int Seq, string Function, string[] Args)
{
    /// <summary>Argument <paramref name="i"/>, "" past the end.</summary>
    public string Arg(int i) => i < Args.Length ? Args[i] : "";
}

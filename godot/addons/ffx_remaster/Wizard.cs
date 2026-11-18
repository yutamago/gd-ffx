#if TOOLS
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using GdFfx.Remaster;
using GdFfx.Remaster.Materialize;
using GdFfx.Remaster.Wizard;
using Godot;

namespace GdFfx.RemasterEditor;

/// <summary>
/// The remaster wizard (docs/remaster-editor.md, "The remaster wizard"; docs/remaster-plan.md R4):
/// a first automated pass per map, over a batch of maps one after another. For each map: its
/// classic import loaded on its own (not opened as a scene), its colour textures classified
/// (<see cref="WizardPass.Analyze"/>: a starter profile each, or keep classic), the materials made
/// with those profiles (<see cref="QueueBake"/>: GPU when the Materials tab uses it, else the CPU,
/// in the background; their files content-named in the texture folders, no entry written), the
/// lighting from the game's look with VoxelGI, with a renderer the before/after
/// pictures (<see cref="WizardRender"/>), the exposure matched to the classic picture and R3's
/// colour check; all of it staged in <see cref="WizardState"/> for the review. Nothing reaches the
/// project until a map is accepted (<see cref="RemasterPlugin.WizardAccept"/>: one undo step).
/// 2D-layer maps are skipped (or noted as kept classic). The batch pauses between maps and resumes,
/// in this session or the next (the state is saved after each map). Driven by <see cref="Tick"/>.
/// </summary>
public sealed class Wizard
{
    private enum Phase { Import, Analyze, Bake, Imports, Render, Tune, Finish }

    private sealed class Job
    {
        public required MapEntry Entry { get; init; }
        public required WizardMapState Map { get; init; }
        public Phase Phase { get; set; } = Phase.Import;
        public Node3D? Node { get; set; }
        public Aabb Box { get; set; }
        public QueueBake? Bake { get; set; }
        public WizardRender? Render { get; set; }
        public MapLighting? Lighting { get; set; }
        public Stopwatch Clock { get; } = Stopwatch.StartNew();
        public Dictionary<string, PbrRecipe> Recipes { get; } = new();
        public bool OnlyThis { get; init; }
        public float? FirstDL { get; set; }
        public Stopwatch? ImportWait { get; set; }
    }

    private readonly RemasterPlugin _plugin;
    private Job? _job;
    private QueueBake? _remake;
    private (string Key, string Id, string Profile)? _remakeOf;

    public WizardState State { get; private set; }

    /// <summary>What it is doing ("" idle).</summary>
    public string Status { get; private set; } = "";

    /// <summary>The map being made (null: none).</summary>
    public string? Making => _job?.Entry.Key;

    public bool Busy => _job != null || _remake != null;

    /// <summary>The bake going (the progress), if any.</summary>
    public QueueBake? Bake => _job?.Bake ?? _remake;

    /// <summary>Called when a map's pass is staged, a batch ends, a texture is made again.</summary>
    public event System.Action? Changed;

    public Wizard(RemasterPlugin plugin)
    {
        _plugin = plugin;
        State = WizardState.Load();
        // a batch the last session left running comes back paused: Resume goes on
        if (State.Batch is { Finished: false, Paused: false } b)
        {
            b.Paused = true;
            Status = $"a batch at {b.Next} of {b.Maps.Count} (paused: Resume goes on)";
        }
    }

    /// <summary>Reads the state file again (another editor session, an undo wrote it).</summary>
    public void Reload()
    {
        if (!Busy)
            State = WizardState.Load();
    }

    /// <summary>The maps of a scope of the map browser: one map, an area, the maps the project changes, all maps.</summary>
    public List<string> Scope(string scope, string? key, string? area)
    {
        var cat = _plugin.Session.Catalogue;
        if (cat == null)
            return new();
        return scope switch
        {
            "map" when key != null => [key],
            "area" when area != null => cat.Levels.Where(e => e.Area == area).Select(e => e.Key).ToList(),
            "changed" => MapStore.Levels().Where(k => cat.Find(k) != null).ToList(),
            "all" => cat.Levels.Select(e => e.Key).ToList(),
            _ => new(),
        };
    }

    /// <summary>Starts a batch over <paramref name="keys"/> (a running one is replaced when idle). An error or null.</summary>
    public string? Start(IEnumerable<string> keys, string twoD = "skip")
    {
        if (!_plugin.Session.IsOpen)
            return "the archive is not open";
        if (Busy)
            return "the wizard is busy";
        var list = keys.Select(k => k.ToLowerInvariant()).Distinct().ToList();
        if (list.Count == 0)
            return "no maps";
        State.Batch = new WizardBatch { Maps = list, TwoD = twoD, Started = Now() };
        State.Save();
        Status = $"batch of {list.Count} map(s)";
        GD.Print($"ffx remaster: wizard: batch of {list.Count} map(s): {string.Join(", ", list.Take(12))}{(list.Count > 12 ? ", ..." : "")}");
        return null;
    }

    /// <summary>Pauses the batch after the map being made (saved: resumes in this session or the next).</summary>
    public void Pause()
    {
        if (State.Batch is not { } b || b.Finished)
            return;
        b.Paused = true;
        State.Save();
        Status = _job != null ? $"pausing after {_job.Entry.Key}" : "paused";
        Changed?.Invoke();
    }

    /// <summary>Resumes a paused batch. An error or null.</summary>
    public string? Resume()
    {
        if (State.Batch is not { } b || b.Finished)
            return "no batch to resume";
        b.Paused = false;
        State.Save();
        Status = $"resumed at {b.Next + 1} of {b.Maps.Count}";
        GD.Print($"ffx remaster: wizard: {Status}");
        Changed?.Invoke();
        return null;
    }

    /// <summary>Stops the map being made (dropped, not staged) and pauses the batch.</summary>
    public void Cancel()
    {
        _job?.Bake?.Cancel();
        if (_job != null)
        {
            _job.Render?.Free();
            if (_job.Node != null && GodotObject.IsInstanceValid(_job.Node) && _job.Node.GetParent() == null)
                _job.Node.Free();
            State.Maps.Remove(_job.Entry.Key);
            _job = null;
        }
        if (State.Batch is { } b)
            b.Paused = true;
        State.Save();
        Status = "cancelled";
        Changed?.Invoke();
    }

    public void Tick()
    {
        if (_remake != null)
            TickRemake();
        if (_job != null)
        {
            try
            {
                TickJob(_job);
            }
            catch (System.Exception e)
            {
                Fail(_job, e.Message);
                GD.PrintErr($"ffx remaster: wizard: {_job?.Entry.Key}: {e}");
            }
            return;
        }
        if (State.Batch is not { Paused: false } b || b.Finished || _remake != null || !_plugin.Session.IsOpen)
            return;
        var key = b.Maps[b.Next];
        var entry = _plugin.Session.Catalogue?.Find(key);
        if (entry == null)
        {
            State.Maps[key] = new WizardMapState { Key = key, State = "failed", Reason = "not in the catalogue", When = Now() };
            Advance(b);
            return;
        }
        if (entry.Model3D == null)
        {
            // a map of 2D layers: nothing 3D to remaster (R7's), skipped or kept classic
            State.Maps[key] = new WizardMapState
            {
                Key = key, Level = entry.Level, State = "skipped", When = Now(),
                Reason = b.TwoD == "classic" ? "2D layers only: kept classic" : "2D layers only: skipped",
            };
            GD.Print($"ffx remaster: wizard: {key}: {State.Maps[key].Reason}");
            Advance(b);
            return;
        }
        _job = new Job { Entry = entry, Map = new WizardMapState { Key = key, Level = entry.Level, State = "making", When = Now() } };
        Status = $"{key} ({b.Next + 1}/{b.Maps.Count}): importing";
        _plugin.Session.Classic?.Path("map", entry.Level);
    }

    private void Advance(WizardBatch b)
    {
        b.Next++;
        State.Save();
        if (b.Finished)
        {
            Status = $"batch done: {State.Summary()}";
            GD.Print($"ffx remaster: wizard: {Status}");
        }
        Changed?.Invoke();
    }

    private void TickJob(Job job)
    {
        var key = job.Entry.Key;
        switch (job.Phase)
        {
            case Phase.Import:
            {
                var classic = _plugin.Session.Classic!;
                classic.Poll();
                if (classic.Path("map", job.Entry.Level) is not { } path)
                {
                    if (classic.Failed("map", job.Entry.Level))
                        Fail(job, "the classic map did not import");
                    return;
                }
                var map = ClassicLibrary.Load(path, $"PS3Data{job.Entry.Level}tex/");
                if (map == null)
                {
                    Fail(job, $"{path} did not load");
                    return;
                }
                ClassicMaterials.FixLayers(map);
                map.Scale = Vector3.One * ClassicLibrary.MapScale;
                job.Node = map;
                job.Phase = Phase.Analyze;
                Status = $"{key}: classifying";
                return;
            }
            case Phase.Analyze:
            {
                var (textures, box) = WizardPass.Analyze(job.Node!, job.Entry.Level, TextureActions.Source);
                job.Box = box;
                var jobs = new List<QueueBake.Job>();
                foreach (var t in textures)
                {
                    var ts = new WizardTextureState
                    {
                        Id = t.Id, Kind = TextureClassifier.Name(t.Class.Kind), AutoProfile = t.Class.Profile, Score = Round(t.Class.Score),
                        Margin = Round(t.Class.Margin), Why = t.Class.Explain(), Surfaces = t.Use.Surfaces, Area = Mathf.Round(t.Use.Area),
                    };
                    job.Map.Textures.Add(ts);
                    if (t.Class.Profile.Length == 0 || WizardPass.Recipe(t.Class.Profile) is not { } r)
                        continue;
                    job.Recipes[t.Id] = r;
                    jobs.Add(new QueueBake.Job(t.Id, r, t.Class.Profile));
                }
                job.Lighting = WizardPass.Lighting(job.Entry.Level, box);
                if (LookCache.Read(job.Entry.Level) == null)
                    GD.Print($"ffx remaster: wizard: {key}: the game has not shown this map here (no look cached): the lighting follows the defaults until it has");
                GD.Print($"ffx remaster: wizard: {key}: {textures.Count} texture(s), {jobs.Count} to make "
                    + $"({string.Join(", ", jobs.GroupBy(j => j.Profile).Select(g => $"{g.Key} {g.Count()}"))}), GI {job.Lighting.GiMode} (box {box.Size.X:0}x{box.Size.Y:0}x{box.Size.Z:0})");
                job.Bake = new QueueBake(_plugin, jobs, _plugin.Materials?.UsingGpu == true, _ => null, (j, baked) => Finish(job, j, baked), $"Wizard: {key}");
                job.Phase = Phase.Bake;
                Status = $"{key}: making {jobs.Count} material(s) on the {(_plugin.Materials?.UsingGpu == true ? "GPU" : "CPU")}";
                return;
            }
            case Phase.Bake:
            {
                var bake = job.Bake!;
                bake.Tick();
                if (!bake.Finished)
                {
                    Status = $"{key}: making materials {bake.Finishes}/{bake.Total}";
                    return;
                }
                job.Map.BakeMs = Mathf.Round(bake.Clock.Elapsed.TotalMilliseconds);
                foreach (var t in job.Map.Textures)
                    if (t.AutoProfile.Length > 0 && t.Entry.Length == 0)
                        t.Error = bake.StateOf(t.Id) == QueueBake.JobState.Failed ? "the bake failed" : "not made";
                job.Bake = null;
                job.Map.Lighting = WizardMapState.LightingFields(job.Lighting!);
                if (WizardRender.Available)
                {
                    // the made PNGs imported first (a material whose maps do not load draws as plain metal)
                    job.ImportWait = Stopwatch.StartNew();
                    job.Phase = Phase.Imports;
                    Status = $"{key}: waiting for Godot's import of the maps";
                }
                else
                {
                    job.Map.Colour = "no renderer (headless): no pictures, no colour check";
                    job.Node!.Free();
                    job.Node = null;
                    job.Phase = Phase.Finish;
                }
                return;
            }
            case Phase.Imports:
            {
                bool ready = !TextureStore.Importing && !EditorInterface.Singleton.GetResourceFilesystem().IsScanning() && StagedImported(job.Map, out _);
                if (!ready && job.ImportWait!.Elapsed.TotalSeconds < 180)
                    return;
                if (!ready && StagedImported(job.Map, out var missing) == false)
                    GD.PrintErr($"ffx remaster: wizard: {key}: {missing} not imported after 180 s; the pictures may show it wrong");
                RemasterTextures.Invalidate();
                job.Render = new WizardRender(_plugin, job.Node!, job.Entry.Level, job.Box, job.Lighting!, StagedSet(job.Map), TextureStore.Dir,
                    _plugin.Session.Game is { } g ? MapGameData.Read(g, job.Entry.Level) : null);
                job.Phase = job.OnlyThis ? Phase.Tune : Phase.Render;
                Status = $"{key}: pictures";
                return;
            }
            case Phase.Render:
            case Phase.Tune:
            {
                var render = job.Render!;
                if (!render.Tick())
                    return;
                var r = render.Colour();
                if (job.Phase == Phase.Render && r != null)
                {
                    float exposure = WizardPass.TuneExposure(job.Lighting!.ToneExposure, r);
                    if (!Mathf.IsEqualApprox(exposure, job.Lighting.ToneExposure))
                    {
                        GD.Print($"ffx remaster: wizard: {key}: colour {r}; exposure {job.Lighting.ToneExposure:0.##} -> {exposure:0.##}");
                        job.FirstDL = r.DL;
                        job.Lighting.ToneExposure = exposure;
                        render.Again(job.Lighting);
                        job.Phase = Phase.Tune;
                        return;
                    }
                }
                else if (job.Phase == Phase.Tune && r != null && job.FirstDL is { } dl0 && Mathf.Abs(r.DL) > Mathf.Abs(dl0))
                {
                    // the tuned exposure kept only when it brought the lightness nearer (the pictures say the first one's numbers then)
                    job.Lighting!.ToneExposure = 1f;
                    GD.Print($"ffx remaster: wizard: {key}: the tuned exposure did not help ({r}); back to 1");
                }
                job.Map.Lighting = WizardMapState.LightingFields(job.Lighting!);
                job.Map.Colour = (r?.ToString() ?? "no pictures")
                    + (LookCache.Read(job.Entry.Level) == null ? " (no look of the game's cached for this map: the defaults' key light, a sky without the game's colours; play the map once)" : "");
                job.Map.ColourOk = r?.Ok ?? true;
                var dir = WizardState.PicturesDir(key);
                job.Map.Before = WizardRender.Save(render.Classic, dir, "classic").ToList();
                job.Map.After = WizardRender.Save(render.Remastered, dir, "remastered").ToList();
                render.Free();
                job.Render = null;
                job.Node = null;
                job.Phase = Phase.Finish;
                return;
            }
            case Phase.Finish:
            {
                var lighting = job.Map.LightingResource();
                job.Map.Note = WizardPass.Note(job.Map.Textures.Select(t => (t.Profile, t.Applies)), lighting, job.Map.When[..10]);
                job.Map.State = "staged";
                if (!job.OnlyThis)
                    job.Map.Ms = Mathf.Round(job.Clock.Elapsed.TotalMilliseconds);
                State.Maps[key] = job.Map;
                _job = null;
                Status = $"{key}: staged ({job.Map.Ms / 1000:0.0} s): {job.Map.Note}";
                GD.Print($"ffx remaster: wizard: {Status}; colour {job.Map.Colour}");
                if (State.Batch is { } b && !job.OnlyThis && b.Next < b.Maps.Count && b.Maps[b.Next] == key)
                    Advance(b);
                else
                {
                    State.Save();
                    Changed?.Invoke();
                }
                return;
            }
        }
    }

    private void Fail(Job? job, string why)
    {
        if (job == null)
            return;
        job.Bake?.Cancel();
        job.Render?.Free();
        if (job.Node != null && GodotObject.IsInstanceValid(job.Node) && job.Node.GetParent() == null)
            job.Node.Free();
        job.Map.State = "failed";
        job.Map.Reason = why;
        job.Map.Ms = Mathf.Round(job.Clock.Elapsed.TotalMilliseconds);
        State.Maps[job.Entry.Key] = job.Map;
        _job = null;
        GD.PrintErr($"ffx remaster: wizard: {job.Entry.Key}: failed: {why}");
        if (State.Batch is { } b && b.Next < b.Maps.Count && b.Maps[b.Next] == job.Entry.Key)
            Advance(b);
        else
            State.Save();
    }

    /// <summary>A texture made: its files written (content-named; no entry is set), its staged entry and thumbnails kept.</summary>
    private RemasterTexture Finish(Job job, QueueBake.Job j, BakedMaps baked)
    {
        var entry = Entry(j.Id, j.Profile, j.Recipe, baked);
        var t = job.Map.Textures.First(x => x.Id == j.Id);
        Stage(t, entry, j.Profile, baked, job.Entry.Key);
        return entry;
    }

    private static RemasterTexture Entry(string id, string profile, PbrRecipe recipe, BakedMaps baked)
    {
        var r = (PbrRecipe)recipe.Duplicate();
        var p = MaterialProfiles.Load(profile);
        return TextureActions.WithMaterial(id, MaterialBaker.Images(baked, r), r, p != null ? profile : "", p != null ? MaterialProfiles.Hash(p) : "",
            p != null ? MaterialProfiles.Differences(r, p) : null);
    }

    private static void Stage(WizardTextureState t, RemasterTexture entry, string profile, BakedMaps baked, string key)
    {
        t.Entry = GD.VarToStr(TextureStore.ToDictionary(entry));
        t.Profile = profile;
        t.Error = "";
        var dir = WizardState.PicturesDir(key);
        DirAccess.MakeDirRecursiveAbsolute(GameSettings.Globalize(dir));
        var name = RemasterTextures.ShortName(t.Id);
        t.Albedo = Thumb(baked.Maps[MaterialStep.Albedo], $"{dir}/{name}-albedo.png");
        t.Normal = Thumb(baked.Maps[MaterialStep.Normal], $"{dir}/{name}-normal.png");
    }

    private static string Thumb(Remaster.Texturing.RgbaImage img, string path)
    {
        var i = PbrBuilder.ToImage(img);
        i.Resize(96, Mathf.Max(1, 96 * i.GetHeight() / Mathf.Max(1, i.GetWidth())), Image.Interpolation.Bilinear);
        i.SavePng(GameSettings.Globalize(path));
        return path;
    }

    /// <summary>Whether every staged material of <paramref name="m"/> loads with its maps imported (else the first that does not).</summary>
    public static bool StagedImported(WizardMapState m, out string? missing)
    {
        missing = null;
        if (!TextureStore.InProject)
            return true;
        foreach (var t in m.Textures)
        {
            if (!t.Applies)
                continue;
            var e = TextureStore.FromDictionary(t.Id, GD.StrToVar(t.Entry).AsGodotDictionary());
            var mat = ResourceLoader.Load(TextureStore.Absolute(e.Material), "", ResourceLoader.CacheMode.Ignore) as BaseMaterial3D;
            if (mat?.AlbedoTexture is not CompressedTexture2D || mat is OrmMaterial3D { OrmTexture: not CompressedTexture2D })
            {
                missing = e.Material;
                return false;
            }
        }
        return true;
    }

    /// <summary>The staged entries of <paramref name="m"/> that apply, as a texture set (paths relative to the store).</summary>
    public static RemasterTextureSet StagedSet(WizardMapState m)
    {
        var set = new RemasterTextureSet();
        foreach (var t in m.Textures)
            if (t.Applies && GD.StrToVar(t.Entry).AsGodotDictionary() is { } d)
                set.Textures.Add(TextureStore.FromDictionary(t.Id, d));
        return set;
    }

    /// <summary>Makes <paramref name="id"/> of <paramref name="key"/>'s staged pass again with <paramref name="profile"/> (the review's "another profile"); one undo step when made. An error or null.</summary>
    public string? Remake(string key, string id, string profile)
    {
        if (_remake != null || _job != null)
            return "the wizard is busy";
        if (!State.Maps.TryGetValue(key, out var m) || m.State != "staged")
            return $"{key} has no staged pass";
        if (m.Textures.All(t => t.Id != id))
            return $"{id} is not one of {key}'s textures";
        if (WizardPass.Recipe(profile) is not { } r)
            return $"no profile {profile}";
        _remakeOf = (key, id, profile);
        _remake = new QueueBake(_plugin, [new QueueBake.Job(id, r, profile)], _plugin.Materials?.UsingGpu == true, _ => null,
            (j, baked) => Keep(Entry(j.Id, j.Profile, j.Recipe, baked), baked), $"Wizard: {RemasterTextures.ShortName(id)} with {profile}");
        Status = $"{key}: making {RemasterTextures.ShortName(id)} with {profile}";
        return null;
    }

    private BakedMaps? _remadeMaps;

    private RemasterTexture Keep(RemasterTexture e, BakedMaps baked)
    {
        _remadeMaps = baked;
        return e;
    }

    private void TickRemake()
    {
        var run = _remake!;
        run.Tick();
        if (!run.Finished)
            return;
        _remake = null;
        var (key, id, profile) = _remakeOf!.Value;
        _remakeOf = null;
        if (run.Results.Count == 0 || _remadeMaps == null || !State.Maps.TryGetValue(key, out var m))
        {
            Status = $"{key}: {RemasterTextures.ShortName(id)} was not made again";
            Changed?.Invoke();
            return;
        }
        var before = WizardState.MapText(m);
        var copy = WizardState.MapFrom(before)!;
        var t = copy.Textures.First(x => x.Id == id);
        Stage(t, run.Results[0].After, profile, _remadeMaps, key);
        t.Decision = "accepted";
        copy.Note = WizardPass.Note(copy.Textures.Select(x => (x.Profile, x.Applies)), copy.LightingResource(), copy.When[..Mathf.Min(10, copy.When.Length)]);
        _remadeMaps = null;
        _plugin.WizardSetMap(key, before, WizardState.MapText(copy), $"Wizard: {RemasterTextures.ShortName(id)} made with {profile}");
        Status = $"{key}: {RemasterTextures.ShortName(id)} made with {profile}";
        Changed?.Invoke();
    }

    /// <summary>Draws <paramref name="key"/>'s pictures again with its textures as decided now (after a review's changes). An error or null.</summary>
    public string? Repicture(string key)
    {
        if (Busy)
            return "the wizard is busy";
        if (!WizardRender.Available)
            return "no renderer (headless)";
        if (!State.Maps.TryGetValue(key, out var m) || m.State != "staged" || _plugin.Session.Catalogue?.Find(key) is not { } entry)
            return $"{key} has no staged pass";
        var classic = _plugin.Session.Classic!;
        if (classic.Path("map", entry.Level) is not { } path || ClassicLibrary.Load(path, $"PS3Data{entry.Level}tex/") is not { } map)
            return "the classic map is not imported";
        ClassicMaterials.FixLayers(map);
        map.Scale = Vector3.One * ClassicLibrary.MapScale;
        var (_, box) = MapTextures.Collect(map);
        _job = new Job
        {
            Entry = entry, Map = WizardState.MapFrom(WizardState.MapText(m))!, Node = map, Box = box, Lighting = m.LightingResource(), OnlyThis = true,
            Phase = Phase.Imports, ImportWait = Stopwatch.StartNew(),
        };
        Status = $"{key}: pictures again";
        return null;
    }

    private static float Round(float v) => Mathf.Snapped(v, 0.01f);

    private static string Now() => System.DateTime.Now.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
}
#endif

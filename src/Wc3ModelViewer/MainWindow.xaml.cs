using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Wc3ModelViewer.Core.Casc;
using Wc3ModelViewer.Core.Convert;
using Wc3ModelViewer.Core.Formats;

namespace Wc3ModelViewer;

public partial class MainWindow : Window
{
    private Wc3Storage? _storage;
    private Wc3AssetIndex? _index;
    private Wc3TextureCache? _cascTextures;              // persistent cache for CASC models
    private Wc3TextureCache? _textures;                  // cache of the CURRENT model (may be loose-file)
    private bool _busy;

    private Wc3ModelEntry? _entry;                       // currently displayed model
    private MdxModel? _model;
    private MdxAnimator? _animator;
    private int _lod;
    private readonly ObservableCollection<GeosetItem> _geosetItems = [];
    private bool _suppressRebuild;                       // guards RebuildScene during bulk visibility changes
    private readonly List<SceneMesh> _sceneMeshes = [];  // meshes of the current scene
    private EffectLayer? _effects;                       // particle and ribbon emitters, null when the model has none

    private MdxSequence? _sequence;                      // active sequence (null = rest pose)
    private bool _playing;
    private double _timeMs;
    private long _wallMs;
    private bool _suppressAnimUi;                        // guards combo/slider handlers during programmatic updates
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private TimeSpan _lastTick;

    /// <summary>Everything the scene holds for one rendered geoset.</summary>
    private sealed class SceneMesh
    {
        public required MdxGeoset Geoset { get; init; }
        public required MeshGeometry3D Mesh { get; init; }
        public required Brush Brush { get; init; }
        public required Vector3[] SkinPositions { get; init; }
        public Vector3[]? SkinNormals { get; init; }

        /// <summary>
        /// A KMTF texture flipbook, when the geoset's layer animates through one: the track that
        /// picks the frame, and every frame decoded up front. Reforged animates water, waterfalls
        /// and moonwells this way — fifty frames each — so decoding on demand would stutter.
        /// </summary>
        public MdxLayer? Flipbook { get; init; }
        public Dictionary<int, BitmapSource>? Frames { get; init; }
        public int CurrentFrame = -1;
    }

    public MainWindow()
    {
        InitializeComponent();
        DetectInstallPath();
        CompositionTarget.Rendering += OnFrameTick;
    }

    /// <summary>Pre-fills the install path with the first common Warcraft III location that exists
    /// (identified by its .build.info file). The XAML default stays if nothing is found.</summary>
    private void DetectInstallPath()
    {
        string[] candidates =
        [
            @"C:\games\Warcraft III",
            @"C:\Program Files (x86)\Warcraft III",
            @"C:\Program Files\Warcraft III",
            @"D:\games\Warcraft III",
        ];
        foreach (string c in candidates)
        {
            if (File.Exists(Path.Combine(c, ".build.info"))) { InstallPath.Text = c; return; }
        }
    }

    // Supersampling factor: the viewport renders at this multiple of its on-screen size, then the Viewbox
    // scales it down. Higher = crisper textures (WPF Viewport3D under-samples at 1:1) at ~factor^2 cost.
    private const double Supersample = 2.0;

    private void OnViewportSlotSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ViewportSlot.ActualWidth > 0 && ViewportSlot.ActualHeight > 0)
        {
            Viewport.Width = ViewportSlot.ActualWidth * Supersample;
            Viewport.Height = ViewportSlot.ActualHeight * Supersample;
        }
    }

    // ---------------- Storage & browsing ----------------

    private void OnOpenClick(object sender, RoutedEventArgs e) => _ = OpenStorageAsync();

    private void OnFilterChanged(object sender, RoutedEventArgs e) => ApplyFilter();

    private void OnAssetSelected(object sender, SelectionChangedEventArgs e)
    {
        if (AssetList.SelectedItem is AssetItem item)
            _ = LoadModelAsync(item.Entry);
    }

    private async Task OpenStorageAsync()
    {
        if (_busy) return;
        string path = InstallPath.Text.Trim();
        if (!File.Exists(Path.Combine(path, ".build.info")))
        {
            Status.Text = "No .build.info there — pick the folder that holds Warcraft III.exe: " + path;
            return;
        }

        SetBusy(true, "Opening storage and cataloguing models…");
        try
        {
            var (storage, index) = await Task.Run(() =>
            {
                var s = new Wc3Storage(path);
                return (s, s.BuildIndex());
            });

            _storage?.Dispose();
            _storage = storage;
            _index = index;
            _cascTextures = new Wc3TextureCache(storage);
            ApplyFilter();
            Status.Text = $"Loaded {index.Models.Count:N0} models " +
                          $"({index.Models.Count(m => m.ArtSet == Wc3ArtSet.Reforged):N0} HD, " +
                          $"{index.Models.Count(m => m.ArtSet == Wc3ArtSet.Classic):N0} SD). " +
                          "Type to filter, then click one to view.";
        }
        catch (Exception ex)
        {
            Status.Text = "Failed to open storage: " + ex.Message;
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private void ApplyFilter()
    {
        if (_index is null) return;
        string q = Filter.Text.Trim();
        var wanted = ArtSetCombo.SelectedIndex switch
        {
            1 => Wc3ArtSet.Reforged,
            2 => Wc3ArtSet.Classic,
            _ => (Wc3ArtSet?)null,
        };

        IEnumerable<Wc3ModelEntry> items = _index.Models;
        if (wanted is not null) items = items.Where(m => m.ArtSet == wanted);
        if (q.Length > 0)
            items = items.Where(m => m.RelativePath.Contains(q, StringComparison.OrdinalIgnoreCase));

        AssetList.ItemsSource = items.Take(5000).Select(m => new AssetItem(m)).ToList();
    }

    private async Task LoadModelAsync(Wc3ModelEntry entry)
    {
        if (_busy || _storage is null) return;

        SetBusy(true, "Loading " + entry.Name + " …");
        try
        {
            var storage = _storage;
            var model = await Task.Run(() => MdxReader.Read(storage.ReadFile(entry.CascName)));
            PresentModel(entry, model, _cascTextures!);
        }
        catch (Exception ex)
        {
            ModelHost.Content = null;
            ExportButton.IsEnabled = false;
            Status.Text = "Could not load model: " + ex.Message;
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private void OnOpenFileClick(object sender, RoutedEventArgs e) => _ = OpenLooseFileAsync();

    /// <summary>
    /// Opens a loose .mdx from disk — custom models. Textures resolve from the model's own folder
    /// first (.blp/.dds beside the file); stock references fall through to the CASC when open.
    /// </summary>
    private async Task OpenLooseFileAsync()
    {
        if (_busy) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open a Warcraft III model",
            Filter = "Warcraft III models (*.mdx)|*.mdx|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;

        SetBusy(true, "Loading " + Path.GetFileName(dlg.FileName) + " …");
        try
        {
            string path = dlg.FileName;
            var model = await Task.Run(() => MdxReader.Read(File.ReadAllBytes(path)));

            // CascName "" = no archive prefix: the texture cache probes LocalRoots, then the
            // classic CASC tree for stock references like Textures\gutz.blp.
            var entry = new Wc3ModelEntry
            {
                CascName = "",
                RelativePath = Path.GetFileName(path),
                ArtSet = model.IsReforged ? Wc3ArtSet.Reforged : Wc3ArtSet.Classic,
            };
            var cache = new Wc3TextureCache(_storage);
            string dir = Path.GetDirectoryName(path) ?? "";
            if (dir.Length > 0)
            {
                cache.LocalRoots.Add(dir);
                // Common layouts for downloaded models: textures one level up or in a subfolder.
                if (Directory.Exists(Path.Combine(dir, "Textures"))) cache.LocalRoots.Add(Path.Combine(dir, "Textures"));
                if (Path.GetDirectoryName(dir) is { Length: > 0 } parent) cache.LocalRoots.Add(parent);
            }
            PresentModel(entry, model, cache);
            if (_storage is null)
                Status.Text += "   (no CASC open — stock game textures will be missing)";
        }
        catch (Exception ex)
        {
            ModelHost.Content = null;
            ExportButton.IsEnabled = false;
            Status.Text = "Could not load model: " + ex.Message;
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private void PresentModel(Wc3ModelEntry entry, MdxModel model, Wc3TextureCache textures)
    {
        _entry = entry;
        _model = model;
        _textures = textures;
        _animator = new MdxAnimator(model);
        _cutoutCache.Clear();
        ClearAnimationUi();

        var effects = new EffectLayer(model, textures, entry.CascName);
        _effects = effects.HasAnything ? effects : null;

        // Default to LOD 0 — the full-detail mesh, and the one the exporter writes.
        var lods = model.LodLevels;
        _lod = lods.FirstOrDefault();
        _suppressRebuild = true;
        LodCombo.ItemsSource = lods.Select(l => $"LOD {l}").ToList();
        LodCombo.SelectedIndex = lods.IndexOf(_lod);
        LodCombo.Visibility = lods.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _suppressRebuild = false;

        PopulateGeosetPanel();
        RebuildScene(zoom: true);
        PopulateAnimCombo();
        ExportButton.IsEnabled = true;

        var shown = _geosetItems.Where(i => i.IsVisible).Select(i => i.Geoset).ToList();
        int hidden = _geosetItems.Count - shown.Count;
        string hiddenNote = hidden > 0 ? $" (+{hidden} hidden)" : "";
        // Effects are called out per model because their absence is otherwise unexplainable: a
        // Reforged HD model's effects are almost always PopcornFX, which this tool can neither draw
        // nor export, and the viewport for one of those is simply empty. Saying so beats leaving
        // the user to wonder whether something is broken.
        var fx = new List<string>();
        if (model.ParticleEmitters.Count > 0) fx.Add($"{model.ParticleEmitters.Count} particle");
        if (model.RibbonEmitters.Count > 0) fx.Add($"{model.RibbonEmitters.Count} ribbon");
        if (model.Lights.Count > 0) fx.Add($"{model.Lights.Count} light");
        string effects = fx.Count > 0 ? $", effects: {string.Join(" + ", fx)}" : "";
        if (model.PopcornEmitterCount > 0)
            effects += $", {model.PopcornEmitterCount} PopcornFX (not shown — Reforged's own system)";

        Status.Text = $"{entry.RelativePath}   —   {entry.ArtSet}, {shown.Count} geosets{hiddenNote}, " +
                      $"{shown.Sum(g => g.VertexCount):N0} verts, {shown.Sum(g => g.TriangleCount):N0} tris, " +
                      $"{model.Sequences.Count} sequences{effects}";
    }

    // ---------------- Geoset panel ----------------

    private void PopulateGeosetPanel()
    {
        if (_model is null) return;
        _suppressRebuild = true;
        _geosetItems.Clear();
        foreach (var geo in _model.Geosets)
        {
            if (geo.LodId != _lod || geo.VertexCount == 0 || geo.Indices.Length < 3) continue;
            _geosetItems.Add(new GeosetItem(geo, DefaultVisible(geo), _model, () => RebuildScene()));
        }
        GeosetList.ItemsSource = _geosetItems;
        _suppressRebuild = false;
    }

    /// <summary>Statically invisible geosets (GEOA alpha 0, no track) carry corpses and alternate forms.</summary>
    private bool DefaultVisible(MdxGeoset geo)
    {
        var anim = _model?.GeosetAnims.FirstOrDefault(a => a.GeosetId == geo.Index);
        return anim is null || anim.AlphaTrack is not null || anim.Alpha >= 0.01f;
    }

    private void SetAllGeosets(Func<GeosetItem, bool> visible)
    {
        if (_geosetItems.Count == 0) return;
        _suppressRebuild = true;
        foreach (var item in _geosetItems) item.IsVisible = visible(item);
        _suppressRebuild = false;
        RebuildScene();
    }

    private void OnShowAllGeosets(object sender, RoutedEventArgs e) => SetAllGeosets(_ => true);
    private void OnHideAllGeosets(object sender, RoutedEventArgs e) => SetAllGeosets(_ => false);
    private void OnResetGeosets(object sender, RoutedEventArgs e) => SetAllGeosets(i => DefaultVisible(i.Geoset));

    private void OnLodChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressRebuild || _model is null || LodCombo.SelectedIndex < 0) return;
        var lods = _model.LodLevels;
        if (LodCombo.SelectedIndex >= lods.Count) return;
        _lod = lods[LodCombo.SelectedIndex];
        PopulateGeosetPanel();
        RebuildScene();
    }

    // ---------------- Scene ----------------

    /// <summary>(Re)builds the 3D content from the geosets currently checked in the panel.</summary>
    private void RebuildScene(bool zoom = false)
    {
        if (_suppressRebuild || _model is null || _entry is null || _textures is null) return;

        var group = new Model3DGroup();

        // Soft NEUTRAL lighting: moderate ambient fill + gentle white key/fill directionals, so
        // surfaces read as solid without tinting the textures (same rig as the D3 viewer).
        group.Children.Add(new AmbientLight(Color.FromRgb(0x66, 0x66, 0x66)));
        group.Children.Add(new DirectionalLight(Color.FromRgb(0x99, 0x99, 0x99), new Vector3D(-0.4, -0.5, -0.75)));
        group.Children.Add(new DirectionalLight(Color.FromRgb(0x3A, 0x3A, 0x3A), new Vector3D(0.7, 0.55, -0.2)));

        _sceneMeshes.Clear();

        // WPF 3D has no alpha test: transparent texels still write depth, so anything drawn later
        // behind them disappears. Draw strictly opaque → cutout → alpha-blended/additive, so a
        // cutout in front (fur, beard, hair) never depth-rejects the solid geometry behind it.
        var cutoutModels = new List<GeometryModel3D>();
        var alphaModels = new List<GeometryModel3D>();

        foreach (var item in _geosetItems)
        {
            if (!item.IsVisible) continue;
            var geo = item.Geoset;
            if ((uint)geo.MaterialId >= (uint)_model.Materials.Count) continue;

            var composite = MaterialCompositor.Compose(_model, _model.Materials[geo.MaterialId],
                                                       _textures, _entry.CascName, teamColor: 0);

            // Reforged flags every HD layer transparent, so Compose returns AlphaTest for solid body
            // parts too. A geoset whose own UVs never touch a transparent texel is really opaque —
            // the same per-geoset test the exporter uses — so it draws in the opaque pass instead of
            // among the cutouts, and its depth no longer competes with the geometry behind.
            if (composite.Blend == CompositeBlend.AlphaTest && !IsRealCutout(geo, composite))
                composite = composite.WithBlend(CompositeBlend.Opaque);

            var brush = MakeBrush(composite);
            var (front, back) = MakeMaterial(composite, brush);

            var mesh = new MeshGeometry3D
            {
                Positions = ToPoints(geo.Positions),
                TriangleIndices = new Int32Collection(geo.Indices),
                TextureCoordinates = new PointCollection(geo.Uvs.Select(uv => new System.Windows.Point(uv.X, uv.Y))),
            };
            if (geo.Normals.Length == geo.VertexCount)
                mesh.Normals = new Vector3DCollection(geo.Normals.Select(n => new Vector3D(n.X, n.Y, n.Z)));

            var gm = new GeometryModel3D(mesh, front) { BackMaterial = back ?? front };
            switch (composite.Blend)
            {
                case CompositeBlend.AlphaBlend or CompositeBlend.Additive: alphaModels.Add(gm); break;
                case CompositeBlend.AlphaTest: cutoutModels.Add(gm); break;
                default: group.Children.Add(gm); break;   // opaque, drawn first
            }

            var flipbook = _model.Materials[geo.MaterialId].Layers
                                 .FirstOrDefault(l => l.TextureIdTrack is { Count: > 0 });

            _sceneMeshes.Add(new SceneMesh
            {
                Geoset = geo, Mesh = mesh, Brush = brush,
                SkinPositions = new Vector3[geo.VertexCount],
                SkinNormals = geo.VertexCount <= 25_000 ? new Vector3[geo.VertexCount] : null,
                Flipbook = flipbook,
                Frames = flipbook is null ? null : DecodeFlipbook(flipbook),
            });
        }
        foreach (var gm in cutoutModels) group.Children.Add(gm);   // cutouts after all opaque
        foreach (var gm in alphaModels) group.Children.Add(gm);    // then blended/additive
        _effects?.AddTo(group);                                    // effects last: they add light over everything

        ModelHost.Content = group;
        if (zoom) Viewport.ZoomExtents(0);
        ApplyPose();   // rebuilt meshes start at bind pose; re-apply the active frame, if any
    }

    /// <summary>
    /// Decodes every distinct frame of a texture flipbook, keyed by TEXS index. These layers are a
    /// single blended texture with no PBR set, so the frames are loaded straight from the cache
    /// rather than through the material compositor — there is nothing to composite.
    /// </summary>
    private Dictionary<int, BitmapSource> DecodeFlipbook(MdxLayer layer)
    {
        var frames = new Dictionary<int, BitmapSource>();
        if (_model is null || _textures is null || _entry is null) return frames;

        foreach (int id in layer.FlipbookTextureIds)
        {
            if ((uint)id >= (uint)_model.Textures.Count) continue;
            var img = _textures.Load(_entry.CascName, _model.Textures[id], ViewerTeamColor);
            if (img is null) continue;
            var bmp = BitmapSource.Create(img.Width, img.Height, 96, 96, PixelFormats.Bgra32,
                                          null, img.Pixels, img.Width * 4);
            bmp.Freeze();
            frames[id] = bmp;
        }
        return frames;
    }

    /// <summary>Player slot the viewport previews team colour with — the same one RebuildScene composites.</summary>
    private const int ViewerTeamColor = 0;

    /// <summary>
    /// Whether a transparent-flagged geoset is a genuine cutout — its own UVs actually sample
    /// transparent texels — versus a solid part sharing an atlas that has cutout regions elsewhere.
    /// Cached per geoset: the coverage rasterisation is the exporter's, too expensive to redo on
    /// every panel toggle.
    /// </summary>
    private bool IsRealCutout(MdxGeoset geo, CompositeMaterial composite)
    {
        if (_cutoutCache.TryGetValue(geo.Index, out bool cached)) return cached;
        bool real = MaterialCompositor.CutoutCoverage(composite.Texture, geo) >= 0.005f;
        _cutoutCache[geo.Index] = real;
        return real;
    }

    private readonly Dictionary<int, bool> _cutoutCache = new();

    private static Brush MakeBrush(CompositeMaterial composite)
    {
        var img = composite.Texture;
        var pixels = img.Pixels;

        // No alpha test in WPF: snap cutout alpha to hard 0/255 at the engine's threshold.
        if (composite.Blend == CompositeBlend.AlphaTest)
        {
            pixels = (byte[])pixels.Clone();
            for (int i = 3; i < pixels.Length; i += 4)
                pixels[i] = pixels[i] >= MaterialCompositor.CutoutThreshold * 255 ? (byte)255 : (byte)0;
        }

        var bmp = BitmapSource.Create(img.Width, img.Height, 96, 96, PixelFormats.Bgra32, null, pixels, img.Width * 4);
        bmp.Freeze();
        // Absolute viewport + Tile = true GL_REPEAT semantics for out-of-range UVs (same as D3 viewer).
        var brush = new ImageBrush(bmp)
        {
            TileMode = TileMode.Tile,
            Stretch = Stretch.Fill,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, 1, 1),
        };
        return brush;
    }

    private static (Material Front, Material? Back) MakeMaterial(CompositeMaterial composite, Brush brush)
    {
        Material material = composite.Blend == CompositeBlend.Additive || composite.Unshaded
            ? new MaterialGroup { Children = { new DiffuseMaterial(Brushes.Black), new EmissiveMaterial(brush) } }
            : new DiffuseMaterial(brush);
        return (material, composite.TwoSided ? material : null);
    }

    private static Point3DCollection ToPoints(Vector3[] v)
    {
        var pc = new Point3DCollection(v.Length);
        foreach (var p in v) pc.Add(new Point3D(p.X, p.Y, p.Z));
        return pc;
    }

    // ---------------- Animation playback ----------------

    private void PopulateAnimCombo()
    {
        if (_model is null) return;
        _suppressAnimUi = true;
        var items = new List<string> { "(rest pose)" };
        items.AddRange(_model.Sequences.Select(s => $"{s.Name}  ({s.DurationMs / 1000.0:0.0}s)"));
        AnimCombo.ItemsSource = items;
        AnimCombo.SelectedIndex = 0;
        AnimCombo.IsEnabled = _model.Sequences.Count > 0;
        _suppressAnimUi = false;
    }

    private void ClearAnimationUi()
    {
        _sequence = null;
        _playing = false;
        _timeMs = 0;
        _suppressAnimUi = true;
        PlayButton.IsEnabled = false;
        PlayButton.IsChecked = false;
        FrameSlider.Value = 0;
        FrameLabel.Text = "";
        _suppressAnimUi = false;
    }

    private void OnAnimSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressAnimUi || _model is null) return;
        int idx = AnimCombo.SelectedIndex;
        if (idx <= 0)
        {
            ClearAnimationUi();
            ApplyPose();          // back to rest pose
            RestoreRestPose();
            return;
        }

        var seq = _model.Sequences[idx - 1];
        _sequence = seq;
        _timeMs = 0;
        _playing = true;
        _lastTick = _clock.Elapsed;
        _effects?.Reset();          // particles from the previous sequence must not bleed into this one

        _suppressAnimUi = true;
        LoopCheck.IsChecked = !seq.NonLooping;
        FrameSlider.Maximum = Math.Max(1, seq.DurationMs);
        FrameSlider.Value = 0;
        PlayButton.IsEnabled = true;
        PlayButton.IsChecked = true;
        _suppressAnimUi = false;

        Status.Text = $"{_entry?.Name}   —   playing '{seq.Name}' ({seq.DurationMs:N0} ms" +
                      $"{(seq.NonLooping ? ", plays once" : "")})";
    }

    private void OnPlayToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressAnimUi || _sequence is null) return;
        _playing = PlayButton.IsChecked == true;
        if (_playing)
        {
            // Restart from the top when play is pressed at the end of a non-looping run.
            if (LoopCheck.IsChecked != true && _timeMs >= _sequence.DurationMs) _timeMs = 0;
            _lastTick = _clock.Elapsed;
        }
    }

    private void OnSpeedChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SpeedLabel is not null) SpeedLabel.Text = $"{e.NewValue:0.0}×";
    }

    private void OnFrameScrubbed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressAnimUi || _sequence is null) return;
        _timeMs = e.NewValue;
        ApplyPose();
    }

    /// <summary>Per-render-frame tick: advances the active sequence and re-skins the scene.</summary>
    private void OnFrameTick(object? sender, EventArgs e)
    {
        if (_sequence is null || !_playing) { _lastTick = _clock.Elapsed; return; }
        var now = _clock.Elapsed;
        double dt = Math.Clamp((now - _lastTick).TotalSeconds, 0, 0.25);
        _lastTick = now;
        _wallMs += (long)(dt * 1000);

        _timeMs += dt * 1000 * SpeedSlider.Value;
        double dur = Math.Max(_sequence.DurationMs, 1);
        if (_timeMs >= dur)
        {
            if (LoopCheck.IsChecked == true) _timeMs %= dur;
            else { _timeMs = dur; _playing = false; }
        }

        ApplyPose();
        UpdateEffects((float)dt);

        _suppressAnimUi = true;
        FrameSlider.Value = _timeMs;
        if (!_playing) PlayButton.IsChecked = false;      // non-looping run ended
        _suppressAnimUi = false;
    }

    /// <summary>
    /// Steps the emitters. Particles are billboarded here rather than in the simulator because
    /// facing depends on the camera, which the simulator has no business knowing about.
    /// </summary>
    private void UpdateEffects(float dt)
    {
        if (_effects is null || _model is null || _animator is null || _sequence is null) return;

        var look = new Vector3D(0, 1, 0);
        var camUp = new Vector3D(0, 0, 1);
        if (Viewport.Camera is ProjectionCamera cam) { look = cam.LookDirection; camUp = cam.UpDirection; }

        var right = Vector3D.CrossProduct(look, camUp);
        if (right.LengthSquared < 1e-9) right = new Vector3D(1, 0, 0); else right.Normalize();
        var up = Vector3D.CrossProduct(right, look);
        if (up.LengthSquared < 1e-9) up = new Vector3D(0, 0, 1); else up.Normalize();

        int t = _sequence.IntervalStart + (int)_timeMs;
        _effects.Update(dt * (float)SpeedSlider.Value, _animator, _sequence, t, _wallMs,
                        new Vector3((float)right.X, (float)right.Y, (float)right.Z),
                        new Vector3((float)up.X, (float)up.Y, (float)up.Z));
    }

    /// <summary>Applies the current time to all meshes: skinning plus GEOA geoset visibility.</summary>
    private void ApplyPose()
    {
        if (_model is null || _animator is null) return;
        if (_sequence is null && _sceneMeshes.Count == 0) return;

        int t = _sequence is null ? 0 : _sequence.IntervalStart + (int)_timeMs;
        _animator.Evaluate(_sequence, t, _wallMs);

        foreach (var sm in _sceneMeshes)
        {
            float alpha = _animator.GeosetAlpha(sm.Geoset.Index, _sequence, t, _wallMs);
            sm.Brush.Opacity = alpha < 0.01f ? 0 : alpha;
            if (alpha < 0.01f) continue;                  // invisible: skip the skinning cost too

            // Advance a texture flipbook. Its keys run on their own timeline from 0, not the
            // sequence's, so the whole animation loops independently of which sequence is playing —
            // water keeps flowing during Stand, Birth and Death alike.
            if (sm is { Flipbook: { } fb, Frames: { Count: > 0 } frames }
                && fb.TextureIdTrack is { Count: > 0 } track)
            {
                int span = Math.Max(1, track.Times[^1]);
                int frame = fb.TextureIdAt((int)(_wallMs % span));
                if (frame != sm.CurrentFrame && frames.TryGetValue(frame, out var bmp)
                    && sm.Brush is ImageBrush ib)
                {
                    ib.ImageSource = bmp;
                    sm.CurrentFrame = frame;
                }
            }
            if (_sequence is null) continue;              // rest pose: geometry already correct

            _animator.SkinGeoset(sm.Geoset, sm.SkinPositions, sm.SkinNormals);

            var pts = sm.Mesh.Positions;
            sm.Mesh.Positions = null!;                    // detach: bulk-update without change events
            for (int v = 0; v < sm.SkinPositions.Length; v++)
                pts[v] = new Point3D(sm.SkinPositions[v].X, sm.SkinPositions[v].Y, sm.SkinPositions[v].Z);
            sm.Mesh.Positions = pts;

            if (sm.SkinNormals is not null)
            {
                var nrm = sm.Mesh.Normals;
                sm.Mesh.Normals = null!;
                for (int v = 0; v < sm.SkinNormals.Length && v < nrm.Count; v++)
                    nrm[v] = new Vector3D(sm.SkinNormals[v].X, sm.SkinNormals[v].Y, sm.SkinNormals[v].Z);
                sm.Mesh.Normals = nrm;
            }
        }
        if (_sequence is not null)
            FrameLabel.Text = $"{(int)_timeMs:N0} / {_sequence.DurationMs:N0} ms";
    }

    /// <summary>Puts all meshes back to their stored bind-pose positions.</summary>
    private void RestoreRestPose()
    {
        foreach (var sm in _sceneMeshes)
        {
            var geo = sm.Geoset;
            var pts = sm.Mesh.Positions;
            sm.Mesh.Positions = null!;
            for (int v = 0; v < geo.VertexCount && v < pts.Count; v++)
                pts[v] = new Point3D(geo.Positions[v].X, geo.Positions[v].Y, geo.Positions[v].Z);
            sm.Mesh.Positions = pts;

            if (geo.Normals.Length == geo.VertexCount)
            {
                var nrm = sm.Mesh.Normals;
                sm.Mesh.Normals = null!;
                for (int v = 0; v < geo.VertexCount && v < nrm.Count; v++)
                    nrm[v] = new Vector3D(geo.Normals[v].X, geo.Normals[v].Y, geo.Normals[v].Z);
                sm.Mesh.Normals = nrm;
            }
            sm.Brush.Opacity = 1;
        }
    }

    // ---------------- Export ----------------

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (_model is null || _entry is null) return;
        var visible = _geosetItems.Where(i => i.IsVisible).Select(i => i.Geoset.Index).ToHashSet();
        var dlg = new ExportDialog(_model, _entry, _lod, visible) { Owner = this };
        if (dlg.ShowDialog() == true)
            _ = RunExportAsync(dlg.Options, dlg.OutputDir, dlg.ExportGltf, dlg.ExportM3);
    }

    private async Task RunExportAsync(M3ExportOptions options, string outDir, bool doGltf, bool doM3)
    {
        if (_model is null || _entry is null || _textures is null) return;
        SetBusy(true, "Exporting…");
        try
        {
            var model = _model;
            var entry = _entry;
            var textures = _textures;

            // D3-exporter layout: <OutDir>\<ModelName>\<ModelName>.m3 (+ .gltf/.bin/PNGs) with the
            // DDS textures under Textures\ — the same relative path the .m3 references, so both
            // the SC2 loose-file preview and a drag-into-importer keep their textures.
            var (fileCount, unitDir, m3Note) = await Task.Run(() =>
            {
                string dir = Path.Combine(outDir, options.ModelName);
                Directory.CreateDirectory(dir);
                int count = 0;
                string note = "";

                if (doGltf)
                {
                    var result = new GltfExporter(model, options).Export(textures, entry.CascName);
                    foreach (var file in result.Files)
                        File.WriteAllBytes(Path.Combine(dir, file.FileName), file.Data);
                    count += result.Files.Count;
                }
                if (doM3)
                {
                    var result = new M3Exporter(model, options).Export(textures, entry.CascName);
                    string m3Path = Path.Combine(dir, options.ModelName + ".m3");
                    File.WriteAllBytes(m3Path, result.M3);
                    string texDir = Path.Combine(dir, options.TextureFolder);
                    Directory.CreateDirectory(texDir);
                    foreach (var tex in result.Textures)
                        File.WriteAllBytes(Path.Combine(texDir, tex.FileName), tex.Data);
                    count += 1 + result.Textures.Count;

                    // Resolve every path the written .m3 asks for against what is now on disk. SC2
                    // draws a layer it cannot find as black rather than reporting anything, so a
                    // broken reference would otherwise only show up as a shading bug in the editor.
                    var refs = M3TextureAudit.Verify(m3Path);
                    var missing = refs.Where(r => !r.Resolved).Select(r => r.Path).ToList();
                    note = missing.Count == 0
                        ? "preview the .m3 in the SC2 cutscene editor, or import the folder as-is"
                        : $"WARNING: {missing.Count} texture reference(s) do not resolve — "
                          + string.Join(", ", missing.Take(3));
                }
                return (count, dir, note);
            });

            Status.Text = $"Exported {fileCount} file(s) to {unitDir}" +
                          (doM3 ? $"   —   {m3Note}"
                                : "   —   import the .gltf into Blender, then export .m3 with m3studio");
        }
        catch (Exception ex)
        {
            Status.Text = "Export failed: " + ex.Message;
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    // ---------------- Plumbing ----------------

    private void SetBusy(bool busy, string? status)
    {
        _busy = busy;
        OpenButton.IsEnabled = !busy;
        AssetList.IsEnabled = !busy;
        if (status is not null) Status.Text = status;
        Mouse.OverrideCursor = busy ? Cursors.Wait : null;
    }

    protected override void OnClosed(EventArgs e)
    {
        CompositionTarget.Rendering -= OnFrameTick;
        _storage?.Dispose();
        base.OnClosed(e);
    }

    private sealed class AssetItem(Wc3ModelEntry entry)
    {
        public Wc3ModelEntry Entry { get; } = entry;
        public override string ToString()
            => $"{Entry.Name}{(Entry.ArtSet == Wc3ArtSet.Reforged ? "  · HD" : "  · SD")}   ({Entry.Folder})";
    }

    /// <summary>One row in the geoset toggle panel: wraps a geoset with a bindable visibility flag.</summary>
    private sealed class GeosetItem : INotifyPropertyChanged
    {
        public MdxGeoset Geoset { get; }
        public string Label { get; }
        public string Tip { get; }
        public Brush LabelBrush { get; }               // dim default-hidden rows

        private bool _isVisible;
        private readonly Action _onChanged;

        public GeosetItem(MdxGeoset geo, bool visible, MdxModel model, Action onChanged)
        {
            Geoset = geo;
            _isVisible = visible;
            _onChanged = onChanged;

            var anim = model.GeosetAnims.FirstOrDefault(a => a.GeosetId == geo.Index);
            string tag = anim is not null && anim.AlphaTrack is null && anim.Alpha < 0.01f ? "hidden"
                : anim?.AlphaTrack is not null ? "animated"
                : "";
            Label = tag.Length > 0
                ? $"geoset {geo.Index}  ({geo.VertexCount:N0}v · {tag})"
                : $"geoset {geo.Index}  ({geo.VertexCount:N0}v)";
            Tip = $"material {geo.MaterialId}, {geo.TriangleCount:N0} triangles" +
                  (geo.LodName.Length > 0 ? $", {geo.LodName}" : "");
            LabelBrush = new SolidColorBrush(visible ? Color.FromRgb(0xCC, 0xCC, 0xCC) : Color.FromRgb(0x88, 0x88, 0x88));
            LabelBrush.Freeze();
        }

        public bool IsVisible
        {
            get => _isVisible;
            set
            {
                if (_isVisible == value) return;
                _isVisible = value;
                OnPropertyChanged();
                _onChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

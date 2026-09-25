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
    private bool _exportable;                            // a model is loaded and shown

    private const int CustomSet = 4;                     // the art-set combo's "Custom" entry
    private string? _customDir;                          // folder the Custom list shows
    private List<Wc3ModelEntry> _customModels = [];      // every .mdx under it, relative paths
    private int _lastArtSet;                             // to step back when the folder prompt is cancelled

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

        // Open the install before anything else can happen. Every path through this app needs it —
        // browsing obviously, but so does a custom model off disk, which nearly always leaves the
        // Warcraft III textures it reuses as bare paths for the game to supply. Opening it up front
        // means those are simply there, rather than a model that loads grey and an error to read.
        bool opened = false;                       // Loaded can fire again if the window is re-parented
        Loaded += async (_, _) =>
        {
            if (opened) return;
            opened = true;
            if (IsInstallPath(InstallPath.Text.Trim())) await OpenStorageAsync();
            else Status.Text = "Point this at your Warcraft III folder — the one holding .build.info — "
                               + "and click Open. Everything else needs it, including custom models, "
                               + "which borrow their textures from the installed game.";
            await OpenFromCommandLineAsync();
        };
    }

    /// <summary>
    /// <c>--open &lt;name&gt; [--play]</c>: filter to a model and load the first match, optionally
    /// playing its first sequence. For scripted checks of the viewer; a user never needs it.
    /// </summary>
    private async Task OpenFromCommandLineAsync()
    {
        var args = Environment.GetCommandLineArgs();
        int at = Array.IndexOf(args, "--open");
        if (at < 0 || at + 1 >= args.Length || _index is null) return;
        Filter.Text = args[at + 1];
        if (args.Contains("--hd")) ArtSetCombo.SelectedIndex = 2;
        if (args.Contains("--de")) ArtSetCombo.SelectedIndex = 1;
        ApplyFilter();
        if (AssetList.Items.Count == 0) { Status.Text = $"--open: nothing matches '{args[at + 1]}'"; return; }
        var item = (AssetItem)AssetList.Items[0]!;
        await LoadModelAsync(item.Entry);
        if (args.Contains("--play") && AnimCombo.Items.Count > 1)
        {
            AnimCombo.SelectedIndex = 1;                 // 0 is the rest pose
            PlayButton.IsChecked = true;
            OnPlayToggled(PlayButton, new RoutedEventArgs());
        }

        // --screenshot <file.png> [--frames n] [--every ms]: render the window off-screen a few
        // times while the animation runs, then quit. Off-screen because a screen grab of a WPF
        // window that another window covers is blank, and a check that cannot see is no check.
        int shot = Array.IndexOf(args, "--screenshot");
        if (shot < 0 || shot + 1 >= args.Length) return;
        int frames = Array.IndexOf(args, "--frames") is int fi && fi >= 0 && fi + 1 < args.Length && int.TryParse(args[fi + 1], out int fn) ? fn : 1;
        int every = Array.IndexOf(args, "--every") is int ei && ei >= 0 && ei + 1 < args.Length && int.TryParse(args[ei + 1], out int ev) ? ev : 700;
        int delay = Array.IndexOf(args, "--delay") is int di && di >= 0 && di + 1 < args.Length && int.TryParse(args[di + 1], out int dv) ? dv : 1200;
        // --speed slows playback so a short effect (most spells run about a second) can be caught mid-flight.
        if (Array.IndexOf(args, "--speed") is int si && si >= 0 && si + 1 < args.Length
            && double.TryParse(args[si + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double sp))
            SpeedSlider.Value = Math.Clamp(sp, SpeedSlider.Minimum, SpeedSlider.Maximum);
        await Task.Delay(delay);
        for (int k = 0; k < frames; k++)
        {
            string file = frames == 1 ? args[shot + 1] : Path.ChangeExtension(args[shot + 1], null) + $"_{k}.png";
            SaveWindowPng(file);
            await Task.Delay(every);
        }
        Close();
    }

    private void SaveWindowPng(string file)
    {
        var root = (FrameworkElement)Content;
        int w = Math.Max(1, (int)root.ActualWidth), h = Math.Max(1, (int)root.ActualHeight);
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(file);
        enc.Save(fs);
    }

    /// <summary>A Warcraft III folder is the one holding <c>.build.info</c>, which names the CASC build.</summary>
    private static bool IsInstallPath(string path) =>
        path.Length > 0 && File.Exists(Path.Combine(path, ".build.info"));

    /// <summary>
    /// Pre-fills the install path: the folder last opened successfully, else the first common
    /// Warcraft III location that exists. The XAML default stays if nothing is found.
    /// </summary>
    private void DetectInstallPath()
    {
        if (UserSettings.Get("install") is { Length: > 0 } saved && IsInstallPath(saved))
        {
            InstallPath.Text = saved;
            return;
        }

        string[] candidates =
        [
            @"C:\games\Warcraft III",
            @"C:\Program Files (x86)\Warcraft III",
            @"C:\Program Files\Warcraft III",
            @"D:\games\Warcraft III",
        ];
        foreach (string c in candidates)
        {
            if (IsInstallPath(c)) { InstallPath.Text = c; return; }
        }
    }

    private static void RememberInstallPath(string path) => UserSettings.Set("install", path);

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

    /// <summary>
    /// Switching to Custom lists the user's own folder instead of the install. The first time, or
    /// when the remembered folder has gone, it asks for one; cancelling steps back to the art set
    /// that was showing, since an empty list would read as "the folder has no models".
    /// </summary>
    private void OnArtSetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CustomDirButton is null) return;             // fires once from InitializeComponent
        if (ArtSetCombo.SelectedIndex == CustomSet)
        {
            if (_customDir is null && UserSettings.Get("custom.dir") is { Length: > 0 } saved && Directory.Exists(saved))
                _customDir = saved;
            if (_customDir is null && !PickCustomDir())
            {
                ArtSetCombo.SelectedIndex = _lastArtSet;
                return;
            }
            // Rescanned on every switch: the point of the folder is that models get added to it.
            ScanCustomDir();
        }
        _lastArtSet = ArtSetCombo.SelectedIndex;
        CustomDirButton.Visibility = ArtSetCombo.SelectedIndex == CustomSet ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilter();
    }

    private void OnCustomDirClick(object sender, RoutedEventArgs e)
    {
        if (_busy || !PickCustomDir()) return;
        ScanCustomDir();
        ApplyFilter();
    }

    private bool PickCustomDir()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the folder holding your custom models" };
        if (_customDir is not null) dlg.InitialDirectory = _customDir;
        if (dlg.ShowDialog(this) != true) return false;
        _customDir = dlg.FolderName;
        UserSettings.Set("custom.dir", _customDir);
        return true;
    }

    /// <summary>
    /// Every <c>.mdx</c> under the custom folder, subfolders included: downloaded models usually
    /// arrive one per folder, with their textures beside them.
    /// </summary>
    private void ScanCustomDir()
    {
        if (_customDir is null) return;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
        };
        try
        {
            _customModels = Directory.EnumerateFiles(_customDir, "*.mdx", options)
                .Select(f => new Wc3ModelEntry
                {
                    CascName = "",
                    RelativePath = Path.GetRelativePath(_customDir, f),
                    ArtSet = Wc3ArtSet.Classic,          // unknown until read; the list says "custom"
                })
                .OrderBy(m => m.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
            Status.Text = $"{_customModels.Count:N0} custom model(s) in {_customDir}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _customModels = [];
            Status.Text = "Could not read the custom folder: " + ex.Message;
        }
    }

    private void OnAssetSelected(object sender, SelectionChangedEventArgs e)
    {
        // Several models selected is a batch to export, not a model to view: the viewer keeps what
        // it shows and the Export button counts the selection instead.
        UpdateExportButton();
        if (AssetList.SelectedItems.Count == 1 && AssetList.SelectedItem is AssetItem item)
            _ = item.FilePath is not null ? LoadLooseAsync(item.FilePath, fromBrowser: true) : LoadModelAsync(item.Entry);
    }

    private List<AssetItem> SelectedBatch() => AssetList.SelectedItems.OfType<AssetItem>().ToList();

    private void UpdateExportButton()
    {
        int n = AssetList.SelectedItems.Count;
        ExportButton.Content = n > 1 ? $"Export {n}…" : "Export…";
        ExportButton.IsEnabled = !_busy && (n > 1 ? _storage is not null : _exportable);
    }

    private async Task OpenStorageAsync()
    {
        if (_busy) return;
        string path = InstallPath.Text.Trim();
        if (!IsInstallPath(path))
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
            _cascTextures = new Wc3TextureCache(storage, index);
            RememberInstallPath(path);
            OpenFileButton.IsEnabled = true;
            ApplyFilter();
            Status.Text = $"Loaded {index.Models.Count:N0} models " +
                          $"({index.Models.Count(m => m.ArtSet == Wc3ArtSet.Definitive):N0} DE, " +
                          $"{index.Models.Count(m => m.ArtSet == Wc3ArtSet.Reforged):N0} HD, " +
                          $"{index.Models.Count(m => m.ArtSet == Wc3ArtSet.Classic):N0} SD) and " +
                          $"{index.TextureLookup.Count:N0} textures. " +
                          "Type to filter, click a model to view, or Open file… for a custom one.";
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
            1 => Wc3ArtSet.Definitive,
            2 => Wc3ArtSet.Reforged,
            3 => Wc3ArtSet.Classic,
            _ => (Wc3ArtSet?)null,
        };

        bool custom = ArtSetCombo.SelectedIndex == CustomSet && _customDir is not null;
        IEnumerable<Wc3ModelEntry> items = custom ? _customModels : _index.Models;
        if (wanted is not null) items = items.Where(m => m.ArtSet == wanted);
        if (q.Length > 0)
            items = items.Where(m => m.RelativePath.Contains(q, StringComparison.OrdinalIgnoreCase));

        AssetList.ItemsSource = items.Take(5000)
            .Select(m => new AssetItem(m, custom ? Path.Combine(_customDir!, m.RelativePath) : null)).ToList();
    }

    private async Task LoadModelAsync(Wc3ModelEntry entry)
    {
        if (_busy || _storage is null) return;

        SetBusy(true, "Loading " + entry.Name + " …");
        try
        {
            var storage = _storage;
            var model = await Task.Run(() =>
            {
                var m = MdxReader.Read(storage.ReadFile(entry.CascName));
                // PopcornFX effects live in .pkb bakes beside the model; resolve them here, with the
                // archive at hand, so the viewer and the exporter see the same stand-in emitters.
                PopcornApproximation.Attach(m, storage.TryReadFile, entry.CascName);
                return m;
            });
            PresentModel(entry, model, _cascTextures!);
        }
        catch (Exception ex)
        {
            ModelHost.Content = null;
            _exportable = false;
            Status.Text = "Could not load model: " + ex.Message;
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private void OnOpenFileClick(object sender, RoutedEventArgs e) => OpenLooseFile();

    /// <summary>
    /// Opens a loose .mdx from disk — custom models. Textures resolve from the model's own folder
    /// first (.blp/.dds beside the file); stock references fall through to the game install, which
    /// is the usual case, since most custom models ship only the art their author made and leave
    /// every borrowed Warcraft III texture as a bare path.
    /// </summary>
    private void OpenLooseFile()
    {
        // The button is disabled without one, so this is a guard rather than a path users reach.
        if (_busy || _storage is null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open a Warcraft III model",
            Filter = "Warcraft III models (*.mdx)|*.mdx|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        _ = LoadLooseAsync(dlg.FileName, fromBrowser: false);
    }

    /// <summary>
    /// Loads a model file from disk: one picked with Open file…, or one listed from the custom
    /// folder (<paramref name="fromBrowser"/>, which keeps the browser's selection).
    /// </summary>
    private async Task LoadLooseAsync(string path, bool fromBrowser)
    {
        if (_busy || _storage is null) return;
        SetBusy(true, "Loading " + Path.GetFileName(path) + " …");
        try
        {
            var storage = _storage;
            var model = await Task.Run(() =>
            {
                var m = MdxReader.Read(File.ReadAllBytes(path));
                PopcornApproximation.Attach(m, storage.TryReadFile, "");
                return m;
            });

            var entry = LooseEntry(path, model);
            var cache = LooseTextureCache(storage, _index, path, model);
            PresentModel(entry, model, cache);
            // The loose model is what Export now means, not a batch still selected in the browser.
            if (!fromBrowser) AssetList.UnselectAll();

            // PresentModel has already resolved every texture to draw the model, so the cache can
            // now say where each one came from. A custom model borrowing stock art is the normal
            // case, and the count is the user's assurance that the export will package it.
            Status.Text += "   —   " + DescribeTextureSources(cache, model);
        }
        catch (Exception ex)
        {
            ModelHost.Content = null;
            _exportable = false;
            Status.Text = "Could not load model: " + ex.Message;
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    /// <summary>
    /// CascName "" = no archive prefix: the texture cache probes LocalRoots, then the classic CASC
    /// tree for stock references like Textures\gutz.blp.
    /// </summary>
    private static Wc3ModelEntry LooseEntry(string path, MdxModel model) => new()
    {
        CascName = "",
        RelativePath = Path.GetFileName(path),
        ArtSet = model.IsReforged ? Wc3ArtSet.Reforged : Wc3ArtSet.Classic,
    };

    /// <summary>
    /// Textures for a model loaded from disk. The catalog is what lets a custom model's re-pathed
    /// reference (war3mapImported\x.blp, a bare file name, an absolute path off the author's desktop)
    /// still find the stock texture it means — a loose model has no archive prefix to anchor a guess to.
    /// </summary>
    private static Wc3TextureCache LooseTextureCache(Wc3Storage storage, Wc3AssetIndex? index, string path, MdxModel model)
    {
        var cache = new Wc3TextureCache(storage, index) { PreferHd = model.IsReforged };
        string dir = Path.GetDirectoryName(path) ?? "";
        if (dir.Length > 0)
        {
            cache.LocalRoots.Add(dir);
            // Common layouts for downloaded models: textures one level up or in a subfolder.
            if (Directory.Exists(Path.Combine(dir, "Textures"))) cache.LocalRoots.Add(Path.Combine(dir, "Textures"));
            if (Path.GetDirectoryName(dir) is { Length: > 0 } parent) cache.LocalRoots.Add(parent);
        }
        return cache;
    }

    /// <summary>
    /// Summarises where a loose model's textures came from, for the status bar. Textures taken from
    /// the game install are called out because they are the ones the user did not supply and might
    /// not expect to be exported — and because a count of zero on a model that clearly borrows stock
    /// art means the install is not open.
    /// </summary>
    private string DescribeTextureSources(Wc3TextureCache cache, MdxModel model)
    {
        var p = cache.ProvenanceOf(model, "", ViewerTeamColor);

        var parts = new List<string>();
        if (p.BesideModel > 0) parts.Add($"{p.BesideModel} beside the model");
        if (p.FromGameInstall > 0) parts.Add($"{p.FromGameInstall} from the game install");
        if (parts.Count == 0) parts.Add("none resolved");
        if (p.Missing.Count > 0) parts.Add($"{p.Missing.Count} missing");
        return "textures: " + string.Join(", ", parts);
    }

    private void PresentModel(Wc3ModelEntry entry, MdxModel model, Wc3TextureCache textures)
    {
        _entry = entry;
        _model = model;
        _textures = textures;
        _animator = new MdxAnimator(model);
        _cutoutCache.Clear();
        ClearAnimationUi();

        var effects = new EffectLayer(model, textures, entry.CascName) { PlayerSlot = ViewerTeamColor };
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
        // After RebuildScene: the panel reports where each texture actually came from, which the
        // cache only knows once something has asked it to resolve them.
        PopulateTexturePanel();
        _exportable = true;
        UpdateExportButton();

        var shown = _geosetItems.Where(i => i.IsVisible).Select(i => i.Geoset).ToList();
        int hidden = _geosetItems.Count - shown.Count;
        string hiddenNote = hidden > 0 ? $" (+{hidden} hidden)" : "";
        // Effects are called out per model because their absence is otherwise unexplainable: a
        // Reforged HD model's effects are almost always PopcornFX, run here from their bakes' own
        // compiled scripts, and one whose bake did not resolve leaves the viewport simply empty.
        // Saying so beats leaving the user to wonder whether something is broken.
        var fx = new List<string>();
        int popcornLayers = model.ParticleEmitters.Count(e => e.IsPopcorn);
        int native = model.ParticleEmitters.Count - popcornLayers;
        if (native > 0) fx.Add($"{native} particle");
        if (model.RibbonEmitters.Count > 0) fx.Add($"{model.RibbonEmitters.Count} ribbon");
        if (model.Lights.Count > 0) fx.Add($"{model.Lights.Count} light");
        if (model.PopcornEmitters.Count > 0)
        {
            int unresolved = model.PopcornEmitters.Count(c => c.Effect is null);
            int running = model.PopcornEmitters.Count(c => c.Runtime is not null);
            fx.Add(running > 0
                ? $"{model.PopcornEmitters.Count} PopcornFX ({running} simulated from its scripts)"
                : popcornLayers > 0 ? $"{model.PopcornEmitters.Count} PopcornFX (approximated as {popcornLayers} layer{(popcornLayers == 1 ? "" : "s")})"
                : unresolved > 0 ? $"{model.PopcornEmitters.Count} PopcornFX (bake not found — not shown)"
                                 : $"{model.PopcornEmitters.Count} PopcornFX (no drawable layers)");
        }
        string effectNote = fx.Count > 0 ? $", effects: {string.Join(" + ", fx)}" : "";

        Status.Text = $"{entry.RelativePath}   —   {entry.ArtSet}, {shown.Count} geosets{hiddenNote}, " +
                      $"{shown.Sum(g => g.VertexCount):N0} verts, {shown.Sum(g => g.TriangleCount):N0} tris, " +
                      $"{model.Sequences.Count} sequences{effectNote}";
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

    // ---------------- Texture panel ----------------

    /// <summary>
    /// One TEXS reference as the panel shows it: what the model asked for, what answered it, and
    /// how confident that answer is.
    /// </summary>
    private sealed class TextureItem
    {
        public required MdxTexture Texture { get; init; }
        public required string Reference { get; init; }
        public required string Name { get; init; }
        public required string SourceLabel { get; init; }
        public required string From { get; init; }
        public required Brush NameBrush { get; init; }
        public required Brush SourceBrush { get; init; }
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Rebuilds the texture list from what the cache actually resolved, and raises the banner when
    /// anything is still unmatched. Call after every load and after every remap.
    /// </summary>
    private void PopulateTexturePanel()
    {
        if (_model is null || _textures is null || _entry is null)
        {
            TextureList.ItemsSource = null;
            TextureWarning.Visibility = Visibility.Collapsed;
            return;
        }

        var items = new List<TextureItem>();
        var missing = new List<string>();
        int chosen = 0;

        foreach (var tex in _model.Textures)
        {
            // Replaceable slots are generated, not files; there is nothing to map.
            if (tex.IsTeamColor || tex.IsTeamGlow || tex.FileName.Length == 0) continue;

            // Resolve it here rather than trusting what the scene happened to need: RebuildScene
            // only composes the materials of the geosets currently visible at the current LOD, so a
            // texture belonging to a hidden geoset would never have been attempted and would report
            // as missing. Load is cached, so asking costs nothing the second time.
            _ = _textures.Load(_entry.CascName, tex, ViewerTeamColor);
            var origin = _textures.OriginOf(_entry.CascName, tex);
            string leaf = System.IO.Path.GetFileName(tex.FileName.Replace('/', '\\'));

            string label;
            Brush nameBrush = Frozen(0xCC, 0xCC, 0xCC), sourceBrush = Frozen(0x7A, 0x88, 0x92);
            if (origin is null)
            {
                label = "not found — click … to map it";
                nameBrush = Frozen(0xFF, 0xA0, 0x70);
                sourceBrush = Frozen(0xFF, 0xA0, 0x70);
                missing.Add(leaf);
            }
            else
            {
                var o = origin.Value;
                label = o.Source switch
                {
                    TextureSource.ChosenByUser => "mapped by you",
                    TextureSource.BesideModel => "beside the model",
                    TextureSource.BesideModelByName => "found by name near the model",
                    TextureSource.GameInstall => "game install",
                    _ => o.Alternatives > 1
                        ? $"game install, by name ({o.Alternatives} candidates)"
                        : "game install, by name",
                };
                if (o.Source == TextureSource.ChosenByUser) sourceBrush = Frozen(0x8A, 0xD0, 0x8A);
                else if (o.IsAmbiguous) sourceBrush = Frozen(0xD8, 0xC0, 0x70);
                if (o.Source == TextureSource.ChosenByUser) chosen++;
            }

            items.Add(new TextureItem
            {
                Texture = tex, Reference = tex.FileName, Name = leaf,
                SourceLabel = label, From = origin?.From ?? tex.FileName,
                NameBrush = nameBrush, SourceBrush = sourceBrush,
            });
        }

        TextureList.ItemsSource = items;
        TextureSummary.Text = $"{items.Count} texture(s)"
                              + (chosen > 0 ? $", {chosen} mapped by you" : "")
                              + (missing.Count > 0 ? $", {missing.Count} missing" : "");

        TextureWarning.Visibility = missing.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (missing.Count > 0)
            TextureWarningText.Text = missing.Count == 1
                ? $"1 texture could not be found: {missing[0]} — it draws as magenta until you map it."
                : $"{missing.Count} textures could not be found: {string.Join(", ", missing.Take(4))}"
                  + (missing.Count > 4 ? $" and {missing.Count - 4} more" : "")
                  + " — they draw as magenta until you map them.";
    }

    /// <summary>The banner is a shortcut to the panel that can fix what it is complaining about.</summary>
    private void OnTextureWarningClick(object sender, MouseButtonEventArgs e) => SidePanel.SelectedIndex = 1;

    private void OnPickTexture(object sender, RoutedEventArgs e)
    {
        if (_textures is null || sender is not Button { Tag: TextureItem item }) return;

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a texture for " + item.Name,
            Filter = "Textures (*.blp;*.dds;*.tga)|*.blp;*.dds;*.tga|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;

        _textures.SetOverride(item.Reference, dlg.FileName);
        ReloadTextures();
    }

    private void OnClearTextureOverrides(object sender, RoutedEventArgs e)
    {
        if (_textures is null || _textures.Overrides.Count == 0) return;
        _textures.ClearOverrides();
        ReloadTextures();
    }

    /// <summary>
    /// Redraws with the current mapping. The composited materials and the cutout classification are
    /// both derived from the pixels, so both caches have to go, not just the scene.
    /// </summary>
    private void ReloadTextures()
    {
        _cutoutCache.Clear();
        RebuildScene();
        PopulateTexturePanel();
    }

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
                                                       _textures, _entry.CascName, ViewerTeamColor);

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
        if (zoom)
        {
            // An effect-only model (every Reforged spell effect) has no mesh to frame, and its
            // emitters are empty until the animation runs, so frame the volume they will fill.
            if (_sceneMeshes.Count == 0 && _effects is not null && EffectExtent(_model) is { } box) FrameBox(box);
            else Viewport.ZoomExtents(0);
        }
        ApplyPose();   // rebuilt meshes start at bind pose; re-apply the active frame, if any
    }

    /// <summary>
    /// Points the camera at a box so its tallest side fills most of the view. Helix's own
    /// ZoomExtents fits the box's bounding sphere and leaves a tall, thin effect (a beam) as a sliver.
    /// </summary>
    private void FrameBox(Rect3D box)
    {
        if (Viewport.Camera is not PerspectiveCamera cam) { Viewport.ZoomExtents(box, 0); return; }
        var centre = new Point3D(box.X + box.SizeX / 2, box.Y + box.SizeY / 2, box.Z + box.SizeZ / 2);
        // Keep the heading but look from low down: spell effects are mostly vertical (beams,
        // pillars, rising sparks), and the default steep view foreshortens them to a sliver.
        var flat = new Vector3D(cam.LookDirection.X, cam.LookDirection.Y, 0);
        if (flat.LengthSquared < 1e-9) flat = new Vector3D(0, 1, 0);
        flat.Normalize();
        var dir = new Vector3D(flat.X, flat.Y, -0.27);
        dir.Normalize();
        double extent = Math.Max(box.SizeZ, Math.Max(box.SizeX, box.SizeY));
        double dist = extent * 0.6 / Math.Tan(cam.FieldOfView * Math.PI / 360) + Math.Max(box.SizeX, box.SizeY) * 0.5;
        cam.Position = centre - dir * dist;
        cam.LookDirection = dir * dist;
        cam.NearPlaneDistance = Math.Max(0.1, dist / 1000);
    }

    /// <summary>The box a model's emitters can reach: each node's pivot grown by card size plus travel.</summary>
    private static Rect3D? EffectExtent(MdxModel model)
    {
        Rect3D box = Rect3D.Empty;
        // A PopcornFX effect is framed from where its sprites actually go in a short headless run;
        // its fixed-field stand-ins are hidden and would frame the wrong thing.
        foreach (var corn in model.PopcornEmitters)
        {
            if (corn.Runtime is null || (uint)corn.NodeIndex >= (uint)model.Nodes.Count) continue;
            if (Wc3ModelViewer.Core.Formats.Popcorn.PkCornPlayer.EstimateBounds(corn.Runtime) is not var (lo, hi)) continue;
            const float m = PopcornApproximation.MetresToWc3;
            var p = model.Nodes[corn.NodeIndex].Pivot;
            box.Union(new Rect3D(p.X + lo.X * m, p.Y + lo.Y * m, p.Z + lo.Z * m, (hi.X - lo.X) * m, (hi.Y - lo.Y) * m, (hi.Z - lo.Z) * m));
        }
        foreach (var e in model.ParticleEmitters)
        {
            if (e.IsPopcorn && model.PopcornEmitters.Any(c => c.Runtime is not null && c.NodeIndex == e.NodeIndex)) continue;
            if ((uint)e.NodeIndex >= (uint)model.Nodes.Count) continue;
            var p = model.Nodes[e.NodeIndex].Pivot;
            // Cards are drawn centred, so half a size; travel counts to the colour peak, where the
            // effect is brightest, because a faded tail should not push the camera away. A
            // fixed-length beam has its whole length from birth. Emission is a cone about the
            // node's +Z opened by the latitude, so a narrow cone only reaches upward while a wide
            // one (a scatter) reaches every way; framing the beam as a cube would put the camera
            // twice as far back as the effect needs.
            float half = Math.Clamp(0.5f * Math.Max(Math.Max(e.StartScale, e.MiddleScale), e.EndScale), 10f, 3000f);
            float travel = Math.Clamp(Math.Max(e.Speed * Math.Max(e.Life, 0.05f) * Math.Clamp(e.MiddleTime, 0.25f, 1f), e.BeamLength), 0f, 3000f);
            float sideways = e.Latitude >= 45f ? travel : 0f;
            float down = e.Latitude >= 90f ? travel : 0f;
            box.Union(new Rect3D(p.X - half - sideways, p.Y - half - sideways, p.Z - half - down,
                                 2 * (half + sideways), 2 * (half + sideways), 2 * half + down + travel));
        }
        foreach (var r in model.RibbonEmitters)
        {
            if ((uint)r.NodeIndex >= (uint)model.Nodes.Count) continue;
            var p = model.Nodes[r.NodeIndex].Pivot;
            float reach = Math.Clamp(r.HeightAbove + r.HeightBelow, 10f, 3000f);
            box.Union(new Rect3D(p.X - reach, p.Y - reach, p.Z - reach, 2 * reach, 2 * reach, 2 * reach));
        }
        return box.IsEmpty ? null : box;
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
    private int ViewerTeamColor;

    private void OnTeamColorChanged(object sender, SelectionChangedEventArgs e)
    {
        // Read the slot off the sender: WPF raises this while InitializeComponent is still running,
        // before the x:Name field is assigned.
        int slot = Math.Max((sender as ComboBox)?.SelectedIndex ?? 0, 0);
        if (slot == ViewerTeamColor) return;
        ViewerTeamColor = slot;
        if (_effects is not null) _effects.PlayerSlot = slot;
        if (_suppressRebuild || _model is null) return;
        RebuildScene();
    }

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

        // No additive blend either. What defines an additive layer is that black adds nothing, so
        // carry brightness in the alpha channel: the brightest texels stay solid and glow, and the
        // black field an effect card is mostly made of becomes transparent instead of a rectangle.
        // WC3's Additive mode ignores the texture's own alpha, so overwriting it here loses nothing.
        if (composite.Blend == CompositeBlend.Additive)
        {
            pixels = (byte[])pixels.Clone();
            for (int i = 0; i < pixels.Length; i += 4)
                pixels[i + 3] = Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2]));
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
        // An additive card is pure emission and nothing else. Giving it the opaque black
        // DiffuseMaterial that an unshaded SURFACE needs is what made every glow, aura and spell
        // card draw as a solid black or team-coloured rectangle standing through the model: the
        // black diffuse is fully opaque, so the quad occluded everything behind it.
        //
        // An unshaded surface still needs that black matte under its emission, but the matte must
        // be the texture brush multiplied to black, not a solid black brush: only then does it carry
        // the texture's alpha and the brush Opacity that GEOA drives. A solid matte ignored both, so
        // ClericMissile's sword, faded to 0, stayed a pitch-black silhouette (MdxProbe --wpfblend).
        Material material = composite.Blend == CompositeBlend.Additive
            ? new EmissiveMaterial(brush)
            : composite.Unshaded
                ? new MaterialGroup { Children = {
                      new DiffuseMaterial(brush) { Color = Colors.Black, AmbientColor = Colors.Black },
                      new EmissiveMaterial(brush) } }
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
            RestoreRestPose();
            ApplyPose();          // back to rest pose, with billboards facing the camera
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
            // The effects start over too: a player still holding the finished run's instances sees
            // no gate edge and would never spawn a fresh one.
            if (LoopCheck.IsChecked != true && _timeMs >= _sequence.DurationMs)
            {
                _timeMs = 0;
                _effects?.Reset();
            }
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
        if (_sequence is null || !_playing)
        {
            _lastTick = _clock.Elapsed;
            // A paused or unanimated model still has to turn its billboards as the camera orbits.
            if (_animator is { HasBillboards: true } && CameraBasis() != _poseCamera) ApplyPose();
            return;
        }
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
        var camPos = new Point3D(0, -1000, 500);
        if (Viewport.Camera is ProjectionCamera cam) { look = cam.LookDirection; camUp = cam.UpDirection; camPos = cam.Position; }

        var right = Vector3D.CrossProduct(look, camUp);
        if (right.LengthSquared < 1e-9) right = new Vector3D(1, 0, 0); else right.Normalize();
        var up = Vector3D.CrossProduct(right, look);
        if (up.LengthSquared < 1e-9) up = new Vector3D(0, 0, 1); else up.Normalize();

        int t = _sequence.IntervalStart + (int)_timeMs;
        _effects.Update(dt * (float)SpeedSlider.Value, _animator, _sequence, t, _wallMs,
                        new Vector3((float)right.X, (float)right.Y, (float)right.Z),
                        new Vector3((float)up.X, (float)up.Y, (float)up.Z),
                        new Vector3((float)camPos.X, (float)camPos.Y, (float)camPos.Z));
    }

    /// <summary>Applies the current time to all meshes: skinning plus GEOA geoset visibility.</summary>
    private void ApplyPose()
    {
        if (_model is null || _animator is null) return;
        if (_sequence is null && _sceneMeshes.Count == 0) return;

        int t = _sequence is null ? 0 : _sequence.IntervalStart + (int)_timeMs;
        _animator.Camera = CameraBasis();
        _poseCamera = _animator.Camera;
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
            // Rest pose: geometry is already correct — unless a billboard has to face the camera.
            if (_sequence is null && !_animator.HasBillboards) continue;

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

    private (System.Numerics.Vector3 Look, System.Numerics.Vector3 Up)? _poseCamera;

    /// <summary>The viewport camera's look and up vectors — the view billboarded nodes turn to face.</summary>
    private (System.Numerics.Vector3 Look, System.Numerics.Vector3 Up)? CameraBasis()
    {
        if (Viewport.Camera is not ProjectionCamera cam) return null;
        var look = cam.LookDirection;
        var up = cam.UpDirection;
        return (new System.Numerics.Vector3((float)look.X, (float)look.Y, (float)look.Z),
                new System.Numerics.Vector3((float)up.X, (float)up.Y, (float)up.Z));
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
        if (_busy) return;
        var batch = SelectedBatch();
        if (batch.Count > 1)
        {
            var bdlg = new ExportDialog(batch.Select(i => i.ToString()).ToList()) { Owner = this };
            if (bdlg.ShowDialog() == true)
                _ = RunBatchExportAsync(batch, bdlg);
            return;
        }
        if (_model is null || _entry is null) return;
        var visible = _geosetItems.Where(i => i.IsVisible).Select(i => i.Geoset.Index).ToHashSet();
        var dlg = new ExportDialog(_model, _entry, _lod, visible) { Owner = this };
        if (dlg.ShowDialog() == true)
            _ = RunExportAsync(dlg.Options, dlg.OutputDir, dlg.ExportGltf, dlg.ExportM3, dlg.Layout);
    }

    private async Task RunExportAsync(M3ExportOptions options, string outDir, bool doGltf, bool doM3, ExportLayout layout)
    {
        if (_model is null || _entry is null || _textures is null) return;
        SetBusy(true, "Exporting…");
        try
        {
            var model = _model;
            var entry = _entry;
            var textures = _textures;
            var r = await Task.Run(() => ExportWriter.Write(model, entry.CascName, textures, options,
                                                            outDir, doGltf, doM3, layout));

            string reused = r.TexturesReused > 0 ? $" ({r.TexturesReused} texture(s) were already there)" : "";
            Status.Text = $"Exported {r.Files} file(s){reused}, {r.Bytes / 1048576.0:0.0} MB, to {r.Location}" +
                          (doM3 ? $"   —   {r.Note}"
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

    /// <summary>
    /// Exports every model selected in the browser with one set of options. Each is read and
    /// converted in turn, off the UI thread; one that fails is logged and skipped rather than
    /// ending the run, and <c>export-log.txt</c> in the output folder keeps each model's report,
    /// which a single status line cannot hold for fifty models.
    /// </summary>
    private async Task RunBatchExportAsync(IReadOnlyList<AssetItem> batch, ExportDialog dlg)
    {
        if (_storage is null || _index is null || _cascTextures is null) return;
        SetBusy(true, $"Exporting {batch.Count} models…");
        var storage = _storage;
        var index = _index;
        var overrides = _cascTextures.Overrides.ToList();
        string outDir = dlg.OutputDir;
        bool doGltf = dlg.ExportGltf, doM3 = dlg.ExportM3;
        var layout = dlg.Layout;
        var optionsFor = dlg.OptionsFor;
        IProgress<string> progress = new Progress<string>(s => Status.Text = s);
        try
        {
            var (ok, failed, warned, files, reused, bytes, logPath) = await Task.Run(() =>
            {
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var log = new System.Text.StringBuilder();
                log.AppendLine($"Batch export, {DateTime.Now:yyyy-MM-dd HH:mm}, {batch.Count} models, "
                               + (layout == ExportLayout.SharedAssets ? "shared Assets folder" : "one folder per model"));
                int ok = 0, warned = 0, files = 0, reused = 0;
                long bytes = 0;
                var failed = new List<string>();
                for (int i = 0; i < batch.Count; i++)
                {
                    var item = batch[i];
                    var entry = item.Entry;
                    // Stock names repeat across art sets, custom ones across download folders; the
                    // suffix says which copy a renamed model is.
                    string name = ExportWriter.UniqueName(entry.Name, item.FilePath is null
                        ? entry.ArtSet switch { Wc3ArtSet.Definitive => "_de", Wc3ArtSet.Reforged => "_hd", _ => "_sd" }
                        : "_" + Path.GetFileName(Path.GetDirectoryName(item.FilePath)), used);
                    string source = item.FilePath ?? entry.CascName;
                    progress.Report($"Exporting {i + 1} of {batch.Count}: {entry.RelativePath} …");
                    try
                    {
                        MdxModel model;
                        Wc3TextureCache textures;
                        // A cache per model: one keeps every decoded image, and fifty HD models'
                        // 2048² sets would run to gigabytes. The user's texture mappings carry over.
                        if (item.FilePath is not null)
                        {
                            model = MdxReader.Read(File.ReadAllBytes(item.FilePath));
                            PopcornApproximation.Attach(model, storage.TryReadFile, "");
                            textures = LooseTextureCache(storage, index, item.FilePath, model);
                        }
                        else
                        {
                            model = MdxReader.Read(storage.ReadFile(entry.CascName));
                            PopcornApproximation.Attach(model, storage.TryReadFile, entry.CascName);
                            textures = new Wc3TextureCache(storage, index);
                        }
                        foreach (var (reference, file) in overrides) textures.SetOverride(reference, file);
                        var r = ExportWriter.Write(model, entry.CascName, textures, optionsFor(name),
                                                   outDir, doGltf, doM3, layout);
                        ok++;
                        if (r.Warned) warned++;
                        files += r.Files;
                        reused += r.TexturesReused;
                        bytes += r.Bytes;
                        log.AppendLine($"{name}  <-  {source}: {r.Files} file(s)" +
                                       (r.Note.Length > 0 ? "   —   " + r.Note : ""));
                    }
                    catch (Exception ex)
                    {
                        failed.Add(entry.Name);
                        log.AppendLine($"{name}  <-  {source}: FAILED — {ex.Message}");
                    }
                }
                Directory.CreateDirectory(outDir);
                string logPath = Path.Combine(outDir, "export-log.txt");
                File.WriteAllText(logPath, log.ToString());
                return (ok, failed, warned, files, reused, bytes, logPath);
            });

            string text = $"Exported {ok} of {batch.Count} models — {files} file(s), {bytes / 1048576.0:0.0} MB, to {outDir}";
            if (reused > 0) text += $"; {reused} shared texture(s) were written only once";
            if (warned > 0) text += $"   —   {warned} with texture warnings";
            if (failed.Count > 0) text += $"   —   FAILED: {string.Join(", ", failed.Take(4))}" +
                                          (failed.Count > 4 ? $" (+{failed.Count - 4} more)" : "");
            Status.Text = text + $"   —   details in {Path.GetFileName(logPath)}";
        }
        catch (Exception ex)
        {
            Status.Text = "Batch export failed: " + ex.Message;
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
        // Stays disabled while no install is open — see the button's XAML.
        OpenFileButton.IsEnabled = !busy && _storage is not null;
        UpdateExportButton();
        if (status is not null) Status.Text = status;
        Mouse.OverrideCursor = busy ? Cursors.Wait : null;
    }

    protected override void OnClosed(EventArgs e)
    {
        CompositionTarget.Rendering -= OnFrameTick;
        _storage?.Dispose();
        base.OnClosed(e);
    }

    /// <summary>A browser row: an install model, or a file in the custom folder (<see cref="FilePath"/>).</summary>
    private sealed class AssetItem(Wc3ModelEntry entry, string? filePath = null)
    {
        public Wc3ModelEntry Entry { get; } = entry;
        public string? FilePath { get; } = filePath;
        public override string ToString()
            => FilePath is not null
                ? $"{Entry.Name}  · custom" + (Entry.Folder.Length > 0 ? $"   ({Entry.Folder})" : "")
                : $"{Entry.Name}  · {Entry.ArtSet switch { Wc3ArtSet.Definitive => "DE", Wc3ArtSet.Reforged => "HD", _ => "SD" }}   ({Entry.Folder})";
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

using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using Wc3ModelViewer.Core.Casc;
using Wc3ModelViewer.Core.Convert;
using Wc3ModelViewer.Core.Formats;

namespace Wc3ModelViewer;

/// <summary>
/// Collects export options and closes with <c>DialogResult = true</c>; the main window runs the
/// export and reports progress in its status bar — the same flow as the D3 viewer.
/// </summary>
public partial class ExportDialog : Window
{
    private readonly MdxModel? _model;                   // null in a batch
    private readonly Wc3ModelEntry? _entry;
    private readonly HashSet<int> _visibleGeosets = [];
    private readonly List<SequenceRow> _rows = [];
    private readonly IReadOnlyList<string>? _batch;      // the batch's models, as the browser lists them

    /// <summary>Valid after the dialog closes with true.</summary>
    public M3ExportOptions Options { get; private set; } = new();

    /// <summary>
    /// The same options under another model name, for a batch — which names each model as it
    /// writes it. Valid after the dialog closes with true.
    /// </summary>
    public Func<string, M3ExportOptions> OptionsFor { get; private set; } = _ => new();
    public string OutputDir { get; private set; } = "";
    public bool ExportGltf { get; private set; } = true;
    public bool ExportM3 { get; private set; }
    public ExportLayout Layout { get; private set; }

    /// <summary>
    /// A batch export: every model selected in the browser, with one set of options. What is a
    /// property of one model — its sequence list, LOD choice and visible geosets — cannot be picked
    /// per model here, so a batch exports every geoset at full detail with all sequences or none.
    /// </summary>
    public ExportDialog(IReadOnlyList<string> batch)
    {
        InitializeComponent();
        _batch = batch;
        SequencePanel.Visibility = Visibility.Collapsed;
        BatchPanel.Visibility = Visibility.Visible;
        BatchTitle.Text = $"Models to export ({batch.Count})";
        BatchList.ItemsSource = batch;
        BatchSeqCombo.SelectedIndex = Clamp(UserSettings.GetInt("export.batchseq", 0), BatchSeqCombo.Items.Count);
        LodCombo.ItemsSource = new[] { "LOD 0 (full)" };
        LodCombo.SelectedIndex = 0;
        LodCombo.IsEnabled = false;
        VisibleOnlyCheck.IsChecked = false;
        VisibleOnlyCheck.IsEnabled = false;
        ModelNameRow.Visibility = Visibility.Collapsed;
        RestoreSettings();
        Title = $"Export {batch.Count} models (.m3)";
    }

    public ExportDialog(MdxModel model, Wc3ModelEntry entry, int viewerLod, HashSet<int> visibleGeosets)
    {
        InitializeComponent();
        _model = model;
        _entry = entry;
        _visibleGeosets = visibleGeosets;

        // The plan already drops exact copies and renumbers shared names, so each row is one
        // distinct animation with a unique default name.
        _rows = M3Exporter.PlanSequences(model)
            .Select(p => new SequenceRow(p.Index, $"{p.Sequence.Name}  ({p.Sequence.DurationMs / 1000.0:0.0}s)",
                                         p.Name))
            .ToList();
        SequenceChecks.ItemsSource = _rows;

        // Plenty of doodads and buildings carry no animation at all, and any model can be exported
        // static by unticking everything — both write one empty Stand and the rest pose.
        SeqHint.Text = _rows.Count == 0
            ? "This model has no animations — it exports static."
            : "None selected = static export.";

        var lods = model.LodLevels;
        LodCombo.ItemsSource = lods.Select(l => l == 0 ? "LOD 0 (full)" : $"LOD {l}").ToList();
        LodCombo.SelectedIndex = Math.Max(0, lods.IndexOf(viewerLod));
        ModelNameBox.Text = entry.Name;

        RestoreSettings();
        VisibleOnlyCheck.IsChecked = UserSettings.GetBool("export.visibleonly", false);
        Title = $"Export {entry.Name} (.m3)";
    }

    private void RestoreSettings()
    {
        // Everything that means the same thing for the next model comes back as it was left —
        // porting a collection otherwise means retyping the scale on every single export. LOD and
        // the geoset/sequence selections are properties of *this* model and are not restored.
        //
        // The scale box used to be in native Warcraft III units and is now in StarCraft II's, so a
        // value an older build saved means something else. Convert it once — a stored 0.025 was
        // asking for SC2 scale and becomes 1.0 — and mark the store, or the remembered number would
        // silently come back forty times too small.
        if (UserSettings.Get("export.scaleunit").Length == 0 && UserSettings.Get("export.scale").Length > 0)
            UserSettings.SetMany(
                ("export.scale", (UserSettings.GetFloat("export.scale", M3ExportOptions.Sc2UnitsPerWc3Unit)
                                  / M3ExportOptions.Sc2UnitsPerWc3Unit)
                                 .ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)),
                ("export.scaleunit", "sc2"));
        ScaleBox.Text = UserSettings.Get("export.scale", "1.0");
        // Both layouts build below this root (a <ModelName> subfolder, or one shared Assets\),
        // so the default here is just the collection root.
        OutDirBox.Text = UserSettings.Get("export.outdir", Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Wc3Exports"));
        M3Check.IsChecked = UserSettings.GetBool("export.m3", true);
        GltfCheck.IsChecked = UserSettings.GetBool("export.gltf", false);
        ConvertPbrCheck.IsChecked = UserSettings.GetBool("export.convertpbr", true);
        GeosetVisCheck.IsChecked = UserSettings.GetBool("export.geoavis", true);
        ReduceKeysCheck.IsChecked = UserSettings.GetBool("export.reducekeys", true);
        TeamColorCombo.SelectedIndex = Clamp(UserSettings.GetInt("export.teamcolor", 0), TeamColorCombo.Items.Count);
        TexSizeCombo.SelectedIndex = Clamp(UserSettings.GetInt("export.texsize", 2), TexSizeCombo.Items.Count);
        BakeFxCheck.IsChecked = UserSettings.GetBool("export.bakefx", true);
        FxAtlasCombo.SelectedIndex = Clamp(UserSettings.GetInt("export.fxatlas", 0), FxAtlasCombo.Items.Count);
        LayoutCombo.SelectedIndex = Clamp(UserSettings.GetInt("export.layout", 0), LayoutCombo.Items.Count);
        UpdateLayoutHint();
    }

    private void OnOutputChanged(object sender, RoutedEventArgs e) => UpdateLayoutHint();

    /// <summary>Spells out where the files will land, since the two layouts differ only in that.</summary>
    private void UpdateLayoutHint()
    {
        // Fires from InitializeComponent, before the named elements below the combo exist.
        if (LayoutHint is null || OutDirBox is null || ModelNameBox is null) return;
        string root = OutDirBox.Text.Trim();
        string name = _batch is null ? ModelNameBox.Text.Trim() : "<model>";
        if (root.Length == 0 || name.Length == 0) { LayoutHint.Text = ""; return; }
        LayoutHint.Text = LayoutCombo.SelectedIndex == 1
            ? $"→ {Path.Combine(root, "Assets", name + ".m3")}\n   textures in {Path.Combine(root, "Assets", "textures")}\\ — shared by every model"
            : $"→ {Path.Combine(root, name, "Assets", name + ".m3")}\n   textures in {Path.Combine(root, name, "Assets", "textures")}\\";
    }

    private void OnAllSeqClick(object sender, RoutedEventArgs e)
    {
        foreach (var r in _rows) r.IsChecked = true;
    }

    private void OnNoneSeqClick(object sender, RoutedEventArgs e)
    {
        foreach (var r in _rows) r.IsChecked = false;
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the export folder" };
        if (dlg.ShowDialog(this) == true) OutDirBox.Text = dlg.FolderName;
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (GltfCheck.IsChecked != true && M3Check.IsChecked != true)
        {
            MessageBox.Show(this, "Pick at least one format.", "Export",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        // No sequence selected is a valid export, not an error: the model goes out static, in its
        // rest pose, under one empty Stand — the only way to export a model that was never
        // animated, and the way to drop the animation from one that was.
        var selected = _rows.Where(r => r.IsChecked).ToList();
        // The box is in StarCraft II's scale, because that is where every model exported by this
        // tool is going: 1.0 comes out the size SC2's own art is built at. The exporter underneath
        // still counts in Warcraft III units — its key tolerance and the deviation it reports are
        // quoted in them — so the conversion happens here, once.
        if (!float.TryParse(ScaleBox.Text.Trim(), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out float typedScale)
            || !float.IsFinite(typedScale) || typedScale <= 0)
        {
            MessageBox.Show(this, "Scale must be a positive number.", "Export",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        float scale = typedScale * M3ExportOptions.Sc2UnitsPerWc3Unit;
        if (OutDirBox.Text.Trim().Length == 0)
        {
            MessageBox.Show(this, "Pick an output folder.", "Export",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string modelName = _batch is null ? ModelNameBox.Text.Trim() : "";
        if (_batch is null && (modelName.Length == 0 || modelName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            MessageBox.Show(this, "The model name must be a valid file name.", "Export",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var lods = _model?.LodLevels ?? [0];
        int lod = LodCombo.SelectedIndex >= 0 && LodCombo.SelectedIndex < lods.Count
            ? lods[LodCombo.SelectedIndex] : 0;
        // A batch has no per-model sequence list: all of them (null) or none (static).
        HashSet<int>? sequences = _batch is not null
            ? (BatchSeqCombo.SelectedIndex == 1 ? [] : null)
            : selected.Select(r => r.Index).ToHashSet();

        // Read out of the controls now: a batch calls OptionsFor from its worker thread.
        var sequenceNames = selected
            .Where(r => r.ExportName.Trim().Length > 0)
            .ToDictionary(r => r.Index, r => r.ExportName.Trim());
        int teamColor = TeamColorCombo.SelectedIndex;
        bool convertPbr = ConvertPbrCheck.IsChecked == true;
        bool geosetVis = GeosetVisCheck.IsChecked == true;
        var geosets = VisibleOnlyCheck.IsChecked == true ? _visibleGeosets : null;
        bool reduceKeys = ReduceKeysCheck.IsChecked == true;
        int maxTex = TexSizeCombo.SelectedIndex switch { 1 => 2048, 2 => 1024, 3 => 512, _ => 0 };
        bool bakeFx = BakeFxCheck.IsChecked == true;
        int fxAtlas = FxAtlasCombo.SelectedIndex switch { 1 => 1024, 2 => 2048, _ => 0 };
        OptionsFor = name => new M3ExportOptions
        {
            Sequences = sequences,
            SequenceNames = sequenceNames,
            Lod = lod,
            Scale = scale,
            TeamColor = teamColor,
            ConvertPbr = convertPbr,
            GeosetVisibility = geosetVis,
            Geosets = geosets,
            ReduceKeys = reduceKeys,
            MaxTextureSize = maxTex,
            BakeEffects = bakeFx,
            ImpostorAtlasSize = fxAtlas,
            ModelName = name,
        };
        // A batch names each model as it goes — see ExportWriter.UniqueName.
        Options = OptionsFor(_batch is null ? modelName : "Model");
        OutputDir = OutDirBox.Text.Trim();
        ExportGltf = GltfCheck.IsChecked == true;
        ExportM3 = M3Check.IsChecked == true;
        Layout = LayoutCombo.SelectedIndex == 1 ? ExportLayout.SharedAssets : ExportLayout.FolderPerModel;

        // Remembered on a real export only: a cancelled dialog was not a decision.
        UserSettings.SetMany(
            ("export.scale", ScaleBox.Text.Trim()),
            ("export.scaleunit", "sc2"),
            ("export.outdir", OutputDir),
            ("export.m3", ExportM3 ? "1" : "0"),
            ("export.gltf", ExportGltf ? "1" : "0"),
            ("export.convertpbr", ConvertPbrCheck.IsChecked == true ? "1" : "0"),
            ("export.geoavis", GeosetVisCheck.IsChecked == true ? "1" : "0"),
            ("export.reducekeys", ReduceKeysCheck.IsChecked == true ? "1" : "0"),
            ("export.layout", LayoutCombo.SelectedIndex.ToString()),
            ("export.teamcolor", TeamColorCombo.SelectedIndex.ToString()),
            ("export.texsize", TexSizeCombo.SelectedIndex.ToString()),
            ("export.bakefx", BakeFxCheck.IsChecked == true ? "1" : "0"),
            ("export.fxatlas", FxAtlasCombo.SelectedIndex.ToString()));
        // Each only means something in the mode that shows it.
        if (_batch is null) UserSettings.Set("export.visibleonly", VisibleOnlyCheck.IsChecked == true);
        else UserSettings.Set("export.batchseq", BatchSeqCombo.SelectedIndex);

        DialogResult = true;
    }

    /// <summary>A remembered combo index is only valid while the list is still that long.</summary>
    private static int Clamp(int index, int count) => count == 0 ? -1 : Math.Clamp(index, 0, count - 1);

    /// <summary>One sequence row: its index in the model, checkbox, and an editable export name.</summary>
    private sealed class SequenceRow(int index, string label, string defaultExportName) : INotifyPropertyChanged
    {
        public int Index { get; } = index;
        public string Label { get; } = label;

        private bool _isChecked = true;
        public bool IsChecked
        {
            get => _isChecked;
            set { if (_isChecked != value) { _isChecked = value; OnPropertyChanged(); } }
        }

        private string _exportName = defaultExportName;
        public string ExportName
        {
            get => _exportName;
            set { if (_exportName != value) { _exportName = value; OnPropertyChanged(); } }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

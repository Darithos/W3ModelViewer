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
    private readonly MdxModel _model;
    private readonly Wc3ModelEntry _entry;
    private readonly HashSet<int> _visibleGeosets;
    private readonly List<SequenceRow> _rows;

    /// <summary>Valid after the dialog closes with true.</summary>
    public M3ExportOptions Options { get; private set; } = new();
    public string OutputDir { get; private set; } = "";
    public bool ExportGltf { get; private set; } = true;
    public bool ExportM3 { get; private set; }

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
        // The export itself creates a <ModelName> subfolder (D3-exporter layout), so the
        // default here is just the collection root.
        OutDirBox.Text = UserSettings.Get("export.outdir", Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Wc3Exports"));
        M3Check.IsChecked = UserSettings.GetBool("export.m3", true);
        GltfCheck.IsChecked = UserSettings.GetBool("export.gltf", false);
        ConvertPbrCheck.IsChecked = UserSettings.GetBool("export.convertpbr", true);
        GeosetVisCheck.IsChecked = UserSettings.GetBool("export.geoavis", true);
        ReduceKeysCheck.IsChecked = UserSettings.GetBool("export.reducekeys", true);
        VisibleOnlyCheck.IsChecked = UserSettings.GetBool("export.visibleonly", false);
        TeamColorCombo.SelectedIndex = Clamp(UserSettings.GetInt("export.teamcolor", 0), TeamColorCombo.Items.Count);
        TexSizeCombo.SelectedIndex = Clamp(UserSettings.GetInt("export.texsize", 2), TexSizeCombo.Items.Count);

        Title = $"Export {entry.Name} (.m3)";
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

        var lods = _model.LodLevels;
        int lod = LodCombo.SelectedIndex >= 0 && LodCombo.SelectedIndex < lods.Count
            ? lods[LodCombo.SelectedIndex] : 0;

        Options = new M3ExportOptions
        {
            Sequences = selected.Select(r => r.Index).ToHashSet(),
            SequenceNames = selected
                .Where(r => r.ExportName.Trim().Length > 0)
                .ToDictionary(r => r.Index, r => r.ExportName.Trim()),
            Lod = lod,
            Scale = scale,
            TeamColor = TeamColorCombo.SelectedIndex,
            ConvertPbr = ConvertPbrCheck.IsChecked == true,
            GeosetVisibility = GeosetVisCheck.IsChecked == true,
            Geosets = VisibleOnlyCheck.IsChecked == true ? _visibleGeosets : null,
            ReduceKeys = ReduceKeysCheck.IsChecked == true,
            MaxTextureSize = TexSizeCombo.SelectedIndex switch { 1 => 2048, 2 => 1024, 3 => 512, _ => 0 },
            ModelName = _entry.Name,
        };
        OutputDir = OutDirBox.Text.Trim();
        ExportGltf = GltfCheck.IsChecked == true;
        ExportM3 = M3Check.IsChecked == true;

        // Remembered on a real export only: a cancelled dialog was not a decision.
        UserSettings.SetMany(
            ("export.scale", ScaleBox.Text.Trim()),
            ("export.scaleunit", "sc2"),
            ("export.outdir", OutputDir),
            ("export.m3", ExportM3 ? "1" : "0"),
            ("export.gltf", ExportGltf ? "1" : "0"),
            ("export.convertpbr", Options.ConvertPbr ? "1" : "0"),
            ("export.geoavis", Options.GeosetVisibility ? "1" : "0"),
            ("export.reducekeys", Options.ReduceKeys ? "1" : "0"),
            ("export.visibleonly", VisibleOnlyCheck.IsChecked == true ? "1" : "0"),
            ("export.teamcolor", TeamColorCombo.SelectedIndex.ToString()),
            ("export.texsize", TexSizeCombo.SelectedIndex.ToString()));

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

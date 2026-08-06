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

        _rows = model.Sequences
            .Select(s => new SequenceRow(s.Name, $"{s.Name}  ({s.DurationMs / 1000.0:0.0}s)",
                                         M3Exporter.MapSequenceName(s.Name)))
            .ToList();
        SequenceChecks.ItemsSource = _rows;

        var lods = model.LodLevels;
        LodCombo.ItemsSource = lods.Select(l => l == 0 ? "LOD 0 (full)" : $"LOD {l}").ToList();
        LodCombo.SelectedIndex = Math.Max(0, lods.IndexOf(viewerLod));

        // The export itself creates a <ModelName> subfolder (D3-exporter layout), so the
        // default here is just the collection root.
        OutDirBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Wc3Exports");

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
        var selected = _rows.Where(r => r.IsChecked).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "Select at least one sequence — SC2 needs at minimum a Stand.",
                            "Export", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!float.TryParse(ScaleBox.Text.Trim(), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out float scale)
            || !float.IsFinite(scale) || scale <= 0)
        {
            MessageBox.Show(this, "Scale must be a positive number.", "Export",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
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
            Sequences = selected.Select(r => r.Wc3Name).ToHashSet(StringComparer.Ordinal),
            SequenceNames = selected
                .Where(r => r.ExportName.Trim().Length > 0)
                .ToDictionary(r => r.Wc3Name, r => r.ExportName.Trim(), StringComparer.Ordinal),
            Lod = lod,
            Scale = scale,
            TeamColor = TeamColorCombo.SelectedIndex,
            ConvertPbr = ConvertPbrCheck.IsChecked == true,
            GeosetVisibility = GeosetVisCheck.IsChecked == true,
            Geosets = VisibleOnlyCheck.IsChecked == true ? _visibleGeosets : null,
            ModelName = _entry.Name,
        };
        OutputDir = OutDirBox.Text.Trim();
        ExportGltf = GltfCheck.IsChecked == true;
        ExportM3 = M3Check.IsChecked == true;
        DialogResult = true;
    }

    /// <summary>One sequence row: original name, checkbox, and an editable export name.</summary>
    private sealed class SequenceRow(string wc3Name, string label, string defaultExportName) : INotifyPropertyChanged
    {
        public string Wc3Name { get; } = wc3Name;
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

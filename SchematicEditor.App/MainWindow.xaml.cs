using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using SchematicEditor.Core;

namespace SchematicEditor.App;

public partial class MainWindow : Window
{
    private sealed record PaletteItem(string Name, ImageSource Icon);

    private sealed record ErcListItem(string Text, Brush DotBrush, Vec2? Location);

    private static readonly Brush ErrorDot = Frozen(Color.FromRgb(0xd0, 0x20, 0x20));
    private static readonly Brush WarningDot = Frozen(Color.FromRgb(0xe8, 0x9a, 0x00));
    private static readonly Brush OkDot = Frozen(Color.FromRgb(0x18, 0xa0, 0x46));

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private string? _currentPath;

    public MainWindow()
    {
        InitializeComponent();

        Palette.ItemsSource = SymbolLibrary.All
            .Select(def => new PaletteItem(def.Name, SymbolIconFactory.Create(def)))
            .ToList();

        Canvas.StatusChanged += s => StatusText.Text = s;
        Canvas.CursorStatusChanged += s => CoordsText.Text = s;
        Canvas.SelectionOrToolChanged += UpdateToolbar;
        Canvas.SimulationStateChanged += OnSimulationStateChanged;
        Canvas.SimulationFrame += () =>
            ClearProbesButton.IsEnabled = Canvas.Probes.Count > 0;
        Scope.Attach(Canvas);
        UpdateToolbar();

        // File shortcuts live on the window; editing shortcuts live on the canvas.
        InputBindings.Add(new KeyBinding(new RelayCommand(() => OnNew(this, null!)),
            Key.N, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => OnOpen(this, null!)),
            Key.O, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => OnSave(this, null!)),
            Key.S, ModifierKeys.Control));

        Loaded += (_, _) => Canvas.Focus();
    }

    private sealed class RelayCommand(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }

    private void UpdateToolbar()
    {
        bool running = Canvas.IsRunning;

        SelectToggle.IsChecked = !running && Canvas.Tool == EditorTool.Select;
        WireToggle.IsChecked = !running && Canvas.Tool == EditorTool.Wire;
        SelectToggle.IsEnabled = !running;
        WireToggle.IsEnabled = !running;
        UndoButton.IsEnabled = !running && Canvas.Undo.CanUndo;
        RedoButton.IsEnabled = !running && Canvas.Undo.CanRedo;
        Palette.IsEnabled = !running;
        RotateButton.IsEnabled = Canvas.CanRotateMirror;
        MirrorButton.IsEnabled = Canvas.CanRotateMirror;
        ProbeToggle.IsChecked = Canvas.ProbeArmed;

        (ToolText.Text, HintText.Text) = running
            ? ("Running",
                "Click a switch to toggle it  •  probe tool: click a wire or component  •  Esc stop")
            : Canvas.ProbeArmed
            ? ("Probe",
                "Click a wire/pin for a voltage probe, a component body for current  •  click a probe to remove it  •  Esc exit")
            : Canvas.Tool switch
            {
                EditorTool.Wire => ("Wire",
                    "Click a pin or wire to start  •  click a target pin/wire to finish  •  RMB / Esc cancel"),
                EditorTool.Place => ("Place",
                    "Click to place  •  R rotate  •  M mirror  •  Esc / RMB back to Select"),
                _ => ("Select",
                    "Drag to move / rubber-band  •  R rotate  •  M mirror  •  double-click to edit  •  Del delete"),
            };

        if (!running && Canvas.Tool != EditorTool.Place)
            Palette.SelectedIndex = -1;
    }

    // ------------------------------------------------------------ simulation

    private void OnSimulationStateChanged()
    {
        bool running = Canvas.IsRunning;

        RunIcon.Data = (System.Windows.Media.Geometry)FindResource(running ? "IconStop" : "IconPlay");
        var brush = running ? Frozen(Color.FromRgb(0xC8, 0x35, 0x35)) : Frozen(Color.FromRgb(0x2E, 0x9E, 0x4F));
        RunIcon.Fill = brush;
        RunIcon.Stroke = brush;
        RunButton.ToolTip = running ? "Stop simulation (Esc)" : "Run simulation";

        ResetButton.IsEnabled = running;

        if (running)
            ScopePanel.Visibility = Visibility.Visible;

        UpdateToolbar();
    }

    private void OnRotate(object sender, RoutedEventArgs e)
    {
        Canvas.RotateSelectionOrGhost();
        Canvas.Focus();
    }

    private void OnMirror(object sender, RoutedEventArgs e)
    {
        Canvas.MirrorSelectionOrGhost();
        Canvas.Focus();
    }

    private void OnRunStop(object sender, RoutedEventArgs e)
    {
        if (Canvas.IsRunning) Canvas.StopSimulation();
        else Canvas.StartSimulation();
        Canvas.Focus();
    }

    private void OnResetSim(object sender, RoutedEventArgs e)
    {
        Canvas.ResetSimulation();
        Canvas.Focus();
    }

    private void OnProbeToggle(object sender, RoutedEventArgs e)
    {
        Canvas.ProbeArmed = ProbeToggle.IsChecked == true;
        Canvas.Focus();
    }

    private void OnScopeWindow(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } &&
            double.TryParse(tag, System.Globalization.CultureInfo.InvariantCulture, out double sec))
        {
            Scope.WindowSeconds = sec;
            Scope.InvalidateVisual();
        }
        Canvas.Focus();
    }

    private void OnClearProbes(object sender, RoutedEventArgs e)
    {
        Canvas.ClearProbes();
        Canvas.Focus();
    }

    private void OnScopeClear(object sender, RoutedEventArgs e)
    {
        Canvas.ClearProbes();
        Canvas.Focus();
    }

    private void OnScopeClose(object sender, RoutedEventArgs e) =>
        ScopePanel.Visibility = Visibility.Collapsed;

    // ------------------------------------------------------------ tools

    private void OnPaletteSelection(object sender, SelectionChangedEventArgs e)
    {
        if (Palette.SelectedItem is PaletteItem item)
        {
            Canvas.SetPlaceTool(item.Name);
            Canvas.Focus();
        }
    }

    private void OnSelectTool(object sender, RoutedEventArgs e)
    {
        Canvas.SetSelectTool();
        Canvas.Focus();
    }

    private void OnWireTool(object sender, RoutedEventArgs e)
    {
        Canvas.SetWireTool();
        Canvas.Focus();
    }

    private void OnUndo(object sender, RoutedEventArgs e) { Canvas.Undo.Undo(); Canvas.Focus(); }
    private void OnRedo(object sender, RoutedEventArgs e) { Canvas.Undo.Redo(); Canvas.Focus(); }
    private void OnFit(object sender, RoutedEventArgs e) { Canvas.ZoomToFit(); Canvas.Focus(); }

    // ------------------------------------------------------------ file I/O

    private void OnNew(object sender, RoutedEventArgs e)
    {
        Canvas.SetDocument(new SchematicDocument());
        _currentPath = null;
        Title = "Schematic Editor";
    }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Schematic (*.schem.json)|*.schem.json|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var loadedDoc = JsonIo.LoadFromFile(dlg.FileName, out var savedProbes);
            Canvas.SetDocument(loadedDoc);
            Canvas.LoadProbes(savedProbes);
            _currentPath = dlg.FileName;
            Title = "Schematic Editor — " + Path.GetFileName(dlg.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Open failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "Schematic (*.schem.json)|*.schem.json",
            FileName = _currentPath is not null ? Path.GetFileName(_currentPath) : "untitled.schem.json",
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            JsonIo.SaveToFile(Canvas.Document, dlg.FileName, Canvas.ExportProbes());
            _currentPath = dlg.FileName;
            Title = "Schematic Editor — " + Path.GetFileName(dlg.FileName);
            StatusText.Text = "Saved " + dlg.FileName;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnExportDxf(object sender, RoutedEventArgs e) =>
        ExportWith("DXF (*.dxf)|*.dxf", "schematic.dxf", DxfExporter.ExportToFile);

    private void OnExportSvg(object sender, RoutedEventArgs e) =>
        ExportWith("SVG (*.svg)|*.svg", "schematic.svg", SvgExporter.ExportToFile);

    private void ExportWith(string filter, string defaultName, Action<SchematicDocument, string> export)
    {
        var dlg = new SaveFileDialog { Filter = filter, FileName = defaultName };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            export(Canvas.Document, dlg.FileName);
            StatusText.Text = "Exported " + dlg.FileName;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ------------------------------------------------------------ analysis

    private void OnNetlist(object sender, RoutedEventArgs e)
    {
        var window = new Window
        {
            Title = "Netlist",
            Owner = this,
            Width = 480,
            Height = 400,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new TextBox
            {
                Text = Canvas.CurrentNetlist.ToText(),
                IsReadOnly = true,
                FontFamily = new FontFamily("Consolas"),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(8),
                BorderThickness = new Thickness(0),
            },
        };
        window.ShowDialog();
    }

    private void OnErc(object sender, RoutedEventArgs e)
    {
        var issues = ErcChecker.Check(Canvas.Document, Canvas.CurrentNetlist);

        List<ErcListItem> items = issues.Count == 0
            ? [new ErcListItem("No issues found.", OkDot, null)]
            : [.. issues.Select(i => new ErcListItem(
                i.Message,
                i.Severity == ErcSeverity.Error ? ErrorDot : WarningDot,
                i.Location))];
        ErcList.ItemsSource = items;

        ErcPanel.Visibility = Visibility.Visible;
        StatusText.Text = $"ERC: {issues.Count} issue(s)";
    }

    private void OnCloseErcPanel(object sender, RoutedEventArgs e) =>
        ErcPanel.Visibility = Visibility.Collapsed;

    private void OnErcItemActivate(object sender, RoutedEventArgs e)
    {
        if (ErcList.SelectedItem is ErcListItem { Location: { } loc })
        {
            Canvas.CenterOn(loc);
            Canvas.Focus();
        }
    }
}

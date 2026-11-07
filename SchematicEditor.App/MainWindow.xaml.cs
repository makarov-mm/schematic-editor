using System.ComponentModel;
using System.Globalization;
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
    private const string AppTitle = "Schematic Editor";
    private const string FileFilter = "Schematic (*.schem.json)|*.schem.json";

    private sealed record PaletteItem(string Name, ImageSource Icon);

    private sealed record ErcListItem(string Text, Brush DotBrush, Vec2? Location);

    private sealed class RelayCommand(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }

    private static readonly Brush ErrorDot = Paint.Brush(0xD0, 0x20, 0x20);
    private static readonly Brush WarningDot = Paint.Brush(0xE8, 0x9A, 0x00);
    private static readonly Brush OkDot = Paint.Brush(0x18, 0xA0, 0x46);
    private static readonly Brush RunBrush = Paint.Brush(0x2E, 0x9E, 0x4F);
    private static readonly Brush StopBrush = Paint.Brush(0xC8, 0x35, 0x35);

    private string? _currentPath;
    private string _savedJson;   // the document as last opened or saved, to tell whether it has changed

    public MainWindow()
    {
        InitializeComponent();

        Palette.ItemsSource = SymbolLibrary.All.Select(d => new PaletteItem(d.Name, SymbolIconFactory.Create(d))).ToList();

        Editor.StatusChanged += message => StatusText.Text = message;
        Editor.CursorStatusChanged += readout => CoordsText.Text = readout;
        Editor.SelectionOrToolChanged += UpdateToolbar;
        Editor.SimulationStateChanged += OnSimulationStateChanged;
        Editor.SimulationFrame += () =>
        {
            ClearProbesButton.IsEnabled = Editor.Probes.Count > 0;
            AcButton.IsEnabled = Editor.Probes.Any(p => !p.IsCurrent);
        };
        Editor.DocumentChanged += () =>
        {
            if (ErcPanel.Visibility == Visibility.Visible)
                RunErc();
        };
        Scope.Attach(Editor);
        _savedJson = CurrentJson();
        UpdateToolbar();

        // The file commands work anywhere in the window; the editing keys belong to the canvas.
        InputBindings.Add(new KeyBinding(new RelayCommand(NewDocument), Key.N, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(OpenDocument), Key.O, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => Save()), Key.S, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => SaveAs()), Key.S, ModifierKeys.Control | ModifierKeys.Shift));

        Loaded += (_, _) => Editor.Focus();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!ConfirmDiscardChanges())
            e.Cancel = true;
        base.OnClosing(e);
    }

    private void UpdateToolbar()
    {
        bool running = Editor.IsRunning;

        SelectToggle.IsChecked = !running && Editor.Tool == EditorTool.Select;
        WireToggle.IsChecked = !running && Editor.Tool == EditorTool.Wire;
        SelectToggle.IsEnabled = !running;
        WireToggle.IsEnabled = !running;
        UndoButton.IsEnabled = !running && Editor.Undo.CanUndo;
        RedoButton.IsEnabled = !running && Editor.Undo.CanRedo;
        UndoButton.ToolTip = Editor.Undo.UndoName is { } undo ? $"Undo {undo} (Ctrl+Z)" : "Undo (Ctrl+Z)";
        RedoButton.ToolTip = Editor.Undo.RedoName is { } redo ? $"Redo {redo} (Ctrl+Y)" : "Redo (Ctrl+Y)";
        Palette.IsEnabled = !running;
        RotateButton.IsEnabled = Editor.CanRotateMirror;
        MirrorButton.IsEnabled = Editor.CanRotateMirror;
        ProbeToggle.IsChecked = Editor.ProbeArmed;

        (ToolText.Text, HintText.Text) =
            running ? ("Running", "Click a switch to toggle it  •  Shift+wheel over R/C/L to tweak its value  •  Esc stop")
            : Editor.ProbeArmed ? ("Probe", "Click a wire/pin for a voltage probe, a component body for current  •  click a probe to remove it  •  Esc exit")
            : Editor.Tool switch
            {
                EditorTool.Wire => ("Wire", "Click a pin or wire to start  •  click a target pin/wire to finish  •  RMB / Esc cancel"),
                EditorTool.Place => ("Place", "Click to place  •  R rotate  •  M mirror  •  Esc / RMB back to Select"),
                _ => ("Select", "Drag to move / rubber-band  •  R rotate  •  M mirror  •  double-click to edit  •  Del delete"),
            };

        if (!running && Editor.Tool != EditorTool.Place)
            Palette.SelectedIndex = -1;
    }

    // ------------------------------------------------------------------ tools and editing

    private void OnPaletteSelection(object sender, SelectionChangedEventArgs e)
    {
        if (Palette.SelectedItem is PaletteItem item)
        {
            Editor.SetPlaceTool(item.Name);
            Editor.Focus();
        }
    }

    private void OnSelectTool(object sender, RoutedEventArgs e) => WithEditor(Editor.SetSelectTool);
    private void OnWireTool(object sender, RoutedEventArgs e) => WithEditor(Editor.SetWireTool);
    private void OnUndo(object sender, RoutedEventArgs e) => WithEditor(Editor.Undo.Undo);
    private void OnRedo(object sender, RoutedEventArgs e) => WithEditor(Editor.Undo.Redo);
    private void OnFit(object sender, RoutedEventArgs e) => WithEditor(Editor.ZoomToFit);
    private void OnRotate(object sender, RoutedEventArgs e) => WithEditor(Editor.RotateSelectionOrGhost);
    private void OnMirror(object sender, RoutedEventArgs e) => WithEditor(Editor.MirrorSelectionOrGhost);

    // Runs a toolbar action, then gives the keyboard back to the canvas.
    private void WithEditor(Action action)
    {
        action();
        Editor.Focus();
    }

    // ------------------------------------------------------------------ simulation

    private void OnSimulationStateChanged()
    {
        bool running = Editor.IsRunning;
        RunIcon.Data = (Geometry)FindResource(running ? "IconStop" : "IconPlay");
        RunIcon.Fill = RunIcon.Stroke = running ? StopBrush : RunBrush;
        RunButton.ToolTip = running ? "Stop simulation (Esc)" : "Run simulation";
        ResetButton.IsEnabled = running;
        if (running)
            ScopePanel.Visibility = Visibility.Visible;
        UpdateToolbar();
    }

    private void OnRunStop(object sender, RoutedEventArgs e) =>
        WithEditor(() =>
        {
            if (Editor.IsRunning)
                Editor.StopSimulation();
            else
                Editor.StartSimulation();
        });

    private void OnResetSim(object sender, RoutedEventArgs e) => WithEditor(Editor.ResetSimulation);

    private void OnProbeToggle(object sender, RoutedEventArgs e) =>
        WithEditor(() =>
        {
            Editor.ProbeArmed = ProbeToggle.IsChecked == true;
            if (Editor.ProbeArmed)
                ScopePanel.Visibility = Visibility.Visible;   // where the probes show
        });

    private void OnClearProbes(object sender, RoutedEventArgs e) => WithEditor(Editor.ClearProbes);

    private void OnScopeWindow(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } && double.TryParse(tag, CultureInfo.InvariantCulture, out double seconds))
            Scope.WindowSeconds = seconds;
        Editor.Focus();
    }

    private void OnScopeClose(object sender, RoutedEventArgs e) => ScopePanel.Visibility = Visibility.Collapsed;

    private void OnAcAnalysis(object sender, RoutedEventArgs e)
    {
        if (Editor.ComputeAcSweep(1.0, 100e3, 48, out string? error) is { } sweep)
            new BodeWindow(sweep) { Owner = this }.Show();
        else
            StatusText.Text = error ?? "AC analysis failed.";
        Editor.Focus();
    }

    // ------------------------------------------------------------------ files

    private void OnNew(object sender, RoutedEventArgs e) => NewDocument();
    private void OnOpen(object sender, RoutedEventArgs e) => OpenDocument();
    private void OnSave(object sender, RoutedEventArgs e) => Save();

    private void NewDocument()
    {
        if (!ConfirmDiscardChanges())
            return;
        Editor.SetDocument(new SchematicDocument());
        SetCurrentFile(null);
    }

    private void OpenDocument()
    {
        if (!ConfirmDiscardChanges())
            return;

        var dialog = new OpenFileDialog { Filter = FileFilter + "|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            SchematicDocument doc = JsonIo.LoadFromFile(dialog.FileName, out List<ProbeInfo> probes);
            Editor.SetDocument(doc);
            Editor.LoadProbes(probes);
            SetCurrentFile(dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Open failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Saves to the current file, or asks for one. False when cancelled or failed.</summary>
    private bool Save() => _currentPath is null ? SaveAs() : SaveTo(_currentPath);

    private bool SaveAs()
    {
        var dialog = new SaveFileDialog
        {
            Filter = FileFilter,
            FileName = _currentPath is null ? "untitled.schem.json" : Path.GetFileName(_currentPath),
        };
        return dialog.ShowDialog(this) == true && SaveTo(dialog.FileName);
    }

    private bool SaveTo(string path)
    {
        try
        {
            File.WriteAllText(path, CurrentJson());
            SetCurrentFile(path);
            StatusText.Text = "Saved " + path;
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    // The document as it would be saved, probes and switch positions included.
    private string CurrentJson() => JsonIo.Save(Editor.Document, Editor.ExportProbes());

    private void SetCurrentFile(string? path)
    {
        _currentPath = path;
        _savedJson = CurrentJson();
        Title = path is null ? AppTitle : $"{AppTitle} — {Path.GetFileName(path)}";
    }

    /// <summary>Offers to save the changes before they would be lost. False when the user cancels.</summary>
    private bool ConfirmDiscardChanges()
    {
        if (CurrentJson() == _savedJson)
            return true;

        string name = _currentPath is null ? "the new schematic" : Path.GetFileName(_currentPath);
        return MessageBox.Show(this, $"Save the changes to {name}?", AppTitle, MessageBoxButton.YesNoCancel, MessageBoxImage.Question) switch
        {
            MessageBoxResult.Yes => Save(),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    private void OnExportDxf(object sender, RoutedEventArgs e) => Export("DXF (*.dxf)|*.dxf", "schematic.dxf", DxfExporter.ExportToFile);

    private void OnExportSvg(object sender, RoutedEventArgs e) => Export("SVG (*.svg)|*.svg", "schematic.svg", SvgExporter.ExportToFile);

    private void Export(string filter, string defaultName, Action<SchematicDocument, string> export)
    {
        var dialog = new SaveFileDialog { Filter = filter, FileName = defaultName };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            export(Editor.Document, dialog.FileName);
            StatusText.Text = "Exported " + dialog.FileName;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ------------------------------------------------------------------ analysis

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
                Text = Editor.CurrentNetlist.ToText(),
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
        int issues = RunErc();
        ErcPanel.Visibility = Visibility.Visible;
        StatusText.Text = $"ERC: {issues} issue(s)";
    }

    // Fills the ERC panel, which stays up to date while it is open. Gives the number of issues.
    private int RunErc()
    {
        List<ErcIssue> issues = ErcChecker.Check(Editor.Document, Editor.CurrentNetlist);
        List<ErcListItem> items = issues.Count == 0
            ? [new ErcListItem("No issues found.", OkDot, null)]
            : [.. issues.Select(i => new ErcListItem(i.Message, i.Severity == ErcSeverity.Error ? ErrorDot : WarningDot, i.Location))];
        ErcList.ItemsSource = items;
        return issues.Count;
    }

    private void OnCloseErcPanel(object sender, RoutedEventArgs e) => ErcPanel.Visibility = Visibility.Collapsed;

    private void OnErcItemActivate(object sender, RoutedEventArgs e)
    {
        if (ErcList.SelectedItem is ErcListItem { Location: { } location })
        {
            Editor.CenterOn(location);
            Editor.Focus();
        }
    }
}

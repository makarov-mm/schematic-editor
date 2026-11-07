using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Windows.Media;
using SchematicEditor.Core;

namespace SchematicEditor.App;

// The live simulation, probes and the AC sweep.
public sealed partial class SchematicCanvas
{
    /// <summary>Simulated seconds per step.</summary>
    public const double SimDt = 5e-5;

    // At most this much time is simulated per frame, so that a stall (a dialog, a window being
    // dragged) doesn't end in a burst of steps; the simulation then falls behind real time.
    private const double MaxFrameTime = 0.08;

    private static readonly Color[] ProbePalette =
    [
        Color.FromRgb(0xE6, 0x19, 0x4B), Color.FromRgb(0x3C, 0xB4, 0x4B),
        Color.FromRgb(0x43, 0x63, 0xD8), Color.FromRgb(0xF5, 0x82, 0x31),
        Color.FromRgb(0x91, 0x1E, 0xB4), Color.FromRgb(0x00, 0xA8, 0xB5),
        Color.FromRgb(0xF0, 0x32, 0xE6), Color.FromRgb(0x9A, 0x63, 0x24),
    ];

    // Values a resistor, capacitor or inductor steps through with Shift+wheel, times powers of ten.
    private static readonly double[] ValueLadder = [1.0, 2.2, 4.7];

    private CircuitSimulator? _sim;
    private readonly Stopwatch _frameClock = new();
    private double _pendingTime;                 // real time not simulated yet, less than a step
    private readonly List<Probe> _probes = [];

    public bool IsRunning => _sim is not null;
    public double SimTime => _sim?.Time ?? 0;
    public IReadOnlyList<Probe> Probes => _probes;

    /// <summary>Raised after every simulated frame, and whenever the probes change.</summary>
    public event Action? SimulationFrame;

    /// <summary>Raised when the simulation starts or stops.</summary>
    public event Action? SimulationStateChanged;

    // ------------------------------------------------------------------ running

    /// <summary>Builds the circuit and runs it in real time. False when it cannot be simulated.</summary>
    public bool StartSimulation()
    {
        if (IsRunning)
            return true;

        CircuitSimulator? sim = CircuitSimulator.Build(Document, _netlist, out List<string> problems);
        if (sim is null)
        {
            Status(string.Join(" ", problems));
            return false;
        }

        _sim = sim;
        foreach (Probe probe in _probes)
        {
            probe.Trace.Clear();
            if (!probe.IsCurrent)
                probe.Node = sim.ResolveNode(probe.Anchor) ?? -1;
        }
        int lostProbes = _probes.RemoveAll(p => !p.IsCurrent && p.Node < 0);

        _selection.Clear();
        SetSelectToolQuietly();
        _pendingTime = 0;
        _frameClock.Restart();
        CompositionTarget.Rendering += OnRendering;

        SimulationStateChanged?.Invoke();
        SelectionOrToolChanged?.Invoke();
        ProbesChanged();

        List<string> messages = ["Simulation running.", .. sim.Warnings];
        if (lostProbes > 0)
            messages.Add($"Removed {lostProbes} probe(s) that no longer match the circuit.");
        Status(string.Join(" ", messages));
        return true;
    }

    public void StopSimulation()
    {
        if (_sim is null)
            return;

        CompositionTarget.Rendering -= OnRendering;
        _sim = null;
        SimulationStateChanged?.Invoke();
        SelectionOrToolChanged?.Invoke();
        Status("Simulation stopped.");
        InvalidateVisual();
    }

    /// <summary>Back to time zero: reactive elements discharged, fuses whole, traces cleared.</summary>
    public void ResetSimulation()
    {
        _sim?.Reset();
        foreach (Probe probe in _probes)
            probe.Trace.Clear();
        ProbesChanged();
    }

    // Once per frame while running: the simulation catches up with real time.
    private void OnRendering(object? sender, EventArgs e)
    {
        if (_sim is not { } sim)
            return;

        _pendingTime += Math.Min(_frameClock.Elapsed.TotalSeconds, MaxFrameTime);
        _frameClock.Restart();
        try
        {
            for (; _pendingTime >= SimDt; _pendingTime -= SimDt)
            {
                sim.Step(SimDt);
                foreach (Probe probe in _probes)
                {
                    double value = probe.Symbol is { } symbol ? sim.GetCurrent(symbol) ?? 0
                        : probe.Node >= 0 ? sim.GetNodeVoltage(probe.Node) : 0;
                    probe.Trace.Add(sim.Time, value);
                }
            }
        }
        catch (InvalidOperationException ex)
        {
            StopSimulation();
            Status("Simulation stopped: " + ex.Message);
            return;
        }

        InvalidateVisual();
        SimulationFrame?.Invoke();
    }

    /// <summary>A click while running toggles a switch, live: no undo entry and no rebuild.</summary>
    private void ToggleSwitchAt(Vec2 point)
    {
        if (HitTest(point) is SymbolInstance { Definition.Kind: SymbolKind.Switch } toggled)
        {
            toggled.StateOn = !toggled.StateOn;
            InvalidateVisual();
        }
    }

    /// <summary>Steps a component's value along the 1 / 2.2 / 4.7 ladder; the running circuit takes it over at once.</summary>
    private void TweakValue(SymbolInstance component, bool up)
    {
        if (_sim is null || !Units.TryParse(component.Value, out double value) || value <= 0)
            return;

        string old = component.Value;
        component.Value = Units.FormatValue(NextLadderValue(value, up));
        if (_sim.UpdateComponentValue(component))
        {
            Status($"{component.DisplayName} = {component.Value}");
            InvalidateVisual();
        }
        else
        {
            component.Value = old;
        }
    }

    // The next value of the 1 / 2.2 / 4.7 ladder above or below value: ... 4.7, 10, 22, 47 ...
    private static double NextLadderValue(double value, bool up)
    {
        int exponent = (int)Math.Floor(Math.Log10(value));
        double[] rungs = [.. Enumerable.Range(exponent - 1, 3).SelectMany(e => ValueLadder.Select(m => m * Math.Pow(10, e)))];
        double next = up ? rungs.First(r => r > value * (1 + 1e-9)) : rungs.Last(r => r < value * (1 - 1e-9));
        return Math.Clamp(next, 1e-12, 1e12);
    }

    /// <summary>The status line while running: the current through the symbol under the cursor, or the voltage there.</summary>
    private string LiveReadout(CircuitSimulator sim, Vec2 snapped)
    {
        string reading =
            HitTest(_cursorWorld) is SymbolInstance symbol && sim.GetCurrent(symbol) is { } current
                ? $"   {symbol.DisplayName}: {Units.Format(current, "A")}"
            : sim.GetVoltageAt(Document.FindPinNear(_cursorWorld, HitTolerance * 2) ?? snapped) is { } voltage
                ? "   " + Units.Format(voltage, "V")
            : "";
        return string.Create(CultureInfo.InvariantCulture, $"t {sim.Time:0.000} s{reading}");
    }

    // ------------------------------------------------------------------ probes

    /// <summary>Removes the probe at a point, or else adds one: on a two-pin symbol a current probe, on a wire or pin a voltage probe.</summary>
    private void ProbeClick(Vec2 point)
    {
        double reach = HitTolerance * 2;
        if (_probes.Find(p => p.Position.DistanceTo(point) <= reach) is { } hit)
        {
            _probes.Remove(hit);
            Status($"Removed probe {hit.Label}.");
        }
        else if (HitTest(point) is SymbolInstance { Definition.Pins.Count: 2 } symbol)
        {
            AddProbe(Probe.Current(symbol, NextProbeColor()));
            Status($"Current probe on {symbol.DisplayName}.");
        }
        else
        {
            Vec2 anchor = Document.FindPinNear(point, reach) ?? point.Snap(Grid);
            if (_netlist.FindNetAt(anchor) is not { } net)
            {
                Status("No net here: click a wire or a pin.");
                return;
            }
            AddProbe(Probe.Voltage(anchor, NextProbeColor()));
            Status($"Voltage probe on {net.Name}.");
        }
        ProbesChanged();
    }

    private void AddProbe(Probe probe)
    {
        probe.Label = ProbeLabel(probe);
        if (_sim is not null && !probe.IsCurrent)
            probe.Node = _sim.ResolveNode(probe.Anchor) ?? -1;
        _probes.Add(probe);
    }

    private string ProbeLabel(Probe probe) =>
        probe.Symbol is { } symbol ? $"I({symbol.DisplayName})" : $"V({_netlist.FindNetAt(probe.Anchor)?.Name})";

    /// <summary>The first palette colour no probe has, so that colours stay apart after removals.</summary>
    private Color NextProbeColor() =>
        ProbePalette.FirstOrDefault(c => !_probes.Exists(p => p.Color == c), ProbePalette[_probes.Count % ProbePalette.Length]);

    public void ClearProbes()
    {
        _probes.Clear();
        ProbesChanged();
    }

    /// <summary>Restores probes saved with a document; those that no longer hit anything are dropped.</summary>
    public void LoadProbes(IEnumerable<ProbeInfo> saved)
    {
        _probes.Clear();
        foreach (ProbeInfo info in saved)
        {
            if (info.Type == "I")
            {
                if (Document.FindById(info.SymbolId) is SymbolInstance { Definition.Pins.Count: 2 } symbol)
                    AddProbe(Probe.Current(symbol, NextProbeColor()));
            }
            else
            {
                var anchor = new Vec2(info.X, info.Y);
                if (_netlist.FindNetAt(anchor) is not null)
                    AddProbe(Probe.Voltage(anchor, NextProbeColor()));
            }
        }
        ProbesChanged();
    }

    /// <summary>The probes, to be saved with the document.</summary>
    public List<ProbeInfo> ExportProbes() =>
        [.. _probes.Select(p => p.Symbol is { } symbol ? new ProbeInfo("I", SymbolId: symbol.Id) : new ProbeInfo("V", p.Anchor.X, p.Anchor.Y))];

    /// <summary>After an edit: drops the probes whose symbol or net is gone, and renames the others.</summary>
    private void RevalidateProbes()
    {
        _probes.RemoveAll(p => p.Symbol is { } symbol ? Document.FindById(symbol.Id) != symbol : _netlist.FindNetAt(p.Anchor) is null);
        foreach (Probe probe in _probes)
            probe.Label = ProbeLabel(probe);
        ProbesChanged();
    }

    private void ProbesChanged()
    {
        InvalidateVisual();
        SimulationFrame?.Invoke();
    }

    // ------------------------------------------------------------------ AC analysis

    /// <summary>
    /// The frequency response at the voltage probes, for the Bode plot. Uses the running simulator
    /// when there is one, so that switches and diodes are as on screen, otherwise a new one. Null,
    /// with the reason, when the sweep cannot run.
    /// </summary>
    public AcSweep? ComputeAcSweep(double fMin, double fMax, int pointsPerDecade, out string? error)
    {
        error = null;
        List<Probe> probes = [.. _probes.Where(p => !p.IsCurrent)];
        if (probes.Count == 0)
        {
            error = "Place at least one voltage probe first.";
            return null;
        }

        CircuitSimulator? sim = _sim;
        if (sim is null)
        {
            sim = CircuitSimulator.Build(Document, _netlist, out List<string> problems);
            if (sim is null)
            {
                error = string.Join(" ", problems);
                return null;
            }
        }
        if (!sim.HasAcSource)
        {
            error = "No AC source: add an ACSource to drive the frequency sweep.";
            return null;
        }

        double[] frequencies = CircuitSimulator.LogFrequencies(fMin, fMax, pointsPerDecade);
        int[] nodes = [.. probes.Select(p => sim.ResolveNode(p.Anchor) ?? 0)];
        try
        {
            Complex[][] response = sim.SweepAc(nodes, frequencies);
            return new AcSweep(frequencies, [.. probes.Select((p, k) => new AcTrace(p.Label, p.Color, response[k]))]);
        }
        catch (InvalidOperationException ex)
        {
            error = ex.Message;
            return null;
        }
    }
}

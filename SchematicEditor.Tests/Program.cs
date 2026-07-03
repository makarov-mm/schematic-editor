using SchematicEditor.Core;

// Dependency-free smoke tests for the core library. Run: dotnet run
int failed = 0, passed = 0;

void Check(bool cond, string name)
{
    if (cond) { passed++; Console.WriteLine($"  ok   {name}"); }
    else { failed++; Console.WriteLine($"  FAIL {name}"); }
}

// ---------------------------------------------------------------- transforms
Console.WriteLine("Transforms:");
{
    var def = SymbolLibrary.Get("Resistor");
    var s = new SymbolInstance(def, new Vec2(100, 50)) { Rotation = Rotation.R90 };
    var pins = s.WorldPins().Select(p => p.World).ToArray();
    // Local (-20,0) rotated 90 CW (Y down) -> (0,-20); (20,0) -> (0,20).
    Check(pins[0].Key() == new Vec2(100, 30).Key(), "R90 pin 1 position");
    Check(pins[1].Key() == new Vec2(100, 70).Key(), "R90 pin 2 position");

    s.Rotation = Rotation.R0;
    s.Mirror = true;
    pins = s.WorldPins().Select(p => p.World).ToArray();
    Check(pins[0].Key() == new Vec2(120, 50).Key(), "Mirror pin 1 position");
}

// ------------------------------------------------------- build a demo circuit
// Battery -> switch -> resistor -> back to battery, with a ground tap (T-connection).
var doc = new SchematicDocument();
var undo = new UndoStack(doc);

SymbolInstance Place(string symbol, Vec2 pos, Rotation rot = Rotation.R0)
{
    var inst = new SymbolInstance(SymbolLibrary.Get(symbol), pos) { Rotation = rot };
    undo.Push(new AddElementCommand(inst));
    return inst;
}

Wire AddWire(params Vec2[] pts)
{
    var w = new Wire(pts);
    undo.Push(new AddElementCommand(w));
    return w;
}

var bt = Place("Battery", new Vec2(0, 0));            // pins (0,-20) / (0,20)
var sw = Place("Switch", new Vec2(60, -40));          // pins (40,-40) / (80,-40)
var r = Place("Resistor", new Vec2(120, 0), Rotation.R90); // pins (120,-20) / (120,20)
var gnd = Place("Ground", new Vec2(30, 40));          // pin (30,40)

AddWire(new Vec2(0, -20), new Vec2(0, -40), new Vec2(40, -40));
AddWire(new Vec2(80, -40), new Vec2(120, -40), new Vec2(120, -20));
AddWire(new Vec2(120, 20), new Vec2(120, 40), new Vec2(0, 40), new Vec2(0, 20));

Console.WriteLine("RefDes assignment:");
Check(bt.RefDes == "BT1", $"battery refdes ({bt.RefDes})");
Check(r.RefDes == "R1", $"resistor refdes ({r.RefDes})");

// ------------------------------------------------------------------- netlist
Console.WriteLine("Netlist:");
var netlist = NetlistExtractor.Extract(doc);
Console.Write(netlist.ToText());

Check(netlist.Nets.Count == 3, $"3 nets extracted ({netlist.Nets.Count})");

var gndNet = netlist.Nets.FirstOrDefault(n => n.Name == "GND");
Check(gndNet != null, "GND net exists");
Check(gndNet != null && gndNet.Pins.Count == 3, "GND net has 3 pins (BT-, R.2, GND.1)");

var n1 = netlist.Nets.FirstOrDefault(n =>
    n.Pins.Any(p => p.Symbol == bt && p.Pin.Name == "+"));
Check(n1 != null && n1.Pins.Any(p => p.Symbol == sw), "BT+ connected to switch");

Check(netlist.Junctions.Count == 1, $"one junction (ground T-tap) ({netlist.Junctions.Count})");
Check(netlist.Junctions.Count == 1 && netlist.Junctions[0].Key() == new Vec2(30, 40).Key(),
    "junction at (30,40)");
Check(netlist.DanglingWireEnds.Count == 0, "no dangling wire ends");

// ----------------------------------------------------------------------- ERC
Console.WriteLine("ERC (clean circuit):");
var issues = ErcChecker.Check(doc, netlist);
foreach (var i in issues) Console.WriteLine("  " + i);
Check(issues.Count == 0, $"clean circuit has no issues ({issues.Count})");

Console.WriteLine("ERC (broken circuit):");
var floating = Place("Capacitor", new Vec2(200, 0));
var stub = AddWire(new Vec2(200, 80), new Vec2(240, 80));
netlist = NetlistExtractor.Extract(doc);
issues = ErcChecker.Check(doc, netlist);
foreach (var i in issues) Console.WriteLine("  " + i);
Check(issues.Count(i => i.Message.StartsWith("Unconnected pin C1")) == 2,
    "two unconnected pins reported for floating capacitor");
Check(issues.Count(i => i.Message.StartsWith("Dangling wire end")) == 2,
    "two dangling ends reported for stub wire");

// Short a source: wire directly across the battery.
var shortWire = AddWire(new Vec2(0, -20), new Vec2(-40, -20), new Vec2(-40, 20), new Vec2(0, 20));
netlist = NetlistExtractor.Extract(doc);
issues = ErcChecker.Check(doc, netlist);
Check(issues.Any(i => i.Message.Contains("short-circuited")), "shorted battery detected");
undo.Undo(); // remove the short

// ----------------------------------------------------------------- undo/redo
Console.WriteLine("Undo/redo:");
int before = doc.Elements.Count;
undo.Push(new DeleteElementsCommand(new SchematicElement[] { floating, stub }));
Check(doc.Elements.Count == before - 2, "delete removed 2 elements");
undo.Undo();
Check(doc.Elements.Count == before, "undo restored elements");
undo.Redo();
Check(doc.Elements.Count == before - 2, "redo re-applied delete");

undo.Push(new MoveElementsCommand(new SchematicElement[] { r }, new Vec2(5, 5)));
Check(r.Position.Key() == new Vec2(125, 5).Key(), "move applied");
undo.Undo();
Check(r.Position.Key() == new Vec2(120, 0).Key(), "move undone");

undo.Push(new RotateSymbolCommand(r));
Check(r.Rotation == Rotation.R180, "rotate 90 -> 180");
undo.Undo();
Check(r.Rotation == Rotation.R90, "rotate undone");

// --------------------------------------------------------------- JSON I/O
Console.WriteLine("JSON roundtrip:");
string json = JsonIo.Save(doc);
var doc2 = JsonIo.Load(json);
Check(doc2.Symbols.Count() == doc.Symbols.Count(), "symbol count preserved");
Check(doc2.Wires.Count() == doc.Wires.Count(), "wire count preserved");
var r2 = doc2.Symbols.FirstOrDefault(s => s.RefDes == "R1");
Check(r2 != null && r2.Rotation == Rotation.R90 && r2.Value == "10k",
    "resistor rotation and value preserved");
var net2 = NetlistExtractor.Extract(doc2);
Check(net2.Nets.Count == NetlistExtractor.Extract(doc).Nets.Count,
    "netlist identical after roundtrip");

// ------------------------------------------------------------------ exports
Console.WriteLine("Connection queries:");
Check(doc.IsConnectionPoint(new Vec2(0, -20)), "battery pin is a connection point");
Check(doc.IsConnectionPoint(new Vec2(60, 40)), "wire segment interior is a connection point");
Check(doc.IsConnectionPoint(new Vec2(120, -40)), "wire corner vertex is a connection point");
Check(!doc.IsConnectionPoint(new Vec2(300, 300)), "empty space is not a connection point");
var near = doc.FindPinNear(new Vec2(2, -18), 5);
Check(near != null && near.Value.Key() == new Vec2(0, -20).Key(), "FindPinNear snaps to battery pin");
Check(doc.FindPinNear(new Vec2(300, 300), 5) == null, "FindPinNear returns null far away");

Console.WriteLine("Exports:");
string dxf = DxfExporter.Export(doc);
Check(dxf.Contains("AC1009") && dxf.TrimEnd().EndsWith("EOF"), "DXF header/footer");
Check(dxf.Contains("LINE") && dxf.Contains("CIRCLE") && dxf.Contains("TEXT"),
    "DXF contains expected entity types");
Check(!dxf.Contains(','), "DXF uses invariant decimal separator");

string svg = SvgExporter.Export(doc);
Check(svg.Contains("<svg") && svg.Contains("</svg>"), "SVG well-formed shell");
Check(svg.Contains("<polyline") && svg.Contains("<circle"), "SVG contains geometry");

File.WriteAllText("/tmp/demo.dxf", dxf);
File.WriteAllText("/tmp/demo.svg", svg);
File.WriteAllText("/tmp/demo.schem.json", json);

// ------------------------------------------------------------ units parsing
Console.WriteLine("Units:");
{
    Check(Units.TryParse("10k", out double v1) && Math.Abs(v1 - 10_000) < 1e-9, "10k -> 10000");
    Check(Units.TryParse("4.7k", out double v2) && Math.Abs(v2 - 4700) < 1e-9, "4.7k -> 4700");
    Check(Units.TryParse("100n", out double v3) && Math.Abs(v3 - 100e-9) < 1e-15, "100n -> 1e-7");
    Check(Units.TryParse("12V", out double v4) && Math.Abs(v4 - 12) < 1e-9, "12V -> 12");
    Check(Units.TryParse("2mV", out double v5) && Math.Abs(v5 - 2e-3) < 1e-12, "2mV -> 0.002 (milli)");
    Check(Units.TryParse("2MHz", out double v6) && Math.Abs(v6 - 2e6) < 1e-3, "2MHz -> 2e6 (mega)");
    Check(Units.TryParseLampRating("12V 5W", out double lr, out double lp)
        && Math.Abs(lr - 28.8) < 1e-9 && Math.Abs(lp - 5) < 1e-9, "12V 5W -> 28.8 Ohm, 5 W");
    Check(Units.TryParseAcSpec("5V 50Hz", out double aa, out double af)
        && Math.Abs(aa - 5) < 1e-9 && Math.Abs(af - 50) < 1e-9, "5V 50Hz parsed");
    Check(!Units.TryParse("abc", out _), "garbage rejected");
}

// ------------------------------------------------------- simulation helpers
SchematicDocument SimDoc(Action<SchematicDocument, Func<string, Vec2, Rotation, SymbolInstance>, Action<Vec2[]>> build)
{
    var d = new SchematicDocument();
    var u = new UndoStack(d);
    SymbolInstance P(string sym, Vec2 pos, Rotation rot)
    {
        var inst = new SymbolInstance(SymbolLibrary.Get(sym), pos) { Rotation = rot };
        u.Push(new AddElementCommand(inst));
        return inst;
    }
    void W(Vec2[] pts) => u.Push(new AddElementCommand(new Wire(pts)));
    build(d, P, W);
    return d;
}

CircuitSimulator MustBuild(SchematicDocument d)
{
    var sim = CircuitSimulator.Build(d, NetlistExtractor.Extract(d), out var problems);
    if (sim == null) throw new InvalidOperationException(string.Join("; ", problems));
    return sim;
}

// --------------------------------------------------------------- DC divider
Console.WriteLine("Simulation, DC divider:");
{
    // V1 10V vertical at x=0; R1, R2 stacked at x=60; mid node between them.
    SymbolInstance? r1 = null;
    var d = SimDoc((doc, P, W) =>
    {
        var v = P("VSource", new Vec2(0, 0), Rotation.R0);
        v.Value = "10V";
        r1 = P("Resistor", new Vec2(60, -20), Rotation.R90);   // pins (60,-40)/(60,0)
        var r2 = P("Resistor", new Vec2(60, 40), Rotation.R90); // pins (60,20)/(60,60)
        r2.Value = "10k";
        P("Ground", new Vec2(0, 80), Rotation.R0);
        W([new Vec2(0, -20), new Vec2(0, -40), new Vec2(60, -40)]);
        W([new Vec2(60, 0), new Vec2(60, 20)]);
        W([new Vec2(60, 60), new Vec2(60, 80), new Vec2(0, 80)]);
        W([new Vec2(0, 20), new Vec2(0, 80)]);
    });
    var sim = MustBuild(d);
    sim.Step(1e-4);
    double? mid = sim.GetVoltageAt(new Vec2(60, 10));
    Check(mid != null && Math.Abs(mid.Value - 5.0) < 1e-3, $"divider midpoint = 5 V (got {mid:0.###})");
    double? i = sim.GetCurrent(r1!);
    Check(i != null && Math.Abs(Math.Abs(i.Value) - 0.5e-3) < 1e-6, $"divider current = 0.5 mA (got {i:0.######})");
    double? top = sim.GetVoltageAt(new Vec2(0, -40));
    Check(top != null && Math.Abs(top.Value - 10.0) < 1e-3, "source node = 10 V");
}

// ------------------------------------------------------------- RC charging
Console.WriteLine("Simulation, RC transient:");
{
    var d = SimDoc((doc, P, W) =>
    {
        var v = P("VSource", new Vec2(0, 0), Rotation.R0);
        v.Value = "10V";
        var r = P("Resistor", new Vec2(60, -40), Rotation.R0);   // pins (40,-40)/(80,-40)
        r.Value = "1k";
        var c = P("Capacitor", new Vec2(120, 0), Rotation.R90);  // pins (120,-20)/(120,20)
        c.Value = "1u";
        P("Ground", new Vec2(0, 60), Rotation.R0);
        W([new Vec2(0, -20), new Vec2(0, -40), new Vec2(40, -40)]);
        W([new Vec2(80, -40), new Vec2(120, -40), new Vec2(120, -20)]);
        W([new Vec2(120, 20), new Vec2(120, 60), new Vec2(0, 60)]);
        W([new Vec2(0, 20), new Vec2(0, 60)]);
    });
    var sim = MustBuild(d);
    double dt = 1e-6;
    for (int s2 = 0; s2 < 1000; s2++) sim.Step(dt); // t = 1 ms = one time constant (R=1k, C=1u)
    double? vc = sim.GetVoltageAt(new Vec2(120, -20));
    double expected = 10.0 * (1 - Math.Exp(-1));
    Check(vc != null && Math.Abs(vc.Value - expected) < 0.05,
        $"RC at t=tau: {vc:0.###} V vs {expected:0.###} V");
}

// -------------------------------------------------------------- RL step
Console.WriteLine("Simulation, RL transient:");
{
    SymbolInstance? l = null;
    var d = SimDoc((doc, P, W) =>
    {
        var v = P("VSource", new Vec2(0, 0), Rotation.R0);
        v.Value = "10V";
        var r = P("Resistor", new Vec2(60, -40), Rotation.R0);
        r.Value = "1k";
        l = P("Inductor", new Vec2(120, 0), Rotation.R90);      // pins (120,-20)/(120,20)
        l.Value = "10m";
        P("Ground", new Vec2(0, 60), Rotation.R0);
        W([new Vec2(0, -20), new Vec2(0, -40), new Vec2(40, -40)]);
        W([new Vec2(80, -40), new Vec2(120, -40), new Vec2(120, -20)]);
        W([new Vec2(120, 20), new Vec2(120, 60), new Vec2(0, 60)]);
        W([new Vec2(0, 20), new Vec2(0, 60)]);
    });
    var sim = MustBuild(d);
    double dt = 1e-7; // tau = L/R = 10 us
    for (int s2 = 0; s2 < 100; s2++) sim.Step(dt); // t = tau
    double? il = sim.GetCurrent(l!);
    double expected = 10.0 / 1e3 * (1 - Math.Exp(-1));
    Check(il != null && Math.Abs(Math.Abs(il.Value) - expected) < 3e-4,
        $"RL at t=tau: |I| {Math.Abs(il ?? 0):0.######} vs {expected:0.######}");
}

// ----------------------------------------------------------------- AC + amp
Console.WriteLine("Simulation, AC source:");
{
    var d = SimDoc((doc, P, W) =>
    {
        var v = P("ACSource", new Vec2(0, 0), Rotation.R0);
        v.Value = "5V 50Hz";
        var r = P("Resistor", new Vec2(60, 0), Rotation.R90);   // pins (60,-20)/(60,20)
        P("Ground", new Vec2(0, 60), Rotation.R0);
        W([new Vec2(0, -20), new Vec2(0, -40), new Vec2(60, -40), new Vec2(60, -20)]);
        W([new Vec2(60, 20), new Vec2(60, 60), new Vec2(0, 60)]);
        W([new Vec2(0, 20), new Vec2(0, 60)]);
    });
    var sim = MustBuild(d);
    double dt = 5e-5, vmax = 0;
    for (int s2 = 0; s2 < 400; s2++)   // one 50 Hz period
    {
        sim.Step(dt);
        double v = sim.GetVoltageAt(new Vec2(60, -20)) ?? 0;
        vmax = Math.Max(vmax, v);
    }
    Check(Math.Abs(vmax - 5.0) < 0.05, $"AC peak = 5 V (got {vmax:0.###})");
    // Value at a known phase: t = 400*dt = 20 ms -> sin(2*pi) = 0.
    double vEnd = sim.GetVoltageAt(new Vec2(60, -20)) ?? 99;
    Check(Math.Abs(vEnd) < 0.2, $"AC value at full period ≈ 0 (got {vEnd:0.###})");
}

// ------------------------------------------------------------ switch + lamp
Console.WriteLine("Simulation, switch and lamp:");
{
    SymbolInstance? swi = null, lamp = null;
    var d = SimDoc((doc, P, W) =>
    {
        var v = P("Battery", new Vec2(0, 0), Rotation.R0);
        v.Value = "12V";
        swi = P("Switch", new Vec2(60, -40), Rotation.R0);       // pins (40,-40)/(80,-40)
        lamp = P("Lamp", new Vec2(120, 0), Rotation.R90);       // pins (120,-20)/(120,20)
        lamp.Value = "12V 5W";
        P("Ground", new Vec2(0, 60), Rotation.R0);
        W([new Vec2(0, -20), new Vec2(0, -40), new Vec2(40, -40)]);
        W([new Vec2(80, -40), new Vec2(120, -40), new Vec2(120, -20)]);
        W([new Vec2(120, 20), new Vec2(120, 60), new Vec2(0, 60)]);
        W([new Vec2(0, 20), new Vec2(0, 60)]);
    });
    var sim = MustBuild(d);
    sim.Step(1e-4);
    Check(sim.GetLampBrightness(lamp!) < 0.01, "open switch: lamp dark");
    double iOff = Math.Abs(sim.GetCurrent(lamp!) ?? 1);
    Check(iOff < 1e-4, $"open switch: no current (got {iOff:0.######})");

    swi!.StateOn = true;   // click!
    sim.Step(1e-4);
    double br = sim.GetLampBrightness(lamp!);
    Check(br > 0.95, $"closed switch: lamp at full brightness (got {br:0.###})");
    double iOn = Math.Abs(sim.GetCurrent(lamp!) ?? 0);
    Check(Math.Abs(iOn - 12.0 / 28.8) < 0.01, $"lamp current ≈ 0.417 A (got {iOn:0.###})");

    swi.StateOn = false;
    sim.Step(1e-4);
    Check(sim.GetLampBrightness(lamp!) < 0.01, "switch reopened: lamp dark again");
}

// ------------------------------------------------------------------- fuse
Console.WriteLine("Simulation, fuse:");
{
    SymbolInstance? fuse = null;
    var d = SimDoc((doc, P, W) =>
    {
        var v = P("Battery", new Vec2(0, 0), Rotation.R0);
        v.Value = "9V";
        fuse = P("Fuse", new Vec2(60, -40), Rotation.R0);
        fuse.Value = "1A";
        var r = P("Resistor", new Vec2(120, 0), Rotation.R90);
        r.Value = "1";                                          // 9 A > 1 A rating
        P("Ground", new Vec2(0, 60), Rotation.R0);
        W([new Vec2(0, -20), new Vec2(0, -40), new Vec2(40, -40)]);
        W([new Vec2(80, -40), new Vec2(120, -40), new Vec2(120, -20)]);
        W([new Vec2(120, 20), new Vec2(120, 60), new Vec2(0, 60)]);
        W([new Vec2(0, 20), new Vec2(0, 60)]);
    });
    var sim = MustBuild(d);
    sim.Step(1e-4);
    Check(sim.IsFuseBlown(fuse!), "overcurrent blew the fuse");
    sim.Step(1e-4);
    double i = Math.Abs(sim.GetCurrent(fuse!) ?? 1);
    Check(i < 1e-3, $"blown fuse conducts nothing (got {i:0.####})");
    sim.Reset();
    Check(!sim.IsFuseBlown(fuse!), "reset un-blows the fuse");
}

// ------------------------------------------------------- diode rectifier
Console.WriteLine("Simulation, diode rectifier:");
{
    var d = SimDoc((doc, P, W) =>
    {
        var v = P("ACSource", new Vec2(0, 0), Rotation.R0);
        v.Value = "5V 50Hz";
        var diode = P("Diode", new Vec2(60, -40), Rotation.R0); // anode left
        var r = P("Resistor", new Vec2(120, 0), Rotation.R90);
        r.Value = "1k";
        P("Ground", new Vec2(0, 60), Rotation.R0);
        W([new Vec2(0, -20), new Vec2(0, -40), new Vec2(40, -40)]);
        W([new Vec2(80, -40), new Vec2(120, -40), new Vec2(120, -20)]);
        W([new Vec2(120, 20), new Vec2(120, 60), new Vec2(0, 60)]);
        W([new Vec2(0, 20), new Vec2(0, 60)]);
    });
    var sim = MustBuild(d);
    double dt = 5e-5, vmax = 0, vmin = 0;
    for (int s2 = 0; s2 < 800; s2++)   // two periods
    {
        sim.Step(dt);
        double v = sim.GetVoltageAt(new Vec2(120, -40)) ?? 0;
        vmax = Math.Max(vmax, v);
        vmin = Math.Min(vmin, v);
    }
    Check(Math.Abs(vmax - 4.3) < 0.15, $"rectified peak ≈ 5 - 0.7 (got {vmax:0.###})");
    Check(vmin > -0.2, $"negative half suppressed (got min {vmin:0.###})");
}

// ------------------------------------------------- two separate ground nets
Console.WriteLine("Simulation, ground merging:");
{
    var d = SimDoc((doc, P, W) =>
    {
        var v = P("VSource", new Vec2(0, 0), Rotation.R0);
        v.Value = "10V";
        var r = P("Resistor", new Vec2(60, -40), Rotation.R0);
        P("Ground", new Vec2(120, 0), Rotation.R0);             // grounds are not wired together
        P("Ground", new Vec2(0, 60), Rotation.R0);
        W([new Vec2(0, -20), new Vec2(0, -40), new Vec2(40, -40)]);
        W([new Vec2(80, -40), new Vec2(120, -40), new Vec2(120, 0)]);
        W([new Vec2(0, 20), new Vec2(0, 60)]);
    });
    var sim = MustBuild(d);
    sim.Step(1e-4);
    double? top = sim.GetVoltageAt(new Vec2(0, -40));
    Check(top != null && Math.Abs(top.Value - 10.0) < 1e-3,
        $"separate grounds form one reference (got {top:0.###})");
}

// -------------------------------------------------- probe persistence
Console.WriteLine("JSON, probe roundtrip:");
{
    var d = SimDoc((doc, P, W) =>
    {
        var v = P("VSource", new Vec2(0, 0), Rotation.R0);
        var r = P("Resistor", new Vec2(60, 0), Rotation.R90);
        P("Ground", new Vec2(0, 60), Rotation.R0);
        W([new Vec2(0, -20), new Vec2(0, -40), new Vec2(60, -40), new Vec2(60, -20)]);
        W([new Vec2(60, 20), new Vec2(60, 60), new Vec2(0, 60)]);
        W([new Vec2(0, 20), new Vec2(0, 60)]);
    });
    var rsym = d.Symbols.First(s2 => s2.Definition.Name == "Resistor");
    var saved = JsonIo.Save(d, [new ProbeInfo("V", 60, -40), new ProbeInfo("I", SymbolId: rsym.Id)]);
    var loaded = JsonIo.Load(saved, out var probes);
    Check(probes.Count == 2, $"two probes roundtrip (got {probes.Count})");
    Check(probes[0].Type == "V" && Math.Abs(probes[0].X - 60) < 1e-9, "voltage probe anchor kept");
    Check(probes[1].Type == "I" && probes[1].SymbolId == rsym.Id, "current probe symbol id kept");
    var legacy = JsonIo.Load(JsonIo.Save(d), out var none);
    Check(none.Count == 0 && legacy.Symbols.Count() == 3, "probe-free files load fine");

    var net = NetlistExtractor.Extract(d).FindNetAt(new Vec2(30, -40));
    Check(net != null, "FindNetAt hits a segment interior");
    Check(NetlistExtractor.Extract(d).FindNetAt(new Vec2(300, 300)) == null, "FindNetAt misses empty space");
}

// ------------------------------------------------- build-time diagnostics
Console.WriteLine("Simulation, diagnostics:");
{
    var d1 = SimDoc((doc, P, W) =>
    {
        var v = P("VSource", new Vec2(0, 0), Rotation.R0);
        var r = P("Resistor", new Vec2(60, 0), Rotation.R90);
        W([new Vec2(0, -20), new Vec2(0, -40), new Vec2(60, -40), new Vec2(60, -20)]);
        W([new Vec2(0, 20), new Vec2(0, 40), new Vec2(60, 40), new Vec2(60, 20)]);
    });
    var sim1 = CircuitSimulator.Build(d1, NetlistExtractor.Extract(d1), out var p1);
    Check(sim1 == null && p1.Any(x => x.Contains("ground", StringComparison.OrdinalIgnoreCase)),
        "missing ground reported");

    var d2 = SimDoc((doc, P, W) =>
    {
        var r = P("Resistor", new Vec2(60, 0), Rotation.R90);
        P("Ground", new Vec2(60, 60), Rotation.R0);
        W([new Vec2(60, 20), new Vec2(60, 60)]);
    });
    var sim2 = CircuitSimulator.Build(d2, NetlistExtractor.Extract(d2), out var p2);
    Check(sim2 == null && p2.Any(x => x.Contains("source", StringComparison.OrdinalIgnoreCase)),
        "missing source reported");
}

// ------------------------------------------------------------------ summary
Console.WriteLine($"\n{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

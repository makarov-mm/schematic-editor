# Examples

Open any file with **File → Open** and press **▶ Run** — every example ships
with probes already placed on the interesting nets, so the oscilloscope lights
up immediately. Add your own with the probe tool (click a wire for voltage, a
component body for current); probes are saved with the file.

| File | What it shows | Try this |
|---|---|---|
| `bridge-power-supply.schem.json` | **The showcase**: a complete linear PSU — 15 V 50 Hz source, full-wave diode bridge (D1–D4), 2200 µF reservoir, 10 kΩ bleeder, LC filter (100 mH choke + 470 µF), a DC-side 2 A fuse, always-on indicator lamp, switch and a 12 V 5 W load. | Probe the reservoir net and the output net, 20 ms window: sawtooth **100 Hz** ripple (full-wave doubles the frequency!) on the reservoir, nearly flat DC after the choke (1.8 → 0.08 V p-p). Close `S1` — the main lamp lights, ripple grows under load. The fuse sits *after* the filter on purpose: at power-up the reservoir inrush peaks around 10 A and would kill any fast fuse on the AC side. |
| `voltage-multiplier.schem.json` | **Cockcroft-Walton cascade** (two Greinacher stages): 15 V AC in → ~25 V after stage 1 → ~48 V DC after stage 2. Four diodes, four caps — the topology behind X-ray tubes and CRT anodes. | Probe both stage outputs, 2 s window, and watch the ladder pump itself up over a couple of seconds. Then close `S1`: a mere 4.7 kΩ load collapses the output to ~14 V — CW multipliers are famously weak sources, and the simulator shows exactly why. Open it and watch the slow recovery. |
| `rlc-resonance.schem.json` | **Series resonance**: 100 mH and 100 µF resonate at 50.3 Hz — almost exactly the 50 Hz source. `S1` shorts the 22 Ω damping resistor, flipping Q from ~1.2 to ~9.5. | Probe the capacitor net and the source, 0.2 s window. Damped: ~6 V on the cap. Close `S1` — the 5 V source rings the capacitor up to **~42 V**, and you see the envelope grow over ~10 cycles (τ = 2L/R ≈ 60 ms). Resonant voltage magnification, live. |
| `lamp-and-switch.schem.json` | The interactive flagship: 12 V battery → 2 A fuse → switch → 12 V 5 W lamp. | Run, click `S1` — the lamp glows at full rated power (≈0.42 A). Probe `E1` for current. |
| `fuse-blow.schem.json` | Overcurrent protection. A 1 Ω load would draw 9 A through a 1 A fuse. | Run, close `S1` — `F1` blows instantly (red ✕), current stops. **Reset** un-blows it. |
| `rc-charging.schem.json` | First-order transient, τ = R·C = 100 kΩ × 10 µF = **1 s** — slow enough to watch live. | Probe the capacitor's top net, set the scope to the 2 s window, close `S1` and watch the exponential rise to 10 V. Open — watch it hold (no discharge path). |
| `half-wave-rectifier.schem.json` | AC → DC: 10 V 50 Hz source, diode, 47 µF smoothing cap, 1 kΩ load. | Probe the source's top net and the load's top net; 20 ms scope window. You'll see the sine and, above its negative half, the charged cap sagging between peaks (ripple ≈ 9.3 → 6.4 V). |
| `voltage-divider.schem.json` | DC bias basics: 10 V across 1 kΩ + 2 kΩ. | Probe the midpoint: 6.67 V. Hover any wire in run mode for an instant readout. |
| `demo.schem.json` | The original editing demo (battery, switch, resistor, T-junction to ground). | Good for trying the editing tools; it simulates too. |

All circuits pass ERC with no errors and were verified against analytic
solutions by the generator that produced them.

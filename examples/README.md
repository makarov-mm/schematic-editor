# Examples

Open any file with **File → Open**, press **▶ Run**, then explore. Suggested
probes: arm the probe tool, click a wire for voltage or a component body for
current.

| File | What it shows | Try this |
|---|---|---|
| `lamp-and-switch.schem.json` | The interactive flagship: 12 V battery → 2 A fuse → switch → 12 V 5 W lamp. | Run, click `S1` — the lamp glows at full rated power (≈0.42 A). Probe `E1` for current. |
| `fuse-blow.schem.json` | Overcurrent protection. A 1 Ω load would draw 9 A through a 1 A fuse. | Run, close `S1` — `F1` blows instantly (red ✕), current stops. **Reset** un-blows it. |
| `rc-charging.schem.json` | First-order transient, τ = R·C = 100 kΩ × 10 µF = **1 s** — slow enough to watch live. | Probe the capacitor's top net, set the scope to the 2 s window, close `S1` and watch the exponential rise to 10 V. Open — watch it hold (no discharge path). |
| `half-wave-rectifier.schem.json` | AC → DC: 10 V 50 Hz source, diode, 47 µF smoothing cap, 1 kΩ load. | Probe the source's top net and the load's top net; 20 ms scope window. You'll see the sine and, above its negative half, the charged cap sagging between peaks (ripple ≈ 9.3 → 6.4 V). |
| `voltage-divider.schem.json` | DC bias basics: 10 V across 1 kΩ + 2 kΩ. | Probe the midpoint: 6.67 V. Hover any wire in run mode for an instant readout. |
| `demo.schem.json` | The original editing demo (battery, switch, resistor, T-junction to ground). | Good for trying the editing tools; it simulates too. |

All circuits pass ERC with no errors and were verified against analytic
solutions by the generator that produced them.

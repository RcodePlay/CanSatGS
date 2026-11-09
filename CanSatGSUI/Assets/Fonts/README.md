# Fonts

The three CanTauri typefaces, embedded so the app looks the same on every computer.
All are free under the SIL Open Font License (see the `OFL-*.txt` files).

| File | Face | Used for |
| --- | --- | --- |
| `Fraunces-Bold.ttf` | Fraunces 700 | Panel titles, callsign-style headings, big numbers, mission clock |
| `Jost-Light/Regular/Medium/Bold.ttf` | Jost 300–700 | Labels, body text, buttons |
| `SpaceMono-Regular/Bold.ttf` | Space Mono 400/700 | Telemetry values, timestamps, small caps labels |

Fraunces is a variable font. Avalonia can't set its axes, so `Fraunces-Bold.ttf` is a static instance
cut from Google Fonts' `Fraunces[SOFT,WONK,opsz,wght].ttf` with the design system's settings already
applied: `wght 700, opsz 144, SOFT 30, WONK 1`. Jost was cut from `Jost[wght].ttf` the same way
(fontTools `varLib.instancer`). Space Mono files are the originals.

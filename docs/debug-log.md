# Debug log

## 2026-10-21: close button drifts left on the 150% monitor

**Machine.** Windows 11, two monitors: 2200x1650 at 150% (primary, system DPI 144)
and 3840x2160 at 200% (secondary).

**Symptom.** On the 150% monitor the close button's clickable area sits to the left
of the drawn button; clicking the button itself does nothing. The 200% monitor is fine.

**Affected version.** Release `v2025.06.30`. That build is DPI-unaware (no DPI entry
in its manifest, `SetProcessDPIAware` declared but never called), so Windows
bitmap-stretches it and its hit testing does not match what is drawn on that screen.
Why only the 150% monitor drifts was not determined; that code no longer exists.

**Fixed in.** Commit `a9aaf48` (2026-09-21, "Make the process DPI aware and scale tab
layout to match"), which makes the process PerMonitorV2. Every later tag includes it:
`v2026.10.07`, `v2026.10.10`, and the current beta `v2026.10.17-beta.1`. A Release
build of current `main` was confirmed working on both monitors by the user.

**Dead end, do not repeat.** A first attempt rescaled mouse-message points by
`Dpi.value() / GetDpiForSystem()` in `TabStrip`. That is wrong for a PerMonitorV2
process, where message points, cursor positions and window rectangles are already
physical pixels of their screen. On the 200% monitor (factor 192/144) it pushed every
pointer position outside the strip, so hover, click and drag stopped working and the
collapsed strip never expanded. It was reverted; see the DPI note in
[architecture.md](architecture.md).

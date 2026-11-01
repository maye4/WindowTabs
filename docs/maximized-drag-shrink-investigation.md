# Drag from maximized sometimes keeps the full-screen size, 2026-11-01

Source: `62e1df2` on `main`, plus the current uncommitted recovery implementation and diagnostics in
`WtProgram/DesktopPlugins/InputManagerPlugin.fs` and `WtProgram/WindowGroup.fs`
(listed under [Current working tree](#current-working-tree)). This is an
investigation record. A final-guard live trace covered 37 caption drags: 35 finalizations all
ended at 1920x1080, 22 stale-loop recoveries ran, and no abort, rejection, timeout or crash was
logged. The terminal final correction itself was not exercised.

## Symptom

A group's top window is maximized. The user presses its title bar and drags. Expected:
the window shrinks to its normal size under the pointer, with the shrink animation that
`38d5599` introduced. About 1 drag in 10 to 20, the window instead moves at its full
maximized size (3866x2186 on the test monitor) for the whole drag and is dropped at
that size. The group's other window is then aligned to that wrong frame.

Reproduced on a 3840x2160 monitor at 192 dpi (virtual coordinates around
`-831,-2161`), a second monitor at 144 dpi, a group of 2 windows. The application that
owns the dragged window (hwnd `395746` in the traces) was not identified. Its title bar
is partly custom drawn: some presses on it answer `WM_NCHITTEST` with `HTCLIENT`.

## How the shrink works (since `38d5599`)

1. `InputManagerPlugin.llHook`, `WM_LBUTTONDOWN`: `captionGroup` sends `WM_NCHITTEST`
   (20 ms timeout) and accepts only `HTCAPTION`. The group's other windows are hidden
   (`WindowGroup.pressHide`).
2. `llHook`, `WM_MOUSEMOVE`: once the pointer leaves the system drag box
   (`CaptionPress.isDrag`), WindowTabs restores the window itself with
   `MaximizedRestore.restore` ([WindowGroup.fs:246](../WtProgram/WindowGroup.fs#L246)).
   That calls `SetWindowPlacement` with `SW_SHOWNOACTIVATE` and a normal rectangle placed
   under the pointer (`MaximizedRestore.dragFrame`). The point is that a window
   un-maximized by `SetWindowPlacement` plays the shrink animation, while one
   un-maximized by the system move loop plays a cross-fade.
3. The hook does not block. The restore is dispatched, and the mouse move continues to
   the application. The application's `DefWindowProc` runs the system move loop.

The restore and the move loop run on different threads, in different processes, and are
not ordered. That is the source of the bug.

## Evidence

Traces from `WINDOWTABS_DRAG_TRACE`. Times are ms since WindowTabs started and are
written when WindowTabs' thread handles the event, not when Windows raised it.

### Failed drag

```
76177 CAPTION-PRESS hwnd=395746 pt=1773,-2133
76186 CAPTION-DRAG hwnd=395746 start=1773,-2133 now=1772,-2129 second=false
76186 CAPTION-RESTORE hwnd=395746 rcNormal=483,-2143 1920x1080
76204 CAPTION-RESTORED hwnd=395746 bounds=483,-2143 1920x1080 maximized=false
76216 START top=395746 dpi=192 members=2 bounds=483,-2143 1920x1080 maximized=false
76221 MOVE-LOC top=395746 bounds=483,-2143 1920x1080 maximized=false
76254 MOVE-LOC top=395746 bounds=-861,-2135 3866x2186 maximized=false
76325 MOVE-LOC top=395746 bounds=-955,-2036 3866x2186 maximized=false
 ...  (every later move is 3866x2186)
77303 END top=395746 stillMoving=false dpi=192
77304 RESIZE-HIDDEN hwnd=199822 tid=16 target=-1098,-1850 3866x2186
```

The window's maximized frame on this monitor is `-843,-2173 3866x2186` (from the
`ALIGN ... cmd=3` lines). `-861,-2135` is that frame shifted by the pointer's movement
since the press.

### Good drag, same session

```
74136 CAPTION-DRAG hwnd=395746 start=1791,-2125 now=1791,-2122 second=false
74136 CAPTION-RESTORE hwnd=395746 rcNormal=494,-2144 1920x1080
74143 CAPTION-RESTORED hwnd=395746 bounds=494,-2144 1920x1080 maximized=false
74169 START top=395746 dpi=192 members=2 bounds=494,-2144 1920x1080 maximized=false
74174 START top=395746 dpi=192 members=2 bounds=494,-2144 1920x1080 maximized=false
74198 MOVE-LOC top=395746 bounds=471,-2093 1920x1080 maximized=false
 ...  (every later move is 1920x1080)
```

### All six drags in that session

| Drag | `SetWindowPlacement` duration (DRAG to RESTORED) | `START` lines | Result |
|---|---|---|---|
| 1 | 30 ms (65289 to 65319) | 1 | shrank |
| 2 | 11 ms | 2 | shrank |
| 3 | 12 ms | 1 | shrank |
| 4 | 7 ms | 1 | shrank |
| 5 | 7 ms | 2 | shrank |
| 6 | 18 ms | 1 | full size |

Drag 1 took longer than drag 6 and still shrank, so the duration alone does not predict
the failure.

### Earlier session (before the last trace lines were added)

Same pattern: `CAPTION-RESTORE rcNormal=538,-2144 1920x842` was written 1 ms after
`CAPTION-DRAG`, and the drag still ended with the group frame at `3866x2186`.

## What is established

- WindowTabs' restore succeeds. Right after `SetWindowPlacement` returns, the window is
  1920x1080 and not maximized.
- The system move loop then positions the window with the frame it had while
  maximized, offset by the pointer's movement. It keeps doing so for the whole loop.
- Because the window is no longer maximized when the loop moves it, the loop does not
  run its own un-maximize either. Nothing corrects the size before the drop.
- The hit test, the follower hiding and the dispatch of the restore are not the cause in
  these traces: the restore ran within 0 to 1 ms of the drag being detected in every
  failed drag recorded.

## Not established (historical hypothesis, superseded by the 132716 trace below)

The move loop captures the window's rectangle before WindowTabs' `SetWindowPlacement`
reaches the application: probably on `WM_NCLBUTTONDOWN` / `SC_MOVE` entry, or at
`WM_ENTERSIZEMOVE`, both of which can happen before the drag threshold is passed. In most
drags the restore is processed before the loop first uses that rectangle. When the
application thread is busy at that moment, the loop's stale maximized rectangle wins. It
is not known why the good drags 2 and 5 log two `START` events and the failed drag logs
one, or whether that matters.

To prove the order, record the `dwmsEventTime` that Windows passes to the WinEvent
callback for `EVENT_SYSTEM_MOVESIZESTART` and for each `EVENT_OBJECT_LOCATIONCHANGE` of
the top window, and the `GetTickCount` time just before and after `SetWindowPlacement`.
Compare them in a failed drag. Spy++ on the application thread (`WM_NCLBUTTONDOWN`,
`WM_SYSCOMMAND`, `WM_ENTERSIZEMOVE`, `WM_WINDOWPOSCHANGING`) would also show the order.

## Ruled out

- **Hit-test misses** (`CAPTION-MISS`). Some presses on this window returned `HTCLIENT`
  quickly (`answered=true hit=1`). WindowTabs ignores those, the system handles the drag,
  and those drags shrank normally. No timeout (`answered=false`) was seen.
- **A long restore dispatch.** The first theory was that the restore waited on the group
  thread behind `pressHide`. The traces show that it was not blocked there. They do not
  establish that it preceded the move loop's rectangle capture, or which component later
  reapplied the exact old geometry.
- **Restore skipped.** No `CAPTION-RESTORE-SKIPPED` line was logged.

## Fix options

1. **Restart the move loop when it is detected (implemented).** During a drag that
   WindowTabs restored, the first `EVENT_OBJECT_LOCATIONCHANGE` that shows the window at
   or above the work area's size while not maximized means the loop has the stale
   rectangle. Send a bounded `WM_CANCELMODE`, confirm that the native loop has actually
   gone idle, put the window back at the restored size under the worker's pointer, then
   post `WM_SYSCOMMAND SC_MOVE | HTCAPTION` at the pointer's current position while the
   button is still down. The implementation does not use `ReleaseCapture` from
   WindowTabs' thread because the capture belongs to the target application. Cost:
   possibly a one-frame jump, only in drags that hit the race. Allow one restart per
   press and retain a passive cleanup watchdog if cancellation or restart fails.
2. **Correct the size on every move.** Fights the loop, which reapplies the stale size on
   every mouse move. Expect flicker. Not recommended.
3. **Stop restoring ourselves.** Let the move loop un-maximize. `MaximizedRestore.store`
   (called from `saveTopWindowPlacement`) already writes the corrected normal rectangle,
   so the size is right. This gives up the shrink animation and gets the system's
   cross-fade, the behaviour before `38d5599`.
4. **Restore before the loop starts** (worth investigating). If the loop captures on
   `WM_NCLBUTTONDOWN`, the restore cannot come after the drag threshold. Options would be
   swallowing the press in the low-level hook and replaying it after restoring, which is
   intrusive and affects double-click.

## Current working tree

Uncommitted, on top of `62e1df2`:

- [InputManagerPlugin.fs](../WtProgram/DesktopPlugins/InputManagerPlugin.fs)
  - The drag threshold now hands the press to `WindowGroup.captionDragStarted`; the
    group captures the restore plan and a worker performs the native placement.
  - Trace `CAPTION-MISS` when a press on a grouped window is not `HTCAPTION`.
- [WindowGroup.fs](../WtProgram/WindowGroup.fs)
  - `MaximizedRestore.Plan` captures the original size, intended frame, identity and
    generation before the worker starts. The token publishes the initial native result
    separately from the group callback.
  - A stale normal window at the original maximized size triggers one bounded
    `WM_CANCELMODE`, stable-idle poll, verified reposition and a posted
    `SC_MOVE | HTCAPTION`. Replacement success requires the intended normal size.
  - Release retains the Plan for one `Finalizing` worker. It corrects a surviving stale
    frame at the immutable release point before save, solo-size capture or follower
    alignment. New press/ownership changes invalidate it; a rejected frame marker blocks
    late synthetic END/LOCATION paths.
  - Trace `CAPTION-RESTORE`, `CAPTION-PLACE`, `CAPTION-RESTORED`,
    `RECOVERY-DETECTED`, `RECOVERY-CANCEL`, `RECOVERY-LOOP-STOPPED`,
    `RECOVERY-RESTART-STALE`, `RECOVERY-FINALIZE`, `RECOVERY-FINAL-REPAIR` and their
    abort/timeout paths.
  - `START` trace also logs the top window's bounds and maximized state.
  - Trace `MOVE-LOC` for each location change of the top window during a move.

All trace lines write only when `WINDOWTABS_DRAG_TRACE` is set. `TabInteraction.fsx`
has deterministic assertions for the stale-frame guard, release/finalization split,
one-worker guard, commit-after-repair condition, invalidation, ordinary false positives,
cancel/restart ordering and stable-idle sampling. They were not run while this
implementation was written and have not been run since.

## Live final-guard validation

A user-run trace covered 37 caption drags. It recorded 35 finalizations, all at 1920x1080;
22 stale-loop recoveries; and zero abort, rejection, timeout or crash lines. The trace exercised
successful finalization and recovery cleanup, but not the terminal final correction because no
finalization found a stale frame to repair. The deterministic suites remain unrun.

## Reproducing with a trace

```powershell
$env:WINDOWTABS_DRAG_TRACE = "$env:TEMP\wt-drag.log"
Start-Process WtProgram\bin\Debug\WindowTabs.exe -WorkingDirectory WtProgram\bin\Debug
```

Starting a new copy makes the running one exit. Group two windows, maximize them, then
repeatedly press the top window's title bar and drag. Double-click the title bar between
drags to maximize again. A failed drag shows `MOVE-LOC` lines at the maximized size
after `CAPTION-RESTORED` reported the normal size.

## Confirmed root causes, ranked

The trace below supersedes the earlier "best hypothesis" ranking. It proves that the
restore itself succeeded, that restart success was accepted at the stale size, and that
the later save/alignment propagated it. It does not identify the exact process or timing
that wrote the stale geometry.

1. **Plausible contributor, exact writer unproven: the old loop appeared to stop before
   the stale frame arrived.** The trace records one idle sample, a successful
   reposition, and a posted replacement loop; that replacement then reports the original
   maximized size. The trace does not prove the native move mode stayed idle or resumed
   too early. A custom target/compositor writer or later unwind could recreate the same
   geometry. The new stable-idle check is conservative mitigation, not proof.
2. **High, confirmed: release discarded the final stale-frame guard.** The retained plan
   was dropped as soon as the replacement appeared idle. The next `END` was treated as
   an ordinary successful drag, so `saveTopWindowPlacement` and `captureSoloSize` accepted
   the huge frame and follower alignment propagated it.
3. **High, confirmed: restart success was inferred from move mode alone.** The code did
   not require the intended normal size before promoting the replacement to `Restarted`.
   The trace's first post-restart sample was already `3866x2186 maximized=false`.
4. **Medium: release and invalidation were one operation.** Cancelling native work at
   release also destroyed the Plan, leaving nothing that could repair the frame before
   save/align.
5. **Low: the initial native restore result was not published separately from the group
   phase callback.** Release/finalization could not distinguish a pending or failed
   initial restore from one that had actually succeeded.

Failed live trace
(`wt-drag-recovery-20261101-132716.log`, milliseconds from WindowTabs start):

```text
297117 RECOVERY-DETECTED Edge hwnd=199822 bounds=-845,-2165 3866x2186 normal=520,-2048 1920x1080
297119 RECOVERY-CANCEL sent=true
297120 RECOVERY-LOOP-STOPPED stopped=true
297132 CAPTION-PLACE rcNormal=512,-2040 1920x1080 restored=true
297133 RECOVERY-REPOSITION placed=true
297133 RECOVERY-START ... posted=true
297171 RECOVERY-RESTARTED
297171 MOVE-LOC bounds=-863,-2138 3866x2186 maximized=false
297172 END stillMoving=true
297172 START bounds=-863,-2138 3866x2186 maximized=false
297604 END stillMoving=false
297606 RESIZE-HIDDEN target=-1148,-1934 3866x2186
297645 ALIGN target=-1148,-1934 3866x2186
```

The fix retains the Plan across release, requires stable idle, the initial restore result and
completion of any recovery worker, verifies the replacement size, and performs at most one
terminal release-point repair before save/align. The later final-guard trace was clean across
37 drags, but it did not exercise the terminal correction path. The new deterministic assertions
have not been run.

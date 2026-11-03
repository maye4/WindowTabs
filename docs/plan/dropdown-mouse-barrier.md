# Drop-down mouse barrier

## Goal

When a drop-down strip opens below the boundary between monitors, prevent the
pointer from overshooting upward into monitor 1 within the strip's horizontal
span. Keep sideways and downward movement available so the pointer can enter
the menu or leave the strip.

## Proposed approach

Use the existing `WH_MOUSE_LL` hook in `InputManagerPlugin.fs` to detect upward
pointer movement crossing the boundary within the open strip's bounds. Check
the segment between the previous and proposed pointer positions so fast
diagonal movement cannot skip over the barrier. Suppress a crossing and clamp
the pointer to the permitted edge with `SetCursorPos`.

Publish the active strip bounds and boundary coordinate as a small immutable
snapshot that the hook can read without waiting on a group thread. Enable the
barrier while the strip is open, and clear it when the strip closes, a drag
starts, or the owning strip is destroyed. Keep the hook callback limited to a
geometry check and cursor correction.

`ClipCursor` is a poor fit for this behavior because it confines the pointer to
a rectangle; the desired barrier is only one edge segment and must leave
sideways exits open.

## Validation points

- Verify slow, fast, and diagonal upward movements cannot cross the barrier.
- Verify horizontal travel, downward entry into the menu, and leaving at either
  side still work.
- Verify dismissing the menu, starting a drag, and destroying the strip always
  release the barrier.
- Verify the correction feels smooth and does not create repeated cursor-hook
  events.

## References

- `WtProgram/DesktopPlugins/InputManagerPlugin.fs` installs the existing mouse
  hook.
- `Win32/WinUser.cs` declares `ClipCursor`; add a `SetCursorPos` declaration if
  the implementation uses it and it is not already available elsewhere.
- [LowLevelMouseProc](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelmouseproc)
- [SetCursorPos](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setcursorpos)
- [ClipCursor](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-clipcursor)

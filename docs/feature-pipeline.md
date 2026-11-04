
# a new tab joining a group

## tab related actions
'[tab][tab]' -(a new tab joining)-> '[tab][tab][shadow tab]' -(mouse release)-> '[tab][tab][tab]'

The shadow tab fades in over 250 ms as it rises into its slot; a drag that leaves without dropping
fades it off the row over the same time.

## animation

Normal-sized windows ease from their current frame into the group's frame over 180 ms, and back to
their own frame when dragged out. The animation covers position changes as well as size changes:
Windows only supplies a native animation for maximize/restore, not normal-to-normal frame changes.
The native worker uses elapsed time and skips overdue frames; pointer updates retarget the same
transition without restarting it. An ownership change cancels the remaining frames before the next
group places the window. Windows' animation-effects setting is respected. Ordinary group moves and
resizes still align immediately, as do pointer-only moves while floating outside a group.

# a new tab joining a full-screen group

## window related actions
'original window' -(a new tab joining a full-screen group)-> 'full-screen window'

## animation
'[group strip]' -(a new tab joining)-> '[group strip] held at the top' ->
'[group strip]' over the group's top window

The joining window grows into the group's frame with the system's expand animation, which draws over
everything below it. The strip is held at the top of the z-order until 350 ms after placement finishes
(at most 1.5 s from the first request), directly under the tab the drag
carries so the tab in the hand stays above it, and then put back immediately above the group's top
window. A tab leaving a maximized group is the same, with the shrink animation.

## Bug: drag a edge tab out
current behavior:
drag a edge browser tab out of edge -> a new edge window A -> windowTabs auto add this window to the existing group -> the whole group dragged to the window A position

wanted behavior:
drag a edge browser tab out of edge -> a new edge window A -> windowTabs should not auto add this window to the existing grou, even auto group is on, because when a suer drag a edge tab our of edge, he certainly want a new window -> the previous group should stay where it is, not dragged to the window A position
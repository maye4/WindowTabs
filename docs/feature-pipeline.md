
# a new tab joining a group

## tab related actions
'[tab][tab]' -(a new tab joining)-> '[tab][tab][shadow tab]' -(mouse release)-> '[tab][tab][tab]'

The shadow tab fades in over 250 ms as it rises into its slot; a drag that leaves without dropping
fades it off the row over the same time.

## animation


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

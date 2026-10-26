namespace Bemo
open System

type IDragDropParent =
    abstract member dragBegin : unit -> unit
    abstract member dragDrop : (Pt * obj) -> unit
    abstract member dragEnd : unit -> unit

type IDragDropNotification =
    abstract member dragBegin : unit -> unit
    abstract member dragEnd : unit -> unit

type IDragDropTarget =
    inherit IDragDropNotification
    abstract member dragEnter : obj -> Pt -> bool
    abstract member dragMove : Pt -> unit
    abstract member dragExit : unit -> unit

/// The tab a drag takes off its strip. The window dragged out of its group shows the real window
/// under the pointer, so the tab has to come along: without it the window loses the chrome the
/// strip drew for it the moment the pointer leaves.
type DragTabSprite = {
    /// The floating tab for a window whose visible frame is this: a key that changes exactly when the
    /// picture or the slot has to change, the picture itself, and where it goes. One call per frame,
    /// so the strip is asked for the placement once rather than once per part.
    frame : Rect -> obj * Img * Pt
    /// Frees what the picture needed while the drag ran: the tab's icon, which the group gives up
    /// with the tab and the drag had to copy.
    release : unit -> unit
    }

type IDragDrop =
    abstract member registerNotification: IDragDropNotification -> unit
    abstract member unregisterNotification: IDragDropNotification -> unit
    abstract member registerTarget : (IntPtr * IDragDropTarget) -> unit
    abstract member unregisterTarget : IntPtr -> unit
    /// A tab dragged out of its group shows the real window under the pointer, carrying its tab.
    abstract member beginDrag : (IntPtr * Pt * obj * DragTabSprite) -> unit
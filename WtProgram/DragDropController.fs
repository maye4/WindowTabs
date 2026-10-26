namespace Bemo
open System
open System.Drawing
open System.Windows.Forms

/// The icon a drag carries for the strip it is about to land in. A tab's icon is disposed with the
/// tab as it leaves its group — before the strip it lands in ever asks for it — so the drag copies it
/// when it starts and hands that copy over. One drag runs at a time, so one slot is enough.
module DragIcon =
    /// Never the held value itself: that is None, and therefore null, until a drag starts.
    let private gate = obj()
    let mutable private held : Icon option = None
    /// Copy the tab's icon now, while it is still the strip's to give.
    let remember (icon:Icon) =
        // Written on the strip's thread, read by whichever strip the pointer reaches.
        lock gate (fun() ->
            held |> Option.iter(fun previous -> previous.Dispose())
            held <- Some(new Icon(icon, icon.Width, icon.Height)))
    /// The copy, for a strip that wants to draw the tab with it. It stays the drag's to dispose, so a
    /// strip the pointer leaves and comes back to finds it again.
    let current () = lock gate (fun() -> held)
    /// Let go of the copy once no strip can be drawing with it, as when a drag is abandoned. The drop
    /// does not call this: the strips it was handed to clear their previews on a posted message, so
    /// the copy outlives the drop and the next drag's remember replaces it.
    let release () =
        lock gate (fun() ->
            held |> Option.iter(fun previous -> previous.Dispose())
            held <- None)

type IDragState =
    abstract member mouseMove : Pt -> unit
    abstract member dispose : unit -> unit

type DragDetectingStateInfo = {
    initialPt : Pt
    onBegin : unit -> unit
    }

type DragDetectingState(info:DragDetectingStateInfo) =
    /// Pointer travel that turns a press into a drag. Kept small so a drag begins as soon as the
    /// pointer does, but above the pixel or two a hand moves while pressing.
    let dragStartDistance = 3.0
    interface IDragState with
        member this.mouseMove(ptScreen) =
            if ptScreen.distance(info.initialPt) > dragStartDistance then
                info.onBegin()
        member this.dispose() = ()

/// The tab a floating drag carries over the window it moves. The strip renders it, so the window
/// wears the tab the user grabbed, and it is placed where the strip the drop leaves the window
/// with draws that tab, so letting go does not move it.
type FloatingTab(os:OS, sprite:DragTabSprite, grabOffset:Sz -> Pt) =
    let window = AnimationWindow(os)
    do  window.setAlpha(byte 0xFF)
    /// The grabbed point inside the picture as drawn now. The picture is redrawn for the frame it
    /// hangs over, so the point moves with it, and the tab keeps the cursor on the same spot.
    let mutable anchor = Pt()
    /// The frame geometry the picture was drawn for. The strip the drop leaves draws its row a corner
    /// shape and a pixel apart from the strip the drag started in, so the picture is redrawn when that
    /// geometry changes — not on every pointer move.
    let mutable drawnFor : obj = null
    let place (visible:Rect) =
        let key, image, location = sprite.frame visible
        if not(obj.Equals(drawnFor, key)) then
            window.setImage(image)
            // Once, on the first picture: the anchor is where the user took hold of the tab, and a
            // redraw for a frame a pixel apart must not move the tab out from under the cursor.
            if isNull drawnFor then anchor <- grabOffset image.size
            drawnFor <- key
        location
    /// Follow the pointer, not the strip's row: the tab is held where it was grabbed, so a window
    /// changing frame underneath it cannot move it out from under the cursor.
    member this.follow(ptScreen:Pt) (visible:Rect) =
        place visible |> ignore
        window.setLocation(ptScreen.sub(anchor))
        window.setIsVisible(true)
    /// The grabbed point inside the picture, for a strip taking the tab over to hold it by.
    member this.anchorOffset = anchor
    /// Hold the tab at a screen point, keeping the picture it already has: over a strip the window
    /// belongs to the group, so only the tab the user is holding follows the pointer.
    member this.holdAt(topLeft:Pt) =
        window.setLocation(topLeft)
        window.setIsVisible(true)
    member this.hide() = window.setIsVisible(false)
    member this.hwnd = window.hwnd
    interface IDisposable with
        member this.Dispose() = window.Dispose()

type DragCapturedStateInfo = {
    target : IDragDropTarget
    targetHwnd : IntPtr
    targetWindow : Window
    /// The tab the user is holding. The strip shows where it would land, and this keeps the tab
    /// itself under the pointer, so it does not escape the mouse the moment the group forms.
    tab : FloatingTab option
    tabOffset : Pt
    onDragOut : Pt -> unit
    }

type DragCapturedState(info:DragCapturedStateInfo) =
    /// How far past the strip a tab has to be before it leaves the group and the window starts to
    /// float. This was 20, which the hand read as "nothing happens" before the drag began.
    let dragOutDistance = 6
    interface IDragState with
        member this.mouseMove(ptScreen) =
            let dragBounds = info.targetWindow.bounds.inflate(dragOutDistance, dragOutDistance)
            if dragBounds.containsPoint(ptScreen) then
                info.target.dragMove(info.targetWindow.ptToClient(ptScreen))
                info.tab |> Option.iter(fun tab -> tab.holdAt(ptScreen.sub(info.tabOffset)))
            else
                info.target.dragExit()
                info.onDragOut(ptScreen)
        member this.dispose() = ()

type DragFloatingStateInfo = {
    targets: Map2<IntPtr, IDragDropTarget>
    /// The tab dragged out of a group leaves its window showing under the pointer, at its real
    /// size, until it is dropped or dragged back in.
    window : Window
    normalSize : Sz option
    /// The tab that left the strip, drawn over the window for as long as it floats.
    tab : FloatingTab option
    onDragIn: (IntPtr * Pt) -> unit
    }

type DragFloatingState(info:DragFloatingStateInfo) =
    let os = OS()
    member this.follow(ptScreen) =
        info.window.followDrag(ptScreen) info.normalSize
        info.tab |> Option.iter(fun tab -> tab.follow ptScreen info.window.visibleBounds)
    interface IDragState with
        member this.mouseMove(ptScreen) =
            // The dragged window stays shown all the way to a target strip: hiding it when
            // the pointer came near the strip took the tab's own chrome away mid-drag. The
            // strip's dragEnter still merges the tab, and the window follows the pointer
            // again if that is refused.
            let targetHwnd = os.windowAtPt(ptScreen).hwnd
            match info.targets.tryFind(targetHwnd) with
            | Some _ -> info.onDragIn(targetHwnd, ptScreen)
            | None -> this.follow(ptScreen)
        // The tab is not hidden here: entering a strip hands it over to the captured state, which
        // keeps holding it under the pointer until the drag ends.
        member this.dispose() = ()

type DragActionInfo = {
    targets : Map2<IntPtr, IDragDropTarget>
    notifications: Set2<IDragDropNotification>
    initialHwnd : IntPtr
    initialPt : Pt
    data: obj
    /// The tab the drag takes off the strip, so the floating window keeps its chrome.
    tab : DragTabSprite
    onCancel : unit -> unit
    onBegin : unit -> unit
    onDrop : Pt -> unit
    onEnd : unit -> unit
    }

type DragAction(info:DragActionInfo) =
    let os = OS()
    let Cell = CellScope(true, false)
    let ptScreenCell = Cell.create(info.initialPt)
    let dragStateCell = Cell.create(None:Option<IDragState>)
    let captureWindowCell = Cell.create(None:Option<IWindow>)
    // A tab dragged out of a group shows its window under the pointer instead of a
    // shrunk drag image: the same window is moved back into a group, never parked.
    let dragWindowCell = Cell.create(None:Option<Window * Sz option>)
    let floatingTabCell = Cell.create(None:Option<FloatingTab>)
    let timer = new Timer()

    member this.setNextState(newState:obj) =
        let newState = unbox<IDragState>(newState)
        dragStateCell.value.iter <| fun state -> state.dispose()
        dragStateCell.set(Some(newState))

    member this.captureWindow : Window = os.windowFromHwnd(captureWindowCell.value.Value.hwnd)

    member this.captureEnded(ptScreen) =
        this.captureWindow.releaseCapture()
        (captureWindowCell.value.Value :?>IDisposable).Dispose()
        timer.Dispose()
        dragStateCell.value.Value.dispose()
        floatingTabCell.value.iter(fun tab -> (tab :> IDisposable).Dispose())
        // The sprite is built on mouse-down, before this drag proves to be one: a click that never
        // moves would otherwise keep the tab's copied icon for the life of the process.
        info.tab.release()
        match dragStateCell.value.Value with
        | :? DragDetectingState ->
            info.onCancel()
        | :? DragCapturedState ->
            info.targets.values.iter <| fun target -> target.dragEnd()
            info.notifications.items.iter <| fun n -> n.dragEnd()
            info.onEnd()
        | :? DragFloatingState ->
            info.targets.values.iter <| fun target -> target.dragEnd()
            info.notifications.items.iter <| fun n -> n.dragEnd()
            // The drop places the same window made visible under the pointer; it is the
            // user's window now, so nothing hides it afterwards.
            info.onDrop(ptScreen)
            info.onEnd()
        | _ -> ()

    member this.wndProc (msg:Win32Message) =
        let ptScreen() =             
            let pt = msg.lParam.location
            let ptScreen = this.captureWindow.ptToScreen(pt)
            ptScreenCell.set(ptScreen)
            ptScreen
        match msg.msg with
        | WindowMessages.WM_MOUSEMOVE -> 
            dragStateCell.value.Value.mouseMove(ptScreen())
        | WindowMessages.WM_MOUSELEAVE
        | WindowMessages.WM_LBUTTONUP -> 
            this.captureEnded(ptScreen())
        | _ -> ()
        msg.def()

    /// A tab strip is dragged on its group's thread, which also owns the tab's window, so the
    /// calls stay on that thread: going through an invoker would queue the moves behind the
    /// captures this mouse hook holds. The source strip's handle names the dragged window.
    member private this.draggedWindow =
        dragWindowCell.value |> Option.map(fst) |> Option.defaultValue (os.windowFromHwnd(info.initialHwnd))

    member this.dragFloat(ptScreen) =
        let state = DragFloatingState({
            targets = info.targets
            window = this.draggedWindow
            normalSize = dragWindowCell.value |> Option.map snd |> Option.flatten
            tab = floatingTabCell.value
            onDragIn = fun (targetHwnd, ptScreen) ->
                this.dragEnter(targetHwnd, ptScreen, false)
        })
        this.setNextState state
        // The transition itself must show both pieces, even if the mouse stops here.
        state.follow(ptScreen)

    member this.dragEnter(targetHwnd, ptScreen, isInitial) =
        let target = info.targets.find(targetHwnd)
        let targetWindow = os.windowFromHwnd(targetHwnd)
        let ptTarget = targetWindow.ptToClient(ptScreen)
        if target.dragEnter info.data ptTarget then
            let tabOffset =
                match floatingTabCell.value with
                | Some tab -> tab.anchorOffset
                | None ->
                    match info.data with
                    | :? TabDragInfo as dragInfo -> dragInfo.tabOffset
                    | _ -> Pt()
            this.setNextState <| DragCapturedState({
                target = target
                targetHwnd = targetHwnd
                targetWindow = targetWindow
                tab = floatingTabCell.value
                tabOffset = tabOffset
                onDragOut = fun(ptScreen) -> 
                    let targetHwnd = os.windowAtPt(ptScreen).hwnd
                    match info.targets.tryFind(targetHwnd) with
                    | Some _ -> this.dragEnter(targetHwnd, ptScreen, false)
                    | None -> this.dragFloat(ptScreen)
            })
        else 
            if isInitial then target.dragExit()
            this.dragFloat(ptScreen)

    member this.dragDetect() =
        this.setNextState <| DragDetectingState({
            initialPt = info.initialPt
            onBegin = fun() ->  
                dragWindowCell.value <-
                    match info.data with
                    | :? TabDragInfo as dragInfo ->
                        let (Tab hwnd) = dragInfo.tab
                        Some(os.windowFromHwnd(hwnd), dragInfo.lastNormalSize)
                    | _ -> None
                // Rendered now: the strip gives the tab up as soon as the drag leaves it, and
                // with it the tab info the picture is drawn from. A picture that will not draw
                // costs the drag its tab, not the drag.
                let grabOffset =
                    match info.data with
                    | :? TabDragInfo as dragInfo -> fun (size:Sz) -> dragInfo.offsetIn size
                    | _ -> fun (_:Sz) -> Pt()
                try floatingTabCell.set(Some(new FloatingTab(os, info.tab, grabOffset))) with _ -> ()
                info.targets.values.iter <| fun target -> target.dragBegin()
                info.notifications.items.iter <| fun n -> n.dragBegin()
                info.onBegin()
                this.dragEnter(info.initialHwnd, info.initialPt, true)
        })

    member this.start() =
        if captureWindowCell.value.IsSome then failwith "already started"
        captureWindowCell.set(Some(os.createWindow this.wndProc 0 0))
        this.captureWindow.setCapture()
        timer.Interval <- 500
        timer.Tick.Add <| fun _ -> if this.captureWindow.hasCapture.not then this.captureEnded(ptScreenCell.value)
        timer.Start()
        this.dragDetect()

type DragDropController(parent:IDragDropParent) =
    let lockObj = obj()
    let withLock = lock lockObj
    let Cell = CellScope(true, false)
    let targetsCell = Cell.create(Map2())
    let notificationsCell = Cell.create(Set2())
    let dragActionCell = Cell.create(None)

    interface IDragDrop with
        member x.registerNotification(notify) = withLock <| fun() ->
            notificationsCell.map(fun l -> l.add notify)
        member x.unregisterNotification(notify) = withLock <| fun() ->
            notificationsCell.map(fun l -> l.remove notify)
        member x.registerTarget((hwnd, target)) = withLock <| fun() ->
            targetsCell.map(fun targets -> targets.add hwnd target)
        member x.unregisterTarget(hwnd) = withLock <| fun() ->
            targetsCell.map(fun targets -> targets.remove hwnd)
        member x.beginDrag((initialHwnd, initialPt, data, tab)) = withLock <| fun() ->
            if dragActionCell.value.IsNone then 
                let dragAction = DragAction({
                    targets = targetsCell.value
                    notifications = notificationsCell.value
                    initialHwnd = initialHwnd
                    initialPt = initialPt
                    data = data
                    tab = tab
                    onCancel = fun() -> 
                        dragActionCell.set(None)
                    onBegin = fun() -> 
                        parent.dragBegin()
                    onDrop = fun pt ->
                        parent.dragDrop(pt, data)
                    onEnd = fun() ->    
                        parent.dragEnd()
                        dragActionCell.set(None)
                })
                dragActionCell.set(Some(dragAction))
                try dragAction.start()
                with error ->
                    dragActionCell.set(None)
                    tab.release()
                    reraise()
            else
                // No second drag begins, so nothing would ever release what this one brought.
                tab.release()
            ()


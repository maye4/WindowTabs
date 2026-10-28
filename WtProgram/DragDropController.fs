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
    /// Let go of the copy once no strip can be drawing with it. Called at the end of every capture,
    /// after the targets have been told the drag is over: each strip that previewed holds a copy of its
    /// own, so nothing reads this one after that point.
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

/// The tab a drag carries, in its own layered window over the one it moves. The strip draws the
/// picture, and the tab is placed where a strip holding the window's current frame puts it, so it sits
/// on the window's edge and a window changing frame moves the picture with it. While floating, its
/// shape is always a floating window's: outside the frame, corners up.
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
    /// The size the anchor was measured for.
    let mutable anchorSize = Sz()
    /// Where the picture's top-left corner is now. Tracked where the picture is placed, so the flight
    /// into a slot starts from what the hand can see rather than from where the pointer is.
    let mutable position = Pt()
    /// The flight into a slot, if one is running. It belongs to the drag and goes with it.
    let mutable flight : Timer option = None
    let stopFlight () =
        flight |> Option.iter(fun timer -> timer.Stop(); timer.Dispose())
        flight <- None
    let place (visible:Rect) =
        let key, image, location = sprite.frame visible
        if not(obj.Equals(drawnFor, key)) then
            window.setImage(image)
            // The grab point scales with the picture: a redraw of the same size leaves it alone, while
            // a strip that draws the tab wider or narrower moves the point with it.
            if isNull drawnFor || anchorSize <> image.size then anchor <- grabOffset image.size
            anchorSize <- image.size
            drawnFor <- key
        location
    /// Wear the window it moves: the tab is drawn at the row position the strip gives a window of this
    /// frame, so it always sits flush on the window's edge. Holding it by the grabbed point instead left
    /// a gap or an overlap between the two, by wherever the hand happened to close on the tab.
    member this.follow(ptScreen:Pt) (visible:Rect) =
        let location = place visible
        position <- location
        window.setLocation(location)
        window.setIsVisible(true)
    /// The grabbed point inside the picture, for a strip taking the tab over to hold it by.
    member this.anchorOffset = anchor
    /// Draw the picture for this frame, and put the tab where the hand is holding it. Used for the
    /// first frame of a drag: the picture has to exist, but the tab belongs under the cursor, not at
    /// the row position of the window it was taken from.
    member this.takeHold(ptScreen:Pt) (visible:Rect) =
        place visible |> ignore
        this.holdAt(ptScreen.sub(anchor))
    /// Hold the tab at a screen point, keeping the picture it already has: over a strip the window
    /// belongs to the group, so only the tab the user is holding follows the pointer.
    member this.holdAt(location:Pt) =
        position <- location
        window.setLocation(location)
        window.setIsVisible(true)
    /// Where the picture is drawn now: where the flight to a slot starts, and what the trace reports.
    member this.topLeft = position
    /// Fly the picture into the slot the strip took the tab into. The tab the hand let go of moves to
    /// where the strip already shades the place, so the drop reads as one gesture instead of a cut.
    /// A timer on this thread steps it and a stopwatch decides the progress, because a queued tick
    /// arrives late and unevenly; the last frame is the target itself, and `arrived` runs once.
    member this.animateTo (target:Pt) (milliseconds:int) (arrived:unit -> unit) =
        stopFlight()
        let from = position
        let duration = float (max 1 milliseconds)
        let clock = Diagnostics.Stopwatch.StartNew()
        let finished = ref false
        let step () =
            if not finished.Value then
                let progress = min 1.0 (float clock.ElapsedMilliseconds / duration)
                if progress >= 1.0 then
                    finished.Value <- true
                    stopFlight()
                    position <- target
                    window.setLocation(target)
                    arrived()
                else
                    // Cubic ease-out: most of the distance in the first frames, no overshoot.
                    let eased = 1.0 - pown (1.0 - progress) 3
                    let at = Pt(from.x + int (Math.Round(eased * float (target.x - from.x))),
                                from.y + int (Math.Round(eased * float (target.y - from.y))))
                    position <- at
                    window.setLocation(at)
        let timer = new Timer(Interval = 16)
        timer.Tick.Add <| fun _ -> step()
        flight <- Some timer
        // The first step now rather than at the first tick: waiting 16 ms shows as a pause on release.
        step()
        if not finished.Value then timer.Start()
    member this.hwnd = window.hwnd
    interface IDisposable with
        // A flight still in progress is stopped first: its timer must not step a disposed window.
        member this.Dispose() =
            stopFlight()
            window.Dispose()

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
    /// The strip showing the preview this drag would land in.
    member this.target = info.target
    /// Where that strip leaves the tab once the drop commits, or None if it is not taking it.
    member this.landing = info.target.dropLanding()
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
    /// The end of the drag happens once, however many of the timer, the mouse-leave and the
    /// button-up arrive: the rest find the capture gone and the icon copies already released.
    let mutable ended = false
    /// How long the tab takes to fly into the slot it was dropped into: long enough to read as the
    /// tab landing, short enough not to hold up the next gesture.
    let landMilliseconds = 175

    member this.setNextState(newState:obj) =
        let newState = unbox<IDragState>(newState)
        dragStateCell.value.iter <| fun state -> state.dispose()
        dragStateCell.set(Some(newState))

    member this.captureWindow : Window = os.windowFromHwnd(captureWindowCell.value.Value.hwnd)

    /// The window a tab drag carries, when the drag is one.
    member private this.draggedHwnd =
        match info.data with
        | :? TabDragInfo as dragInfo -> let (Tab hwnd) = dragInfo.tab in hwnd
        | _ -> IntPtr.Zero

    member this.captureEnded(ptScreen) =
        if not ended then
            ended <- true
            this.captureWindow.releaseCapture()
            let captureWindow = captureWindowCell.value
            captureWindowCell.set None
            captureWindow |> Option.iter(fun window -> (window :?> IDisposable).Dispose())
            timer.Dispose()
            let state = dragStateCell.value
            dragStateCell.set None
            state |> Option.iter(fun s -> s.dispose())
            // The tab the hand let go of outlives the capture when a strip has taken it: that picture
            // is what has to be seen arriving in the slot, and it is disposed when it gets there.
            let tab = floatingTabCell.value
            floatingTabCell.set None
            // Asked before the drop commits: the strip's order already holds the tab in the slot its
            // preview chose, and the shade drawn for it is still there to measure the slot against.
            let landing, landingTarget =
                match state with
                | Some(:? DragCapturedState as captured) -> captured.landing, Some captured.target
                | _ -> None, None
            // The sprite is built on mouse-down, before this drag proves to be one: a click that never
            // moves would otherwise keep the tab's copied icon for the life of the process.
            info.tab.release()
            match state with
            | Some(:? DragDetectingState) ->
                this.land tab landing landingTarget
                info.onCancel()
            | Some(:? DragCapturedState) ->
                info.targets.values.iter <| fun target -> target.dragEnd()
                info.notifications.items.iter <| fun n -> n.dragEnd()
                info.onEnd()
                this.land tab landing landingTarget
            | Some(:? DragFloatingState) ->
                info.targets.values.iter <| fun target -> target.dragEnd()
                info.notifications.items.iter <| fun n -> n.dragEnd()
                // The drop places the same window made visible under the pointer; it is the
                // user's window now, so nothing hides it afterwards.
                info.onDrop(ptScreen)
                info.onEnd()
                // Nothing took the tab into a row: it was dropped on the desktop, so there is no slot
                // to fly to and the picture goes at once.
                this.land tab None None
            | _ -> this.land tab None None
            // After the previews were told to clear: every strip that took one holds its own copy,
            // and the drag's copy has no reader left.
            DragIcon.release()

    /// Show the tab arriving: the picture the hand let go of flies from where it was drawn to the slot
    /// the strip marked, and the strip stops shading the slot once it is there. Only the arrival moves
    /// — what the drop committed was committed before this runs, so a flight cut short changes
    /// nothing. With no slot to fly to, the overlay goes at once, as it always has.
    member private this.land (tab:FloatingTab option) (landing:Rect option) (target:IDragDropTarget option) =
        let dispose () = tab |> Option.iter(fun tab -> (tab :> IDisposable).Dispose())
        match tab, target, landing with
        | Some(tab), Some(target), Some(landing) ->
            let finished = ref false
            let arrived (cancelled:bool) =
                if not finished.Value then
                    finished.Value <- true
                    dispose()
                    if not cancelled then
                        try target.dragLanded() with _ -> ()
                    DragTrace.write(sprintf "LANDED cancelled=%b" cancelled)
            DragTrace.write(sprintf "LAND from=%d,%d to=%s ms=%d"
                                tab.topLeft.x tab.topLeft.y (DragTrace.rect landing) landMilliseconds)
            try tab.animateTo landing.location landMilliseconds (fun() -> arrived false)
            with _ -> arrived true
        // A target that took the drop but gave no rectangle still owns a shade: telling it that the
        // tab has landed is what clears it. Without this, a drop whose slot could not be measured
        // left the strip shading the tab for good.
        | _ ->
            dispose()
            target |> Option.iter(fun target ->
                try target.dragLanded() with _ -> ())
            if target.IsSome then DragTrace.write(sprintf "LANDED nocrect cancelled=false")

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
            // The captured state holds the tab by the pointer; doing it now keeps the hand and the
            // picture in agreement from this frame on, instead of at the next mouse move.
            floatingTabCell.value |> Option.iter(fun tab -> tab.holdAt(ptScreen.sub(tabOffset)))
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
                // Drawn and anchored before any strip is asked to take the tab, and placed under the
                // hand: an unprimed overlay holds a tab with no picture, and priming it by the row of
                // the window it came from sends the tab to the top edge first.
                match dragWindowCell.value, floatingTabCell.value with
                | Some(window, _), Some tab -> tab.takeHold info.initialPt window.visibleBounds
                | _ -> ()
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


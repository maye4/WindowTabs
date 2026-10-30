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

/// The window the drag draws the tab it carries in. It is topmost, so a strip raised over a window's
/// maximize or restore animation goes directly under it: the strip is topmost too, and moving above
/// this window would cover the tab in the hand until that hold ends. One drag runs at a time, so one
/// slot is enough, as with DragIcon.
module DragTabWindow =
    let private gate = obj()
    let mutable private held = IntPtr.Zero
    let set (hwnd:IntPtr) = lock gate (fun() -> held <- hwnd)
    let clear (hwnd:IntPtr) = lock gate (fun() -> if held=hwnd then held <- IntPtr.Zero)
    let current () = lock gate (fun() -> held)

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
        // From the first frame: a strip can be asked to hold itself over an animation before the
        // hand has even left the group it started in.
        DragTabWindow.set window.hwnd
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
    /// Keep the original grip and picture size: rounding each rescale moves the grip a pixel at a
    /// time when the hand crosses between monitors. None while the tab follows its window's edge.
    let mutable grip : (Pt * Sz) option = None
    /// The monitor whose scale the picture is drawn for, and its rect: read again only when the
    /// pointer leaves that rect, which is once per crossing rather than once per move.
    let mutable monitorRect = Rect()
    let mutable dpi = Dpi.value()
    /// The frame the picture was last laid over, so the captured phase can redraw without being
    /// handed the window again.
    let mutable lastVisible = Rect()
    /// The flight into a slot, if one is running. It belongs to the drag and goes with it.
    let mutable flight : Timer option = None
    let stopFlight () =
        flight |> Option.iter(fun timer -> timer.Stop(); timer.Dispose())
        flight <- None
    /// The scale the picture is drawn for: the monitor the pointer is over. A pointer in the gap
    /// between monitors keeps the last one, so the tab never falls back to the source monitor's size.
    let dpiAt (pt:Pt) =
        if monitorRect.containsPoint pt then dpi
        else
            // Both together or neither: Dpi.forMonitorPoint answers for the nearest monitor, so a point
            // in a gap beside another monitor would store that monitor's scale next to this one's rect,
            // and the picture would keep the wrong scale on the way back.
            (match Monitors.all().list |> List.tryFind(fun mon -> mon.displayRect.containsPoint pt) with
             | Some mon ->
                 monitorRect <- mon.displayRect
                 dpi <- Dpi.forMonitorPoint pt
             | None -> ())
            dpi
    /// The tab casts the shadow it casts in a strip, so picking it up does not take the shadow away.
    /// The shadow is drawn into the picture, which makes the window larger than the tab by this much
    /// above and beside it; everything else here speaks of the tab, and only the window is offset.
    let shadowRenderer = TabShadow.Renderer()
    let mutable shadowOffset = Pt()
    let withShadow (image:Img) =
        try
            let padding = Dpi.withValue dpi TabShadow.padding
            let shadow = shadowRenderer.Render(image.width, image.height, TabShadow.silhouette image.bitmap, padding, TabUp)
            try
                use g = Graphics.FromImage(shadow)
                g.DrawImageUnscaled(image.bitmap, padding, padding)
                shadowOffset <- Pt(padding, padding)
                Img(shadow)
            with _ ->
                shadow.Dispose()
                reraise()
        finally image.bitmap.Dispose()
    let setLocation (tabAt:Pt) = window.setLocation(tabAt.sub(shadowOffset))
    let place (pt:Pt) (visible:Rect) =
        lastVisible <- visible
        let key, location, render = sprite.frame (dpiAt pt) visible
        if not(obj.Equals(drawnFor, key)) then
            let image = render()
            DragTrace.write(sprintf "RESCALE dpi=%d size=%dx%d" dpi image.size.width image.size.height)
            // The grab point scales with the picture: a redraw of the same size leaves it alone, while
            // a strip that draws the tab wider or narrower moves the point with it.
            if isNull drawnFor || anchorSize <> image.size then anchor <- grabOffset image.size
            anchorSize <- image.size
            window.setImage(withShadow image)
            drawnFor <- key
        location
    /// Wear the window it moves: the tab is drawn at the row position the strip gives a window of this
    /// frame, so it always sits flush on the window's edge. Holding it by the grabbed point instead left
    /// a gap or an overlap between the two, by wherever the hand happened to close on the tab.
    member this.follow(ptScreen:Pt) (visible:Rect) =
        let location = place ptScreen visible
        grip <- None
        position <- location
        setLocation(location)
        window.setIsVisible(true)
    /// The grabbed point inside the picture, for a strip taking the tab over to hold it by.
    member this.anchorOffset = anchor
    /// Draw the picture for this frame, and put the tab where the hand is holding it. Used for the
    /// first frame of a drag: the picture has to exist, but the tab belongs under the cursor, not at
    /// the row position of the window it was taken from.
    member this.takeHold(ptScreen:Pt) (visible:Rect) =
        place ptScreen visible |> ignore
        this.holdAt(ptScreen.sub(anchor))
    /// Hold the tab by the grabbed point, redrawn for the monitor the hand is over. Over a strip the
    /// window belongs to the group, so only the tab the user is holding follows the pointer - but it
    /// still has to be the size of the monitor it is over, not of the one the drag began on.
    /// A strip takes the tab over where the hand already holds it: a floating tab sits on its window's
    /// edge rather than at the grab point, and snapping to that point made the tab jump as the preview
    /// appeared.
    member this.holdBy(ptScreen:Pt) =
        let held, heldSize = grip |> Option.defaultWith(fun () -> ptScreen.sub(position), anchorSize)
        if not lastVisible.size.isEmptyArea then place ptScreen lastVisible |> ignore
        grip <- Some(held, heldSize)
        let scaleBy offset source target = int(Math.Round(float offset * float target / float(max 1 source)))
        let held =
            if anchorSize = heldSize then held
            else Pt(scaleBy held.x heldSize.width anchorSize.width, scaleBy held.y heldSize.height anchorSize.height)
        this.holdAt(ptScreen.sub(held))
    /// Hold the tab at a screen point, keeping the picture it already has.
    member this.holdAt(location:Pt) =
        position <- location
        setLocation(location)
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
        let frames = ref 0
        let step () =
            if not finished.Value then
                let progress = min 1.0 (float clock.ElapsedMilliseconds / duration)
                if progress >= 1.0 then
                    finished.Value <- true
                    stopFlight()
                    position <- target
                    setLocation(target)
                    DragTrace.write(sprintf "FLIGHT end elapsed=%dms frames=%d" clock.ElapsedMilliseconds frames.Value)
                    arrived()
                else
                    frames := !frames + 1
                    DragTrace.write(sprintf "FLIGHT frame %d elapsed=%dms" frames.Value clock.ElapsedMilliseconds)
                    // Cubic ease-out: most of the distance in the first frames, no overshoot.
                    let eased = 1.0 - pown (1.0 - progress) 3
                    let at = Pt(from.x + int (Math.Round(eased * float (target.x - from.x))),
                                from.y + int (Math.Round(eased * float (target.y - from.y))))
                    position <- at
                    setLocation(at)
        let timer = new Timer(Interval = 16)
        timer.Tick.Add <| fun _ -> step()
        flight <- Some timer
        DragTrace.write(sprintf "FLIGHT begin from=%d,%d to=%d,%d ms=%d thread=%d"
                            from.x from.y target.x target.y milliseconds
                            System.Threading.Thread.CurrentThread.ManagedThreadId)
        // The first step now rather than at the first tick: waiting 16 ms shows as a pause on release.
        step()
        if not finished.Value then timer.Start()
    member this.hwnd = window.hwnd
    interface IDisposable with
        // A flight still in progress is stopped first: its timer must not step a disposed window.
        member this.Dispose() =
            stopFlight()
            // The slot outlives this object if it is not cleared here, and a stale handle is what a
            // strip holding itself over the next animation would try to go under.
            DragTabWindow.clear window.hwnd
            window.Dispose()

type DragCapturedStateInfo = {
    target : IDragDropTarget
    targetHwnd : IntPtr
    targetWindow : Window
    /// The tab the user is holding. The strip shows where it would land, and this keeps the tab
    /// itself under the pointer, so it does not escape the mouse the moment the group forms.
    tab : FloatingTab option
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
                info.tab |> Option.iter(fun tab -> tab.holdBy ptScreen)
            else
                info.target.dragExit()
                info.onDragOut(ptScreen)
        member this.dispose() = ()

/// Moves the dragged window off the drag's thread. A move waits for the app to lay itself out, which
/// for a large window or a slow app is longer than a pointer move, so the tab and the pointer stalled
/// with it. Only the newest frame matters: one asked for while a move runs replaces any still waiting.
type DragWindowMover(window:Window, ?moveWindow:Pt * Sz -> unit) =
    let gate = obj()
    let mutable wanted : (Pt * Sz) option = None
    let mutable running = false
    let mutable disposed = false
    let mutable settled : (unit -> unit) option = None
    let customMove = moveWindow.IsSome
    let moveWindow = defaultArg moveWindow (fun (visibleAt,size) -> window.followDragTo visibleAt size)
    let apply (visibleAt,size) =
        let start = window.bounds
        if start.size<>size && DragTrace.enabled then
            DragTrace.write(sprintf "RESIZE-FLOAT hwnd=%d tid=%d custom=%b enabled=%b visible=%b minimized=%b maximized=%b fullscreen=%b moving=%b start=%s size=%dx%d"
                                (window.hwnd.ToInt64()) Threading.Thread.CurrentThread.ManagedThreadId customMove
                                (WindowFrameAnimation.enabled()) window.isVisible window.isMinimized window.isMaximized window.isFullScreen
                                window.isInMoveSize (DragTrace.rect start) size.width size.height)
        if not customMove && window.isVisible && not window.isMinimized && not window.isMaximized &&
           not window.isFullScreen && start.size<>size && WindowFrameAnimation.enabled() then
            let mutable latest = visibleAt,size
            let target () =
                lock gate (fun () ->
                    wanted |> Option.iter(fun value -> latest <- value)
                    wanted <- None)
                let point,size = latest
                Rect(point.add(window.dragFrameOffset),size)
            let current () =
                lock gate (fun () -> not disposed && settled.IsNone) &&
                WinUserApi.IsWindow(window.hwnd) && window.isVisible && not window.isMinimized && not window.isMaximized && not window.isInMoveSize
            WindowFrameAnimation.run start target window.move current
            if DragTrace.enabled then
                let cancelled = lock gate (fun () -> disposed || settled.IsSome)
                DragTrace.write(sprintf "RESIZE-FLOAT-END hwnd=%d tid=%d handoff=%b visible=%b minimized=%b maximized=%b moving=%b actual=%s"
                                    (window.hwnd.ToInt64()) Threading.Thread.CurrentThread.ManagedThreadId cancelled
                                    window.isVisible window.isMinimized window.isMaximized window.isInMoveSize (DragTrace.rect window.bounds))
        else moveWindow(visibleAt,size)
    let rec run () =
        let next,after = lock gate (fun () ->
            let next = wanted
            wanted <- None
            if next.IsNone then
                running <- false
                let after = settled
                settled <- None
                next,after
            else next,None)
        match next with
        | Some(visibleAt,size) ->
            (try if WinUserApi.IsWindow window.hwnd then apply(visibleAt,size)
             with error -> Diagnostics.Trace.TraceWarning("Drag move failed: {0}",error.Message))
            run()
        | None -> after |> Option.iter(fun action -> action())
    member this.isMoving = lock gate (fun () -> running)

    /// Ask for the window to show at this frame; returns at once.
    member this.request (visibleAt:Pt) (size:Sz) =
        lock gate (fun () ->
            if not disposed then
                settled <- None
                wanted <- Some(visibleAt,size)
                if not running then
                    running <- true
                    Threading.ThreadPool.QueueUserWorkItem(fun _ -> run()) |> ignore)
    /// Finish the in-flight native call before handing the window over, without blocking the UI.
    member this.settle (after:unit -> unit) =
        let now = lock gate (fun () ->
            wanted <- None
            if running then settled <- Some after; false
            else true)
        if now then after()
    interface IDisposable with
        member this.Dispose() = lock gate (fun () ->
            disposed <- true
            wanted <- None
            settled <- None)

type DragFloatingStateInfo = {
    targets: Map2<IntPtr, IDragDropTarget>
    /// The tab dragged out of a group leaves its window showing under the pointer, at its real
    /// size, until it is dropped or dragged back in.
    window : Window
    normalSize : Sz option
    mover : DragWindowMover
    /// The tab that left the strip, drawn over the window for as long as it floats.
    tab : FloatingTab option
    onDragIn: (IntPtr * Pt) -> unit
    }

type DragFloatingState(info:DragFloatingStateInfo) =
    let os = OS()
    let timer = new System.Windows.Forms.Timer(Interval=16)
    let mutable pointer = Pt()
    let drawTab() =
        if WinUserApi.IsWindow(info.window.hwnd) then
            info.tab |> Option.iter(fun tab -> tab.follow pointer info.window.visibleBounds)
    do timer.Tick.Add(fun _ ->
        // Sample before drawing: a worker finishing during the draw still needs one final refresh.
        let moving = info.mover.isMoving
        drawTab()
        if not moving then timer.Stop())
    member this.follow(ptScreen) =
        // Only the owning STA draws the tab. It follows actual frames during the resize, including
        // when the pointer stops, rather than detaching from the window at the final requested size.
        let window = info.window
        let size = window.dragSize ptScreen (info.normalSize |> Option.orElse (Some window.placement.rcNormalPosition.size))
        let visibleAt = window.dragVisibleAt ptScreen size
        pointer <- ptScreen
        info.mover.request visibleAt size
        drawTab()
        if info.tab.IsSome then timer.Start()
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
        member this.dispose() = timer.Dispose()

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
    let mutable mover : DragWindowMover option = None
    let invoker = InvokerService.invoker
    let mutable handoff = 0L
    let mutable awaitingMoves = false
    let mutable pendingEnter : (IntPtr * Pt * bool) option = None
    let floatingTabCell = Cell.create(None:Option<FloatingTab>)
    let timer = new Timer()
    /// The end of the drag happens once, however many of the timer, the mouse-leave and the
    /// button-up arrive: the rest find the capture gone and the icon copies already released.
    let mutable ended = false
    /// How long the tab takes to fly into the slot it was dropped into: long enough to read as the
    /// tab landing, short enough not to hold up the next gesture.
    let landMilliseconds = 175

    /// The drag has been released and its work is done. A flight that never arrived would otherwise
    /// hold the drag action for the life of the process, and no further drag could start.
    member this.isSettled = ended && not awaitingMoves

    /// A queued handoff belongs to this pointer frame only; a later move or release replaces it.
    member private this.afterMoves(action:unit -> unit) =
        handoff <- handoff + 1L
        let version = handoff
        let complete () = if handoff=version then action()
        match mover with
        | Some mover -> mover.settle(fun () ->
            if invoker.invokeRequired then invoker.asyncInvoke complete else complete())
        | None -> complete()

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
            awaitingMoves <- true
            this.afterMoves(fun () ->
                try
                    try pendingEnter |> Option.iter(fun (hwnd,point,initial) -> this.enterTarget(hwnd,point,initial))
                    with error -> Diagnostics.Trace.TraceWarning("Drag handoff failed: {0}",error.Message)
                finally
                    pendingEnter <- None
                    this.finishCapture(ptScreen))

    member private this.finishCapture(ptScreen) =
        awaitingMoves <- false
        let state = dragStateCell.value
        dragStateCell.set None
        state |> Option.iter(fun s -> s.dispose())
        // The drop places the window where the last move was heading; nothing moves it after that.
        mover |> Option.iter(fun mover -> (mover :> IDisposable).Dispose())
        mover <- None
        // The tab the hand let go of outlives the capture when a strip has taken it: that picture
        // is what has to be seen arriving in the slot, and it is disposed when it gets there.
        let tab = floatingTabCell.value
        floatingTabCell.set None
        // Asked before the drop commits: the strip's order already holds the tab in the slot its
        // preview chose, and the shade drawn for it is still there to measure the slot against.
        let landing, landingTarget =
            // Reading the slot talks to the destination's thread, and that group can be gone by
            // now (its last window closed during the drag). A throw here would skip the commit,
            // leave the drag action uncleared — no further drag could start — and strand the
            // overlay on screen.
            try
              match state with
              | Some(:? DragCapturedState as captured) -> captured.landing, Some captured.target
              | _ -> None, None
            with _ -> None, None
        // The sprite is built on mouse-down, before this drag proves to be one: a click that never
        // moves would otherwise keep the tab's copied icon for the life of the process.
        info.tab.release()
        match state with
        | Some(:? DragDetectingState) ->
            this.land tab landing landingTarget info.onCancel
        | Some(:? DragCapturedState) ->
            info.targets.values.iter <| fun target -> target.dragEnd()
            info.notifications.items.iter <| fun n -> n.dragEnd()
            // Ending the drag is what lets discovery run, and discovery destroys a group that
            // lost its last window - the very group whose thread is flying this picture. The
            // flight is a timer on that thread, so it would die with it and the tab would cut
            // instead of landing. The desktop hears the drag is over when the tab has arrived.
            this.land tab landing landingTarget info.onEnd
        | Some(:? DragFloatingState) ->
            info.targets.values.iter <| fun target -> target.dragEnd()
            info.notifications.items.iter <| fun n -> n.dragEnd()
            // The drop places the same window made visible under the pointer; it is the
            // user's window now, so nothing hides it afterwards.
            info.onDrop(ptScreen)
            // Nothing took the tab into a row: it was dropped on the desktop, so there is no slot
            // to fly to and the picture goes at once.
            this.land tab None None info.onEnd
        | _ -> this.land tab None None ignore
        // After the previews were told to clear: every strip that took one holds its own copy,
        // and the drag's copy has no reader left.
        DragIcon.release()

    /// Show the tab arriving: the picture the hand let go of flies from where it was drawn to the slot
    /// the strip marked, and the strip stops shading the slot once it is there. Only the arrival moves
    /// — what the drop committed was committed before this runs, so a flight cut short changes
    /// nothing. With no slot to fly to, the overlay goes at once, as it always has.
    member private this.land (tab:FloatingTab option) (landing:Rect option) (target:IDragDropTarget option)
                              (after: unit -> unit) =
        let dispose () = tab |> Option.iter(fun tab -> (tab :> IDisposable).Dispose())
        // Exactly once, on every path out of here: an arrival that never comes must not leave the
        // drag action held, and a flight that throws must not skip the end of the drag.
        let settled = ref false
        let finish () =
            if not settled.Value then
                settled.Value <- true
                (try after() with _ -> ())
        match tab, target, landing with
        | Some(tab), Some(target), Some(landing) ->
            let finished = ref false
            let arrived (cancelled:bool) =
                if not finished.Value then
                    finished.Value <- true
                    // The shade goes whether or not the picture got there: what it marks was committed
                    // before the flight began, and a shade nobody clears is a tab that cannot be used.
                    (try dispose() with _ -> ())
                    (try target.dragLanded() with _ -> ())
                    DragTrace.write(sprintf "LANDED cancelled=%b" cancelled)
                    finish()
            DragTrace.write(sprintf "LAND from=%d,%d to=%s ms=%d"
                                tab.topLeft.x tab.topLeft.y (DragTrace.rect landing) landMilliseconds)
            try tab.animateTo landing.location landMilliseconds (fun() -> arrived false)
            with _ -> arrived true
        // A target that took the drop but gave no rectangle still owns a shade: telling it that the
        // tab has landed is what clears it. Without this, a drop whose slot could not be measured
        // left the strip shading the tab for good.
        | _ ->
            dispose()
            match target with
            | Some target ->
                (try target.dropSettled() with _ -> ())
                DragTrace.write(sprintf "LANDED nocrect")
            | None ->
                // Nothing took the drop: the release happened while the picture was over no strip at
                // all (another monitor, the desktop). Every strip that shaded a slot drew it for this
                // drag, so every one of them has to hear that the drag is over.
                info.targets.values.iter(fun target ->
                    try target.dropSettled() with _ -> ())
                DragTrace.write(sprintf "LANDED notarget settled=%d" info.targets.values.count)
            finish()

    member this.wndProc (msg:Win32Message) =
        let ptScreen() =             
            let pt = msg.lParam.location
            let ptScreen = this.captureWindow.ptToScreen(pt)
            ptScreenCell.set(ptScreen)
            ptScreen
        match msg.msg with
        | WindowMessages.WM_MOUSEMOVE when not ended ->
            handoff <- handoff + 1L
            pendingEnter <- None
            // Moves queued behind the release are not part of the drag: acting on one re-opens a
            // preview in a strip that has already handled its end, and the shade it draws then has
            // no drop left to clear it.
            match dragStateCell.value with
            | Some state -> state.mouseMove(ptScreen())
            | None -> ()
        | WindowMessages.WM_MOUSEMOVE -> ()
        | WindowMessages.WM_MOUSELEAVE
        | WindowMessages.WM_LBUTTONUP -> 
            this.captureEnded(ptScreen())
        | _ -> ()
        msg.def()

    /// The source strip's handle names the dragged window until its tab data has been captured.
    member private this.draggedWindow =
        dragWindowCell.value |> Option.map(fst) |> Option.defaultValue (os.windowFromHwnd(info.initialHwnd))

    member this.dragFloat(ptScreen) =
        let window = this.draggedWindow
        let windowMover =
            match mover with
            | Some existing -> existing
            | None ->
                let created = new DragWindowMover(window)
                mover <- Some created
                created
        let state = DragFloatingState({
            targets = info.targets
            window = window
            normalSize = dragWindowCell.value |> Option.map snd |> Option.flatten
            mover = windowMover
            tab = floatingTabCell.value
            onDragIn = fun (targetHwnd, ptScreen) ->
                this.dragEnter(targetHwnd, ptScreen, false)
        })
        this.setNextState state
        // The transition itself must show both pieces, even if the mouse stops here.
        if not ended then state.follow(ptScreen)

    member this.dragEnter(targetHwnd, ptScreen, isInitial) =
        pendingEnter <- Some(targetHwnd,ptScreen,isInitial)
        // The strip places the window as soon as it takes the tab: a float move still on its way would
        // land after that and pull the window back out of the group's frame.
        this.afterMoves(fun () ->
            if not ended then
                pendingEnter <- None
                this.enterTarget(targetHwnd,ptScreen,isInitial))

    member private this.enterTarget(targetHwnd, ptScreen, isInitial) =
        let target = info.targets.find(targetHwnd)
        let targetWindow = os.windowFromHwnd(targetHwnd)
        let ptTarget = targetWindow.ptToClient(ptScreen)
        if target.dragEnter info.data ptTarget then
            this.setNextState <| DragCapturedState({
                target = target
                targetHwnd = targetHwnd
                targetWindow = targetWindow
                tab = floatingTabCell.value
                onDragOut = fun(ptScreen) -> 
                    let targetHwnd = os.windowAtPt(ptScreen).hwnd
                    match info.targets.tryFind(targetHwnd) with
                    | Some _ -> this.dragEnter(targetHwnd, ptScreen, false)
                    | None -> this.dragFloat(ptScreen)
            })
            // The captured state holds the tab by the pointer; doing it now keeps the hand and the
            // picture in agreement from this frame on, instead of at the next mouse move.
            floatingTabCell.value |> Option.iter(fun tab -> tab.holdBy ptScreen)
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
            // A released drag that never finished its work is not this drag's problem.
            match dragActionCell.value with
            | Some(previous:DragAction) when previous.isSettled -> dragActionCell.set None
            | _ -> ()
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


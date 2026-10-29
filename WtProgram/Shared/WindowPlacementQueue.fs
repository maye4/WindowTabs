namespace Bemo
open System
open System.Collections.Generic
open System.Diagnostics
open System.Threading

type private PlacementEntry<'a>(hwnd:IntPtr) =
    member _.hwnd = hwnd
    member val owner : obj option = None with get,set
    member val version = 0L with get,set
    member val pending : 'a option = None with get,set
    member val current : 'a option = None with get,set
    member val running = false with get,set
    member val node : LinkedListNode<PlacementEntry<'a>> option = None with get,set
    member this.queued = this.node.IsSome

/// Shared across groups so a transferred HWND cannot receive concurrent placements.
/// Native calls in progress cannot be cancelled; queued work and subsequent calls can.
type WindowPlacementQueue<'a>(apply:IntPtr -> 'a -> (unit -> bool) -> unit, ?workerLimit:int) =
    let gate = obj()
    let entries = Dictionary<IntPtr,PlacementEntry<'a>>()
    let ready = LinkedList<PlacementEntry<'a>>()
    let limit = max 1 (defaultArg workerLimit 4)
    let mutable workers = 0
    let idle = Event<obj>()
    let owns owner (entry:PlacementEntry<'a>) = entry.owner |> Option.exists(fun value -> obj.ReferenceEquals(value,owner))
    let ownerIsIdle owner = entries.Values |> Seq.forall(fun entry -> not(owns owner entry) || (not entry.running && entry.pending.IsNone))
    let notifyIdle owner =
        owner |> Option.iter(fun value ->
            try idle.Trigger value with error -> Trace.TraceWarning("Placement completion notification failed: {0}",error.Message))
    let dequeue (entry:PlacementEntry<'a>) =
        entry.node |> Option.iter(fun node -> ready.Remove(node))
        entry.node <- None
    let enqueue (entry:PlacementEntry<'a>) =
        if not entry.running && not entry.queued && entry.pending.IsSome then
            entry.node <- Some(ready.AddLast(entry))
    let rec run() =
        let job = lock gate (fun () ->
            let mutable job = None
            while job.IsNone && ready.Count>0 do
                let entry = ready.First.Value
                dequeue entry
                match entry.pending with
                | Some value when entry.owner.IsSome ->
                    entry.pending <- None
                    entry.running <- true
                    entry.current <- Some value
                    job <- Some(entry,value,entry.version)
                | _ -> if entry.owner.IsNone then entries.Remove(entry.hwnd) |> ignore
            if job.IsNone then workers <- workers-1
            job)
        match job with
        | None -> ()
        | Some(entry,value,version) ->
            let valid() = lock gate (fun () -> entry.owner.IsSome && entry.version=version)
            try
                if valid() then apply entry.hwnd value valid
            with error -> Trace.TraceWarning("Follower placement failed: {0}",error.Message)
            let completed = lock gate (fun () ->
                entry.running <- false
                entry.current <- None
                enqueue entry
                if entry.owner.IsNone then entries.Remove(entry.hwnd) |> ignore
                entry.owner |> Option.filter ownerIsIdle)
            notifyIdle completed
            run()
    let start() =
        let additional = min (limit-workers) ready.Count
        for _ in 1..additional do
            workers <- workers+1
            ThreadPool.QueueUserWorkItem(WaitCallback(fun _ -> run())) |> ignore
    member _.Idle = idle.Publish
    member _.claim(owner,hwnd) =
      let completed = lock gate (fun () ->
        let entry =
            match entries.TryGetValue(hwnd) with
            | true,entry -> entry
            | _ -> let entry = PlacementEntry<'a>(hwnd) in entries.Add(hwnd,entry); entry
        let previous = entry.owner |> Option.filter(fun value -> not(ownerIsIdle value))
        entry.version <- entry.version+1L
        dequeue entry
        entry.owner <- Some owner
        entry.pending <- None
        previous |> Option.filter(fun value -> not(obj.ReferenceEquals(value,owner)) && ownerIsIdle value))
      notifyIdle completed
    member _.submit(owner,hwnd,value) = lock gate (fun () ->
        match entries.TryGetValue(hwnd) with
        | true,entry when owns owner entry ->
            entry.version <- entry.version+1L
            entry.pending <- Some value
            enqueue entry
            start()
        | _ -> ())
    member _.cancel(owner,hwnd,?whenValue:'a -> bool) =
      let completed = lock gate (fun () ->
        let wasIdle = ownerIsIdle owner
        match entries.TryGetValue(hwnd) with
        | true,entry when owns owner entry ->
            let latest = if entry.pending.IsSome then entry.pending else entry.current
            if latest |> Option.exists(defaultArg whenValue (fun _ -> true)) then
                entry.version <- entry.version+1L
                entry.pending <- None
                dequeue entry
        | _ -> ()
        if not wasIdle && ownerIsIdle owner then Some owner else None)
      notifyIdle completed
    member _.release(owner,hwnd) =
      let completed = lock gate (fun () ->
        let wasIdle = ownerIsIdle owner
        match entries.TryGetValue(hwnd) with
        | true,entry when owns owner entry ->
            entry.version <- entry.version+1L
            entry.owner <- None
            entry.pending <- None
            dequeue entry
            if not entry.running then entries.Remove(hwnd) |> ignore
        | _ -> ()
        if not wasIdle && ownerIsIdle owner then Some owner else None)
      notifyIdle completed
    member _.isIdle(owner) = lock gate (fun () -> ownerIsIdle owner)
    member _.isWindowIdle(owner,hwnd) = lock gate (fun () ->
        match entries.TryGetValue(hwnd) with
        | true,entry when owns owner entry -> not entry.running && entry.pending.IsNone
        | _ -> true)
    member _.isWindowBusyWith(owner,hwnd,predicate) = lock gate (fun () ->
        match entries.TryGetValue(hwnd) with
        | true,entry when owns owner entry -> [entry.pending;entry.current] |> List.exists(Option.exists predicate)
        | _ -> false)
    member _.retainedCount = lock gate (fun () -> entries.Count)

type FollowerPlacementRequest =
    /// The source's window bounds and placement, the window to stay below, and the source's
    /// visible frame when the follower should match that rather than the window bounds.
    | AlignWindow of Rect * OSWindowPlacement * IntPtr option * Rect option * bool
    /// A window restored without activation can still rise above the tab that asked for it;
    /// the handle it should stay below goes with the request.
    | SetMinimized of bool * IntPtr option
    | HideForMove
    | PrepareMinimizeAnchor

module FollowerPlacement =
    let private identity hwnd =
        let mutable pid = 0
        let tid = WinUserApi.GetWindowThreadProcessId(hwnd,&pid)
        pid,tid

    let private apply hwnd (expected,request) current =
        let valid() = current() && identity hwnd=expected && WinUserApi.IsWindow(hwnd)
        if valid() then
            let window = OS().windowFromHwnd(hwnd)
            match request with
            | PrepareMinimizeAnchor -> window.prepareMinimizeAnchor(valid)
            | HideForMove ->
                // A move takes the followers off the screen for as long as the window is held:
                // nothing of them can show, on any monitor and at any scale, and neither the strip
                // nor the taskbar is in use while a drag runs. A window the mark cannot be put on
                // stays visible, so a killed run cannot leave one invisible for good.
                if valid() && not window.isMinimized && window.isVisible then
                    window.hideForMove()
                    DragTrace.write(sprintf "HIDE %d at=%s hidden=%b dpi=%d"
                                        (hwnd.ToInt64()) (DragTrace.rect window.bounds)
                                        (not window.isVisible) (Dpi.forWindow hwnd))
                    // The group can be torn down, or the window can leave it, while this hide is
                    // in flight: nothing else will show it again in this run, so the hide undoes
                    // itself.
                    if not(valid()) then
                        window.showCurrentStateNoActivate()
                        MoveHidden.release hwnd |> ignore
                        DragTrace.write(sprintf "UNHIDE %d stale-request" (hwnd.ToInt64()))
            | SetMinimized(minimized,below) ->
                if window.isMinimized<>minimized && valid() then
                    if minimized then window.prepareMinimizeAnchor(valid)
                    if valid() then window.showWindow(if minimized then ShowWindowCommands.SW_SHOWMINNOACTIVE else ShowWindowCommands.SW_SHOWNOACTIVATE)
                    below |> Option.iter(fun top -> if valid() && WinUserApi.IsWindow(top) then window.insertAfter(top))
            | AlignWindow(bounds,wp,below,visible,_) when MoveHidden.holds hwnd ->
                // A follower this run hid for the move comes back here: set the group's frame while
                // it is still off the screen, then show it. A window that is maximized cannot be
                // moved, so it is placed instead, which also restores it onto that frame; the show
                // is a plain SW_SHOWNA so it does not undo that.
                if valid() && window.isMinimized then
                    // Hidden and minimized at once: nothing in this run would show it, and the group
                    // cannot place a minimized window, so it comes back minimized and unmarked.
                    if not window.isVisible then
                        window.showWindow(ShowWindowCommands.SW_SHOWMINNOACTIVE)
                    if window.isVisible then MoveHidden.release hwnd |> ignore
                    DragTrace.write(sprintf "BACK %d minimized visible=%b marked=%b"
                                        (hwnd.ToInt64()) window.isVisible (MoveHidden.holds hwnd))
                elif valid() && not window.isMinimized then
                    let own,seen = window.bounds,window.visibleBounds
                    let target =
                        match visible with
                        | Some frame when not seen.size.isEmptyArea ->
                            let left,top = seen.x-own.x,seen.y-own.y
                            Rect(Pt(frame.x-left,frame.y-top),Sz(frame.width+own.width-seen.width,frame.height+own.height-seen.height))
                        | _ -> bounds
                    // An application that is shown on a monitor of another scale rescales itself by
                    // the ratio between the two (measured: asked 2353x1301 on a 192 dpi monitor from
                    // a window at 144, landed 3137x1735). It is asked for the frame divided by that
                    // ratio instead, so its own scaling lands on the frame and no wrong-sized frame
                    // is ever painted. Only a window whose scale really changes takes this path.
                    let windowDpi = Dpi.forWindow hwnd
                    let targetDpi = Dpi.forMonitorPoint target.location
                    let asked =
                        if windowDpi<>targetDpi && targetDpi>0 then
                            let ratio = float windowDpi / float targetDpi
                            Rect(target.location, Sz(int(Math.Round(float target.size.width * ratio)),
                                                     int(Math.Round(float target.size.height * ratio))))
                        else target
                    // A maximized follower refuses a move, and the group's frame is a normal
                    // rectangle, so it would come back maximized and covering the screen instead of
                    // in the group. A placement sets its state and its rectangle in one call, onto
                    // the frame it is being given: rcNormalPosition is in workspace coordinates
                    // while the frame is on the screen, converted the way the drag restore does.
                    if window.isMaximized then
                        let workspaceOffset =
                            match Mon.fromPoint target.location with
                            | Some mon -> mon.workRect.location.sub(mon.displayRect.location)
                            | None -> Pt()
                        window.setPlacement { window.placement with
                                                showCmd = ShowWindowCommands.SW_SHOWNOACTIVATE
                                                flags = 0
                                                rcNormalPosition = Rect(asked.location.sub(workspaceOffset), asked.size) }
                    elif window.bounds<>asked then window.move(asked)
                    if valid() && not window.isVisible then window.showCurrentStateNoActivate()
                    // A show the application refused keeps the mark, so the next start finds the
                    // window and gives it back; only a window that is really on screen is done.
                    if valid() && window.isVisible then MoveHidden.release hwnd |> ignore
                    // A follower restored without activation can rise above the window it belongs
                    // under, and nothing later in this request would put it back.
                    if valid() && window.isVisible && not window.isMinimized then
                        below |> Option.iter(fun top ->
                            if valid() && WinUserApi.IsWindow(top) then window.insertAfter(top))
                    // A hidden window learns its monitor's scale when it is shown, not when it is
                    // moved, so an application placed across two scales rescales itself *after*
                    // this show and comes back larger than the frame it was given. The frame is
                    // re-applied once, after that, and only when the window really moved off it.
                    if valid() && window.isVisible && window.bounds<>target then
                        let refused = window.bounds
                        window.move(target)
                        DragTrace.write(sprintf "CORRECT %d asked=%s refused=%s now=%s dpi=%d"
                                            (hwnd.ToInt64()) (DragTrace.rect target) (DragTrace.rect refused)
                                            (DragTrace.rect window.bounds) (Dpi.forWindow hwnd))
                    DragTrace.write(sprintf "BACK %d want=%s got=%s visible=%b marked=%b minimized=%b dpi=%d"
                                        (hwnd.ToInt64()) (DragTrace.rect target) (DragTrace.rect window.bounds)
                                        window.isVisible (MoveHidden.holds hwnd) window.isMinimized
                                        (Dpi.forWindow hwnd))
            | AlignWindow(bounds,wp,below,visible,_) ->
                let actual = window.placement
                let previous = defaultArg below window.prevZorder.hwnd
                let mutable positioned = false
                // Apps draw invisible resize borders of different widths (new Outlook 10px, most
                // 7px at 125%). Matching window bounds left the visible frames a few pixels apart,
                // so the tabs, which sit on the visible frame, shifted as the tabs were switched.
                // A restored follower takes the source's visible frame plus its own borders.
                let target() =
                    match visible with
                    | Some frame when wp.showCmd=ShowWindowCommands.SW_SHOWNORMAL ->
                        let own,seen = window.bounds,window.visibleBounds
                        if seen.size.isEmptyArea then bounds
                        else
                            let left,top = seen.x-own.x,seen.y-own.y
                            Rect(Pt(frame.x-left,frame.y-top),Sz(frame.width+own.width-seen.width,frame.height+own.height-seen.height))
                    | _ -> bounds
                let move() = if valid() then positioned <- true;window.move(target())
                let place value =
                    if valid() then
                        positioned <- true
                        window.setPlacement(value)
                // MoveWindow preserves the visible bounds of an Aero snapped window.
                if wp.showCmd=ShowWindowCommands.SW_SHOWNORMAL && actual.showCmd=ShowWindowCommands.SW_SHOWNORMAL then
                    if window.bounds<>target() then move()
                elif actual.showCmd=ShowWindowCommands.SW_SHOWMINIMIZED then
                    let minimized = {wp with showCmd=ShowWindowCommands.SW_SHOWMINIMIZED}
                    if actual<>minimized then place minimized
                elif wp.showCmd=ShowWindowCommands.SW_SHOWNORMAL then
                    // Snapping keeps the old restore rectangle in WINDOWPLACEMENT.
                    // Restore without activation, then use the source's actual frame.
                    place {wp with showCmd=ShowWindowCommands.SW_SHOWNOACTIVATE}
                    move()
                elif actual.showCmd=ShowWindowCommands.SW_SHOWMAXIMIZED && wp.showCmd=ShowWindowCommands.SW_SHOWMAXIMIZED then
                    // Cross-monitor maximization requires moving before restoring placement. A
                    // maximized frame never equals the work-area rect the group keeps, so comparing
                    // the two moved and re-placed every maximized follower on each of the top
                    // window's location changes, including every step of its own maximize. Only
                    // another monitor or another restore rect is work to do.
                    let frame = target()
                    let elsewhere = Mon.fromHwnd hwnd <> Mon.fromPoint(Pt(frame.x+frame.width/2,frame.y+frame.height/2))
                    if elsewhere then
                        move()
                        place wp
                    elif actual.rcNormalPosition<>wp.rcNormalPosition then place wp
                elif actual<>wp then place wp
                if valid() && window.needsMinimizeAnchor then positioned <- true
                window.prepareMinimizeAnchor(valid)
                // Restoring can move again after SetWindowPlacement. Correct stacking
                // after every native phase, using the source captured by the request.
                // An application that has just been put on a monitor of another scale applies the
                // rectangle the system suggests for it (measured: 2357x1303 asked, Chromium landed
                // 3137x1735, exactly the 192/144 ratio) and ignores the size asked for, so the
                // follower comes back larger than the group's frame and shows outside it until
                // something places it again. One more move, after that handler has run, is what
                // puts it back in the frame; it is not repeated, so an application that refuses the
                // size keeps its own and no move loop starts. A maximized window's frame is the
                // system's, larger than the work area by its borders, and refuses a move anyway.
                if positioned && valid() && not window.isMinimized && not window.isMaximized && window.bounds<>target() then
                    let refused = window.bounds
                    window.move(target())
                    let want = target()
                    DragTrace.write(sprintf "CORRECT %d asked=%s refused=%s now=%s"
                                        (hwnd.ToInt64()) (DragTrace.rect want) (DragTrace.rect refused)
                                        (DragTrace.rect window.bounds))
                if positioned && valid() && not window.isMinimized && previous<>IntPtr.Zero && WinUserApi.IsWindow(previous) then
                    window.insertAfter(previous)
                DragTrace.write(sprintf "ALIGN %d want=%s got=%s cmd=%d dpi=%d"
                                    (hwnd.ToInt64()) (DragTrace.rect bounds) (DragTrace.rect window.bounds)
                                    wp.showCmd (Dpi.forWindow hwnd))

    /// A group's windows usually each have a thread of their own and change state together, so a
    /// large group is not minimized or restored four windows at a time. The thread pool adds threads
    /// beyond the processor count only slowly, so the limit stays within it.
    let queue = WindowPlacementQueue(apply, workerLimit=(Environment.ProcessorCount |> min 16 |> max 4))
    let request hwnd bounds placement = identity hwnd,AlignWindow(bounds,placement,None,None,true)
    let alignBelowRequest hwnd bounds placement top = identity hwnd,AlignWindow(bounds,placement,Some top,None,true)
    /// As request and alignBelowRequest, matching the source's visible frame when restored.
    let alignFrameRequest hwnd bounds visible placement below = identity hwnd,AlignWindow(bounds,placement,below,Some visible,true)
    /// As alignFrameRequest, for a follower this run hid for the move: the frame is applied by a
    /// move and the window is shown once it is in it.
    let alignHiddenFrameRequest hwnd bounds visible placement below = identity hwnd,AlignWindow(bounds,placement,below,Some visible,false)
    let minimizeRequest hwnd minimized = identity hwnd,SetMinimized(minimized,None)
    let restoreBelowRequest hwnd top = identity hwnd,SetMinimized(false,Some top)
    let hideRequest hwnd = identity hwnd,HideForMove
    let prepareMinimizeRequest hwnd = identity hwnd,PrepareMinimizeAnchor
    let isChangingMinimizeState owner hwnd =
        queue.isWindowBusyWith(owner,hwnd,fun (_,request) -> match request with SetMinimized _ -> true | _ -> false)

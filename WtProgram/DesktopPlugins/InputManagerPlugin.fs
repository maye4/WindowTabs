namespace Bemo
open System
open System.Runtime.InteropServices

/// The deadline and foreground belong to the first press, not subsequent repeats.
type NumberLeaderState() =
    let mutable armed : (IntPtr * DateTime) option = None
    member _.active = armed.IsSome
    member _.cancel() = armed <- None
    member _.arm foreground (now:DateTime) = armed <- Some(foreground,now.AddSeconds(10.0))
    member _.validate foreground now =
        match armed with
        | Some(hwnd,deadline) when hwnd=foreground && now<deadline -> true
        | _ -> armed <- None; false
    member this.key foreground now key count keys =
        if not (this.validate foreground now) then false,None
        elif (NumberLeaderKeys.index keys key).IsSome then
            armed <- None
            true,(NumberLeaderKeys.index keys key |> Option.filter(fun index -> index<count))
        elif key=0x1B then armed <- None; true,None
        elif List.contains key [0x10;0x11;0x12;0xA0;0xA1;0xA2;0xA3;0xA4;0xA5] then false,None
        else armed <- None; false,None

/// Keep releases paired with swallowed presses even if focus or modifiers change.
type NumericShortcutCapture() =
    let pressed = Collections.Generic.HashSet<int>()
    member _.handle(msg, key, target:int option) =
        if msg=WindowMessages.WM_KEYUP || msg=WindowMessages.WM_SYSKEYUP then
            pressed.Remove(key), None
        elif msg=WindowMessages.WM_KEYDOWN || msg=WindowMessages.WM_SYSKEYDOWN then
            if target.IsSome then
                pressed.Add(key) |> ignore
                true, target
            else pressed.Contains(key), None
        else false, None

/// The second press of a system double-click: the same window, within the double-click time and
/// box. That press is the one that toggles the window's state — maximize or restore — and the group
/// must keep its other windows on screen for it, so they animate with the change exactly as they do
/// when the restore button is clicked. Only the first press of the pair hides them, for a drag it
/// may start; a double-click that turns into a drag still hides them on its first movement.
module CaptionPress =
    /// Milliseconds since the hook started: `TickCount` wraps, and the age of a press taken across
    /// the wrap would read as negative, so a pair that far apart would look like one double-click.
    let private clock = Diagnostics.Stopwatch.StartNew()
    let now () = clock.ElapsedMilliseconds
    let isSecond (previous:(int64 * Pt * IntPtr) option) (at:int64) (pt:Pt) (hwnd:IntPtr) =
        match previous with
        | Some(beforeAt,beforePt,beforeHwnd) when beforeHwnd=hwnd ->
            let box = System.Windows.Forms.SystemInformation.DoubleClickSize
            at-beforeAt <= int64 System.Windows.Forms.SystemInformation.DoubleClickTime &&
                2 * abs(pt.x-beforePt.x) <= box.Width && 2 * abs(pt.y-beforePt.y) <= box.Height
        | _ -> false
    /// A caption press that has travelled past the system's drag box is a drag, and starts the move
    /// loop the same way the system's own threshold does.
    let isDrag (start:Pt) (pt:Pt) =
        let box = System.Windows.Forms.SystemInformation.DragSize
        2 * abs(pt.x-start.x) > box.Width || 2 * abs(pt.y-start.y) > box.Height

type InputManagerPlugin(msgSet:Set2<Int32>) as this =
    let hookProcDelegate = HOOKPROC(this.llHook)
    let mutable kbHook = null
    let OS = OS()
    let numeric = NumericTabHotKeyPlugin()
    let numericCapture = NumericShortcutCapture()
    let leader = NumberLeaderState()
    let leaderCapture = NumericShortcutCapture()
    let mutable leaderGroup : GroupInfo option = None
    let timer = new System.Windows.Forms.Timer(Interval=50)
    let owned = new LifetimeScope(ignore)
    let mutable leaderCode = SettingsCatalog.shortcutDefault "numberLeader"
    let mutable leaderEnabled = false
    let mutable leaderKeys = SettingsCatalog.textDefault "numberLeaderKeys"
    let mutable mouseHook = IntPtr.Zero
    /// The group whose caption took the last press, so the release is answered by that group even if
    /// the pointer has left it. A hold that is never released would keep the group's windows hidden.
    let mutable pressGroup : (IntPtr * GroupInfo) option = None
    /// The last caption press, for the system's double-click rule: the second press of a pair keeps
    /// the group's other windows on screen.
    let mutable lastCaptionPress : (int64 * Pt * IntPtr) option = None
    /// A caption press that may become a drag: where the pointer went down, the window and group that
    /// own it, and whether this was the second press of a double-click.
    let mutable captionDrag : (Pt * IntPtr * GroupInfo * bool) option = None
    let mutable cachedPath = (IntPtr.Zero, "")
    let pathFor hwnd =
        if fst cachedPath <> hwnd then
            let path = try OS.windowFromHwnd(hwnd).pid.processPath with _ -> ""
            cachedPath <- (hwnd,path)
        snd cachedPath

    member this.desktop = Services.desktop

    member this.foregroundGroup = Services.desktop.foregroundGroup

    member private _.cancelLeader() =
        leader.cancel()
        timer.Stop()
        leaderGroup |> Option.iter(fun group -> group.invokeGroup(fun() -> group.group.bb.write("numberBadges",false)))
        leaderGroup <- None

    member private this.validateLeader() =
        let sameGroup = leaderGroup |> Option.exists(fun group ->
            not group.isExited && (this.foregroundGroup |> Option.exists(fun current -> current.hwnd=group.hwnd)))
        if not sameGroup then leader.cancel()
        leader.validate (WinUserApi.GetForegroundWindow()) DateTime.UtcNow

    member private this.toggleLeader() =
        if leader.active then this.cancelLeader()
        elif leaderEnabled then
            this.foregroundGroup |> Option.iter(fun group ->
                let info = group.cast<GroupInfo>()
                leaderGroup <- Some info
                leader.arm (WinUserApi.GetForegroundWindow()) DateTime.UtcNow
                let keys = leaderKeys
                info.invokeGroup(fun() ->
                    info.group.bb.write("numberBadgeKeys",keys)
                    info.group.bb.write("numberBadges",true))
                timer.Start())

    /// The group that owns the window under the pointer, when that point is the window's caption: the
    /// one place a press starts a move. A press anywhere else in a window belongs to the application.
    /// The window under the pointer can be a child of the one the group holds, so it is normalised to
    /// its root first.
    member private this.captionGroup(pt:Pt) =
        let hwnd = Win32Helper.GetTopLevelWindowFromPoint(pt.Point)
        if hwnd=IntPtr.Zero then None else
        this.desktop.groups.tryFind(fun group -> group.windows.contains((=)hwnd))
        |> Option.filter(fun group ->
            // Ask the window what that point of it is: WM_NCHITTEST, answered with HTCAPTION when the
            // press would start a move. The wait is short because this runs on the thread that feeds
            // the pointer: a window that cannot answer in 20 ms is not one to wait for.
            let mutable result = IntPtr.Zero
            let lParam = IntPtr((pt.y <<< 16) ||| (pt.x &&& 0xFFFF))
            let answered = WinUserApi.SendMessageTimeout(hwnd,0x0084,IntPtr.Zero,lParam,0x0002,20,&result)
            let caption = answered<>IntPtr.Zero && result.ToInt32()=2
            // A timed-out hit test is a press the drag restore never sees.
            if not caption then
                DragTrace.write(sprintf "CAPTION-MISS hwnd=%d pt=%d,%d answered=%b hit=%d" (hwnd.ToInt64()) pt.x pt.y (answered<>IntPtr.Zero) (result.ToInt32()))
            caption)
        |> Option.map(fun group -> hwnd,group.cast<GroupInfo>())

    member this.llHook nCode (wParam:IntPtr) lParam = 
        let msg = wParam.ToInt32()
        if nCode>=0 && List.contains msg [WindowMessages.WM_LBUTTONDOWN;WindowMessages.WM_RBUTTONDOWN;WindowMessages.WM_MBUTTONDOWN;WindowMessages.WM_XBUTTONDOWN] then this.cancelLeader()
        // The button is what the user pressed, so the group hides its other windows for the whole
        // press, not from the first frame of a move it may never make. The release belongs to the
        // press that took the hide, not to whatever happens to be under the pointer by then.
        if nCode>=0 && msg=WindowMessages.WM_LBUTTONDOWN then
            let hookStruct = unbox<MSLLHOOKSTRUCT>(Marshal.PtrToStructure(lParam, typeof<MSLLHOOKSTRUCT>))
            let pt = hookStruct.pt.Pt
            let now = CaptionPress.now()
            let pressed = this.captionGroup pt
            let secondClick =
                pressed |> Option.exists(fun(hwnd,_) -> CaptionPress.isSecond lastCaptionPress now pt hwnd)
            lastCaptionPress <- pressed |> Option.map(fun(hwnd,_) -> now,pt,hwnd)
            // The second press toggles the window's state, and the other windows animate with it
            // only if they are still on screen: they are not hidden for it.
            // A maximized window is restored by the group once this press turns into a drag, so the
            // drag shows the shrink the caption button does rather than the system's fade.
            captionDrag <-
                pressed |> Option.map(fun(hwnd,info) ->
                    let suffix = if secondClick then "-2ND" else ""
                    DragTrace.write(sprintf "CAPTION-PRESS%s hwnd=%d pt=%d,%d" suffix (hwnd.ToInt64()) pt.x pt.y)
                    pt,hwnd,info,secondClick)
            if secondClick then
                pressGroup <- None
                pressed |> Option.iter(fun(hwnd,info) ->
                    info.invokeGroup <| fun() ->
                        if not info.isExited then info.group.captionPressStarted(hwnd))
            else
                pressGroup <- pressed
                pressed |> Option.iter(fun(hwnd,info) ->
                    info.invokeGroup <| fun() ->
                        if not info.isExited then
                            info.group.captionPressStarted(hwnd)
                            info.group.pressHide(hwnd))
        if nCode>=0 && msg=WindowMessages.WM_MOUSEMOVE then
            match captionDrag with
            | Some(start,hwnd,info,secondClick) ->
                let hookStruct = unbox<MSLLHOOKSTRUCT>(Marshal.PtrToStructure(lParam, typeof<MSLLHOOKSTRUCT>))
                let pt = hookStruct.pt.Pt
                if CaptionPress.isDrag start pt then
                    DragTrace.write(sprintf "CAPTION-DRAG hwnd=%d start=%d,%d now=%d,%d second=%b" (hwnd.ToInt64()) start.x start.y pt.x pt.y secondClick)
                    captionDrag <- None
                    lastCaptionPress <- None
                    if secondClick then pressGroup <- Some(hwnd,info)
                    // Capture the restore on the group STA, then let it start the native call on a
                    // worker. The hook returns immediately; the group keeps the token that decides
                    // whether a late move loop needs the one-shot recovery.
                    info.invokeGroup(fun() ->
                        if not info.isExited && Win32Helper.IsKeyPressed(VirtualKeyCodes.VK_LBUTTON) then
                            if secondClick then info.group.pressHide(hwnd)
                            info.group.captionDragStarted(hwnd,start,pt))
            | None -> ()
        if nCode>=0 && msg=WindowMessages.WM_LBUTTONUP then
            captionDrag <- None
            let hookStruct = unbox<MSLLHOOKSTRUCT>(Marshal.PtrToStructure(lParam, typeof<MSLLHOOKSTRUCT>))
            let pt = hookStruct.pt.Pt
            match pressGroup with
            | Some(_,info) ->
                pressGroup <- None
                info.invokeGroup <| fun() ->
                    if not info.isExited then info.group.pressShow(pt)
            | None -> ()
        if nCode>=0 && msgSet.contains(msg) then
            this.foregroundGroup.iter <| fun group ->
                let hookStruct = unbox<MSLLHOOKSTRUCT>(Marshal.PtrToStructure(lParam, typeof<MSLLHOOKSTRUCT>))
                let pt = hookStruct.pt.Pt
                let data = hookStruct.mouseData.IntPtr
                let groupInfo = group.cast<GroupInfo>()
                groupInfo.invokeGroup <| fun() ->
                    groupInfo.group.postMouseLL(msg, pt, data)

        WinUserApi.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam)

    member this.registerMouseLLHook() =
        mouseHook <- WinUserApi.SetWindowsHookEx(WindowHookTypes.WH_MOUSE_LL, hookProcDelegate, IntPtr.Zero, 0)

    member this.registerKeyboardLLHook() =
        kbHook <- OS.registerKeyboardLLHook <| fun(key, data) ->
            if data.dwExtraInfo = AltMenuMask.marker then None else
            // The group runs asynchronously; Ctrl may be released before it handles
            // this key. Preserve the modifier state belonging to this input event.
            let controlPressed = Win32Helper.IsKeyPressed(VirtualKeyCodes.VK_CONTROL)
            let foreground = this.foregroundGroup
            let foregroundHwnd = WinUserApi.GetForegroundWindow()
            let altPressed = (data.flags &&& LlKeyboardHookFlags.LLKHF_ALTDOWN) <> 0
            let extraModifier = Win32Helper.IsKeyPressed(VirtualKeyCodes.VK_SHIFT) ||
                                Win32Helper.IsKeyPressed(int System.Windows.Forms.Keys.LWin) ||
                                Win32Helper.IsKeyPressed(int System.Windows.Forms.Keys.RWin)
            let wasArmed = leader.active
            if wasArmed then this.validateLeader() |> ignore
            let down = int key=WindowMessages.WM_KEYDOWN || int key=WindowMessages.WM_SYSKEYDOWN
            let shiftPressed = Win32Helper.IsKeyPressed(VirtualKeyCodes.VK_SHIFT)
            let chordMatches = data.vkCode=(leaderCode &&& 0xFF) && controlPressed=((leaderCode &&& 0x200)<>0) && altPressed=((leaderCode &&& 0x400)<>0) && shiftPressed=((leaderCode &&& 0x100)<>0)
            let leaderConsumed,leaderTarget =
                if down && not chordMatches then
                    leader.key foregroundHwnd DateTime.UtcNow data.vkCode (foreground |> Option.map(fun g -> g.windows.length) |> Option.defaultValue 0) leaderKeys
                else false,None
            if wasArmed && not leader.active then this.cancelLeader()
            let capturedLeader,_ = leaderCapture.handle(int key,data.vkCode,if leaderConsumed then Some 0 else None)
            let target =
                if wasArmed || capturedLeader || extraModifier || (leaderEnabled && chordMatches) then None
                else foreground |> Option.bind(fun group ->
                    numeric.targetIndex(int key,data.vkCode,controlPressed,altPressed=altPressed)
                    |> Option.filter(NumericShortcutTarget.available group.windows.list (WinUserApi.GetForegroundWindow())))
            let target = target |> Option.filter(fun _ -> NumberShortcutRules.enabled (pathFor (WinUserApi.GetForegroundWindow())))
            let consumed,activate = numericCapture.handle(int key,data.vkCode,target)
            let activate = if leaderConsumed then leaderTarget else activate
            if (activate.IsSome || leaderConsumed) && altPressed then AltMenuMask.send()
            activate |> Option.iter(fun index ->
                foreground.iter <| fun group ->
                    let groupInfo = group.cast<GroupInfo>()
                    groupInfo.invokeGroup <| fun() -> groupInfo.group.activateIndex(index,true))
            if consumed || capturedLeader then Some 1 else None

    interface IStartupInputPlugin

    interface IPlugin with
        member x.init() =
            leaderCode <- Services.program.getHotKey "numberLeader"
            leaderEnabled <- Services.settings.getValue("enableNumberLeader") :?> bool
            leaderKeys <- Services.settings.getValue("numberLeaderKeys") :?> string
            owned.Own(Services.settings.notifyValue "numberLeaderKeys" (fun value -> leaderKeys <- unbox value; this.cancelLeader())) |> ignore
            owned.Own(Services.settings.notifyValue "hotKeys" (fun _ -> leaderCode <- Services.program.getHotKey "numberLeader"; this.cancelLeader())) |> ignore
            this.registerMouseLLHook()
            this.registerKeyboardLLHook()
            owned.Own(NumberLeaderRequest.toggle.Publish.Subscribe(fun() -> this.toggleLeader())) |> ignore
            owned.Own(Services.settings.notifyValue "enableNumberLeader" (fun _ -> leaderEnabled <- Services.settings.getValue("enableNumberLeader") :?> bool; this.cancelLeader())) |> ignore
            owned.Own(OS.setSingleWinEvent WinEvent.EVENT_SYSTEM_FOREGROUND (fun _ ->
                cachedPath <- (IntPtr.Zero, "")
                if leader.active && not (this.validateLeader()) then this.cancelLeader())) |> ignore
            timer.Tick.Add(fun _ -> if not (this.validateLeader()) then this.cancelLeader())

    interface IDisposable with
        member _.Dispose() =
            this.cancelLeader()
            timer.Dispose()
            (owned :> IDisposable).Dispose()
            if mouseHook<>IntPtr.Zero then WinUserApi.UnhookWindowsHookEx(mouseHook) |> ignore
            if not (isNull kbHook) then kbHook.Dispose()

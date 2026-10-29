namespace Bemo
open System
open System.Collections
open System.Drawing
open System.Drawing.Drawing2D
open System.Drawing.Imaging
open System.Reflection
open System.IO
open System.Windows.Forms
open Bemo.Win32.Forms

type ITabStripMonitor =
    abstract member tabClick : (MouseButton * Tab * TabPart * MouseAction * Pt) -> unit
    abstract member tabActivate : (Tab) -> unit
    abstract member tabClose : Tab -> unit
    abstract member tabMoved : Tab * int -> unit
    abstract member windowMsg : Win32Message -> unit

/// What a floating drag carries: where the strip the drop leaves puts the tab, how that strip draws
/// it, and the release that lets go of what the drag had to keep for the picture.
type FloatingTabSprite = {
    /// Both take the scale of the monitor the picture is drawn for: the drag crosses monitors while
    /// the strip that drew the tab never does.
    location : int -> bool -> Sz -> Pt
    image : int -> bool -> Sz -> Img
    release : unit -> unit
    }

type TabStrip(monitor:ITabStripMonitor) as this =
    let Cell = CellScope(false, true)
    let _os = OS()
    let taskbar = _os.getTaskbar()
    let tabMovedEvent = Event<_>()
    let contentBoundsCell = Cell.create(Rect())
    /// The tab another group's drag is previewing here, with the icon the drag carries: the real tab
    /// is still under the pointer until the mouse is released.
    let ghostTabCell = Cell.create(None:(Icon * Tab) option)
    let appearanceCell = Cell.create(None)
    let foregroundCell = Cell.create(None:Tab option)
    let prevForegroundCell = Cell.create(None)
    let sizeCell = Bemo.Cell<Sz>(Cell, Sz.empty, (=))
    let dimmedCell = Cell.create(false)
    // Position is presentation state, not an input to pixel rendering.
    let mutable location = Pt.empty
    let mutable renderedFrames = 0L
    let mutable renderedOffset = 0
    let mutable relocating = false
    let lorderCell = Cell.create(List2())
    let zorderCell = Cell.create(List2())
    let visibleCell = Bemo.Cell<bool>(Cell, false, (=))
    let transparentCell = Cell.create(true)
    let showInsideCell = Bemo.Cell<bool>(Cell, false, (=))
    let isInAltTabCell = Cell.create(false)
    let numberBadgeKeysCell = Bemo.Cell<string>(Cell, SettingsCatalog.textDefault "numberLeaderKeys", (=))
    let numberBadgesCell = Bemo.Cell<bool>(Cell, false, (=))
    let iconOnlyCell = Cell.create(false)
    let dockCell = Cell.create(
        match Services.settings.getValue("tabPosition") :?> string with
        | "Left" -> TabDockLeft
        | "Right" -> TabDockRight
        | _ -> TabDockTop)
    let scrollOffsetCell = Cell.create(0)
    let dockChangedEvent = Event<unit>()
    let layoutChangedEvent = Event<bool>()
    let mutable columnWidthValue = Services.settings.getValue("tabColumnWidth") :?> int
    let mutable columnResize : (int*int*bool) option = None
    let mutable columnScroll : (int*int) option = None
    let mutable dragScrollLocation : Pt option = None
    let dragScrollTimer = new Timer(Interval=80)
    let alignmentMap = 
        Map.ofList [
            "Left", TabLeft
            "Center", TabCenter
            "Right", TabRight
        ]
    let alignmentDefault = 
        let alignmentDefault = Services.settings.getValue("alignment").cast<string>()
        match alignmentMap.TryFind alignmentDefault with
        | Some align -> align
        | None -> TabCenter
    let alignment = Cell.create(Map2(List2([(TabUp,alignmentDefault);(TabDown,alignmentDefault)])))
    let mutable alignmentOverrides = Set.empty
    let capturedCell = Bemo.Cell<Option<Tab*TabPart>>(Cell, None, (=))
    let hoverCell = Bemo.Cell<Option<Tab*TabPart>>(Cell, None, (=))
    // With equality: this is assigned on every pointer move while a drag previews here, and each
    // assignment without it repainted the whole row, uploaded the image and rebuilt the shadow.
    let slideCell = Bemo.Cell<(Tab * int) option>(Cell, None, (=))
    let heldLayoutCell = Bemo.Cell<(float * float) option>(Cell, None, (=))
    let ptCell = Cell.create(None)
    let tabInfoCell = Cell.create(Map2():Map2<Tab,TabInfo>)
    let layeredWindowCell = Cell.create(None)
    let eventHandlersCell = Cell.create(Set2())
    let tabTint = Cell.create(Map2())
    let colorStyleCell = Cell.create(Services.settings.getValue("tabColorStyle") :?> string)
    let tabBgColor = Cell.create(Map2())
    let mutable shadowWindow : TabShadowWindow option = None
    let mutable shadowRefreshPending = false
    let shadowRefreshMessage = 0x8000 + 67
    let hwndRef = ref IntPtr.Zero
    let isShrunkCell = Bemo.Cell<bool>(Cell, false, (=))
    // How much of the strip shows, from the collapsed bar (0) to all of it (1), and where an
    // expand or collapse in progress is heading. Presentation state, like the location.
    let mutable reveal = 1.0
    let mutable revealFrom = 1.0
    let mutable revealTarget = 1.0
    let revealClock = Diagnostics.Stopwatch()
    let revealTimer = new Timer(Interval=15)
    /// Tabs a drop preview pushed aside, sliding into their new slot: how far from the slot each was
    /// drawn when it was pushed (along the row, and across it for a tab rising into the row), and
    /// when, on nudgeClock. Presentation state, like reveal: it never fires the cell listener, only
    /// the frames it paints.
    let nudges = Collections.Generic.Dictionary<Tab,float*float*float>(HashIdentity.Structural)
    /// The row the nudges were measured against; against any other row they mean nothing.
    let mutable nudgeKey : (Tab list * (float*float) * Sz * TabDock * TabDirection * int * int) option = None
    let nudgeClock = Diagnostics.Stopwatch()
    let nudgeTimer = new Timer(Interval=15)
    let mutable lastNudgeFrame = 0.0
    /// Previews fading into the row after a join, and when each fade started on nudgeClock. A fade
    /// is presentation state like a nudge: it only changes the frames this strip paints.
    let fades = Collections.Generic.Dictionary<Tab,float>(HashIdentity.Structural)
    /// The row the fades were started against; against any other row they mean nothing.
    let mutable fadeKey : (Tab list * (float*float) * Sz * TabDock * TabDirection * int * int) option = None
    /// The shade a leaving preview took out of the row, with where it was drawn and when its fade
    /// started: drawn over the row while it fades, because the tab itself has already left.
    let mutable leaving : (Img * Pt * float) option = None
    let mutable leavingKey : (Tab list * (float*float) * Sz * TabDock * TabDirection * int * int) option = None
    /// Rebuilding the tab shadow is the most expensive part of a paint: it scans the whole strip bitmap
    /// for its mask and blurs it into a second layered window. A paint that follows another one closely
    /// only moves the shadow; the rebuild happens once the row has stopped changing.
    let mutable shadowPending = false
    let mutable shadowRefreshing = false
    let mutable lastPaintAt = 0.0
    let paintClock = Diagnostics.Stopwatch()
    let shadowTimer = new Timer(Interval=40)
    /// The rounded offsets the last frame was drawn with: a frame that would draw the same pixels is
    /// not worth the render, the upload and the shadow.
    let mutable lastOffsets : int[] = [||]
    /// Waits out a press that handed the capture to the drag check, see WM_MOUSELEAVE.
    let captureWatch = new Timer(Interval=30)
    let mutable pointerSource : unit -> Point = fun () -> Cursor.Position
    /// A cut-short title shows in full once the pointer rests on its tab, see pointTitle.
    let titleTimer = new Timer(Interval=500)
    let mutable titleTab : Tab option = None
    let mutable titleTip : TabTitleTip option = None
    /// The full strip and the collapsed bar while an expand or collapse runs, so a frame only
    /// blends them; dropped whenever what they show changes.
    let mutable revealImages : (Img*Img) option = None
    let dropRevealImages() =
        revealImages |> Option.iter(fun (full,collapsed) -> full.bitmap.Dispose(); collapsed.bitmap.Dispose())
        revealImages <- None
    let destroyingEvent = Event<unit>()
    let makeFont = TabMetrics.font
    let mutable fontKey = Dpi.value(),0
    /// Fonts a DPI or height change replaced. A sprite built before the change still draws with the
    /// font it captured, so disposing one here would make that draw fail - and a cross-monitor drag
    /// changes the DPI while the tab is in the air. They are released with the strip instead.
    let retiredFonts = Collections.Generic.List<Font>()
    let mutable normalFont = makeFont 0 FontStyle.Regular
    let mutable renamedFont = makeFont 0 FontStyle.Italic

    let isMouseOverExport = Cell.export <| fun() ->
        if this.isVertical then ptCell.value.IsSome else hoverCell.value.IsSome

    let showInsideExport = Cell.export <| fun() -> showInsideCell.value

    let addEvent(evt,handler) =
        eventHandlersCell.map(fun s -> s.add(_os.setSingleWinEvent evt handler))
    do  
        addEvent(WinEvent.EVENT_SYSTEM_SWITCHSTART, fun(hwnd) -> isInAltTabCell.set(true))
        addEvent(WinEvent.EVENT_SYSTEM_SWITCHEND, fun(hwnd) -> isInAltTabCell.set(false))
        
        layeredWindowCell.value <-
            let style = WindowsStyles.WS_POPUP
            let styleExe =
                WindowsExtendedStyles.WS_EX_LAYERED ||| 
                WindowsExtendedStyles.WS_EX_TOOLWINDOW
            Some(_os.createWindow this.wndProc style styleExe)
        hwndRef := layeredWindowCell.value.Value.hwnd
        shadowWindow <- Some(new TabShadowWindow(_os, hwndRef.Value))
        
        isMouseOverExport.init()
        showInsideExport.init()

        revealTimer.Tick.Add(fun _ -> this.stepReveal())
        nudgeTimer.Tick.Add(fun _ -> this.stepNudge())
        shadowTimer.Tick.Add(fun _ ->
            shadowTimer.Stop()
            shadowPending <- false
            shadowRefreshing <- true
            try if this.visible then this.paint()
            finally shadowRefreshing <- false)
        dragScrollTimer.Tick.Add(fun _ ->
            match dragScrollLocation with
            | Some pt when this.isVertical && slideCell.value.IsSome ->
                this.scrollColumnTo(scrollOffsetCell.value+this.dragScrollDirection(pt)*this.ts.rowStep)
                this.updateDragScroll()
            | _ -> dragScrollTimer.Stop())
        titleTimer.Tick.Add(fun _ -> titleTimer.Stop(); this.showTitle())
        captureWatch.Tick.Add(fun _ ->
            if this.isVertical && WinUserApi.GetCapture()=IntPtr.Zero then
                this.refreshHover(trackLeave=false)
                if not this.pointerInside then captureWatch.Stop()
            elif WinUserApi.GetCapture()=IntPtr.Zero then
                captureWatch.Stop()
                this.refreshHover())

        Cell.listen <| fun() ->
            this.update()

    member _.setTabTint(tab,color) =
        if tabTint.value.tryFind(tab) <> color then tabTint.value <- match color with Some c -> tabTint.value.add tab c | None -> tabTint.value.remove tab
    member _.colorStyle with get() = colorStyleCell.value and set value = colorStyleCell.value <- value
    member _.numberBadgeKeys with get() = numberBadgeKeysCell.value and set value = numberBadgeKeysCell.value <- value
    member _.numberBadges with get() = numberBadgesCell.value and set value = numberBadgesCell.value <- value

    member private this.inAltSwitch = isInAltTabCell.value

    member private this.layeredWindow = layeredWindowCell.value.Value
    member private this.os = _os
    member private this.window : Window = this.os.windowFromHwnd(this.hwnd)
    member private this.pt = ptCell.value.Value
    member private this.ptScreen = this.window.ptToScreen(this.pt)
    member private this.setPt = ptCell.set

    member private this.size = sizeCell.value

    member private this.showInside = showInsideCell.value

    member private this.tsBase direction =
        {
            tabs = Map2(this.tabs.items.map <| fun tab ->
                let ti = this.tabInfo(tab)
                let ghost =
                    match ghostTabCell.value with
                    | Some(_, dragging) -> dragging = tab
                    | None -> false
                let tabInfo = {
                    tint = tabTint.value.tryFind(tab)
                    colorStyle = colorStyleCell.value
                    numberBadge = if numberBadgesCell.value then lorderCell.value.list |> List.tryFindIndex ((=) tab) |> Option.filter(fun i -> i<numberBadgeKeysCell.value.Length) |> Option.map(fun i -> string numberBadgeKeysCell.value.[i]) else None
                    bgColor = tabBgColor.value.tryFind(tab)
                    TabDisplayInfo.text = ti.text
                    // The window joined this group a moment ago and its icon is still coming; the drag
                    // brought its own, so the tab is drawn with the icon the user grabbed.
                    icon =
                        if ghost then
                            match ghostTabCell.value with
                            | Some(icon, _) -> icon
                            | None -> ti.iconSmall
                        else ti.iconSmall
                    textFont = if ti.isRenamed then renamedFont else normalFont
                    textBrush = SystemBrushes.MenuText
                    ghost = ghost
                }
                tab,tabInfo
            )
            hover = hoverCell.value
            captured = capturedCell.value
            lorder = lorderCell.value
            zorder = zorderCell.value
            size = this.size
            slide = this.slide
            direction = direction
            alignment = alignment.value.find direction
            onlyIcons = this.isIconOnly
            transparent = this.transparent
            dock = dockCell.value
            scrollOffset = scrollOffsetCell.value
            held = heldLayoutCell.value
            centerShift =
                if this.showInside then float(this.appearance.tabIndentFlipped - this.appearance.tabIndentNormal) / 2.0
                else 0.0
            appearance =
                if this.isIconOnly then
                    { this.appearance with tabMaxWidth = Dpi.scale 50 }
                else
                    this.appearance
        }
    member private this.ts : TabStripSprite<Tab> = this.tsBase this.direction
        
    member private this.onMouse(down, pt:Pt, btn, (tab:Tab, part)) =
        monitor.tabClick(btn, tab, part, down, pt)
        
    member private this.hit : Option<Tab*TabPart> = maybe {
        let! pt = ptCell.value
        let! hit = this.hitAt(pt)
        return hit 
        }
    
    member private this.processMouse(mouse,?trackLeave:bool) =
        // Hover/capture changes share one render. Equal hover values leave the
        // render listener untouched, while pointer/click callbacks still run.
        this.withUpdate(fun () ->
            match mouse with
            | MouseMove(pt) ->
                this.setPt(Some(pt))
                if this.window.hasCapture.not && defaultArg trackLeave true then
                    this.window.trackMouseLeave()
                let hit = this.hit
                hoverCell.set(hit)
                this.pointTitle(hit |> Option.map fst)
                let enableHoverActivate = Services.settings.getValue("enableHoverActivate").cast<bool>()
                if enableHoverActivate then
                    hit.iter <| fun(hitTab, hitPart) -> monitor.tabActivate(hitTab)
            | MouseClick(pt, btn, action) ->
                this.setPt(Some(pt))
                this.hideTitle()
                this.hit.iter <| fun(hitTab, hitPart) ->
                    match action with
                    | MouseDown ->
                        capturedCell.set(Some(hitTab, hitPart))
                    | MouseUp ->
                        capturedCell.value.iter <| fun(capturedTab, capturedPart) ->
                        let closeClick = btn = MouseLeft && hitPart = capturedPart && hitPart = TabClose
                        // Middle-click closes too (TabStripDecorator); both keep the layout.
                        let middleCloses = btn = MouseMiddle && (Services.settings.getValue("enableMiddleClickClose") :?> bool)
                        if hitTab = capturedTab && (closeClick || middleCloses) then
                            heldLayoutCell.set(Some(this.ts.layout))
                        if hitTab = capturedTab && closeClick then
                            monitor.tabClose(hitTab)
                        capturedCell.set(None)
                    | MouseDblClick -> ()
                    this.onMouse(action, pt, btn, (hitTab, hitPart))
                hoverCell.set(this.hit)
            | MouseLeave ->
                titleTab <- None
                this.hideTitle()
                this.setPt(None)
                capturedCell.set(None)
                hoverCell.set(None)
                heldLayoutCell.set(None))

    member private this.hideTitle() =
        titleTimer.Stop()
        titleTip |> Option.iter(fun tip -> tip.hide())

    /// From tab to tab, a title already showing moves straight to the next cut one; otherwise
    /// it waits for the pointer to rest. A click puts it away until the pointer moves on.
    member private this.pointTitle(tab:Tab option) =
        if tab<>titleTab then
            titleTab <- tab
            let showing = titleTip |> Option.exists(fun tip -> tip.Visible)
            this.hideTitle()
            if tab.IsSome then
                if showing then this.showTitle() else titleTimer.Start()

    member private this.showTitle() =
        titleTab |> Option.iter(fun tab ->
            let sprites : List2<Pt*TabSprite<Tab>> = this.tabSprites
            match sprites.list |> List.tryFind(fun (_,sprite) -> sprite.id=tab) with
            | Some(offset,sprite) when this.visible && not this.isShrunk && not (OpenMenus.any()) && sprite.titleCut ->
                let tip =
                    match titleTip with
                    | Some tip -> tip
                    | None ->
                        let tip = new TabTitleTip()
                        titleTip <- Some tip
                        tip
                let strip : Rect = this.bounds
                let anchor = if this.isVertical then Rect(strip.location.add(offset),sprite.size) else strip
                let pointer = pointerSource()
                tip.show(sprite.displayInfo.text,sprite.displayInfo.textFont,
                         Rectangle(anchor.x,anchor.y,anchor.width,anchor.height),pointer.X,
                         ?side=(if this.isVertical then Some this.dock else None),?pointerY=Some pointer.Y)
            | _ -> ())

    member internal _.titleTipShown = titleTip |> Option.exists(fun tip -> tip.Visible)

    member private this.wndProc(msg:Win32Message) =
        // Where the frame on screen sits in the full strip: collapsed, expanded or part way.
        let mousePt() = msg.lParam.location.add(this.renderedShift)
        let mouseDown btn =
            let pt = mousePt()
            if btn<>MouseLeft || not (this.beginColumnInteraction(pt)) then
                this.processMouse(MouseClick(pt, btn, MouseDown))
            msg.def()
        let mouseUp btn = 
            if btn=MouseLeft && (columnResize.IsSome || columnScroll.IsSome) then
                columnResize <- None
                columnScroll <- None
                this.window.releaseCapture()
                this.refreshHover()
            else this.processMouse(MouseClick(mousePt(), btn, MouseUp))
            msg.def()
        let mouseDblClick btn =
            this.processMouse(MouseClick(mousePt(), btn, MouseDblClick))
            msg.def()

        //don't callback until the window has been created
        if layeredWindowCell.value.IsSome then
            monitor.windowMsg(msg)

        match msg.msg with
        | message when message = shadowRefreshMessage ->
            shadowRefreshPending <- false
            shadowWindow |> Option.iter (fun shadow -> shadow.sync())
            0
        | WindowMessages.WM_WINDOWPOSCHANGED
        | WindowMessages.WM_SHOWWINDOW ->
            let result = msg.def()
            if not relocating then
                shadowWindow |> Option.iter (fun shadow -> shadow.sync())
                this.refreshShadow()
            result
        | WindowMessages.WM_MOUSEACTIVATE ->
            MouseActivateReturnCodes.MA_NOACTIVATE
        | WindowMessages.WM_MOUSEMOVE ->
            let pt = mousePt()
            match columnResize,columnScroll with
            | Some(start,width,growsRight),_ ->
                let delta = this.window.ptToScreen(pt).x-start
                let logical = int(Math.Round(float delta*96.0/float(Dpi.value())))
                this.columnWidth <- width+(if growsRight then logical else -logical)
            | _,Some(start,offset) ->
                let travel = this.ts.scrollTrack.height-(this.ts.scrollThumb |> Option.map(fun thumb -> thumb.height) |> Option.defaultValue 0)
                if travel>0 then this.scrollColumnTo(offset+(pt.y-start)*this.ts.scrollMaximum/travel)
            | _ ->
                Cursor.Current <- if this.isColumnResizeEdge(pt) then Cursors.SizeWE else Cursors.Default
                this.processMouse(MouseMove(pt))
            msg.def()
        | 0x0215 -> // Another window took the capture; stop before the next movement.
            columnResize <- None
            columnScroll <- None
            msg.def()
        | WindowMessages.WM_LBUTTONDOWN -> mouseDown MouseLeft
        | WindowMessages.WM_LBUTTONUP -> mouseUp MouseLeft
        | WindowMessages.WM_LBUTTONDBLCLK -> mouseDblClick(MouseLeft)
        | WindowMessages.WM_RBUTTONDOWN -> mouseDown MouseRight
        | WindowMessages.WM_RBUTTONUP -> mouseUp MouseRight
        | WindowMessages.WM_MBUTTONDOWN -> mouseDown MouseMiddle
        | WindowMessages.WM_MBUTTONUP -> mouseUp MouseMiddle
        | WindowMessages.WM_MOUSELEAVE ->
            // Pressing a tab hands the capture to the drag check, and Windows reports that as the
            // pointer leaving while it is still on the tab: auto-hide then began to collapse the
            // strip under the held button. Hover holds until the button is let go, then follows
            // where the pointer really is.
            if this.isVertical && WinUserApi.GetCapture()=IntPtr.Zero && this.pointerInside then
                // Expanding a layered column can deliver a stale leave at its attached edge.
                this.refreshHover(trackLeave=false)
                captureWatch.Start()
            elif WinUserApi.GetCapture()<>IntPtr.Zero && this.pointerInside then captureWatch.Start()
            else this.processMouse(MouseLeave)
            msg.def()
        | _ ->
            msg.def()

    member private this.appearance = appearanceCell.value.Value
    member private this.top = zorderCell.value.head
    member private this.isEmpty = this.lorder.isEmpty
    member private this.contentOffset = this.appearance.tabHeightOffset
    member private this.location = location
    // Used by native performance/regression hosts to verify that unchanged
    // hover and movement do not regenerate pixels; not a user-facing setting.
    member internal _.renderCount = renderedFrames
    
    member private this.update() =
        if not this.visible then this.hideTitle()
        let target = if this.isShrunk then 0.0 else 1.0
        dropRevealImages()
        if this.visible then
            if target<>revealTarget then
                revealTarget <- target
                // The first frame and an empty strip have nothing to move from.
                if renderedFrames>0L && not this.isEmpty && TabReveal.enabled() then
                    revealFrom <- reveal
                    revealClock.Restart()
                    revealTimer.Start()
                else
                    reveal <- target
                    revealTimer.Stop()
            this.paint()
        else
            revealTimer.Stop()
            revealTarget <- target
            reveal <- target
            shadowWindow |> Option.iter (fun shadow -> shadow.hide())
            this.window.hide()

    member private this.stepReveal() =
        let span = TabReveal.duration*abs(revealTarget-revealFrom)
        let t = if span<=0.0 then 1.0 else min 1.0 (revealClock.Elapsed.TotalMilliseconds/span)
        reveal <- revealFrom+(revealTarget-revealFrom)*TabReveal.ease t
        if t>=1.0 then
            reveal <- revealTarget
            revealTimer.Stop()
            dropRevealImages()
        if this.visible then this.paint()

    member private this.paint() =
        StartupTrace.measure "strip.paint" (fun () -> this.paintCore())

    member private this.paintCore() =
        let image,offset =
            StartupTrace.measure "strip.paint.render" (fun () ->
            if reveal>=1.0 then this.renderAt false,0
            elif reveal<=0.0 then
                let offset =
                    if this.isVertical then
                        let anchoredRight = (this.dock=TabDockLeft) <> this.showInside
                        if anchoredRight then max 0 (this.size.width-Dpi.scale 4) else 0
                    else this.ts.collapsedOffset
                this.renderAt true,offset
            else
                let full,collapsed =
                    match revealImages with
                    | Some images -> images
                    | None ->
                        let images = this.renderAt false,this.renderAt true
                        revealImages <- Some images
                        images
                if this.isVertical then
                    TabReveal.sideFrame full.bitmap collapsed.bitmap reveal ((this.dock=TabDockLeft) <> this.showInside)
                else TabReveal.frame full.bitmap collapsed.bitmap reveal this.direction)
        try
            renderedOffset <- offset
            renderedFrames <- renderedFrames+1L
            StartupTrace.measure "strip.paint.upload" (fun () ->
                this.window.update(image, this.location.add(this.renderedShift), 255uy))
            // A burst of paints - a join, a slide, a resize - would rebuild the mask on every frame.
            let recent = paintClock.Elapsed.TotalMilliseconds - lastPaintAt < 40.0
            let animating = nudges.Count > 0 || fades.Count > 0 || leaving.IsSome
            let skip = (not shadowRefreshing) && (animating || shadowPending)
            StartupTrace.measure "strip.paint.shadow" (fun () ->
              shadowWindow |> Option.iter (fun shadow ->
                if reveal<1.0 || this.isEmpty || (this.isVertical && SystemInformation.HighContrast) then shadow.hide()
                elif skip then shadow.move()
                else shadow.update(image, 255uy, this.direction)))
            // Only a paint that came hot on the heels of another arms the deferred rebuild: a lone paint
            // has already drawn the shadow it needs.
            if recent && not skip && not shadowRefreshing && shadowWindow.IsSome && not animating then
                shadowPending <- true
                shadowTimer.Stop()
                shadowTimer.Start()
            lastPaintAt <- paintClock.Elapsed.TotalMilliseconds
        finally
            image.bitmap.Dispose()

    member private this.render : Img = this.renderAt this.isShrunk

    member private this.renderAt collapsed : Img =
        try
            let image =
                if collapsed then this.ts.renderCollapsed
                else
                    let image = this.ts.renderWith (this.nudgeShift()) (this.fadeAlpha())
                    this.drawLeaving image
                    image
            if dimmedCell.value && not SystemInformation.HighContrast then
                try Img(TabDimming.render this.appearance.tabNormalBgColor image.bitmap)
                finally image.bitmap.Dispose()
            else image
        with ex ->
            Img(Sz(1,1))


    member private this.withUpdate f =
        Cell.beginUpdate()
        try f()
        finally Cell.endUpdate()

    member private this.move() =
        // Layered HWNDs retain their pixels. Moving them needs neither a new
        // strip bitmap nor extraction/comparison of the shadow silhouette.
        relocating <- true
        try
            this.window.updateLocation(this.location.add(this.renderedShift))
            shadowWindow |> Option.iter(fun shadow -> shadow.move())
        finally relocating <- false

    // Run after Windows finishes activation/owner popup bookkeeping. Coalescing
    // prevents resize/move message bursts from producing redundant refreshes.
    member this.refreshShadow() =
        if this.hwnd <> IntPtr.Zero && shadowWindow.IsSome && not shadowRefreshPending then
            shadowRefreshPending <- true
            if not (WinUserApi.PostMessage(this.hwnd, shadowRefreshMessage, IntPtr.Zero, IntPtr.Zero)) then
                shadowRefreshPending <- false

    member this.hwnd = hwndRef.Value
    
    member this.addTabSlide tab (slide:Option<_>) =
        let previousCount = this.tabs.count
        Cell.beginUpdate()
        let addToEnd(l:Cell<List2<_>>)=
            if l.value.any((=) tab).not then
                l.map(fun l -> l.append(tab))
        addToEnd(lorderCell)
        addToEnd(zorderCell)
        slide.iter <| fun slide ->
            this.slide <- Some(slide)
        Cell.endUpdate()
        if this.isVertical && this.tabs.count<>previousCount then dockChangedEvent.Trigger()
    
    member this.addTab tab = this.addTabSlide tab None

    member this.removeTab tab =
        Cell.beginUpdate()
        lorderCell.map(fun l -> l.where((<>) tab))
        zorderCell.map(fun z -> z.where((<>) tab))
        tabInfoCell.map(fun m -> m.remove tab)
        tabTint.map(fun m -> m.remove tab)
        tabBgColor.map(fun m -> m.remove tab)
        this.ensureSelectedVisible()
        Cell.endUpdate()
        if this.isVertical then dockChangedEvent.Trigger()

    member this.tabs : Set2<Tab> = Set2(lorderCell.value)

    member this.lorder 
        with get() : List2<_> = lorderCell.value


    member this.movedTab = this.ts.movedTab

    member this.moveTab(tab, index) =
        this.withUpdate(fun () ->
            lorderCell.set(lorderCell.value.move((=) tab, index))
            this.ensureSelectedVisible())
        monitor.tabMoved(tab, index)
        tabMovedEvent.Trigger(tab, index)

    member this.tabMoved = tabMovedEvent.Publish

    member this.zorder
        with get() = zorderCell.value
        and set(zorder:List2<Tab>) =
            let next = zorder.where(this.tabs.contains)
            if next.list <> zorderCell.value.list then
                this.withUpdate(fun () ->
                    zorderCell.set(next)
                    this.ensureSelectedVisible())

    member this.sprite = this.ts.sprite

    member this.tabSprites = this.ts.tabSprites
            
    member this.isIconOnly 
        with get() = iconOnlyCell.value || (this.isVertical && columnWidthValue=fst SettingsCatalog.columnWidthRange)
        and set(value) =
            iconOnlyCell.set(value)
            if this.isVertical then
                if not value && columnWidthValue=fst SettingsCatalog.columnWidthRange then this.columnWidth <- SettingsCatalog.columnWidthDefault
                else dockChangedEvent.Trigger()

    member this.dock
        with get() = dockCell.value
        and set(value) =
            this.hideTitle()
            this.withUpdate(fun () ->
                slideCell.set(None)
                heldLayoutCell.set(None)
                scrollOffsetCell.set(0)
                dockCell.set(value)
                this.ensureSelectedVisible())
            dockChangedEvent.Trigger()
            layoutChangedEvent.Trigger(true)

    member this.layoutChanged = layoutChangedEvent.Publish
    member this.dockChanged = dockChangedEvent.Publish
    member this.columnWidth
        with get() = columnWidthValue
        and set(value) =
            let next = between SettingsCatalog.columnWidthRange value
            let wasIconOnly = iconOnlyCell.value
            if this.isVertical then iconOnlyCell.set(false)
            if next<>columnWidthValue || (this.isVertical && wasIconOnly) then
                columnWidthValue <- next
                dockChangedEvent.Trigger()
                layoutChangedEvent.Trigger(true)

    member private this.isColumnResizeEdge(pt:Pt) =
        if not this.isVertical || this.isShrunk then false
        else
            let growsRight = (this.dock=TabDockRight) <> this.showInside
            if growsRight then pt.x>=this.size.width-Dpi.scale 4 else pt.x<Dpi.scale 4

    member private this.beginColumnInteraction(pt:Pt) =
        if not this.isVertical || this.isShrunk then false
        elif this.isColumnResizeEdge(pt) then
            this.hideTitle()
            columnResize <- Some(this.window.ptToScreen(pt).x,(if this.isIconOnly then fst SettingsCatalog.columnWidthRange else columnWidthValue),(this.dock=TabDockRight) <> this.showInside)
            this.window.setCapture()
            true
        elif this.ts.scrollThumb.IsSome && this.ts.scrollTrack.containsPoint(pt) then
            this.hideTitle()
            let thumb = this.ts.scrollThumb.Value
            if thumb.containsPoint(pt) then
                columnScroll <- Some(pt.y,scrollOffsetCell.value)
                this.window.setCapture()
            else this.scrollColumnTo(scrollOffsetCell.value+(if pt.y<thumb.y then -this.size.height else this.size.height))
            true
        else false

    member this.scrollColumnTo(value) =
        if this.isVertical then
            this.hideTitle()
            this.withUpdate(fun () ->
                scrollOffsetCell.set(between (0,this.ts.scrollMaximum) value)
                hoverCell.set(None))

    member private this.dragScrollDirection(pt:Pt) =
        let margin = Dpi.scale 20
        let edge = min (Dpi.scale 24) (this.size.height/3)
        if pt.x < -margin || pt.x > this.size.width+margin || pt.y < -margin || pt.y > this.size.height+margin then 0
        elif pt.y<edge && scrollOffsetCell.value>0 then -1
        elif pt.y>=this.size.height-edge && scrollOffsetCell.value<this.ts.scrollMaximum then 1
        else 0

    member private this.updateDragScroll() =
        let active = this.isVertical && slideCell.value.IsSome &&
                     (dragScrollLocation |> Option.exists(fun pt -> this.dragScrollDirection(pt)<>0))
        if active then dragScrollTimer.Start() else dragScrollTimer.Stop()

    member this.dragScrollPoint
        with set(value) =
            dragScrollLocation <- value
            this.updateDragScroll()

    member this.isVertical = this.dock=TabDockLeft || this.dock=TabDockRight
    member this.columnContentHeight = this.lorder.count*this.ts.rowStep+Dpi.scale 4
    member private this.renderedShift = if this.isVertical then Pt(renderedOffset,0) else Pt(0,renderedOffset)

    /// Switching into an overflowing column keeps the selected window's row reachable.
    member private this.ensureSelectedVisible() =
        if this.isVertical && not this.isEmpty then
            let index = lorderCell.value.findIndex((=)zorderCell.value.head)
            let top = index*this.ts.rowStep+Dpi.scale 4
            let bottom = top+this.ts.rowHeight
            let maximum = max 0 (this.lorder.count*this.ts.rowStep+Dpi.scale 4-this.size.height)
            let desired = if top<scrollOffsetCell.value then top elif bottom>scrollOffsetCell.value+this.size.height then bottom-this.size.height else scrollOffsetCell.value
            let next = between (0,maximum) desired
            if next<>scrollOffsetCell.value then this.hideTitle()
            scrollOffsetCell.set(next)

    member this.isShrunk    
        with get() = isShrunkCell.value
        and set(newValue) =
            if not newValue || (columnResize.IsNone && columnScroll.IsNone) then isShrunkCell.set(newValue)

    member this.isMouseOver = isMouseOverExport :> ICellOutput<_>

    /// A popup over the strip, such as its menu, takes the pointer without a move, so hover
    /// ends. When it goes, the pointer may still be over a tab: hover there again and track
    /// its leaving, as a move would.
    member this.refreshHover(?trackLeave:bool) =
        if this.visible && this.window.isVisible then
            let cursor = pointerSource()
            let point = Pt(cursor.X,cursor.Y)
            if this.pointerBounds.containsPoint(point) && this.pointerUncovered(point) then
                this.processMouse(MouseMove(Pt(cursor.X-location.x,cursor.Y-location.y)),?trackLeave=trackLeave)
            else this.processMouse(MouseLeave)

    /// Where the pointer is on screen; native hosts supply one instead of moving the real cursor.
    member internal _.pointer with get() = pointerSource and set(value) = pointerSource <- value

    member private this.pointerBounds : Rect =
        if this.isVertical && revealTarget>0.0 then
            let tolerance = Dpi.scale TabMetrics.sideHoverTolerance
            if this.dock=TabDockLeft then Rect(location.sub(Pt(tolerance,0)),Sz(this.size.width+tolerance,this.size.height))
            else Rect(location,Sz(this.size.width+tolerance,this.size.height))
        else this.window.bounds

    /// Coordinate polling must respect popups covering the strip, while retaining the
    /// tolerance beyond its edge and the full target bounds during expansion.
    member private this.pointerUncovered(point:Pt) =
        let hit = WinUserApi.WindowFromPoint(point.POINT)
        if hit=IntPtr.Zero || hit=this.hwnd then true
        elif this.window.bounds.containsPoint(point) then false
        else
            let root = WinUserApi.GetAncestor(hit,GetAncestorConstants.GA_ROOT)
            let rec above hwnd =
                hwnd<>IntPtr.Zero && (hwnd=root || above (WinUserApi.GetWindow(hwnd,GetWindowConstants.GW_HWNDPREV)))
            not (above (WinUserApi.GetWindow(this.hwnd,GetWindowConstants.GW_HWNDPREV)))

    member private this.pointerInside =
        let cursor = pointerSource()
        let point = Pt(cursor.X,cursor.Y)
        this.pointerBounds.containsPoint(point) && this.pointerUncovered(point)

    /// No room above the window on its monitor, so the tabs sit inside it over the title bar:
    /// true when the window is maximized, snapped to the top, or moved against the top edge.
    member this.isShownInside = showInsideExport :> ICellOutput<bool>

    member this.getAlignment direction = alignment.value.find(direction)

    /// The alignments chosen for this strip alone, which the default no longer changes.
    member this.alignmentChoices = alignmentOverrides |> Set.toList |> List.map(fun direction -> direction, alignment.value.find direction)

    member this.setAlignment((direction, newAlignment)) =
        alignmentOverrides <- alignmentOverrides.Add(direction)
        alignment.map(fun m -> m.add direction newAlignment)
        if this.isVertical then dockChangedEvent.Trigger()
        layoutChangedEvent.Trigger(true)

    member this.setDefaultAlignment(value:string) =
        let next = alignmentMap.TryFind(value) |> Option.defaultValue TabCenter
        alignment.map(fun current ->
            [TabUp;TabDown] |> List.fold (fun result direction ->
                if alignmentOverrides.Contains(direction) then result else result.add direction next) current)
        if this.isVertical then dockChangedEvent.Trigger()
        layoutChangedEvent.Trigger(false)
            
    member this.direction = if showInsideCell.value then TabDown else TabUp
    
    member this.tabInfo tab : TabInfo = 
        tabInfoCell.value.tryFind(tab).def({
            text = ""
            isRenamed = false
            iconSmall = System.Drawing.SystemIcons.Application
            iconBig = System.Drawing.SystemIcons.Application
            preview = fun() -> Img(Sz(1,1))
        })

    member this.setTabInfo((tab, tabInfo)) = 
        tabInfoCell.map(fun m -> m.add tab tabInfo)

    member internal this.hasTabInfo(tab) = tabInfoCell.value.contains(tab)
            
    member this.tabLocation = this.ts.tabLocation

    /// The tab this strip is showing as the place a drag would land, with the icon to draw it with.
    member this.ghostTab
        with get() : (Icon * Tab) option = ghostTabCell.value
        and set(value:(Icon * Tab) option) =
            let previous = ghostTabCell.value
            // Identity, not equality: the icon is an owned GDI handle, so "the same preview" means the
            // same handle on the same tab, never two tuples that merely compare equal.
            let unchanged =
                match previous, value with
                | Some(wasIcon, wasTab), Some(icon, tab) -> wasTab = tab && obj.ReferenceEquals(wasIcon, icon)
                | None, None -> true
                | _ -> false
            if not unchanged then
                ghostTabCell.set value
                // The preview draws with a copy this strip made and this strip releases, because the
                // drag that lent the icon ends on a posted message and the next drag replaces it.
                match previous with
                | Some(icon, _) -> icon.Dispose()
                | None -> ()

    /// The slot a drag would put its tab in: how many of the tabs this strip shows sit left of the
    /// pointer. The previewing tab is not counted, since it is already in the row.
    member private this.dropIndex (pt:Pt) =
        this.tabSprites.list
        |> List.filter(fun (location:Pt, sprite:TabSprite<Tab>) ->
            match ghostTabCell.value with
            | Some(_, ghost) when sprite.id = ghost -> false
            | _ ->
                // A vertical strip stacks its tabs by y: their x is the same for all of them.
                if this.isVertical then location.y + sprite.size.height / 2 <= pt.y
                else location.x + sprite.size.width / 2 <= pt.x)
        |> List.length

    /// Put the previewing tab in the slot the pointer names, so it stands where the drop will leave
    /// it instead of sliding under the pointer: the tab the user holds is the one that moves.
    member this.previewDrop (pt:Pt, ?animate:bool) =
        let animate = defaultArg animate true
        match ghostTabCell.value with
        | Some(_, ghost) ->
            let current = lorderCell.value
            let index = this.dropIndex pt
            // Only move the tab when the slot it names differs from the one it already sits in: the old
            // guard compared List2 wrappers, which never compare equal, so every pointer move rebuilt
            // the order and repainted the row for an unchanged result.
            match current.tryFindIndex((=) ghost) with
            | Some at when at = index ->
                // A move inside one slot changes nothing, but a slide may still be running and the
                // drag starves the strip's timer, so the pointer path advances it here.
                this.stepNudge()
            | _ -> this.nudgeTo (current.move((=) ghost, index)) animate
        | None -> ()

    /// The row the nudges belong to. Anything that changes the row - a tab added or removed, a
    /// resize, a dock, direction, scroll or scale change - makes every stored offset meaningless.
    member private this.nudgeRowKey (strip:TabStripSprite<Tab>) (order:Tab list) =
        (order, strip.layout, strip.size, strip.dock, strip.direction, strip.scrollOffset, Dpi.value())

    /// Whether this strip draws frames an animation can show: on screen, fully open, and with
    /// Windows' own animation effects left on.
    member private this.animates =
        TabReveal.enabled() && renderedFrames>0L && this.visible && reveal>=1.0 && not this.isShrunk

    /// How far each pushed tab is drawn from its settled slot right now. The zero shift costs one test.
    member private this.nudgeShift() : Tab -> Pt =
        if nudges.Count = 0 then (fun _ -> Pt.empty)
        elif nudgeKey <> Some(this.nudgeRowKey this.ts lorderCell.value.list) then
            this.stopAnimations()
            (fun _ -> Pt.empty)
        else
            let elapsed = nudgeClock.Elapsed.TotalMilliseconds
            let vertical = this.isVertical
            fun (tab:Tab) ->
                match nudges.TryGetValue tab with
                | true,(along,across,start) ->
                    // Aged from this tab's own push: a second push starts at its full offset and eases
                    // from there, instead of inheriting the age of the first.
                    let along = int(Math.Round(TabNudge.offset along (elapsed - start)))
                    let across = int(Math.Round(TabNudge.offset across (elapsed - start)))
                    if vertical then Pt(across,along) else Pt(along,across)
                | _ -> Pt.empty

    /// How solid each tab is drawn: a tab a join preview has just put in the row fades in over a few
    /// frames instead of standing solid in the first one.
    member private this.fadeAlpha() : Tab -> float32 =
        if fades.Count = 0 then (fun _ -> 1.0f)
        elif fadeKey <> Some(this.nudgeRowKey this.ts lorderCell.value.list) then
            // The row changed under the fades: their times belong to tabs that are not where they were.
            fades.Clear()
            fadeKey <- None
            (fun _ -> 1.0f)
        else
            let elapsed = nudgeClock.Elapsed.TotalMilliseconds
            fun (tab:Tab) ->
                match fades.TryGetValue tab with
                | true,start -> float32(TabFade.progress (elapsed - start))
                | _ -> 1.0f

    /// A preview tab that has left the row is drawn off it, fading, instead of vanishing in the frame
    /// the drag carried it away. It is drawn before the row is dimmed, so it dims with the row.
    member private this.drawLeaving (image:Img) =
        if leaving.IsSome && leavingKey <> Some(this.nudgeRowKey this.ts lorderCell.value.list) then
            this.dropLeaving()
        match leaving with
        | Some(shade, at, start) ->
            let alpha = float32(1.0 - TabFade.progress (nudgeClock.Elapsed.TotalMilliseconds - start))
            if alpha > 0.0f then
                use graphics = image.graphics
                use attributes = new ImageAttributes()
                attributes.SetColorMatrix(ColorMatrix(Matrix33 = alpha))
                graphics.DrawImage(shade.bitmap,Rectangle(at.Point,shade.size.Size),0,0,shade.width,shade.height,GraphicsUnit.Pixel,attributes)
        | None -> ()

    member private this.dropLeaving() =
        leaving |> Option.iter(fun (shade,_,_) -> shade.bitmap.Dispose())
        leaving <- None
        leavingKey <- None

    /// Every preview animation and the picture it owns, for a row that changed under them or a strip
    /// that is going away.
    member private this.stopAnimations() =
        nudges.Clear()
        fades.Clear()
        nudgeKey <- None
        fadeKey <- None
        this.dropLeaving()
        nudgeTimer.Stop()
        nudgeClock.Stop()
        lastOffsets <- [||]

    /// A tab another group's drag brings in rises into its slot from the window edge while the tabs
    /// it makes room for slide aside, instead of the whole row re-laying in one frame.
    member this.joinPreview (tab:Tab, pt:Pt) =
        let before = this.ts
        let origin = this.location
        this.withUpdate(fun () ->
            this.addTab tab
            match ghostTabCell.value with
            | Some(_, ghost) when ghost = tab ->
                this.nudgeFrom before origin (lorderCell.value.move((=) ghost, this.dropIndex pt)) (Some tab) true
                if this.animates then
                    // A new preview takes over from the shade the last one left.
                    this.dropLeaving()
                    if not nudgeClock.IsRunning then nudgeClock.Restart()
                    fades.[tab] <- nudgeClock.Elapsed.TotalMilliseconds
                    fadeKey <- Some(this.nudgeRowKey this.ts lorderCell.value.list)
                    if not nudgeTimer.Enabled then nudgeTimer.Start()
            | _ -> ())

    /// The drag carries its tab away again: the preview goes, and the tabs it pushed aside slide back
    /// instead of the whole row re-laying in one frame.
    member this.leavePreview (tab:Tab) =
        let before = this.ts
        let origin = this.location
        // Caught while the row still holds the tab: it is the shade that row would have drawn.
        let shade =
            if this.animates && before.tabs.contains tab then
                let shift = this.nudgeShift()
                let alpha = this.fadeAlpha() tab
                before.tabSprites.list
                |> List.tryFind(fun (_,sprite) -> sprite.id = tab)
                |> Option.bind(fun (location,sprite) ->
                    try
                        let drawn = if alpha < 1.0f then (FadedSprite(sprite, alpha) :> ISprite).render else sprite.render
                        Some(drawn, origin.add(location).add(shift tab))
                    with _ -> None)
            else None
        this.withUpdate(fun () ->
            this.ghostTab <- None
            if this.tabs.contains tab then
                this.removeTab tab
                match shade with
                | Some(image, at) -> this.startLeaving image (at.sub(this.location))
                | None -> ()
                this.nudgeFrom before origin lorderCell.value None true
            else shade |> Option.iter(fun (image,_) -> image.bitmap.Dispose()))
        if leaving.IsSome && not nudgeTimer.Enabled then nudgeTimer.Start()

    /// Draw the shade a leaving preview took out over the row while it fades off it.
    member private this.startLeaving (image:Img) (at:Pt) =
        this.dropLeaving()
        if not nudgeClock.IsRunning then nudgeClock.Restart()
        leaving <- Some(image, at, nudgeClock.Elapsed.TotalMilliseconds)
        leavingKey <- Some(this.nudgeRowKey this.ts lorderCell.value.list)
        // The order change paints this frame synchronously; counting it keeps the first tick from
        // painting the same shade again a millisecond later.
        lastNudgeFrame <- nudgeClock.Elapsed.TotalMilliseconds

    /// Record where the tabs are drawn now, before the order changes, so the ones the new order
    /// displaces slide into place instead of jumping. A tab pushed again starts from where it is
    /// drawn at that moment, not from the slot it is on its way to.
    member private this.nudgeTo (next:List2<Tab>) (animate:bool) = this.nudgeFrom this.ts this.location next None animate

    /// `before` is the row as last drawn, which a joining tab is not part of yet; `rising` is that tab.
    member private this.nudgeFrom (before:TabStripSprite<Tab>) (origin:Pt) (next:List2<Tab>) (rising:Tab option) (animate:bool) =
        let drawable = animate && this.animates
        if not drawable then
            // Nothing this strip paints can show an animation: drop what the last one left behind
            // rather than leave it waiting on a clock that is stopped.
            this.stopAnimations()
        else
            let after = { this.ts with lorder = next }
            let beforeKey = Some(this.nudgeRowKey before before.lorder.list)
            if nudgeKey.IsSome && nudgeKey <> beforeKey then
                // The row changed under the slides: their offsets belong to a row that is gone.
                nudges.Clear()
            if fadeKey.IsSome && fadeKey <> beforeKey then
                fades.Clear()
            // A preview moving to another slot keeps its fade age; only unrelated row changes cancel it.
            fades.Keys |> Seq.filter(fun tab -> not(after.tabs.contains tab)) |> List.ofSeq
                       |> List.iter(fun tab -> fades.Remove tab |> ignore)
            fadeKey <- if fades.Count > 0 then Some(this.nudgeRowKey after next.list) else None
            let along (pt:Pt) = if this.isVertical then pt.y else pt.x
            let across (pt:Pt) = if this.isVertical then pt.x else pt.y
            // Growing a centred or bottom-aligned side column moves the strip's origin too.
            let moved = origin.sub(this.location)
            // tabSprites is (location, sprite): the sprite carries the tab it draws.
            let positions (strip:TabStripSprite<Tab>) =
                strip.tabSprites.list |> List.map(fun (location,sprite) -> sprite.id, location)
            let was = positions before
            let elapsed () =
                if nudgeClock.IsRunning then nudgeClock.Elapsed.TotalMilliseconds else 0.0
            let drawnOffset (tab:Tab) =
                match nudges.TryGetValue tab with
                | true,(along,across,start) -> TabNudge.offset along (elapsed() - start), TabNudge.offset across (elapsed() - start)
                | _ -> 0.0, 0.0
            // A rising tab starts a whole tab deep on the window's side of the row, where the strip
            // bitmap ends, so it comes up out of the window edge.
            let rise (sprite:TabSprite<Tab>) =
                if this.isVertical then
                    let width = float sprite.size.width
                    if (this.dock=TabDockLeft) <> this.showInside then width else -width
                else
                    let height = float sprite.size.height
                    if this.direction=TabUp then height else -height
            let pushed =
                after.tabSprites.list |> List.choose(fun (location,sprite) ->
                    let tab = sprite.id
                    match was |> List.tryFind(fun (other,_) -> other = tab) with
                    | Some(_,wasAt) ->
                        let drawnAlong,drawnAcross = drawnOffset tab
                        let displacement = wasAt.add(moved).sub(location)
                        let from = float(along displacement) + drawnAlong
                        let fromAcross = float(across displacement) + drawnAcross
                        if abs from >= 1.0 || abs fromAcross >= 1.0 then Some(tab, from, fromAcross) else None
                    | None when rising = Some tab -> Some(tab, 0.0, rise sprite)
                    | None -> None)
            nudges.Clear()
            nudgeKey <- None
            if pushed.Length > 0 then
                if not nudgeClock.IsRunning then nudgeClock.Restart()
                let startedAt = elapsed()
                pushed |> List.iter(fun (tab,from,across) -> nudges.[tab] <- (from, across, startedAt))
                nudgeKey <- Some(this.nudgeRowKey after next.list)
                if not nudgeTimer.Enabled then nudgeTimer.Start()
        // The order change paints synchronously; counting it as the last frame keeps the pointer path
        // and the timer from painting again a millisecond later.
        if nudges.Count > 0 then lastNudgeFrame <- nudgeClock.Elapsed.TotalMilliseconds
        lorderCell.set next

    /// Advance the slides and the fades. Progress is read from the clock, never from tick counts: a
    /// tick that arrives late must not stretch or shorten the motion.
    member private this.stepNudge () =
        let active() = nudges.Count > 0 || fades.Count > 0 || leaving.IsSome
        if active() then
            let nowMs = nudgeClock.Elapsed.TotalMilliseconds
            let settled =
                nudges |> Seq.filter(fun entry -> let _,_,start = entry.Value in nowMs - start >= TabNudge.duration)
                       |> Seq.map(fun entry -> entry.Key) |> List.ofSeq
            settled |> List.iter(fun tab -> nudges.Remove tab |> ignore)
            fades |> Seq.filter(fun entry -> nowMs - entry.Value >= TabFade.duration)
                  |> Seq.map(fun entry -> entry.Key) |> List.ofSeq
                  |> List.iter(fun tab -> fades.Remove tab |> ignore)
            match leaving with
            | Some(_,_,start) when nowMs - start >= TabFade.duration -> this.dropLeaving()
            | _ -> ()
            let running = active()
            // Every producer of a frame - the order change, the pointer, the timer - shares this one
            // deadline, so a burst of pointer moves cannot repaint the row several times over.
            let due = nowMs - lastNudgeFrame >= 12.0 || not running
            // A frame whose rounded offsets and opacities are the ones already drawn changes nothing
            // on screen.
            let offsets =
                if due then
                    let slides =
                        nudges |> Seq.collect(fun entry ->
                                    let along,across,start = entry.Value
                                    [ int(Math.Round(TabNudge.offset along (nowMs - start)))
                                      int(Math.Round(TabNudge.offset across (nowMs - start))) ])
                               |> Array.ofSeq |> Array.sort
                    let opacities =
                        fades |> Seq.map(fun entry -> int(Math.Round(TabFade.progress (nowMs - entry.Value) * 64.0)))
                              |> Array.ofSeq |> Array.sort
                    let shade =
                        leaving |> Option.map(fun (_,_,start) -> int(Math.Round((1.0 - TabFade.progress (nowMs - start)) * 64.0)))
                                |> Option.toArray
                    Array.concat [slides; opacities; shade]
                else [||]
            // The frame an animation ends on always paints: the frames before it only moved the shadow,
            // and this is the one that draws the shadow the row settles with. An animation that ran out
            // between two frames would otherwise compare its empty offsets with the ones it started from.
            let changed = not running || not(lastOffsets = offsets)
            if due && changed then
                let rowKey = Some(this.nudgeRowKey this.ts lorderCell.value.list)
                if (nudges.Count > 0 && nudgeKey <> rowKey) || (fades.Count > 0 && fadeKey <> rowKey) then
                    this.stopAnimations()
                else
                    lastNudgeFrame <- nowMs
                    lastOffsets <- offsets
                    DragTrace.write(sprintf "NUDGE frame tabs=%d fades=%d elapsed=%d" nudges.Count fades.Count (int nowMs))
                if not (active()) then
                    // A drag can starve the deferred timer too; finish its shadow in this paint.
                    shadowTimer.Stop()
                    shadowPending <- false
                    shadowRefreshing <- true
                    try if this.visible then this.paint()
                    finally shadowRefreshing <- false
                elif this.visible then this.paint()
            elif due then lastNudgeFrame <- nowMs
        if not (active()) then this.stopAnimations()
    
    /// The tab a floating drag carries: where a strip that holds only this tab puts it, and how that
    /// strip draws it. That strip is the floating window's own, and not the one the drag started in —
    /// a maximized window's strip is as wide as the screen, keeps room for the caption buttons and
    /// draws its row inside the title bar, which runs the other way round. Laid out and drawn for the
    /// strip it left, a centred tab landed at the far right of the screen with the corners flipped.
    member this.floatingTab (tab:Tab) (appearanceAt:unit -> TabAppearanceInfo) : FloatingTabSprite =
        let strip = this.tsBase this.direction
        // Read once, here, while the strip still holds the tab: the drag gives it up as soon as it
        // leaves, and with it the display info both the slot and the picture are read from. The icon
        // goes with the group's window, which is disposed when the tab leaves, while the picture is
        // still to be drawn for the window that floats — so the drag keeps a copy of its own.
        let display = strip.tabs.find(tab)
        let icon = display.icon.Clone() :?> Icon
        let style = display.textFont.Style
        // One font per monitor this drag is drawn for. The strip's own fonts belong to the strip's
        // scale, and a picture drawn for another monitor must not borrow them - they are also the
        // fonts a DPI change on that strip replaces.
        let fonts = Collections.Generic.Dictionary<int * int,Font>()
        let fontAt dpi (appearance:TabAppearanceInfo) =
            // The row height can change while the drag runs (the appearance is read live), and a font
            // made for the old height would clip the name in the new row.
            let key = dpi, appearance.tabHeight
            match fonts.TryGetValue key with
            | true, font -> font
            | _ ->
                let font = TabMetrics.font appearance.tabHeight style
                fonts.[key] <- font
                font
        let at dpi (appearance:TabAppearanceInfo) showInside size =
            let direction = if showInside then TabDown else TabUp
            { strip with
                appearance = appearance
                tabs = Map2(List2([tab, { display with icon = icon; textFont = fontAt dpi appearance }]))
                lorder = List2([tab])
                zorder = List2([tab])
                slide = None
                held = None
                hover = None
                captured = None
                scrollOffset = 0
                size = size
                direction = direction
                // Alignment is a per-direction setting: carried into the other direction unchanged, the
                // tab is laid out by the alignment of the strip it left and moves sideways on release.
                alignment = this.getAlignment direction
                centerShift =
                    if showInside then float(appearance.tabIndentFlipped - appearance.tabIndentNormal) / 2.0
                    else 0.0 }
        // Everything inside the override reads that monitor's scale: the appearance, the row the tab
        // is laid in, the font, and the cache key the body is stored under. The override has to wrap
        // the layout and the drawing themselves, not the record that describes them - building the
        // record and then reading it outside the override leaves every length at the thread's scale.
        let forDpi dpi showInside size (apply:TabStripSprite<Tab> -> 'a) =
            Dpi.withValue dpi (fun () -> apply (at dpi (appearanceAt()) showInside size))
        { location = (fun dpi showInside size -> forDpi dpi showInside size (fun strip -> strip.tabLocation tab))
          image = (fun dpi showInside size -> forDpi dpi showInside size (fun strip -> strip.renderTab tab))
          release = (fun () ->
                        icon.Dispose()
                        fonts.Values |> Seq.iter(fun font -> font.Dispose())
                        fonts.Clear()) }

    member this.setTabBgColor((tab, color)) =
        if tabBgColor.value.tryFind(tab) <> color then
            match color with
            | Some(color) -> 
                tabBgColor.map(fun m -> m.add tab color)
            | None -> 
                tabBgColor.map(fun m -> m.remove tab)
        
    member this.setTabAppearance(appearance:TabAppearanceInfo) =
        let key = Dpi.value(),appearance.tabHeight
        if fontKey <> key then
            retiredFonts.Add normalFont
            retiredFonts.Add renamedFont
            normalFont <- makeFont appearance.tabHeight FontStyle.Regular
            renamedFont <- makeFont appearance.tabHeight FontStyle.Italic
            fontKey <- key
        appearanceCell.set(Some(appearance))
            
    member this.contentBounds 
        with get() = contentBoundsCell.value
        and set(value) = contentBoundsCell.set(value)
            
    member this.foreground 
        with get() = foregroundCell.value
        and set(value) =
            prevForegroundCell.set(this.foreground)
            foregroundCell.set(value) 
            
    member this.bounds = this.window.bounds

    member this.setPlacement(placement) =
        let moved = location <> placement.bounds.location
        let heightChanged = this.size.height<>placement.bounds.height
        let previousRender = renderedFrames
        location <- placement.bounds.location
        this.withUpdate(fun () ->
            showInsideCell.set(placement.showInside)
            sizeCell.set(placement.bounds.size)
            if heightChanged then this.ensureSelectedVisible())
        // A size/direction change already uploaded fresh pixels at the new
        // position. Otherwise keep the existing strip/shadow surfaces.
        if moved && this.visible && renderedFrames>0L && renderedFrames=previousRender then
            this.move()
     
    member this.dimmed
        with get() = dimmedCell.value
        and set(value) = dimmedCell.set(value)

    member this.visible 
        with get() = visibleCell.value
        and set(value) =
            // Windows may hide an owned popup independently of our desired state.
            // Keep the equal-value fast path, but restore externally hidden strips.
            if value && visibleCell.value && not this.window.isVisible then this.update()
            else visibleCell.set(value)
            
    member this.transparent 
        with get() = transparentCell.value
        and set(value) = transparentCell.set(value)

    member this.slide 
        with get() : (Tab * int) option = slideCell.value
        and set(value) =
            slideCell.set(value)
            this.updateDragScroll()

    member this.renderTs(top) =
        let ts = this.ts
        let ts = 
            { ts with
                zorder =    
                    match top with
                    | Some(top) -> ts.zorder.moveToEnd((=)top)
                    | None -> ts.zorder
            }
        ts.render

    member _.destroying = destroyingEvent.Publish
    member this.destroy() = 
        // A strip can be destroyed while a drag is previewing in it: that copy belongs to the strip,
        // and once the strip is gone nothing else can reach it.
        this.ghostTab <- None
        this.stopAnimations()
        nudgeTimer.Dispose()
        dragScrollTimer.Dispose()
        destroyingEvent.Trigger()
        revealTimer.Dispose()
        shadowTimer.Dispose()
        captureWatch.Dispose()
        titleTimer.Dispose()
        titleTip |> Option.iter(fun tip -> tip.Dispose())
        titleTip <- None
        dropRevealImages()
        normalFont.Dispose()
        renamedFont.Dispose()
        retiredFonts |> Seq.iter(fun font -> font.Dispose())
        retiredFonts.Clear()
        shadowWindow |> Option.iter (fun shadow -> (shadow :> IDisposable).Dispose())
        shadowWindow <- None
        eventHandlersCell.value.items.iter(fun d -> d.Dispose())
        layeredWindowCell.value.iter <| fun w -> (w :?> IDisposable).Dispose()
        this.window.destroy()
            
    member private this.hitAt(pt:Pt) =
        if this.isVertical && this.isShrunk && Rect(Pt(),this.size).containsPoint(pt) then
            zorderCell.value.tryHead.map(fun tab -> tab,TabBackground)
        else this.ts.tryHit(pt)

    member this.tryHit(pt:Pt) : Option<_> =
        this.hitAt(pt.add(this.renderedShift))

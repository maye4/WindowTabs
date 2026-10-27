// Render probe + regression check for the drag's ghost preview.
// Run: dotnet build tests/TestHost.fsproj -p:TestName=GhostRenderProbe && tests/Debug/GhostRenderProbe.exe
// Writes probe-normal.png / probe-ghost.png for eyeballing, and fails if the ghost loses its body.
#r "System.Drawing"
#r "Debug/Win32.dll"
#r "Debug/WindowTabs.exe"

open System
open System.Drawing
open System.Drawing.Imaging
open Bemo

let main () =
    let appearance = Theme.light
    let info text : TabDisplayInfo = {
        tint=None; colorStyle="Stripe"; numberBadge=None; bgColor=None; text=text; icon=SystemIcons.Application
        textFont=SystemFonts.MenuFont; textBrush=Brushes.Black; ghost=false }
    let baseStrip : TabStripSprite<int> = {
        tabs=Map2(List2([1,info "Active tab"; 2,info "Another window"]))
        lorder=List2([1;2]); zorder=List2([1;2]); size=Sz(420,28)
        slide=None; direction=TabUp; alignment=TabLeft; onlyIcons=false; held=None; centerShift=0.0; dock=TabDockTop; scrollOffset=0
        transparent=false; appearance=appearance; hover=None; captured=None }
    let out = IO.Path.Combine(__SOURCE_DIRECTORY__, "Debug")

    let save name (img:Img) =
        let canvas = new Bitmap(img.size.width, img.size.height)
        use g = Graphics.FromImage(canvas)
        g.Clear(Color.FromArgb(120,120,120))
        g.DrawImageUnscaled(img.bitmap, 0, 0)
        canvas.Save(IO.Path.Combine(out, name), ImageFormat.Png)
        canvas

    let normal = baseStrip.render
    use normalCopy = save "probe-normal.png" normal

    let ghosted = { baseStrip with tabs=Map2(List2([1,info "Active tab"; 2,{info "Another window" with ghost=true}])) }
    let ghost = ghosted.render
    use ghostCopy = save "probe-ghost.png" ghost

    // The previewed tab must still be a tab: solid body, drawn text, drawn icon — all under the mask.
    // A blank body (or a mask composed of nothing) leaves the row's background showing through, which
    // is what the user saw as "only the icon is darkened".
    let failed = ResizeArray<string>()
    let check cond message = if not cond then failed.Add message
    let tabLeft = ghosted.tabLocation 2
    let tabSize = (snd (ghosted.tabSprites.list |> List.find(fun (_,t) -> t.id=2))).size
    let midY = tabLeft.y + tabSize.height / 2
    let bodyX = tabLeft.x + tabSize.width - Dpi.scale 8
    let bodyAlpha = ghostCopy.GetPixel(bodyX, midY).A
    check (bodyAlpha = 255uy) (sprintf "The previewed tab has no body: alpha at (%d,%d) is %d" bodyX midY bodyAlpha)

    let luminance (c:Color) = int c.R + int c.G + int c.B
    let normalBody = normalCopy.GetPixel(bodyX, midY)
    let ghostBody = ghostCopy.GetPixel(bodyX, midY)
    check (luminance ghostBody < luminance normalBody)
          (sprintf "The previewed tab is not shaded: %d vs %d" (luminance ghostBody) (luminance normalBody))

    let iconX = tabLeft.x + Dpi.scale 6
    check (ghostCopy.GetPixel(iconX, midY).A > 0uy) "The previewed tab lost its icon"

    normal.bitmap.Dispose()
    ghost.bitmap.Dispose()

    if failed.Count > 0 then
        for f in failed do printfn "FAIL: %s" f
        failwith (sprintf "%d ghost-render checks failed" failed.Count)
    printfn "Ghost render checks passed (body, shading, icon)."

TestInit.run main

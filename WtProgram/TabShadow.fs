namespace Bemo

open System
open System.Drawing
open System.Drawing.Imaging
open System.Runtime.InteropServices
open Bemo.Win32.Forms

/// An exterior alpha mask, independent of tab colour and text.
module TabShadow =
    /// How far the shadow reaches past the tabs, at the current scale.
    let padding () = max 3 (Dpi.scale 15)

    let silhouette (bitmap:Bitmap) =
        // Read once: each Bitmap.Width is a GDI+ call, and asking per pixel cost 30 ms on a strip as
        // wide as a 4K screen.
        let width, height = bitmap.Width, bitmap.Height
        let rect = Rectangle(0, 0, width, height)
        let data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb)
        try
            let row = Array.zeroCreate<byte> (width * 4)
            let mask = Array.zeroCreate<byte> (width * height)
            for y in 0 .. height - 1 do
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length)
                let start = y * width
                for x in 0 .. width - 1 do
                    // The strip uses alpha=1 for otherwise empty hit-test areas.
                    let a = row.[x * 4 + 3]
                    mask.[start + x] <- if a <= 1uy then 0uy else a
            mask
        finally
            bitmap.UnlockBits(data)

    /// Two masks are the same picture. F# equality on arrays compares element by element through the
    /// generic path, which on a 4K strip took as long as building the mask.
    let sameMask (a:byte[]) (b:byte[]) =
        a.Length = b.Length &&
        (let mutable i = 0
         while i < a.Length && a.[i] = b.[i] do i <- i + 1
         i = a.Length)

    /// The columns of the mask that hold anything: a strip is as wide as its window, while the tabs
    /// often cover a fraction of it, and the shadow of an empty column is empty.
    let private occupied width height (mask:byte[]) =
        let mutable first, last = width, -1
        for y in 0 .. height - 1 do
            let row = y * width
            let mutable x = 0
            while x < first do
                if mask.[row + x] <> 0uy then first <- x
                x <- x + 1
            let mutable x = width - 1
            while x > last do
                if mask.[row + x] <> 0uy then last <- x
                x <- x - 1
        first, last

    /// float m / 255.0 for every alpha, read instead of dividing in the inner loops: the same values.
    let private alphaOf = Array.init 256 (fun m -> float m / 255.0)

    /// Scratch storage belongs to one shadow, not to each frame.
    type Renderer() =
        let mutable horizontal = Array.empty<float>
        let mutable prefix = Array.empty<int>
        let mutable pixels = Array.empty<byte>
        let mutable columnTop = Array.empty<int>
        let mutable columnBottom = Array.empty<int>
        member _.Render(width, height, mask:byte[], padding, direction) =
            let w = width + padding * 2
            let h = height + padding
            if horizontal.Length < w*h then horizontal <- Array.zeroCreate (max (w*h) (horizontal.Length*2))
            if prefix.Length < width+1 then prefix <- Array.zeroCreate (width+1)
            if pixels.Length < w*4 then pixels <- Array.zeroCreate (w*4)
            if columnTop.Length < w then
                columnTop <- Array.zeroCreate w
                columnBottom <- Array.zeroCreate w
            Array.Clear(horizontal,0,w*h)
            // Locals rather than fields: the loops below read them millions of times.
            let horizontal, prefix, pixels = horizontal, prefix, pixels
            let columnTop, columnBottom = columnTop, columnBottom
            let sigma = float padding / 3.0
            let weights = Array.init (padding * 2 + 1) (fun i ->
                let d = float (i - padding)
                exp (-d * d / (2.0 * sigma * sigma)))
            let total = Array.sum weights
            let kernel = weights |> Array.map (fun v -> v / total)
            let mutable solid = 0.0
            for weight in kernel do solid <- solid + weight
            // Only these output columns can be anything but empty: every other one is a sum of zeros.
            let first, last = occupied width height mask
            let fromX, toX = (if last < 0 then 0, -1 else first, min (w - 1) (last + padding * 2))
            // The rows of each column that hold anything after the first pass. A zero term adds nothing
            // to a sum, exactly, so the second pass reads only these and the result is unchanged.
            for x in fromX .. toX do
                columnTop.[x] <- h
                columnBottom.[x] <- -1
            for y in padding .. h - 1 do
                let sy = y-padding
                let row = if direction=TabDown then height-1-sy else sy
                prefix.[0] <- 0
                for x in 0..width-1 do prefix.[x+1] <- prefix.[x] + int mask.[row*width+x]
                for x in fromX .. toX do
                    let left,right = max 0 (x-padding*2),min width (x+1)
                    let sum = prefix.[right]-prefix.[min width left]
                    let mutable v = if sum=255*kernel.Length then solid else 0.0
                    // Long opaque or empty runs need no convolution. At edges,
                    // preserve the original kernel and summation order exactly.
                    // The hot loop: only columns inside the mask can add anything, and they add in
                    // the same order as the full convolution, so the sum is the same.
                    if sum<>0 && sum<>255*kernel.Length then
                        let start = row * width
                        for sx in max 0 (x - padding*2) .. min (width - 1) x do
                            let m = mask.[start + sx]
                            if m <> 0uy then
                                v <- v + alphaOf.[int m] * kernel.[sx - x + padding*2]
                    horizontal.[y * w + x] <- v
                    if v <> 0.0 then
                        if y < columnTop.[x] then columnTop.[x] <- y
                        if y > columnBottom.[x] then columnBottom.[x] <- y
            let bitmap = new Bitmap(w, h, PixelFormat.Format32bppArgb)
            let data = bitmap.LockBits(Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb)
            // Fade the side tails into the window junction. No bottom shadow row.
            let fadeHeight = max 1 (padding / 3)
            try
              for y in 0 .. h - 1 do
                Array.Clear(pixels,0,w*4)
                let fade = min 1.0 (float (h - 1 - y) / float fadeHeight)
                // The mask row under this output row, if there is one, for cutting the tabs out.
                let maskRow =
                    let sy = y - padding
                    if sy < 0 || sy >= height then -1
                    else (if direction=TabDown then height-1-sy else sy) * width
                if fade>0.0 then
                  for x in fromX .. toX do
                    let top = max (y - padding) columnTop.[x]
                    let bottom = min (y + padding) columnBottom.[x]
                    if top <= bottom then
                        // The helper is owned by (and above) the strip; cut the tabs out.
                        let sx = x - padding
                        let exterior =
                            if maskRow < 0 || sx < 0 || sx >= width then 1.0
                            else 1.0 - alphaOf.[int mask.[maskRow + sx]]
                        if exterior>0.0 then
                            let mutable v = 0.0
                            for sy in top .. bottom do
                                v <- v + horizontal.[sy * w + x] * kernel.[sy - y + padding]
                            pixels.[x * 4 + 3] <- byte (Math.Round(64.0 * v * exterior * fade))
                let targetY = if direction=TabDown then h-1-y else y
                Marshal.Copy(pixels, 0, IntPtr.Add(data.Scan0, targetY * data.Stride), w * 4)
            finally
                bitmap.UnlockBits(data)
            bitmap

    /// Padding exists above and beside the strip, never below it.
    let render width height mask padding = Renderer().Render(width,height,mask,padding,TabUp)

    /// Mirroring is done by row addressing, without another mask or bitmap pass.
    let renderForDirection width height (mask:byte[]) padding direction =
        Renderer().Render(width,height,mask,padding,direction)

/// An owned, nonactivating, click-through window. Ownership keeps its stacking
/// with the tab strip, without making it globally topmost or changing tab bounds.
type TabShadowWindow(os:OS, owner:IntPtr) =
    let renderer = TabShadow.Renderer()
    let mutable padding = TabShadow.padding()
    let helper =
        os.createWindow (fun msg -> msg.def()) WindowsStyles.WS_POPUP
            (WindowsExtendedStyles.WS_EX_LAYERED |||
             WindowsExtendedStyles.WS_EX_TOOLWINDOW |||
             WindowsExtendedStyles.WS_EX_NOACTIVATE |||
             WindowsExtendedStyles.WS_EX_TRANSPARENT)
    let window = os.windowFromHwnd(helper.hwnd)
    let mutable enabled = false
    let mutable direction = TabUp
    let mutable opacity = 255uy
    let mutable cached : (int * int * byte[] * TabDirection * Bitmap) option = None
    do window.setParent(os.windowFromHwnd(owner))

    member private this.locationFor direction =
        let pt = (os.windowFromHwnd(owner)).bounds.location
        Pt(pt.x - padding, pt.y - (if direction = TabUp then padding else 0))

    member private this.location = this.locationFor direction

    member this.sync() =
        let parent = os.windowFromHwnd(owner)
        if parent.isVisible && enabled then
            // Activation and owner changes may hide/reorder owned popups after
            // the strip was painted. Restore pixels and adjacency explicitly.
            cached |> Option.iter (fun (_, _, _, _, bitmap) ->
                Win32Helper.UpdateLayeredWindow(helper.hwnd, this.location.Point, bitmap, opacity))
            let previous = parent.prevZorder
            if previous.hwnd <> helper.hwnd then window.insertAfter(previous)
            if not window.isVisible then window.showNoActivate()
        else window.hide()

    member this.hide() =
        enabled <- false
        window.hide()

    member this.moveForDirection(newDirection:TabDirection) =
        let parent = os.windowFromHwnd(owner)
        if parent.isVisible && enabled then
            window.updateLocation(this.locationFor newDirection)
            if not window.isVisible then window.showNoActivate()
        else window.hide()

    member this.move() = this.moveForDirection(direction)

    member this.update(image:Img, alpha:byte, newDirection:TabDirection) =
        let nextPadding = TabShadow.padding()
        if nextPadding <> padding then
            cached |> Option.iter(fun (_,_,_,_,bitmap) -> bitmap.Dispose())
            cached <- None
            padding <- nextPadding
        let mask = TabShadow.silhouette image.bitmap
        let bitmap =
            match cached with
            | Some(w, h, previous, previousDirection, bitmap) when w = image.width && h = image.height && previousDirection = newDirection && TabShadow.sameMask previous mask -> bitmap
            | previous ->
                let bitmap = renderer.Render(image.width,image.height,mask,padding,newDirection)
                previous |> Option.iter (fun (_, _, _, _, old) -> old.Dispose())
                cached <- Some(image.width, image.height, mask, newDirection, bitmap)
                bitmap
        direction <- newDirection
        opacity <- alpha
        enabled <- true
        // Use the cached bitmap directly; Window.update makes another bitmap.
        Win32Helper.UpdateLayeredWindow(helper.hwnd, this.location.Point, bitmap, alpha)
        window.showNoActivate()

    interface IDisposable with
        member this.Dispose() =
            (helper :?> IDisposable).Dispose()
            cached |> Option.iter (fun (_, _, _, _, bitmap) -> bitmap.Dispose())
            cached <- None

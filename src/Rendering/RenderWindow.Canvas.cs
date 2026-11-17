namespace WinGLSLScrSaver;

// Partial of RenderWindow: shared-canvas draw/present path (CanvasDraw,
// Present, PresentCore, UpdateFpsLabel, EnsureRawTarget, EnsureLaneTarget).

using System.Diagnostics;
using System.Numerics;
using Pie;
using Pie.ShaderCompiler;
using DXGI = Vortice.DXGI;

internal partial class RenderWindow
{
    // ---- Shared canvas path (normal runs, diag=0) -------------------------

    // Raw window (windows 1..N in a normal run): no Pie device of its own -
    // a plain DXGI swapchain on the owner's shared device, blitting its
    // slice of the owner's canvas.
    public bool InitRawGraphics(RenderWindow owner)
    {
        try
        {
            _isRawWindow = true;
            CanvasBlit.EnsureShared(owner);
            Log.W($"Raw window initialized: bounds={Bounds} (shared device, canvas slice blit)");
            return true;
        }
        catch (Exception ex)
        {
            Log.Ex("InitRawGraphics", ex);
            return false;
        }
    }

    // One normal-run frame, DRAW phase (presents happen later, after every
    // window has drawn - nothing sits behind a flip in the GPU queue, which
    // would stall up to a whole vblank). Pass B blits are gated to this
    // monitor's own refresh rate.
    private bool _canvasWillPresent;

    private void CanvasDraw(float time)
    {
        int w = ClientSize.Width, h = ClientSize.Height;
        _canvasWillPresent = false;
        if (w <= 0 || h <= 0) return;

        if (IsCanvasOwner && _device != null)
        {
            // Pass A: shader renders ONCE PER LOOP TICK (not per present), so
            // a slower monitor always blits a fresh canvas.
            CanvasBlit.BeginFrame();
            _device.SetFramebuffer(_fb);
            _device.Viewport = new Rectangle(0, 0, _rtW, _rtH);
            _device.UpdateBuffer(_uniform, 0, new Vector4(_rtW, _rtH, 1f, time));
            _device.SetShader(_mainShader);
            _device.SetUniformBuffer(0u, _uniform);
            _device.SetDepthStencilState(_depthDisabled);
            _device.SetPrimitiveType(PrimitiveType.TriangleList);
            _device.SetInputLayout(_mainLayout);
            _device.SetVertexBuffer(0u, _quadVb, 8u);
            _device.SetIndexBuffer(_indexBuffer, IndexType.UInt);
            _device.DrawIndexed(6u);
        }

        // Pass B gating: draw/present only when this monitor is due by its
        // own refresh rate - mixed-refresh setups never run at the slowest rate.
        if (PresentGating && time < _nextPresentDue)
        {
            UpdateFpsLabel(time);
            return;
        }
        _nextPresentDue = Math.Max(_nextPresentDue + 1.0 / RefreshHz, time);
        _canvasWillPresent = true;

        if (IsCanvasOwner && _device != null)
        {
            // Pass B: this window's slice blit through Pie itself (framebuffer
            // null = Pie's own backbuffer). Never cache an RTV over the
            // swapchain - the backbuffer is recreated on resize.
            _device.SetFramebuffer(null);
            _device.Viewport = new Rectangle(0, 0, w, h);
            _device.SetShader(_blitShader);
            _device.SetTexture(0u, _rt, _sampler);
            _device.SetTexture(1u, _rt, _pointSampler);
            _device.SetDepthStencilState(_depthDisabled);
            _device.SetPrimitiveType(PrimitiveType.TriangleList);
            _device.SetInputLayout(_blitLayout);
            _device.SetVertexBuffer(0u, _blitVb, 16u);
            _device.SetIndexBuffer(_indexBuffer, IndexType.UInt);
            _device.DrawIndexed(6u);
            return;
        }

        if (UseLane)
        {
            // Lane path: device1 only copies the canvas into the shared
            // keyed-mutex texture; blit + present run on the present thread
            // on the lane device - fully parallel.
            EnsureLaneTarget();
            if (_laneSwap != null)
            {
                CanvasBlit.LaneCopy();
                return;
            }
            UseLane = false; // lane init failed - fall through to the raw path
        }

        EnsureRawTarget();
        // Frame-latency wait (DXGI 1.3 model): wait BEFORE drawing into the
        // back buffer. ~0ms normally; only a full queue makes us wait here.
        if (_frameLatencyWaitable != IntPtr.Zero)
        {
            long w0 = Stopwatch.GetTimestamp();
            Native.WaitForSingleObject(_frameLatencyWaitable, 1000);
            double wMs = (Stopwatch.GetTimestamp() - w0) * 1000.0 / Stopwatch.Frequency;
            FrameLatencyWaitTotalMs += wMs;
            FrameLatencyWaitCount++;
            if (wMs > FrameLatencyWaitMaxMs) FrameLatencyWaitMaxMs = wMs;
        }
        CanvasBlit.Draw(this);
    }

    // One normal-run frame, PRESENT phase (after ALL windows have drawn).
    // With present_threads=1 the non-owner path hands the flip to this
    // window's present thread instead of blocking the loop here.
    public void Present(float time)
    {
        if (_disposed || !_canvasWillPresent) return;
        _canvasWillPresent = false;

        if (UsePresentThread && !IsCanvasOwner)
        {
            RequestPresent(time);
            UpdateFpsLabel(time);
            return;
        }
        PresentCore(time);
        UpdateFpsLabel(time);
    }

    // The actual present calls, safe to run on the present thread: only
    // swapchain Present calls + static timing counters - no device-context
    // access, no WinForms control access.
    private void PresentCore(float time)
    {
        if (IsCanvasOwner && _device != null)
        {
            long pO = Stopwatch.GetTimestamp();
            _device.Present(PresentInterval);
            double pMsO = (Stopwatch.GetTimestamp() - pO) * 1000.0 / Stopwatch.Frequency;
            OwnerPresentTotalMs += pMsO;
            if (pMsO < OwnerPresentMinMs) OwnerPresentMinMs = pMsO;
            if (pMsO > OwnerPresentMaxMs) OwnerPresentMaxMs = pMsO;
            PresentFrames++;
            return;
        }

        // DO_NOT_WAIT (never stall the loop): a not-ready swapchain returns
        // WAS_STILL_DRAWING and the frame is skipped. Default mode 3 never
        // tears; modes 1/2 add ALLOW_TEARING (with SyncInterval 0 they tear
        // visibly on non-primary monitors). The fps label or tear-fix child
        // window over the flip swapchain forces DWM-composed presentation,
        // which masks that tearing. Blt-model swapchains support neither
        // flag - present with flags=0 there (windowed blt cannot tear).
        int presentFlags = !RawBlt && PresentGating ? RawPresentFlagMode switch
        {
            1 => DXGI_PRESENT_ALLOW_TEARING,
            2 => DXGI_PRESENT_DO_NOT_WAIT | DXGI_PRESENT_ALLOW_TEARING,
            3 => DXGI_PRESENT_DO_NOT_WAIT,
            _ => 0,
        } : 0;

        // Mark the swapchain busy for the main thread (Draw / resize must
        // not touch it concurrently - see _presentInFlight).
        _presentInFlight = true;
        try
        {
            DXGI.IDXGISwapChain1 swap;
            if (_laneSwap != null)
            {
                // Lane present: LaneDraw returns false when device1 still
                // holds the keyed mutex - skip this tick (one stale frame).
                if (_laneWaitable != IntPtr.Zero)
                    Native.WaitForSingleObject(_laneWaitable, 1000);
                if (!CanvasBlit.LaneDraw(this))
                    return;
                swap = _laneSwap;
            }
            else
            {
                swap = _rawSwap;
                if (swap == null) return; // not drawn yet (lane fallback frame)
            }

            long pR = Stopwatch.GetTimestamp();
            var pres = swap.Present(PresentInterval, (DXGI.PresentFlags) presentFlags);
            double pMsR = (Stopwatch.GetTimestamp() - pR) * 1000.0 / Stopwatch.Frequency;
            RawPresentTotalMs += pMsR;
            if (pMsR < RawPresentMinMs) RawPresentMinMs = pMsR;
            if (pMsR > RawPresentMaxMs) RawPresentMaxMs = pMsR;
            if (pres.Code == DXGI_ERROR_WAS_STILL_DRAWING)
            {
                RawWasStillCount++;
                if (!_dwnLogged)
                {
                    _dwnLogged = true;
                    Log.W("Raw present returned WasStillDrawing (DWM busy) - frame skipped");
                }
            }
            else
            {
                PresentFrames++;
            }
        }
        finally { _presentInFlight = false; }
    }

    // show_fps=1 overlay: creates the label on first use, then updates it at
    // most every 0.5s. Counts real presents (PresentFrames).
    // tear_fix_child=1 with show_fps off: instead of the label, create the
    // invisible child window once (same DWM-composition tearing fix).
    private void UpdateFpsLabel(double time)
    {
        if (_tearFixChild == null && Program.Cfg.TearFixChild && !Program.Cfg.ShowFps && IsHandleCreated)
        {
            try
            {
                var child = new Control
                {
                    Location = new Point(0, 0),
                    Size = new Size(64, 24),
                    Enabled = false, // never takes mouse input - input-exit keeps working
                };
                Controls.Add(child);
                child.BringToFront();
                // Layered + hit-test transparent + alpha 0 = fully invisible,
                // while remaining a child HWND that clips the swapchain
                // (layered child windows need Win8+).
                IntPtr h = child.Handle;
                int ex = (int) Native.GetWindowLong(h, Native.GWL_EXSTYLE);
                Native.SetWindowLong(h, Native.GWL_EXSTYLE,
                    (IntPtr) (ex | Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT));
                Native.SetLayeredWindowAttributes(h, 0, 0, Native.LWA_ALPHA);
                _tearFixChild = child;
                Log.W($"Tear-fix: transparent child window created over the flip swapchain " +
                      $"(show_fps off, hwnd=0x{h.ToInt64():X})");
            }
            catch (Exception ex) { Log.Ex("Tear-fix child window", ex); }
        }

        if (_fpsLabel == null)
        {
            if (!Program.Cfg.ShowFps || !IsHandleCreated) return;
            _fpsLabel = new Label
            {
                BackColor = Color.FromArgb(170, 0, 0, 0),
                ForeColor = Color.Lime,
                Font = new Font("Consolas", 14f, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(16, 12),
                Padding = new Padding(6, 3, 6, 3),
            };
            Controls.Add(_fpsLabel);
            _fpsLabel.BringToFront();
            _fpsLabelT = time;
        }

        double dt = time - _fpsLabelT;
        if (dt < 0.5) return;
        double fps = (PresentFrames - _fpsLabelFrames) / dt;
        _fpsLabel.Text = $"{fps:F1} fps";
        _fpsLabelFrames = PresentFrames;
        _fpsLabelT = time;
    }

    // Raw-window swapchain + slice VB management (lazy, after the window
    // settles). RawSwapBlt: 0 = flip model (default), 1 = blt 2 buffers,
    // 2 = blt 1 buffer. Stored once - ResizeBuffers must repeat the same
    // effect/count/flags as creation.
    internal bool RawBlt;
    internal int RawBltBuffers;

    // Waits up to timeoutMs for the present thread to leave this window's
    // swapchain. Returns true when STILL busy - the caller must skip this
    // tick instead of touching the swapchain concurrently (UB).
    internal bool WaitForPresentIdle(int timeoutMs)
    {
        var swIdle = System.Diagnostics.Stopwatch.StartNew();
        while (PresentInFlight && swIdle.ElapsedMilliseconds < timeoutMs)
            System.Threading.Thread.Yield();
        return PresentInFlight;
    }

    private void EnsureRawTarget()
    {
        if (!_isRawWindow) return;

        int w = ClientSize.Width, h = ClientSize.Height;
        if (_rawVb != null && _rawBuiltW == w && _rawBuiltH == h) return;

        if (_rawSwap == null)
        {
            RawBlt = Program.Cfg.RawSwapBlt != 0;
            RawBltBuffers = Program.Cfg.RawSwapBlt == 2 ? 1 : 2;
            _rawSwap = CanvasBlit.CreateSwapchain(Handle, w, h,
                out _frameLatencyWaitable, RawBlt ? RawBltBuffers : Program.Cfg.SwapBuffers,
                Program.Cfg.MaxLatency, null, RawBlt);
            Log.W($"Raw swapchain created ({(RawBlt ? $"blt, {RawBltBuffers} buffers" : $"flip, {Program.Cfg.SwapBuffers} buffers, latency {Program.Cfg.MaxLatency}")}, waitable={(_frameLatencyWaitable != IntPtr.Zero)}): {w}x{h}");
            if (_frameLatencyWaitable != IntPtr.Zero)
            {
                // Initial wait so the first frame starts from a signaled state.
                Native.WaitForSingleObject(_frameLatencyWaitable, 1000);
            }
        }
        else if (_rawBuiltW != w || _rawBuiltH != h)
        {
            // Never ResizeBuffers while the present thread is inside Present
            // (1-in-5 AV): wait, and if still busy, retry on the next frame.
            if (WaitForPresentIdle(50)) return; // still busy - retry next frame
            // ResizeBuffers MUST repeat the creation flags and buffer count.
            _rawSwap.ResizeBuffers(RawBlt ? RawBltBuffers : Program.Cfg.SwapBuffers, w, h,
                DXGI.Format.B8G8R8A8_UNorm,
                RawBlt ? 0
                    : DXGI.SwapChainFlags.AllowTearing | DXGI.SwapChainFlags.FrameLatencyWaitableObject);
            Log.W($"Raw swapchain resized: {w}x{h}");
        }

        _rawVb?.Dispose();
        _rawVb = CanvasBlit.CreateVb(ComputeCanvasVerts());
        _rawBuiltW = w;
        _rawBuiltH = h;
    }

    // Lane-window equivalent of EnsureRawTarget: a swapchain on the LANE
    // device (device2) plus this window's slice vertex buffer there. The
    // shared canvas texture is created once by CanvasBlit.EnsureLaneDevice.
    private void EnsureLaneTarget()
    {
        int w = ClientSize.Width, h = ClientSize.Height;
        if (_laneVb != null && _laneSwap != null && _laneBuiltW == w && _laneBuiltH == h) return;

        if (_laneSwap == null)
        {
            if (!CanvasBlit.EnsureLaneDevice(this))
            {
                // Lane init failed: disable lane AND present thread - two
                // threads presenting one device's swapchains AVs (measured
                // 9/10 launches in the stress harness). Main thread presents
                // the raw swapchain itself (sequential, safe).
                UseLane = false;
                UsePresentThread = false;
                ShutdownPresentThread(200);
                return;
            }
            var newLaneSwap = CanvasBlit.CreateSwapchain(Handle, w, h,
                out _laneWaitable, Program.Cfg.SwapBuffers, Program.Cfg.MaxLatency,
                CanvasBlit.LaneDev);
            Log.W($"Lane swapchain created (device2, flip, {Program.Cfg.SwapBuffers} buffers, latency {Program.Cfg.MaxLatency}, waitable={(_laneWaitable != IntPtr.Zero)}): {w}x{h}");
            if (_laneWaitable != IntPtr.Zero)
                Native.WaitForSingleObject(_laneWaitable, 1000);
            // Publish LAST: the present thread polls _laneSwap and immediately
            // calls LaneDraw (which uses _laneVb) - it must never observe a
            // published swapchain whose vertex buffer does not exist yet.
            _laneVb = CanvasBlit.LaneCreateVb(ComputeCanvasVerts());
            _laneSwap = newLaneSwap;
        }
        else if (_laneBuiltW != w || _laneBuiltH != h)
        {
            // Never ResizeBuffers while the present thread is inside Present
            // on the lane swapchain (same UB as the raw swapchain).
            if (WaitForPresentIdle(50)) return; // still busy - retry next frame
            _laneSwap.ResizeBuffers(Program.Cfg.SwapBuffers, w, h, DXGI.Format.B8G8R8A8_UNorm,
                DXGI.SwapChainFlags.AllowTearing | DXGI.SwapChainFlags.FrameLatencyWaitableObject);
            Log.W($"Lane swapchain resized: {w}x{h}");
            _laneVb?.Dispose();
            _laneVb = CanvasBlit.LaneCreateVb(ComputeCanvasVerts());
        }

        _laneBuiltW = w;
        _laneBuiltH = h;
    }

    // Maps this screen's share of the shared canvas: the canvas (master size)
    // is scaled uniformly to COVER the group bbox (centered, overflow
    // cropped) and the screen samples the canvas window it shows.
    private VPosUv[] ComputeCanvasVerts()
    {
        float w = ClientSize.Width, h = ClientSize.Height;
        float mx = _offsetInGroup.X, my = _offsetInGroup.Y;
        float gw = _groupBbox.Width, gh = _groupBbox.Height;

        float scale = Math.Max(gw / _master.Width, gh / _master.Height); // cover
        float dw = _master.Width * scale, dh = _master.Height * scale;
        float ox = (gw - dw) / 2f, oy = (gh - dh) / 2f;

        float ix0 = Math.Max(ox, mx), iy0 = Math.Max(oy, my);
        float ix1 = Math.Min(ox + dw, mx + w), iy1 = Math.Min(oy + dh, my + h);
        if (!(ix0 < ix1 && iy0 < iy1))
        {
            _sliceEmpty = true;
            return new VPosUv[4]
            {
                new(new Vector2(), new Vector2()), new(new Vector2(), new Vector2()),
                new(new Vector2(), new Vector2()), new(new Vector2(), new Vector2()),
            };
        }
        _sliceEmpty = false;

        float u0 = (ix0 - ox) / dw, v0 = (iy0 - oy) / dh;
        float u1 = (ix1 - ox) / dw, v1 = (iy1 - oy) / dh;

        float px = ix0 - mx, py = iy0 - my;
        float pw = ix1 - ix0, ph = iy1 - iy0;
        float cx0 = px / w * 2f - 1f, cx1 = (px + pw) / w * 2f - 1f;
        float cy0 = 1f - py / h * 2f, cy1 = 1f - (py + ph) / h * 2f;

        return new VPosUv[4]
        {
            new(new Vector2(cx1, cy0), new Vector2(u1, v0)), // top-right
            new(new Vector2(cx1, cy1), new Vector2(u1, v1)), // bottom-right
            new(new Vector2(cx0, cy1), new Vector2(u0, v1)), // bottom-left
            new(new Vector2(cx0, cy0), new Vector2(u0, v0)), // top-left
        };
    }

    // Blit quad vertices for the current client size and letterbox layout.
    // Winding MUST be clockwise (D3D11 default rasterizer front face); top of
    // the quad samples v0 (top of the RT) - no vertical flip. Clip space +Y up.
    private VPosUv[] ComputeBlitVerts()
    {
        float w = ClientSize.Width, h = ClientSize.Height;
        float mx = _offsetInGroup.X, my = _offsetInGroup.Y;

        float ix0 = Math.Max(_letterbox.X, mx);
        float iy0 = Math.Max(_letterbox.Y, my);
        float ix1 = Math.Min(_letterbox.X + _fitted.Width, mx + w);
        float iy1 = Math.Min(_letterbox.Y + _fitted.Height, my + h);

        if (!(ix0 < ix1 && iy0 < iy1))
            ix0 = iy0 = ix1 = iy1 = 0; // no visible slice; Pass B skips the draw

        float px = ix0 - mx, py = iy0 - my;
        float pw = ix1 - ix0, ph = iy1 - iy0;

        float u0 = (ix0 - _letterbox.X) / _fitted.Width, v0 = (iy0 - _letterbox.Y) / _fitted.Height;
        float u1 = (ix1 - _letterbox.X) / _fitted.Width, v1 = (iy1 - _letterbox.Y) / _fitted.Height;

        float cx0 = px / w * 2f - 1f, cx1 = (px + pw) / w * 2f - 1f;
        float cy0 = 1f - py / h * 2f, cy1 = 1f - (py + ph) / h * 2f;

        return new VPosUv[4]
        {
            new(new Vector2(cx1, cy0), new Vector2(u1, v0)), // top-right
            new(new Vector2(cx1, cy1), new Vector2(u1, v1)), // bottom-right
            new(new Vector2(cx0, cy1), new Vector2(u0, v1)), // bottom-left
            new(new Vector2(cx0, cy0), new Vector2(u0, v0)), // top-left
        };
    }

}

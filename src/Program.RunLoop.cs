namespace WinGLSLScrSaver;

// Partial of Program: the render loop, fps pacing (grid/rel/pi), per-window
// present gating and FPS/diag stats. Entry point lives in Program.cs.

using System.Diagnostics;

internal partial class Program
{
    private static void RunLoop(List<RenderWindow> windows, int targetFps, int fpsCap = 0)
    {
        // Init graphics BEFORE showing: device binds to the final handle and
        // the first frame is ready when the window becomes visible (no white flash).
        int okCount = 0;
        if (Program.CanvasPipeline && windows.Count > 0)
        {
            // Window 0 hosts the only device and renders the canvas; the rest
            // are raw DXGI swapchain windows blitting their slice.
            windows[0].IsCanvasOwner = true;
            if (windows[0].InitGraphics(VertexShaderStatic, FragmentShaderStatic))
            {
                okCount++;
                for (int i = 1; i < windows.Count; i++)
                    if (windows[i].InitRawGraphics(windows[0]))
                        okCount++;
            }
        }
        else
        {
            // Diag modes keep per-window devices (pixel readbacks etc.).
            foreach (var w in windows)
                if (w.InitGraphics(VertexShaderStatic, FragmentShaderStatic))
                    okCount++;
        }
        Log.W($"Graphics initialized on {okCount}/{windows.Count} windows");

        if (okCount == 0)
        {
            Log.W("ABORT: no window initialized graphics");
            _running = false;
        }

        foreach (var w in windows) w.Show();
        if (windows.Count > 0 && !windows[0].IsPreview)
            windows[0].Activate();
        // Fullscreen canvas runs: loop paced by timer, presents never block,
        // each monitor gates its own presents to its own refresh rate.
        bool canvasMode = Program.CanvasPipeline
            && windows.Count > 0 && !windows[0].IsPreview;
        if (canvasMode)
            foreach (var w in windows) w.PresentGating = true;
        int presentInterval = canvasMode || fpsCap > 0 ? 0 : 1;
        foreach (var w in windows) w.PresentInterval = presentInterval;
        Log.W($"All windows shown (presentInterval={presentInterval}, gating={canvasMode})");

        // Present threads + lanes: each non-owner window presents from its own
        // thread and (lanes=2) its own D3D11 device, because Present calls on
        // ONE device serialize at DWM composition granularity (~5.9ms each).
        // The owner stays on the main thread - the Pie device context is not
        // thread-safe and its present is cheap.
        bool lanes = canvasMode && Cfg.PresentLanes == 2;
        bool usePresentThreads = canvasMode && (Cfg.PresentThreads || lanes);
        if (usePresentThreads)
            foreach (var w in windows)
                if (!w.IsCanvasOwner)
                {
                    w.UseLane = lanes;
                    w.UsePresentThread = true;
                    w.StartPresentThread();
                }
        Log.W($"Present threads: {(usePresentThreads ? "on (non-owner windows)" : "off")}; lanes: {(lanes ? "2 (per-window device)" : "1")}");

        var sw = Stopwatch.StartNew();
        var frame = TimeSpan.FromMilliseconds(1000.0 / targetFps);
        // Timer-paced modes: any FPS cap, and fullscreen canvas runs. Sleep(1)
        // is unreliable here - since Win10 2004 raised timer resolution only
        // applies to the FOCUSED process, and a screensaver never has focus.
        bool timerPaced = canvasMode || fpsCap > 0;
        int paceFps = fpsCap > 0 ? fpsCap : targetFps;
        var capFrame = timerPaced ? TimeSpan.FromMilliseconds(1000.0 / paceFps) : TimeSpan.Zero;
        string paceMode = Cfg.FpsPace;
        double capMs = capFrame.TotalMilliseconds;
        double nextDueMs = sw.Elapsed.TotalMilliseconds;
        double lastStartMs = 0, lastIntervalMs = 0, piInteg = 0;
        Log.W($"Pacing: {paceMode} @ {paceFps} fps (timerPaced={timerPaced}, cap={fpsCap})");
        double diagSeconds = Program.DiagMode == 7 ? 4.0
            : Program.DiagMode > 0 ? 6.0 : double.PositiveInfinity;
        long frames = 0;

        // Real-fps sampling: every ~2s log how many frames each window
        // actually handed to DWM (post gating).
        double fpsLogT = 0;
        long fpsLogFrames = 0;
        var fpsLogPresents = new long[windows.Count];
        long winFrames = 0;
        double renderMsSum = 0, waitMsSum = 0, preMsSum = 0;
        double renderMsMin = double.MaxValue, renderMsMax = 0;
        double waitMsMax = 0;

        while (_running)
        {
            Application.DoEvents();
            if (!_running) break;

            double pre0 = sw.Elapsed.TotalMilliseconds;
            double nowMs = sw.Elapsed.TotalMilliseconds;

            if (sw.Elapsed.TotalSeconds > diagSeconds)
            {
                Log.W("DIAG auto-exit reached");
                break;
            }

            if (windows.Any(w => w.ShutdownRequested))
            {
                Log.W("Exit: input shutdown requested");
                break;
            }
            if (windows.All(w => w.IsDisposed))
            {
                Log.W("Exit: all windows closed");
                break;
            }

            float t = (float) sw.Elapsed.TotalSeconds;
            double f0 = sw.Elapsed.TotalMilliseconds;
            preMsSum += f0 - pre0;
            foreach (var w in windows)
                w.Render(t);
            // Present phase: canvas runs AND preview present here, AFTER all
            // windows have drawn - nothing sits behind a flip in the GPU queue.
            // Diag 1-6 present inside Render(); Present() no-ops for them.
            foreach (var w in windows)
                w.Present(t);
            double rMs = sw.Elapsed.TotalMilliseconds - f0;
            renderMsSum += rMs;
            if (rMs < renderMsMin) renderMsMin = rMs;
            if (rMs > renderMsMax) renderMsMax = rMs;
            winFrames++;

            frames++;
            if (frames == 1) Log.W("First frame rendered");
            if (frames % 600 == 0) Log.W($"Heartbeat: {frames} frames, t={t:F1}s");

            // Every ~2s: per-window present fps + where the frame budget went.
            if (canvasMode && t - fpsLogT >= 2.0)
            {
                double dt = t - fpsLogT;
                var parts = new List<string>();
                for (int i = 0; i < windows.Count; i++)
                {
                    double f = (windows[i].PresentFrames - fpsLogPresents[i]) / dt;
                    parts.Add($"{i}:{f:F1}");
                    fpsLogPresents[i] = windows[i].PresentFrames;
                }
                double nf = Math.Max(1, winFrames);
                Log.W($"FPS: loop={(frames - fpsLogFrames) / dt:F1} presents=[{string.Join(' ', parts)}] " +
                      $"pre={preMsSum / nf:F2} render={renderMsSum / nf:F2}ms [min {renderMsMin:F2} max {renderMsMax:F2}] " +
                      $"wait={waitMsSum / nf:F2}ms [max {waitMsMax:F2}] " +
                      $"ownerPresent={RenderWindow.OwnerPresentTotalMs / nf:F2}ms [{RenderWindow.OwnerPresentMinMs:F2}..{RenderWindow.OwnerPresentMaxMs:F2}] " +
                      $"rawPresent={RenderWindow.RawPresentTotalMs / nf:F2}ms [{RenderWindow.RawPresentMinMs:F2}..{RenderWindow.RawPresentMaxMs:F2}] " +
                      $"wasStill={RenderWindow.RawWasStillCount} " +
                      $"waitable={RenderWindow.FrameLatencyWaitTotalMs / Math.Max(1, RenderWindow.FrameLatencyWaitCount):F2}ms avg [{RenderWindow.FrameLatencyWaitMaxMs:F2} max] n={RenderWindow.FrameLatencyWaitCount}");
                fpsLogT = t;
                fpsLogFrames = frames;
                winFrames = 0; renderMsSum = 0; waitMsSum = 0; preMsSum = 0;
                renderMsMin = double.MaxValue; renderMsMax = 0; waitMsMax = 0;
                RenderWindow.OwnerPresentTotalMs = 0;
                RenderWindow.RawPresentTotalMs = 0;
                RenderWindow.RawWasStillCount = 0;
                RenderWindow.FrameLatencyWaitTotalMs = 0;
                RenderWindow.FrameLatencyWaitMaxMs = 0;
                RenderWindow.FrameLatencyWaitCount = 0;
                RenderWindow.RawPresentMinMs = double.MaxValue;
                RenderWindow.RawPresentMaxMs = 0;
                RenderWindow.OwnerPresentMinMs = double.MaxValue;
                RenderWindow.OwnerPresentMaxMs = 0;
            }

            // Frame-interval measurement (start-to-start) for the PI trim.
            if (lastStartMs > 0) lastIntervalMs = nowMs - lastStartMs;
            lastStartMs = nowMs;

            if (timerPaced)
            {
                if (paceMode == "grid")
                {
                    // Old absolute-grid pacing (kept for A/B comparison): waits
                    // for the next ceil-quantized grid slot; phase jitter can
                    // push frames into the next slot (measured ~80 fps @ 120 cap).
                    var elapsed = sw.Elapsed;
                    var nextFrame = TimeSpan.FromTicks((long) Math.Ceiling(elapsed.Ticks / (double) capFrame.Ticks) * capFrame.Ticks);
                    var remain = nextFrame - elapsed;
                    if (remain > TimeSpan.Zero)
                    {
                        var until = DateTime.UtcNow + remain;
                        while (DateTime.UtcNow < until && _running)
                        {
                            Application.DoEvents();
                            var left = until - DateTime.UtcNow;
                            if (left > TimeSpan.FromMilliseconds(2)) Native.SleepHighRes(left);
                        }
                    }
                }
                else
                {
                    // Relative pacer (rel/pi): next frame due exactly one cap
                    // interval after the previous - no grid aliasing. Overrun
                    // reset: never burst to catch up after a stall.
                    nextDueMs += capMs;
                    if (nowMs - nextDueMs > 0) nextDueMs = nowMs;
                    double remainMs = nextDueMs - nowMs;
                    if (paceMode == "pi")
                    {
                        // PI trim: proportional + integral correction of the
                        // wait from the measured frame-interval error.
                        double err = lastIntervalMs - capMs; // + = too slow
                        piInteg = Math.Clamp(piInteg + err, -capMs, capMs);
                        double corr = Math.Clamp(0.30 * err + 0.15 * piInteg, -1.5, 1.5);
                        remainMs -= corr;
                    }
                    if (remainMs > 0)
                    {
                        var until = DateTime.UtcNow + TimeSpan.FromMilliseconds(remainMs);
                        while (DateTime.UtcNow < until && _running)
                        {
                            Application.DoEvents();
                            var left = until - DateTime.UtcNow;
                            if (left > TimeSpan.FromMilliseconds(2)) Native.SleepHighRes(left);
                        }
                    }
                }
            }
            else
            {
                var elapsed = sw.Elapsed;
                var nextFrame = TimeSpan.FromTicks((long) Math.Ceiling(elapsed.Ticks / (double) frame.Ticks) * frame.Ticks);
                var remain = nextFrame - elapsed;
                while (remain > TimeSpan.Zero && _running)
                {
                    if (remain > TimeSpan.FromMilliseconds(2))
                        Thread.Sleep(1);
                    else
                        Thread.SpinWait(50);
                    elapsed = sw.Elapsed;
                    remain = nextFrame - elapsed;
                }
            }
            double waitMs = sw.Elapsed.TotalMilliseconds - f0 - rMs;
            waitMsSum += waitMs;
            if (waitMs > waitMsMax) waitMsMax = waitMs;
        }

        foreach (var w in windows)
            w.ShutdownPresentThread(500);

        Log.W($"Render loop ended after {frames} frames");
        foreach (var w in windows)
        {
            try { w.Close(); } catch { }
        }
        Log.W("All windows closed, exiting");
    }

}

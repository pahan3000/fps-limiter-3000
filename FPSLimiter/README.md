# FPS Limiter - scroll lag fix

Drop `FPSLimiter.cs` and `LimiterService.cs` over the ones in the project (or `git apply scroll-lag-fix.patch`), rebuild, repack.

1. `OnGameSelected` no longer probes Special K on the UI thread. It was a blocking
   `UIDispatcher.Invoke` that ran 7x `Process.GetProcessesByName` (plus folder checks) on every selection
   step. Now: 150 ms debounce, probing on a worker thread, UI thread only flips icon visibility.
2. `UpdateRunningGameMonitors` (3 s UI-thread timer) returns immediately when no game is running instead of
   enumerating displays + VRR detection every tick.

Not compiled here (no C# toolchain in my sandbox) - build before shipping.

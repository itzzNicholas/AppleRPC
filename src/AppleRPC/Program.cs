// SPDX-License-Identifier: AGPL-3.0-only
using System.Reflection;
using AppleRPC.Artwork;
using AppleRPC.Configuration;
using AppleRPC.Logging;
using AppleRPC.Presence;

namespace AppleRPC;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        // Single instance. "Local\" = one per Windows session/user, which matches Discord's per-user pipe.
        using var mutex = new Mutex(initiallyOwned: true, @"Local\AppleRPC.SingleInstance.9d2f6c4e", out var isFirst);
        if (!isFirst) return 0;

        var config = Config.Load(AppPaths.ConfigFile);
        Log.Init(config.CreateLogger());
        Log.Info($"AppleRPC {Assembly.GetExecutingAssembly().GetName().Version} starting on {Environment.OSVersion}.");
        if (config.LoadError is not null) Log.Warn(config.LoadError);

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error("Unhandled UI exception.", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception.", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("Unobserved task exception.", e.Exception); e.SetObserved(); };

        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var media = new MediaWatcher(config);
        var artwork = new ArtworkService(config.Artwork, cachePath: AppPaths.ArtworkCacheFile);
        var presence = new PresenceService(config);
        var coordinator = new RpcCoordinator(config, media, artwork, presence);

        try
        {
            using var tray = new TrayApp(config, coordinator, new Strings(config.Language), new StartupManager());

            // Start off the UI thread: the first WinRT/IPC calls can take a moment.
            _ = Task.Run(async () =>
            {
                try { await coordinator.StartAsync(); }
                catch (Exception ex) { Log.Error("AppleRPC failed to start.", ex); }
            });

            Application.Run(tray);
        }
        catch (Exception ex)
        {
            Log.Error("Fatal error.", ex);
            return 1;
        }
        finally
        {
            Log.Info("Shutting down.");
            coordinator.Dispose();
            media.Dispose();
            try { presence.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(4)); } catch { /* best effort */ }
            artwork.Dispose();
            mutex.ReleaseMutex();
        }
        return 0;
    }
}

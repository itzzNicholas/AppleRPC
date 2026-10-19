// SPDX-License-Identifier: AGPL-3.0-only
using System.Diagnostics;
using AppleRPC.Configuration;
using AppleRPC.Logging;

namespace AppleRPC;

/// <summary>The whole UI: a tray icon with a small context menu. There is deliberately no window.</summary>
public sealed class TrayApp : ApplicationContext
{
    private const int MaxTooltipLength = 100;   // NotifyIcon.Text accepts up to 127 characters

    private readonly Strings _strings;
    private readonly StartupManager _startup;
    private readonly Config _config;
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _statusItem = new() { Enabled = false };
    private readonly ToolStripMenuItem _discordItem = new() { Enabled = false };
    private readonly ToolStripMenuItem _startupItem;
    private readonly Control _uiThread = new();   // only used to hop back onto the UI thread

    public TrayApp(Config config, RpcCoordinator coordinator, Strings strings, StartupManager startup)
    {
        _config = config;
        _strings = strings;
        _startup = startup;

        _ = _uiThread.Handle;   // force handle creation on this (the UI) thread

        _startupItem = new ToolStripMenuItem(strings.StartWithWindows) { Checked = startup.IsEnabled };
        _startupItem.Click += (_, _) => ToggleStartup();

        var github = new ToolStripMenuItem(strings.OpenGitHub);
        github.Click += (_, _) => OpenShell(Config.GitHubUrl);
        var openConfig = new ToolStripMenuItem(strings.OpenConfig);
        openConfig.Click += (_, _) => OpenShell(AppPaths.ConfigFile);
        var openLogs = new ToolStripMenuItem(strings.OpenLogs);
        openLogs.Click += (_, _) => { Directory.CreateDirectory(AppPaths.LogDirectory); OpenShell(AppPaths.LogDirectory); };
        var exit = new ToolStripMenuItem(strings.Exit);
        exit.Click += (_, _) => ExitThread();

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            _statusItem, _discordItem, new ToolStripSeparator(),
            _startupItem, github, openConfig, openLogs, new ToolStripSeparator(),
            exit,
        });

        _notifyIcon = new NotifyIcon
        {
            Icon = LoadIcon(),
            ContextMenuStrip = menu,
            Text = "AppleRPC",
            Visible = true,
        };

        coordinator.StatusChanged += status => RunOnUi(() => ApplyStatus(status));
        ApplyStatus(coordinator.Status);

        if (!config.HasValidApplicationId)
            _notifyIcon.ShowBalloonTip(8000, "AppleRPC", strings.NeedsSetupBalloon, ToolTipIcon.Warning);
    }

    private void ApplyStatus(AppStatus status)
    {
        var line1 = _strings.Status(status);
        var line2 = _strings.Discord(status);
        _statusItem.Text = Truncate(line1, 80);
        _discordItem.Text = Truncate(line2, 80);
        _notifyIcon.Text = Truncate($"AppleRPC — {line1}", MaxTooltipLength);
    }

    private void ToggleStartup()
    {
        var target = !_startupItem.Checked;
        if (_startup.SetEnabled(target)) _startupItem.Checked = target;
        else _notifyIcon.ShowBalloonTip(4000, "AppleRPC", _strings.StartupFailed, ToolTipIcon.Error);
    }

    private static void OpenShell(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn($"Could not open '{target}'.", ex); }
    }

    private void RunOnUi(Action action)
    {
        try
        {
            if (_uiThread.IsDisposed) return;
            if (_uiThread.InvokeRequired) _uiThread.BeginInvoke(action);
            else action();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { /* shutting down */ }
    }

    private static Icon LoadIcon()
    {
        try
        {
            using var stream = typeof(TrayApp).Assembly.GetManifestResourceStream("AppleRPC.ico");
            if (stream is not null) return new Icon(stream, SystemInformation.SmallIconSize);
        }
        catch (Exception ex) { Log.Warn("Could not load the embedded tray icon.", ex); }
        return SystemIcons.Application;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    protected override void ExitThreadCore()
    {
        _notifyIcon.Visible = false;   // otherwise the ghost icon lingers in the tray until hovered
        _notifyIcon.Dispose();
        _uiThread.Dispose();
        base.ExitThreadCore();
    }
}

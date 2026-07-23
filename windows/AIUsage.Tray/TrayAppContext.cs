using AIUsage.Core;

namespace AIUsage.Tray;

public sealed class TrayAppContext : ApplicationContext
{
    private readonly NotifyIcon _notify = new() { Visible = true };
    private readonly UsageClient _client = UsageClient.CreateDefault();
    private readonly CodexClient _codexClient = CodexClient.CreateDefault();
    private readonly CancellationTokenSource _cts = new();
    private readonly SynchronizationContext _ui;
    private TrayState _state = new TrayState.Loading();
    private CodexState _codex = new CodexState.Loading();
    private RenderedIcon? _currentIcon;
    private Task? _inFlight;

    public TrayAppContext()
    {
        // SynchronizationContext.Current is still null here (no control created, message loop
        // not started) — install the WinForms context explicitly instead of capturing null.
        if (SynchronizationContext.Current is not WindowsFormsSynchronizationContext)
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        _ui = SynchronizationContext.Current!;

        BuildMenu();
        // Unsupported private API — guard the lookup so a runtime servicing change degrades
        // left-click to a no-op (right-click still works natively) instead of crashing.
        var showMenu = typeof(NotifyIcon).GetMethod("ShowContextMenu",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        _notify.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left && showMenu != null)
                try { showMenu.Invoke(_notify, null); } catch (Exception) { }
        };
        ApplyState(_state);
        // Re-render the icon when display scale/monitor changes (DPI checklist item).
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        // Fire-and-forget is safe: PollLoopAsync handles every exception internally.
        _ = PollLoopAsync();
    }

    private void OnDisplayChanged(object? sender, EventArgs e) =>
        _ui.Post(_ => ApplyState(_state), null);

    private async Task PollLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            // Belt-and-braces: RefreshOnceAsync shouldn't throw, but a fault here would
            // silently kill the fire-and-forget loop forever.
            try { await RefreshOnceAsync(); } catch (Exception) { }
            try { await Task.Delay(TimeSpan.FromSeconds(60), _cts.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    private Task RefreshOnceAsync()
    {
        if (_inFlight is { IsCompleted: false }) return _inFlight; // single-flight: join
        _inFlight = DoRefreshAsync();
        return _inFlight;
    }

    private async Task DoRefreshAsync()
    {
        UsageSnapshot? snap; string? err;
        // Task.Run: FetchAsync's synchronous prefix (credential file read + 500ms race retry)
        // must not run on the UI thread (checkpoint-2 finding).
        try { (snap, err) = await Task.Run(() => _client.FetchAsync(_cts.Token)); }
        catch (OperationCanceledException) { return; }
        catch (Exception) { (snap, err) = (null, "unexpected error"); } // no-crash: poll loop must never fault

        CodexFetchResult codexResult;
        try { codexResult = await Task.Run(() => _codexClient.FetchAsync(_cts.Token)); }
        catch (OperationCanceledException) { return; }
        catch (Exception) { codexResult = new CodexFetchResult(null, "unexpected error", true); }

        var next = TrayStateMachine.Next(_state, snap, err);
        var nextCodex = CodexStateMachine.Next(_codex, codexResult);
        try
        {
            _ui.Post(_ =>
            {
                try { _state = next; _codex = nextCodex; ApplyState(next); BuildMenu(); }
                catch (Exception) { /* rendering failure must not kill the UI thread */ }
            }, null);
        }
        catch (Exception) { /* Post can throw during shutdown — must not fault the poll loop */ }
    }

    private void ApplyState(TrayState state)
    {
        var size = Math.Max(SystemInformation.SmallIconSize.Width, SystemInformation.SmallIconSize.Height);
        var rendered = IconRenderer.Render(state, _codex, size);
        _notify.Icon = rendered.Icon;
        _notify.Text = Display.Tooltip(state, _codex);
        _currentIcon?.Dispose();
        _currentIcon = rendered;
    }

    private void BuildMenu()
    {
        var menu = new ContextMenuStrip();
        var now = DateTimeOffset.UtcNow;

        var snapshot = _state switch
        {
            TrayState.Ok ok => ok.Snapshot,
            TrayState.Degraded d => d.Last,
            _ => null,
        };
        if (_state is TrayState.Degraded deg)
            menu.Items.Add(new ToolStripMenuItem($"⚠ {deg.Reason}") { Enabled = false });
        if (_state is TrayState.Loading)
            menu.Items.Add(new ToolStripMenuItem("Loading…") { Enabled = false });

        if (snapshot != null)
        {
            AddLimitRow(menu, "Session", snapshot.Session, now);
            AddLimitRow(menu, "Weekly", snapshot.WeeklyAll, now);
            AddLimitRow(menu, "Model", snapshot.WeeklyScoped, now);
        }

        // Codex section — hidden entirely when not logged in
        if (_codex is not CodexState.Hidden and not CodexState.Loading)
        {
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Codex") { Enabled = false });
            if (_codex is CodexState.Degraded cd)
                menu.Items.Add(new ToolStripMenuItem($"⚠ {cd.Reason}") { Enabled = false });
            foreach (var r in _codex.LastReadings ?? [])
                menu.Items.Add(new ToolStripMenuItem(
                    $"{r.Name}  {r.Percent}%" + (r.ResetsAt is { } ra
                        ? $"  ·  {Display.ResetCountdown(ra, now)}" : "")) { Enabled = false });
        }

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Refresh now", null, (_, _) => _ = RefreshOnceAsync());

        var auto = new ToolStripMenuItem("Start at login") { Checked = Autostart.IsEnabled() };
        auto.Click += (_, _) =>
        {
            var err = auto.Checked ? Autostart.Disable() : Autostart.Enable();
            if (err != null) auto.Text = $"Start at login — {err}";
            auto.Checked = Autostart.IsEnabled();
        };
        menu.Items.Add(auto);
        menu.Items.Add("Quit", null, (_, _) => ExitApp());

        var old = _notify.ContextMenuStrip;
        _notify.ContextMenuStrip = menu;
        old?.Dispose();
    }

    private static void AddLimitRow(ContextMenuStrip menu, string label, UsageLimit limit, DateTimeOffset now) =>
        menu.Items.Add(new ToolStripMenuItem(
            $"{label}  {limit.Percent}%  ·  {Display.ResetCountdown(limit.ResetsAt, now)}") { Enabled = false });

    private void ExitApp()
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        _cts.Cancel();
        _notify.Visible = false;
        _notify.Dispose();
        _currentIcon?.Dispose();
        ExitThread();
    }
}

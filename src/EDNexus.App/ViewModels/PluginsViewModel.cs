using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EDNexus.App.Services.Plugins;
using EDNexus.Plugins.Abstractions;
using EDNexus.Plugins.Hosting;

namespace EDNexus.App.ViewModels;

/// <summary>Where an installed plugin stands, as the plugin manager shows it.</summary>
public enum PluginRowState
{
    /// <summary>Loaded and receiving events.</summary>
    Running,

    /// <summary>A load, reload or unload is in progress.</summary>
    Loading,

    /// <summary>No grant matches the files on disk now (never approved, or changed since), so it does not run.</summary>
    NeedsApproval,

    /// <summary>Switched off by the commander; the choice persists.</summary>
    Disabled,

    /// <summary>Unloaded automatically after repeated errors; lasts until reloaded, enabled or the app restarts.</summary>
    Quarantined,

    /// <summary>Approved, but it could not be loaded (incompatible, <c>Initialize</c> threw, ...).</summary>
    Failed,

    /// <summary>Approved and enabled, but not loaded (the plugins have not started yet, or there is no runtime).</summary>
    NotRunning,

    /// <summary>The folder is not a loadable plugin (bad manifest, folder name is not the id, unreadable).</summary>
    Invalid,
}

/// <summary>
/// The plugin manager in Settings (#61): every folder under the plugins root, its state and last
/// error, enable / disable, consent per declared capability, revoke and reload.
/// </summary>
/// <remarks>
/// <para>
/// <b>Consent.</b> Nothing here decides consent. Listing reads each folder with
/// <see cref="PluginConsentRequest.TryRead"/> (the same manifest and hashes the host checks) and asks
/// <see cref="PluginConsent.FromGrants"/> whether a grant matches; it never records one. A grant is
/// recorded only by <see cref="PluginItemViewModel.ApproveCommand"/>, for the exact request that
/// row shows and the capabilities ticked on it, through <see cref="PluginService.Approve"/>; it is
/// withdrawn only through <see cref="PluginService.Revoke"/>, which also stops a running copy.
/// </para>
/// <para>
/// <b>Threading.</b> Every lifecycle call runs plugin code, so it runs on the thread pool, one at a
/// time. The list is rebuilt from a snapshot read off the UI thread and applied through
/// <c>postToUi</c>, like <see cref="MainWindowViewModel"/>; status changes pushed by the service
/// (a quarantine, say) are marshalled the same way.
/// </para>
/// </remarks>
public sealed partial class PluginsViewModel : ObservableObject, IDisposable
{
    private readonly PluginService _service;
    private readonly Action<Action> _postToUi;
    private readonly Action<string> _openFolder;
    private readonly SemaphoreSlim _one = new(1, 1);
    private bool _disposed;

    /// <param name="service">The app's plugin service.</param>
    /// <param name="postToUi">Runs an action on the UI thread.</param>
    /// <param name="openFolder">Opens a folder in the OS file manager.</param>
    public PluginsViewModel(PluginService service, Action<Action> postToUi, Action<string> openFolder)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(postToUi);
        ArgumentNullException.ThrowIfNull(openFolder);
        _service = service;
        _postToUi = postToUi;
        _openFolder = openFolder;
        PluginsFolder = service.PluginsRoot;
        _service.StatusChanged += OnStatusChanged;
    }

    /// <summary>One row per folder under the plugins root, in folder-name order.</summary>
    public ObservableCollection<PluginItemViewModel> Plugins { get; } = [];

    /// <summary>The plugins root the app loads from, or null when plugins are off (no per-user folder).</summary>
    public string? PluginsFolder { get; }

    /// <summary>Whether there is a plugins folder at all.</summary>
    public bool PluginsAvailable => PluginsFolder is not null;

    /// <summary>True while an action or a rescan is running; the view disables its controls.</summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>Plugins currently showing an error.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasErrors))] private int _errorCount;

    /// <summary>Whether any plugin is showing an error.</summary>
    public bool HasErrors => ErrorCount > 0;

    /// <summary>One line: how many are installed, running, waiting for approval and failing.</summary>
    [ObservableProperty] private string _summary = "";

    /// <summary>Re-reads the plugins folder (a plugin copied in while the window is open shows up) and every plugin's state.</summary>
    [RelayCommand]
    public Task RefreshAsync() => Exclusive(null);

    /// <summary>Opens <see cref="PluginsFolder"/> in the file manager, creating it first if it does not exist yet.</summary>
    [RelayCommand]
    private void OpenFolder()
    {
        if (PluginsFolder is not { } folder) return;
        try
        {
            Directory.CreateDirectory(folder);
            _openFolder(folder);
        }
        catch (Exception ex)
        {
            // Best effort, like "Open logs": a file manager that will not start must not crash Settings.
            Trace.TraceWarning($"Plugins: could not open the plugins folder: {PluginLogText.Sanitise(ex.Message)}");
        }
    }

    internal Task ApproveAsync(PluginItemViewModel row)
    {
        if (row.Request is not { } request) return Task.CompletedTask;
        // Exactly what the row shows: network is ticked and cannot be unticked (it cannot be enforced).
        var granted = row.Capabilities.Where(c => c.IsGranted).Select(c => c.Name).ToList();
        return Exclusive(() => _service.Approve(request, granted));
    }

    internal Task RevokeAsync(PluginItemViewModel row) => Exclusive(() => _service.Revoke(row.Id));

    internal Task ReloadAsync(PluginItemViewModel row) => Exclusive(() => _service.Reload(row.Id));

    internal Task ToggleEnabledAsync(PluginItemViewModel row)
    {
        var enable = !row.IsEnabled;
        return Exclusive(() => _service.SetEnabled(row.Id, enable));
    }

    /// <summary>Runs <paramref name="action"/> (if any) on the pool, then re-reads and applies the list; one at a time.</summary>
    private async Task Exclusive(Action? action)
    {
        await _one.WaitAsync();
        try
        {
            SetOnUi(() => IsBusy = true);
            if (action is not null)
            {
                try { await Task.Run(action); }
                catch (Exception ex)
                {
                    Trace.TraceWarning($"Plugins: the plugin manager's action failed: {PluginLogText.Sanitise(ex.Message)}");
                }
            }
            var snapshot = await Task.Run(ReadSnapshot);
            await OnUi(() => Apply(snapshot));
        }
        finally
        {
            SetOnUi(() => IsBusy = false);
            _one.Release();
        }
    }

    /// <summary>What one folder looks like right now. Read off the UI thread.</summary>
    internal sealed record PluginEntry(
        string Id,
        PluginConsentRequest? Request,
        string? Problem,
        PluginStatus? Status,
        bool Disabled,
        IReadOnlyCollection<string>? Approved,
        bool HasGrant,
        bool CanReload);

    private List<PluginEntry> ReadSnapshot()
    {
        var entries = new List<PluginEntry>();
        if (PluginsFolder is not { } root || !Directory.Exists(root)) return entries;
        string[] folders;
        try
        {
            folders = Directory.GetDirectories(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Plugins: could not list the plugins folder: {PluginLogText.Sanitise(ex.Message)}");
            return entries;
        }

        var consent = PluginConsent.FromGrants(_service.Grants);
        var supervisor = _service.Runtime?.Supervisor;
        foreach (var dir in folders.Order(StringComparer.Ordinal))
        {
            var folder = Path.GetFileName(dir);
            if (folder.StartsWith('.')) continue;   // installer work folders, never plugins
            PluginConsentRequest.TryRead(dir, out var request, out var problem);
            if (request is not null && !string.Equals(request.Manifest.Id, folder, StringComparison.Ordinal))
            {
                problem = $"the folder must be named exactly after the plugin id '{request.Manifest.Id}'";
                request = null;
            }
            if (problem is not null) problem = PluginLogText.Sanitise(problem);   // folder names and file errors are untrusted text
            var id = request?.Manifest.Id ?? folder;
            var status = request is null ? null : supervisor?.GetStatus(id);
            var disabled = _service.Enablement.IsDisabled(id);
            var approved = request is null ? null : consent(request);
            entries.Add(new PluginEntry(id, request, problem, status, disabled, approved,
                HasGrant: _service.Grants.Get(id) is not null,
                CanReload: supervisor is not null && request is not null && approved is not null && !disabled));
        }
        return entries;
    }

    private void Apply(List<PluginEntry> snapshot)
    {
        Plugins.Clear();
        foreach (var entry in snapshot)
            Plugins.Add(new PluginItemViewModel(this, entry));
        UpdateSummary();
    }

    private void OnStatusChanged(PluginStatus status)
        => _postToUi(() =>
        {
            if (_disposed) return;
            var row = Plugins.FirstOrDefault(p => string.Equals(p.Id, status.Id, StringComparison.Ordinal));
            if (row is null) return;
            row.ApplyStatus(status);
            UpdateSummary();
        });

    private void UpdateSummary()
    {
        ErrorCount = Plugins.Count(p => p.LastError is not null);
        var running = Plugins.Count(p => p.State == PluginRowState.Running);
        var waiting = Plugins.Count(p => p.State == PluginRowState.NeedsApproval);
        Summary = Plugins.Count == 0
            ? "No plugins installed."
            : $"{Plugins.Count} installed · {running} running · {waiting} waiting for approval · {ErrorCount} with errors";
    }

    private void SetOnUi(Action action) => _postToUi(action);

    private Task OnUi(Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _postToUi(() =>
        {
            try { action(); done.SetResult(); }
            catch (Exception ex) { done.SetException(ex); }
        });
        return done.Task;
    }

    /// <summary>Stops listening for status changes (call when the window closes).</summary>
    public void Dispose()
    {
        _disposed = true;
        _service.StatusChanged -= OnStatusChanged;
    }
}

/// <summary>One installed plugin in the manager.</summary>
public sealed partial class PluginItemViewModel : ObservableObject
{
    private readonly PluginsViewModel _owner;
    private readonly PluginsViewModel.PluginEntry _entry;

    internal PluginItemViewModel(PluginsViewModel owner, PluginsViewModel.PluginEntry entry)
    {
        _owner = owner;
        _entry = entry;
        var manifest = entry.Request?.Manifest;
        Name = manifest?.Name ?? PluginLogText.Sanitise(entry.Id, 150);
        Version = manifest?.Version ?? "";
        Author = manifest?.Author;
        Description = manifest?.Description;
        // Ticked as currently granted; with no matching grant (never approved, or changed since) the
        // plugin is offered everything it declares. Network is always ticked.
        Capabilities = (manifest?.Capabilities ?? [])
            .Select(c => new PluginCapabilityViewModel(c, entry.Approved is null || entry.Approved.Contains(c, StringComparer.Ordinal)))
            .ToList();
        NetworkWarning = manifest is null ? null : NetworkWarningFor(manifest);
        ApplyStatus(entry.Status);
    }

    /// <summary>The plugin id (the folder name when the manifest could not be read).</summary>
    public string Id => _entry.Id;

    /// <summary>The manifest's display name.</summary>
    public string Name { get; }

    /// <summary>The plugin's own version.</summary>
    public string Version { get; }

    /// <summary>The manifest's author, if any.</summary>
    public string? Author { get; }

    /// <summary>The manifest's description, if any.</summary>
    public string? Description { get; }

    /// <summary>"v1.2.3 · by Someone", for the row's subtitle.</summary>
    public string Byline => string.Join(" · ", new[] { Version.Length > 0 ? "v" + Version : null, Author is null ? null : "by " + Author }.Where(s => s is not null));

    /// <summary>Every capability the manifest declares, with whether it is (to be) granted.</summary>
    public IReadOnlyList<PluginCapabilityViewModel> Capabilities { get; }

    /// <summary>Whether the plugin declares any capability.</summary>
    public bool HasCapabilities => Capabilities.Count > 0;

    /// <summary>Set when the plugin declares <c>network</c>: what it could send off the machine.</summary>
    public string? NetworkWarning { get; }

    /// <summary>The commander's enable / disable choice (persisted).</summary>
    public bool IsEnabled => !_entry.Disabled;

    /// <summary>Whether a grant matching the files on disk exists.</summary>
    public bool IsApproved => _entry.Approved is not null;

    /// <summary>The folder can be approved (its manifest and files could be read and hashed).</summary>
    public bool CanApprove => _entry.Request is not null;

    /// <summary>A grant is recorded (matching or not), so there is something to revoke.</summary>
    public bool CanRevoke => _entry.HasGrant;

    /// <summary>The plugin can be reloaded from disk (approved, enabled, and the plugins have a runtime).</summary>
    public bool CanReload => _entry.CanReload;

    /// <summary>Whether the enable toggle applies (a folder that is not a plugin has nothing to enable).</summary>
    public bool CanToggle => _entry.Request is not null;

    /// <summary>The approve button's label.</summary>
    public string ApproveLabel => IsApproved ? "Save permissions" : "Allow to run";

    internal PluginConsentRequest? Request => _entry.Request;

    /// <summary>Where the plugin stands.</summary>
    [ObservableProperty] private PluginRowState _state;

    /// <summary><see cref="State"/> in words.</summary>
    [ObservableProperty] private string _stateText = "";

    /// <summary>The most recent error (load failure, handler error, quarantine), or null.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] private string? _lastError;

    /// <summary>Whether <see cref="LastError"/> is set.</summary>
    public bool HasError => LastError is not null;

    [RelayCommand] private Task Approve() => _owner.ApproveAsync(this);

    [RelayCommand] private Task Revoke() => _owner.RevokeAsync(this);

    [RelayCommand] private Task Reload() => _owner.ReloadAsync(this);

    [RelayCommand] private Task ToggleEnabled() => _owner.ToggleEnabledAsync(this);

    internal void ApplyStatus(PluginStatus? status)
    {
        (State, StateText) = Describe(status);
        // An unapproved or disabled plugin is not failing: its "Denied" / "disabled" load result is
        // the expected outcome, not an error to count.
        LastError = State switch
        {
            PluginRowState.Invalid => _entry.Problem ?? "this folder is not a loadable plugin",
            PluginRowState.NeedsApproval or PluginRowState.Disabled or PluginRowState.Loading => null,
            _ => string.IsNullOrEmpty(status?.LastError) ? null : status.LastError,
        };
    }

    private (PluginRowState, string) Describe(PluginStatus? status)
    {
        if (_entry.Request is null) return (PluginRowState.Invalid, "Cannot be loaded");
        if (_entry.Disabled) return (PluginRowState.Disabled, "Disabled");
        switch (status?.State)
        {
            case PluginState.Quarantined: return (PluginRowState.Quarantined, "Quarantined: stopped after repeated errors");
            case PluginState.Enabled:
                // The copy in memory is the approved one; what is on disk now no longer matches it.
                return (PluginRowState.Running, _entry.Approved is null
                    ? "Running the copy you allowed, but its files have changed: it needs your approval before it starts again"
                    : "Running");
            case PluginState.Loading: return (PluginRowState.Loading, "Loading…");
        }
        if (_entry.Approved is null)
            return (PluginRowState.NeedsApproval, _entry.HasGrant
                ? "Needs your approval again: its files changed since you allowed it"
                : "Needs your approval: not running");
        if (status?.State == PluginState.Failed) return (PluginRowState.Failed, "Failed to start");
        return (PluginRowState.NotRunning, "Allowed, not running");
    }

    private static string? NetworkWarningFor(PluginManifest manifest)
    {
        if (!manifest.Declares(PluginCapabilities.Network)) return null;
        var reads = manifest.Declares(PluginCapabilities.Events) || manifest.Declares(PluginCapabilities.State);
        return reads
            ? "This plugin uses the network and reads your game data. It can send your commander name, credit balance, location, ship, cargo and everything else in your journal off this machine. EDNexus cannot block a plugin's network use: allow it only if you trust its author."
            : "This plugin uses the network. EDNexus cannot block a plugin's network use: allow it only if you trust its author.";
    }
}

/// <summary>One declared capability and whether the commander grants it.</summary>
public sealed partial class PluginCapabilityViewModel : ObservableObject
{
    internal PluginCapabilityViewModel(string name, bool granted)
    {
        Name = name;
        // Network cannot be enforced in-process, so a tick box for it would promise what nothing
        // delivers: it is shown, always part of the grant, and warned about instead.
        CanChange = name != PluginCapabilities.Network;
        IsGranted = granted || !CanChange;
        Label = name switch
        {
            PluginCapabilities.Events => "Journal events: everything the game logs, including your commander name and credit balance",
            PluginCapabilities.State => "Commander state: your commander name, credit balance, location, ship, cargo and materials",
            PluginCapabilities.Storage => "Storage: keep its own data in its plugin folder",
            PluginCapabilities.UiDashboard => "Dashboard widgets (not available in EDNexus yet)",
            PluginCapabilities.UiOverlay => "Overlay panels (not available in EDNexus yet)",
            PluginCapabilities.Network => "Network: can send data off this machine (cannot be switched off)",
            _ => name,
        };
    }

    /// <summary>The capability as written in <c>plugin.json</c>.</summary>
    public string Name { get; }

    /// <summary>What granting it means, in words.</summary>
    public string Label { get; }

    /// <summary>False for <c>network</c>, which is always part of the grant.</summary>
    public bool CanChange { get; }

    /// <summary>Whether it is (to be) granted; applied by the row's approve command.</summary>
    [ObservableProperty] private bool _isGranted;
}

using FileHound.Core.Persistence;
using FileHound.Indexing.Ntfs;

namespace FileHound.Indexing;

public sealed record IndexOptions(
    string DataDirectory,
    IReadOnlyCollection<string> ExcludedPaths,
    bool PreferTurbo = true,
    Func<IReadOnlyList<DriveDescriptor>>? DriveSource = null,
    TimeSpan? SnapshotInterval = null,
    TimeSpan? DrivePollInterval = null);

public enum DriveStatus { Loading, Scanning, FillingDetails, Ready, Offline, Error }

public sealed record DriveState(
    DriveDescriptor Drive,
    IndexMode Mode,
    DriveStatus Status,
    long Entries,
    double Progress,
    int Skipped,
    bool MetadataComplete,
    DateTimeOffset? LastIndexed,
    string? Error);

/// <summary>
/// Owns one <see cref="VolumeIndex"/> per drive: loads snapshots, chooses Turbo (MFT + USN, elevated NTFS) or
/// Standard (walker + watcher) indexing, keeps indexes live, detects drive arrival/removal and saves snapshots.
/// </summary>
public sealed class IndexManager : IAsyncDisposable
{
    private sealed class Slot(DriveDescriptor drive)
    {
        public DriveDescriptor Drive = drive;
        public VolumeIndex? Index;
        public DriveState State = new(drive, IndexMode.Standard, DriveStatus.Loading, 0, 0, 0, false, null, null);
        public IDisposable? Updater;
        public CancellationTokenSource Cts = new();
        public Task Work = Task.CompletedTask;
    }

    private readonly IndexOptions _options;
    private readonly object _gate = new();
    private readonly Dictionary<char, Slot> _slots = [];
    private readonly CancellationTokenSource _lifetime = new();
    private Task _pollLoop = Task.CompletedTask;
    private Task _snapshotLoop = Task.CompletedTask;
    private readonly Throttler _stateThrottle;
    private readonly Throttler _indexThrottle;

    public IndexManager(IndexOptions options)
    {
        _options = options;
        _stateThrottle = new Throttler(250, () => StateChanged?.Invoke(this, EventArgs.Empty));
        _indexThrottle = new Throttler(400, () => IndexChanged?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>Optional diagnostic sink (wired to the app log).</summary>
    public static Action<string>? Log { get; set; }

    /// <summary>Raised (throttled, background thread) when drive states change.</summary>
    public event EventHandler? StateChanged;
    /// <summary>Raised (throttled, background thread) after live updates or a scan changed searchable content.</summary>
    public event EventHandler? IndexChanged;

    public IReadOnlyList<DriveState> Drives
    {
        get { lock (_gate) return _slots.Values.OrderBy(s => s.Drive.Letter).Select(s => s.State).ToList(); }
    }

    public IReadOnlyList<VolumeIndex> Volumes
    {
        get
        {
            lock (_gate)
                return _slots.Values.Where(s => s.Index is not null && s.State.Status != DriveStatus.Offline)
                    .OrderBy(s => s.Drive.Letter).Select(s => s.Index!).ToList();
        }
    }

    private string IndexDirectory => Path.Combine(_options.DataDirectory, "index");
    private IReadOnlyList<DriveDescriptor> DiscoverDrives() => (_options.DriveSource ?? DriveDiscovery.GetDrives)();

    /// <summary>Discovers drives, publishes snapshots (so search works immediately) and starts indexing in the background.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(IndexDirectory);
        var drives = DiscoverDrives();
        var loads = new List<Task>();
        foreach (var d in drives)
        {
            var slot = new Slot(d);
            lock (_gate) _slots[d.Letter] = slot;
            var loaded = new TaskCompletionSource();
            loads.Add(loaded.Task);
            StartDrive(slot, useSnapshot: true, loaded);
        }
        await Task.WhenAll(loads).WaitAsync(ct).ConfigureAwait(false);
        var token = _lifetime.Token;
        _pollLoop = Task.Run(() => PollDrivesAsync(token), token);
        _snapshotLoop = Task.Run(() => SnapshotLoopAsync(token), token);
        RaiseStateChanged();
    }

    /// <summary>Completes when no drive is loading, scanning or filling details.</summary>
    public async Task WaitForIdleAsync()
    {
        while (true)
        {
            Task[] work;
            lock (_gate) work = _slots.Values.Select(s => s.Work).ToArray();
            await Task.WhenAll(work).ConfigureAwait(false);
            lock (_gate) if (_slots.Values.All(s => s.Work.IsCompleted)) return;
        }
    }

    public Task RescanAsync(char letter)
    {
        Slot? slot;
        lock (_gate) _slots.TryGetValue(char.ToUpperInvariant(letter), out slot);
        if (slot is null) return Task.CompletedTask;
        StopSlot(slot);
        slot.Cts = new CancellationTokenSource();
        StartDrive(slot, useSnapshot: false, null);
        return Task.CompletedTask;
    }

    public async Task SaveSnapshotsAsync()
    {
        List<Slot> slots;
        lock (_gate) slots = _slots.Values.ToList();
        await Task.Run(() =>
        {
            foreach (var s in slots)
            {
                var v = s.Index;
                if (v is null || !v.IsDirty || s.State.Status is not (DriveStatus.Ready or DriveStatus.FillingDetails)) continue;
                try
                {
                    v.IsDirty = false;
                    SnapshotSerializer.Save(v, Path.Combine(IndexDirectory, SnapshotSerializer.FileNameFor(s.Drive.Letter, s.Drive.Serial)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    v.IsDirty = true;
                    Log?.Invoke($"Snapshot save failed for {s.Drive.Letter}: {ex.Message}");
                }
            }
        }).ConfigureAwait(false);
    }

    private void StartDrive(Slot slot, bool useSnapshot, TaskCompletionSource? loaded)
    {
        var ct = slot.Cts.Token;
        slot.Work = Task.Run(async () =>
        {
            try { await IndexDriveAsync(slot, useSnapshot, loaded, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log?.Invoke($"Indexing {slot.Drive.Letter}: failed: {ex}");
                Update(slot, s => s with { Status = DriveStatus.Error, Error = ex.Message });
            }
            finally { loaded?.TrySetResult(); }
        });
    }

    private async Task IndexDriveAsync(Slot slot, bool useSnapshot, TaskCompletionSource? loaded, CancellationToken ct)
    {
        var drive = slot.Drive;
        VolumeIndex? snapshot = null;
        if (useSnapshot)
        {
            snapshot = SnapshotSerializer.Load(Path.Combine(IndexDirectory, SnapshotSerializer.FileNameFor(drive.Letter, drive.Serial)));
            if (snapshot is not null)
            {
                Publish(slot, snapshot);
                var when = File.GetLastWriteTime(Path.Combine(IndexDirectory, SnapshotSerializer.FileNameFor(drive.Letter, drive.Serial)));
                Update(slot, s => s with { Mode = snapshot.Mode, Status = DriveStatus.Ready, Entries = snapshot.LiveCount, Progress = 1, MetadataComplete = true, LastIndexed = when });
                Log?.Invoke($"Indexing {drive.Letter}: snapshot loaded ({snapshot.LiveCount:N0} entries)");
            }
        }
        loaded?.TrySetResult();

        bool turbo = _options.PreferTurbo && drive.IsNtfs && Elevation.IsElevated;
        if (turbo)
        {
            try
            {
                await RunTurboAsync(slot, snapshot, ct).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log?.Invoke($"Indexing {drive.Letter}: Turbo failed, falling back to Standard: {ex.Message}");
            }
        }
        await RunStandardAsync(slot, snapshot, ct).ConfigureAwait(false);
    }

    private async Task RunTurboAsync(Slot slot, VolumeIndex? snapshot, CancellationToken ct)
    {
        var drive = slot.Drive;
        VolumeIndex v;
        bool needFill = true;
        if (snapshot is { Mode: IndexMode.Turbo } &&
            MftScanner.TryQueryJournal(drive.Letter, out ulong journalId, out long firstUsn, out _) &&
            journalId == snapshot.UsnJournalId && snapshot.NextUsn >= firstUsn)
        {
            v = snapshot;
            needFill = false;
            Log?.Invoke($"Indexing {drive.Letter}: resuming USN journal from snapshot");
        }
        else
        {
            Update(slot, s => s with { Mode = IndexMode.Turbo, Status = DriveStatus.Scanning, Progress = 0, Error = null });
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var progress = new Progress(p => Update(slot, s => s with { Entries = p.Entries, Progress = p.Fraction }));
            v = await Task.Run(() => new MftScanner().Scan(drive, _options.ExcludedPaths, progress, ct), ct).ConfigureAwait(false);
            Publish(slot, v);
            Log?.Invoke($"Indexing {drive.Letter}: MFT scan {v.LiveCount:N0} entries in {sw.Elapsed.TotalSeconds:F1}s");
        }

        var usn = new UsnUpdater(v, drive.Letter, TimeSpan.FromMilliseconds(500));
        usn.Applied += (_, _) => { RaiseIndexChanged(); };
        usn.JournalInvalid += (_, _) => { Log?.Invoke($"Indexing {drive.Letter}: USN journal invalid, rescanning"); _ = RescanAsync(drive.Letter); };
        slot.Updater = usn;
        usn.Start();

        if (needFill)
        {
            Update(slot, s => s with { Mode = IndexMode.Turbo, Status = DriveStatus.FillingDetails, Entries = v.LiveCount, Progress = 0, MetadataComplete = false });
            await new MetadataFiller().FillAsync(v, new Progress<double>(p => Update(slot, s => s with { Progress = p })), ct).ConfigureAwait(false);
            v.IsDirty = true;
        }
        Update(slot, s => s with { Mode = IndexMode.Turbo, Status = DriveStatus.Ready, Entries = v.LiveCount, Progress = 1, MetadataComplete = true, LastIndexed = DateTimeOffset.Now, Error = null });
        RaiseIndexChanged();
        await SaveSnapshotsAsync().ConfigureAwait(false);
    }

    private async Task RunStandardAsync(Slot slot, VolumeIndex? snapshot, CancellationToken ct)
    {
        var drive = slot.Drive;
        var v = new VolumeIndex(drive.Root, IndexMode.Standard, 1 << 16) { VolumeSerial = drive.Serial };
        bool streaming = snapshot is null;
        if (streaming) Publish(slot, v);
        Update(slot, s => s with { Mode = IndexMode.Standard, Status = streaming ? DriveStatus.Scanning : DriveStatus.Ready, Progress = streaming ? 0 : 1, Error = null });

        int workers = drive.IsRemovable ? 2 : Math.Min(Environment.ProcessorCount, 8);
        var walker = new DirectoryWalker(_options.ExcludedPaths, workers);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var progress = new Progress(p =>
        {
            if (streaming) { Update(slot, s => s with { Entries = p.Entries, Progress = p.Fraction, Skipped = p.Skipped }); RaiseIndexChanged(); }
        });
        var result = await walker.WalkAsync(v, drive.Root, VolumeIndex.RootEntry, progress, ct).ConfigureAwait(false);
        if (!streaming) Publish(slot, v);
        Log?.Invoke($"Indexing {drive.Letter}: walked {result.Entries:N0} entries in {sw.Elapsed.TotalSeconds:F1}s ({result.Skipped} skipped)");

        var watcher = new WatcherUpdater(v, drive.Root, walker, TimeSpan.FromMilliseconds(500));
        watcher.Applied += (_, _) => RaiseIndexChanged();
        watcher.Overflowed += (_, _) => { Log?.Invoke($"Indexing {drive.Letter}: watcher overflow, rescanning"); _ = RescanAsync(drive.Letter); };
        try
        {
            watcher.Start();
            slot.Updater = watcher;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException)
        {
            watcher.Dispose();
            Log?.Invoke($"Indexing {drive.Letter}: live updates unavailable: {ex.Message}");
        }

        Update(slot, s => s with { Status = DriveStatus.Ready, Entries = v.LiveCount, Progress = 1, Skipped = result.Skipped, MetadataComplete = true, LastIndexed = DateTimeOffset.Now });
        RaiseIndexChanged();
        await SaveSnapshotsAsync().ConfigureAwait(false);
    }

    private void Publish(Slot slot, VolumeIndex v)
    {
        lock (_gate) slot.Index = v;
        RaiseIndexChanged();
    }

    private void Update(Slot slot, Func<DriveState, DriveState> change)
    {
        lock (_gate) slot.State = change(slot.State);
        RaiseStateChanged();
    }

    private void StopSlot(Slot slot)
    {
        slot.Cts.Cancel();
        try { slot.Work.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        slot.Updater?.Dispose();
        slot.Updater = null;
    }

    private async Task PollDrivesAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.DrivePollInterval ?? TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                IReadOnlyList<DriveDescriptor> current;
                try { current = DiscoverDrives(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                var letters = current.ToDictionary(d => d.Letter);
                List<Slot> gone = [], back = [];
                List<DriveDescriptor> added = [];
                lock (_gate)
                {
                    foreach (var s in _slots.Values)
                    {
                        bool present = letters.TryGetValue(s.Drive.Letter, out var d) && d.Serial == s.Drive.Serial;
                        if (!present && s.State.Status != DriveStatus.Offline) gone.Add(s);
                        if (present && s.State.Status == DriveStatus.Offline) back.Add(s);
                    }
                    foreach (var d in current)
                        if (!_slots.TryGetValue(d.Letter, out var s) || (s.Drive.Serial != d.Serial && s.State.Status == DriveStatus.Offline))
                            added.Add(d);
                }
                foreach (var s in gone)
                {
                    StopSlot(s);
                    Update(s, st => st with { Status = DriveStatus.Offline });
                    Log?.Invoke($"Drive {s.Drive.Letter}: offline");
                    RaiseIndexChanged();
                }
                foreach (var s in back) { s.Cts = new CancellationTokenSource(); StartDrive(s, true, null); }
                foreach (var d in added)
                {
                    var slot = new Slot(d);
                    lock (_gate) _slots[d.Letter] = slot;
                    Log?.Invoke($"Drive {d.Letter}: arrived");
                    StartDrive(slot, true, null);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task SnapshotLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.SnapshotInterval ?? TimeSpan.FromMinutes(15));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                await SaveSnapshotsAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    private void RaiseStateChanged() => _stateThrottle.Signal();
    private void RaiseIndexChanged() => _indexThrottle.Signal();

    /// <summary>Coalesces bursts of signals into at most one (trailing) invocation per window.</summary>
    private sealed class Throttler(int ms, Action raise)
    {
        private int _pending;

        public void Signal()
        {
            if (Interlocked.Exchange(ref _pending, 1) == 1) return;
            _ = Task.Delay(ms).ContinueWith(_ =>
            {
                Volatile.Write(ref _pending, 0);
                try { raise(); }
                catch (Exception ex) { Log?.Invoke($"Event handler failed: {ex}"); }
            }, TaskScheduler.Default);
        }
    }
    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        List<Slot> slots;
        lock (_gate) slots = _slots.Values.ToList();
        foreach (var s in slots) s.Cts.Cancel();
        try { await Task.WhenAll(slots.Select(s => s.Work).Append(_pollLoop).Append(_snapshotLoop)).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException) { }
        foreach (var s in slots) s.Updater?.Dispose();
        await SaveSnapshotsAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private sealed class Progress(Action<ScanProgress> report) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => report(value);
    }

}

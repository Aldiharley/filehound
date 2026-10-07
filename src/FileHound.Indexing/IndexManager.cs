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
/// <remarks>
/// Lifecycle rules: per drive, at most one indexing run is active (rescans are serialized by <c>Slot.Gate</c> and
/// coalesced); the previously published index stays searchable until its replacement is complete; live updaters
/// are only attached to the published index and are disposed before the next run starts.
/// </remarks>
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
        public readonly SemaphoreSlim Gate = new(1, 1);
        public int RescanQueued;
        public DateTime LastOverflowRescan;
    }

    private readonly IndexOptions _options;
    private readonly object _gate = new();
    private readonly Dictionary<char, Slot> _slots = [];
    private readonly CancellationTokenSource _lifetime = new();
    private Task _pollLoop = Task.CompletedTask;
    private Task _snapshotLoop = Task.CompletedTask;
    private readonly Throttler _stateThrottle;
    private readonly Throttler _indexThrottle;
    private volatile string[] _excluded;

    public IndexManager(IndexOptions options)
    {
        _options = options;
        _excluded = options.ExcludedPaths.ToArray();
        _stateThrottle = new Throttler(250, () => StateChanged?.Invoke(this, EventArgs.Empty));
        _indexThrottle = new Throttler(400, () => IndexChanged?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>Optional diagnostic sink (wired to the app log).</summary>
    public static Action<string>? Log { get; set; }

    /// <summary>Test hook: runs on the drive task right before a refresh walk starts replacing a published snapshot.</summary>
    internal Action? BeforeRefreshWalk { get; set; }

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

    /// <summary>Folders to skip from now on (applied on the next scan or rescan of each drive).</summary>
    public void SetExcludedPaths(IEnumerable<string> paths) => _excluded = paths.ToArray();

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
            var slot = new Slot(d) { Cts = NewSlotCts() };
            lock (_gate) _slots[d.Letter] = slot;
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            loads.Add(loaded.Task);
            StartDrive(slot, useSnapshot: true, standIn: null, loaded);
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
            lock (_gate)
                if (_slots.Values.All(s => s.Work.IsCompleted && s.RescanQueued == 0 && s.Gate.CurrentCount == 1)) break;
            await Task.Delay(20).ConfigureAwait(false);
        }
        // Resume on a fresh thread-pool turn: otherwise the caller continues inline inside the completing drive
        // task, whose async frames still reference the superseded snapshot index until they unwind.
        await Task.Yield();
    }

    /// <summary>
    /// Re-indexes one drive in the background. The current index stays searchable until the new one is ready.
    /// Concurrent requests for the same drive are serialized and coalesced.
    /// </summary>
    public async Task RescanAsync(char letter)
    {
        Slot? slot;
        lock (_gate) _slots.TryGetValue(char.ToUpperInvariant(letter), out slot);
        if (slot is null || _lifetime.IsCancellationRequested) return;
        if (Interlocked.Exchange(ref slot.RescanQueued, 1) == 1) return; // one is already waiting to run
        try { await slot.Gate.WaitAsync(_lifetime.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        try
        {
            Volatile.Write(ref slot.RescanQueued, 0);
            if (_lifetime.IsCancellationRequested || slot.State.Status == DriveStatus.Offline) return;
            await StopSlotAsync(slot).ConfigureAwait(false);
            if (_lifetime.IsCancellationRequested) return; // shutting down: don't start a run nobody will stop
            slot.Cts = NewSlotCts();
            VolumeIndex? standIn;
            lock (_gate) standIn = slot.Index;
            StartDrive(slot, useSnapshot: false, standIn, loaded: null);
        }
        catch (ObjectDisposedException) { /* manager disposed meanwhile */ }
        finally { slot.Gate.Release(); }
    }

    /// <summary>Per-run token source, cancelled automatically when the manager shuts down.</summary>
    private CancellationTokenSource NewSlotCts() => CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);

    /// <summary>Fire-and-forget rescan request from an updater callback (never blocks the caller's thread).</summary>
    private void RequestRescan(char letter, string reason)
    {
        Log?.Invoke($"Indexing {letter}: {reason}, rescanning");
        _ = Task.Run(() => RescanAsync(letter));
    }

    /// <summary>
    /// A live updater's loop died. Rescan to rebuild the index with a fresh updater, but never more than once per
    /// ten minutes per drive, so a persistent fault degrades to a stale-but-searchable index with a visible note
    /// instead of an endless rescan loop.
    /// </summary>
    private void OnUpdaterFaulted(Slot slot, string what, Exception ex)
    {
        Log?.Invoke($"Indexing {slot.Drive.Letter}: {what} failed: {ex}");
        if (!TryClaimRescanWindow(slot))
        {
            Log?.Invoke($"Indexing {slot.Drive.Letter}: rescan deferred; live updates are off until then");
            Update(slot, s => s with { Error = $"Live updates stopped ({ex.GetType().Name}); will rescan later" });
            return;
        }
        RequestRescan(slot.Drive.Letter, $"{what} failed");
    }

    /// <summary>True at most once per ten minutes per drive: the updater-triggered rescan budget.</summary>
    private static bool TryClaimRescanWindow(Slot slot)
    {
        var now = DateTime.UtcNow;
        if (now - slot.LastOverflowRescan < TimeSpan.FromMinutes(10)) return false;
        slot.LastOverflowRescan = now;
        return true;
    }

    public async Task SaveSnapshotsAsync()
    {
        List<Slot> slots;
        lock (_gate) slots = _slots.Values.ToList();
        await Task.Run(() =>
        {
            foreach (var s in slots)
            {
                VolumeIndex? v;
                DriveStatus status;
                lock (_gate) { v = s.Index; status = s.State.Status; }
                // A partially filled Turbo index may be saved; resume detects and completes missing metadata.
                if (v is null || !v.IsDirty || status is not (DriveStatus.Ready or DriveStatus.FillingDetails)) continue;
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

    private void StartDrive(Slot slot, bool useSnapshot, VolumeIndex? standIn, TaskCompletionSource? loaded)
    {
        var ct = slot.Cts.Token;
        slot.Work = Task.Run(async () =>
        {
            try { await IndexDriveAsync(slot, useSnapshot, standIn, loaded, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log?.Invoke($"Indexing {slot.Drive.Letter}: failed: {ex}");
                Update(slot, s => s with { Status = DriveStatus.Error, Error = ex.Message });
            }
            finally { loaded?.TrySetResult(); }
        });
    }

    private async Task IndexDriveAsync(Slot slot, bool useSnapshot, VolumeIndex? standIn, TaskCompletionSource? loaded, CancellationToken ct)
    {
        var drive = slot.Drive;
        VolumeIndex? snapshot = null;
        if (useSnapshot)
        {
            var path = Path.Combine(IndexDirectory, SnapshotSerializer.FileNameFor(drive.Letter, drive.Serial));
            snapshot = SnapshotSerializer.Load(path);
            if (snapshot is not null)
            {
                Publish(slot, snapshot);
                var when = File.GetLastWriteTime(path);
                long live = snapshot.LiveCount;
                var mode = snapshot.Mode;
                Update(slot, s => s with { Mode = mode, Status = DriveStatus.Ready, Entries = live, Progress = 1, MetadataComplete = true, LastIndexed = when });
                Log?.Invoke($"Indexing {drive.Letter}: snapshot loaded ({live:N0} entries)");
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
                DisposeUpdater(slot);
            }
        }
        // From here on the superseded index is the slot's business. Clear our hoisted references so it can be
        // collected as soon as the new index is published, rather than living on through the snapshot save.
        var previous = snapshot ?? standIn;
        snapshot = null;
        standIn = null;
        await RunStandardAsync(slot, previous, ct).ConfigureAwait(false);
    }

    private async Task RunTurboAsync(Slot slot, VolumeIndex? snapshot, CancellationToken ct)
    {
        var drive = slot.Drive;
        VolumeIndex v;
        if (snapshot is { Mode: IndexMode.Turbo } &&
            MftScanner.TryQueryJournal(drive.Letter, out ulong journalId, out long firstUsn, out _) &&
            journalId == snapshot.UsnJournalId && snapshot.NextUsn >= firstUsn)
        {
            v = snapshot;
            ApplyExclusions(v, drive);
            Log?.Invoke($"Indexing {drive.Letter}: resuming USN journal from snapshot");
        }
        else
        {
            snapshot = null; // stays searchable via the slot; don't root it from this frame through the scan and save
            Update(slot, s => s with { Mode = IndexMode.Turbo, Status = DriveStatus.Scanning, Progress = 0, Error = null });
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var progress = new Progress(p => Update(slot, s => s with { Entries = p.Entries, Progress = p.Fraction }));
            var excluded = _excluded;
            var scanner = new MftScanner();
            v = await Task.Run(() => scanner.Scan(drive, excluded, progress, ct), ct).ConfigureAwait(false);
            if (scanner.FallbackReason is not null) Log?.Invoke($"Indexing {drive.Letter}: faster MFT tiers skipped ({scanner.FallbackReason}); used {scanner.Method}");
            ct.ThrowIfCancellationRequested();
            Publish(slot, v);
            Log?.Invoke($"Indexing {drive.Letter}: MFT scan ({scanner.Method}) {v.LiveCount:N0} entries in {sw.Elapsed.TotalSeconds:F1}s");
        }

        ct.ThrowIfCancellationRequested();
        var usn = new UsnUpdater(v, drive.Letter, TimeSpan.FromMilliseconds(500));
        usn.Applied += (_, _) => RaiseIndexChanged();
        usn.JournalInvalid += (_, _) => RequestRescan(drive.Letter, "USN journal invalid");
        usn.Faulted += (_, ex) => OnUpdaterFaulted(slot, "USN updater", ex);
        slot.Updater = usn;
        usn.Start();

        // The raw $MFT reader delivers sizes and dates itself; enumeration (or a snapshot saved mid-fill) does not.
        bool needFill = MetadataFiller.HasIncompleteMetadata(v);
        if (needFill)
        {
            Update(slot, s => s with { Mode = IndexMode.Turbo, Status = DriveStatus.FillingDetails, Entries = v.LiveCount, Progress = 0, MetadataComplete = false });
            // SetMetadata marks the index dirty only when something was actually filled, so folders that can
            // never be listed don't force a snapshot rewrite on every launch.
            await new MetadataFiller().FillAsync(v, new Progress<double>(p => Update(slot, s => s with { Progress = p })), ct, onlyIncomplete: true).ConfigureAwait(false);
        }
        Update(slot, s => s with { Mode = IndexMode.Turbo, Status = DriveStatus.Ready, Entries = v.LiveCount, Progress = 1, MetadataComplete = true, LastIndexed = DateTimeOffset.Now, Error = null });
        RaiseIndexChanged();
        await SaveSnapshotsAsync().ConfigureAwait(false);
    }

    /// <param name="previous">Index currently published for this drive (snapshot or the one being rescanned); it stays
    /// searchable until the new walk completes. When null, results stream in as the walk progresses.</param>
    private async Task RunStandardAsync(Slot slot, VolumeIndex? previous, CancellationToken ct)
    {
        var drive = slot.Drive;
        var v = new VolumeIndex(drive.Root, IndexMode.Standard, 1 << 16) { VolumeSerial = drive.Serial };
        bool streaming = previous is null;
        previous = null; // the slot keeps it searchable; this frame must not keep it alive once the slot lets go
        if (streaming) Publish(slot, v);
        // Shown as Scanning either way; when refreshing, the previous index keeps answering searches meanwhile.
        Update(slot, s => s with { Mode = IndexMode.Standard, Status = DriveStatus.Scanning, Progress = 0, Error = null });

        int workers = drive.IsRemovable ? 2 : Math.Min(Environment.ProcessorCount, 8);
        var walker = new DirectoryWalker(_excluded, workers);

        // Start watching before the walk (paused) so changes made while walking are queued, not lost.
        // Removable drives are not watched: an open watcher handle blocks "Safely remove hardware".
        WatcherUpdater? watcher = null;
        if (!drive.IsRemovable)
        {
            watcher = new WatcherUpdater(v, drive.Root, walker, TimeSpan.FromMilliseconds(500));
            watcher.Applied += (_, _) => RaiseIndexChanged();
            watcher.Faulted += (_, ex) => Log?.Invoke($"Indexing {drive.Letter}: live update failed: {ex}"); // Overflowed follows and drives the rescan
            watcher.Overflowed += (_, _) =>
            {
                // Rate-limit: on a disk that churns faster than we can walk, rescanning in a loop would never settle.
                if (!TryClaimRescanWindow(slot)) { Log?.Invoke($"Indexing {drive.Letter}: watcher lost events again; next rescan deferred"); return; }
                RequestRescan(drive.Letter, "watcher lost events");
            };
            try { watcher.Start(paused: true); }
            catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException or UnauthorizedAccessException)
            {
                watcher.Dispose();
                watcher = null;
                Log?.Invoke($"Indexing {drive.Letter}: live updates unavailable: {ex.Message}");
            }
        }

        bool attached = false;
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var progress = new Progress(p =>
            {
                Update(slot, s => s with { Entries = streaming ? p.Entries : s.Entries, Progress = p.Fraction, Skipped = p.Skipped });
                if (streaming) RaiseIndexChanged();
            });
            if (!streaming) BeforeRefreshWalk?.Invoke();
            var result = await walker.WalkAsync(v, drive.Root, VolumeIndex.RootEntry, progress, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            v.TrimExcess();
            if (!streaming) Publish(slot, v);
            Log?.Invoke($"Indexing {drive.Letter}: walked {result.Entries:N0} entries in {sw.Elapsed.TotalSeconds:F1}s ({result.Skipped} skipped)");

            if (watcher is not null)
            {
                slot.Updater = watcher;
                attached = true;
                watcher.Resume(); // replays everything queued during the walk
            }
            Update(slot, s => s with { Status = DriveStatus.Ready, Entries = v.LiveCount, Progress = 1, Skipped = result.Skipped, MetadataComplete = true, LastIndexed = DateTimeOffset.Now });
        }
        finally
        {
            if (!attached) watcher?.Dispose();
        }
        RaiseIndexChanged();
        await SaveSnapshotsAsync().ConfigureAwait(false);
    }

    private void ApplyExclusions(VolumeIndex v, DriveDescriptor drive)
    {
        foreach (var path in _excluded)
        {
            if (!path.StartsWith(drive.Root, StringComparison.OrdinalIgnoreCase)) continue;
            int e = v.FindByPath(path);
            if (e > 0) v.Delete(e);
        }
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

    private static void DisposeUpdater(Slot slot)
    {
        var updater = Interlocked.Exchange(ref slot.Updater, null);
        updater?.Dispose();
    }

    /// <summary>Stops live updates and the running indexing task for a slot without blocking the caller's thread.</summary>
    private static async Task StopSlotAsync(Slot slot)
    {
        await Task.Run(() => DisposeUpdater(slot)).ConfigureAwait(false);
        slot.Cts.Cancel();
        try { await slot.Work.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }
        DisposeUpdater(slot); // a run that was finishing may have attached one meanwhile
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
                    await s.Gate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        await StopSlotAsync(s).ConfigureAwait(false);
                        Update(s, st => st with { Status = DriveStatus.Offline });
                    }
                    finally { s.Gate.Release(); }
                    Log?.Invoke($"Drive {s.Drive.Letter}: offline");
                    RaiseIndexChanged();
                }
                foreach (var s in back)
                {
                    await s.Gate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        s.Cts = NewSlotCts();
                        StartDrive(s, useSnapshot: true, standIn: null, loaded: null);
                    }
                    finally { s.Gate.Release(); }
                    Log?.Invoke($"Drive {s.Drive.Letter}: back online");
                }
                foreach (var d in added)
                {
                    var slot = new Slot(d) { Cts = NewSlotCts() };
                    lock (_gate) _slots[d.Letter] = slot;
                    Log?.Invoke($"Drive {d.Letter}: arrived");
                    StartDrive(slot, useSnapshot: true, standIn: null, loaded: null);
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
        _lifetime.Cancel(); // also cancels every slot's linked token source
        List<Slot> slots;
        lock (_gate) slots = _slots.Values.ToList();
        // Wait for in-flight rescans to finish their (now cancelled) hand-over so no new run starts behind us.
        foreach (var s in slots)
        {
            if (await s.Gate.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false)) s.Gate.Release();
        }
        try { await Task.WhenAll(slots.Select(s => s.Work).Append(_pollLoop).Append(_snapshotLoop)).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException) { }
        foreach (var s in slots) DisposeUpdater(s);
        await SaveSnapshotsAsync().ConfigureAwait(false);
        // _lifetime is intentionally not disposed: late rescan requests may still observe its token.
    }

    private sealed class Progress(Action<ScanProgress> report) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => report(value);
    }
}

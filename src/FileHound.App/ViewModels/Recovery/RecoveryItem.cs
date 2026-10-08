using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using FileHound.App.Services;
using FileHound.Core.Recovery;

namespace FileHound.App.ViewModels.Recovery;

/// <summary>One row in a recovery list: a candidate plus its display strings, selection and (after recovery) its outcome.</summary>
public sealed partial class RecoveryItem : ObservableObject
{
    private ImageSource? _icon;
    private bool _iconLoaded;

    public RecoveryItem(RecoveryCandidate candidate, DateTime nowUtc)
    {
        Candidate = candidate;
        Name = candidate.Name;
        Folder = candidate.OriginalFolder ?? "<unknown folder>";
        IsUnknownFolder = candidate.OriginalFolder is null;
        SizeText = candidate.IsDirectory ? "" : candidate.Size < 0 ? "…" : Formatting.Size(candidate.Size);
        WhenText = candidate.DeletedUtc is { } d ? Formatting.Modified(d.Ticks, nowUtc) : candidate.ModifiedUtc is { } m ? Formatting.Modified(m.Ticks, nowUtc) : "";
        WhenTooltip = candidate.DeletedUtc is { } dd ? "Deleted " + dd.ToLocalTime().ToString("f") : "";
        Detail = candidate.Detail ?? "";
        (GradeKey, GradeText, GradeTooltip) = Describe(candidate);
    }

    public RecoveryCandidate Candidate { get; }
    public string Name { get; }
    public string Folder { get; }
    public bool IsUnknownFolder { get; }
    public string SizeText { get; }
    public string WhenText { get; }
    public string WhenTooltip { get; }
    public string Detail { get; }
    public bool IsDirectory => Candidate.IsDirectory;

    /// <summary>Grade chip key: Excellent, Good, Partial, Overwritten, Zeroed, Encrypted, Unknown, Recycled, Replaced, Reused.</summary>
    [ObservableProperty] private string _gradeKey;
    [ObservableProperty] private string _gradeText;
    [ObservableProperty] private string _gradeTooltip;
    [ObservableProperty] private bool _isSelected;
    /// <summary>After recovery: "Recovered ✓", "Degraded", "Failed", or null.</summary>
    [ObservableProperty] private string? _status;
    [ObservableProperty] private string? _statusKey;
    [ObservableProperty] private string? _sha256;
    [ObservableProperty] private string? _recoveredPath;

    public string Sha256Short => Sha256 is { Length: > 12 } s ? s[..12] + "…" : Sha256 ?? "";

    public ImageSource? Icon
    {
        get
        {
            if (!_iconLoaded) { _icon = ResultItem.IconProvider?.Get(Candidate.OriginalPath, Candidate.IsDirectory); _iconLoaded = true; }
            return _icon;
        }
    }

    public void ApplyOutcome(RecoveredFile r)
    {
        RecoveredPath = r.Succeeded ? r.RecoveredPath : null;
        Sha256 = r.Sha256Hex.Length > 0 ? r.Sha256Hex : null;
        OnPropertyChanged(nameof(Sha256Short));
        if (!r.Succeeded) { Status = "Failed — " + r.Error; StatusKey = "Failed"; }
        else if (r.FinalGrade != Candidate.Grade && r.FinalGrade is RecoveryGrade.Partial or RecoveryGrade.Zeroed) { Status = "Degraded — changed since the scan"; StatusKey = "Degraded"; }
        else { Status = "Recovered ✓"; StatusKey = "Recovered"; }
    }

    public void ApplyRestore(string path)
    {
        RecoveredPath = path;
        Status = "Restored ✓";
        StatusKey = "Recovered";
    }

    /// <summary>Grade chip copy per the UI spec §8: truthful cause-and-effect, never "100%".</summary>
    public static (string Key, string Text, string Tooltip) Describe(RecoveryCandidate c)
    {
        if (c.Key is DeletionLogItem log)
        {
            return log.Kind switch
            {
                DeletionKind.Recycled => ("Recycled", "In Recycle Bin", "This file was moved to the Recycle Bin. Restore it from the Recycle Bin tab."),
                DeletionKind.Replaced => ("Replaced", "Replaced", "A new file with the same name appeared right after; this is usually an app saving a new version."),
                _ => log.Slot switch
                {
                    SlotState.Free => ("Excellent", "Recoverable (slot intact)", "The file's record is still unused on the drive, so undelete can bring it back."),
                    SlotState.Reused => ("Overwritten", "Record reused", "Another file has taken this file's record. Try Deep scan to find its contents by signature."),
                    _ => ("Unknown", "Checking…", "FileHound is checking whether this file's record is still intact."),
                },
            };
        }
        return c.Grade switch
        {
            RecoveryGrade.Excellent => ("Excellent", "Excellent", "All data still on disk."),
            RecoveryGrade.Good => ("Good", "Good", "All data appears intact, but it could not be fully verified."),
            RecoveryGrade.Partial => ("Partial", $"Partially overwritten ({c.PercentIntact}%)", $"Some of this file's space has been reused by other files. About {c.PercentIntact}% is still intact."),
            RecoveryGrade.Overwritten => ("Overwritten", "Overwritten", "Every part of this file's space has been reused by other files."),
            RecoveryGrade.Zeroed => ("Zeroed", "Zeroed", "On SSDs, Windows tells the drive to discard deleted data within seconds. If a file shows Zeroed, no copy of it exists on this drive."),
            RecoveryGrade.Encrypted => ("Encrypted", "Encrypted", "This file was encrypted with EFS and cannot be recovered."),
            _ => ("Unknown", "Unknown", c.Detail ?? "FileHound could not determine whether this file's data is intact."),
        };
    }
}

/// <summary>The slot check result for a deletion-log item, filled in lazily.</summary>
public enum SlotState { Pending, Free, Reused, Unknown }

/// <summary>Key object for deletion-log candidates: the entry plus its lazily computed slot state.</summary>
public sealed class DeletionLogItem(DeletionEntry entry)
{
    public DeletionEntry Entry { get; } = entry;
    public DeletionKind Kind => Entry.Kind;
    public SlotState Slot { get; set; } = SlotState.Pending;
}

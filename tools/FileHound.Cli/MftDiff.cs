using System.Buffers.Binary;
using System.Runtime.InteropServices;
using FileHound.Core.Index;
using FileHound.Indexing;
using FileHound.Indexing.Interop;
using FileHound.Indexing.Ntfs;

/// <summary>
/// Diagnostics (elevated): compares the enumeration index with the FSCTL_GET_NTFS_FILE_RECORD index record by record
/// and classifies every difference by re-fetching and inspecting the raw FILE record.
/// </summary>
internal static unsafe class MftDiff
{
    public static int Run(char letter, string reportPath)
    {
        var lines = new List<string> { $"mft-diff {letter}: {DateTime.Now:yyyy-MM-dd HH:mm:ss}" };
        void Say(string s) { lines.Add(s); File.WriteAllLines(reportPath, lines); }
        try
        {
            var drive = DriveDiscovery.GetDrives().Single(d => d.Letter == letter);
            var en = new MftScanner { PreferRaw = false, PreferFileRecord = false }.Scan(drive, [], null, CancellationToken.None);
            var fr = new MftScanner { PreferRaw = false, PreferFileRecord = true }.Scan(drive, [], null, CancellationToken.None);
            Say($"enumeration live={en.LiveCount:N0}  file-record live={fr.LiveCount:N0}");

            var reasons = new Dictionary<string, int>();
            var examples = new Dictionary<string, List<string>>();
            void Count(string reason, string example)
            {
                reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
                if (!examples.TryGetValue(reason, out var l)) examples[reason] = l = [];
                if (l.Count < 6) l.Add(example);
            }

            using var h = Kernel32.OpenVolume(letter);
            int recordSize = 1024;
            byte* vd = stackalloc byte[128];
            if (Kernel32.DeviceIoControl(h, 0x00090064, null, 0, vd, 128, out _, 0)) recordSize = (int)*(uint*)(vd + 48);
            var buf = new byte[12 + recordSize];

            int differentPath = 0;
            for (int e = 1; e < en.Count; e++)
            {
                if (!en.IsLive(e)) continue;
                long rec = en.RecordOf(e);
                int f = fr.FindByRecord(rec);
                if (f > 0 && fr.IsLive(f))
                {
                    if (!PathBuilder.GetFullPath(fr, f).Equals(PathBuilder.GetFullPath(en, e), StringComparison.OrdinalIgnoreCase))
                    {
                        differentPath++;
                        Count("same record, different path", $"{PathBuilder.GetFullPath(en, e)}  vs  {PathBuilder.GetFullPath(fr, f)}");
                    }
                    continue;
                }
                string path = PathBuilder.GetFullPath(en, e);
                // Missing: is the parent missing too (cascade), or is this record itself rejected?
                long parentRec = en.RecordOf(en.Parent(e));
                int fp = en.Parent(e) == 0 ? 0 : fr.FindByRecord(parentRec);
                if (fp < 0) { Count("parent folder missing in file-record index", path); continue; }
                Count(Classify(h, rec, buf, recordSize), $"{path}  (record {rec})");
            }
            Say($"records found under a different path: {differentPath:N0}");
            foreach (var (reason, n) in reasons.OrderByDescending(kv => kv.Value))
            {
                Say($"{n,9:N0}  {reason}");
                foreach (var ex in examples[reason]) Say($"            {ex}");
            }
        }
        catch (Exception ex) { Say("exception: " + ex); }
        return 0;
    }

    private static string Classify(Microsoft.Win32.SafeHandles.SafeFileHandle h, long rec, byte[] buf, int recordSize)
    {
        long input = rec;
        fixed (byte* p = buf)
        {
            if (!Kernel32.DeviceIoControl(h, 0x00090068, &input, 8, p, buf.Length, out int ret, 0) || ret < 12)
                return $"fetch failed ({Marshal.GetLastPInvokeError()})";
        }
        long got = (long)(BinaryPrimitives.ReadUInt64LittleEndian(buf) & 0x0000_FFFF_FFFF_FFFF);
        if (got != rec) return $"control code returned a lower record (record not in use per MFT bitmap)";
        var record = buf.AsSpan(12, recordSize).ToArray();
        int usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(4));
        int usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(6));
        ushort usn = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(usaOffset));
        var tails = Enumerable.Range(1, usaCount - 1).Select(i => BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(i * 512 - 2))).ToArray();
        var saved = Enumerable.Range(1, usaCount - 1).Select(i => BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(usaOffset + 2 * i))).ToArray();
        if (!MftRecordParser.ApplyFixups(record, acceptAlreadyApplied: true))
            return $"fixup check failed (usaCount={usaCount}, usn={usn:X4}, tails=[{string.Join(",", tails.Select(t => t.ToString("X4")))}], saved=[{string.Join(",", saved.Select(t => t.ToString("X4")))}])";
        if (!MftRecordParser.TryParse(record, out var r)) return "parse rejected (malformed attribute)";
        if (!r.InUse) return "record header says not in use";
        if (r.BaseRecord != 0) return "is an extension record";
        if (!r.HasName) return $"no usable $FILE_NAME (attribute list={r.HasAttributeList})";
        return $"parsed fine but absent (name={r.Name.ToString()}, parent={r.ParentRecord}, attrList={r.HasAttributeList}, dir={r.IsDirectory})";
    }
}

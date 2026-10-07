# Raw $MFT Reader — Design Spec

**Date:** 2026-10-07 · **Status:** Approved · **Extends:** architecture spec §7.2 and the §10 roadmap item "Raw $MFT reader"

## Problem
Turbo indexing of C: (5.07M entries) spends 77.5 s cold / 9 s warm in `FSCTL_ENUM_USN_DATA`, then a separate
metadata fill (19 s warm, ~45 s cold) because the enumeration returns no sizes or dates. FileHound's own
processing accounts for only ~1.1 s (measured with `turbo-bench`).

## Goal
One sequential pass over the raw `$MFT` yields names, parents, attributes, sizes and modified times.
- Cold first scan of C: substantially faster than 77.5 s + fill. Warm scan ≤ the current 9 s, with **no** separate fill.
- Results are equivalent to the current path: the entry count is within 1%, and sampled sizes equal `FileInfo.Length`.
- No regressions: anything unusual falls back to the existing `FSCTL_ENUM_USN_DATA` scanner automatically.

## Design
### Locating the MFT
1. `FSCTL_GET_NTFS_VOLUME_DATA` gives:
   - `BytesPerSector` @40
   - `BytesPerCluster` @44
   - `BytesPerFileRecordSegment` @48
   - `MftValidDataLength` @56
   - `MftStartLcn` @64
2. Read record 0 at `MftStartLcn × BytesPerCluster`, apply fixups, and find the unnamed non-resident `$DATA` attribute (0x80). Decode its data runs into extents `(Vcn, Lcn, ClusterCount)`.
3. **Fallback triggers.** `NotSupportedException` is raised, and the scan falls back to enumeration, if any of these hold:
   - the record size is greater than the cluster size, or is not a power of two;
   - record 0 has an `$ATTRIBUTE_LIST`, meaning a fragmented MFT whose runs span extension records;
   - the signature or fixups are invalid;
   - no unnamed `$DATA` attribute is found.

### Reading
- **Order:** read in VCN order, bounded by `MftValidDataLength`, with `ReadFile` on the volume handle.
- **Buffers:** 4 MB, aligned to 4096, and always a whole number of clusters.
- **Pipelining:** a reader thread fills buffers into a bounded channel with capacity 3. The calling thread parses them, so I/O and parsing overlap.
- **Sparse runs:** these contain no records and are skipped.
- **Record numbers:** a record's number is its byte offset within the `$MFT` stream divided by the record size.

### Parsing a FILE record (`MftRecordParser`)
- **Header and fixups:**
  - The record must start with the signature `FILE`.
  - Fixups: the update sequence array sits at offset @4 with its count at @6. For each 512-byte stride, the last two bytes must equal the update sequence number; they are then replaced with the saved value. On a mismatch the record is torn and is skipped.
- **Header fields:**
  - flags @22: 0x1 = in use, 0x2 = directory;
  - first attribute @20;
  - bytes in use @24;
  - base record @32 (low 48 bits; 0 means this is a base record).
- **Attributes** are walked until type `0xFFFFFFFF`, with every length bounds-checked:
  - `0x10 $STANDARD_INFORMATION` (resident): modified FILETIME @8 and file attributes @32.
  - `0x30 $FILE_NAME` (resident):
    - fields: parent FRN @0, name length @64, namespace @65, UTF-16 name @66;
    - namespace 2 (DOS) is ignored; the first other name wins, matching enumeration's one-name behaviour.
  - `0x80 $DATA`, only when unnamed:
    - resident: the value length is the size;
    - non-resident: the data size @48, taken only when the start VCN is 0;
    - named streams are ignored.
  - `0x20 $ATTRIBUTE_LIST`: noted.

### Building
- Base records that are in use and have a name are passed to `VolumeIndexBuilder.AddRecord(rec, parent, name, flags | MetadataKnown, size, mtime)`. Records with a reserved number (< 16, apart from the root) and skipped root names, which are the same as today, are dropped.
- Extension records (base ≠ 0) contribute their name and/or unnamed `$DATA` size to a small side table keyed by base record.
- Base records that are missing a name or a size (because they have an attribute list) are deferred and completed from that side table at the end.
- The volume journal ID and `NextUsn` are queried **before** reading, so the USN updater replays every change made during the scan. Torn or changing records are healed by that replay.

### Integration
- `MftScanner.Scan` tries `RawMftReader` first and falls back to the existing enumeration on `NotSupportedException`, `InvalidDataException`, `IOException` or `Win32Exception`. The fallback is logged.
- `IndexManager.RunTurboAsync` sets `needFill = MetadataFiller.HasIncompleteMetadata(v)`, using `onlyIncomplete: true`. With the raw reader nothing is incomplete, so the fill is skipped; the enumeration path still fills everything.
- `turbo-bench` reports raw vs enumeration timings, entry counts and a 2,000-file size-agreement sample.

## Addendum — validation findings (2026-10-07)
- **C: (system volume):** every `ReadFile` on the volume handle fails with Win32 error 50 (`ERROR_NOT_SUPPORTED`), including the boot sector, under every set of open flags, even with correct alignment. Control codes still succeed. BitLocker is off; Bitdefender is active and is the likely blocker of raw disk reads.
  - **Added tier:** `FileRecordMftReader`. It calls `FSCTL_GET_NTFS_FILE_RECORD` on each range from the top down, one call per in-use record, with parallel workers that each have their own handle.
  - **Measured:** 5.03M entries in 7.8 s with complete metadata, against 9 s + 20 s for enumeration plus fill. With one thread it takes 21.7 s.
- **Fixups on in-memory records:** records returned by the control code are NTFS's in-memory copies, which are already fixed up. Their update sequence array can be stale when a record has changed in memory but not been flushed. Validating those records against it rejected about 1–2k recently changed records and orphaned about 27k descendants, the 0.9% gap against enumeration. Such records are now accepted as returned. Torn-write checks remain for raw disk reads.
  **Verified after the fix (mft-diff on C:):** the file-record tier and enumeration now both report exactly 5,071,490 live entries. The only other difference is one temporary `.tmp` file created between the two scans.
- **M: (HDD, 2.5M entries):** the raw tier matches enumeration exactly on entry count (100%), finds 1,999 of 2,000 paths (the one miss is a temporary build file), and matches 2,000 of 2,000 sizes. It took 25.6 s cold and 9.5 s warm.
- **F: (8 TB):** the `$MFT` there has an attribute list, so the raw tier steps aside and the file-record tier handles it. Supporting raw reads of attribute-list MFTs is future work.
- **Hard links:** for about 171k records on C:, enumeration and the parser report different link names for the same record. Both index one name per record; indexing every link name is future work.

## Testing
- **Unit tests** on synthetic FILE records built by a test helper:
  - the fixups round-trip;
  - a torn record is rejected;
  - a record not in use;
  - the directory flag;
  - a DOS name listed first while the Win32 name wins;
  - resident and non-resident `$DATA`;
  - a named stream is ignored;
  - an extension record;
  - an attribute list.
- **Data-run decoding:** positive, negative and sparse runs.
- **Reader core** over a synthetic MFT buffer: root, a folder, a file, an extension-record merge, a skipped metafile, and a record not in use.
- **Elevated real-drive check** via `turbo-bench`: requires one UAC approval.

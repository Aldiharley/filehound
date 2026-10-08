# Deleted-file recovery for FileHound — research report

**Date:** 2026-10-08 · **Status:** research, no code · **Scope:** consumer undelete on the user's own NTFS volumes, Windows 11, .NET 10 / WPF

FileHound already has the hard parts of an NTFS reader: FILE-record parsing with fixups (`MftRecordParser`), `$STANDARD_INFORMATION`/`$FILE_NAME`/unnamed `$DATA` with `DataRuns.Decode`, a raw `$MFT` tier (`RawMftReader`, blocked on C: by Win32 error 50), a per-record tier over `FSCTL_GET_NTFS_FILE_RECORD` (`FileRecordMftReader`), a USN updater and a struct-of-arrays index. This report says what has to be added for recovery, with exact layouts, control codes and decision rules, and which parts are blocked by the raw-read problem.

**Recommended shape of the feature (summary).** Four sources, in increasing cost and privilege:

| Source | Needs admin | Needs raw reads | Names/paths | Typical hit rate |
|---|---|---|---|---|
| 1. Recycle Bin (`$I`/`$R`) | no (own bin) / yes (others) | no | full path | the common case |
| 2. "Recently deleted" from the USN journal + MFT probe | yes (journal) | no for the list, yes for the bytes | full path | always lists, recovers only with 3 |
| 3. MFT undelete (not-in-use FILE records + `$Bitmap`) | yes | yes (`$MFT` + data clusters) | name + reconstructed path | good if minutes–days old and not on a busy SSD |
| 4. Carving free space by signature | yes | yes | none (type + size) | last resort, slow |

Plus Volume Shadow Copies as an opportunistic fifth source (previous versions by path) when snapshots exist.

---

## 1. NTFS undelete via the MFT

### 1.1 What NTFS does on delete

For a plain delete (not a move to the Recycle Bin, which is a rename; see §4 and §7):

1. The `$FILE_NAME` entry is removed from the parent directory's `$I30` index. Entries in the middle of a B-tree node are overwritten when the remaining entries shift; an entry at the end of a node may survive in node slack ([TSK NTFS File Recovery](https://wiki.sleuthkit.org/NTFS-File-Recovery/)). Directory slack is therefore a poor source of names; the MFT record itself is the good one.
2. In the FILE record: flag bit `0x01` (in use) at offset `0x16` is cleared, and the **sequence number** at `0x10` is incremented at deletion time (skips 0, wraps `0xFFFF → 1`) ([linux-ntfs: file record](https://flatcap.github.io/linux-ntfs/ntfs/concepts/file_record.html), [ntfs-3g mft.c](https://ognproject.evlt.uma.es/gitea/opengnsys/ntfs-3g/src/branch/edge/libntfs-3g/mft.c)). Everything else in the record — `$STANDARD_INFORMATION`, all `$FILE_NAME`s, the `$DATA` header with its resident value or data runs — is left intact until the record is reallocated.
3. The record's bit in `$MFT:$BITMAP` is cleared; the file's clusters are cleared in `$Bitmap` (file record 6).
4. `$LogFile` gets redo/undo records for the metadata changes (a circular log; it never holds non-resident data, so it is only a metadata history and not worth implementing here).
5. The USN journal gets a record with `USN_REASON_FILE_DELETE | USN_REASON_CLOSE` (`0x80000200`) carrying the name, the file reference (with sequence number), the parent reference and a timestamp ([USN_RECORD_V2](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-usn_record_v2)).
6. On SSDs, NTFS issues a TRIM for the freed clusters (unless `fsutil behavior query DisableDeleteNotify` is 1). Most drives then return zeros for those LBAs within seconds. This, not record reuse, is the main reason MFT undelete fails on a modern laptop; show it as a warning rather than pretending.

### 1.2 FILE record and attribute offsets that recovery needs

FILE record header (little-endian; FileHound already parses most of this):

| Offset | Size | Field |
|---|---|---|
| 0x00 | 4 | `"FILE"` |
| 0x04 / 0x06 | 2 / 2 | update sequence array offset / count |
| 0x08 | 8 | `$LogFile` LSN |
| 0x10 | 2 | **sequence number** |
| 0x12 | 2 | hard-link count |
| 0x14 | 2 | first attribute offset |
| 0x16 | 2 | flags: `0x01` in use, `0x02` directory |
| 0x18 / 0x1C | 4 / 4 | bytes used / allocated |
| 0x20 | 8 | base record reference (0 for base records) |
| 0x2C | 4 | this record's number (XP+) — useful as a sanity check when carving stray records |

Attribute header: type @0 (4), length @4 (4), non-resident @8 (1), name length @9 (1), name offset @0xA (2), **flags @0xC (2: `0x0001` compressed, `0x4000` encrypted, `0x8000` sparse)**, id @0xE (2). Resident: value length @0x10 (4), value offset @0x14 (2). Non-resident: start VCN @0x10, last VCN @0x18, run-list offset @0x20 (2), compression unit @0x22 (2), allocated @0x28, real size @0x30, initialized size @0x38.

`$FILE_NAME` (0x30) body ([linux-ntfs](https://flatcap.github.io/linux-ntfs/ntfs/attributes/file_name.html)): **parent reference @0x00 (8: low 48 bits record number, high 16 bits parent's sequence number)**, C/A/M/R times @0x08–0x27, allocated size @0x28, real size @0x30, flags @0x38, name length @0x40 (1), namespace @0x41 (1: 0 POSIX, 1 Win32, 2 DOS, 3 Win32+DOS), UTF-16 name @0x42.

### 1.3 Enumerating not-in-use records

`FSCTL_GET_NTFS_FILE_RECORD` (`0x00090068`) "always returns a file record that is in use": asking for record N returns the nearest in-use record ≤ N ([MS docs](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_get_ntfs_file_record)). So the file-record tier cannot see deleted records. It can, however, see **gaps**: requesting N and getting back M < N proves that M+1…N are not in use. That is a cheap "is this deleted record still unreused?" oracle (used in §7) even when the bytes are unreadable.

Ways to get the bytes of a not-in-use record:

| Method | Privilege | Works when raw reads blocked? |
|---|---|---|
| Raw `$MFT` read on `\\.\X:` (existing `RawMftReader`; extents from record 0's `$DATA` runs, located via `FSCTL_GET_NTFS_VOLUME_DATA` `MftStartLcn`@64, `BytesPerFileRecordSegment`@48, `MftValidDataLength`@56) | admin | no (error 50 on C:) |
| `\\.\PhysicalDriveN` + volume offset (§3.2) | admin | unknown, probably the same filter |
| Shadow-copy device `\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopyN` opened as a block device (§3.3) | admin | unknown — must test |
| Opening `X:\$MFT` by name | — | NTFS refuses data access to `$MFT` through the file API; not an option |

The records come from the same parser as today; just stop dropping records whose in-use flag is clear. Pre-filter: signature `"FILE"`, fixups valid, base record (ref @0x20 == 0), has at least one `$FILE_NAME`, record number < 16 skipped. Extension records of deleted files (attribute lists) are rare for user files and can be ignored in v1.

### 1.4 Recoverability decision rules

For a candidate deleted record `R` with unnamed `$DATA` `D`:

1. **Resident** (`D.nonResident == 0`): the content is inside the record → recoverable with certainty as long as the record is intact. (Files up to roughly 700 bytes in 1 KB records.)
2. **Encrypted** (`D.flags & 0x4000`, EFS): mark *not recoverable* (no keys). ntfsundelete does the same and reports 0% ([ntfsundelete(8)](https://man.cx/ntfsundelete(8))).
3. **Compressed** (`D.flags & 0x0001`): runs are in compression units (16 clusters) with LZNT1 blocks; recoverable only with an LZNT1 decoder (DiscUtils has one under MIT, see §10). Mark "needs decompression" in v1 or skip.
4. **Sparse** (`0x8000`) runs with LCN = 0 in the run list are zero-filled; fine.
5. **Non-resident**: decode runs → list of (LCN, count). Load `$Bitmap` (§2). Count clusters whose bit is 1 (allocated to a live file now) → **overwritten**. Report `percentRecoverable = freeClusters / totalClusters` (ntfsundelete's `-p`). Anything < 100% is "partial".
6. **Free but overwritten by a short-lived file** (ntfsundelete's caveat) and **TRIM**: read the first cluster and check it. Rules: (a) if the extension is in the signature table (§5) the first bytes must match; (b) if the first 4 KB is all zeros and `D.initializedSize > 0`, mark "probably trimmed/zeroed"; (c) for structured types run the §5 validator over the reassembled stream and downgrade to "damaged" on failure. Recuva's "Excellent/Poor/Very poor/Unrecoverable" and R-Studio's "recovery chances" are exactly this: cluster-bitmap check plus content sanity, and both vendors say it is an estimate, not a guarantee.
7. **Size**: use `D.realSize` (@0x30), fall back to `$FILE_NAME` real size @0x30, cap by allocated size; ntfsundelete uses the largest size it finds.
8. **Truncate on read**: clusters past `initializedSize` are zeros; do not read them.

### 1.5 Reconstructing the original path

`$FILE_NAME.parentRef` = (parent record number, parent sequence number). Resolve:

- Parent record in use and `parent.seq == ref.seq` → the directory still exists: path = live path (FileHound's index has it) + `\` + name.
- Parent record **not in use** and `parent.seq == ref.seq + 1`? No — sequence numbers increment on delete, so for a deleted parent whose record is unreused, the record's current seq is `ref.seq + 1` (ntfs-3g increments at free). Compare `parent.seq == ref.seq` **or** `ref.seq + 1` (with wrap) and the record not in use → the parent directory was deleted too; recurse to rebuild the deleted folder chain (bounded depth, stop at record 5 = root, or at a mismatch).
- Parent in use but `parent.seq != ref.seq` → the parent record was reused by another directory. The Sleuth Kit calls these **orphan files** and lists them in a virtual `$OrphanFiles` directory; show them as `<unknown folder>\name` and, if the USN journal still has a `RENAME`/`FILE_CREATE` record for that FRN, use the parent FRN recorded there (§7).
- Prefer the Win32 or Win32+DOS namespace name (1 or 3) over DOS (2), as the parser does today.

### 1.6 How long deleted records survive

NTFS allocates a new MFT record from the first free bit in `$MFT:$BITMAP` from a hint, so freed slots are refilled by the next file creations (Carrier, *File System Forensic Analysis*, describes a first-available strategy for MFT entries; cluster allocation on Windows 10 favours filling holes near the start of the volume rather than the end — [Karresand et al., DFRWS 2019](https://dfrws.org/presentation/an-empirical-study-of-the-ntfs-cluster-allocation-behavior-over-time/)). Practical consequence: on an idle data drive a deleted record can survive for months; on C: with browser caches and temp files it can be gone in minutes, and its clusters in seconds on a TRIM-enabled SSD. The sequence number is the only thing that tells a reused slot apart from the original, which is why every reference FileHound keeps for recovery must carry the 16-bit sequence number and not just the 48-bit record number.

---

## 2. `$Bitmap` without raw reads: `FSCTL_GET_VOLUME_BITMAP`

Control code `0x0009006F` (`CTL_CODE(FILE_DEVICE_FILE_SYSTEM, 27, METHOD_NEITHER, FILE_ANY_ACCESS)`) ([MS docs](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_get_volume_bitmap)).

```c
typedef struct { LARGE_INTEGER StartingLcn; } STARTING_LCN_INPUT_BUFFER;          // 8 bytes
typedef struct { LARGE_INTEGER StartingLcn; ULONG Flags; } STARTING_LCN_INPUT_BUFFER_EX; // 12 bytes, Win10+
#define GET_VOLUME_BITMAP_FLAG_MASK_METADATA 0x00000001   // bitmap "includes metadata" (ntdoc)
typedef struct {
    LARGE_INTEGER StartingLcn;   // @0  rounded-down start actually used
    LARGE_INTEGER BitmapSize;    // @8  number of clusters described, from StartingLcn to the END of the volume
    BYTE          Buffer[1];     // @16 bit i (LSB first) = cluster StartingLcn+i; 1 = allocated, 0 = free
} VOLUME_BITMAP_BUFFER;
```

Rules and caveats:

- Input `StartingLcn` must be a multiple of 8 and is rounded down further by the file system; on Vista+ the returned bitmap is page-aligned, so expect rounding to a multiple of 32,768 LCNs. Always use the returned `StartingLcn`.
- **Buffer-size strategy.** `BitmapSize` counts clusters *to the end of the volume*, so a complete read needs `16 + ceil(TotalClusters/8)` bytes (C: with 4 KB clusters and 1 TB → ~32 MB). Either (a) call once with a 20-byte buffer, expect `ERROR_MORE_DATA` (234) — the header is still filled in, so size the real buffer from `BitmapSize` and call again (the ReactOS defrag code does this), or (b) stream in chunks: a fixed 8 MB buffer, loop while the call returns `ERROR_MORE_DATA`, treating the returned bytes (`bytesReturned - 16`) as valid bits and advancing `StartingLcn += validBits` (rounded to 8). Chunking keeps memory flat and lets you check a deleted file's runs lazily.
- `ERROR_MORE_DATA` is a *success with partial data*, not a failure. Error 1784 (`ERROR_INVALID_USER_BUFFER`) means the output pointer/length is bad.
- The bitmap is a snapshot: "can be incorrect as soon as it has been read if the volume has write activity". For an overwrite check that is fine; re-read before the final copy if the scan took long.
- Privilege: the docs say "opened with any access" but "only Administrators can open Volume handles"; an OSR report shows `FSCTL_GET_VOLUME_BITMAP` returning `STATUS_ACCESS_DENIED` on a `FILE_READ_ATTRIBUTES`-only handle while `FSCTL_GET_NTFS_VOLUME_DATA` worked on the same handle ([OSR thread](https://community.osr.com/t/fsctl-get-volume-bitmap-giving-status-access-denied/27107)). Plan on the elevated `GENERIC_READ` handle FileHound already uses for `FSCTL_GET_NTFS_FILE_RECORD`.
- **Local probe (this PC, unelevated, inside the tool sandbox):** every NTFS FSCTL (`GET_NTFS_VOLUME_DATA`, `GET_VOLUME_BITMAP`, `GET_NTFS_FILE_RECORD`, `QUERY_USN_JOURNAL`) returned error 1 (`ERROR_INVALID_FUNCTION`) on zero-access and `FILE_READ_ATTRIBUTES` handles for C: and M:, while `IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS` succeeded (C: = disk 0 @ 122,683,392; M: = disk 5 @ 16 MiB). Because `GET_NTFS_VOLUME_DATA` is known to work on such handles, this looks like a sandbox artefact; it was not possible to repeat the probe outside the sandbox. Add an `--probe` to `turbo-bench` that reports each control code's result elevated and unelevated before designing around it.
- It is not needed for the unprivileged list (§7); it is needed only to score recoverability and to restrict carving to free space.

Alternative when raw reads work anyway: `$Bitmap` is file record 6; its unnamed `$DATA` runs can be read directly. Same bit layout, no 16-byte header.

---

## 3. Reading free clusters when raw volume reads are blocked

Observed state (raw-MFT design addendum): on C:, every `ReadFile` on the volume handle fails with `ERROR_NOT_SUPPORTED` (50) under every flag combination, including the boot sector; control codes succeed; M: reads fine. BitLocker is off; Bitdefender is active.

### 3.1 Same volume, different door

Things worth one test each, none likely to help by itself: open by `\\?\Volume{GUID}` or `\\?\GLOBALROOT\Device\HarddiskVolumeN` instead of `\\.\C:`; `FILE_FLAG_NO_BUFFERING` with 4 KB-aligned buffer and offset; `FSCTL_ALLOW_EXTENDED_DASD_IO` (`0x00090083`); `FILE_READ_DATA` instead of `GENERIC_READ`. A filter that completes read IRPs on the volume device with `STATUS_NOT_SUPPORTED` will not care which name opened it. Error 50 is the Win32 translation of `STATUS_NOT_SUPPORTED`/`STATUS_INVALID_DEVICE_REQUEST`, which is what a minifilter or a volume filter would return if it refuses the IRP. Diagnose once with Process Monitor (the failing `IRP_MJ_READ` on `C:` shows the result) and `fltmc instances -v C:` (which filters are attached and at what altitude). The design addendum guessed Bitdefender; the test is to add `FileHound.exe` (and `turbo-bench`) to **Advanced Threat Defense → Settings → Manage exceptions** — ATD exclusions are per executable, not per folder ([Bitdefender KB 130706](https://www.bitdefender.com/consumer/support/answer/130706/)) — and retry. No Bitdefender document names "raw disk read" as a monitored behaviour; ESET does document a HIPS operation "Direct access to disk" that covers reads and writes and is meant for backup/partition tools ([ESET HIPS rule settings](https://help.eset.com/eav/19/en-US/idh_hips_editor_single_rule.html)); Kaspersky's Application Control documents files/registry/network only. Expect the same behaviour from other suites on other users' machines; the feature must degrade gracefully (§7 list without bytes, Recycle Bin, VSS).

A second cheap test: run Microsoft's own tool, `winfr C: D:\out /regular /n \Users\<you>\Desktop\<deleted file>`. winfr needs admin and reads the volume raw. If it also fails on C:, the block is global and the recommended user action is the AV exclusion; if it works, the filter is targeting FileHound specifically (behavioural score) and an exclusion will fix it.

### 3.2 `\\.\PhysicalDriveN` + partition offset

`IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS` (`0x00560000`, no input) on the volume handle returns

```c
typedef struct { DWORD DiskNumber; LARGE_INTEGER StartingOffset; LARGE_INTEGER ExtentLength; } DISK_EXTENT; // 24 bytes, @8 in the output
typedef struct { DWORD NumberOfDiskExtents; DISK_EXTENT Extents[1]; } VOLUME_DISK_EXTENTS;           // extents @8 (alignment)
```

(`ERROR_MORE_DATA` if more than one extent fits; multi-extent means dynamic disk/Storage Spaces — refuse.) Then open `\\.\PhysicalDrive<DiskNumber>` with `GENERIC_READ | FILE_SHARE_READ | FILE_SHARE_WRITE`, `FILE_FLAG_NO_BUFFERING`, and read at `StartingOffset + LCN × BytesPerCluster`, sector-aligned. Admin required. It worked unelevated for the IOCTL in the probe above. Caveats: (1) a **BitLocker** volume returns ciphertext at the disk level — detect it before trusting the bytes: the first sector at `StartingOffset` must carry `"NTFS    "` at offset 3 (a BitLocker volume shows `-FVE-FS-`), or query `Win32_EncryptableVolume.ProtectionStatus` in `root\CIMV2\Security\MicrosoftVolumeEncryption`; (2) the same security filter may sit on the disk stack (ESET's rule explicitly covers it); (3) 4Kn disks need 4 KB alignment, which the cluster-granular reads already satisfy.

### 3.3 Volume Shadow Copy as a read path

A VSS snapshot is exposed as a read-only pseudo-volume `\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopyN` ([adioltean, MS archive](https://learn.microsoft.com/archive/blogs/adioltean/creating-shadow-copies-from-the-command-line)). File-level access is `<device>\path\file` (works with .NET `File.OpenRead` because of the `\\?\` prefix). Block-level access is the device opened without a trailing backslash: forensic practice images shadow copies with `dd if=\\.\HarddiskVolumeShadowCopyN` ([Forensic Focus](https://www.forensicfocus.com/forums/general/dd-windows-forensic-acquisition/)), so the device supports raw reads and the existing `RawMftReader` could run against it unchanged (`FSCTL_GET_NTFS_VOLUME_DATA` works on the snapshot too). Whether the filter that blocks `\\.\C:` also blocks the shadow device is an open question that only a test answers; the volsnap device is a different device object, which is the one real hope here. Creating a snapshot: `Win32_ShadowCopy.Create("C:\\", "ClientAccessible")` ([MS docs](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/vsswmi/create-method-in-class-win32-shadowcopy)) or `IVssBackupComponents` via AlphaVSS (MIT) — admin only; return 1 = access denied. Two costs: a snapshot **writes to the source volume** (the diff area in `System Volume Information`), which violates the §8 rule and may overwrite free clusters, so it must be opt-in with that warning; and it is point-in-time, which is also a benefit (`$MFT` and `$Bitmap` stop moving under you). Prefer existing snapshots (§6) and offer creation as "Freeze the drive now (uses some disk space)".

### 3.4 What winfr does

Closed source; from Microsoft's page ([Recover lost files on Windows](https://support.microsoft.com/en-us/windows/recover-lost-files-on-windows-10-61f5b28a-f5b8-3cc2-0f8e-a63cb4e1d4c4)): `/regular` = MFT-based undelete on healthy NTFS (names and folders preserved), `/extensive` = segment + signature; `/ntfs` = fast MFT mode; `/segment` = "recovery option for NTFS drives using file record segments" (scans for FILE records, which also finds records whose `$MFT` extent mapping is gone); `/signature` = carving, groups listed by `winfr /#`: ASF (wma, wmv, asf), JPEG (jpg, jpeg, jpe, jif, jfif, jfi), MP3, MPEG (mpeg, mp4, mpg, m4a, m4v, m4b, m4r, mov, 3gp, qt), PDF, PNG, ZIP (zip, docx, xlsx, pptx, odt, ods, odp, odg, odi, odf, odc, odm, ott, otg, otp, ots, otc, oti, otf, oth). Other switches worth copying: `/u` (recover undeleted files — i.e. Recycle Bin contents), `/k` (system files), `/g` (files without a primary data stream), `/e` (default filter that hides noise types), `/o:<a|n|b>` overwrite policy, output into `Recovery_<date time>` on the destination. It requires admin and "The source and destination drives must be different."

---

## 4. Recycle Bin recovery

Layout: `X:\$Recycle.Bin\<user SID>\` on every NTFS volume (hidden+system; ACL grants the owner SID, SYSTEM and Administrators, so listing other users' folders needs elevation; the current user's folder is readable unelevated). Each deleted item is a pair `$I<6 base-36 chars>.<ext>` (metadata) and `$R<same chars>.<ext>` (the data; for a folder, `$R…` is the directory with its whole tree inside, and `$I` describes the folder).

`$I` version 2 (Windows 10+), verified on this machine (a 200-byte `$I` = 28 + 2×86):

| Offset | Size | Field |
|---|---|---|
| 0x00 | 8 | version = 2 (1 on Vista–8.1) |
| 0x08 | 8 | original size in bytes (for a folder: total size) |
| 0x10 | 8 | deletion time, FILETIME UTC |
| 0x18 | 4 | path length in UTF-16 code units, **including the NUL terminator** |
| 0x1C | 2×len | original full path, UTF-16LE, NUL-terminated |

Version 1 is 544 bytes: same first 24 bytes, then a fixed 520-byte (260-char) NUL-padded path at 0x18 ([libyal dtformats: Windows Recycle.Bin file formats](https://github.com/libyal/dtformats/blob/main/documentation/Windows%20Recycle.Bin%20file%20formats.asciidoc); [Champlain forensics blog](https://leahycenterblog.champlain.edu/?p=1250)). XP's `INFO2` is out of scope.

Restoring = `MoveFileEx($R…, originalPath)` without replace, recreating missing parent folders, then deleting the `$I`; if the target exists, offer "keep both" like winfr's `/o:b`. For the **current user's** bin prefer the Shell: `Shell.Application.NameSpace(10)` (ssfBITBUCKET) items expose the original location and an "undelete"/`ESTORE` verb, which keeps Explorer's view consistent. For **other users'** bins (elevated) the `$R` move is the only way; map SID → account name with `LookupAccountSid` for display. Note the per-volume policy "Don't move files to the Recycle Bin" (`NukeOnDelete`) and that items larger than the bin's quota are deleted directly — both show up as real deletes in §7, not here. FileHound's USN updater already sees a recycle as `RENAME_OLD_NAME`/`RENAME_NEW_NAME` with the new parent under `$Recycle.Bin\<SID>`; tag those entries "In Recycle Bin" instead of "deleted".

---

## 5. File carving for files whose MFT record is gone

### 5.1 Scan strategy

- **Scan only free clusters** (from §2's bitmap) and **only at cluster boundaries**: NTFS data starts on a cluster boundary, so a signature at a non-boundary is either slack or an embedded object. That is 8× fewer checks than sector scanning and removes most false positives (an embedded JPEG thumbnail inside a PSD never starts a cluster). PhotoRec does the same ("reads the media block by block … checks each block against a built-in signature database") ([cgsecurity: PhotoRec](https://www.cgsecurity.org/wiki/PhotoRec)).
- Skip files that would be MFT-resident (< ~700 B): they come from §1, not from carving.
- Read in large aligned chunks (the 4 MB pipelined reader already exists); dispatch on the first byte (256-entry table) and only then compare full magics.
- When a validator confirms a file, skip its clusters; when a new signature appears inside a file being assembled, stop the current file and validate what you have (PhotoRec's rule), then "re-check earlier blocks where a signature was found but the file was too small" to catch simple fragmentation.
- Fragmentation reality: Garfinkel's survey of ~300 second-hand drives found under 10% of files fragmented overall but much higher rates for the types people want back (PST ~42%, Word ~17%, JPEG ~16%), with two fragments the dominant case ([summary in Pal/Sencar/Memon, DFRWS 2008](https://dfrws.org/sites/default/files/session-files/2008_USA_paper-detecting_file_fragmentation_point_using_sequential_hypothesis_testing.pdf); original: [Garfinkel, DFRWS 2007](https://dfrws.org/presentation/carving-contiguous-and-fragmented-files-with-object-validation/)). Contiguous carving recovers the first fragment only; a v2 could add Garfinkel's bifragment gap carving (try gap sizes between a validated head and a footer, validate each candidate) for JPEG/PNG/ZIP where validation is strong.

### 5.2 Signature table (header @ offset unless noted; end-detection; sensible cap)

| Type | Header | End / size rule | Cap |
|---|---|---|---|
| JPEG | `FF D8 FF` + `E0/E1/E2/E8/DB/FE` | walk markers (`FF xx` + BE length) to SOS, then scan entropy data for `FF D9`; skip embedded EXIF thumbnail (its own `FF D8`/`FF D9`) | 64 MB |
| PNG | `89 50 4E 47 0D 0A 1A 0A` | chunk walk (BE len + type + data + CRC32) until `IEND`; verify CRCs (`System.IO.Hashing.Crc32`) | 256 MB |
| GIF | `GIF87a` / `GIF89a` | block walk to trailer `3B` | 64 MB |
| BMP | `BM` | LE u32 size @2 | 256 MB |
| TIFF / DNG / CR2 / NEF | `49 49 2A 00` / `4D 4D 00 2A` | IFD walk (strip offsets+counts) ⇒ max extent | 512 MB |
| WebP | `RIFF`…`WEBP` | LE u32 @4 + 8 | 64 MB |
| HEIC / AVIF / MP4 / M4A / MOV / 3GP | `ftyp` @4 (brand `heic`,`avif`,`isom`,`mp42`,`M4A `,`qt  `…) | box walk: BE u32 size (1 → u64 @8, 0 → to end) + type; stop after last top-level box (`moov`/`mdat`) | 64 GB |
| MKV / WebM | `1A 45 DF A3` | EBML: parse Segment size (vint) | 64 GB |
| AVI | `RIFF`…`AVI ` | LE u32 @4 + 8 (≥ 4 GB AVIs use `AVIX` chunks: continue while next chunk is `RIFF`) | 64 GB |
| WAV | `RIFF`…`WAVE` | LE u32 @4 + 8 | 4 GB |
| FLAC | `fLaC` | metadata block walk, then frames; cap | 1 GB |
| OGG / OPUS | `OggS` | page walk (segment table) until a page with EOS flag | 1 GB |
| MP3 | `ID3` (size = syncsafe u32 @6 + 10) or frame sync `FF Fx/Ex` | walk frames (header → frame length) while consecutive headers agree on version/layer/sample rate | 256 MB |
| ASF / WMV / WMA | `30 26 B2 75 8E 66 CF 11 A6 D9 00 AA 00 62 CE 6C` | File Properties object carries file size (u64 @ +40 in that object) | 64 GB |
| FLV | `FLV 01` | tag walk | 4 GB |
| PDF | `%PDF-` | last `%%EOF` (files may have several from incremental updates; take the last within cap, then trim trailing garbage) | 1 GB |
| ZIP / DOCX / XLSX / PPTX / ODF / EPUB / JAR / APK | `50 4B 03 04` | walk local headers (needs sizes or data descriptors) or, simpler, find EOCD `50 4B 05 06` + comment length (ZIP64: `50 4B 06 06`); classify by first entry: `[Content_Types].xml` + `word/`,`xl/`,`ppt/` → docx/xlsx/pptx; `mimetype` → ODF/EPUB | 4 GB |
| 7z | `37 7A BC AF 27 1C` | total = 32 + NextHeaderOffset (u64 @12) + NextHeaderSize (u64 @20) | 64 GB |
| RAR4 / RAR5 | `52 61 72 21 1A 07 00` / `…07 01 00` | block walk | 64 GB |
| GZIP | `1F 8B 08` | inflate to find the end (or cap); ISIZE last 4 bytes | 4 GB |
| XZ | `FD 37 7A 58 5A 00` | footer `59 5A` ("YZ") after stream footer | 4 GB |
| BZ2 | `42 5A 68 31-39` | no length — cap, or decode | 1 GB |
| TAR | `ustar` @257 | header walk (512-byte blocks, size octal) | 64 GB |
| CAB | `MSCF` | LE u32 @8 | 2 GB |
| PE (exe/dll/sys) | `MZ`; `PE\0\0` at LE u32 @0x3C | headers + Σ section raw sizes (overlay lost); classify dll by Characteristics bit 0x2000 | 512 MB |
| OLE2 / CFB (doc, xls, ppt, msi, msg) | `D0 CF 11 E0 A1 B1 1A E1` | sector size 2^(u16 @30); walk FAT to count sectors; classify by root storage CLSID / stream names | 2 GB |
| RTF | `{\rtf1` | brace balance to the final `}` | 256 MB |
| SQLite | `SQLite format 3\0` | page size BE u16 @16 (1 → 65536) × page count BE u32 @28 (valid when u32 @24 == u32 @92) | 16 GB |
| PST | `!BDN` | u64 `ibFileEof` @0xB8 (Unicode PST) | 64 GB |
| LNK | `4C 00 00 00 01 14 02 00 00 00 00 00 C0 00 00 00 00 00 00 46` | structure walk | 1 MB |
| PSD | `8BPS` | section walk | 2 GB |
| ICO / CUR | `00 00 01 00` / `00 00 02 00` + count | directory entries (offset+size) ⇒ max extent | 16 MB |
| TTF / OTF / WOFF | `00 01 00 00` / `OTTO` / `wOFF` | table directory ⇒ max extent / u32 @8 | 64 MB |
| MIDI | `MThd` | chunk walk | 16 MB |
| XML / HTML / TXT / JSON / CSV | `<?xml`, `<!DOCTYPE html`, `<html`, UTF-8 BOM, `{`… | textual; end at first non-text cluster — low confidence, show only with a "text" type and a snippet | 16 MB |

Validator depth is what separates a usable carve from noise: PhotoRec truncates to the header-declared size, discards files smaller than their header claims, and "parses data-stream formats such as MP3 and stops when the stream ends". Implement validators as pure `ReadOnlySpan<byte>` parsers over the reassembled stream so they double as the §1.4 sanity check for MFT undelete. PhotoRec is GPL: use its documented behaviour and its public format page as a checklist, not its code; the Apache-2.0 Scalpel branch in the Sleuth Kit repo and public-domain Foremost ship header/footer tables (`scalpel.conf`, `foremost.conf`) that can be adopted directly.

### 5.3 Throughput and presentation

Carving is sequential-read-bound: NVMe 2–7 GB/s, SATA SSD ~500 MB/s, HDD 100–250 MB/s; with the bitmap restricting the scan to free space, a 1 TB volume at 40% free is ~400 GB → 1–3 min NVMe, ~15 min SATA, ~40 min HDD; the PhotoRec page's caveat about mechanical seek time applies to fragmented retries only. Parsing cost is negligible against I/O if dispatch is by first byte.

Carved files have no names, dates or paths. Present: type (from the validator, including the OOXML subtype), size (parsed), "validated" vs "header only", source LCN, and a preview: images decoded in memory with WPF `BitmapDecoder` (size-capped), text snippet for textual types, duration/dimensions from headers for media, entry list for ZIP/OOXML, first-page text for PDF only if a PDF library is already in the app. Group by type, let the user multi-select and recover to the destination as `<type>_<LCN>.<ext>` (PhotoRec uses `f<offset>.ext`, winfr a file number). De-duplicate against §1: if a carved start LCN equals the first LCN of a deleted record's run list, show the named record instead and drop the carve.

---

## 6. Volume Shadow Copies as a recovery source

Enumerate (all need elevation): `vssadmin list shadows` (parse "Shadow Copy Volume: \\?\GLOBALROOT\Device\HarddiskVolumeShadowCopyN" and "Original Volume"); WMI `root\cimv2:Win32_ShadowCopy` (`ID`, `DeviceObject`, `VolumeName` (`\\?\Volume{GUID}\`), `InstallDate`, `ClientAccessible`, `Persistent`); or `IVssBackupComponents::Query(VSS_OBJECT_SNAPSHOT)` through AlphaVSS. Map `VolumeName` to the drive letter with `GetVolumePathNamesForVolumeName`. Read a previous version of `C:\Users\x\doc.docx` by opening `\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopyN\Users\x\doc.docx` (plain `CreateFile`/`File.OpenRead`; directory enumeration works the same way, which is how Explorer's "Previous Versions" tab is populated). Shadow copies also contain the snapshot-time `$MFT`, so a deleted file's record can be read from the snapshot even after the live record was reused — and if the shadow device accepts raw reads (§3.3), the full §1 pipeline can run on it.

Availability on a default Windows 11 home PC is poor: System Protection is **off by default** on the system drive, so no restore points/shadow copies are created unless the user, an installer, or a backup tool turned it on ([Pureinfotech](https://pureinfotech.com/enable-system-protection-windows-11/), [Windows Central](https://windowscentral.com/software-apps/windows-11/7-features-i-wish-came-enabled-by-default-on-windows-11)). Treat VSS as opportunistic: "N snapshots found (dates)"; when the user looks for a path, check each snapshot newest-first and show "Version from <date>, <size>" rows next to the MFT/Recycle Bin results. Offer "turn on System Protection" as advice, not as part of recovery.

---

## 7. The USN journal as a recovery aid

FileHound already consumes `FSCTL_READ_USN_JOURNAL` (`0x000900BB`) records. Layout of `USN_RECORD_V2` ([MS docs](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-usn_record_v2)): RecordLength u32 @0, Major/Minor u16 @4/@6, FileReferenceNumber u64 @8 (48-bit record + 16-bit sequence), ParentFileReferenceNumber u64 @16, Usn i64 @24, TimeStamp FILETIME @32, Reason u32 @40, SourceInfo @44, SecurityId @48, FileAttributes @52, FileNameLength (bytes) u16 @56, FileNameOffset u16 @58, FileName UTF-16 @60 (not NUL-terminated); records are 8-byte aligned. (`USN_RECORD_V3` uses 16-byte `FILE_ID_128` references: parent @24, Usn @40, TimeStamp @48, Reason @56, name length/offset @72/@74, name @76.) Reasons that matter: `FILE_DELETE 0x200`, `CLOSE 0x80000000`, `FILE_CREATE 0x100`, `RENAME_OLD_NAME 0x1000`, `RENAME_NEW_NAME 0x2000`, `DATA_OVERWRITE 0x1`, `DATA_TRUNCATION 0x4`, `BASIC_INFO_CHANGE 0x8000`.

Rolling "Recently deleted" list:

1. On `FILE_DELETE | CLOSE`, **before** removing the entry from the index, capture `{name, FRN (with seq), parentFRN, parent path (resolved from the index now, while the parent still exists), size, attributes (directory bit), modified time from the index, deletion timestamp}` into a ring buffer (e.g. 50k entries, persisted alongside the index so it survives restarts). A delete of a directory produces one record per descendant plus the directory; group them under the directory.
2. If the preceding records for the same FRN were `RENAME_NEW_NAME` into `\$Recycle.Bin\<SID>`, tag "In Recycle Bin" and link to the `$I` (§4). If a later `FILE_CREATE` carries the same name and parent, it is likely a save-by-replace (editors do rename-delete-create): tag "replaced by a new version" and hide by default.
3. On startup, backfill from the journal's `FirstUsn` so deletions that happened while FileHound was not running are listed (journal size is typically tens of MB → hours to days of history; `$UsnJrnl:$J` beyond that is sparse-truncated and gone).
4. For each listed entry, compute a live state cheaply with the §1.3 gap oracle: `FSCTL_GET_NTFS_FILE_RECORD(FRN.record)` → returned record < requested ⇒ record still free ⇒ "recoverable if raw reads are available" (and the bytes are then read by §1); returned == requested ⇒ the slot was reused ⇒ MFT undelete impossible, offer carving (§5) filtered by the known extension and size, and VSS (§6).
5. Default noise filter (like winfr's `/e`): `*.tmp`, `~$*`, `*.etl`, `*.log`, browser cache paths, `$Recycle.Bin`, `System Volume Information`, AppData\Local\Temp — toggleable.

This list is the one recovery feature that works without raw reads and without admin for the display (the journal itself needs an elevated handle; FileHound already has that path).

---

## 8. Safety rules

- **Read-only, always.** Open the source volume and shadow devices with `GENERIC_READ` only; never issue write FSCTLs (`FSCTL_MOVE_FILE`, `FSCTL_SET_ZERO_DATA`, `FSCTL_LOCK/DISMOUNT_VOLUME`, `FSCTL_DELETE_USN_JOURNAL`), never "repair" a record in place. ntfsundelete's wording: "ntfsundelete only ever reads from the NTFS Volume" — a good sentence to put in the UI.
- **Destination must be a different volume.** Compare volume GUIDs (`GetVolumeNameForVolumeMountPoint`) of source and destination, not drive letters (junctions). Hard-refuse the same volume, like winfr ("The source and destination drives must be different") and TestDisk ("Don't write anything to the file system holding the data"); Recuva and R-Studio only warn and let the user continue, with R-Studio's "not recommended … continue at your own risk" pop-up. Hard refusal is the better default for a consumer tool; a different *folder* on the same volume is never enough.
- **Stop FileHound's own writes to the source.** During a recovery session on C:, FileHound's index file, logs, crash dumps and temp previews must not land on C:: keep the "recently deleted" buffer in memory, defer index flushes, render previews from memory streams, and write logs to the destination volume or `%TEMP%` only if `%TEMP%` is elsewhere. Also pause the USN-driven index rewrite for that volume.
- **No snapshot creation without consent** (it writes a diff area to the source; §3.3). Prefer existing snapshots.
- **Warn early**: on entering the recovery view show Microsoft's advice ("minimize or avoid using your computer", new files overwrite deleted data) and the SSD/TRIM caveat; show the drive's free-space churn (USN rate) as "the longer you wait, the less comes back".
- **Expectation management**: label every candidate with a confidence derived from §1.4 (bitmap + validator) and say, as R-Studio's moderators do, that a clean-looking record can still yield overwritten content.
- **Re-check before copy**: re-read the bitmap bits for the chosen runs just before copying and compare the first cluster's hash with the scan-time hash; if changed, mark the file as degraded.

---

## 9. Legal and ethical scope

This is an undelete utility for the user's own drives, not evidence tooling: no write blocking, no chain of custody, no hashing claims. Things worth gating or stating:

- **Elevation is a consent boundary.** Raw reads, the cluster bitmap, the USN handle, carving, VSS enumeration and other users' Recycle Bins all require admin. Carving free space exposes deleted data of *every* account on a shared PC; that is normal for an administrator but the UI should say so ("Scans all free space on C:, including data deleted by other user accounts") before the first elevated scan, and the Recycle Bin view should separate "your Recycle Bin" (unelevated) from "other users' Recycle Bins (administrator)".
- **Never bypass encryption.** EFS-encrypted records are listed as not recoverable; BitLocker is only read through the mounted volume or snapshot, never from `PhysicalDrive` ciphertext.
- **Removable media** belongs to whoever plugged it in; nothing to enforce, but keep the "own drives" wording in the feature description.
- **AV exclusions.** If the user adds FileHound to Bitdefender ATD exceptions to get raw reads working, document that the exclusion only affects FileHound's own process and is for reads.
- No licensing issues with reading NTFS structures; the on-disk format is documented by third parties and implemented by many permissively licensed projects (§10).

---

## 10. Prior art in C#/.NET

| Project | License | Fitness |
|---|---|---|
| [DiscUtils](https://github.com/DiscUtils/DiscUtils) / maintained fork [LTRData.DiscUtils](https://github.com/LTRData/DiscUtils) (NuGet `LTRData.DiscUtils.FileSystems`, .NET 8–10) | MIT | Full NTFS read/write over a `Stream`, including `$Bitmap`, `$I30` index parsing, attribute lists, **LZNT1 decompression** and NTFS security descriptors; no undelete API, but the parsers are a reference and the LZNT1 code is directly reusable for compressed deleted files. Can run on a raw volume stream or on a VSS device. |
| [EricZimmerman/MFT](https://github.com/EricZimmerman/MFT) (used by MFTECmd) | MIT | C# parsers for `$MFT` (deleted records included — MFTECmd reports `InUse=false`), `$J` (USN), `$Boot`, `$LogFile`, `$SDS`, `$I30`; the best reference for edge cases (attribute lists, resident/non-resident, slack index entries). Older-style code, not span-based; read it, don't depend on it. |
| EricZimmerman RecycleBin / RBCmd | MIT | `$I` parser (v1 and v2); confirms the §4 layout. |
| [AlphaVSS](https://github.com/alphaleonis/AlphaVSS) (NuGet `AlphaVSS`) | MIT | Managed `IVssBackupComponents`: enumerate/create/delete snapshots. C++/CLI, Windows-only, last release 2023 — acceptable for an optional feature; WMI `Win32_ShadowCopy` via `System.Management` avoids the dependency for enumeration and creation. |
| RawNtfsAccess / ReadLiveNTFS | MIT | Small raw-volume NTFS reader (admin, read-only); less complete than FileHound's own parser. |
| NtfsReader (SourceForge / NuGet repackagings) | LGPL 2.x | Fast enumeration only; licence makes it unattractive for an Apache-2.0 app. |
| Scalpel (Sleuth Kit branch) | Apache 2.0 (standalone 2.02 is GPL) | Signature config format and tables usable as-is. |
| Foremost | public domain (US Air Force OSI) | `foremost.conf` and built-in header/footer rules usable. |
| PhotoRec/TestDisk, ntfs-3g/ntfsundelete, The Sleuth Kit, libyal libfsntfs | GPL / GPL / CPL+IPL / LGPL 3 | Behavioural references only (validation rules, orphan handling, percentage-recoverable, `$OrphanFiles`); do not copy code. |
| winfr | closed | Behavioural reference (modes, switches, safety wording). |

Nothing in the .NET ecosystem ships NTFS undelete or carving as a library; the gap FileHound fills is the combination of its existing span-based parser with the §1–§7 pieces. The only external code worth taking is DiscUtils' LZNT1 decoder and, optionally, AlphaVSS.

---

## Open questions to settle with a `turbo-bench --probe` before design sign-off

1. Which of `FSCTL_GET_VOLUME_BITMAP` (+`_EX`), `GET_NTFS_VOLUME_DATA`, `GET_NTFS_FILE_RECORD` succeed on a `FILE_READ_ATTRIBUTES` handle unelevated, and on the `GENERIC_READ` handle elevated, on this machine.
2. Does `ReadFile` on `\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopyN` succeed where `\\.\C:` fails? (Requires one snapshot; `vssadmin create shadow /for=C:` elevated.)
3. Does `ReadFile` on `\\.\PhysicalDrive0` at C:'s `StartingOffset` succeed, and is the first sector `NTFS` (not `-FVE-FS-`)?
4. Does `winfr C: D:\out /regular` work on this machine? Does adding `turbo-bench.exe` to Bitdefender ATD exceptions change (1)–(3)?
5. What does Process Monitor show as the completing driver for the failing `IRP_MJ_READ` on C:?

## Sources

- Microsoft: [FSCTL_GET_VOLUME_BITMAP](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_get_volume_bitmap) · [STARTING_LCN_INPUT_BUFFER](https://learn.microsoft.com/windows/win32/api/winioctl/ns-winioctl-starting_lcn_input_buffer) · [STARTING_LCN_INPUT_BUFFER_EX (ntdoc)](https://ntdoc.m417z.com/starting_lcn_input_buffer_ex) · [VOLUME_BITMAP_BUFFER (ntdoc)](https://ntdoc.m417z.com/volume_bitmap_buffer) · [FSCTL_GET_NTFS_FILE_RECORD](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_get_ntfs_file_record) · [IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-ioctl_volume_get_volume_disk_extents) · [USN_RECORD_V2](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-usn_record_v2) · [Win32_ShadowCopy.Create](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/vsswmi/create-method-in-class-win32-shadowcopy) · [Creating shadow copies from the command line](https://learn.microsoft.com/archive/blogs/adioltean/creating-shadow-copies-from-the-command-line) · [Windows File Recovery](https://support.microsoft.com/en-us/windows/recover-lost-files-on-windows-10-61f5b28a-f5b8-3cc2-0f8e-a63cb4e1d4c4)
- NTFS format: [linux-ntfs FILE record](https://flatcap.github.io/linux-ntfs/ntfs/concepts/file_record.html) · [linux-ntfs $FILE_NAME](https://flatcap.github.io/linux-ntfs/ntfs/attributes/file_name.html) · [ntfs-3g mft.c](https://ognproject.evlt.uma.es/gitea/opengnsys/ntfs-3g/src/branch/edge/libntfs-3g/mft.c) · [libyal dtformats: Recycle.Bin formats](https://github.com/libyal/dtformats/blob/main/documentation/Windows%20Recycle.Bin%20file%20formats.asciidoc) · [Champlain: Windows 10 Recycle Bin](https://leahycenterblog.champlain.edu/?p=1250)
- Tools: [TSK NTFS File Recovery](https://wiki.sleuthkit.org/NTFS-File-Recovery/) · [ntfsundelete(8)](https://man.cx/ntfsundelete(8)) · [TestDisk: undelete for NTFS](https://www.cgsecurity.org/wiki/TestDisk:_undelete_file_for_NTFS) · [PhotoRec](https://www.cgsecurity.org/wiki/PhotoRec) · [PhotoRec custom signatures](https://www.cgsecurity.org/testdisk_doc/photorec_custom_signature.html) · [winfr signature groups (Puget Systems)](https://www.pugetsystems.com/support/guides/windows-10-file-recovery-tool-1849/) · [OSR: FSCTL_GET_VOLUME_BITMAP ERROR_MORE_DATA](https://community.osr.com/t/fsctl-get-volume-bitmap-error-more-data-available/35767) · [OSR: FSCTL_GET_VOLUME_BITMAP access denied](https://community.osr.com/t/fsctl-get-volume-bitmap-giving-status-access-denied/27107) · [ReactOS DriveVolume.cpp](https://doxygen.reactos.org/d3/dfc/DriveVolume_8cpp_source.html) · [Forensic Focus: dd on shadow copies](https://www.forensicfocus.com/forums/general/dd-windows-forensic-acquisition/)
- Research: [Garfinkel 2007, DFRWS](https://dfrws.org/presentation/carving-contiguous-and-fragmented-files-with-object-validation/) · [Pal, Sencar, Memon 2008](https://dfrws.org/sites/default/files/session-files/2008_USA_paper-detecting_file_fragmentation_point_using_sequential_hypothesis_testing.pdf) · [Karresand et al. 2019, NTFS allocation](https://dfrws.org/presentation/an-empirical-study-of-the-ntfs-cluster-allocation-behavior-over-time/)
- Security products: [Bitdefender ATD exceptions](https://www.bitdefender.com/consumer/support/answer/130706/) · [ESET HIPS "Direct access to disk"](https://help.eset.com/eav/19/en-US/idh_hips_editor_single_rule.html)
- Vendor behaviour: [R-Studio forum on same-drive destination](https://forum.r-tt.com/viewtopic.php?p=14014) · [Recuva states (SPK tutorial)](https://www.spkaa.com/wp-content/uploads/2012/10/SPK_Recuva.pdf) · [Windows 11 System Protection default](https://pureinfotech.com/enable-system-protection-windows-11/)
- Libraries: [LTRData.DiscUtils](https://github.com/LTRData/DiscUtils) · [EricZimmerman/MFT](https://github.com/EricZimmerman/MFT) · [AlphaVSS](https://www.nuget.org/packages/AlphaVSS) · [Scalpel (Sleuth Kit, Apache 2.0)](https://github.com/sleuthkit/scalpel) · [Foremost (public domain)](https://src.fedoraproject.org/rpms/foremost/blob/f99baf815e7bc703bb6623a05fe3c248b6b0d769/f/foremost.spec)

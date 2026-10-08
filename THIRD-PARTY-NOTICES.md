# Third-party notices

FileHound is licensed under the Apache License 2.0 (see `LICENSE`). It uses or adapts the following third-party work.

## fzf — scoring model (adapted, MIT)

`src/FileHound.Core/Matching/FuzzyScorer.cs` adapts the scoring constants and bonus model of fzf's
FuzzyMatchV1 (https://github.com/junegunn/fzf, `src/algo/algo.go`).

```
The MIT License (MIT)

Copyright (c) 2013-2024 Junegunn Choi

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
```

## DiscUtils — LZNT1 reference (MIT)

`src/FileHound.Core/Recovery/Lznt1.cs` implements the LZNT1 decompressor from Microsoft's public format description
(MS-XCA §2.3). DiscUtils' implementation (https://github.com/DiscUtils/DiscUtils, `Library/DiscUtils.Ntfs/LZNT1.cs`,
MIT, © Kenneth Bell and contributors) was used as a reference for the displacement/length split; no code was copied.

## NuGet packages

| Package | License | Use |
|---|---|---|
| CommunityToolkit.Mvvm | MIT (© .NET Foundation and Contributors) | MVVM source generators |
| H.NotifyIcon.Wpf | MIT (© havendv) | System tray icon |
| System.IO.Hashing | MIT (© .NET Foundation and Contributors) | XxHash64 snapshot checksums |
| System.Management | MIT (© .NET Foundation and Contributors) | Win32_ShadowCopy enumeration and creation (Previous versions) |
| xunit, Microsoft.NET.Test.Sdk | Apache-2.0 / MIT | Tests only |

## Artwork

The clay hound mascot and icon set were generated for FileHound with the Higgsfield CLI
(`tools/assets/generate.ps1` records every prompt) and post-processed by `tools/assets/process.ps1`.

## Algorithms (no code copied)

- Myers, G. (1999). *A fast bit-vector algorithm for approximate string matching based on dynamic programming.* J. ACM 46(3).
- NTFS change journal and MFT enumeration via documented Win32 `FSCTL_ENUM_USN_DATA` / `FSCTL_READ_USN_JOURNAL`.

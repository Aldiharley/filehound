<#
.SYNOPSIS
    Renders the README screenshots against a throwaway "Demo" volume, so nothing personal appears in them.
.DESCRIPTION
    Run as administrator (Turbo, undelete and deep scan need it). Creates a 512 MB VHD, formats it NTFS with a change
    journal, fills it with made-up documents and pictures, deletes some the three ways a user does (plain delete,
    Recycle Bin, delete-then-overwrite), then runs FileHound in snapshot mode limited to that drive. The VHD is detached
    and deleted afterwards. Output: docs/screenshots/*.png (or -OutDir).
.EXAMPLE
    .\tools\screenshots\demo-shots.ps1            # uses src\FileHound.App\bin\Release\net10.0-windows\FileHound.exe
#>
param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\..\src\FileHound.App\bin\Release\net10.0-windows\FileHound.exe'),
    [string]$OutDir = (Join-Path $PSScriptRoot '..\..\docs\screenshots'),
    [string]$DisplayName = 'Dennis'
)
$ErrorActionPreference = 'Stop'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole('Administrators')) { throw 'Run as administrator.' }
if (Get-Process FileHound -ErrorAction SilentlyContinue) { throw 'Exit the running FileHound first (snapshot mode is a second instance).' }
$Exe = (Resolve-Path $Exe).Path
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName Microsoft.VisualBasic

$work = Join-Path $env:TEMP "fh-demo-$(Get-Date -Format yyyyMMddHHmmss)"
New-Item -ItemType Directory $work | Out-Null
$vhd = Join-Path $work 'demo.vhd'
$data = Join-Path $work 'data'
$shots = Join-Path $work 'shots'
$letter = [char[]](90..70) | Where-Object { -not (Test-Path "$($_):\") } | Select-Object -First 1
$root = "$($letter):\"

function Invoke-Diskpart([string]$script) {
    $f = Join-Path $work "dp-$([guid]::NewGuid().ToString('N')).txt"
    Set-Content $f $script -Encoding ascii
    & diskpart.exe /s $f | Out-Null
    Remove-Item $f
}
function New-Picture([string]$path, [int]$w, [int]$h, [string]$hex, [string]$label) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.ColorTranslator]::FromHtml($hex))
    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(90, 255, 255, 255))), $w * 0.55, $h * 0.1, $w * 0.35, $w * 0.35)
    $font = New-Object System.Drawing.Font 'Segoe UI', ([Math]::Max(12, $w / 18)), ([System.Drawing.FontStyle]::Bold)
    $g.DrawString($label, $font, [System.Drawing.Brushes]::White, 16, $h - $w / 10 - 24)
    $g.Dispose()
    $fmt = if ($path -like '*.png') { [System.Drawing.Imaging.ImageFormat]::Png } else { [System.Drawing.Imaging.ImageFormat]::Jpeg }
    $bmp.Save($path, $fmt); $bmp.Dispose()
}
function New-Zip([string]$path, [hashtable]$entries) {
    Add-Type -AssemblyName System.IO.Compression
    $fs = [IO.File]::Create($path)
    $zip = New-Object System.IO.Compression.ZipArchive $fs, ([System.IO.Compression.ZipArchiveMode]::Create)
    foreach ($k in $entries.Keys) { $w = New-Object IO.StreamWriter ($zip.CreateEntry($k).Open()); $w.Write($entries[$k]); $w.Dispose() }
    $zip.Dispose(); $fs.Dispose()
}
function New-Pdf([string]$path, [string]$title, [int]$pages) {
    $body = "%PDF-1.7`n1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj`n2 0 obj << /Type /Pages /Count $pages >> endobj`n3 0 obj << /Title ($title) >> endobj`n" + ('%' + ('x' * 4000) + "`n") * 12 + "trailer << /Root 1 0 R >>`n%%EOF`n"
    [IO.File]::WriteAllText($path, $body, [Text.Encoding]::ASCII)
}
Add-Type -Name Native -Namespace Fh -MemberDefinition @'
[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
public static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
[DllImport("kernel32.dll", SetLastError = true)]
public static extern bool FlushFileBuffers(Microsoft.Win32.SafeHandles.SafeFileHandle h);
'@
function Flush {
    # Writes NTFS's cached metadata ($MFT, $Bitmap) so raw reads see the deletions; the lazy writer alone can take a while.
    $h = [Fh.Native]::CreateFileW("\\.\$($letter):", [uint32]3221225472, [uint32]3, [IntPtr]::Zero, 3, 0x02000000, [IntPtr]::Zero)
    if (-not $h.IsInvalid) { [void][Fh.Native]::FlushFileBuffers($h); $h.Dispose() }
    Start-Sleep 1
}

try {
    Write-Host "Creating demo volume $root"
    Invoke-Diskpart @"
create vdisk file="$vhd" maximum=512 type=expandable
select vdisk file="$vhd"
attach vdisk
create partition primary
format fs=ntfs quick label=Demo
assign letter=$letter
"@
    for ($i = 0; $i -lt 50 -and -not (Test-Path $root); $i++) { Start-Sleep -Milliseconds 200 }
    & fsutil.exe usn createjournal m=8000000 a=800000 "$($letter):" | Out-Null

    # ---- contents. The two "overwritten" pictures are created first so they hold the lowest MFT records: after the
    # deletes, exactly two new files reuse exactly those records, and every other deleted file keeps its record intact.
    foreach ($d in 'Documents', 'Photos', 'Projects', 'Projects\filehound', 'Music') { New-Item -ItemType Directory (Join-Path $root $d) | Out-Null }
    New-Picture "$root\Photos\family-dinner.jpg" 1200 900 '#D9798A' 'Family dinner'
    New-Picture "$root\Photos\screenshot-2026-02-14.png" 1280 800 '#6FAE84' 'Screenshot'
    New-Pdf "$root\Documents\Invoice scan 2026-03.pdf" 'Invoice 2026-03' 2
    New-Pdf "$root\Documents\Quarterly report Q3.pdf" 'Quarterly report' 14
    New-Pdf "$root\Documents\Lease agreement (signed).pdf" 'Lease' 9
    New-Zip "$root\Documents\Budget 2026.xlsx" @{ '[Content_Types].xml' = '<Types/>'; 'xl/workbook.xml' = '<workbook/>'; 'xl/worksheets/sheet1.xml' = ('<row/>' * 3000) }
    New-Zip "$root\Documents\Thesis draft v7.docx" @{ '[Content_Types].xml' = '<Types/>'; 'word/document.xml' = ('<w:p>lorem ipsum</w:p>' * 2500) }
    Set-Content "$root\Documents\Notes.txt" ("Shopping, ideas, and a reminder to call the dentist.`n" * 40)
    Set-Content "$root\Documents\Recipe - lemon tart.rtf" ('{\rtf1\ansi\deff0 {\fonttbl{\f0 Arial;}} \f0\fs24 Lemon tart: 200 g flour, 100 g butter, 3 lemons, 150 g sugar, 3 eggs.\par' + ('\par Bake at 180 C for 35 minutes.' * 60) + '}')
    New-Picture "$root\Photos\holiday-beach.jpg" 1600 1066 '#7E9CC9' 'Holiday 2026'
    New-Picture "$root\Photos\mountain-sunrise.jpg" 1600 1066 '#EE8F6E' 'Sunrise'
    New-Picture "$root\Photos\cat-on-keyboard.png" 1024 768 '#9E85C4' 'Cat'
    New-Picture "$root\Photos\garden.png" 800 600 '#E3B248' 'Garden'
    Set-Content "$root\Projects\filehound\app.py" ("import sys`nprint('hello')`n" * 50)
    Set-Content "$root\Projects\filehound\README.md" ("# demo project`n" * 30)
    New-Zip "$root\Projects\archive-2025.zip" @{ 'notes/jan.txt' = ('x' * 5000); 'notes/feb.txt' = ('y' * 5000) }
    [IO.File]::WriteAllText("$root\Music\playlist.m3u" , "holiday.mp3`nsunrise.mp3`n")
    Flush

    # ---- deletions: Recycle Bin, plain (Undelete + Recently deleted), and overwritten records (Deep scan only).
    # Recycle first: the bin's own folders, desktop.ini and $I files need new MFT records, and NTFS would hand them the
    # records the plain deletes had just freed.
    foreach ($f in "$root\Documents\Notes.txt", "$root\Photos\cat-on-keyboard.png") {
        [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteFile($f, 'OnlyErrorDialogs', 'SendToRecycleBin')
    }
    Flush
    Remove-Item "$root\Photos\mountain-sunrise.jpg", "$root\Documents\Budget 2026.xlsx", "$root\Projects\archive-2025.zip", "$root\Documents\Lease agreement (signed).pdf"
    Remove-Item "$root\Photos\family-dinner.jpg", "$root\Photos\screenshot-2026-02-14.png"
    Flush
    for ($i = 0; $i -lt 2; $i++) { Set-Content "$root\Projects\filehound\build-$i.log" ('x' * 2000) }   # reuse those two records
    Flush
    Start-Sleep 2

    # ---- capture
    New-Item -ItemType Directory $data, $shots | Out-Null
    Set-Content (Join-Path $data 'settings.json') ('{"DisplayName":"' + $DisplayName + '","Fuzzy":true}') -Encoding utf8
    Write-Host "Running $Exe in snapshot mode on $($letter): ..."
    $p = Start-Process $Exe -ArgumentList "--snapshot `"$shots`" --data `"$data`" --drives $letter --query `"invce scan`" --warmup 12" -PassThru -Wait
    Write-Host "snapshot exit $($p.ExitCode)"
    New-Item -ItemType Directory $OutDir -Force | Out-Null
    Get-ChildItem $shots -Filter *.png | ForEach-Object { Copy-Item $_.FullName (Join-Path $OutDir $_.Name) -Force; Write-Host "  $($_.Name)" }
}
catch {
    Write-Host "ERROR: $($_.Exception.Message)"
    Write-Host $_.ScriptStackTrace
    throw
}
finally {
    Write-Host 'Detaching demo volume'
    Invoke-Diskpart "select vdisk file=`"$vhd`"`ndetach vdisk"
    Start-Sleep 1
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

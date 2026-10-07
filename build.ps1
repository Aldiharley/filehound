<#
.SYNOPSIS
    Builds FileHound, installing the .NET 10 SDK first when it is missing.

.DESCRIPTION
    Checks the installed .NET SDKs against global.json. If no suitable .NET 10 SDK is found it installs one, preferring
    winget (machine-wide) and falling back to Microsoft's dotnet-install script (per-user, under %LOCALAPPDATA%).
    Then it builds the solution and, on request, runs the tests, publishes the self-contained single-file exe,
    and installs that exe for the current user with a Start Menu shortcut.

    End users of the published FileHound.exe never need .NET installed: the exe carries its own runtime.

.PARAMETER Test
    Run the test suite after building.
.PARAMETER Publish
    Publish the self-contained FileHound.exe to .\publish.
.PARAMETER Install
    Copy the published exe to %LOCALAPPDATA%\Programs\FileHound and create a Start Menu shortcut (implies -Publish).
.PARAMETER NoInstallSdk
    Fail instead of installing the SDK when it is missing.

.EXAMPLE
    .\build.ps1                      # make sure the SDK is there, then build
    .\build.ps1 -Test -Install       # build, test, publish and install for this user
#>
[CmdletBinding()]
param(
    [switch]$Test,
    [switch]$Publish,
    [switch]$Install,
    [switch]$NoInstallSdk
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if ($Install) { $Publish = $true }

function Write-Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }

# --- 1. Work out which SDK global.json asks for ----------------------------------------------------------------------
$globalJson = Get-Content (Join-Path $root 'global.json') -Raw | ConvertFrom-Json
$required = [version]$globalJson.sdk.version          # e.g. 10.0.100
$channel = "$($required.Major).$($required.Minor)"      # e.g. 10.0

function Get-InstalledSdks {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) { return @() }
    $lines = & $dotnet.Source --list-sdks 2>$null
    if ($LASTEXITCODE -ne 0) { return @() }
    foreach ($line in $lines) {
        if ($line -match '^(\d+\.\d+\.\d+)') { [version]$Matches[1] }
    }
}

function Test-SdkPresent {
    # global.json uses rollForward=latestFeature: any SDK with the same major.minor and at least the pinned version works.
    foreach ($v in Get-InstalledSdks) {
        if ($v.Major -eq $required.Major -and $v.Minor -eq $required.Minor -and $v -ge $required) { return $true }
    }
    return $false
}

function Refresh-Path {
    $machine = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    $user = [Environment]::GetEnvironmentVariable('Path', 'User')
    $env:Path = "$machine;$user;$env:Path"
}

# --- 2. Install the SDK if needed ------------------------------------------------------------------------------------
Write-Step "Checking for a .NET $channel SDK (global.json pins $required)"
if (Test-SdkPresent) {
    Write-Host "    found: $((Get-InstalledSdks | Where-Object { $_.Major -eq $required.Major } | Sort-Object -Descending | Select-Object -First 1))"
}
elseif ($NoInstallSdk) {
    throw "No .NET $channel SDK (>= $required) is installed. Install it from https://dotnet.microsoft.com/download or rerun without -NoInstallSdk."
}
else {
    Write-Step "No .NET $channel SDK found - installing"
    $installed = $false

    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if ($winget) {
        Write-Host "    via winget (Microsoft.DotNet.SDK.$($required.Major))"
        & $winget.Source install --id "Microsoft.DotNet.SDK.$($required.Major)" --exact --silent --accept-source-agreements --accept-package-agreements
        if ($LASTEXITCODE -eq 0) {
            # winget returns before the SDK installer has finished writing files; wait for the installer processes.
            while (Get-Process -Name "dotnet-sdk-*", "winget" -ErrorAction SilentlyContinue) { Start-Sleep -Seconds 2 }
            Refresh-Path
            $installed = Test-SdkPresent
        }
        if (-not $installed) { Write-Warning "winget did not produce a usable SDK; trying the dotnet-install script" }
    }

    if (-not $installed) {
        $dotnetRoot = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
        Write-Host "    via dotnet-install.ps1 into $dotnetRoot (per-user, no admin needed)"
        $script = Join-Path ([IO.Path]::GetTempPath()) 'dotnet-install.ps1'
        Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $script -UseBasicParsing
        & $script -Channel $channel -InstallDir $dotnetRoot -NoPath
        $env:DOTNET_ROOT = $dotnetRoot
        $env:Path = "$dotnetRoot;$env:Path"
        [Environment]::SetEnvironmentVariable('DOTNET_ROOT', $dotnetRoot, 'User')
        $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
        if ($userPath -notlike "*$dotnetRoot*") { [Environment]::SetEnvironmentVariable('Path', "$dotnetRoot;$userPath", 'User') }
        $installed = Test-SdkPresent
    }

    if (-not $installed) { throw "Could not install the .NET $channel SDK automatically. Install it from https://dotnet.microsoft.com/download and rerun." }
    Write-Host "    installed: $(Get-InstalledSdks | Where-Object { $_.Major -eq $required.Major } | Sort-Object -Descending | Select-Object -First 1)"
}

# --- 3. Build / test / publish / install -----------------------------------------------------------------------------
Push-Location $root
try {
    Write-Step 'dotnet build FileHound.sln -c Release'
    dotnet build FileHound.sln -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

    if ($Test) {
        Write-Step 'dotnet test FileHound.sln -c Release'
        dotnet test FileHound.sln -c Release --no-build --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
    }

    if ($Publish) {
        Write-Step 'dotnet publish src/FileHound.App (self-contained single file) -> .\publish'
        dotnet publish src/FileHound.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    }

    if ($Install) {
        $dest = Join-Path $env:LOCALAPPDATA 'Programs\FileHound'
        $exe = Join-Path $dest 'FileHound.exe'
        Write-Step "Installing to $dest"
        New-Item -ItemType Directory -Force $dest | Out-Null
        $running = Get-Process -Name FileHound -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }
        if ($running) {
            # FileHound closes to the tray, so there is no window to close politely; stop it and wait until the exe is free.
            Write-Host '    stopping the running FileHound'
            $running | Stop-Process -Force -ErrorAction SilentlyContinue
            $running | Wait-Process -Timeout 30 -ErrorAction SilentlyContinue
        }
        $copied = $false
        for ($attempt = 1; $attempt -le 10 -and -not $copied; $attempt++) {
            try { Copy-Item (Join-Path $root 'publish\FileHound.exe') $exe -Force; $copied = $true }
            catch [System.IO.IOException] { if ($attempt -eq 10) { throw }; Start-Sleep -Seconds 1 }
        }
        $shortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\FileHound.lnk'
        $shell = New-Object -ComObject WScript.Shell
        $lnk = $shell.CreateShortcut($shortcut)
        $lnk.TargetPath = $exe
        $lnk.WorkingDirectory = $dest
        $lnk.IconLocation = "$exe,0"
        $lnk.Description = 'FileHound - fast file search'
        $lnk.Save()
        Write-Host "    installed $exe"
        Write-Host "    Start Menu shortcut: $shortcut"
    }
}
finally { Pop-Location }

Write-Host 'Done.' -ForegroundColor Green

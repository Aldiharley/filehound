<#
  Generates FileHound's clay mascot poses and icon set with the Higgsfield CLI (nano_banana_pro).
  Usage:  pwsh tools/assets/generate.ps1 [-Only name1,name2] [-StyleRef path\to\style.jpg]
  Output: assets-src/<name>.png (raw) and assets-src/<name>-cut.png (background removed, when requested).
  Requires: `higgsfield auth login` done once. Costs ~2 credits per image + 1 per background removal.
#>
param(
    [string[]]$Only,
    [string]$StyleRef
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$out = Join-Path $root 'assets-src'
New-Item -ItemType Directory -Force $out | Out-Null
$hero = Join-Path $out 'hound-hero.png'

$mascotStyle = 'Same character as the reference image: cute 3D clay beagle puppy "Hound" with floppy caramel-brown ears, cream face and chest, big shiny friendly eyes, pastel mint neckerchief. Claymorphism, smooth matte plasticine texture, soft rounded shapes, soft studio lighting, gentle contact shadow, Pixar-like charm, no text.'
$iconStyle = 'Single 3D clay UI icon in the exact style of the reference sheet: soft pastel claymorphism, smooth matte plasticine, puffy rounded edges, subtle soft shadow, pastel lilac, periwinkle blue, mint and soft pink palette with cream white accents. One object only, centered, front three-quarter view, on a plain pure white background, no text, no border, high detail.'

$jobs = [ordered]@{
    'hound-avatar' = @{ ref = $hero;  cut = $false; prompt = "$mascotStyle Head-and-shoulders portrait, waving one paw hello with a happy smile, looking at the viewer. Plain flat solid cream background (#FBF4EA)." }
    'hound-sleep'  = @{ ref = $hero;  cut = $true;  prompt = "$mascotStyle Curled up asleep on a round pastel mint cushion, eyes closed, peaceful smile, tiny floating 'z' shapes made of clay. Full body, centered, plain flat solid cream background (#FBF4EA)." }
    'hound-sniff'  = @{ ref = $hero;  cut = $true;  prompt = "$mascotStyle Walking forward with nose down to the ground, sniffing a trail of small pastel peach paw prints, tail up, curious happy expression. Full body side three-quarter view, centered, plain flat solid cream background (#FBF4EA)." }
    'hound-shrug'  = @{ ref = $hero;  cut = $true;  prompt = "$mascotStyle Sitting and shrugging with both paws raised, head tilted, puzzled but cute expression, the magnifying glass lying beside it. Full body, centered, plain flat solid cream background (#FBF4EA)." }
    'hound-cut'    = @{ ref = $hero;  cut = $true;  prompt = "$mascotStyle Exactly the reference pose: sitting on a small clay rock, holding the big round butter-yellow magnifying glass up to one eye, tongue out. Full body, centered, plain flat solid soft peach background (#F8CDB8)." }
    'icon-folder'   = @{ ref = $StyleRef; cut = $true; prompt = "$iconStyle A mint green file folder with cream paper sheets peeking out." }
    'icon-document' = @{ ref = $StyleRef; cut = $true; prompt = "$iconStyle A butter yellow and cream document page with a folded corner and soft grey text lines." }
    'icon-image'    = @{ ref = $StyleRef; cut = $true; prompt = "$iconStyle A lilac picture frame showing pastel mountains and a little sun." }
    'icon-video'    = @{ ref = $StyleRef; cut = $true; prompt = "$iconStyle A periwinkle blue clapperboard / video player with a white play triangle." }
    'icon-audio'    = @{ ref = $StyleRef; cut = $true; prompt = "$iconStyle A soft pink pair of musical notes." }
    'icon-archive'  = @{ ref = $StyleRef; cut = $true; prompt = "$iconStyle A peach colored cardboard archive box with a zipper on the lid." }
    'icon-app'      = @{ ref = $StyleRef; cut = $true; prompt = "$iconStyle A periwinkle blue rounded app window with a small rocket shape." }
    'icon-code'     = @{ ref = $StyleRef; cut = $true; prompt = "$iconStyle A mint rounded code window showing angle brackets < / >." }
    'icon-drive'    = @{ ref = $StyleRef; cut = $true; prompt = "$iconStyle A rounded lilac and cream hard disk drive with a small mint status light." }
    'icon-paw'      = @{ ref = $StyleRef; cut = $true; prompt = "$iconStyle A soft peach dog paw print badge." }
    'icon-clock'    = @{ ref = $StyleRef; cut = $true; prompt = "$iconStyle A butter yellow round alarm clock with cream face." }
    'icon-search'   = @{ ref = $StyleRef; cut = $true; prompt = "$iconStyle A lilac magnifying glass with a cream lens highlight." }
    'icon-bolt'     = @{ ref = $StyleRef; cut = $true; prompt = "$iconStyle A butter yellow lightning bolt with a peach glow." }
    'icon-gear'     = @{ ref = $StyleRef; cut = $true; prompt = "$iconStyle A lilac settings gear cog." }
    'icon-star'     = @{ ref = $StyleRef; cut = $true; prompt = "$iconStyle A cute butter yellow star with a tiny happy face." }
    'icon-home'     = @{ ref = $StyleRef; cut = $true; prompt = "$iconStyle A mint little house with a peach roof." }
}

# Skip assets that already exist so the script can be re-run after failures.
$selected = $jobs.Keys | Where-Object { (-not $Only -or $Only -contains $_) -and -not (Test-Path (Join-Path $out ($(if ($jobs[$_].cut) { "$_-cut.png" } else { "$_.png" })))) }
$worker = {
    param($name, $prompt, $ref, $cut, $out)
    function Invoke-Hf([string[]]$hfArgs) {
        for ($attempt = 0; $attempt -lt 6; $attempt++) {
            $text = (& higgsfield @hfArgs 2>&1 | Out-String)
            if ($text -notmatch 'rate_limit_reached') { return $text }
            Start-Sleep -Seconds 20
        }
        throw "rate limited: $text"
    }
    $raw = Join-Path $out "$name.png"
    if (-not (Test-Path $raw)) {
        $hfArgs = @('generate', 'create', 'nano_banana_pro', '--prompt', $prompt, '--aspect_ratio', '1:1', '--resolution', '2k', '--wait', '--json')
        if ($ref) { $hfArgs += @('--image', $ref) }
        $json = Invoke-Hf $hfArgs
        $result = ($json.Substring($json.IndexOf('[')) | ConvertFrom-Json)[0]
        if ($result.status -ne 'completed') { throw "$name failed: $json" }
        Invoke-WebRequest $result.result_url -OutFile $raw
    }
    if ($cut) {
        $json2 = Invoke-Hf @('generate', 'create', 'image_background_remover', '--image', $raw, '--wait', '--json')
        $r2 = ($json2.Substring($json2.IndexOf('[')) | ConvertFrom-Json)[0]
        if ($r2.status -eq 'completed') { Invoke-WebRequest $r2.result_url -OutFile (Join-Path $out "$name-cut.png") }
    }
    "$name ok"
}

$maxConcurrent = 3   # plan limit is 4 concurrent jobs; keep one slot free for background removal
$all = @()
foreach ($name in $selected) {
    while (@(Get-Job | Where-Object { $_.State -eq 'Running' -and $all -contains $_ }).Count -ge $maxConcurrent) { Start-Sleep -Seconds 2 }
    $j = $jobs[$name]
    $all += Start-Job -Name $name -ArgumentList $name, $j.prompt, $j.ref, $j.cut, $out -ScriptBlock $worker
}
$all | Wait-Job | Out-Null
foreach ($job in $all) {
    try { Receive-Job $job -ErrorAction Stop } catch { Write-Warning "$($job.Name): $($_.Exception.Message)" }
}
$all | Remove-Job
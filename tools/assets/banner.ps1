<#
  Generates the illustration layers for the GitHub banner with the Higgsfield CLI (nano_banana_pro), then composes
  the final PNGs (text overlay in Pillow). Spec: docs/superpowers/specs/2026-10-09-github-banner.md
  Usage:  pwsh tools/assets/banner.ps1 [-Force]        (-Force regenerates even when assets-src/banner-*.png exist)
  Output: assets-src/banner-social.png, assets-src/banner-wide.png (raw renders, copied to docs/banner/src/ so the
          compositor can be re-run from a clean clone without spending credits)
          docs/banner/social-preview.png (1280x640), docs/banner/readme-banner.png (1600x500)
  Requires: `higgsfield auth login` done once (~2 credits per image), Python with Pillow.
#>
param([switch]$Force)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$out = Join-Path $root 'assets-src'
$hero = Join-Path $out 'hound-hero.png'

$mascotStyle = 'Same character as the reference image: cute 3D clay beagle puppy "Hound" with floppy caramel-brown ears, cream face and chest, big shiny friendly eyes, pastel mint neckerchief. Claymorphism, smooth matte plasticine texture, soft rounded shapes, soft studio lighting, gentle contact shadow, Pixar-like charm, no text.'
$pose = 'Sitting three-quarter view, body turned toward the left of the frame, holding the big round butter-yellow magnifying glass up in the right paw, and with the left paw lifting a rescued cream envelope-shaped file with a peach ribbon out of a small pastel mint clay recycle bin whose cream lid is tipped open beside him, tail up, delighted expression, tongue out, three tiny butter-yellow clay sparkles. Floating gently around him, four small puffy clay objects: a mint green file folder with cream paper peeking out, a butter yellow document page with a folded corner, a lilac picture frame with a tiny pastel mountain, a periwinkle blue hard drive with a mint status light. Soft warm peach glow (#F9D3C0) behind the character only. Gentle warm contact shadows, no grey shadows.'
$noText = 'No text, no letters, no logo, no watermark.'

$jobs = [ordered]@{
    'banner-social' = @{ ratio = '16:9'; prompt = "$mascotStyle $pose The character and all objects occupy only the right 45% of the frame. The entire left 55% of the frame is completely empty, plain flat solid cream background (#FBF4EA), nothing in it. Wide horizontal banner composition. $noText" }
    'banner-wide'   = @{ ratio = '21:9'; prompt = "$mascotStyle $pose The character and all objects occupy only the right 42% of the frame. The entire left 58% of the frame is completely empty, plain flat solid cream background (#FBF4EA), nothing in it. Ultra-wide horizontal banner composition. $noText" }
}

function Invoke-Hf([string[]]$hfArgs) {
    for ($attempt = 0; $attempt -lt 6; $attempt++) {
        $text = (& higgsfield @hfArgs 2>&1 | Out-String)
        if ($text -notmatch 'rate_limit_reached') { return $text }
        Start-Sleep -Seconds 20
    }
    throw "rate limited: $text"
}

foreach ($name in $jobs.Keys) {
    $raw = Join-Path $out "$name.png"
    if ((Test-Path $raw) -and -not $Force) { Write-Host "$name exists, skipping"; continue }
    $j = $jobs[$name]
    Write-Host "Generating $name ($($j.ratio))..."
    $json = Invoke-Hf @('generate', 'create', 'nano_banana_pro', '--prompt', $j.prompt, '--aspect_ratio', $j.ratio, '--resolution', '2k', '--image', $hero, '--wait', '--json')
    $result = ($json.Substring($json.IndexOf('[')) | ConvertFrom-Json)[0]
    if ($result.status -ne 'completed') { throw "$name failed: $json" }
    Invoke-WebRequest $result.result_url -OutFile $raw
    Write-Host "  -> $raw"
}
$keep = Join-Path $root 'docs\banner\src'
New-Item -ItemType Directory -Force $keep | Out-Null
foreach ($name in $jobs.Keys) { Copy-Item (Join-Path $out "$name.png") $keep -Force }
Copy-Item (Join-Path $out 'icon-paw-cut.png') $keep -Force

Write-Host 'Composing...'
& python (Join-Path $PSScriptRoot 'banner_compose.py') $root

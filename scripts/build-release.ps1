[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$version = (Get-Content (Join-Path $repoRoot "VERSION") -Raw).Trim()
$project = Join-Path $repoRoot "src\SnapGlyph.App\SnapGlyph.App.csproj"
$outputs = Join-Path $repoRoot "outputs"
$name = "SnapGlyph-portable-$Runtime-v$version"
$stage = Join-Path $outputs $name
$zip = Join-Path $outputs "$name.zip"

if (-not (Get-Command "dotnet" -ErrorAction SilentlyContinue)) {
    throw ".NET SDK not found. Install the SDK version specified by global.json."
}

if (-not (Test-Path (Join-Path $repoRoot "models\v6\PP-OCRv6_rec_small.onnx"))) {
    throw "Models are missing. Run scripts/fetch-models.ps1 first."
}

New-Item -ItemType Directory -Path $outputs -Force | Out-Null
if (Test-Path -LiteralPath $stage) {
    Remove-Item -LiteralPath $stage -Recurse -Force
}
if (Test-Path -LiteralPath $zip) {
    Remove-Item -LiteralPath $zip -Force
}

& dotnet publish $project `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained true `
    --output $stage `
    -p:DebugType=None `
    -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

foreach ($notice in @("LICENSE", "THIRD_PARTY_NOTICES.md")) {
    Copy-Item -LiteralPath (Join-Path $repoRoot $notice) -Destination (Join-Path $stage $notice)
}

$readme = @"
SnapGlyph $version

Offline Windows screenshot, OCR, and barcode scanning.
Run SnapGlyph.exe. The application stays in the system tray.

See README.md, LICENSE, and THIRD_PARTY_NOTICES.md in the project repository.
"@
[System.IO.File]::WriteAllText(
    (Join-Path $stage "README.txt"),
    $readme,
    [System.Text.UTF8Encoding]::new($false))

Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $([System.IO.Path]::GetFileName($zip))" |
    Set-Content -LiteralPath "$zip.sha256" -Encoding ascii

Write-Host ""
Write-Host "Release directory: $stage"
Write-Host "Release archive:   $zip"
Write-Host "SHA-256:           $hash"

# NetAudit publish script
# Usage: .\publish.ps1
# Requires: .NET 10 SDK  |  Run sign-setup.ps1 first to create the signing cert.

$version = "1.0.0"
$rid     = "win-x64"
$config  = "Release"

$solutionRoot = $PSScriptRoot
$appProject   = Join-Path $solutionRoot "NetAudit.App\NetAudit.App.csproj"
$distDir      = Join-Path $solutionRoot "dist"
$outName      = "NetAudit-v$version"
$outDir       = Join-Path $distDir $outName
$zipPath      = Join-Path $distDir "$outName-$rid.zip"
$certFile     = Join-Path $solutionRoot "NetAudit-cert.cer"

# ── Step 1: publish ─────────────────────────────────────────
Write-Host ""
Write-Host "[1/4] Building $outName ($rid)..." -ForegroundColor Cyan

dotnet publish $appProject `
    --configuration $config `
    --runtime $rid `
    --self-contained false `
    --output $outDir `
    "-p:Version=$version" `
    "-p:PublishReadyToRun=true"

if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed." -ForegroundColor Red
    exit 1
}

# ── Step 2: sign exe ────────────────────────────────────────
Write-Host ""
Write-Host "[2/4] Signing executable..." -ForegroundColor Cyan

$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
        Where-Object { $_.Subject -like "*NetAudit*" } |
        Select-Object -First 1

if ($cert) {
    $exePath = Join-Path $outDir "NetAudit.App.exe"
    $sig = Set-AuthenticodeSignature `
        -FilePath $exePath `
        -Certificate $cert `
        -TimestampServer "http://timestamp.digicert.com" `
        -HashAlgorithm SHA256

    if ($sig.Status -eq "Valid") {
        Write-Host "Signed OK: $($cert.Subject)" -ForegroundColor Green
    } else {
        Write-Host "WARNING: Signature status: $($sig.Status)" -ForegroundColor Yellow
    }
} else {
    Write-Host "WARNING: No NetAudit signing cert found in CurrentUser\My." -ForegroundColor Yellow
    Write-Host "         Run sign-setup.ps1 first. Skipping signing." -ForegroundColor Yellow
}

# ── Step 3: clean + bundle trust assets ─────────────────────
Write-Host ""
Write-Host "[3/4] Cleaning and bundling..." -ForegroundColor Cyan

$remove = @("*.pdb", "*.xml")
foreach ($pattern in $remove) {
    Get-ChildItem $outDir -Filter $pattern -Recurse -ErrorAction SilentlyContinue |
        Remove-Item -Force
}

# Bundle installer scripts
Copy-Item (Join-Path $solutionRoot "install.bat") (Join-Path $outDir "install.bat")
Copy-Item (Join-Path $solutionRoot "install.ps1") (Join-Path $outDir "install.ps1")
Write-Host "Bundled install.bat and install.ps1"

if (Test-Path $certFile) {
    Copy-Item $certFile (Join-Path $outDir "NetAudit-cert.cer")
    Write-Host "Bundled NetAudit-cert.cer"
} else {
    Write-Host "NOTE: NetAudit-cert.cer not found (run sign-setup.ps1 first)" -ForegroundColor Yellow
}

# README
$readme = "NetAudit $version" + [System.Environment]::NewLine +
          "=========================" + [System.Environment]::NewLine +
          "" + [System.Environment]::NewLine +
          "FIRST INSTALL" + [System.Environment]::NewLine +
          "  Double-click install.bat" + [System.Environment]::NewLine +
          "  It will automatically install .NET 10 Runtime if needed," + [System.Environment]::NewLine +
          "  trust the certificate, and create a Desktop shortcut." + [System.Environment]::NewLine +
          "" + [System.Environment]::NewLine +
          "UPDATE" + [System.Environment]::NewLine +
          "  Extract new zip into the same folder, overwrite files." + [System.Environment]::NewLine +
          "  No need to run install.bat again." + [System.Environment]::NewLine +
          "  Settings in %LOCALAPPDATA%\NetAudit\settings.json are preserved." + [System.Environment]::NewLine +
          "" + [System.Environment]::NewLine +
          "UNINSTALL" + [System.Environment]::NewLine +
          "  Delete this folder. Optionally delete %LOCALAPPDATA%\NetAudit\"

Set-Content -Path (Join-Path $outDir "README.txt") -Value $readme -Encoding UTF8

# ── Step 4: zip ─────────────────────────────────────────────
Write-Host ""
Write-Host "[4/4] Creating ZIP..." -ForegroundColor Cyan

if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($outDir, $zipPath)

$sizeMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
Write-Host ""
Write-Host "Done: $zipPath ($sizeMb MB)" -ForegroundColor Green
Write-Host "Dir:  $outDir"
Write-Host ""

# install.ps1 — NetAudit first-run setup
# Checks for .NET 10 Desktop Runtime, installs if missing, trusts the certificate.
# Called from install.bat (double-click).

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path $MyInvocation.MyCommand.Path -Parent

function Write-Step($msg) { Write-Host "`n$msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "  OK: $msg" -ForegroundColor Green }
function Write-Warn($msg) { Write-Host "  !!: $msg" -ForegroundColor Yellow }

# ── Check if running as administrator ───────────────────────
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

# ── Check .NET 10 Desktop Runtime ───────────────────────────
Write-Step "[1/3] Checking .NET 10 Desktop Runtime..."

$regKey = "HKLM:\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App"
$has10  = $false
try {
    $has10 = (Get-ChildItem $regKey -ErrorAction SilentlyContinue |
              Where-Object { $_.PSChildName -match "^10\." } |
              Measure-Object).Count -gt 0
} catch {}

if ($has10) {
    Write-Ok ".NET 10 Desktop Runtime already installed."
} else {
    Write-Host "  .NET 10 Desktop Runtime not found." -ForegroundColor Yellow

    # Need admin to install .NET — re-launch elevated if needed
    if (-not $isAdmin) {
        Write-Host "  Requesting administrator rights to install .NET..." -ForegroundColor Yellow
        Start-Process powershell -Verb RunAs `
            -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`"" `
            -Wait
        exit 0
    }

    Write-Step "[1/3] Downloading .NET 10 Desktop Runtime (~55 MB)..."

    $installerUrl = "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe"
    $installerPath = Join-Path $env:TEMP "dotnet10-desktop-x64.exe"

    try {
        # Use BITS for reliable background download with progress
        Import-Module BitsTransfer -ErrorAction SilentlyContinue
        if (Get-Command Start-BitsTransfer -ErrorAction SilentlyContinue) {
            Start-BitsTransfer -Source $installerUrl -Destination $installerPath -DisplayName ".NET 10 Runtime"
        } else {
            $wc = New-Object System.Net.WebClient
            $wc.DownloadFile($installerUrl, $installerPath)
        }
        Write-Host "  Download complete."
    } catch {
        Write-Host "  Download failed: $_" -ForegroundColor Red
        Write-Host "  Manual install: https://dotnet.microsoft.com/download/dotnet/10.0" -ForegroundColor Yellow
        Read-Host "`n  Press Enter to exit"
        exit 1
    }

    Write-Host "  Installing (silent)..."
    $proc = Start-Process $installerPath -ArgumentList "/install /quiet /norestart" -Wait -PassThru
    Remove-Item $installerPath -Force -ErrorAction SilentlyContinue

    if ($proc.ExitCode -eq 0 -or $proc.ExitCode -eq 3010) {
        Write-Ok ".NET 10 Desktop Runtime installed."
        if ($proc.ExitCode -eq 3010) {
            Write-Warn "A reboot may be required before first run."
        }
    } else {
        Write-Host "  Installer exited with code $($proc.ExitCode)" -ForegroundColor Red
        Read-Host "`n  Press Enter to exit"
        exit 1
    }
}

# ── Trust signing certificate ────────────────────────────────
Write-Step "[2/3] Trusting NetAudit certificate..."

$certFile = Join-Path $scriptDir "NetAudit-cert.cer"
if (Test-Path $certFile) {
    $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($certFile)

    $stores = @("Root", "TrustedPublisher")
    foreach ($storeName in $stores) {
        $scope = if ($isAdmin) { "LocalMachine" } else { "CurrentUser" }
        $store = New-Object System.Security.Cryptography.X509Certificates.X509Store($storeName, $scope)
        $store.Open("ReadWrite")
        if (-not ($store.Certificates | Where-Object { $_.Thumbprint -eq $cert.Thumbprint })) {
            $store.Add($cert)
            Write-Ok "Added to $scope\$storeName"
        } else {
            Write-Ok "Already trusted in $scope\$storeName"
        }
        $store.Close()
    }
} else {
    Write-Warn "NetAudit-cert.cer not found — skipping certificate trust."
}

# ── Create desktop shortcut ──────────────────────────────────
Write-Step "[3/3] Creating desktop shortcut..."

$exePath      = Join-Path $scriptDir "NetAudit.App.exe"
$shortcutPath = Join-Path ([Environment]::GetFolderPath("Desktop")) "NetAudit.lnk"

try {
    $wsh     = New-Object -ComObject WScript.Shell
    $lnk     = $wsh.CreateShortcut($shortcutPath)
    $lnk.TargetPath       = $exePath
    $lnk.WorkingDirectory = $scriptDir
    $lnk.Description      = "NetAudit — network and system monitor"
    $lnk.Save()
    Write-Ok "Shortcut created on Desktop."
} catch {
    Write-Warn "Could not create shortcut: $_"
}

# ── Done ─────────────────────────────────────────────────────
Write-Host ""
Write-Host "Setup complete. Run NetAudit from the Desktop shortcut." -ForegroundColor Green
Write-Host ""
Read-Host "Press Enter to exit"

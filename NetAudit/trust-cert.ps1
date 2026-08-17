# trust-cert.ps1 — run ONCE on each user machine
# Trusts the NetAudit code signing certificate so Defender/SmartScreen won't block the app.
# No admin required (installs to CurrentUser store).

$certFile = Join-Path $PSScriptRoot "NetAudit-cert.cer"

if (-not (Test-Path $certFile)) {
    Write-Host "ERROR: NetAudit-cert.cer not found." -ForegroundColor Red
    Write-Host "Place trust-cert.ps1 and NetAudit-cert.cer in the same folder."
    Read-Host "Press Enter to exit"
    exit 1
}

$cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($certFile)

$stores = @(
    [System.Security.Cryptography.X509Certificates.X509Store]::new("Root",             "CurrentUser"),
    [System.Security.Cryptography.X509Certificates.X509Store]::new("TrustedPublisher", "CurrentUser")
)

foreach ($store in $stores) {
    $store.Open("ReadWrite")
    if (-not ($store.Certificates | Where-Object { $_.Thumbprint -eq $cert.Thumbprint })) {
        $store.Add($cert)
        Write-Host "Added to $($store.Name)" -ForegroundColor Green
    } else {
        Write-Host "Already trusted in $($store.Name)" -ForegroundColor Yellow
    }
    $store.Close()
}

Write-Host ""
Write-Host "Done. NetAudit is now trusted on this machine." -ForegroundColor Green
Read-Host "Press Enter to exit"

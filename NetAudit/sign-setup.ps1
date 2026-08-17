# sign-setup.ps1 — run ONCE on your dev machine
# Creates a self-signed code signing certificate and exports it for distribution.

$certSubject = "CN=NetAudit, O=NetAudit"
$friendlyName = "NetAudit Code Signing"
$exportPath   = Join-Path $PSScriptRoot "NetAudit-cert.cer"

# Check if cert already exists
$existing = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
            Where-Object { $_.Subject -eq $certSubject } |
            Select-Object -First 1

if ($existing) {
    Write-Host "Certificate already exists: $($existing.Thumbprint)" -ForegroundColor Yellow
    $cert = $existing
} else {
    $cert = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $certSubject `
        -KeyUsage DigitalSignature `
        -FriendlyName $friendlyName `
        -CertStoreLocation Cert:\CurrentUser\My `
        -NotAfter (Get-Date).AddYears(10) `
        -TextExtension @(
            "2.5.29.37={text}1.3.6.1.5.5.7.3.3",
            "2.5.29.19={text}"
        )
    Write-Host "Created: $($cert.Thumbprint)" -ForegroundColor Green
}

# Trust locally so publish.ps1 works without warnings
$store = New-Object System.Security.Cryptography.X509Certificates.X509Store(
    "TrustedPublisher", "CurrentUser")
$store.Open("ReadWrite")
if (-not ($store.Certificates | Where-Object { $_.Thumbprint -eq $cert.Thumbprint })) {
    $store.Add($cert)
    Write-Host "Added to CurrentUser\TrustedPublisher"
}
$store.Close()

$rootStore = New-Object System.Security.Cryptography.X509Certificates.X509Store(
    "Root", "CurrentUser")
$rootStore.Open("ReadWrite")
if (-not ($rootStore.Certificates | Where-Object { $_.Thumbprint -eq $cert.Thumbprint })) {
    $rootStore.Add($cert)
    Write-Host "Added to CurrentUser\Root"
}
$rootStore.Close()

# Export public cert for distribution
Export-Certificate -Cert $cert -FilePath $exportPath | Out-Null
Write-Host ""
Write-Host "Done. Exported: $exportPath" -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:"
Write-Host "  1. Run .\publish.ps1  (exe will be signed automatically)"
Write-Host "  2. Distribute NetAudit-cert.cer alongside the ZIP"
Write-Host "  3. Users run trust-cert.ps1 once to trust the certificate"

<#
.SYNOPSIS
    Сборка релиза NetAudit: publish, подпись, архив, контрольная сумма, version.json.

.DESCRIPTION
    Отличается от publish.ps1 тем, что готовит всё нужное для выкладки на GitHub:
    считает SHA-256 архива и прописывает его в version.json. Без этой суммы
    автообновление не может проверить, что скачало именно то, что ожидало.

.PARAMETER Version
    Версия релиза, например 1.1.0. Проставляется в сборку, имя архива и version.json.

.PARAMETER Notes
    Строка «что нового» для баннера обновления в приложении.

.PARAMETER Repo
    Репозиторий GitHub в виде «владелец/имя». Из него строится ссылка на архив.

.EXAMPLE
    .\release.ps1 -Version 1.1.0 -Notes "Тесты сети, игровой режим, трей" -Repo togram251/netaudit
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Notes = "",
    [string]$Repo  = "limbo-wh/netaudit"
)

$ErrorActionPreference = 'Stop'

$root    = $PSScriptRoot
$project = Join-Path $root "NetAudit.App\NetAudit.App.csproj"
$distDir = Join-Path $root "dist"
$outName = "NetAudit-v$Version"
$outDir  = Join-Path $distDir $outName
$rid     = "win-x64"
$zipPath = Join-Path $distDir "$outName-$rid.zip"

Write-Host ""
Write-Host "=== NetAudit $Version ===" -ForegroundColor Cyan

# ── 1. Сборка ─────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "[1/5] Сборка..." -ForegroundColor Cyan

if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }

dotnet publish $project `
    --configuration Release `
    --runtime $rid `
    --self-contained false `
    --output $outDir `
    "-p:Version=$Version" `
    "-p:PublishReadyToRun=true"

if ($LASTEXITCODE -ne 0) { Write-Host "Сборка не удалась." -ForegroundColor Red; exit 1 }

# ── 2. Подпись ────────────────────────────────────────────────────────────
# Подписывается только exe. Проверено 2026-08-17: при включённом Smart App Control
# самоподписанные библиотеки блокируются жёстче неподписанных — подробности в stack.md
Write-Host ""
Write-Host "[2/5] Подпись..." -ForegroundColor Cyan

$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
        Where-Object { $_.Subject -like "*NetAudit*" } |
        Select-Object -First 1

if ($cert) {
    $sig = Set-AuthenticodeSignature `
        -FilePath (Join-Path $outDir "NetAudit.App.exe") `
        -Certificate $cert `
        -TimestampServer "http://timestamp.digicert.com" `
        -HashAlgorithm SHA256
    if ($sig.Status -eq "Valid") { Write-Host "  подписано: $($cert.Subject)" -ForegroundColor Green }
    else { Write-Host "  ВНИМАНИЕ: статус подписи $($sig.Status)" -ForegroundColor Yellow }
} else {
    Write-Host "  сертификат не найден, пропускаю" -ForegroundColor Yellow
}

# ── 3. Чистка и вложения ──────────────────────────────────────────────────
Write-Host ""
Write-Host "[3/5] Чистка и вложения..." -ForegroundColor Cyan

Get-ChildItem $outDir -Include "*.pdb", "*.xml" -Recurse -ErrorAction SilentlyContinue |
    Remove-Item -Force

foreach ($f in @("install.bat", "install.ps1")) {
    $src = Join-Path $root $f
    if (Test-Path $src) { Copy-Item $src (Join-Path $outDir $f); Write-Host "  вложен $f" }
}

$readme = @"
NetAudit $Version

УСТАНОВКА
  Запустите install.bat. Он поставит .NET 10 Runtime, если его нет,
  и создаст ярлык на рабочем столе.

ОБНОВЛЕНИЕ
  Приложение обновляется само: «Тесты и сервис» -> «Проверить обновление».
  Вручную: распакуйте архив в ту же папку с заменой файлов.
  Настройки в %LOCALAPPDATA%\NetAudit\ сохраняются.

УДАЛЕНИЕ
  Удалите эту папку. При желании — и %LOCALAPPDATA%\NetAudit\
"@
[System.IO.File]::WriteAllText((Join-Path $outDir "ПРОЧТИ.txt"), $readme, [System.Text.UTF8Encoding]::new($true))

# ── 4. Архив и контрольная сумма ──────────────────────────────────────────
Write-Host ""
Write-Host "[4/5] Архив..." -ForegroundColor Cyan

if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($outDir, $zipPath)

$sha    = (Get-FileHash $zipPath -Algorithm SHA256).Hash
$sizeMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)

Write-Host "  $zipPath  ($sizeMb МБ)" -ForegroundColor Green
Write-Host "  SHA-256: $sha"

# ── 5. version.json ───────────────────────────────────────────────────────
Write-Host ""
Write-Host "[5/5] version.json..." -ForegroundColor Cyan

$downloadUrl = "https://github.com/$Repo/releases/download/v$Version/$outName-$rid.zip"

$json = [ordered]@{
    version     = $Version
    notes       = $Notes
    downloadUrl = $downloadUrl
    sha256      = $sha
} | ConvertTo-Json

$versionFile = Join-Path $root "version.json"
[System.IO.File]::WriteAllText($versionFile, $json, [System.Text.UTF8Encoding]::new($false))
Write-Host "  обновлён $versionFile" -ForegroundColor Green

# ── Что делать дальше ─────────────────────────────────────────────────────
Write-Host ""
Write-Host "Дальше:" -ForegroundColor Cyan
Write-Host "  1. git add -A; git commit -m ""Релиз $Version""; git push"
Write-Host "  2. gh release create v$Version ""$zipPath"" --title ""NetAudit $Version"" --notes ""$Notes"""
Write-Host ""
Write-Host "  version.json должен лежать в ветке по адресу, указанном в настройках"
Write-Host "  приложения (UpdateCheckUrl). Ссылка на сырой файл выглядит так:"
Write-Host "  https://raw.githubusercontent.com/$Repo/main/NetAudit/version.json" -ForegroundColor Yellow
Write-Host ""

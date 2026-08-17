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
    [string]$Repo  = "limbo-wh/netaudit",
    # Публичный релиз для посторонних — БЕЗ подписи. Замерено 2026-08-17 (stack.md):
    # самоподписанный exe без локального доверия к сертификату на целевой машине
    # блокируется Smart App Control жёстче, чем вообще неподписанный (0/3 против 4/4).
    # Чужой пользователь сертификат не доверяет, так что подпись только вредит.
    # Флаг оставлен на случай купленного коммерческого сертификата или раздачи
    # узкому кругу машин, где сертификат уже доверен через sign-setup.ps1.
    [switch]$Sign
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
# По умолчанию НЕ подписывается. Замерено 2026-08-17 (см. stack.md): на машине,
# где сертификат не доверен локально, Smart App Control блокирует самоподписанный
# exe жёстче, чем вообще неподписанный (0 из 3 запусков против 4 из 4). Для чужого
# человека из интернета сертификат никогда не доверен — подпись только вредит.
# Передайте -Sign, только если распространяете внутри круга машин, где сертификат
# уже доверен через sign-setup.ps1, либо когда появится настоящий коммерческий сертификат.
Write-Host ""
Write-Host "[2/5] Подпись..." -ForegroundColor Cyan

if (-not $Sign) {
    Write-Host "  пропущено — публичный релиз собирается без подписи (см. stack.md)" -ForegroundColor Yellow
} else {
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

        Copy-Item (Join-Path $root "NetAudit-cert.cer") (Join-Path $outDir "NetAudit-cert.cer") -ErrorAction SilentlyContinue
        Write-Host "  сертификат вложен в архив для trust-cert.ps1" -ForegroundColor Green
    } else {
        Write-Host "  сертификат не найден, пропускаю" -ForegroundColor Yellow
    }
}

# ── 3. Чистка и вложения ──────────────────────────────────────────────────
Write-Host ""
Write-Host "[3/5] Чистка и вложения..." -ForegroundColor Cyan

Get-ChildItem $outDir -Include "*.pdb", "*.xml" -Recurse -ErrorAction SilentlyContinue |
    Remove-Item -Force

# install.bat/install.ps1 больше не существуют. Причина: install.bat (батник,
# двойным щелчком поднимающий powershell -ExecutionPolicy Bypass) на файле с
# меткой «загружено из интернета» блокируется Application Control наглухо —
# «Dangerous file extension from the web», проверено 2026-08-17. Ни подпись,
# ни доверенный сертификат этого не лечат: блокируется само расширение .bat.
# Взамен: недостающий .NET Runtime показывает нативным диалогом сам apphost
# (подписан Microsoft, ничем не блокируется), а ярлык на рабочем столе создаёт
# сам процесс — кнопка в приложении, без стороннего скрипта.
if ($Sign) {
    $src = Join-Path $root "trust-cert.ps1"
    if (Test-Path $src) { Copy-Item $src (Join-Path $outDir "trust-cert.ps1"); Write-Host "  вложен trust-cert.ps1" }
}

$readme = @"
NetAudit $Version

УСТАНОВКА
  Запустите NetAudit.App.exe.
  Если Windows покажет окно про отсутствующий компонент — это .NET Desktop
  Runtime, кнопка в этом же окне скачает его с официального сайта Microsoft.
  Ярлык на рабочем столе приложение при первом запуске предложит создать само.

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

# ── 5. Установщик ─────────────────────────────────────────────────────────
# Собирается поверх уже опубликованной папки $outDir. Нужен не только ради
# «приличного вида»: только установщик может один раз, под правами администратора,
# создать задачу в Планировщике, которая потом запускает NetAudit с повышенными
# правами без запроса UAC при каждом старте — без этого счётчик FPS требует
# подтверждения каждый раз (см. installer\register-task.ps1)
Write-Host ""
Write-Host "[5/6] Установщик..." -ForegroundColor Cyan

$setupPath = $null
$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    Write-Host "  Inno Setup не найден — установщик пропущен." -ForegroundColor Yellow
    Write-Host "  Поставить: winget install --id JRSoftware.InnoSetup" -ForegroundColor Yellow
} else {
    & $iscc "/DMyAppVersion=$Version" (Join-Path $root "installer\NetAudit.iss") | Out-Null
    if ($LASTEXITCODE -eq 0) {
        $setupPath = Join-Path $distDir "NetAudit-Setup-v$Version.exe"
        $setupSha  = (Get-FileHash $setupPath -Algorithm SHA256).Hash
        $setupMb   = [math]::Round((Get-Item $setupPath).Length / 1MB, 1)
        Write-Host "  $setupPath  ($setupMb МБ)" -ForegroundColor Green
        Write-Host "  SHA-256: $setupSha"
    } else {
        Write-Host "  Сборка установщика не удалась (код $LASTEXITCODE)." -ForegroundColor Yellow
    }
}

# ── 6. version.json ───────────────────────────────────────────────────────
Write-Host ""
Write-Host "[6/6] version.json..." -ForegroundColor Cyan

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
if ($setupPath) {
    Write-Host "  2. gh release create v$Version ""$setupPath"" ""$zipPath"" --title ""NetAudit $Version"" --notes ""$Notes"""
    Write-Host "     (установщик первым — GitHub показывает его верхним в списке файлов)"
} else {
    Write-Host "  2. gh release create v$Version ""$zipPath"" --title ""NetAudit $Version"" --notes ""$Notes"""
}
Write-Host ""
Write-Host "  version.json должен лежать в ветке по адресу, указанном в настройках"
Write-Host "  приложения (UpdateCheckUrl). Ссылка на сырой файл выглядит так:"
Write-Host "  https://raw.githubusercontent.com/$Repo/main/NetAudit/version.json" -ForegroundColor Yellow
Write-Host ""

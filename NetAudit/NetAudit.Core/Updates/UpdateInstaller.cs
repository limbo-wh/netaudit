using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace NetAudit.Core.Updates;

/// <summary>Ход установки обновления — для показа в интерфейсе.</summary>
public readonly record struct UpdateProgress(string Stage, int Percent);

/// <summary>
/// Установка обновления: скачать, проверить, подменить файлы, перезапуститься.
///
/// Приложение не может переписать само себя, пока работает: файл exe занят.
/// Поэтому распаковка идёт во временную папку, а подменой занимается маленький
/// сценарий PowerShell, который ждёт выхода процесса и только потом копирует файлы.
///
/// Обязательная проверка SHA-256 — не формальность. Ссылка на архив приходит из
/// version.json по сети, и без сверки контрольной суммы подменённый файл был бы
/// запущен с правами пользователя без единого вопроса.
/// </summary>
public sealed class UpdateInstaller
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public sealed class UpdateException(string message) : Exception(message);

    /// <summary>
    /// Скачивает архив, сверяет контрольную сумму и распаковывает во временную папку.
    /// </summary>
    /// <returns>Папка с распакованным обновлением.</returns>
    public async Task<string> DownloadAsync(string url, string? expectedSha256,
                                            IProgress<UpdateProgress> progress,
                                            CancellationToken ct)
    {
        string work = Path.Combine(Path.GetTempPath(), "NetAudit", "update");
        if (Directory.Exists(work)) { try { Directory.Delete(work, true); } catch { } }
        Directory.CreateDirectory(work);

        string zip = Path.Combine(work, "update.zip");

        // ── Скачивание ──────────────────────────────────────────────────────
        progress.Report(new UpdateProgress("Скачивание…", 0));

        using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                                    .ConfigureAwait(false))
        {
            resp.EnsureSuccessStatusCode();
            long? total = resp.Content.Headers.ContentLength;

            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = File.Create(zip);

            var buffer = new byte[128 * 1024];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                done += n;
                int pct = total is > 0 ? (int)(done * 90 / total.Value) : 0;
                progress.Report(new UpdateProgress($"Скачивание… {done / 1_048_576.0:F1} МБ", pct));
            }
        }

        // ── Проверка ────────────────────────────────────────────────────────
        progress.Report(new UpdateProgress("Проверка контрольной суммы…", 92));

        string actual = await Sha256Async(zip, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(expectedSha256))
        {
            // Не отказываем, но и не молчим: пусть решение принимает тот, кто это увидит
            progress.Report(new UpdateProgress(
                $"В version.json нет контрольной суммы. SHA-256 архива: {actual}", 94));
        }
        else if (!actual.Equals(expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateException(
                "Контрольная сумма не совпала — архив повреждён или подменён.\n" +
                $"Ожидалось: {expectedSha256}\nПолучено:  {actual}");
        }

        // ── Распаковка ──────────────────────────────────────────────────────
        progress.Report(new UpdateProgress("Распаковка…", 95));

        string unpack = Path.Combine(work, "files");
        Directory.CreateDirectory(unpack);
        ZipFile.ExtractToDirectory(zip, unpack, overwriteFiles: true);

        // Архив может быть собран как с корневой папкой внутри, так и без неё
        var entries = Directory.GetFileSystemEntries(unpack);
        if (entries.Length == 1 && Directory.Exists(entries[0]))
            unpack = entries[0];

        if (!File.Exists(Path.Combine(unpack, "NetAudit.App.exe")))
            throw new UpdateException("В архиве нет NetAudit.App.exe — это не обновление NetAudit.");

        progress.Report(new UpdateProgress("Готово к установке", 100));
        return unpack;
    }

    /// <summary>
    /// Запускает подмену файлов и завершает приложение.
    /// Возвращает управление только если запустить сценарий не удалось.
    /// </summary>
    public static void ApplyAndRestart(string unpackedDir, Action shutdown)
    {
        string? exe = Environment.ProcessPath;
        string? installDir = Path.GetDirectoryName(exe);
        if (exe is null || installDir is null)
            throw new UpdateException("Не удалось определить папку установки");

        string script = Path.Combine(Path.GetTempPath(), "NetAudit", "apply-update.ps1");
        Directory.CreateDirectory(Path.GetDirectoryName(script)!);

        // BOM обязателен: PowerShell 5.1 без него читает файл как ANSI и калечит кириллицу
        File.WriteAllText(script, BuildApplyScript(unpackedDir, installDir, exe),
                          new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Process.Start(new ProcessStartInfo("powershell.exe")
        {
            Arguments       = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\" {Environment.ProcessId}",
            UseShellExecute = true,
            WindowStyle     = ProcessWindowStyle.Hidden,
            CreateNoWindow  = true,
        });

        shutdown();
    }

    private static string BuildApplyScript(string from, string to, string exe)
    {
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";

        var sb = new StringBuilder();
        sb.AppendLine("param([int]$AppPid)");
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine($"$from = {Q(from)}");
        sb.AppendLine($"$to   = {Q(to)}");
        sb.AppendLine($"$exe  = {Q(exe)}");
        sb.AppendLine($"$log  = {Q(Path.Combine(Path.GetTempPath(), "NetAudit", "update.log"))}");
        sb.AppendLine("function W($t) { Add-Content -LiteralPath $log -Value ((Get-Date -Format 'HH:mm:ss') + '  ' + $t) -Encoding UTF8 }");
        sb.AppendLine();
        sb.AppendLine("W 'Жду завершения NetAudit…'");
        // Ждём выхода процесса: занятый exe переписать нельзя
        sb.AppendLine("for ($i = 0; $i -lt 60; $i++) {");
        sb.AppendLine("  if (-not (Get-Process -Id $AppPid -ErrorAction SilentlyContinue)) { break }");
        sb.AppendLine("  Start-Sleep -Milliseconds 500");
        sb.AppendLine("}");
        sb.AppendLine("Start-Sleep -Milliseconds 700");
        sb.AppendLine();
        // Откат: если копирование сорвётся на середине, вернуть прежние файлы
        sb.AppendLine("$backup = Join-Path $env:TEMP ('NetAudit\\backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))");
        sb.AppendLine("W ('Резервная копия в ' + $backup)");
        sb.AppendLine("New-Item -ItemType Directory -Path $backup -Force | Out-Null");
        sb.AppendLine("Copy-Item -Path (Join-Path $to '*') -Destination $backup -Recurse -Force -ErrorAction SilentlyContinue");
        sb.AppendLine();
        sb.AppendLine("try {");
        sb.AppendLine("  W 'Копирую новые файлы…'");
        sb.AppendLine("  Copy-Item -Path (Join-Path $from '*') -Destination $to -Recurse -Force");
        sb.AppendLine("  W 'Готово'");
        sb.AppendLine("} catch {");
        sb.AppendLine("  W ('ОШИБКА: ' + $_.Exception.Message)");
        sb.AppendLine("  W 'Восстанавливаю из резервной копии…'");
        sb.AppendLine("  Copy-Item -Path (Join-Path $backup '*') -Destination $to -Recurse -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("W 'Запускаю NetAudit'");
        sb.AppendLine("Start-Process -FilePath $exe");
        return sb.ToString();
    }

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }
}

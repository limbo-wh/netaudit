using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace NetAudit.Core.Diagnostics;

/// <summary>Насколько глубоко сбрасывать сеть. Порядок — по возрастанию последствий.</summary>
public enum ResetScope
{
    /// <summary>Кэши имён и ARP. Связь не рвётся, самый безопасный вариант.</summary>
    Caches,
    /// <summary>То же плюс переполучение адреса у DHCP. Разрыв на 2–5 секунд.</summary>
    Dhcp,
    /// <summary>То же плюс выключение и включение сетевого адаптера. Разрыв на 5–15 секунд.</summary>
    Adapter,
    /// <summary>То же плюс сброс Winsock и стека TCP/IP. Требует перезагрузки Windows.</summary>
    Stack,
}

/// <summary>
/// «Перезагрузка сети» без перезагрузки Windows.
///
/// Всё выполняется одним повышенным PowerShell-скриптом, а не серией отдельных
/// запусков: UAC спрашивается один раз, и — что важнее — команда включения
/// адаптера гарантированно идёт следом за выключением даже если NetAudit в этот
/// момент закроется. Иначе можно остаться без сети.
///
/// PowerShell, а не cmd, потому что имена адаптеров бывают кириллическими
/// («Беспроводная сеть»), а батник читается в кодировке OEM и такое имя ломает.
/// Скрипт пишется с BOM — PowerShell 5.1 без BOM читает файл как ANSI.
/// </summary>
public sealed class NetworkResetService
{
    /// <summary>Пользователь отменил запрос UAC.</summary>
    public sealed class CancelledByUserException() : Exception("Повышение прав отменено");

    /// <summary>Имя адаптера, через который сейчас идёт трафик (тот, у кого есть шлюз).</summary>
    public static string? GetActiveAdapterName()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up
                          && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback
                          && ni.GetIPProperties().GatewayAddresses
                                .Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork
                                       && !g.Address.Equals(System.Net.IPAddress.Any)))
                .Select(ni => ni.Name)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    public static string Describe(ResetScope scope) => scope switch
    {
        ResetScope.Caches  => "Кэш DNS, ARP и NetBIOS. Соединение не разрывается.",
        ResetScope.Dhcp    => "То же плюс новый адрес у роутера. Разрыв на 2–5 секунд.",
        ResetScope.Adapter => "То же плюс перезапуск сетевого адаптера. Разрыв на 5–15 секунд.",
        ResetScope.Stack   => "Полный сброс Winsock и TCP/IP. Подействует только после перезагрузки Windows.",
        _                  => "",
    };

    public async Task RunAsync(ResetScope scope, IProgress<TestLine> log, CancellationToken ct)
    {
        string adapter = GetActiveAdapterName() ?? "";

        log.Report(TestLine.Head($"Сброс сети — уровень «{ScopeName(scope)}»"));
        log.Report(TestLine.Dim(Describe(scope)));
        log.Report(adapter.Length > 0
            ? TestLine.Dim($"Активный адаптер: {adapter}")
            : TestLine.Warn("Активный адаптер не определён — перезапуск адаптера будет пропущен"));

        if (scope >= ResetScope.Adapter && adapter.Length == 0)
            log.Report(TestLine.Warn("Нет адаптера со шлюзом: возможно, связь уже потеряна"));

        string dir     = Path.Combine(Path.GetTempPath(), "NetAudit");
        Directory.CreateDirectory(dir);
        string script  = Path.Combine(dir, "net-reset.ps1");
        string logFile = Path.Combine(dir, "net-reset.log");

        if (File.Exists(logFile)) { try { File.Delete(logFile); } catch { } }

        // BOM обязателен: PowerShell 5.1 без него читает скрипт в ANSI и калечит кириллицу
        await File.WriteAllTextAsync(script, BuildScript(scope, adapter, logFile),
                                     new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), ct)
                  .ConfigureAwait(false);

        log.Report(TestLine.Dim("Запрашиваю права администратора…"));

        var psi = new ProcessStartInfo("powershell.exe")
        {
            Arguments      = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\"",
            UseShellExecute = true,   // обязательно для Verb = runas
            Verb            = "runas",
            CreateNoWindow  = true,
            WindowStyle     = ProcessWindowStyle.Hidden,
        };

        Process? proc;
        try
        {
            proc = Process.Start(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)   // ERROR_CANCELLED
        {
            log.Report(TestLine.Bad("Отменено: без прав администратора сброс сети невозможен"));
            throw new CancelledByUserException();
        }

        if (proc is null)
        {
            log.Report(TestLine.Bad("Не удалось запустить PowerShell"));
            return;
        }

        using (proc)
        {
            log.Report(TestLine.Dim("Выполняется, не выключайте компьютер…"));
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }

        foreach (var line in ReadLog(logFile))
            log.Report(line);

        log.Report(TestLine.Empty);
        if (scope == ResetScope.Stack)
            log.Report(TestLine.Warn("⚠ Сброс Winsock и TCP/IP вступит в силу только после перезагрузки Windows"));
        else
            log.Report(TestLine.Good("✓ Готово. Если связь не поднялась за 15 секунд — попробуйте уровень выше"));
    }

    private static string ScopeName(ResetScope s) => s switch
    {
        ResetScope.Caches  => "кэши",
        ResetScope.Dhcp    => "кэши + DHCP",
        ResetScope.Adapter => "перезапуск адаптера",
        ResetScope.Stack   => "полный сброс стека",
        _                  => s.ToString(),
    };

    /// <summary>Читает лог скрипта и раскрашивает строки.</summary>
    private static IEnumerable<TestLine> ReadLog(string path)
    {
        if (!File.Exists(path))
        {
            yield return TestLine.Bad("Скрипт не оставил лога — вероятно, он не запустился");
            yield break;
        }

        // yield нельзя вызывать из catch, поэтому ошибку сначала откладываем в переменную
        string[]? lines = null;
        string? readError = null;
        try { lines = File.ReadAllLines(path, Encoding.UTF8); }
        catch (Exception ex) { readError = ex.Message; }

        if (lines is null)
        {
            yield return TestLine.Bad($"Не удалось прочитать лог: {readError}");
            yield break;
        }

        foreach (var raw in lines)
        {
            string s = raw.TrimEnd();
            if (s.Length == 0) { yield return TestLine.Empty; continue; }

            if (s.StartsWith("### ", StringComparison.Ordinal))
                yield return TestLine.Head("  " + s[4..]);
            else if (s.StartsWith("[ОШИБКА]", StringComparison.Ordinal))
                yield return TestLine.Bad("  " + s);
            else if (s.StartsWith("[ОК]", StringComparison.Ordinal))
                yield return TestLine.Good("  " + s);
            else if (s.StartsWith("[ПРОПУСК]", StringComparison.Ordinal))
                yield return TestLine.Warn("  " + s);
            else
                yield return TestLine.Info("  " + s);
        }
    }

    /// <summary>Экранирование для одинарных кавычек PowerShell.</summary>
    private static string Q(string s) => "'" + s.Replace("'", "''") + "'";

    private static string BuildScript(ResetScope scope, string adapter, string logFile)
    {
        var sb = new StringBuilder();

        sb.AppendLine("$ErrorActionPreference = 'Continue'");
        sb.AppendLine($"$log = {Q(logFile)}");
        sb.AppendLine("$enc = New-Object System.Text.UTF8Encoding($false)");
        sb.AppendLine("function W([string]$t) { [System.IO.File]::AppendAllText($log, $t + [Environment]::NewLine, $enc) }");
        sb.AppendLine("function Run([string]$title, [string]$exe, [string[]]$argv) {");
        sb.AppendLine("  W ''");
        sb.AppendLine("  W ('### ' + $title)");
        sb.AppendLine("  try {");
        sb.AppendLine("    $out = & $exe @argv 2>&1 | Out-String");
        sb.AppendLine("    foreach ($l in ($out -split \"`r?`n\")) { if ($l.Trim().Length -gt 0) { W $l } }");
        sb.AppendLine("    if ($LASTEXITCODE -eq 0) { W '[ОК]' } else { W ('[ОШИБКА] код возврата ' + $LASTEXITCODE) }");
        sb.AppendLine("  } catch { W ('[ОШИБКА] ' + $_.Exception.Message) }");
        sb.AppendLine("}");
        sb.AppendLine();

        // ── Уровень 1: кэши. Безопасно, связь не рвётся ──────────────────────
        sb.AppendLine("Run 'Очистка кэша DNS' 'ipconfig' @('/flushdns')");
        sb.AppendLine("Run 'Очистка таблицы ARP' 'arp' @('-d','*')");
        sb.AppendLine("Run 'Очистка кэша имён NetBIOS' 'nbtstat' @('-R')");
        sb.AppendLine("Run 'Перерегистрация имён NetBIOS' 'nbtstat' @('-RR')");

        // ── Уровень 2: DHCP ──────────────────────────────────────────────────
        if (scope >= ResetScope.Dhcp)
        {
            sb.AppendLine("Run 'Освобождение IP-адреса' 'ipconfig' @('/release')");
            sb.AppendLine("Run 'Получение нового IP-адреса' 'ipconfig' @('/renew')");
            sb.AppendLine("Run 'Перерегистрация в DNS' 'ipconfig' @('/registerdns')");
        }

        // ── Уровень 3: перезапуск адаптера ───────────────────────────────────
        if (scope >= ResetScope.Adapter)
        {
            sb.AppendLine("W ''");
            sb.AppendLine("W '### Перезапуск сетевого адаптера'");
            if (adapter.Length == 0)
            {
                sb.AppendLine("W '[ПРОПУСК] активный адаптер не определён'");
            }
            else
            {
                sb.AppendLine($"$nic = {Q(adapter)}");
                sb.AppendLine("try {");
                sb.AppendLine("  W ('Выключаю: ' + $nic)");
                sb.AppendLine("  Disable-NetAdapter -Name $nic -Confirm:$false -ErrorAction Stop");
                sb.AppendLine("  Start-Sleep -Seconds 3");
                sb.AppendLine("  W ('Включаю: ' + $nic)");
                sb.AppendLine("  Enable-NetAdapter -Name $nic -Confirm:$false -ErrorAction Stop");
                sb.AppendLine("  Start-Sleep -Seconds 4");
                // Страховка: остаться с выключенным адаптером хуже, чем не сбросить его вовсе
                sb.AppendLine("  $st = (Get-NetAdapter -Name $nic -ErrorAction SilentlyContinue).Status");
                sb.AppendLine("  if ($st -ne 'Up') {");
                sb.AppendLine("    W ('Адаптер в состоянии ' + $st + ', повторная попытка включения')");
                sb.AppendLine("    Enable-NetAdapter -Name $nic -Confirm:$false -ErrorAction SilentlyContinue");
                sb.AppendLine("    Start-Sleep -Seconds 4");
                sb.AppendLine("    $st = (Get-NetAdapter -Name $nic -ErrorAction SilentlyContinue).Status");
                sb.AppendLine("  }");
                sb.AppendLine("  if ($st -eq 'Up') { W '[ОК] адаптер поднят' } else { W ('[ОШИБКА] адаптер в состоянии ' + $st) }");
                sb.AppendLine("} catch {");
                sb.AppendLine("  W ('[ОШИБКА] ' + $_.Exception.Message)");
                // Что бы ни случилось выше — пытаемся вернуть адаптер во включённое состояние
                sb.AppendLine("  Enable-NetAdapter -Name $nic -Confirm:$false -ErrorAction SilentlyContinue");
                sb.AppendLine("}");
                sb.AppendLine("Run 'Получение адреса после перезапуска' 'ipconfig' @('/renew')");
            }
        }

        // ── Уровень 4: стек. Действует только после перезагрузки ─────────────
        if (scope >= ResetScope.Stack)
        {
            sb.AppendLine("Run 'Сброс Winsock' 'netsh' @('winsock','reset')");
            sb.AppendLine("Run 'Сброс стека IPv4' 'netsh' @('int','ip','reset')");
            sb.AppendLine("Run 'Сброс стека IPv6' 'netsh' @('int','ipv6','reset')");
        }

        // ── Итоговое состояние ───────────────────────────────────────────────
        sb.AppendLine("W ''");
        sb.AppendLine("W '### Состояние сети после сброса'");
        sb.AppendLine("try {");
        sb.AppendLine("  Get-NetIPConfiguration | Where-Object { $_.IPv4DefaultGateway -ne $null } | ForEach-Object {");
        sb.AppendLine("    W ('Адаптер : ' + $_.InterfaceAlias)");
        sb.AppendLine("    W ('IP      : ' + ($_.IPv4Address.IPAddress -join ', '))");
        sb.AppendLine("    W ('Шлюз    : ' + ($_.IPv4DefaultGateway.NextHop -join ', '))");
        sb.AppendLine("    W ('DNS     : ' + ($_.DNSServer.ServerAddresses -join ', '))");
        sb.AppendLine("  }");
        sb.AppendLine("} catch { W ('[ОШИБКА] ' + $_.Exception.Message) }");

        return sb.ToString();
    }
}

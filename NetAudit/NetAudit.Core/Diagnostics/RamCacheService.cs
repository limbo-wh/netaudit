using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace NetAudit.Core.Diagnostics;

/// <summary>
/// «Очистка кэша ОЗУ» — освобождает страницы, которые Windows держит про запас на случай,
/// если понадобятся снова (standby list), не трогая память работающих программ. Тот же
/// приём, что у утилиты RAMMap («Empty → Standby List») и известной EmptyStandbyList.exe:
/// системный вызов <c>NtSetSystemInformation(SystemMemoryListInformation, MemoryPurgeStandbyList)</c>.
///
/// Требует прав администратора — как и <see cref="NetworkResetService"/>, действие
/// выполняется одним повышенным PowerShell-скриптом, а не поднятием всего приложения
/// до администратора ради одной кнопки.
/// </summary>
public sealed class RamCacheService
{
    /// <summary>Пользователь отменил запрос UAC.</summary>
    public sealed class CancelledByUserException() : Exception("Повышение прав отменено");

    [DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    private static long AvailableMb()
    {
        var mem = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref mem) ? (long)(mem.ullAvailPhys / 1_048_576) : -1;
    }

    public async Task RunAsync(IProgress<TestLine> log, CancellationToken ct)
    {
        log.Report(TestLine.Head("Очистка кэша оперативной памяти"));
        log.Report(TestLine.Dim("Освобождает страницы, которые Windows держит про запас (список ожидания)."));
        log.Report(TestLine.Dim("Память работающих программ не трогает."));

        long before = AvailableMb();
        if (before >= 0) log.Report(TestLine.Dim($"Свободно сейчас: {before} МБ"));

        string dir     = Path.Combine(Path.GetTempPath(), "NetAudit");
        Directory.CreateDirectory(dir);
        string script  = Path.Combine(dir, "ram-purge.ps1");
        string logFile = Path.Combine(dir, "ram-purge.log");

        if (File.Exists(logFile)) { try { File.Delete(logFile); } catch { } }

        // BOM обязателен: PowerShell 5.1 без него читает скрипт в ANSI и калечит кириллицу
        await File.WriteAllTextAsync(script, BuildScript(logFile),
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
            log.Report(TestLine.Bad("Отменено: без прав администратора очистка невозможна"));
            throw new CancelledByUserException();
        }

        if (proc is null)
        {
            log.Report(TestLine.Bad("Не удалось запустить PowerShell"));
            return;
        }

        using (proc)
        {
            log.Report(TestLine.Dim("Выполняется…"));
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }

        foreach (var line in ReadLog(logFile))
            log.Report(line);

        log.Report(TestLine.Empty);
        long after = AvailableMb();
        if (before >= 0 && after >= 0)
            log.Report(TestLine.Good($"✓ Готово. Свободно сейчас: {after} МБ (было {before} МБ)"));
        else
            log.Report(TestLine.Good("✓ Готово"));
    }

    /// <summary>Читает лог скрипта и раскрашивает строки — тот же формат, что у NetworkResetService.</summary>
    private static IEnumerable<TestLine> ReadLog(string path)
    {
        if (!File.Exists(path))
        {
            yield return TestLine.Bad("Скрипт не оставил лога — вероятно, он не запустился");
            yield break;
        }

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
            else
                yield return TestLine.Info("  " + s);
        }
    }

    /// <summary>
    /// Отдельный C#-тип компилируется на лету через Add-Type — тот же системный вызов,
    /// что у EmptyStandbyList.exe/RAMMap. Привилегия SeProfileSingleProcessPrivilege
    /// есть у группы Administrators, но по умолчанию выключена в токене — включаем сами.
    /// </summary>
    private static string BuildScript(string logFile)
    {
        var sb = new StringBuilder();

        sb.AppendLine("$ErrorActionPreference = 'Continue'");
        sb.AppendLine($"$log = '{logFile.Replace("'", "''")}'");
        sb.AppendLine("$enc = New-Object System.Text.UTF8Encoding($false)");
        sb.AppendLine("function W([string]$t) { [System.IO.File]::AppendAllText($log, $t + [Environment]::NewLine, $enc) }");
        sb.AppendLine();

        sb.AppendLine("$csSource = @'");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Runtime.InteropServices;");
        sb.AppendLine("public static class NetAuditRamPurge");
        sb.AppendLine("{");
        sb.AppendLine("    [DllImport(\"ntdll.dll\")]");
        sb.AppendLine("    static extern int NtSetSystemInformation(int infoClass, IntPtr info, int len);");
        sb.AppendLine("    [DllImport(\"advapi32.dll\", SetLastError = true)]");
        sb.AppendLine("    static extern bool OpenProcessToken(IntPtr h, uint access, out IntPtr token);");
        sb.AppendLine("    [DllImport(\"advapi32.dll\", SetLastError = true)]");
        sb.AppendLine("    static extern bool LookupPrivilegeValue(string host, string name, out LUID luid);");
        sb.AppendLine("    [DllImport(\"advapi32.dll\", SetLastError = true)]");
        sb.AppendLine("    static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES newState, uint len, IntPtr prev, IntPtr retLen);");
        sb.AppendLine("    [DllImport(\"kernel32.dll\")]");
        sb.AppendLine("    static extern IntPtr GetCurrentProcess();");
        sb.AppendLine("    [DllImport(\"kernel32.dll\")]");
        sb.AppendLine("    static extern bool CloseHandle(IntPtr h);");
        sb.AppendLine();
        sb.AppendLine("    [StructLayout(LayoutKind.Sequential)] struct LUID { public uint Low; public int High; }");
        sb.AppendLine("    [StructLayout(LayoutKind.Sequential)] struct LUID_AND_ATTRIBUTES { public LUID Luid; public uint Attributes; }");
        sb.AppendLine("    [StructLayout(LayoutKind.Sequential)] struct TOKEN_PRIVILEGES { public uint Count; public LUID_AND_ATTRIBUTES Priv; }");
        sb.AppendLine();
        sb.AppendLine("    const uint TOKEN_ADJUST_PRIVILEGES = 0x20;");
        sb.AppendLine("    const uint TOKEN_QUERY = 0x8;");
        sb.AppendLine("    const uint SE_PRIVILEGE_ENABLED = 0x2;");
        sb.AppendLine();
        // Классический компилятор CodeDom за Add-Type (Windows PowerShell 5.1) не понимает
        // C# 7 «out IntPtr token» на месте — переменные под out нужно объявлять заранее
        sb.AppendLine("    static bool EnablePrivilege(string name)");
        sb.AppendLine("    {");
        sb.AppendLine("        IntPtr token;");
        sb.AppendLine("        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out token))");
        sb.AppendLine("            return false;");
        sb.AppendLine("        try");
        sb.AppendLine("        {");
        sb.AppendLine("            LUID luid;");
        sb.AppendLine("            if (!LookupPrivilegeValue(null, name, out luid)) return false;");
        sb.AppendLine("            var tp = new TOKEN_PRIVILEGES { Count = 1, Priv = new LUID_AND_ATTRIBUTES { Luid = luid, Attributes = SE_PRIVILEGE_ENABLED } };");
        sb.AppendLine("            return AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero) && Marshal.GetLastWin32Error() == 0;");
        sb.AppendLine("        }");
        sb.AppendLine("        finally { CloseHandle(token); }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    public static string Purge()");
        sb.AppendLine("    {");
        sb.AppendLine("        if (!EnablePrivilege(\"SeProfileSingleProcessPrivilege\"))");
        sb.AppendLine("            return \"ERR: не удалось включить SeProfileSingleProcessPrivilege\";");
        sb.AppendLine();
        sb.AppendLine("        IntPtr buf = Marshal.AllocHGlobal(4);");
        sb.AppendLine("        try");
        sb.AppendLine("        {");
        sb.AppendLine("            Marshal.WriteInt32(buf, 4); // MemoryPurgeStandbyList");
        sb.AppendLine("            int status = NtSetSystemInformation(80, buf, 4); // SystemMemoryListInformation");
        sb.AppendLine("            return status == 0 ? \"OK\" : \"ERR: NtSetSystemInformation вернул 0x\" + status.ToString(\"X8\");");
        sb.AppendLine("        }");
        sb.AppendLine("        finally { Marshal.FreeHGlobal(buf); }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine("'@");
        sb.AppendLine();
        sb.AppendLine("W '### Очистка списка ожидания (standby list)'");
        sb.AppendLine("try {");
        sb.AppendLine("    Add-Type -TypeDefinition $csSource -Language CSharp");
        sb.AppendLine("    $result = [NetAuditRamPurge]::Purge()");
        sb.AppendLine("    if ($result -eq 'OK') { W '[ОК] список ожидания очищен' }");
        sb.AppendLine("    else { W ('[ОШИБКА] ' + $result) }");
        sb.AppendLine("} catch { W ('[ОШИБКА] ' + $_.Exception.Message) }");

        return sb.ToString();
    }
}

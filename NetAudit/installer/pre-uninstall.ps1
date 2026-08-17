# Гарантированно освобождает файлы перед удалением.
#
# CloseApplications=yes в NetAudit.iss опирается на Windows Restart Manager,
# а тот ловит далеко не каждую заблокированную библиотеку — у WPF-приложения
# с SkiaSharp/ScottPlot часть нативных DLL подгружается не тем путём, который
# RM отслеживает. Проверено на этой машине: с одним CloseApplications=yes
# после удаления на диске осталось 13 из 16 файлов, процесс остался жив.
#
# Stop-Process возвращается сразу после сигнала на завершение, а не после
# того, как ОС отпустила дескрипторы файлов — отсюда короткий опрос вместо
# одной попытки.
Get-Process -Name "NetAudit.App" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

for ($i = 0; $i -lt 20; $i++) {
    if (-not (Get-Process -Name "NetAudit.App" -ErrorAction SilentlyContinue)) { break }
    Start-Sleep -Milliseconds 300
}

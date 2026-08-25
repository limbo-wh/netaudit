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

# Температура CPU/GPU (TemperatureProbe, LibreHardwareMonitorLib) грузит вспомогательный
# драйвер ядра WinRing0 при работе от администратора и называет его по имени процесса —
# "R0NetAudit_App". TemperatureProbe.Dispose() выгружает его сам при штатном закрытии
# программы, но Stop-Process -Force выше закрытие Dispose не запускает — сигнал на
# завершение получает процесс, а не превентивную очистку. Драйвер остаётся висеть
# в ядре до перезагрузки, а его файл NetAudit.App.sys в {app} остаётся заблокирован
# точно так же, как заблокирован модуль, загруженный в память живого процесса.
# Без этого шага удаление/обновление молча оставляло один файл на диске и не могло
# удалить саму папку. sc.exe, а не Get-Service/Stop-Service: те кернел-драйвер
# в этой конфигурации не видят вовсе, только driverquery/sc его находят.
$driverName = "R0NetAudit_App"
sc.exe stop $driverName 2>&1 | Out-Null
sc.exe delete $driverName 2>&1 | Out-Null

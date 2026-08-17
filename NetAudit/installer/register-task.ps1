# Регистрирует задачу в Планировщике заданий для запуска NetAudit с правами
# администратора без запроса UAC при каждом старте.
#
# Механизм: связка RunLevel=Highest + LogonType=Interactive. Задачу создаёт
# администратор один раз (сейчас, во время установки — сам установщик уже
# работает повышенным). После этого Планировщик заданий — системная служба,
# запущенная от SYSTEM, — сам поднимает процесс с повышенным токеном текущего
# интерактивного входа, минуя обычный путь элевации через explorer.exe и
# связанный с ним запрос согласия. Это стандартный, широко документированный
# приём (его используют многие утилиты, которым изредка нужны права), не обход
# защиты: пользователь уже администратор своей машины, и Windows framework
# API это разрешает через задокументированный публичный интерфейс.
#
# Отличие от ярлыка с флагом RunAsAdmin (DesktopShortcut.CreateElevated в
# самом приложении): тот запрашивает UAC каждый раз, этот — только один раз,
# при установке.
param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [Parameter(Mandatory = $true)][string]$TaskName
)

$ErrorActionPreference = 'Stop'

try {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue

    $action    = New-ScheduledTaskAction -Execute $ExePath
    $principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" `
                                            -RunLevel Highest -LogonType Interactive
    $settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
                                              -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero)

    Register-ScheduledTask -TaskName $TaskName -Action $action -Principal $principal `
                           -Settings $settings -Force | Out-Null
}
catch {
    # Необязательное удобство: если не получилось, падать не должно ничего —
    # ни установка, ни сама программа. Просто не будет ярлыка без UAC-запроса
    Write-Warning "Не удалось создать задачу автоматического повышения прав: $_"
}

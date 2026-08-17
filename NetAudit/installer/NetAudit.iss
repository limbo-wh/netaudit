; Установщик NetAudit — Inno Setup.
;
; Собирается отдельно от NetAudit.App.csproj: это упаковка уже опубликованных
; файлов (dist\NetAudit-vX.Y.Z\, которые готовит release.ps1), а не сборка кода.
; Версия передаётся снаружи: ISCC.exe /DMyAppVersion=1.0.4 installer\NetAudit.iss
;
; Помимо обычной установки в Program Files и записи в «Установка и удаление
; программ», при установке создаётся задача в Планировщике заданий с
; RunLevel=Highest — она позволяет запускать NetAudit с правами администратора
; без запроса UAC при каждом старте (нужно только счётчику FPS в оверлее,
; см. register-task.ps1 — там подробно объяснено, как и почему это работает).

#define MyAppName "NetAudit"
#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif
#define MyAppPublisher "NetAudit"
#define TaskName "NetAudit (администратор)"

[Setup]
; Сгенерирован один раз (2026-08-17) и не должен меняться — по нему Windows
; узнаёт установленные версии друг друга при обновлении через установщик
AppId={{F134AF95-2332-4C85-85B6-F64ADA1ACACB}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\NetAudit
DefaultGroupName=NetAudit
DisableProgramGroupPage=yes
; Program Files, задача в Планировщике и запись в реестр удаления — всё это
; требует прав администратора уже на этапе установки
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=NetAudit-Setup-v{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\NetAudit.App.exe
SetupIconFile=..\NetAudit.App\NetAudit.ico
; Через Restart Manager сам находит и закрывает процессы, держащие файлы —
; без этого при обновлении поверх запущенной программы или удалении сразу
; после закрытия часть DLL остаётся залоченной и не удаляется молча.
; Проверено на этой машине: без этого флага деинсталляция сразу после
; Stop-Process оставила 13 из 16 файлов на диске
CloseApplications=yes
RestartApplications=no
; Сборка без подписи — та же причина, что и у самого NetAudit.App.exe
; (см. NetAudit.App.csproj и stack.md): самоподписанный сертификат без
; локального доверия на чужой машине блокируется Smart App Control жёстче,
; чем вообще неподписанный файл

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать значок на рабочем столе"; GroupDescription: "Дополнительные значки:"
Name: "desktopicon_admin"; Description: "Создать значок «{#TaskName}» на рабочем столе — запуск с правами администратора без запроса UAC при каждом разе, нужно для счётчика FPS"; GroupDescription: "Дополнительные значки:"

[Files]
Source: "..\dist\NetAudit-v{#MyAppVersion}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "register-task.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "unregister-task.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "pre-uninstall.ps1"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\NetAudit"; Filename: "{app}\NetAudit.App.exe"
Name: "{group}\{#TaskName}"; Filename: "{sys}\schtasks.exe"; Parameters: "/Run /TN ""{#TaskName}"""; IconFilename: "{app}\NetAudit.App.exe"; Comment: "Запуск с правами администратора — нужно для счётчика FPS в оверлее"
Name: "{group}\Удалить NetAudit"; Filename: "{uninstallexe}"
Name: "{autodesktop}\NetAudit"; Filename: "{app}\NetAudit.App.exe"; Tasks: desktopicon
Name: "{autodesktop}\{#TaskName}"; Filename: "{sys}\schtasks.exe"; Parameters: "/Run /TN ""{#TaskName}"""; IconFilename: "{app}\NetAudit.App.exe"; Tasks: desktopicon_admin

[Run]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\register-task.ps1"" -ExePath ""{app}\NetAudit.App.exe"" -TaskName ""{#TaskName}"""; Flags: runhidden waituntilterminated; StatusMsg: "Настройка запуска с правами администратора..."
Filename: "{app}\NetAudit.App.exe"; Description: "Запустить NetAudit"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Выполняются до удаления файлов — {app}\*.ps1 ещё на месте. Сначала гарантированно
; закрыть процесс (CloseApplications через Restart Manager ловит не каждую
; заблокированную DLL — проверено), потом снять задачу из планировщика
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\pre-uninstall.ps1"""; Flags: runhidden waituntilterminated; RunOnceId: "CloseApp"
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\unregister-task.ps1"" -TaskName ""{#TaskName}"""; Flags: runhidden waituntilterminated; RunOnceId: "RemoveScheduledTask"

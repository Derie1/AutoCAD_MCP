@echo off
rem AutoCAD MCP Bridge installer. Run from the unpacked release folder, next to AcadMcpBridge.bundle.zip.
rem   INSTALL.BAT               - interactive: language, AutoCAD version, AI clients
rem   INSTALL.BAT 2026 [EN|RU]  - install for AutoCAD 2026 without questions; also A = all supported, U = uninstall
rem The MCP server is Contents\Server\acad_mcp_server.dll, started by the AI client as
rem "dotnet.exe acad_mcp_server.dll". It registers itself: --register claude-desktop / claude-code / codex.
rem Versions up to 0.2.x used Python: their environment %LOCALAPPDATA%\AcadMcpBridge\venv is removed here.
rem Overrides, for tests and development:
rem   ACADMCP_TARGET          plugin bundle folder,   default %APPDATA%\Autodesk\ApplicationPlugins\AcadMcpBridge.bundle
rem   ACADMCP_DATA            config, log, old venv,  default %LOCALAPPDATA%\AcadMcpBridge
rem   ACADMCP_CLAUDE_CONFIG   Claude Desktop config,  default %APPDATA%\Claude\claude_desktop_config.json
rem   ACADMCP_CODEX_CONFIG    Codex config,           default %CODEX_HOME% or %USERPROFILE%\.codex, file config.toml
rem   ACADMCP_NO_CLIENTS=1    do not register the server in any AI client
rem   ACADMCP_NO_CLAUDE_CODE=1  leave Claude Code alone
rem The file is UTF-8 without BOM with CRLF line ends: the first command switches the console to UTF-8.
rem Messages must not contain parentheses: some are expanded inside ( ) blocks.
chcp 65001 >nul
setlocal EnableExtensions DisableDelayedExpansion

rem ---- AutoCAD year -> release series. New version: add it here, to installer\PackageContents and to the build. ----
set "SERIES_2021=R24.0"
set "SERIES_2022=R24.1"
set "SERIES_2023=R24.2"
set "SERIES_2024=R24.3"
set "SERIES_2025=R25.0"
set "SERIES_2026=R25.1"
set "SERIES_2027=R26.0"
set "KNOWN_YEARS=2021 2022 2023 2024 2025 2026 2027"
set "MIN_YEAR=2025"
set "MAX_YEAR=2027"

set "PACKAGE=%~dp0AcadMcpBridge.bundle.zip"
if defined ACADMCP_TARGET (set "TARGET=%ACADMCP_TARGET%") else set "TARGET=%APPDATA%\Autodesk\ApplicationPlugins\AcadMcpBridge.bundle"
if defined ACADMCP_DATA (set "DATA=%ACADMCP_DATA%") else set "DATA=%LOCALAPPDATA%\AcadMcpBridge"
if defined ACADMCP_CLAUDE_CONFIG (set "CLAUDE_CONFIG=%ACADMCP_CLAUDE_CONFIG%") else set "CLAUDE_CONFIG=%APPDATA%\Claude\claude_desktop_config.json"
if defined CODEX_HOME (set "CODEX_DIR=%CODEX_HOME%") else set "CODEX_DIR=%USERPROFILE%\.codex"
if defined ACADMCP_CODEX_CONFIG (set "CODEX_CONFIG=%ACADMCP_CODEX_CONFIG%") else set "CODEX_CONFIG=%CODEX_DIR%\config.toml"
set "OLD_VENV=%DATA%\venv"
set "SERVER_DLL=%TARGET%\Contents\Server\acad_mcp_server.dll"
set "INTERACTIVE=1"
if not "%~1"=="" set "INTERACTIVE=0"
set "RC=0"

title AutoCAD MCP Bridge

rem ---------------------------------------------------------------- language
set "UI_LANG=%~2"
if "%INTERACTIVE%"=="0" goto :lang_selected
echo.
echo   1 - English
echo   2 - Русский
set "UI_LANG="
set /p "UI_LANG=Select installer language [1/2]: "
if "%UI_LANG%"=="2" (set "UI_LANG=RU") else set "UI_LANG=EN"
:lang_selected
if /i "%UI_LANG%"=="RU" (call :messages_ru) else call :messages_en

rem ---------------------------------------------------------------- version
echo.
echo ================================================================
echo   %M_TITLE%
echo ================================================================
echo.
echo %M_FOUND_HEADER%
set "FOUND="
for %%Y in (%KNOWN_YEARS%) do call :detect %%Y
if not defined FOUND echo   %M_NONE_FOUND%
echo.
echo %M_SUPPORTS%
if exist "%TARGET%\versions.txt" call :show_installed

set "CHOICE=%~1"
if "%INTERACTIVE%"=="0" goto :dispatch
echo.
echo %M_PROMPT_YEAR%
echo   A - %M_PROMPT_ALL%
echo   U - %M_PROMPT_UNINSTALL%
echo   Q - %M_PROMPT_QUIT%
:ask_choice
rem Empty answer: ask again. End of input, e.g. a closed pipe: quit.
set "CHOICE="
set /p "CHOICE=%M_PROMPT_CHOICE% " || goto :end
if not defined CHOICE goto :ask_choice

:dispatch
if /i "%CHOICE%"=="Q" goto :end
if /i "%CHOICE%"=="U" goto :uninstall
if /i "%CHOICE%"=="A" (
    set "SERIES=ALL"
    goto :install
)

echo %CHOICE%| findstr /r /x "20[0-9][0-9]" >nul || goto :bad_input
if %CHOICE% LSS %MIN_YEAR% goto :unsupported
if %CHOICE% GTR %MAX_YEAR% goto :too_new
call set "SERIES=%%SERIES_%CHOICE%%%"

reg query "HKLM\SOFTWARE\Autodesk\AutoCAD\%SERIES%" >nul 2>&1 && goto :install
echo.
call echo %M_NOT_FOUND%
if "%INTERACTIVE%"=="0" goto :install
call set "CONFIRM=%M_CONFIRM%"
choice /c YN /m "%CONFIRM%"
if errorlevel 2 goto :end
goto :install

rem ---------------------------------------------------------------- install
:install
call :check_acad_closed || goto :fail
if not exist "%PACKAGE%" (
    echo.
    echo %M_NO_PACKAGE_1%
    echo %M_NO_PACKAGE_2%
    goto :fail
)

rem Versions installed earlier are kept: installing for 2025 and then for 2026 gives both.
set "LIST="
if exist "%TARGET%\versions.txt" set /p LIST=<"%TARGET%\versions.txt"
if "%SERIES%"=="ALL" set "LIST=ALL"
if "%LIST%"=="ALL" goto :list_ready
if not defined LIST (
    set "LIST=%SERIES%"
    goto :list_ready
)
echo  %LIST% | findstr /c:" %SERIES% " >nul || set "LIST=%LIST% %SERIES%"
:list_ready

set "WORK=%TEMP%\AcadMcpBridge_%RANDOM%%RANDOM%"
mkdir "%WORK%" >nul 2>&1
echo.
echo %M_EXTRACTING%
powershell -NoProfile -Command "Expand-Archive -LiteralPath '%PACKAGE%' -DestinationPath '%WORK%' -Force" >nul 2>&1
if errorlevel 1 tar -xf "%PACKAGE%" -C "%WORK%" >nul 2>&1
if not exist "%WORK%\Contents\R25\AcadMcpBridge.dll" goto :extract_failed
if not exist "%WORK%\Contents\R26\AcadMcpBridge.dll" goto :extract_failed
if not exist "%WORK%\Contents\Server\acad_mcp_server.dll" goto :extract_failed
if not exist "%WORK%\Installer\PackageContents\head.xml" goto :extract_failed

echo %M_COPYING% %TARGET%
rem /MIR also removes the Python server files of versions up to 0.2.x.
robocopy "%WORK%" "%TARGET%" /MIR /R:2 /W:1 /NFL /NDL /NJH /NJS /NP >nul
set "COPY_RC=%ERRORLEVEL%"
rmdir /s /q "%WORK%" >nul 2>&1
if %COPY_RC% GEQ 8 (
    echo %M_COPY_FAILED% %COPY_RC%
    goto :fail
)
rem Remove the "downloaded from the internet" mark so .NET loads the DLLs.
powershell -NoProfile -Command "Get-ChildItem -LiteralPath '%TARGET%' -Recurse -File | Unblock-File" >nul 2>&1

rem PackageContents.xml = head + one Components block per selected version + tail.
set "PARTS=%TARGET%\Installer\PackageContents"
set "MANIFEST=%TARGET%\PackageContents.xml"
copy /y /b "%PARTS%\head.xml" "%MANIFEST%" >nul
for %%S in (%LIST%) do copy /y /b "%MANIFEST%"+"%PARTS%\%%S.xml" "%MANIFEST%" >nul
copy /y /b "%MANIFEST%"+"%PARTS%\tail.xml" "%MANIFEST%" >nul
>"%TARGET%\versions.txt" echo %LIST%

echo.
echo %M_DONE%
echo   %TARGET%
call :show_installed

call :remove_old_python
call :write_config
call :register_clients

echo.
echo ================================================================
echo %M_RUN_HINT_1%
echo %M_RUN_HINT_2%
goto :end

rem ---------------------------------------------------------------- old Python environment
:remove_old_python
if not exist "%OLD_VENV%" exit /b 0
echo.
echo %M_OLD_VENV%
echo   %OLD_VENV%
rmdir /s /q "%OLD_VENV%" >nul 2>&1
if exist "%OLD_VENV%" (echo %M_OLD_VENV_FAILED%) else echo %M_OLD_VENV_REMOVED%
exit /b 0

rem ---------------------------------------------------------------- settings template
rem config.json with the defaults; AutoCAD commands and AutoLISP are off ("allowCommands": false) until the
rem user allows them with the Commands button / MCPCOMMANDS. An existing file is the user's: never changed.
:write_config
echo.
if exist "%DATA%\config.json" (
    echo %M_CONFIG_KEPT%
    echo   %DATA%\config.json
    exit /b 0
)
if not exist "%DATA%" mkdir "%DATA%" >nul 2>&1
(
    echo {
    echo   "port": 8766,
    echo   "token": "",
    echo   "requestTimeoutSeconds": 30,
    echo   "allowCommands": false
    echo }
) >"%DATA%\config.json"
rem Not in a ( ) block: the messages contain parentheses.
if exist "%DATA%\config.json" echo %M_CONFIG_CREATED%
if not exist "%DATA%\config.json" echo %M_CONFIG_FAILED%
echo   %DATA%\config.json
exit /b 0

rem ---------------------------------------------------------------- AI clients
:register_clients
if "%ACADMCP_NO_CLIENTS%"=="1" exit /b 0
echo.
echo %M_CLIENTS_STEP%
call :find_dotnet
if not defined DOTNET (
    echo %M_NO_DOTNET_1%
    echo %M_NO_DOTNET_2%
    exit /b 0
)
"%DOTNET%" "%SERVER_DLL%" --version >nul 2>&1 || (
    echo %M_SERVER_FAILED%
    exit /b 0
)
set "ANY_CLIENT="

rem Claude Desktop: its settings folder exists.
set "C_FOUND=" & set "C_HAS="
for %%F in ("%CLAUDE_CONFIG%") do if exist "%%~dpF" set "C_FOUND=1"
if exist "%CLAUDE_CONFIG%" findstr /c:"\"autocad\"" "%CLAUDE_CONFIG%" >nul 2>&1 && set "C_HAS=1"
if defined C_FOUND call :offer claude-desktop "Claude Desktop" "%CLAUDE_CONFIG%"

rem Codex - CLI, IDE extension or app: its settings folder exists or the codex command is on PATH.
set "C_FOUND=" & set "C_HAS="
for %%F in ("%CODEX_CONFIG%") do if exist "%%~dpF" set "C_FOUND=1"
if not defined ACADMCP_CODEX_CONFIG where codex >nul 2>&1 && set "C_FOUND=1"
if exist "%CODEX_CONFIG%" findstr /c:"[mcp_servers.autocad]" "%CODEX_CONFIG%" >nul 2>&1 && set "C_HAS=1"
if defined C_FOUND call :offer codex "Codex" "%CODEX_CONFIG%"

rem Claude Code: the claude command is on PATH.
set "C_FOUND=" & set "C_HAS="
if not "%ACADMCP_NO_CLAUDE_CODE%"=="1" where claude >nul 2>&1 && set "C_FOUND=1"
if defined C_FOUND call :offer claude-code "Claude Code" ""

if not defined ANY_CLIENT echo %M_NO_CLIENTS%
echo.
echo %M_CLIENT_MANUAL%
echo   "%DOTNET%" "%SERVER_DLL%" --register claude-desktop
echo   "%DOTNET%" "%SERVER_DLL%" --register claude-code
echo   "%DOTNET%" "%SERVER_DLL%" --register codex
exit /b 0

rem :offer <client id> <name> <config path or "">. A client that already has the autocad server
rem from an earlier version is updated without asking.
:offer
set "ANY_CLIENT=1"
if defined C_HAS goto :offer_register
if "%INTERACTIVE%"=="0" goto :offer_register
call set "ASK=%M_CLIENT_ASK%"
choice /c YN /m "%ASK% %~2"
if errorlevel 2 exit /b 0
:offer_register
"%DOTNET%" "%SERVER_DLL%" --register %~1 --config "%~3" >nul 2>&1
if errorlevel 1 (echo   %~2: %M_CLIENT_FAILED%) else echo   %~2: %M_CLIENT_DONE%
exit /b 0

:find_dotnet
set "DOTNET="
if exist "%ProgramFiles%\dotnet\dotnet.exe" set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
if defined DOTNET exit /b 0
for /f "delims=" %%D in ('where dotnet 2^>nul') do if not defined DOTNET set "DOTNET=%%D"
exit /b 0

rem ---------------------------------------------------------------- uninstall
:uninstall
call :check_acad_closed || goto :fail
set "ANYTHING="
if exist "%TARGET%" set "ANYTHING=1"
if exist "%OLD_VENV%" set "ANYTHING=1"
if not defined ANYTHING (
    echo.
    echo %M_NOT_INSTALLED%
    goto :end
)
if "%ACADMCP_NO_CLIENTS%"=="1" goto :remove_files
if not exist "%SERVER_DLL%" goto :remove_files_old
call :find_dotnet
if not defined DOTNET goto :remove_files
echo.
"%DOTNET%" "%SERVER_DLL%" --unregister claude-desktop --config "%CLAUDE_CONFIG%" >nul 2>&1
if not errorlevel 1 echo   Claude Desktop: %M_CLIENT_REMOVED%
"%DOTNET%" "%SERVER_DLL%" --unregister codex --config "%CODEX_CONFIG%" >nul 2>&1
if not errorlevel 1 echo   Codex: %M_CLIENT_REMOVED%
if "%ACADMCP_NO_CLAUDE_CODE%"=="1" goto :remove_files
where claude >nul 2>&1 || goto :remove_files
"%DOTNET%" "%SERVER_DLL%" --unregister claude-code >nul 2>&1
if not errorlevel 1 echo   Claude Code: %M_CLIENT_REMOVED%
goto :remove_files
:remove_files_old
rem A version up to 0.2.x is installed: it has no server DLL to unregister with.
echo.
echo %M_OLD_UNREGISTER%
:remove_files
if exist "%TARGET%" rmdir /s /q "%TARGET%"
if exist "%TARGET%" (
    echo %M_REMOVE_FAILED% %TARGET%
    goto :fail
)
if exist "%OLD_VENV%" rmdir /s /q "%OLD_VENV%"
echo.
echo %M_REMOVED%
if "%INTERACTIVE%"=="0" goto :end
if not exist "%DATA%" goto :end
choice /c YN /m "%M_REMOVE_DATA%"
if errorlevel 2 goto :end
rmdir /s /q "%DATA%"
echo %M_DATA_REMOVED%
goto :end

rem ---------------------------------------------------------------- errors
:extract_failed
echo %M_EXTRACT_FAILED% %PACKAGE%
rmdir /s /q "%WORK%" >nul 2>&1
goto :fail

:unsupported
echo.
call echo %M_UNSUPPORTED_1%
call echo %M_UNSUPPORTED_2%
goto :fail

:too_new
echo.
call echo %M_TOO_NEW_1%
echo %M_TOO_NEW_2%
goto :fail

:bad_input
echo.
call echo %M_BAD_INPUT%
goto :fail

:fail
set "RC=1"
goto :end

:end
echo.
if "%INTERACTIVE%"=="0" exit /b %RC%
echo %M_PRESS_KEY%
pause >nul
exit /b %RC%

rem ---------------------------------------------------------------- subroutines
:detect
call set "S=%%SERIES_%1%%"
reg query "HKLM\SOFTWARE\Autodesk\AutoCAD\%S%" >nul 2>&1 || exit /b 0
set "FOUND=1"
if %1 GEQ %MIN_YEAR% (echo   AutoCAD %1  - %M_SUPPORTED%) else echo   AutoCAD %1  - %M_NOT_SUPPORTED%
exit /b 0

:show_installed
set "INSTALLED="
set /p INSTALLED=<"%TARGET%\versions.txt"
if "%INSTALLED%"=="ALL" (
    echo %M_LOADS_ALL%
    exit /b 0
)
set "YEARS="
for %%Y in (%KNOWN_YEARS%) do call :year_if_installed %%Y
echo %M_LOADS_IN%%YEARS%
exit /b 0

:year_if_installed
call set "S=%%SERIES_%1%%"
echo  %INSTALLED% | findstr /c:" %S% " >nul && set "YEARS=%YEARS% %1"
exit /b 0

:check_acad_closed
tasklist /fi "imagename eq acad.exe" 2>nul | find /i "acad.exe" >nul || exit /b 0
echo.
echo %M_CLOSE_ACAD%
exit /b 1

rem ---------------------------------------------------------------- messages
rem %%CHOICE%% is stored as %CHOICE% and expanded later by "call echo".

:messages_en
set "M_TITLE=AutoCAD MCP Bridge - plugin setup"
set "M_FOUND_HEADER=AutoCAD versions found on this computer:"
set "M_NONE_FOUND=none found"
set "M_SUPPORTED=supported"
set "M_NOT_SUPPORTED=not supported"
set "M_SUPPORTS=The plugin supports AutoCAD %MIN_YEAR% to %MAX_YEAR%."
set "M_PROMPT_YEAR=Enter the AutoCAD version year, for example 2026."
set "M_PROMPT_ALL=all supported versions, %MIN_YEAR% to %MAX_YEAR%"
set "M_PROMPT_UNINSTALL=uninstall the plugin"
set "M_PROMPT_QUIT=quit"
set "M_PROMPT_CHOICE=Your choice:"
set "M_NOT_FOUND=WARNING: AutoCAD %%CHOICE%% was not found on this computer."
set "M_CONFIRM=Install for AutoCAD %%CHOICE%% anyway"
set "M_NO_PACKAGE_1=ERROR: AcadMcpBridge.bundle.zip was not found next to INSTALL.BAT."
set "M_NO_PACKAGE_2=Unpack the whole release archive to a folder and run INSTALL.BAT from there."
set "M_EXTRACTING=Extracting..."
set "M_EXTRACT_FAILED=ERROR: could not extract"
set "M_COPYING=Copying to"
set "M_COPY_FAILED=ERROR: could not copy the files, robocopy code"
set "M_DONE=Done. The plugin is installed in:"
set "M_OLD_VENV=Removing the Python environment of an earlier plugin version, it is no longer needed:"
set "M_OLD_VENV_REMOVED=Removed. Python itself stays installed: remove it in Windows Apps if nothing else needs it."
set "M_OLD_VENV_FAILED=WARNING: could not remove it completely; you can delete the folder by hand."
set "M_CLIENTS_STEP=Connection to AI assistants:"
set "M_NO_DOTNET_1=WARNING: the .NET runtime was not found. It comes with AutoCAD 2025-2027:"
set "M_NO_DOTNET_2=check the AutoCAD installation, then run INSTALL.BAT again."
set "M_SERVER_FAILED=WARNING: the MCP server does not start on this computer; see the .NET runtime of AutoCAD."
set "M_CLIENT_ASK=Connect the autocad server to"
set "M_CLIENT_DONE=connected - restart it to load the server"
set "M_CLIENT_FAILED=could not change its settings"
set "M_CLIENT_REMOVED=autocad server removed"
set "M_NO_CLIENTS=No Claude Desktop, Claude Code or Codex was found on this computer."
set "M_CLIENT_MANUAL=To connect an AI assistant later, run one of:"
set "M_OLD_UNREGISTER=An earlier version is installed: if Claude still lists the autocad server, remove it in its settings."
set "M_RUN_HINT_1=Start AutoCAD and press Start on the AI companion panel of the Home tab, or type MCPSTART."
set "M_RUN_HINT_2=Status shows the state, Stop turns the bridge off."
set "M_NOT_INSTALLED=The plugin is not installed."
set "M_REMOVE_FAILED=ERROR: could not remove"
set "M_REMOVED=The plugin has been removed."
set "M_REMOVE_DATA=Also delete the settings and log in %DATA%"
set "M_DATA_REMOVED=Settings and log deleted."
set "M_CONFIG_CREATED=Settings file created (AutoCAD commands and AutoLISP are off; allow them with the Commands button):"
set "M_CONFIG_KEPT=Settings file already exists, left as it is:"
set "M_CONFIG_FAILED=WARNING: could not create the settings file; the plugin uses the defaults:"
set "M_UNSUPPORTED_1=AutoCAD %%CHOICE%% is not supported."
set "M_UNSUPPORTED_2=The plugin runs in AutoCAD %MIN_YEAR% to %MAX_YEAR%: older versions use .NET Framework."
set "M_TOO_NEW_1=AutoCAD %%CHOICE%% is not supported by this release yet."
set "M_TOO_NEW_2=Each AutoCAD series needs its own build of the plugin: look for a newer release."
set "M_BAD_INPUT=Unexpected answer "%%CHOICE%%". Enter a year, for example 2026, or A, U, Q."
set "M_LOADS_ALL=The plugin loads in all supported AutoCAD versions, %MIN_YEAR% to %MAX_YEAR%."
set "M_LOADS_IN=The plugin loads in AutoCAD:"
set "M_CLOSE_ACAD=Close AutoCAD and run the setup again: the plugin files are in use by AutoCAD."
set "M_PRESS_KEY=Press any key to exit..."
exit /b 0

:messages_ru
set "M_TITLE=AutoCAD MCP Bridge - установка плагина"
set "M_FOUND_HEADER=Найденные на компьютере версии AutoCAD:"
set "M_NONE_FOUND=не найдено ни одной"
set "M_SUPPORTED=поддерживается"
set "M_NOT_SUPPORTED=не поддерживается"
set "M_SUPPORTS=Плагин поддерживает AutoCAD %MIN_YEAR%-%MAX_YEAR%."
set "M_PROMPT_YEAR=Введите год версии AutoCAD, например 2026."
set "M_PROMPT_ALL=для всех поддерживаемых версий, %MIN_YEAR%-%MAX_YEAR%"
set "M_PROMPT_UNINSTALL=удалить плагин"
set "M_PROMPT_QUIT=выход"
set "M_PROMPT_CHOICE=Ваш выбор:"
set "M_NOT_FOUND=ВНИМАНИЕ: AutoCAD %%CHOICE%% не найдена на этом компьютере."
set "M_CONFIRM=Всё равно установить для AutoCAD %%CHOICE%%"
set "M_NO_PACKAGE_1=ОШИБКА: рядом с INSTALL.BAT нет файла AcadMcpBridge.bundle.zip."
set "M_NO_PACKAGE_2=Распакуйте весь архив с релизом в папку и запустите INSTALL.BAT оттуда."
set "M_EXTRACTING=Распаковка..."
set "M_EXTRACT_FAILED=ОШИБКА: не удалось распаковать"
set "M_COPYING=Копирование в"
set "M_COPY_FAILED=ОШИБКА: не удалось скопировать файлы, код robocopy"
set "M_DONE=Готово. Плагин установлен в:"
set "M_OLD_VENV=Удаляю окружение Python от прежней версии плагина, оно больше не нужно:"
set "M_OLD_VENV_REMOVED=Удалено. Сам Python остаётся: удалите его в Приложениях Windows, если он больше не нужен."
set "M_OLD_VENV_FAILED=ВНИМАНИЕ: удалить полностью не удалось; папку можно удалить вручную."
set "M_CLIENTS_STEP=Подключение к ИИ-ассистентам:"
set "M_NO_DOTNET_1=ВНИМАНИЕ: не найдена среда .NET. Она ставится вместе с AutoCAD 2025-2027:"
set "M_NO_DOTNET_2=проверьте установку AutoCAD и запустите INSTALL.BAT снова."
set "M_SERVER_FAILED=ВНИМАНИЕ: MCP-сервер не запускается на этом компьютере; проверьте среду .NET от AutoCAD."
set "M_CLIENT_ASK=Подключить сервер autocad к"
set "M_CLIENT_DONE=подключено - перезапустите его, чтобы он подхватил сервер"
set "M_CLIENT_FAILED=не удалось изменить его настройки"
set "M_CLIENT_REMOVED=сервер autocad удалён"
set "M_NO_CLIENTS=На компьютере не найдены Claude Desktop, Claude Code или Codex."
set "M_CLIENT_MANUAL=Чтобы подключить ИИ-ассистента позже, выполните одну из команд:"
set "M_OLD_UNREGISTER=Установлена прежняя версия: если Claude по-прежнему показывает сервер autocad, удалите его в настройках Claude."
set "M_RUN_HINT_1=Запустите AutoCAD и нажмите Start на панели AI companion вкладки Главная или введите MCPSTART."
set "M_RUN_HINT_2=Status показывает состояние, Stop выключает мост."
set "M_NOT_INSTALLED=Плагин не установлен."
set "M_REMOVE_FAILED=ОШИБКА: не удалось удалить"
set "M_REMOVED=Плагин удалён."
set "M_REMOVE_DATA=Удалить также настройки и журнал в %DATA%"
set "M_DATA_REMOVED=Настройки и журнал удалены."
set "M_CONFIG_CREATED=Создан файл настроек (команды AutoCAD и AutoLISP выключены; разрешить — кнопкой Commands):"
set "M_CONFIG_KEPT=Файл настроек уже есть, оставлен без изменений:"
set "M_CONFIG_FAILED=ВНИМАНИЕ: не удалось создать файл настроек; плагин работает с настройками по умолчанию:"
set "M_UNSUPPORTED_1=AutoCAD %%CHOICE%% не поддерживается."
set "M_UNSUPPORTED_2=Плагин работает в AutoCAD %MIN_YEAR%-%MAX_YEAR%: старые версии используют .NET Framework."
set "M_TOO_NEW_1=AutoCAD %%CHOICE%% этим выпуском ещё не поддерживается."
set "M_TOO_NEW_2=Для каждой серии AutoCAD нужна своя сборка плагина: проверьте, нет ли нового выпуска."
set "M_BAD_INPUT=Не понял ответ "%%CHOICE%%". Введите год, например 2026, или A, U, Q."
set "M_LOADS_ALL=Плагин загружается во всех поддерживаемых версиях AutoCAD, %MIN_YEAR%-%MAX_YEAR%."
set "M_LOADS_IN=Плагин загружается в AutoCAD:"
set "M_CLOSE_ACAD=Закройте AutoCAD и запустите установку снова: файлы плагина заняты запущенным AutoCAD."
set "M_PRESS_KEY=Нажмите любую клавишу для выхода..."
exit /b 0

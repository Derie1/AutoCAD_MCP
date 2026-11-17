@echo off
rem AutoCAD MCP Bridge installer. Run from the unpacked release folder, next to AcadMcpBridge.bundle.zip.
rem   INSTALL.BAT               - interactive: language, AutoCAD version, Python environment, Claude connection
rem   INSTALL.BAT 2026 [EN|RU]  - install for AutoCAD 2026 without questions; also A = all supported, U = uninstall
rem Overrides, for tests and development:
rem   ACADMCP_TARGET         plugin bundle folder,   default %APPDATA%\Autodesk\ApplicationPlugins\AcadMcpBridge.bundle
rem   ACADMCP_DATA           venv, config and log,   default %LOCALAPPDATA%\AcadMcpBridge
rem   ACADMCP_CLAUDE_CONFIG  Claude Desktop config,  default %APPDATA%\Claude\claude_desktop_config.json
rem   ACADMCP_NO_PYTHON=1    skip the Python environment step
rem   ACADMCP_NO_CLAUDE=1    skip the Claude registration step
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
set "VENV=%DATA%\venv"
set "VENV_PY=%DATA%\venv\Scripts\python.exe"
set "SERVER=%TARGET%\Contents\acad_mcp_server.py"
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
if not exist "%WORK%\Installer\PackageContents\head.xml" goto :extract_failed

echo %M_COPYING% %TARGET%
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

call :setup_python
call :register_claude

echo.
echo ================================================================
echo %M_RUN_HINT_1%
echo %M_RUN_HINT_2%
goto :end

rem ---------------------------------------------------------------- python
:setup_python
if "%ACADMCP_NO_PYTHON%"=="1" exit /b 0
echo.
echo %M_PY_STEP% %VENV%
if exist "%VENV_PY%" goto :py_install
call :find_python
if not defined PY (
    echo %M_PY_MISSING_1%
    echo %M_PY_MISSING_2%
    exit /b 0
)
%PY% -m venv "%VENV%" >nul 2>&1
if not exist "%VENV_PY%" (
    echo %M_VENV_FAILED%
    exit /b 0
)
:py_install
"%VENV_PY%" -m pip install --disable-pip-version-check -q -r "%TARGET%\Contents\requirements.txt"
"%VENV_PY%" -c "import mcp.server" >nul 2>&1 || (
    echo %M_PIP_FAILED_1%
    echo %M_PIP_FAILED_2%
    exit /b 0
)
echo %M_PY_OK%
exit /b 0

:find_python
set "PY="
py -3 -c "import sys; sys.exit(0 if sys.version_info >= (3, 10) else 1)" >nul 2>&1 && set "PY=py -3"
if defined PY exit /b 0
python -c "import sys; sys.exit(0 if sys.version_info >= (3, 10) else 1)" >nul 2>&1 && set "PY=python"
exit /b 0

rem ---------------------------------------------------------------- claude
:register_claude
if "%ACADMCP_NO_CLAUDE%"=="1" exit /b 0
echo.
echo %M_CLAUDE_STEP%
if not exist "%VENV_PY%" (
    echo %M_CLAUDE_NO_PY%
    goto :claude_manual
)
for %%F in ("%CLAUDE_CONFIG%") do set "CLAUDE_DIR=%%~dpF"
if not exist "%CLAUDE_DIR%" (
    echo %M_CLAUDE_NOT_FOUND%
    goto :claude_manual
)
if "%INTERACTIVE%"=="0" goto :claude_add
choice /c YN /m "%M_CLAUDE_ASK%"
if errorlevel 2 goto :claude_manual
:claude_add
"%VENV_PY%" "%TARGET%\Installer\register_claude.py" add --config "%CLAUDE_CONFIG%" --python "%VENV_PY%" --server "%SERVER%"
if errorlevel 1 (
    echo %M_CLAUDE_FAILED%
    goto :claude_manual
)
echo %M_CLAUDE_DONE%
echo   %CLAUDE_CONFIG%
echo %M_CLAUDE_RESTART%
exit /b 0
:claude_manual
echo %M_CLAUDE_MANUAL%
echo   claude mcp add --scope user autocad -- "%VENV_PY%" "%SERVER%"
exit /b 0

rem ---------------------------------------------------------------- uninstall
:uninstall
call :check_acad_closed || goto :fail
set "ANYTHING="
if exist "%TARGET%" set "ANYTHING=1"
if exist "%VENV%" set "ANYTHING=1"
if not defined ANYTHING (
    echo.
    echo %M_NOT_INSTALLED%
    goto :end
)
if "%ACADMCP_NO_CLAUDE%"=="1" goto :remove_files
if not exist "%TARGET%\Installer\register_claude.py" goto :remove_files
set "PYX="
if exist "%VENV_PY%" set "PYX="%VENV_PY%""
if not defined PYX call :find_python
if not defined PYX if defined PY set "PYX=%PY%"
if not defined PYX goto :remove_files
%PYX% "%TARGET%\Installer\register_claude.py" remove --config "%CLAUDE_CONFIG%"
if not errorlevel 1 echo %M_CLAUDE_REMOVED%
:remove_files
if exist "%TARGET%" rmdir /s /q "%TARGET%"
if exist "%TARGET%" (
    echo %M_REMOVE_FAILED% %TARGET%
    goto :fail
)
if exist "%VENV%" rmdir /s /q "%VENV%"
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
set "M_PY_STEP=Python environment for the MCP server:"
set "M_PY_MISSING_1=WARNING: Python 3.10 or newer was not found. Install it from python.org,"
set "M_PY_MISSING_2=then run INSTALL.BAT again. The AutoCAD plugin itself is installed."
set "M_VENV_FAILED=WARNING: could not create the Python environment."
set "M_PIP_FAILED_1=WARNING: could not install the Python package mcp."
set "M_PIP_FAILED_2=Check the internet connection and run INSTALL.BAT again."
set "M_PY_OK=Python environment is ready."
set "M_CLAUDE_STEP=Connection to Claude:"
set "M_CLAUDE_NO_PY=Skipped: the Python environment is not ready."
set "M_CLAUDE_NOT_FOUND=Claude Desktop was not found on this computer."
set "M_CLAUDE_ASK=Register the autocad MCP server in Claude Desktop"
set "M_CLAUDE_FAILED=WARNING: could not update the Claude Desktop config."
set "M_CLAUDE_DONE=The autocad server is registered in:"
set "M_CLAUDE_RESTART=Restart Claude to load it."
set "M_CLAUDE_MANUAL=To connect manually, for example in Claude Code, run:"
set "M_CLAUDE_REMOVED=The autocad server is removed from the Claude Desktop config."
set "M_RUN_HINT_1=Start AutoCAD and type MCPSTART to start the bridge."
set "M_RUN_HINT_2=MCPSTATUS shows the state, MCPSTOP stops it."
set "M_NOT_INSTALLED=The plugin is not installed."
set "M_REMOVE_FAILED=ERROR: could not remove"
set "M_REMOVED=The plugin and its Python environment have been removed."
set "M_REMOVE_DATA=Also delete the settings and log in %DATA%"
set "M_DATA_REMOVED=Settings and log deleted."
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
set "M_PY_STEP=Окружение Python для MCP-сервера:"
set "M_PY_MISSING_1=ВНИМАНИЕ: не найден Python 3.10 или новее. Установите его с python.org"
set "M_PY_MISSING_2=и запустите INSTALL.BAT снова. Сам плагин AutoCAD уже установлен."
set "M_VENV_FAILED=ВНИМАНИЕ: не удалось создать окружение Python."
set "M_PIP_FAILED_1=ВНИМАНИЕ: не удалось установить Python-пакет mcp."
set "M_PIP_FAILED_2=Проверьте подключение к интернету и запустите INSTALL.BAT снова."
set "M_PY_OK=Окружение Python готово."
set "M_CLAUDE_STEP=Подключение к Claude:"
set "M_CLAUDE_NO_PY=Пропущено: окружение Python не готово."
set "M_CLAUDE_NOT_FOUND=Claude Desktop на этом компьютере не найден."
set "M_CLAUDE_ASK=Зарегистрировать MCP-сервер autocad в Claude Desktop"
set "M_CLAUDE_FAILED=ВНИМАНИЕ: не удалось изменить конфиг Claude Desktop."
set "M_CLAUDE_DONE=Сервер autocad зарегистрирован в:"
set "M_CLAUDE_RESTART=Перезапустите Claude, чтобы он его подхватил."
set "M_CLAUDE_MANUAL=Чтобы подключить вручную, например в Claude Code, выполните:"
set "M_CLAUDE_REMOVED=Сервер autocad удалён из конфига Claude Desktop."
set "M_RUN_HINT_1=Запустите AutoCAD и введите MCPSTART, чтобы включить мост."
set "M_RUN_HINT_2=MCPSTATUS показывает состояние, MCPSTOP выключает мост."
set "M_NOT_INSTALLED=Плагин не установлен."
set "M_REMOVE_FAILED=ОШИБКА: не удалось удалить"
set "M_REMOVED=Плагин и его окружение Python удалены."
set "M_REMOVE_DATA=Удалить также настройки и журнал в %DATA%"
set "M_DATA_REMOVED=Настройки и журнал удалены."
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

# AutoCAD MCP Bridge

Управление запущенным AutoCAD 2025–2027 из Claude (и любого MCP-клиента).
План развития и журнал решений — [PLAN.md](PLAN.md).

```
Claude ──stdio/MCP──► acad_mcp_server.py ──HTTP JSON──► AcadMcpBridge.dll (внутри acad.exe)
                                            localhost:8766
```

Независим от Navisworks MCP: свой порт (8766), бандл, Python-сервер и venv — оба
моста работают одновременно.

## Версии

| AutoCAD | Сборка | .NET |
|---|---|---|
| 2025, 2026 | `Contents\R25` | net8.0 (работает и на .NET 10 после обновлений 2025/2026) |
| 2027 | `Contents\R26` | net10.0 |

## Инструменты (v0.1.1)

| Инструмент | Что делает |
|---|---|
| `health_check` | Версии плагина, AutoCAD, .NET; порт; число маршрутов. Отвечает, даже если AutoCAD занят |
| `get_document_info` | Активный чертёж: имя, путь, несохранённые изменения, единицы, границы, текущий слой/лист, счётчики объектов |

## Установка у пользователя

Нужно: AutoCAD 2025–2027 (не LT), Python 3.10+ с [python.org](https://www.python.org/downloads/)
и доступ в интернет при установке (ставится Python-пакет `mcp`). Права администратора не нужны.

1. Распакуйте `AutoCAD_MCP_<версия>.zip` в любую папку (не запускайте из архива).
2. Закройте AutoCAD и запустите `INSTALL.BAT`.
3. Выберите язык установщика — вопрос на английском: `1` — English, `2` — Русский.
   Дальше все сообщения на выбранном языке.
4. Установщик покажет найденные версии AutoCAD и спросит год версии (`2026`), `A` — все
   поддерживаемые (2025–2027), `U` — удалить, `Q` — выход.
5. Установщик подготовит окружение Python и спросит, зарегистрировать ли сервер `autocad`
   в Claude Desktop (`Y`/`N`). После этого перезапустите Claude.
6. Запустите AutoCAD и введите **`MCPSTART`**.

Без вопросов: `INSTALL.BAT 2026`, `INSTALL.BAT A`, `INSTALL.BAT U`; язык — вторым параметром,
`INSTALL.BAT 2026 RU` (по умолчанию `EN`). В этом режиме сервер регистрируется в Claude Desktop без вопроса.

Что делает установщик:

| Шаг | Куда |
|---|---|
| Плагин (`AcadMcpBridge.bundle.zip` распаковывается) | `%APPDATA%\Autodesk\ApplicationPlugins\AcadMcpBridge.bundle` |
| Окружение Python с пакетом `mcp` | `%LOCALAPPDATA%\AcadMcpBridge\venv` |
| Сервер `autocad` в Claude Desktop (копия конфига до правки — `.bak-autocad`) | `%APPDATA%\Claude\claude_desktop_config.json` |

- `PackageContents.xml` собирается так, что плагин загружается только в выбранных версиях;
  повторный запуск с другой версией добавляет её (список — `versions.txt` в бандле).
- Версии до 2025 отклоняются («не поддерживается»), новее 2027 — «нужен новый выпуск».
- Если выбранная версия не найдена в реестре (`HKLM\SOFTWARE\Autodesk\AutoCAD\R<серия>`) — предупреждает и спрашивает.
- Не работает при запущенном `acad.exe` (DLL была бы занята).
- Если нет Python или интернета, плагин всё равно ставится; шаг Python можно повторить, запустив `INSTALL.BAT` снова.
- Другие серверы в конфиге Claude (`navisworks`, `Revit` …) не трогаются.
- `U` удаляет плагин, окружение Python и запись `autocad` из конфига Claude; настройки и журнал
  (`%LOCALAPPDATA%\AcadMcpBridge`) — по отдельному вопросу.
- Claude Code CLI установщик не настраивает, а печатает команду
  `claude mcp add --scope user autocad -- "<venv>\Scripts\python.exe" "<бандл>\Contents\acad_mcp_server.py"`.

## Installation (English)

1. Unpack `AutoCAD_MCP_<version>.zip` to any folder, close AutoCAD and run `INSTALL.BAT`.
2. Choose the language (`1` English, `2` Russian), then the AutoCAD year (`2026`), `A` for all
   supported versions (2025–2027), `U` to uninstall or `Q` to quit.
3. The installer creates a Python environment in `%LOCALAPPDATA%\AcadMcpBridge\venv` (needs Python 3.10+
   and internet) and offers to register the `autocad` server in Claude Desktop. Restart Claude.
4. Start AutoCAD and type **`MCPSTART`**.

Unattended: `INSTALL.BAT 2026 EN`, `INSTALL.BAT A`, `INSTALL.BAT U`.

## Сборка (разработка)

```powershell
.\Build-Package.ps1           # собрать R25 + R26 -> release\AutoCAD_MCP_<версия>\ и release\AutoCAD_MCP_<версия>.zip
.\Build-Package.ps1 -Deploy   # то же + установить здесь: INSTALL.BAT A без шагов Python и Claude
```

Версия — одна, `<Version>` в `src\AcadMcpBridge.csproj`; `PluginVersion` (C#) и `SERVER_VERSION`
(Python) должны совпадать, иначе сборка остановится. Исходники установщика — папка `installer\`
(`INSTALL.BAT`, части манифеста `PackageContents\`, `register_claude.py`).
Новая версия AutoCAD: строка `SERIES_<год>` и `MAX_YEAR` в `INSTALL.BAT`, файл
`installer\PackageContents\R<серия>.xml`, при новой серии — сборка в `Build-Package.ps1` и csproj.

Для разработки Python-окружение — в папке проекта:

```powershell
python -m venv .venv
.venv\Scripts\python.exe -m pip install -r server\requirements.txt
```

Подключение к Claude Code вручную:

```powershell
claude mcp add autocad -- "<AutoCAD_MCP>\.venv\Scripts\python.exe" "%APPDATA%\Autodesk\ApplicationPlugins\AcadMcpBridge.bundle\Contents\acad_mcp_server.py"
```

## Запуск

В AutoCAD: **`MCPSTART`** — включить мост, `MCPSTATUS` — состояние, `MCPSTOP` — выключить.
Проверка без MCP-клиента: `Invoke-RestMethod http://localhost:8766/health`.

## Настройки

`%LOCALAPPDATA%\AcadMcpBridge\config.json` (необязательный):

```json
{ "port": 8766, "token": "", "requestTimeoutSeconds": 30 }
```

Если меняете порт или задаёте токен — те же значения передайте Python-серверу через
переменные `ACAD_MCP_PORT` и `ACAD_MCP_TOKEN`. Лог: `%LOCALAPPDATA%\AcadMcpBridge\log.txt`.

## Тесты

```powershell
.venv\Scripts\python.exe tests\smoke_test.py
```

Нужен запущенный AutoCAD с открытым чертежом и `MCPSTART`. Только чтение, чертёж не меняется.

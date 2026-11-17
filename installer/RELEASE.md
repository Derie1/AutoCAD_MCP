# AutoCAD MCP Bridge @VERSION@

*Русский — ниже. English — at the end.*

---

## Что это

Плагин для AutoCAD, который даёт ИИ-ассистенту (Claude и другим клиентам MCP) доступ к открытому
чертежу. Вы пишете задачу обычным языком — «найди все насосы и выгрузи их атрибуты», «заполни
таблицу этими значениями», «переложи тексты на другой слой» — ассистент читает чертёж и вносит
изменения через инструменты плагина.

Плагин работает внутри AutoCAD и включается только по вашей команде; связь идёт лишь внутри
компьютера (localhost).

## Возможности

**Анализ чертежа**
Сведения о чертеже, слои, блоки, листы, внешние ссылки, стили, системные переменные; поиск
объектов по типу, слою, блоку, области и тексту; полные свойства объектов; работа с тем, что
пользователь выделил в AutoCAD.

**Таблицы и атрибуты**
Чтение таблиц AutoCAD (тексты, числа, формулы, объединённые ячейки), заполнение ячеек, в том числе
выделенных, добавление и удаление строк и столбцов, создание таблиц из данных. Выгрузка атрибутов
блоков таблицей и массовое заполнение атрибутов.

**Черчение**
Отрезки, полилинии, окружности, дуги, тексты, размеры, вставка блоков с атрибутами, создание слоёв.

**Редактирование**
Свойства объектов (слой, цвет, тип и вес линий, прозрачность), перемещение, копирование, поворот,
масштаб, зеркало, удаление, управление слоями.

**Безопасность**
Каждое изменение ассистента отменяется одним шагом, и отменяются только его изменения, а не ваши.
Массовые изменения — только после подтверждения, с предварительным просмотром. Объекты на
заблокированных слоях и заблокированные ячейки таблиц (в том числе связанные с Excel) не изменяются.

**Управление**
Панель **AI companion** на вкладке «Главная»: кнопки **Start**, **Stop**, **Status**
(команды `MCPSTART`, `MCPSTOP`, `MCPSTATUS`).

## Требования

| Что | Требование |
|---|---|
| Система | Windows 10 или 11, 64 бит |
| AutoCAD | 2025, 2026 или 2027 (полная версия; AutoCAD LT не поддерживается). Отраслевые версии на базе AutoCAD (например, MEP) тоже загружают плагин |
| Python | 3.10 или новее с [python.org](https://www.python.org/downloads/); при установке Python отметьте «Add python.exe to PATH» (или используйте стандартный запуск `py`) |
| Интернет | Нужен один раз при установке: загружается Python-пакет `mcp` |
| ИИ-клиент | Claude Desktop (установщик подключит его сам) или другой MCP-клиент, например Claude Code |
| Права | Права администратора не нужны |
| Порт | Свободный порт 8766 на этом компьютере (меняется в настройках) |

.NET отдельно ставить не нужно — он входит в AutoCAD.

## Установка

1. Распакуйте архив `AutoCAD_MCP_@VERSION@.zip` в любую папку (не запускайте из архива).
2. Закройте AutoCAD и запустите `INSTALL.BAT`.
3. Выберите язык установщика, затем версию AutoCAD (год, например `2026`) или `A` — все поддерживаемые.
4. Установщик:
   - установит плагин в `%APPDATA%\Autodesk\ApplicationPlugins\AcadMcpBridge.bundle`;
   - создаст окружение Python в `%LOCALAPPDATA%\AcadMcpBridge\venv` и загрузит пакет `mcp`;
   - предложит подключить сервер `autocad` к Claude Desktop (копия прежних настроек Claude сохраняется).
5. Перезапустите Claude Desktop.
6. Запустите AutoCAD и нажмите **Start** на панели **AI companion** (вкладка «Главная»)
   или введите `MCPSTART`.

Если Claude Desktop не найден или вы отказались от подключения, установщик показывает готовую команду
для подключения вручную (например, в Claude Code).

**Обновление:** запустите `INSTALL.BAT` новой версии — он заменит старую.
**Удаление:** `INSTALL.BAT`, пункт `U` — удаляются плагин, окружение Python и запись в настройках Claude.
**Без вопросов:** `INSTALL.BAT 2026 RU`, `INSTALL.BAT A`, `INSTALL.BAT U`.

## Если что-то не работает

- Ассистент пишет, что AutoCAD недоступен — нажмите **Start** в AutoCAD.
- Нет панели **AI companion** — плагин не загрузился: проверьте, что AutoCAD был закрыт при установке, и
  посмотрите журнал `%LOCALAPPDATA%\AcadMcpBridge\log.txt`.
- Установщик не нашёл Python или не загрузил `mcp` — установите Python / проверьте интернет и запустите
  `INSTALL.BAT` ещё раз; плагин при этом уже установлен.

---

## What it is

An AutoCAD plugin that gives an AI assistant (Claude and other MCP clients) access to the open
drawing. You describe the task in plain words — "find all pumps and export their attributes",
"fill the table with these values", "move the texts to another layer" — and the assistant reads
the drawing and makes the changes through the plugin's tools.

The plugin runs inside AutoCAD and starts only on your command; it talks only within this
computer (localhost).

## Features

**Drawing analysis**
Drawing info, layers, blocks, layouts, external references, styles, system variables; finding
objects by type, layer, block, area and text; full object properties; working with what the user
has selected in AutoCAD.

**Tables and attributes**
Reading AutoCAD tables (text, numbers, formulas, merged cells), filling cells including the
selected ones, inserting and deleting rows and columns, creating tables from data. Block
attributes exported as a table and filled in bulk.

**Drafting**
Lines, polylines, circles, arcs, texts, dimensions, block insertion with attributes, new layers.

**Editing**
Object properties (layer, color, linetype, lineweight, transparency), move, copy, rotate, scale,
mirror, delete, layer management.

**Safety**
Every change the assistant makes is undone in one step, and only its own changes are undone, never
yours. Bulk changes need confirmation and can be previewed first. Objects on locked layers and
locked table cells (including cells linked to Excel) are never changed.

**Control**
The **AI companion** panel on the **Home** tab: **Start**, **Stop**, **Status** buttons
(commands `MCPSTART`, `MCPSTOP`, `MCPSTATUS`).

## Requirements

| What | Requirement |
|---|---|
| System | Windows 10 or 11, 64-bit |
| AutoCAD | 2025, 2026 or 2027 (full version; AutoCAD LT is not supported). Toolsets built on AutoCAD (e.g. MEP) load the plugin too |
| Python | 3.10 or newer from [python.org](https://www.python.org/downloads/); tick "Add python.exe to PATH" during setup (or use the standard `py` launcher) |
| Internet | Once, during installation: the Python package `mcp` is downloaded |
| AI client | Claude Desktop (connected by the installer) or another MCP client such as Claude Code |
| Rights | No administrator rights needed |
| Port | Free port 8766 on this computer (configurable) |

No separate .NET installation is needed — it comes with AutoCAD.

## Installation

1. Unpack `AutoCAD_MCP_@VERSION@.zip` to any folder (do not run it from inside the archive).
2. Close AutoCAD and run `INSTALL.BAT`.
3. Choose the installer language, then the AutoCAD version (a year such as `2026`) or `A` for all supported.
4. The installer:
   - installs the plugin to `%APPDATA%\Autodesk\ApplicationPlugins\AcadMcpBridge.bundle`;
   - creates a Python environment in `%LOCALAPPDATA%\AcadMcpBridge\venv` and downloads the `mcp` package;
   - offers to connect the `autocad` server to Claude Desktop (a copy of the previous Claude settings is kept).
5. Restart Claude Desktop.
6. Start AutoCAD and press **Start** on the **AI companion** panel (Home tab), or type `MCPSTART`.

If Claude Desktop is not found or you decline, the installer prints a ready-made command to connect
manually (e.g. in Claude Code).

**Update:** run `INSTALL.BAT` of the new version — it replaces the old one.
**Uninstall:** `INSTALL.BAT`, option `U` — removes the plugin, the Python environment and the Claude setting.
**Unattended:** `INSTALL.BAT 2026 EN`, `INSTALL.BAT A`, `INSTALL.BAT U`.

## Troubleshooting

- The assistant says AutoCAD is not reachable — press **Start** in AutoCAD.
- No **AI companion** panel — the plugin did not load: make sure AutoCAD was closed during installation
  and check the log `%LOCALAPPDATA%\AcadMcpBridge\log.txt`.
- The installer found no Python or could not download `mcp` — install Python / check the internet
  connection and run `INSTALL.BAT` again; the plugin itself is already installed.

# AutoCAD MCP Bridge @VERSION@

*Русский — ниже. English — at the end.*

---

## Что это

Плагин для AutoCAD, который даёт ИИ-ассистенту — Claude Desktop, Claude Code, Codex или другому
клиенту MCP — доступ к открытому чертежу. Вы пишете задачу обычным языком — «найди все насосы и
выгрузи их атрибуты», «заполни таблицу этими значениями», «переложи тексты на другой слой» —
ассистент читает чертёж и вносит изменения через инструменты плагина.

Плагин работает внутри AutoCAD и включается только по вашей команде; связь идёт лишь внутри
компьютера (localhost).

**Новое в 0.7.0 — пакетная обработка папок:**
- Ассистент прогоняет одни и те же действия по всей папке чертежей **без окна AutoCAD**: проверка слоёв и
  атрибутов, аудит, очистка, замена атрибутов, печать в PDF. На каждый чертёж запускается консольный
  AutoCAD (AcCoreConsole), несколько одновременно; открытый AutoCAD не нужен, ваша работа в нём не
  прерывается. Примерно 3–4 секунды на чертёж.
- Проверки и печать ничего не меняют: чертежи открываются только для чтения.
- Изменённые чертежи сохраняются **только так, как вы скажете**: копиями в отдельную папку (исходники не
  трогаются) или поверх исходников — тогда рядом остаётся прежняя версия `.bak`. Если вы не сказали,
  ассистент спросит. Версия DWG не меняется; занятые и защищённые от записи файлы пропускаются.
- Команды AutoCAD и AutoLISP в пакете — только если в `config.json` стоит `"allowCommands": true`.
- Длинный пакет можно отслеживать и остановить.

*В 0.6.0:* команды AutoCAD и AutoLISP по вашему разрешению (кнопка **Commands**); исправлено зависание
отмены после команды, переданной чертежу другой программой через COM.

*В 0.5.0:* создание, открытие, сохранение (в том числе в старые DWG и DXF) и закрытие чертежей; печать
листов в PDF; внешние ссылки; очистка и аудит; перенос стандартов из шаблона.

*В 0.4.0:*
- **Ассистент видит чертёж:** снимок текущего вида, всего чертежа, листа, рамки или выбранных объектов —
  ассистент может сам проверить, что нарисовал. Ваш вид на экране при этом не меняется.
- Показать всё / рамку / объекты, перейти на лист или в модель.
- **Размеры:** высота текста, стрелки, точность, масштаб, размещение и положение текста — при создании
  и для готовых размеров.
- **Мультивыноски** с текстом, в том числе несколько стрелок к одной надписи.
- **Стили:** создание и изменение стилей текста (TrueType и SHX), размеров, таблиц и мультивыносок;
  смена стиля у готовых объектов.
- Свойства динамических блоков: значения, которых нет в списке блока, отклоняются, а не подменяются молча.
- Новые объекты по умолчанию «ПоСлою» (цвет, тип и вес линий); новый слой — тип линий Continuous.

*В 0.3.0:* Python больше не нужен — сервер на .NET ставится вместе с плагином; установщик подключает
Claude Desktop, Claude Code и Codex.

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
Отрезки, полилинии, окружности, дуги, тексты, размеры, мультивыноски, вставка блоков с атрибутами,
создание слоёв и стилей оформления.

**Файлы и печать**
Создание, открытие, сохранение (в том числе в старые версии DWG и DXF) и закрытие чертежей, печать листов
в PDF, внешние ссылки, очистка, аудит, перенос стандартов из шаблона.

**Команды и AutoLISP** (только с вашего разрешения)
Любая команда AutoCAD с ответами на запросы и текстом командной строки; выражения AutoLISP с результатом.

**Пакетная обработка**
Те же действия по папке чертежей без окна AutoCAD, несколько чертежей одновременно; отчёт по каждому
файлу; изменённые чертежи — копиями в другую папку или поверх с `.bak`, по вашему выбору; PDF на каждый
чертёж или лист.

**Просмотр**
Снимок вида в картинку (ассистент видит чертёж), показать всё / рамку / объекты, переход между листами.

**Редактирование**
Свойства объектов (слой, цвет, тип и вес линий, прозрачность, стиль, свойства динамических блоков),
настройка размеров, перемещение, копирование, поворот, масштаб, зеркало, удаление, управление слоями.

**Безопасность**
Каждое изменение ассистента отменяется одним шагом, и отменяются только его изменения, а не ваши.
Массовые изменения — только после подтверждения, с предварительным просмотром. Объекты на
заблокированных слоях и заблокированные ячейки таблиц (в том числе связанные с Excel) не изменяются.
Команды AutoCAD и AutoLISP ассистент выполняет только после вашего разрешения. Пакетная обработка
меняет файлы на диске только после вашего выбора, куда сохранять.

**Управление**
Панель **AI companion** на вкладке «Главная»: кнопки **Start**, **Stop**, **Status** и переключатель
**Commands** (команды `MCPSTART`, `MCPSTOP`, `MCPSTATUS`, `MCPCOMMANDS`). Чтобы команды были разрешены
всегда, без кнопки, поставьте `"allowCommands": true` в `%LOCALAPPDATA%\AcadMcpBridge\config.json`
(установщик создаёт его с `false`) и перезапустите AutoCAD.

## Требования

| Что | Требование |
|---|---|
| Система | Windows 10 или 11, 64 бит |
| AutoCAD | 2025, 2026 или 2027 (полная версия; AutoCAD LT не поддерживается). Отраслевые версии на базе AutoCAD (например, MEP) тоже загружают плагин |
| ИИ-клиент | Claude Desktop, Claude Code или Codex (CLI, расширение IDE, приложение) — установщик подключит найденные. Другой MCP-клиент можно подключить вручную |
| Права | Права администратора не нужны |
| Порт | Свободный порт 8766 на этом компьютере (меняется в настройках) |

Больше ничего ставить не нужно: Python и интернет при установке не нужны, среда .NET входит в AutoCAD.

## Установка

1. Распакуйте архив `AutoCAD_MCP_@VERSION@.zip` в любую папку (не запускайте из архива).
2. Закройте AutoCAD и запустите `INSTALL.BAT`.
3. Выберите язык установщика, затем версию AutoCAD (год, например `2026`) или `A` — все поддерживаемые.
4. Установщик:
   - установит плагин в `%APPDATA%\Autodesk\ApplicationPlugins\AcadMcpBridge.bundle`;
   - создаст файл настроек `%LOCALAPPDATA%\AcadMcpBridge\config.json` (команды AutoCAD и AutoLISP в нём
     выключены; если файл уже есть, он не меняется);
   - найдёт Claude Desktop, Claude Code и Codex и для каждого спросит, подключить ли сервер `autocad`
     (перед правкой настроек клиента сохраняется их копия; другие серверы не затрагиваются).
5. Перезапустите подключённые ИИ-клиенты.
6. Запустите AutoCAD и нажмите **Start** на панели **AI companion** (вкладка «Главная»)
   или введите `MCPSTART`.

Если клиент не найден или вы отказались, установщик в конце показывает готовые команды, чтобы
подключить его позже.

**Обновление с версии 0.2.x и раньше:** просто запустите `INSTALL.BAT` новой версии. Он сам удалит
окружение Python прежней версии (`%LOCALAPPDATA%\AcadMcpBridge\venv`) и переведёт Claude Desktop на
новый сервер; настройки плагина сохраняются. Сам Python остаётся — удалите его через «Приложения»
Windows, если он больше ничему не нужен. Обновляйте только через `INSTALL.BAT`, а не копированием файлов.
**Удаление:** `INSTALL.BAT`, пункт `U` — удаляются плагин и запись `autocad` во всех клиентах.
**Без вопросов:** `INSTALL.BAT 2026 RU`, `INSTALL.BAT A`, `INSTALL.BAT U`.

## Если что-то не работает

- Ассистент пишет, что AutoCAD недоступен — нажмите **Start** в AutoCAD.
- Ассистент не видит инструментов AutoCAD — перезапустите его после установки; если не помогло,
  подключите командой, которую показал установщик.
- Нет панели **AI companion** — плагин не загрузился: проверьте, что AutoCAD был закрыт при установке, и
  посмотрите журнал `%LOCALAPPDATA%\AcadMcpBridge\log.txt`.

---

## What it is

An AutoCAD plugin that gives an AI assistant — Claude Desktop, Claude Code, Codex or another MCP
client — access to the open drawing. You describe the task in plain words — "find all pumps and
export their attributes", "fill the table with these values", "move the texts to another layer" —
and the assistant reads the drawing and makes the changes through the plugin's tools.

The plugin runs inside AutoCAD and starts only on your command; it talks only within this
computer (localhost).

**New in 0.7.0 — batch processing of folders:**
- The assistant runs the same actions over a whole folder of drawings **without an AutoCAD window**:
  checking layers and attributes, audit, purge, replacing attributes, printing to PDF. Each drawing gets
  its own console AutoCAD (AcCoreConsole), several at once; AutoCAD does not need to be open, and your
  work in it is not interrupted. About 3-4 seconds per drawing.
- Checks and printing change nothing: the drawings are opened read-only.
- Changed drawings are saved **only the way you say**: as copies in another folder (the originals stay as
  they are) or over the originals, keeping the previous version as `.bak`. If you have not said, the
  assistant asks. The DWG version is kept; files in use or read-only are skipped.
- AutoCAD commands and AutoLISP in batches only with `"allowCommands": true` in `config.json`.
- A long batch can be followed and stopped.

*In 0.6.0:* AutoCAD commands and AutoLISP with your permission (the **Commands** button); fixed the undo
hang after another program sent a command to a drawing through COM.

*In 0.5.0:* creating, opening, saving (older DWG versions and DXF too) and closing drawings; printing
layouts to PDF; external references; purge and audit; standards from a template.

*In 0.4.0:*
- **The assistant sees the drawing:** a picture of the current view, the whole drawing, a layout, a window
  or chosen objects — the assistant can check what it has drawn. Your view on screen does not change.
- Zoom to extents / window / objects, switch to a layout or the model.
- **Dimensions:** text height, arrows, precision, scale, text fit and position — when creating and for
  existing dimensions.
- **Multileaders** with text, including several arrows to one label.
- **Styles:** create and change text (TrueType and SHX), dimension, table and multileader styles; change
  the style of existing objects.
- Dynamic block properties: values not in the block's list are rejected instead of being silently replaced.
- New objects default to ByLayer (color, linetype, lineweight); a new layer gets the Continuous linetype.

*In 0.3.0:* Python is no longer needed — the .NET server is installed with the plugin; the installer
connects Claude Desktop, Claude Code and Codex.

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
Lines, polylines, circles, arcs, texts, dimensions, multileaders, block insertion with attributes,
new layers and styles.

**Files and printing**
Creating, opening, saving (older DWG versions and DXF too) and closing drawings, printing layouts to PDF,
external references, purge, audit, standards from a template.

**Commands and AutoLISP** (only with your permission)
Any AutoCAD command with the answers to its prompts and the command-line text; AutoLISP expressions with their result.

**Batch processing**
The same actions over a folder of drawings without an AutoCAD window, several drawings at once; a report
per file; changed drawings saved as copies in another folder or over the originals with `.bak`, as you
choose; a PDF per drawing or per layout.

**Viewing**
A picture of the view (the assistant sees the drawing), zoom to extents / window / objects, switching layouts.

**Editing**
Object properties (layer, color, linetype, lineweight, transparency, style, dynamic block properties),
dimension settings, move, copy, rotate, scale, mirror, delete, layer management.

**Safety**
Every change the assistant makes is undone in one step, and only its own changes are undone, never
yours. Bulk changes need confirmation and can be previewed first. Objects on locked layers and
locked table cells (including cells linked to Excel) are never changed. The assistant runs AutoCAD
commands and AutoLISP only after you allow it. Batch processing changes files on disk only after you
chose where to save them.

**Control**
The **AI companion** panel on the **Home** tab: **Start**, **Stop**, **Status** buttons and the **Commands**
toggle (commands `MCPSTART`, `MCPSTOP`, `MCPSTATUS`, `MCPCOMMANDS`). To allow commands always, without the
button, set `"allowCommands": true` in `%LOCALAPPDATA%\AcadMcpBridge\config.json` (the installer
creates it with `false`) and restart AutoCAD.

## Requirements

| What | Requirement |
|---|---|
| System | Windows 10 or 11, 64-bit |
| AutoCAD | 2025, 2026 or 2027 (full version; AutoCAD LT is not supported). Toolsets built on AutoCAD (e.g. MEP) load the plugin too |
| AI client | Claude Desktop, Claude Code or Codex (CLI, IDE extension, app) — the installer connects the ones it finds. Other MCP clients can be connected by hand |
| Rights | No administrator rights needed |
| Port | Free port 8766 on this computer (configurable) |

Nothing else to install: no Python and no internet needed during installation; the .NET runtime comes with AutoCAD.

## Installation

1. Unpack `AutoCAD_MCP_@VERSION@.zip` to any folder (do not run it from inside the archive).
2. Close AutoCAD and run `INSTALL.BAT`.
3. Choose the installer language, then the AutoCAD version (a year such as `2026`) or `A` for all supported.
4. The installer:
   - installs the plugin to `%APPDATA%\Autodesk\ApplicationPlugins\AcadMcpBridge.bundle`;
   - creates the settings file `%LOCALAPPDATA%\AcadMcpBridge\config.json` (AutoCAD commands and AutoLISP
     are off in it; an existing file is left as it is);
   - finds Claude Desktop, Claude Code and Codex and asks for each whether to connect the `autocad`
     server (a copy of the client's settings is kept before the change; other servers are not touched).
5. Restart the connected AI clients.
6. Start AutoCAD and press **Start** on the **AI companion** panel (Home tab), or type `MCPSTART`.

If a client is not found or you decline, the installer prints ready-made commands to connect it later.

**Update from 0.2.x or earlier:** just run `INSTALL.BAT` of the new version. It removes the Python
environment of the earlier version (`%LOCALAPPDATA%\AcadMcpBridge\venv`) and switches Claude Desktop to
the new server; the plugin settings are kept. Python itself stays — remove it in Windows Apps if
nothing else needs it. Update only with `INSTALL.BAT`, not by copying files.
**Uninstall:** `INSTALL.BAT`, option `U` — removes the plugin and the `autocad` entry from all clients.
**Unattended:** `INSTALL.BAT 2026 EN`, `INSTALL.BAT A`, `INSTALL.BAT U`.

## Troubleshooting

- The assistant says AutoCAD is not reachable — press **Start** in AutoCAD.
- The assistant does not see the AutoCAD tools — restart it after the installation; if that does not
  help, connect it with the command the installer printed.
- No **AI companion** panel — the plugin did not load: make sure AutoCAD was closed during installation
  and check the log `%LOCALAPPDATA%\AcadMcpBridge\log.txt`.

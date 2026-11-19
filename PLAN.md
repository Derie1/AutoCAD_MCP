# AutoCAD MCP Bridge — план проекта

Плагин для AutoCAD, через который MCP-клиент (Claude Code, Claude Desktop и др.)
управляет запущенным AutoCAD: читает чертёж, создаёт и меняет объекты, работает
со слоями, блоками, листами, печатью.

Создан 2026-11-17. Документ живой: отмечайте сделанное `[x]`, решения — в раздел
«Журнал решений» в конце.

---

## 1. Исходные данные

| Что | Значение |
|---|---|
| Версии AutoCAD на машине | **2026** и **2027** (`C:\Program Files\Autodesk\AutoCAD 20xx\`) |
| .NET у AutoCAD | `acdbmgd.runtimeconfig.json` у обеих версий: **net10.0** (+ WindowsDesktop, AspNetCore 10.0) |
| Сборки API | `acdbmgd.dll`, `acmgd.dll`, `accoremgd.dll` (+ `AcCoreConsole.exe` для пакетной работы без UI) |
| Инструменты | .NET SDK 10.0.401, Python 3.14 |
| Образец | `..\Navisworks_MCP` — та же схема «плагин + HTTP + Python MCP-сервер», 63 инструмента, установка бандлом, `smoke_test.py` |

> AutoCAD 2026 здесь — сборка 25.1.179, и её runtime уже .NET 10 (обновление).
> Но NuGet-пакет `AutoCAD.NET 25.1` нацелен на **net8.0**, и AutoCAD 2026 без
> обновлений работает на .NET 8. Поэтому сборку для 2025/2026 делаем под net8.0:
> она работает и на .NET 8, и на .NET 10. Подробно — раздел 1а.

## 1а. Какие версии AutoCAD можно поддержать (анализ 2026-11-17)

Плагин .NET привязан к **серии** AutoCAD (номер R в реестре) и к версии .NET.
Внутри серии одна DLL работает во всех годах; между сериями нужна отдельная сборка.

| AutoCAD | Серия | .NET | NuGet `AutoCAD.NET` | Сборка плагина |
|---|---|---|---|---|
| 2021 | R24.0 | Framework 4.8 | 24.0.0 (net47) | **R24** — `net48`, одна DLL на 2021–2024 |
| 2022 | R24.1 | Framework 4.8 | 24.1.51000 | ↑ |
| 2023 | R24.2 | Framework 4.8 | 24.2.0 | ↑ |
| 2024 | R24.3 | Framework 4.8 | 24.3.0 | ↑ |
| 2025 | R25.0 | .NET 8 | 25.0.x | **R25** — `net8.0-windows`, одна DLL на 2025–2026 |
| 2026 | R25.1 | .NET 8 (здесь после обновления — .NET 10) | 25.1.x (net8.0) | ↑ |
| 2027 | R26.0 | .NET 10 | 26.0.0 (net10.0) | **R26** — `net10.0-windows` |

Проверено на этой машине: реестр `HKLM\SOFTWARE\Autodesk\AutoCAD\R25.1` (2026, 25.1.179)
и `R26.0` (2027, 26.0.118; там же стоит **AutoCAD MEP 2027** — плагин загрузится и в нём).
Данные по 2021–2025 — по NuGet-пакетам и документации Autodesk, на машине их нет.

**Выводы:**

1. **2025 — бесплатно.** Это та же серия R25, что 2026. Сборка R25 под net8.0 (а не net10)
   работает в 2025 и 2026. Нужно только поставить ссылки на пакет 25.0, а не 25.1, —
   или проверить, что DLL, собранная с 25.1, грузится в 2025 (обычно да, внутри серии).
2. **2027 — отдельная сборка** под .NET 10 (новая серия R26, пакет только под net10.0).
3. **2021–2024 — возможно, но не бесплатно.** Это .NET Framework 4.8, как у Navisworks-плагина.
   Цена:
   - третья сборка и `#if NET48` там, где API .NET различается;
   - нет встроенного `System.Text.Json` → свой небольшой JSON (как в Navisworks MCP —
     там он уже написан именно чтобы не конфликтовать с DLL хост-программы);
   - C# — новейший синтаксис можно, но без API, которых нет в Framework 4.8;
   - **проверить негде**: на машине только 2026/2027. Нужен компьютер или ВМ с одной из
     версий 2021–2024, иначе выпускать «вслепую».
   Сам AutoCAD API (Database, Transaction, Editor, блоки, листы, `PlotEngine`,
   `ExecuteInCommandContextAsync`, `CapturePreviewImage`) в 2021–2027 практически не менялся —
   основные расхождения в .NET, а не в AutoCAD.
4. **Старше 2021** не рассматриваем (по решению).

**Решение (2026-11-17): поддерживаем только 2025–2027** (серии R25 и R26).
2021–2024 не делаем, поэтому совместимость с `net48` не нужна: используем встроенный
`System.Text.Json` (в .NET 8/10 он часть общего фреймворка — конфликта с копией
AutoCAD нет) и современный C#. Минимум сторонних NuGet-пакетов.

Итоговая матрица сборки (`-p:AcadSeries=R25|R26`):

| Сборка | TargetFramework | Ссылки | Папка бандла | `PackageContents.xml` |
|---|---|---|---|---|
| R25 | net8.0-windows | AutoCAD.NET 25.0.x | `Contents\R25\` | SeriesMin R25.0, SeriesMax R25.1 |
| R26 | net10.0-windows | AutoCAD.NET 26.0.0 | `Contents\R26\` | SeriesMin R26.0, SeriesMax R26.0 |

Ссылки на AutoCAD — через NuGet `AutoCAD.NET` с `ExcludeAssets=runtime` (DLL не копируются
в бандл), чтобы собирать любую серию без установленного AutoCAD этой версии.

---

## 2. Архитектура

### Вариант A (выбран для v1) — схема как в Navisworks MCP

```
Claude ──stdio/MCP──► acad_mcp_server.py ──HTTP JSON──► AcadMcpBridge.dll (внутри acad.exe)
                      (Python, mcp SDK)     localhost:8766    HttpListener → очередь → главный поток AutoCAD
```

**Независимость от Navisworks MCP (требование).** Оба моста должны работать
одновременно и не мешать друг другу. Общий только подход, не код и не ресурсы:

| Ресурс | Navisworks MCP | AutoCAD MCP |
|---|---|---|
| Имя MCP-сервера у клиента | `navisworks` | `autocad` |
| Python-сервер | `server_http.py` в `MCPBridge.bundle` | `acad_mcp_server.py` в `AcadMcpBridge.bundle` (своя копия, без импортов из Navisworks) |
| Порт | 8765 | **8766** (настраивается в `config.json` и через переменную `ACAD_MCP_PORT`) |
| Бандл / DLL | `MCPBridge.bundle` / `MCPBridge.dll` | `AcadMcpBridge.bundle` / `AcadMcpBridge.dll` |
| Конфиг и лог | — | `%LOCALAPPDATA%\AcadMcpBridge\` |
| Python-зависимости | `pip install mcp` | свой `.venv` в папке проекта (`AutoCAD_MCP\.venv`), чтобы обновление одного не ломало другой |

Проверки:
- `GET /health` возвращает `"product": "AutoCAD"`; Python-сервер при первом вызове
  сверяет это и выдаёт понятную ошибку, если на порту отвечает что-то другое.
- `INSTALL.BAT` Navisworks удаляет папки `Plugins\MCPBridge*` — имена AutoCAD-моста
  не должны попадать под эти шаблоны (поэтому префикс `Acad`).
- Тест: оба моста запущены, Claude вызывает инструменты обоих в одном диалоге.

- **`AcadMcpBridge.dll`** — .NET-плагин (`IExtensionApplication`), поднимает
  `HttpListener` на `http://localhost:8766/` (8765 занят Navisworks MCP).
  Маршруты `POST /<tool>` и `GET /health`. Про MCP ничего не знает.
- **`acad_mcp_server.py`** — MCP-сервер (stdio) на Python SDK `mcp`. Описывает
  инструменты (имя, описание, JSON-схема) и переводит `tools/call` в HTTP POST.
  Про AutoCAD ничего не знает.

Плюсы: проверенная схема, переиспользуем код и опыт Navisworks MCP
(`server_http.py`, `smoke_test.py`, `INSTALL.BAT`, `Build-Package.ps1`), Python-сторону
можно менять без перезапуска AutoCAD.

### Вариант B (на потом) — MCP-сервер прямо в плагине

AutoCAD 2026/2027 уже загружают `Microsoft.AspNetCore.App 10.0`, значит в плагине
можно поднять Kestrel и официальный C# SDK `ModelContextProtocol` с транспортом
Streamable HTTP: `"type": "http", "url": "http://localhost:8766/mcp"`. Один
компонент вместо двух, схемы инструментов генерируются из атрибутов C#.
Минусы: зависимость от версии ASP.NET внутри AutoCAD, Claude Desktop нужен
stdio-прокси (`mcp-remote`), любая правка инструмента = перезапуск AutoCAD.
→ Решить после v1 (см. «Открытые вопросы»).

### Отвергнутые варианты

- **COM (`AutoCAD.Application` через pywin32 / pyautocad)** — медленно (каждое
  свойство = межпроцессный вызов), ограниченный API, проблемы с ProgID версий и
  регистрацией — те же, что были у старого COM-сервера Navisworks.
- **Только AutoLISP / скрипты `.scr`** — нет нормального обмена данными с клиентом.
  Остаётся как «аварийный люк» (`eval_lisp`, `run_command`, см. ниже).

### Главный технический риск: потоки AutoCAD

AutoCAD API можно вызывать только из главного потока и в правильном контексте.
`HttpListener` отвечает в фоновом потоке, поэтому:

1. Запрос кладётся в очередь (`ConcurrentQueue<WorkItem>` + `TaskCompletionSource`).
2. Обработка в главном потоке: подписка на `Application.Idle` (или
   `SynchronizationContext` UI-потока, захваченный в `Initialize()`).
3. Чтение/изменение базы — `doc.LockDocument()` + `Transaction` (`StartTransaction`,
   `Commit`). Без блокировки документа из application context изменения падают
   с `eLockViolation`.
4. Команды AutoCAD (`Editor.Command`, `-PLOT`, `-PURGE`) — только в контексте команды:
   `DocumentCollection.ExecuteInCommandContextAsync`. Для простых случаев —
   `doc.SendStringToExecute` (асинхронно, результат не вернуть — избегать).
5. Таймаут на каждый запрос (30 с по умолчанию, настраиваемый) — если AutoCAD
   занят модальным диалогом или командой, клиент получает понятную ошибку, а не
   зависание.
6. Если в AutoCAD идёт активная команда — ответ `busy` (проверка
   `doc.CommandInProgress` / `Editor.IsQuiescent`).

Сделать это первым (этап 0) и покрыть тестом: 100 параллельных запросов
`get_document_info` не должны уронить AutoCAD.

---

## 3. Структура проекта

```
AutoCAD_MCP\
├─ PLAN.md                      ← этот файл
├─ README.md                    ← установка и список инструментов (позже)
├─ src\
│  ├─ AcadMcpBridge.csproj      ← -p:AcadSeries=R25|R26 (см. раздел 1а), NuGet AutoCAD.NET без копирования DLL
│  ├─ BridgeApp.cs              ← IExtensionApplication, команды MCPSTART/MCPSTOP/MCPSTATUS, лента
│  ├─ HttpHost.cs               ← HttpListener, маршрутизация, JSON (System.Text.Json)
│  ├─ MainThreadDispatcher.cs   ← очередь → Idle → главный поток, таймауты, busy
│  ├─ Handlers\                 ← по файлу на группу: Document, Query, Draw, Edit, Layers, Blocks, Layouts, Plot, View, Raw
│  └─ Util\                     ← ObjectId ↔ handle, точки/цвета ↔ JSON, ошибки
├─ server\
│  ├─ acad_mcp_server.py        ← MCP-сервер (stdio → HTTP)
│  └─ requirements.txt          ← mcp (ставится в свой venv)
├─ installer\
│  ├─ INSTALL.BAT               ← установщик EN/RU: версия AutoCAD, venv, регистрация в Claude
│  ├─ PackageContents\          ← части манифеста: head, R25.0, R25.1, R26.0, ALL, tail
│  └─ register_claude.py        ← добавить/убрать "autocad" в claude_desktop_config.json
├─ tests\
│  ├─ smoke_test.py             ← прогон всех инструментов на тестовом DWG
│  └─ fixtures\test.dwg
├─ release\                     ← AutoCAD_MCP_<ver>\{INSTALL.BAT, AcadMcpBridge.bundle.zip, README.md} + .zip
└─ Build-Package.ps1            ← сборка R25+R26 → release\; -Deploy ставит здесь
```

Соглашения API:
- Объекты идентифицируются **handle** (hex-строка, стабилен между сессиями),
  не `ObjectId`.
- Координаты — `[x, y, z]` в единицах чертежа (WCS); единицы сообщает `get_document_info`.
- Цвет — ACI (`1..255`), `"ByLayer"`, `"ByBlock"` или `"#RRGGBB"`.
- Списки — постранично (`offset`, `limit`, по умолчанию 200) + `total`.
- Ответ всегда `{ "ok": true, ... }` или `{ "ok": false, "error": "...", "code": "busy|not_found|..." }`.
- Каждое изменяющее действие — отдельная группа отмены (`UNDO BEGIN/END` через
  `TransactionManager` + `Database.UndoRecording`), чтобы `undo` откатывал ровно один вызов MCP.

---

## 4. Этапы и инструменты

**Порядок работы (решено 2026-11-17):** этап 0 → **1 (чтение и анализ)** →
**3 (создание и правка)** — это первая очередь. Затем 2, 4, 5, 6 по необходимости.
Поддерживается «полный» AutoCAD 2025–2027 (раздел 1а), без вертикалей и LT.

### Этап 0 — каркас (MVP связи)
- [x] csproj с сериями R25 (2025–2026, net8.0) и R26 (2027, net10.0); загрузка через `NETLOAD`, затем бандлом
- [x] Команды `MCPSTART` / `MCPSTOP` / `MCPSTATUS`; мост стартует **только по команде**
- [x] Кнопки на ленте: панель «AI companion» на вкладке «Главная» — 0.2.1
- [x] `HttpListener` + диспетчер главного потока + таймауты + `busy`
- [x] `acad_mcp_server.py` с одним инструментом, подключение к Claude Code рядом с Navisworks MCP
- [x] **`health_check`** — версия плагина, версия AutoCAD, порт, число маршрутов
- [x] **`get_document_info`** — имя, путь, единицы (`INSUNITS`), границы, число объектов, текущий слой/лист, сохранён ли

### Этап 1 — чтение чертежа (безопасно, ничего не меняет)
- [x] `list_documents` — открытые чертежи, активный
- [x] `list_layers` — имя, цвет, тип линий, вес, вкл/заморожен/блокирован, печатается, число объектов
- [x] `list_blocks` — определения блоков: имя, атрибуты, число вставок, динамический ли, внешняя ссылка ли
- [x] `find_entities` — фильтр: тип (`LINE`, `INSERT`, `MTEXT`…), слой, имя блока, рамка, пространство (модель/лист) — 0.1.2
- [x] `find_entities`: фильтр по тексту (маска) — 0.1.3
- [x] `get_entity_properties` — общие свойства + специфичные (длина, площадь, точки, текст, атрибуты, свойства динблока, XData)
- [x] `get_selection` — текущий выбор пользователя (PickFirst)
- [x] `list_layouts` — листы, видовые экраны, формат/устройство печати
- [x] `list_xrefs` — внешние ссылки, путь, статус (загружена / не найдена)
- [x] `list_styles` — текстовые, размерные, табличные стили, типы линий
- [x] `get_system_variable` — чтение любых переменных
- [x] `get_table` — содержимое таблиц AutoCAD — 0.1.4
- [x] `extract_block_attributes` — таблица атрибутов всех вставок блока(ов) → JSON/CSV (спецификации, однолинейные схемы)

### Этап 2 — «зрение»
- [x] `capture_view` — PNG в чат (текущий вид / всё / рамка / объекты), рендер вне экрана, листы с видовыми экранами — 0.3.2
- [x] `zoom` (extents / window / entities) вместо трёх отдельных инструментов — 0.3.1
- [x] `set_current_layout` — переключить модель/лист — 0.3.1

### Этап 3 — создание и редактирование
- [x] `create_line` — 0.1.2
- [x] `create_polyline` (2D, с выпуклостями), `create_circle`, `create_arc`
- [x] `create_text`, `create_mtext` — 0.1.6
- [x] `create_table` (из массива строк) — 0.1.7
- [x] `set_table_cells`, `modify_table` (строки, столбцы, объединение, ширина, высота) — 0.1.7
- [x] `insert_block` — с атрибутами и свойствами динблока, масштаб/поворот
- [x] `create_dimension` — линейный / выровненный (позже остальные)
- [x] `set_entity_properties` — слой, цвет, тип линий, вес, видимость
- [x] `update_block_attributes` — массово по фильтру (handle или «блок + атрибут = значение»)
- [x] `transform_entities` — move / copy / rotate / scale / mirror
- [x] `delete_entities` — с подтверждением числа объектов
- [x] `create_layer`, `set_layer_state` (вкл/выкл/заморозить/блокировать/цвет/текущий)
- [x] `set_system_variable`
- [x] `undo` — откатить последние вызовы MCP — 0.1.2

### Этап 4 — файлы, листы, печать
- [ ] `open_document`, `new_document` (по шаблону DWT), `save_document`, `save_as` (с версией формата DWG), `close_document`
- [ ] `plot_to_pdf` — лист(ы) в PDF (`PlotEngine` или `-PLOT`/`PUBLISH` через command context); пакетно все листы
- [ ] `attach_xref`, `reload_xrefs`, `bind_xrefs`
- [ ] `purge`, `audit`
- [ ] `import_layers_from` — стандарт слоёв из шаблона

### Этап 5 — «аварийные люки» (выключены по умолчанию)
- [ ] `run_command` — строка команды AutoCAD через `ExecuteInCommandContextAsync` (`Editor.Command`)
- [ ] `eval_lisp` — выражение AutoLISP, результат обратно (`Application.Invoke` / `acedInvoke`)
- Включаются флагом в `config.json` плагина; в описании инструмента — предупреждение.

### Этап 6 — пакетная обработка без UI (AcCoreConsole)
- [ ] `batch_run` — прогнать операцию (проверка слоёв, замена атрибутов, печать в PDF,
  `purge`/`audit`) по папке DWG через `AcCoreConsole.exe /i file.dwg /s script.scr`
  с загрузкой той же DLL (core-часть без `acmgd`).
- Требует разделить код на «core» (`accoremgd`, `acdbmgd`) и «UI» (`acmgd`).
  Учесть с самого начала: обработчики этапов 1, 3, 4 не должны ссылаться на `acmgd`.

### Идеи под мои задачи (позже)
- Однолинейные схемы / щиты (связь с `SLD_Builder`, `VBA_dwgPanelBuilder`): построение
  схемы из Excel/JSON, выгрузка атрибутов щитов в Excel.
- Проверка чертежа по стандарту: слои, стили, шрифты, лишние блоки → отчёт.
- Связка с Navisworks MCP: экспорт DWG → NWC и обновление федеративной модели.

---

## 4а. Итерация 1 — этапы 1 + 3 и таблицы (план от 2026-11-17)

**Статус: выполнена 2026-11-17, выпуск 0.2.0** (34 инструмента, smoke-тест 174/174 в AutoCAD 2026 и 2027).

Цель: Claude читает чертёж и правит его, включая таблицы AutoCAD (чтение содержимого
и заполнение по заданию). Делаем шагами; после каждого шага — сборка R25/R26, тест
в 2026 и 2027, `Build-Package.ps1` (release актуален), запись в журнал.

### Проверка API таблиц (AcDbMgd 26.0)

Объект `Table` (ACAD_TABLE) даёт всё нужное, методы есть в документации пакета:

| Задача | API |
|---|---|
| Размер, строки/столбцы | `Rows.Count`, `Columns.Count`, `RowHeight`, `ColumnWidth`, `SetSize` |
| Текст ячейки | `GetTextString(row, col, content, FormatOption)` — с форматированием MText или чистый текст; `SetTextString` |
| Значение и тип | `GetValue` / `SetValue` (число, дата, текст) |
| Формулы | `HasFormula`, `GetFormula`, `SetFormula` |
| Поля (fields) | `GetFieldId` / `SetFieldId` |
| Объединённые ячейки | `IsMergedCell`, `GetMergeRange`, `MergeCells`, `UnmergeCells` |
| Блок в ячейке | `GetBlockTableRecordId`, `Get/SetBlockAttributeValue` |
| Защита | `GetCellState` (заблокировано содержимое/формат), `IsContentEditable` |
| Связь с Excel | `GetDataLink`, `GetDataLinkRange`, `UpdateDataLink` |
| Строки/столбцы | `InsertRows(AndInherit)`, `DeleteRows`, `InsertColumns`, `DeleteColumns`, `CanDeleteRows` |
| Массовое заполнение | `SuppressRegenerateTable(true)` → запись → `false` + `RecomputeTableBlock` |
| Стиль | `TableStyle` (словарь `ACAD_TABLESTYLE`) |

Особые случаи, которые надо обработать явно:
- **Текст с форматированием** (`\P`, `{\fArial|…}`): по умолчанию отдавать чистый текст, по флагу — сырой.
- **Объединённые ячейки:** значение только в левой верхней; остальные — `merged_into: [r, c]`.
- **Заблокированные ячейки** (`GetCellState` → содержимое заблокировано; так же AutoCAD блокирует ячейки
  со связью с Excel): запись запрещена, ошибка `cell_locked` с адресами ячеек и подсказкой снять блокировку
  в AutoCAD. Обхода (`force`) нет — писать можно только после того, как пользователь сам снял блокировку.
- **Формулы:** читать и результат, и формулу; записывать формулу, если значение начинается с `=`.
- **Большие таблицы:** чтение диапазоном (`rows: [from, to]`), запись — одним вызовом пачкой ячеек.
- **«Псевдотаблицы» из линий и текстов** (частые в старых чертежах) — это не `Table`; не входят в итерацию,
  см. «Возможные усовершенствования» в конце плана.

### Общие решения для записи (нужны с первого изменяющего инструмента)

- Каждый изменяющий вызов: `doc.LockDocument(Write, "MCP_<tool>", …)` + одна транзакция. Ожидание:
  такой вызов — **один шаг UNDO** с именем `MCP_<tool>`. **Проверить первым делом** (риск).
- Отказ с `busy`, если в AutoCAD идёт команда; `read_only`, если чертёж открыт только на чтение;
  понятная ошибка на заблокированном слое (`eOnLockedLayer`).
- Удаление и массовые изменения — параметр `confirm: true` и ответ с числом затронутых объектов.
- После изменения — обновление экрана (`Editor.UpdateScreen` / `Regen` по необходимости).
- Тесты, которые меняют чертёж, работают только в **новом безымянном пустом** чертеже
  (`DrawingN.dwg`, 0 объектов) — иначе тест отказывается запускаться. Реальные чертежи не трогаются.

### Последовательность шагов

| Шаг | Что делаем | Инструменты | Готово, когда |
|---|---|---|---|
| **1. Основа** | Адресация объектов по handle; общий «паспорт» объекта (тип, слой, цвет, handle, пространство/лист); фильтр (тип, слой, блок, рамка, пространство, лист); постраничность; каркас записи (lock + транзакция + undo + ошибки). Для проверки каркаса сюда же перенесены `find_entities` (из шага 2), `create_line` (из 5) и `undo` (из 4) | `find_entities`, `create_line`, `undo` | `undo` откатывает ровно один вызов MCP (проверено на тестовой линии) |
| **2. Чтение чертежа** | Этап 1 | `list_documents`, `list_layers`, `list_blocks`, `list_layouts`, `list_xrefs`, `list_styles`, `get_system_variable`, `get_entity_properties`, `get_selection` | Claude описывает незнакомый чертёж без угадывания |
| **3. Чтение таблиц и атрибутов** | | `find_entities type=TABLE`, **`get_table`** (весь или диапазон; текст/значения/формулы/объединения/блокировки/связь с Excel), `extract_block_attributes` | Таблица с объединениями, формулой и блоком читается в JSON без потерь |
| **4. Базовая правка** | Этап 3, часть 1 | `undo`, `create_layer`, `set_layer_state`, `set_entity_properties`, `transform_entities` (move/copy/rotate/scale/mirror), `delete_entities`, `set_selection`, `set_system_variable` | Каждая операция откатывается `undo` |
| **5. Создание объектов** | Этап 3, часть 2 | `create_line`, `create_polyline`, `create_circle`, `create_arc`, `create_text`, `create_mtext`, `insert_block` (+атрибуты, свойства динблока), `update_block_attributes`, `create_dimension` | Claude чертит простую схему по описанию |
| **6. Запись в таблицы** | | **`set_table_cells`** (пачка ячеек или 2D-блок с позиции; текст/число/формула), `insert_table_rows` / `delete_table_rows` (то же для столбцов), **`create_table`** (из 2D-массива: стиль, заголовок, ширины, позиция), `merge_table_cells` | Claude заполняет существующую таблицу и создаёт новую из данных пользователя |
| **7. Выпуск** | Тест на все инструменты, README (список инструментов), PLAN, релиз 0.2.0, коммит | — | smoke-тест N/N в 2026 и 2027 |

Порядок обоснован зависимостями: шаг 1 нужен всем; чтение (2–3) безопасно и даёт данные для
проверки записи; запись в таблицы (6) опирается на каркас записи (4) и чтение таблиц (3).
Шаги 2–3 и 4–5 можно поменять местами, если правка нужна раньше.

## 4б. Итерация 2 — этап 2 («зрение») и известные проблемы (план от 2026-11-19)

Цель: ассистент видит чертёж (зум, листы, снимок в чат) и закрывает пробелы, найденные при работе
(раздел 10). Порядок работы как в итерации 1: шаг = версия +0.0.1, тест в 2026 и 2027, `Build-Package.ps1`,
коммит только с согласия, без push; в конце 0.4.0, README, RELEASE.md, коммит и push.

Решения при согласовании:
- `run_command` / `eval_lisp` (этап 5, создание динблоков) — **не в этой итерации**, отдельно (0.5.0).
- `capture_view` **не сдвигает вид пользователя**; другой вид — только явным `zoom`.
- Новые объекты получают цвет/тип/вес линий **«ПоСлою»**, если параметр не передан (не CECOLOR/CELTYPE/CELWEIGHT).
- Стили — четыре инструмента «создать или изменить» по видам, а не один универсальный.
- Зум — один инструмент `zoom` с режимами вместо трёх из этапа 2.

| Шаг | Версия | Что делаем |
|---|---|---|
| 1 | 0.3.1 | `zoom` (extents / window / entities по handle или фильтру, отступ), `set_current_layout`. Смена вида не трогает `undo` ассистента |
| 2 | 0.3.2 | `capture_view` (текущий вид / весь чертёж / рамка / объекты; размер; сохранение в файл); по возможности рендер вне экрана; сервер отдаёт MCP-контент `image` |
| 3 | 0.3.3 | `create_layer` без `linetype` (Continuous); параметры `linetype`/`lineweight` в `insert_block` и `create_*`; «ПоСлою» по умолчанию |
| 4 | 0.3.4 | `dynamic_properties` в `set_entity_properties` (пакетно, `confirm`, `dry_run`), handle сохраняется |
| 5 | 0.3.5 | Размеры: переопределения из системных переменных, `text_height`, `arrow_size`, `text_style`, `text_position`, `fit`; правка существующего размера |
| 6 | 0.3.6 | `set_text_style`, `set_dim_style`, `set_table_style`, `set_mleader_style`; `style` в `set_entity_properties`; `text_style`/`text_height` в `create_table` |
| 7 | 0.3.7 | `create_mleader(points, text, style, layer)` |
| 8 | 0.4.0 | Тесты, README, RELEASE.md, отметки в разделе 10, коммит и push |

## 5. Сборка, установка, проверка

- **Сборка:** `dotnet build src\AcadMcpBridge.csproj -c Release -p:AcadSeries=R25` (и `R26`).
  Ссылки — NuGet `AutoCAD.NET` нужной серии (25.0.x и 26.0.0).
  2025 на машине нет: сборку R25 проверяем в 2026; запуск в 2025 — при возможности на другом ПК.
- **Отладка:** `NETLOAD` из `bin\`, отладчик VS/Rider с `acad.exe` как стартовой программой.
  Горячую перезагрузку .NET-сборок AutoCAD не поддерживает → перезапуск AutoCAD.
- **Установка:** бандл `AcadMcpBridge.bundle` в `%APPDATA%\Autodesk\ApplicationPlugins\` (как SldBuilder;
  из `%ProgramData%` AutoCAD 2026 бандл не подхватил — см. журнал),
  `PackageContents.xml` с `RuntimeRequirements SeriesMin/SeriesMax` по таблице раздела 1а
  (серии R25.1 и R26.0 проверены по реестру этой машины). `INSTALL.BAT` по образцу SLD Builder — см. README,
  раздел «Установка у пользователя».
- **Подключение к Claude Code:**
  ```
  claude mcp add autocad -- "<AutoCAD_MCP>\.venv\Scripts\python.exe" "%APPDATA%\Autodesk\ApplicationPlugins\AcadMcpBridge.bundle\Contents\acad_mcp_server.py"
  ```
  Claude Desktop — `claude_desktop_config.json`, как у Navisworks.
- **Проверка:** `tests\smoke_test.py` прогоняет все маршруты на `tests\fixtures\test.dwg`
  в 2026 и 2027; результат «N/N» записывать в журнал ниже.
  Тесты не должны трогать реальные проектные пути — только копию в temp.

## 6. Безопасность
- Слушать только `localhost` (`http://localhost:8766/`, не `+`/`*`).
- Опциональный токен в заголовке (`X-Bridge-Token`, хранится в `config.json` рядом с DLL).
- Разрушающие действия (`delete_entities`, `purge`, `close_document` без сохранения,
  `run_command`, `eval_lisp`) — явный параметр `confirm: true`.
- Все изменения — с группой отмены; логи вызовов в `%LOCALAPPDATA%\AcadMcpBridge\log.txt`.

---

## 7. Открытые вопросы
1. Вариант B (MCP внутри плагина на C#) — вернуться после v1?

## 8. Журнал решений
- 2026-11-17 — составлен план; порт 8766; образец — Navisworks MCP.
- 2026-11-17 — архитектура A (Python + HTTP), но полностью независимо от Navisworks MCP:
  свои имена, порт, бандл, Python-сервер и зависимости; оба моста работают параллельно.
- 2026-11-17 — мост запускается только командой `MCPSTART` (и кнопкой на ленте).
- 2026-11-17 — первая очередь: этап 1 (чтение и анализ) и этап 3 (создание и правка).
- 2026-11-17 — поддержка только AutoCAD 2026/2027; вертикали (Electrical, Civil 3D) и LT — нет.
- 2026-11-17 — версии: **2025–2027**. Две сборки: R25 (net8.0, 2025–2026) и R26 (net10.0, 2027).
  2021–2024 (R24, .NET Framework 4.8) — не поддерживаем; совместимость с net48 в коде не нужна.
- 2026-11-17 — venv в папке проекта: `AutoCAD_MCP\.venv` (в `.gitignore`). OneDrive будет его
  синхронизировать; если начнутся блокировки файлов при `pip install` — пересоздать venv
  (`python -m venv .venv`), он не хранит ничего ценного.
- 2026-11-17 — **этап 0 сделан (v0.1.0).** Находки:
  - NuGet `AutoCAD.NET` 25.0.2 и 25.1.1 (обновления 2025/2026, авг–сен 2026) уже под net10.0;
    для R25 берём **25.0.1** (последний net8.0) — такая DLL работает и на .NET 8, и на .NET 10.
  - Бандл из `%ProgramData%\Autodesk\ApplicationPlugins` AutoCAD 2026 не загрузил (нет записи
    в `HKCU\...\R25.1\ACAD-9101:409\Applications`). Перенесли в `%APPDATA%\...\ApplicationPlugins`
    и упростили манифест по образцу SldBuilder — загрузился. Какое из двух изменений помогло, не выяснялось.
  - Python SDK `mcp` 2.x: `FastMCP` переименован в `MCPServer` (`from mcp.server import MCPServer`).
  - Главный поток: скрытый `Control`, созданный в `Initialize()`, + `BeginInvoke`; таймаут 30 с → `busy`.
  - `smoke_test.py`: AutoCAD 2026 (сборка R25 на .NET 10.0.15) — **15/15**; AutoCAD 2027
    (сборка R26) — **15/15**; 100 параллельных запросов без ошибок в обоих.
  - Не проверено: AutoCAD 2025 (нет на машине), 2026 без обновления (.NET 8),
    одновременная работа с запущенным Navisworks MCP, подключение к Claude Code.
- 2026-11-17 — сервер `autocad` добавлен в `%APPDATA%\Claude\claude_desktop_config.json` рядом с
  `navisworks` (копия до правки: `claude_desktop_config.json.bak-autocad`). Команда — `python.exe` из
  `AutoCAD_MCP\.venv`, скрипт — из установленного бандла. В `mcp` 2.x до модели доходит текст только
  у `ToolError`, поэтому `BridgeError` наследуется от него (иначе вместо «введите MCPSTART»
  Claude видел бы голое «Error executing tool»).
- 2026-11-17 — **v0.1.1: установщик по образцу SLD Builder.** `installer\INSTALL.BAT` (UTF-8 без BOM, CRLF):
  выбор языка (вопрос на английском, EN/RU), список найденных AutoCAD, год / `A` / `U` / `Q`,
  распаковка `AcadMcpBridge.bundle.zip`, манифест из частей под выбранные версии, venv в
  `%LOCALAPPDATA%\AcadMcpBridge\venv` + `pip install mcp`, регистрация `autocad` в Claude Desktop.
  У пользователя venv — в `%LOCALAPPDATA%`, а не в папке проекта (её у пользователя нет);
  `AutoCAD_MCP\.venv` остаётся для разработки. **Правило: после любых изменений запускать
  `Build-Package.ps1`, чтобы `release\` был актуален.** Проверено в песочнице: 2026, добавление 2027,
  2025 без AutoCAD (предупреждение), 2024 / 2030 / мусор (отказ), интерактивный выбор языка,
  удаление (запись navisworks в конфиге Claude не тронута). Ответ `Y/N` через `choice` в интерактиве
  вручную не проверялся.
- 2026-11-17 — **решения по итерации 1:** заблокированные ячейки таблиц (в т.ч. связанные с Excel) не
  записываются, ошибка с подсказкой; писать только после снятия блокировки пользователем в AutoCAD.
  Псевдотаблицы — в «Возможные усовершенствования». Коммит после каждого шага без push, версия
  +0.0.1 за шаг (0.1.2, 0.1.3 …); после шага 7 — 0.2.0, README с функциями, коммит и push.
  AutoCAD для тестов запускаю сам.

- 2026-11-17 — **шаг 1 сделан (0.1.2).** Основа: адресация по handle, «паспорт» объекта, `EntityFilter`
  (типы с псевдонимами TABLE/BLOCK/POLYLINE/TEXT_ANY, слои и блоки с масками, рамка inside/crossing,
  пространство), постраничность, `Write.Run` (блокировка `MCP_<TOOL>` + транзакция), маршруты трёх видов
  (HTTP / главный поток / асинхронные команды). Инструменты: `find_entities`, `create_line`, `undo`.
  Находки:
  - Блокировка документа с именем команды = один шаг UNDO на вызов — **подтверждено**.
  - Но неудачный вызов тоже оставляет **пустую** группу UNDO. Поэтому `UndoTracker` хранит список групп
    (реальная / пустая), и `undo` откатывает нужное число шагов вместе с пустыми.
  - Любая команда пользователя очищает список: `undo` отказывается (`nothing_to_undo`) — проверено
    командой LINE, отправленной через COM.
  - `undo` идёт через `ExecuteInCommandContextAsync` + `Editor.CommandAsync("_.UNDO", n)`.
  - Запуск AutoCAD через WMI стал запрещён (код 2); `tests\AcadSession.ps1` запускает дочерним процессом,
    закрывает через COM с повторами (AutoCAD бывает занят: RPC_E_CALL_REJECTED) и отказывается закрывать,
    если есть несохранённый именованный чертёж.
  - smoke-тест: 35/35 в 2026 и 2027; запись проверяется только в новом пустом безымянном чертеже.

- 2026-11-17 — **шаг 2 сделан (0.1.3).** Инструменты чтения: `list_documents`, `list_layers` (со счётчиками
  объектов), `list_blocks` (атрибуты, вставки с учётом динблоков), `list_layouts`, `list_xrefs`, `list_styles`
  (текст, размеры, таблицы, мультивыноски, типы линий), `get_system_variable`, `get_entity_properties`
  (до 100 handle за вызов, свойства по типу, атрибуты и свойства динблоков, XData по флагу),
  `get_selection` (PickFirst; из контекста приложения работает), фильтр `text` в `find_entities`
  (TEXT, MTEXT, атрибуты, размеры, ячейки таблиц). Находки:
  - Маска текста должна охватывать переносы строк (MTEXT) — `RegexOptions.Singleline`.
  - `Table.GetTextString(r, c, i, …)` устарел; новый путь — `table.Cells[r, c].GetTextString(FormatOption)`.
  - Тестовый чертёж строится через COM (`testsixture.py`, pywin32 только в dev-venv,
    `tests
equirements-dev.txt`); выделение — `AI_SELALL` / `AI_DESELECT` (ESC через SendCommand не проходит).
  - smoke-тест: 71/71 в 2026 и 2027.

- 2026-11-17 — **шаг 3 сделан (0.1.4).** `get_table`: ячейки 2D-массивом (plain / raw / value), диапазон
  строк и столбцов, объединения, формулы, заблокированные ячейки, блоки в ячейках с атрибутами, связи с
  Excel, выделенные пользователем ячейки (`SubSelection`); больше 5000 ячеек — частями с подсказкой. Без
  handle берёт единственную выделенную таблицу или единственную в чертеже. `extract_block_attributes` —
  атрибуты блоков таблицей (столбцы = теги), фильтры как у `find_entities`. Находки:
  - API таблиц снят из метаданных `AcDbMgd.dll` (MetadataLoadContext; XML-документация неполная):
    новый путь — `table.Cells[r, c]` (`Cell`: `GetTextString(FormatOption)`, `Value`, `State`,
    `Contents[i].Formula/HasFormula/BlockTableRecordId`, `GetMergeRange`, `IsLinked`, `DataType`).
  - `IsMerged` / `GetMergeRange` верны только у левой верхней ячейки объединения — объединения
    собираются один раз по всей таблице.
  - Сразу после запуска AutoCAD COM-объект ещё не отдаёт `ActiveDocument` — фикстура ждёт.
  - smoke-тест: 91/91 в 2026 и 2027.

- 2026-11-17 — **шаг 4 сделан (0.1.5).** `create_layer`, `set_layer_state` (вкл/выкл, заморозка,
  блокировка, печать, цвет, тип и вес линий, текущий, переименование; маски имён), `set_entity_properties`,
  `transform_entities` (move / copy с массивом / rotate / scale / mirror), `delete_entities`, `set_selection`,
  `set_system_variable`. Общий выбор объектов `Targets`: `handles` или `filter` (ключи `find_entities`);
  фильтр требует `confirm: true`, `dry_run` показывает, что будет затронуто; объекты на заблокированных
  слоях — отказ всего вызова. Выбор делается до блокировки документа, поэтому отказ и dry_run не оставляют
  пустых групп UNDO. Типы линий подгружаются из acadiso.lin / acad.lin. smoke-тест: 120/120 в 2026 и 2027.

- 2026-11-17 — **шаг 5 сделан (0.1.6).** `create_polyline` (дуговые сегменты через bulge, ширина, замкнутость),
  `create_circle`, `create_arc` (центр + углы или три точки), `create_text` (выравнивание, поворот, стиль),
  `create_mtext`, `create_dimension` (aligned / linear под углом, замена текста, стиль), `insert_block`
  (масштаб, поворот, значения атрибутов, свойства динблоков), `update_block_attributes` (одно значение по
  handles/filter или разные значения на каждый блок через `updates`). Все создающие инструменты возвращают
  полные свойства нового объекта. Находки:
  - `CircularArc3d` не отвергает три точки на одной прямой — проверка коллинеарности своя.
  - Динамические блоки через COM для теста не создать — установка свойств динблока проверена только
    на отказе для обычного блока; на реальном динблоке не проверено.
  - smoke-тест: 147/147 в 2026 и 2027.

- 2026-11-17 — **шаг 6 сделан (0.1.7).** `set_table_cells` (ячейки списком или 2D-блок с позиции; без `start` —
  от выделенных пользователем ячеек; `add_rows` дописывает строки с форматом последней; текст, числа,
  `=формула`, null = очистить), `modify_table` (insert/delete rows/columns, merge/unmerge, ширина столбца,
  высота строки — один инструмент вместо четырёх из плана), `create_table` (заголовок с объединением, шапка,
  ширины, стиль, слой). Заблокированные ячейки (в т.ч. связанные с Excel): запись и удаление строк с ними —
  отказ `cell_locked` с адресами и подсказкой, ничего не меняется, обхода нет. Запись внутрь объединения и в
  ячейки с блоком — отказ. Находки:
  - `Cell.SetValue(double, ParseOption)` оставляет ячейку пустой; работает `Cell.Value = d`.
  - Без формата число показывается с 15 знаками. Формат задаётся `Contents[0].DataFormat = "%lu2%prN"`
    (`Cell.DataFormat` на вывод не влияет); N = числу знаков в исходном числе.
  - Текст «как на экране» для чисел даёт `GetTextString(FormatOptionNone)`; `IgnoreMtextFormat` — полную
    точность. `get_table` (plain): текст — без MTEXT-кодов, числа и формулы — как показаны.
  - smoke-тест: 174/174 в 2026 и 2027.

- 2026-11-17 — **шаг 7: выпуск 0.2.0.** README переписан: что умеет (34 инструмента по группам, примеры
  запросов), правила безопасности изменений, ограничения, как запускать тесты. smoke-тест 174/174 в
  2026 и 2027 на версии 0.2.0. Коммит и push в github.com/Derie1/AutoCAD_MCP.
  Дальше по плану: этап 2 («зрение»: снимок вида, зум) и этап 4 (файлы, листы, печать в PDF);
  проверить свойства динблоков на реальном динамическом блоке.

- 2026-11-17 — **0.2.1: панель на ленте.** Три иконки (`tools\Make-Icons.ps1` → `src\Resources\*.png`, встроены в
  DLL), панель «AI companion» с кнопками Start / Stop / Status на вкладке «Главная». Делается API ленты
  (`Autodesk.Windows.ComponentManager.Ribbon`) при загрузке плагина — без правки CUIx: ничего не надо
  настраивать при установке, при удалении плагина панель исчезает; после смены рабочего пространства
  (WSCURRENT) добавляется снова; без вкладки «Главная» — своя вкладка. Кнопка выполняет команду через
  `SendStringToExecute`. Находки:
  - Id вкладки — `ACAD.ID_TabHome` (с префиксом группы CUI), `FindTab("ID_TabHome")` её не находит.
  - `UseWPF` убирает `System.IO` из неявных using (конфликт с `System.Windows.Shapes.Path`).
  - Кнопки проверены нажатием через UI Automation (Stop → мост выключен, Start → включён) в 2026 и 2027;
    `health_check` сообщает `ribbon: "Home tab"`. smoke-тест 175/175 в 2026 и 2027.

- 2026-11-17 — **release\:** архивы всех версий сохраняются (`AutoCAD_MCP_<ver>.zip`), распакованная папка
  заменяется текущей. Добавлено описание релиза `installer\RELEASE.md` (RU, затем EN: что это, возможности
  по группам, требования, установка, неполадки) — копируется в `release\RELEASE.md` и в архив. Архивы до 0.2.1
  были удалены прежней логикой сборки; при необходимости их можно пересобрать из коммитов.

- 2026-11-18 — **0.3.0: MCP-сервер на C# вместо Python.** `server\AcadMcpServer`: свой минимальный MCP по stdio
  (initialize / ping / tools/list / tools/call, параллельные вызовы), без NuGet-пакетов; определения 34
  инструментов выгружены из Python-сервера в `tools.json` (встроен в DLL). Запуск — `dotnet.exe
  acad_mcp_server.dll` (net8.0, RollForward Major): **неподписанный exe на этом ПК блокируется политикой
  в любых папках пользователя** («Access is denied»), а подписанный `dotnet.exe` с DLL работает.
  Регистрация в клиентах — сама DLL: `--register|--unregister claude-desktop|claude-code|codex|all`
  (Claude Desktop — JSON, Codex — таблица `[mcp_servers.autocad]` в `config.toml`, остальное не трогается,
  Claude Code — через CLI `claude mcp add --scope user`; копия `.bak-autocad` перед первой правкой).
  Установщик: удаляет окружение Python 0.2.x (`%LOCALAPPDATA%\AcadMcpBridge\venv`), находит клиенты и
  спрашивает по каждому (уже подключённый — обновляет без вопроса), печатает команды для ручного
  подключения; `U` отключает все клиенты. Python в проекте остался только для тестов. Проверено:
  песочница (обновление с 0.2.x, Codex, удаление, без клиентов), Claude Code — с подставной командой
  `claude`; smoke-тест 178/178 в 2026 и 2027 (MCP через `dotnet acad_mcp_server.dll`). На этой машине
  Claude Desktop перерегистрирован на установленную DLL. Вариант B (сервер внутри плагина) отклонён:
  Claude Desktop не подключается к localhost по HTTP, при закрытом AutoCAD инструментов не видно,
  риск для стабильности и конфликтов сборок внутри acad.exe.

- 2026-11-19 — **0.3.1 (итерация 2, шаг 1): `zoom`, `set_current_layout`.** Зум — `Editor.SetCurrentView`
  с пересчётом рамки WCS → DCS вида и подгонкой под пропорции окна; extents считается по видимым объектам
  текущего пространства (слои выкл/замороженные пропускаются, первый видовой экран листа — тоже).
  **Вид и UNDO:** блокировка документа в контексте приложения — это отдельная группа UNDO, и инструмент
  `undo` (UNDO n) откатывал бы зум вместо изменения. `DisableUndoRecording` не помогает. Решение: зум без
  блокировки (`SetCurrentView` работает и так — UNDO не трогается); переключение листа без блокировки
  падает (`eSetFailed`), поэтому оно под блокировкой и записывается в UndoTracker как пустая группа
  (откат последнего изменения заодно возвращает лист). При проверке найдено зависание `undo` после COM
  `SendCommand` (раздел 10) — тестовая заготовка больше не выделяет объекты через COM `SendCommand`.
  smoke-тест 198/198 в 2026 и 2027 (по 2 прогона).

- 2026-11-19 — **0.3.2 (итерация 2, шаг 2): `capture_view`.** Рендер вне экрана: `Manager.AcquireGraphicsKernel`
  («3D Drawing») → `CreateAutoCADOffScreenDevice` → свой `GraphicsSystem.View` (направление и поворот как у вида
  пользователя, своё окно через `SetView`) → `Device.GetSnapshot`. Вид пользователя не меняется (проверено по
  VIEWCTR/VIEWSIZE). `CapturePreviewImage` не нужен. Цвет 7 сам подстраивается под фон (dark/light).
  **Листы:** вид листа рисует только рамки видовых экранов, без модели. Поэтому модель каждого видового экрана
  рендерится отдельно (окно = часть ВЭ внутри кадра, масштаб ViewHeight/Height, заморозка слоёв ВЭ) и
  накладывается на картинку листа с прозрачным фоном: если добавить несколько видов на одно устройство,
  каждый стирает свой прямоугольник (пропадают рамки и перекрытия). Непрямоугольная подрезка ВЭ не учитывается.
  MCP-сервер отдаёт поле `image` моста как MCP-контент `image` (PNG base64) + текст с остальным ответом.
  smoke-тест 212/212 в 2026 и 2027.

- 2026-11-19 — **0.3.3 (итерация 2, шаг 3): исправления из раздела 10.** Новый слой получает тип линий
  Continuous (как команда СЛОЙ); имя типа линий слоя читается безопасно и для слоёв без него (`list_layers`).
  Все `create_*`, `insert_block` и `create_table` ставят цвет, тип и вес линий **«ПоСлою»**, если параметр не
  передан, — не CECOLOR/CELTYPE/CELWEIGHT (общий `Props.ApplyCommon`); у всех появились `linetype` и
  `lineweight`, у `create_table` ещё и `color`. smoke-тест 224/224 в 2026 и 2027.

- 2026-11-19 — **0.3.4 (итерация 2, шаг 4): `dynamic_properties` в `set_entity_properties`.** Меняет параметры
  уже вставленных динамических блоков (по handle или фильтру с `confirm`/`dry_run`, вместе с другими свойствами);
  handle, атрибуты и XData сохраняются, вызов — один шаг `undo`. Если среди целей есть не динамический блок —
  отказ без изменений. **Найдено:** AutoCAD молча подгоняет значение не из списка параметра к ближайшему
  допустимому (850 → 800) — теперь такой вызов отклоняется с перечнем допустимых значений; если AutoCAD изменил
  значение из-за пределов/шага параметра — тоже отказ (то же в `insert_block`). Впервые проверено на настоящем
  динамическом блоке: тест копирует «Door - Metric» из примера AutoCAD (`Sample\en-us\Dynamic Blocks\
  Architectural - Metric.dwg`) через ObjectDBX `CopyObjects` (без `SendCommand`). smoke-тест 235/235 в 2026 и 2027.

- 2026-11-19 — **0.3.5 (итерация 2, шаг 5): размеры.** `create_dimension` без `style` берёт текущий стиль вместе
  с текущими переопределениями DIM* (`SetDimstyleData(db.GetDimstyleData())`), как РЗМЛИНЕЙНЫЙ. Новые параметры
  (переопределения у объекта): `text_height`, `arrow_size`, `arrow` (стрелки AutoCAD `_ARCHTICK`… или блок;
  блок стрелки создаётся через DIMBLK с возвратом прежнего значения), `text_style`, `decimals`, `dim_scale`,
  `fit` (best / arrows / text / both_outside / text_inside = DIMATFIT + DIMTIX), `text_position` (+`text_leader`,
  DIMTMOVE 2/1). Новый инструмент **`modify_dimension`** — те же параметры, `text_override`, смена `style`,
  `reset_overrides`, по handle или фильтру; `get_entity_properties` показывает действующие значения размера.
  Свойство текстового стиля у `Dimension` — `TextStyleId` (не `Dimtxsty`). Попутно исправлены описания `color`
  у `create_*` (после шага 3 оставалось «текущий цвет»). 38 инструментов, smoke-тест 252/252 в 2026 и 2027.

## 9. Возможные усовершенствования
- **Псевдотаблицы из линий и текстов** — распознавать сетку из отрезков/полилиний и тексты в ячейках,
  читать как обычную таблицу; запись — заменой текстов. Частый случай в старых чертежах и схемах щитов.
- **Python-сервер разбить на модули**, когда инструментов станет больше ~40 (пока один файл проще
  копировать в бандл).

## 10. Известные проблемы
Список пополняется по результатам тестов. Исправленное — отмечать `[x]` с версией и датой.

- [x] *(исправлено в 0.3.3, 2026-11-19)* **`create_layer` без `linetype` падает с `[autocad_error] NullObjectId: eNullObjectId`** (найдено 2026-11-17,
  плагин 0.2.1, AutoCAD 2027, при черчении узла через мост).
  - *Воспроизведение:* `create_layer(name="Тест", color=8, lineweight=0.5)` — без параметра `linetype`.
    С `linetype="Continuous"` (или любым другим) слой создаётся нормально.
  - *Причина:* у нового `LayerTableRecord` `LinetypeObjectId` пустой, пока его явно не задали; `Apply()`
    задаёт тип линии только если он передан, а `Describe()` (`src/Handlers/LayerHandlers.cs`) делает
    `GetObject(l.LinetypeObjectId)` → исключение. Транзакция откатывается, слой не создаётся.
  - *Исправление:* в `CreateLayer` после создания записи по умолчанию ставить
    `layer.LinetypeObjectId = db.ContinuousLinetype` (как делает команда СЛОЙ); в `Describe()` на всякий
    случай обрабатывать `ObjectId.Null` (вернуть `"Continuous"`). Добавить тест на создание слоя без `linetype`.

- [ ] **Через мост нельзя создать динамический блок, а обход через LISP слишком медленный** (найдено 2026-11-18,
  плагин 0.3.0, AutoCAD 2027; задача: превратить полилинию отвода кабельного лотка 90° в динамический блок
  с параметром «Width» = 50/100/150/200/300/400/500/600, линии на слое 0, цвет/тип линии «ПоБлоку»).
  - *Что пробовали:* LISP-скрипт `TRAYELBOW`: блок с чистой геометрией через `entmake` → `-BEDIT` →
    `BPARAMETER Linear` (Label/Value set List) → два `BACTION Stretch` (второй — с угловым смещением −90°) →
    `BSAVE`/`BCLOSE` → вставка вместо полилинии. Проверяли на копии чертежа. Около 5 прогонов и много времени,
    блок так и не создан.
  - *Причины:*
    1. В мосте нет `run_command` / `eval_lisp` (этап 5), поэтому скрипт нельзя выполнить и отладить прямо
       в сеансе пользователя.
    2. В .NET API нет создания динамических блоков (параметров и действий) — только команды редактора
       блоков, а их запросы в LISP приходится подбирать вслепую.
    3. AcCoreConsole не поддерживает редактор блоков (`-BEDIT` — Unknown command), а из Git Bash вообще
       зависает (из PowerShell работает). Каждая проверка требовала запуска полного AutoCAD 2026 с `/b script.scr`
       (~1 мин), результат приходилось читать из `LOGFILEON`-журнала. Пользователь видел тестовое окно
       и работал в нём, журналы смешивались.
    4. Подводные камни команд: параметр не выбирается через `entlast` (только точкой на размерной линии);
       `BACTIONTOOL` и первый `BACTION` зацикливаются на «Select objects» — `""` не завершает выбор;
       после первого `BACTION` `CMDACTIVE` = 4, и следующая строка скрипта уходит в командную строку.
  - *Решение (на будущее):* реализовать этап 5 — `run_command` (через `ExecuteInCommandContextAsync` +
    `Editor.CommandAsync`, ответ: текст командной строки/эхо, `CMDACTIVE` после выполнения) и `eval_lisp`
    (результат выражения обратно клиенту). Тогда команды редактора блоков выполняются прямо в рабочем сеансе
    за секунды, с UNDO для отката, без отдельных экземпляров AutoCAD. Учесть: включение флагом +
    `confirm: true` (раздел 6); выполнение внутри `-BEDIT` (контекст редактора блоков); захват текста
    запросов, чтобы видеть, где команда ждёт ввода; принудительный выход (`ESC`/`(command)`), если команда
    осталась активной.
  - *Дополнительно:* для типовых деталей лотков (отводы, тройники, переходы) держать библиотеку готовых
    динамических блоков в DWG-шаблоне и вставлять их через `insert_block` с `dynamic_properties` — это
    уже работает и быстрее генерации блока с нуля. Черновик `TrayElbow.lsp` (внутренний радиус постоянный,
    растягивается наружная сторона) можно довести до рабочего состояния после появления `run_command`.

- [x] *(исправлено в 0.3.4, 2026-11-19)* **Нельзя изменить динамические свойства уже вставленного блока** (найдено 2026-11-18, плагин 0.3.0,
  AutoCAD 2027; задача: лоток `CGI_Cable Trays L` → Distance1 = 3000 (длина), Distance2 = 600 (ширина)).
  - *Воспроизведение:* выделить вставку динамического блока и попросить сменить размер — `set_entity_properties`
    принимает только слой/цвет/тип линии и т. п., `dynamic_properties` есть только у `insert_block`.
  - *Обход:* новая вставка с теми же точкой/поворотом/слоем и нужными `dynamic_properties` + удаление старой.
    Минусы: меняется handle, теряются XData/гиперссылки/прочие отличия старой вставки, два шага в UNDO.
  - *Исправление:* инструмент `set_block_properties(handles, dynamic_properties, attributes)` (или параметр
    `dynamic_properties` в `set_entity_properties`) — переиспользовать `SetDynamic()` из `BlockHandlers.cs`;
    пакетно по фильтру с `confirm`, как у других изменяющих инструментов.

- [x] *(исправлено в 0.3.3, 2026-11-19)* **`insert_block` ставит вставке текущие тип и вес линии вместо «ПоСлою»** (найдено там же).
  - *Воспроизведение:* при `CELTYPE = Continuous`, `CELWEIGHT = -3` вставка получила linetype `Continuous`
    и lineweight `Default`, хотя передан `color: "ByLayer"`; пришлось отдельно звать `set_entity_properties`.
  - *Причина:* `SetDatabaseDefaults()` берёт CELTYPE/CELWEIGHT; у `insert_block` нет параметров `linetype` /
    `lineweight`.
  - *Исправление:* добавить в `insert_block` (и в остальные `create_*`) параметры `linetype` и `lineweight`
    (как у `set_entity_properties`); подумать, не ставить ли по умолчанию ByLayer.

- [ ] **Нет инструментов для стилей оформления и мультивыносок** (найдено 2026-11-18, плагин 0.3.0, AutoCAD 2027;
  задача: план 3-комнатной квартиры с мебелью и экспликацией, текстовый стиль со шрифтом ISOCPEUR высотой 250,
  применить его к размерам, выноскам и таблице экспликации).
  - *Что нельзя сделать через мост:* `list_styles` только читает стили. Нельзя создать/изменить текстовый стиль
    (шрифт, высота, ширина), размерный стиль (DIMTXSTY, DIMTXT, засечки, точность), стиль таблиц (текстовый стиль
    и высота по типам строк), стиль мультивыносок; нельзя создать саму мультивыноску (MLEADER) и сменить стиль
    у существующих объектов (`set_entity_properties` не принимает `style`). `set_system_variable` не помогает:
    `TEXTSTYLE`/`DIMTXSTY` принимают только уже существующий стиль. Пользователь просил не обходить это через LISP.
  - *Решение (на будущее):*
    1. `create_text_style` / `set_text_style(name, font (TTF или SHX), big_font, height, width_factor,
       oblique_deg, current)` — `TextStyleTableRecord`, для TTF `Font = new FontDescriptor(...)`.
    2. `create_dim_style` / `set_dim_style(name, based_on, text_style, text_height, arrow (блок/засечка),
       arrow_size, ext_offset, ext_extend, text_gap, decimals, units, scale, current)` — `DimStyleTableRecord`
       (Dimtxsty, Dimtxt, Dimblk, Dimasz, Dimexo, Dimexe, Dimgap, Dimdec, Dimscale…).
    3. `create_table_style(name, text_style, text_height по Title/Header/Data, выравнивание, поля)` —
       `TableStyle` в словаре `ACAD_TABLESTYLE`.
    4. `create_mleader_style(name, text_style, text_height, arrow, arrow_size, landing_gap, dogleg)` —
       `MLeaderStyle` в `ACAD_MLEADERSTYLE`.
    5. `create_mleader(points, text, style, layer)` — `MLeader` с MText-содержимым.
    6. Параметр `style` в `set_entity_properties` (TEXT/MTEXT → текстовый стиль, DIMENSION → размерный,
       ACAD_TABLE → табличный, MULTILEADER → стиль выносок), пакетно по фильтру с `confirm`; для таблиц
       дополнительно сбрасывать переопределения стиля/высоты текста в ячейках.
    7. Параметр `text_style`/`text_height` в `create_table` (сейчас только `style` таблицы).

- [x] *(исправлено в 0.3.5, 2026-11-19; `text_height` таблиц — шаг 6)* **`create_dimension` не учитывает текущие переопределения размерного стиля** (найдено 2026-11-18, плагин 0.3.0,
  AutoCAD 2027; тот же план квартиры).
  - *Воспроизведение:* `set_system_variable` DIMTXT=250, DIMASZ=150, DIMBLK=_ARCHTICK, DIMDEC=0, DIMTAD=1 →
    `create_dimension(...)`. Размер создаётся со стилем Standard и его исходными значениями (текст и засечки 0.18),
    переопределений у объекта нет. Команда РЗМЛИНЕЙНЫЙ в той же ситуации учла бы переопределения.
  - *Причина (предположительно):* размеру задаётся только `DimensionStyle = db.Dimstyle`; переопределения из
    системных переменных (`db.GetDimstyleData()`) к объекту не применяются.
  - *Исправление:* после создания вызывать `dim.SetDimstyleData(db.GetDimstyleData())` (так переносятся текущие
    переопределения), либо добавить в `create_dimension` параметры `text_height`, `arrow_size`, `text_style`
    как переопределения. Плюс тот же параметр `style` в `set_entity_properties` (см. запись про стили выше).
  - *Связанное:* `create_table` на стиле Standard в метрическом чертеже даёт текст ячеек высотой 0.18 —
    без `text_height` в `create_table`/`set_table_cells` таблицу нельзя сделать читаемой (см. п. 7 записи про стили).
  - *Временный приём (проверено 2026-11-18):* шрифт и высоту можно задать кодами MTEXT —
    `create_dimension(text_override="{\fISOCPEUR|b0|i0|c204|p34;\H250;<>}")` и такие же коды в значениях
    `set_table_cells`. Текст становится нужного размера, но засечки (DIMASZ 0.18) остаются невидимыми, а AutoCAD
    считает, что текст не помещается: у размеров 1350, 1000, 990, 300, 120 он выносится за выносную линию
    и налезает на соседние размеры. Порядок p1/p2 задаёт, в какую сторону выносится текст; размеры 120 (толщина
    перегородок) пришлось удалить. Нужны параметры `text_position` (или `text_move`/DIMTMOVE) и `fit`
    (DIMATFIT/DIMTIX) в `create_dimension`, а также изменение существующего размера (текст, положение текста,
    переопределения) без пересоздания.

- [ ] **`undo` зависает, если в сеансе AutoCAD был вызван COM `SendCommand`** (найдено 2026-11-19, плагин 0.3.1,
  есть и в 0.3.0; AutoCAD 2026 и 2027).
  - *Воспроизведение:* новый чертёж → `create_line` → внешний COM-клиент `doc.SendCommand("_.REGEN ")`
    (так делала тестовая заготовка: `AI_SELALL`/`AI_DESELECT`) → `undo` → `timeout` через 30 с. Дальше зависает
    каждый `undo`, пока не придёт другой запрос к мосту; запрос `ExecuteInCommandContextAsync` при этом стоит
    в очереди, хотя AutoCAD «спокоен» (IsQuiescent, CMDACTIVE = 0, документ не заблокирован). Выделение через
    `set_selection` к зависанию не приводит.
  - *Не помогло:* WM_NULL главному окну, пустой `BeginInvoke`, `SetFocusToDwgView`, `UpdateScreen`,
    `PostCommandPrompt`, транзакция чтения, освобождение COM в клиенте.
  - *Влияние:* у пользователя — только если COM `SendCommand` (или `vla-sendcommand` в LISP) вызывает другая
    программа/надстройка. Тесты обходят: выделение — через мост.
  - *Идеи:* выполнять UNDO не через command context (например, `SendStringToExecute` + ожидание `CommandEnded`),
    либо при задержке > 1–2 с повторять запрос; разобраться, что именно «отпускает» очередь при следующем запросе.

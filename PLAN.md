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
- [ ] Кнопка на ленте (перенесено: нужна `AdWindows`, для связи не обязательна)
- [x] `HttpListener` + диспетчер главного потока + таймауты + `busy`
- [x] `acad_mcp_server.py` с одним инструментом, подключение к Claude Code рядом с Navisworks MCP
- [x] **`health_check`** — версия плагина, версия AutoCAD, порт, число маршрутов
- [x] **`get_document_info`** — имя, путь, единицы (`INSUNITS`), границы, число объектов, текущий слой/лист, сохранён ли

### Этап 1 — чтение чертежа (безопасно, ничего не меняет)
- [ ] `list_documents` — открытые чертежи, активный
- [ ] `list_layers` — имя, цвет, тип линий, вес, вкл/заморожен/блокирован, печатается, число объектов
- [ ] `list_blocks` — определения блоков: имя, атрибуты, число вставок, динамический ли, внешняя ссылка ли
- [ ] `find_entities` — фильтр: тип (`LINE`, `INSERT`, `MTEXT`…), слой, имя блока, рамка, текст по маске; пространство (модель/лист)
- [ ] `get_entity_properties` — общие свойства + специфичные (длина, площадь, точки, текст, атрибуты, свойства динблока, XData)
- [ ] `get_selection` — текущий выбор пользователя (PickFirst)
- [ ] `list_layouts` — листы, видовые экраны, формат/устройство печати
- [ ] `list_xrefs` — внешние ссылки, путь, статус (загружена / не найдена)
- [ ] `list_styles` — текстовые, размерные, табличные стили, типы линий
- [ ] `get_system_variable` — чтение любых переменных
- [ ] `extract_block_attributes` — таблица атрибутов всех вставок блока(ов) → JSON/CSV (спецификации, однолинейные схемы)

### Этап 2 — «зрение»
- [ ] `capture_view` — PNG текущего вида в чат (`Document.CapturePreviewImage` или `GraphicsSystem.View.GetSnapshot` — проверить, что даёт нормальное разрешение)
- [ ] `zoom_extents`, `zoom_window`, `zoom_to_entities`
- [ ] `set_current_layout` — переключить модель/лист

### Этап 3 — создание и редактирование
- [ ] `create_line`, `create_polyline` (2D, с выпуклостями), `create_circle`, `create_arc`
- [ ] `create_text`, `create_mtext`, `create_table` (из массива строк)
- [ ] `insert_block` — с атрибутами и свойствами динблока, масштаб/поворот
- [ ] `create_dimension` — линейный / выровненный (позже остальные)
- [ ] `set_entity_properties` — слой, цвет, тип линий, вес, видимость
- [ ] `update_block_attributes` — массово по фильтру (handle или «блок + атрибут = значение»)
- [ ] `transform_entities` — move / copy / rotate / scale / mirror
- [ ] `delete_entities` — с подтверждением числа объектов
- [ ] `create_layer`, `set_layer_state` (вкл/выкл/заморозить/блокировать/цвет/текущий)
- [ ] `set_system_variable`
- [ ] `undo` — откатить последний вызов MCP

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

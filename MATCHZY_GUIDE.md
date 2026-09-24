# Как устроен MatchZy: разбор для Java-разработчика

Документ о том, как работает сам плагин MatchZy (версия 0.8.15 в этом форке): из чего он состоит, как проходит матч от загрузки до конца серии, какие события он ловит и какие отправляет наружу и в какой файл идти под конкретную задачу. Код на C#, пояснения — через аналогии с Java/Spring.

---

## 1. Что это и на чём работает

- **CS2-сервер** ничего не знает о плагинах на C#. Их запускает **CounterStrikeSharp (CSS)** — фреймворк, который ставится на сервер через Metamod и грузит `.dll` из `game/csgo/addons/counterstrikesharp/plugins/<Имя>/`.
- **MatchZy** — один такой плагин: одна `MatchZy.dll` плюс зависимости (Newtonsoft.Json, Dapper, SQLite/MySQL-драйверы). Всё это лежит в `dist/`.
- Плагин живёт внутри процесса сервера. Он подписывается на игровые события движка (раунд начался, игрок умер, матч закончился), шлёт серверу консольные команды (`mp_restartgame 1`, `changelevel de_mirage`) и сам регистрирует команды: консольные (`matchzy_loadmatch_url`) и чатовые (`.ready`).

Аналогия: CS2 — это «контейнер», CSS — Spring Boot, MatchZy — приложение с контроллерами (команды) и `@EventListener` (игровые события).

---

## 2. C# → Java: словарь

| C# в этом коде | Java/Spring | Где встречается |
|---|---|---|
| `MatchZy.csproj` | `build.gradle` | корень |
| NuGet-пакет `CounterStrikeSharp.API` | Maven-зависимость | `MatchZy.csproj` |
| `dotnet build -c Release` | `./gradlew build` | — |
| `class MatchZy : BasePlugin` | главный класс приложения | `MatchZy.cs` |
| `public override void Load(bool hotReload)` | `@PostConstruct`: всё настраивается здесь | `MatchZy.cs` |
| **`partial class MatchZy`** | **один класс, разрезанный на ~25 файлов.** Поля из `MatchZy.cs` видны в `Utility.cs` и наоборот. Для Java непривычно, но это ключ к навигации: «где объявлено поле» — почти всегда `MatchZy.cs`, `MatchConfig.cs` или верх файла по теме | все `*.cs` в корне |
| `[ConsoleCommand("css_ready", "...")]` | `@RequestMapping`, только вместо URL — консольная команда | `ConsoleCommands.cs`, `ConfigConvars.cs`, ... |
| `RegisterEventHandler<EventRoundEnd>(handler, HookMode.Pre)` | `@EventListener`. `Pre` — до того, как движок применит событие (можно изменить), `Post` — после | `MatchZy.cs` → `Load` |
| `HookResult.Continue / Changed / Stop` | «пропустить дальше / я изменил событие / отменить» | обработчики событий |
| `FakeConVar<bool> x = new("matchzy_...", "...", default)` | `@Value("${matchzy...:default}")`, но меняется в рантайме командой в консоли | `ConfigConvars.cs` |
| `AddTimer(5.0f, () => ..., TimerFlags.REPEAT)` | `@Scheduled` / `ScheduledExecutorService`, но на игровом потоке | везде |
| `Task.Run(async () => ...)` | `CompletableFuture.runAsync` | отправка событий, БД |
| `JObject`, `json["team1"]` (Newtonsoft) | Jackson `JsonNode`, `node.get("team1")` | `MatchManagement.cs` |
| `[JsonPropertyName("matchid")]` | `@JsonProperty("matchid")` | `Events.cs`, `MatchData.cs` |
| `required string X { get; init; }` | поле record | `Events.cs` |
| `string?` | `@Nullable String` | везде |
| `Dictionary<K,V>`, `List<T>`, `HashSet<T>` | `HashMap`, `ArrayList`, `HashSet` | везде |
| `Localizer["matchzy.ready.markedready"]` | `MessageSource.getMessage(...)`: тексты в `lang/*.json` | везде |
| `Log("...")` | `log.info(...)`. Пишет в консоль сервера с префиксом плагина | `Utility.cs` |

### Правило главного потока

В Spring можно звать что угодно из любого потока. Здесь нельзя: **всё, что трогает игру** (`Server.*`, игроки, `ConVar`, `Server.GameDirectory`), вызывается только с главного потока сервера. Из `Task.Run` или после `await` сервер падает с `Native was invoked on a non-main thread`.

Как это решено в коде:
- обработчики событий, таймеры (`AddTimer`) и команды уже выполняются на главном потоке;
- в фон через `Task.Run` уходят только HTTP (`SendEventAsync`, загрузка демок и бэкапов) и запросы к БД;
- вернуться из фона на главный поток: `Server.NextFrame(() => ...)` или `Server.NextWorldUpdate(...)`. Обёртка `SyncContextScope` (`SynchronizationContextManagement.cs`) делает так, что код после `await` сам продолжается на главном потоке.

---

## 3. Состояние: флаги вместо state machine

Явной state machine нет. Фаза матча — это набор `bool`-полей в `MatchZy.cs`, которые методы переключают вручную. Чтобы понять, что сейчас происходит, смотрят на эти флаги:

| Флаг | Значит |
|---|---|
| `isMatchSetup` | загружен конфиг матча (`matchzy_loadmatch*`). Без него плагин работает в режиме «паблика/пуга» |
| `readyAvailable` | принимаются `.ready`: идёт разминка перед стартом |
| `isWarmup` | разминка |
| `matchStarted` | матч начался: нож или лайв |
| `isKnifeRound` | идёт ножевой раунд |
| `isSideSelectionPhase` | нож закончен, победитель выбирает сторону (`.stay` / `.switch`) |
| `isMatchLive` | идут боевые раунды |
| `isPaused` | пауза |
| `isPractice`, `isSleep`, `isVeto`, `isPreVeto`, `isDryRun` | тренировка, режим сна, вето на сервере, «сухой прогон» |
| `liveMatchId` | id матча (в форке — строка, у оригинала — число) |

Где ставятся: `StartWarmup`, `StartKnifeRound`, `StartAfterKnifeWarmup`, `SetLiveFlags`, `ResetMatch` — всё в `Utility.cs`.

Данные матча лежат в `matchConfig` (`MatchConfig.cs`: карты, `CurrentMapNumber`, `NumMaps`, стороны, `PlayersPerTeam`, `RemoteLogURL`, ...) и в двух командах `matchzyTeam1` / `matchzyTeam2` (`Teams.cs`: имя, id, игроки, счёт серии, тренеры). Кто сейчас за CT, а кто за T, — в словарях `teamSides` / `reverseTeamSides`: после смены сторон они меняются местами.

---

## 4. Жизненный цикл матча

```
Load()                                          MatchZy.cs
  exec MatchZy/config.cfg, регистрация команд и обработчиков
  AutoStart()  ── matchzy_autostart_mode: 0 сон / 1 разминка / 2 тренировка   Utility.cs

matchzy_loadmatch_url "<url>"  или  matchzy_loadmatch <файл>   MatchManagement.cs
  LoadMatchFromURL → GET конфига → LoadMatchFromJSON
    проверка JSON (ValidateMatchJsonStructure)
    команды, игроки, карты, стороны, cvars матча
    changelevel на первую карту (если другая)
    isMatchSetup = true, readyAvailable = true, StartWarmup()
    ──► событие series_start

Разминка: игроки заходят                        EventHandlers.cs
  EventPlayerConnectFull: чужих (нет в конфиге) кикает, своих ставит в нужную команду
  .ready / !ready → OnPlayerReady → CheckLiveRequired     ConsoleCommands.cs / Utility.cs
    все готовы (IsTeamsReady: players_per_team / min_players_to_ready) → HandleMatchStart

HandleMatchStart                                Utility.cs
  имена команд, запись матча в БД, файл бэкапов
  ├─ вето на сервере (isPreVeto)  → CreateVeto          MapVeto.cs
  ├─ нож (сторона "knife")       → StartKnifeRound
  └─ стороны заданы              → StartLive

Нож                                             MatchZy.cs (EventRoundEnd, Pre)
  конец ножевого раунда → DetermineKnifeWinner → StartAfterKnifeWarmup
  победитель пишет .stay / .switch → StartLive          ConsoleCommands.cs

StartLive                                       Utility.cs
  exec live.cfg, cvars матча, запись демки
  ──► событие going_live

Каждый раунд
  EventRoundStart → HandlePostRoundStartEvent: бэкап раунда, сброс урона, тренеры
  EventRoundEnd (Post) → HandlePostRoundEndEvent:
    счёт в чат, отчёт об уроне, статистика в БД
    ──► событие round_end
    на половине / в овертайме → SwapSidesInTeamData (CT↔T в teamSides)

Конец карты                                     EventHandlers.cs → Utility.cs
  EventCsWinPanelMatch → HandleMatchEnd
    остановка демки, статистика игроков в БД и CSV
    ──► событие map_result
    серия решена?  (clinch_series / все карты сыграны)
      да  → EndSeries
      нет → CurrentMapNumber++, через mp_match_restart_delay: changelevel → разминка → снова .ready

EndSeries                                       MatchManagement.cs
  итог в БД, cvars матча откатываются (matchzy_reset_cvars_on_series_end)
  ──► событие series_end
  ResetMatch → плагин снова «пустой»
```

---

## 5. Какие события движка он ловит

Все подписки — в `Load()` (`MatchZy.cs`), обработчики — в `EventHandlers.cs` или прямо в `Load()` лямбдами.

| Событие движка | Что делает MatchZy |
|---|---|
| `EventPlayerConnectFull` | вайтлист, кик чужих при загруженном матче, статус готовности, первый игрок запускает разминку |
| `EventPlayerDisconnect` | чистит кэш игроков и готовность |
| `EventPlayerTeam` (Pre) | держит игрока в его команде из конфига, скрывает смену команды тренером |
| команда `jointeam` | не даёт игроку сменить команду во время матча |
| `EventPlayerChat` | **роутер чат-команд** (см. раздел 6) |
| `EventRoundStart` | бэкап раунда, урон, тренеры, hostname |
| `EventRoundFreezeEnd` | переставляет тренеров |
| `EventRoundEnd` (Pre) | исход ножевого раунда |
| `EventRoundEnd` (Post) | боевой раунд: событие `round_end`, статистика, смена сторон |
| `EventCsWinPanelMatch` | конец карты → `HandleMatchEnd` |
| `EventPlayerHurt` | урон для отчёта после раунда (`.damage`-сводка) |
| `EventPlayerDeath` | в разминке возвращает деньги до 16000; скрывает самоубийство тренера |
| `OnMapStart` | после смены карты: разминка или автостарт |
| `OnEntitySpawned`, `Event*Detonate` | цвет смоков, гранаты для тренировки |

---

## 6. Команды

Два механизма, и это частый источник путаницы:

1. **Консольные команды CSS** — методы с `[ConsoleCommand("css_ready")]`. CSS сам делает их доступными и из консоли, и из чата: `!ready` и `/ready` в чате вызывают `css_ready`.
2. **Чат-команды с точкой** (`.ready`, `.pause`) — CSS про них не знает. Их ловит обработчик `EventPlayerChat` в `Load()`: словарь `commandActions` («текст → метод») плюс цепочка `if (message.StartsWith(".map"))` для команд с аргументами. Обычно оба пути ведут в один метод (`OnPlayerReady`).

Где что лежит:

| Что | Файл |
|---|---|
| Игроки: `.ready`, `.unready`, `.stay`, `.switch`, `.pause`, `.unpause`, `.tech`, `.tac`, `.stop` | `ConsoleCommands.cs`, `Pausing.cs`, `BackupManagement.cs` |
| Админ в чате: `.start`, `.restart`, `.endmatch`, `.map`, `.forcepause`, `.restore`, `.asay`, `.rcon` | `ConsoleCommands.cs`, `Utility.cs` |
| Загрузка матча: `matchzy_loadmatch`, `matchzy_loadmatch_url` | `MatchManagement.cs` |
| Команды в матче: `matchzy_addplayer`, `matchzy_removeplayer`, `.coach` | `Teams.cs`, `Coach.cs` |
| Вето на сервере: `.ban`, `.pick` | `MapVeto.cs` |
| Настройки `matchzy_*` (см. раздел 7) | `ConfigConvars.cs`, `RemoteLogConfig.cs`, `DemoManagement.cs` |
| Права админа | `IsPlayerAdmin` в `Utility.cs`: флаги CSS (`@css/...`) или `cfg/MatchZy/admins.json` |

---

## 7. Настройки и конфиги

| Что | Где | Когда применяется |
|---|---|---|
| Настройки плагина `matchzy_*` (готовность, нож, паузы, вайтлист, префиксы, hostname, ...) | `cfg/MatchZy/config.cfg`; обработчики — `ConfigConvars.cs` | один раз в `Load()` (`exec MatchZy/config.cfg`) |
| cvars игры на разминке | `cfg/MatchZy/warmup.cfg` | `StartWarmup` |
| cvars ножа | `cfg/MatchZy/knife.cfg` | `StartKnifeRound` |
| cvars боевого матча | `cfg/MatchZy/live.cfg` (+ `live_override.cfg`, wingman-варианты) | `StartLive` |
| cvars из конфига матча (`"cvars": {...}`) | JSON матча | после live.cfg (`ExecuteChangedConvars`), откат в `EndSeries` |
| Админы | `cfg/MatchZy/admins.json` | `Load()` |
| БД статистики | `cfg/MatchZy/database.json`: SQLite по умолчанию (`matchzy.db` рядом с плагином) или MySQL | `Load()`, `DatabaseStats.cs` |
| Вайтлист | `cfg/MatchZy/whitelist.cfg` | при подключении |

Если `warmup.cfg` / `knife.cfg` / `live.cfg` нет на сервере, плагин выполняет зашитую строку cvars по умолчанию (`ExecWarmupCfg`, `StartKnifeRound`, `ExecLiveCFG` в `Utility.cs`). «Почему на сервере не такие настройки» — сначала проверить, есть ли файл.

---

## 8. Конфиг матча (JSON)

Разбирается в `LoadMatchFromJSON` и `GetOptionalMatchValues` (`MatchManagement.cs`).

| Поле | Обязательное | Что |
|---|---|---|
| `team1`, `team2` | да | `{ "id", "name", "players": { "<steamid64>": "<ник>" } }`. Кого нет в `players`, при загруженном матче кикает |
| `maplist` | да | карты: пул для вето или сразу список на серию |
| `num_maps` | да | сколько карт в серии (BO1/3/5) |
| `matchId` | нет | id матча (в форке — строка), уходит во все события как `matchid` |
| `map_sides` | нет | на каждую карту: `knife`, `team1_ct`, `team1_t`, `team2_ct`, `team2_t` |
| `skip_veto` | нет | не проводить вето на сервере. Если карт в `maplist` ровно `num_maps`, вето пропускается само |
| `veto_mode` | нет | порядок банов/пиков для вето на сервере |
| `clinch_series` | нет | заканчивать серию, когда исход решён (2:0 в BO3) |
| `players_per_team` | нет | игроков в команде, от него считается готовность |
| `min_players_to_ready`, `min_spectators_to_ready` | нет | сколько `.ready` нужно для старта |
| `spectators` | нет | зрители/кастеры, которых пускает |
| `wingman` | нет | режим 2×2 |
| `cvars` | нет | любые cvars на матч |
| `remoteLogUrl` / `remote_log_url` (+ `...HeaderKey/Value`) | нет | куда слать события |

---

## 9. Какие события он отправляет наружу

Все — HTTP POST JSON на `remoteLogUrl` из `SendEventAsync` (`PublishEvents.cs`), в фоне (`Task.Run`). Классы событий — `Events.cs`, вложенные DTO — `MatchData.cs`. У каждого события есть поля `event` (тип) и `matchid`.

| `event` | Когда | Где создаётся | Главное в теле |
|---|---|---|---|
| `series_start` | конфиг матча загружен | `LoadMatchFromJSON` | команды, `num_maps` |
| `going_live` | начались боевые раунды карты | `StartLive` | `map_number` |
| `round_end` | конец каждого боевого раунда | `HandlePostRoundEndEvent` | счёт, причина, победитель раунда, статистика игроков |
| `map_result` | конец карты | `HandleMatchEnd` | победитель карты, счёт, статистика команд и игроков |
| `series_end` | конец серии | `EndSeries` | победитель, счёт серии |
| `map_picked`, `map_vetoed`, `side_picked` | вето на сервере | `MapVeto.cs` | карта, команда |

`player_disconnect` и `demo_upload_ended` описаны в `Events.cs`, но в этой версии нигде не создаются.

Если бэк не ответил 2xx, событие просто пишется в лог: повторной отправки и очереди нет.

---

## 10. Остальные подсистемы коротко

| Подсистема | Файл | Суть |
|---|---|---|
| Паузы | `Pausing.cs`, `Utility.cs` (`PauseMatch`, `UnpauseMatch`) | `.pause` — тактическая или техническая (`matchzy_use_pause_command_for_tactical_pause`); снятие — когда `.unpause` сказали обе команды (`unpauseData`); лимиты тех. пауз в `config.cfg` |
| Бэкапы раундов | `BackupManagement.cs` | в начале каждого раунда `CreateMatchZyRoundDataBackup`; `.stop` — обе команды просят откатить раунд; `.restore <n>` и `matchzy_loadbackup` — админом; можно выгружать по URL (`matchzy_remote_backup_url`) |
| Демки | `DemoManagement.cs` | запись с `StartLive`, остановка в `HandleMatchEnd`, выгрузка на `matchzy_demo_upload_url` |
| Статистика | `DatabaseStats.cs` | SQLite/MySQL через Dapper: матчи, карты, игроки; CSV в `csgo/MatchZy_Stats/<matchid>` |
| Тренеры | `Coach.cs` | тренер в команде без слота игрока, переставляется каждый раунд |
| Вето на сервере | `MapVeto.cs` | `.ban` / `.pick` в чате, если карт больше, чем `num_maps`, и `skip_veto` не задан |
| Тренировка | `PracticeMode.cs` (самый большой файл) | `.prac`: гранаты, спавны, боты — к матчам отношения не имеет |
| Сон | `SleepMode.cs` | `matchzy_autostart_mode 0`: плагин ничего не делает, пока не позовут |
| Совместимость с Get5 | `G5API.cs` | статус в формате Get5 (`get5_status`) для внешних панелей |

---

## 11. Где менять: рецепты

**Изменить, когда матч стартует.** `CheckLiveRequired` и `IsTeamsReady` / `IsTeamReady` (`ReadySystem.cs`, `Utility.cs`). Числа — из `players_per_team`, `min_players_to_ready` конфига или `matchzy_minimum_ready_required`.

**Добавить чат-команду.** Метод с `[ConsoleCommand("css_xxx", "...")]` (для `!xxx`) и строка `{ ".xxx", OnXxx }` в словаре `commandActions` в `Load()` (для `.xxx`). Если нужен аргумент — ветка `StartsWith` в обработчике `EventPlayerChat`.

**Добавить настройку `matchzy_*`.** Поле `FakeConVar<T>` или метод с `[ConsoleCommand]` в `ConfigConvars.cs` по образцу соседних, строку со значением по умолчанию — в `cfg/MatchZy/config.cfg`.

**Добавить поле в конфиг матча.** Чтение в `GetOptionalMatchValues` (`MatchManagement.cs`) по образцу: `if (json["поле"] != null) matchConfig.X = json["поле"]!.Value<int>();`, поле — в `MatchConfig.cs`. Если обязательное — ещё в `ValidateMatchJsonStructure`.

**Добавить поле в событие** (например, в `map_result`):
1. свойство с `[JsonPropertyName("...")]` в классе события (`Events.cs`) или в DTO (`MatchData.cs`);
2. заполнить там, где событие создаётся (`new MapResultEvent { ... }` в `HandleMatchEnd`, `Utility.cs`).

**Новое событие наружу.** Класс в `Events.cs` (наследник `MatchZyMatchEvent` / `MatchZyMapEvent`, в конструкторе `base("имя")`), создать в нужном месте и отправить: `Task.Run(async () => await SendEventAsync(ev));`. Все данные из игры собрать **до** `Task.Run` — правило главного потока.

**Поменять игровые настройки фаз** (время раунда, деньги, овертайм) — это не код, а `warmup.cfg` / `knife.cfg` / `live.cfg` на сервере или `cvars` в конфиге матча.

**Поменять тексты в чате.** `lang/ru.json` / `lang/en.json` (ключи вида `matchzy.ready.markedready`); часть сообщений зашита строками прямо в коде (`PrintToAllChat($"...")`).

---

## 12. Сборка, выкладка, отладка

- **Сборка:** .NET SDK 8, `dotnet build -c Release`. Результат — `bin/Release/net8.0/`: оттуда `MatchZy.dll` (и новые зависимости, если добавлялись) в `dist/`. Версия `CounterStrikeSharp.API` в `MatchZy.csproj` не должна быть новее, чем CSS на сервере.
- **Выкладка:** `dist/*` → `game/csgo/addons/counterstrikesharp/plugins/MatchZy/`, `cfg/MatchZy/` → `game/csgo/cfg/MatchZy/`. Затем перезапуск сервера или перезагрузка плагина через `css_plugins`. `config.cfg` и файлы, читаемые в `Load()`, подхватываются только при загрузке плагина.
- **Логи:** всё, что пишет `Log(...)`, — в консоли сервера (и в логах панели) с префиксом плагина. Удобно искать по меткам в квадратных скобках: `[LoadMatchFromJSON]`, `[SendEventAsync]`, `[HandleMatchEnd]`, `[FULL CONNECT]`.
- **Проверить текущее состояние:** в консоли `get5_status` (`G5API.cs`) или `css_plugins list`.

---

## 13. На что обратить внимание в этом форке

- `LoadMatchDataCommand` (`MatchManagement.cs`) пишет в лог значение заголовка авторизации открытым текстом — лучше маскировать.
- `SendEventAsync` (`PublishEvents.cs`) при пустом `remoteLogUrl` пишет «Event was skipped», но не выходит из метода: дальше `PostAsync(null)` падает, исключение ловится. Не ломает, но засоряет лог — нужен `return`.
- В репозитории файлы IDE: `.idea/.idea.MatchZy.dir/.idea/workspace.xml`, `Folder.DotSettings.user` — их стоит добавить в `.gitignore`.

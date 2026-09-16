using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Events;

namespace MatchZy
{
    [MinimumApiVersion(227)]
    public partial class MatchZy : BasePlugin
    {
        public override string ModuleName => "DeloPlay";
        public override string ModuleVersion => "0.8.15-deloplay-custom";
        public override string ModuleAuthor => "";
        public override string ModuleDescription => "";

        public string chatPrefix = $"[{ChatColors.Blue}DeloPlay{ChatColors.Default}]";
        public string adminChatPrefix = $"[{ChatColors.Red}ADMIN{ChatColors.Default}]";

        // Plugin phase states
        public bool isPractice = false;
        public bool isSleep = false;
        public bool readyAvailable = false;
        public bool matchStarted = false;
        public bool isWarmup = false;
        public bool isKnifeRound = false;
        public bool isSideSelectionPhase = false;
        public bool isMatchLive = false;
        public string liveMatchId = "-1";
        public int autoStartMode = 1;
        public bool mapReloadRequired = false;

        // Pause state
        public bool isPaused = false;
        public Dictionary<string, object> unpauseData = new()
        {
            { "ct", false },
            { "t", false },
            { "pauseTeam", "" }
        };
        bool isPauseCommandForTactical = false;

        // Knife phase
        public int knifeWinner = 0;
        public string knifeWinnerName = "";

        // Connected players cache
        public int connectedPlayers = 0;
        private Dictionary<int, bool> playerReadyStatus = new();
        private Dictionary<int, CCSPlayerController> playerData = new();
        private Dictionary<string, string> loadedAdmins = new();

        // Timers
        public CounterStrikeSharp.API.Modules.Timers.Timer? unreadyPlayerMessageTimer = null;
        public CounterStrikeSharp.API.Modules.Timers.Timer? sideSelectionMessageTimer = null;
        public CounterStrikeSharp.API.Modules.Timers.Timer? pausedStateTimer = null;
        public int chatTimerDelay = 13;

        // Configuration defaults
        public bool isKnifeRequired = true;
        public int minimumReadyRequired = 2;
        public bool isWhitelistRequired = false;
        public bool isSaveNadesAsGlobalEnabled = false;
        public bool isPlayOutEnabled = false;
        public bool playerHasTakenDamage = false;

        // Commands registry
        public Dictionary<string, Action<CCSPlayerController?, CommandInfo?>>? commandActions;
        private Database database = new();

        public override void Load(bool hotReload)
        {
            LoadAdmins();
            database.InitializeDatabase(ModuleDirectory);

            Server.ExecuteCommand("execifexists MatchZy/config.cfg");

            teamSides[matchzyTeam1] = "CT";
            teamSides[matchzyTeam2] = "TERRORIST";
            reverseTeamSides["CT"] = matchzyTeam1;
            reverseTeamSides["TERRORIST"] = matchzyTeam2;

            if (!hotReload)
            {
                AutoStart();
            }
            else
            {
                UpdatePlayersMap();
                AutoStart();
            }

            commandActions = new Dictionary<string, Action<CCSPlayerController?, CommandInfo?>>
            {
                { ".ready", OnPlayerReady },
                { ".r", OnPlayerReady },
                { ".forceready", OnForceReadyCommandCommand },
                { ".unready", OnPlayerUnReady },
                { ".notready", OnPlayerUnReady },
                { ".ur", OnPlayerUnReady },
                { ".stay", OnTeamStay },
                { ".switch", OnTeamSwitch },
                { ".swap", OnTeamSwitch },
                { ".tech", OnTechCommand },
                { ".p", OnPauseCommand },
                { ".pause", OnPauseCommand },
                { ".unpause", OnUnpauseCommand },
                { ".up", OnUnpauseCommand },
                { ".forcepause", OnForcePauseCommand },
                { ".fp", OnForcePauseCommand },
                { ".forceunpause", OnForceUnpauseCommand },
                { ".fup", OnForceUnpauseCommand },
                { ".tac", OnTacCommand },
                { ".roundknife", OnKnifeCommand },
                { ".rk", OnKnifeCommand },
                { ".playout", OnPlayoutCommand },
                { ".start", OnStartCommand },
                { ".force", OnStartCommand },
                { ".forcestart", OnStartCommand },
                { ".skipveto", OnSkipVetoCommand },
                { ".sv", OnSkipVetoCommand },
                { ".restart", OnRestartMatchCommand },
                { ".rr", OnRestartMatchCommand },
                { ".endmatch", OnEndMatchCommand },
                { ".forceend", OnEndMatchCommand },
                { ".reloadmap", OnMapReloadCommand },
                { ".settings", OnMatchSettingsCommand },
                { ".whitelist", OnWLCommand },
                { ".reload_admins", OnReloadAdmins },
                { ".match", OnMatchCommand },
                { ".uncoach", OnUnCoachCommand },
                { ".stop", OnStopCommand },
                { ".help", OnHelpCommand },
                { ".t", OnTCommand },
                { ".ct", OnCTCommand },
                { ".spec", OnSpecCommand }
            };

            // Player & Session lifecycle hooks
            RegisterEventHandler<EventPlayerConnectFull>(EventPlayerConnectFullHandler);
            RegisterEventHandler<EventPlayerDisconnect>(EventPlayerDisconnectHandler);
            RegisterEventHandler<EventCsWinPanelRound>(EventCsWinPanelRoundHandler, hookMode: HookMode.Pre);
            RegisterEventHandler<EventCsWinPanelMatch>(EventCsWinPanelMatchHandler);
            RegisterEventHandler<EventRoundStart>(EventRoundStartHandler);
            RegisterEventHandler<EventRoundFreezeEnd>(EventRoundFreezeEndHandler);
            RegisterEventHandler<EventPlayerDeath>(EventPlayerDeathPreHandler, hookMode: HookMode.Pre);
            RegisterListener<Listeners.OnEntitySpawned>(OnEntitySpawnedHandler);

            // Coach mute on team change
            RegisterEventHandler<EventPlayerTeam>((@event, info) =>
            {
                CCSPlayerController? player = @event.Userid;
                if (!IsPlayerValid(player)) return HookResult.Continue;

                if (matchzyTeam1.coach.Contains(player!) || matchzyTeam2.coach.Contains(player!))
                {
                    @event.Silent = true;
                    return HookResult.Changed;
                }
                return HookResult.Continue;
            }, HookMode.Pre);

            // Team enforcement during match/veto
            RegisterEventHandler<EventPlayerTeam>((@event, info) =>
            {
                if (!isMatchSetup && !isVeto) return HookResult.Continue;

                CCSPlayerController? player = @event.Userid;
                if (!IsPlayerValid(player)) return HookResult.Continue;
                if (player!.IsHLTV || player.IsBot) return HookResult.Continue;

                CsTeam playerTeam = GetPlayerTeam(player);
                SwitchPlayerTeam(player, playerTeam);

                return HookResult.Continue;
            });

            AddCommandListener("jointeam", (player, info) =>
            {
                if ((isMatchSetup || isVeto) && player != null && player.IsValid)
                {
                    if (int.TryParse(info.ArgByIndex(1), out int joiningTeam))
                    {
                        int playerTeam = (int)GetPlayerTeam(player);
                        if (joiningTeam != playerTeam)
                        {
                            return HookResult.Stop;
                        }
                    }
                }
                return HookResult.Continue;
            });

            AddCommandListener("noclip", OnConsoleNoClip);

            // Knife round resolution
            RegisterEventHandler<EventRoundEnd>((@event, info) =>
            {
                if (!isKnifeRound) return HookResult.Continue;

                DetermineKnifeWinner();
                @event.Winner = knifeWinner;
                int finalEvent = 10;
                if (knifeWinner == 3) finalEvent = 8;
                else if (knifeWinner == 2) finalEvent = 9;

                @event.Reason = finalEvent;
                isSideSelectionPhase = true;
                isKnifeRound = false;
                StartAfterKnifeWarmup();

                return HookResult.Changed;
            }, HookMode.Pre);

            // Competitive live round resolution
            RegisterEventHandler<EventRoundEnd>((@event, info) =>
            {
                try
                {
                    if (!isMatchLive) return HookResult.Continue;
                    HandlePostRoundEndEvent(@event);
                    return HookResult.Continue;
                }
                catch (Exception e)
                {
                    Log($"[EventRoundEnd FATAL] An error occurred: {e.Message}");
                    return HookResult.Continue;
                }
            }, HookMode.Post);

            RegisterListener<Listeners.OnMapStart>(mapName =>
            {
                AddTimer(1.0f, () =>
                {
                    if (!isMatchSetup)
                    {
                        AutoStart();
                        return;
                    }
                    if (isWarmup) StartWarmup();
                });
            });

            // Warmup money reset
            RegisterEventHandler<EventPlayerDeath>((@event, info) =>
            {
                var player = @event.Userid;
                if (!isWarmup) return HookResult.Continue;
                if (!IsPlayerValid(player)) return HookResult.Continue;
                if (player!.InGameMoneyServices != null) player.InGameMoneyServices.Account = 16000;
                return HookResult.Continue;
            });

            // Damage stats tracking
            RegisterEventHandler<EventPlayerHurt>((@event, info) =>
            {
                CCSPlayerController? attacker = @event.Attacker;
                CCSPlayerController? victim = @event.Userid;

                if (!IsPlayerValid(attacker) || !IsPlayerValid(victim)) return HookResult.Continue;
                if (!attacker!.IsValid || attacker.IsBot && !(@event.DmgHealth > 0 || @event.DmgArmor > 0)) return HookResult.Continue;

                if (matchStarted && victim!.TeamNum != attacker.TeamNum)
                {
                    int targetId = (int)victim.UserId!;
                    UpdatePlayerDamageInfo(@event, targetId);
                    if (attacker != victim) playerHasTakenDamage = true;
                }

                return HookResult.Continue;
            });

            // Chat command router
            RegisterEventHandler<EventPlayerChat>((@event, info) =>
            {
                int index = @event.Userid + 1;
                var playerUserId = NativeAPI.GetUseridFromIndex(index);

                var originalMessage = @event.Text.Trim();
                var message = originalMessage.ToLower();

                var parts = originalMessage.Split(' ');
                var messageCommandArg = parts.Length > 1 ? string.Join(' ', parts.Skip(1)) : string.Empty;

                if (!playerData.TryGetValue(playerUserId, out CCSPlayerController? player) || player == null)
                {
                    UpdatePlayersMap();
                    player = playerData.GetValueOrDefault(playerUserId);
                }

                if (commandActions.TryGetValue(message, out var action))
                {
                    action(player, null);
                }

                if (message.StartsWith(".map")) HandleMapChangeCommand(player, messageCommandArg);
                if (message.StartsWith(".readyrequired")) HandleReadyRequiredCommand(player, messageCommandArg);
                if (message.StartsWith(".restore")) HandleRestoreCommand(player, messageCommandArg);
                if (message.StartsWith(".team1")) HandleTeamNameChangeCommand(player, messageCommandArg, 1);
                if (message.StartsWith(".team2")) HandleTeamNameChangeCommand(player, messageCommandArg, 2);
                if (message.StartsWith(".coach")) HandleCoachCommand(player, messageCommandArg);
                if (message.StartsWith(".ban")) HandeMapBanCommand(player, messageCommandArg);
                if (message.StartsWith(".pick")) HandeMapPickCommand(player, messageCommandArg);

                if (message.StartsWith(".asay"))
                {
                    if (IsPlayerAdmin(player, "css_asay", "@css/chat"))
                    {
                        if (!string.IsNullOrWhiteSpace(messageCommandArg))
                            Server.PrintToChatAll($"{adminChatPrefix} {messageCommandArg}");
                        else
                            ReplyToUserCommand(player, Localizer["matchzy.cc.usage", ".asay <message>"]);
                    }
                    else
                    {
                        SendPlayerNotAdminMessage(player);
                    }
                }

                if (message.StartsWith(".rcon"))
                {
                    if (IsPlayerAdmin(player, "css_rcon", "@css/rcon"))
                    {
                        Server.ExecuteCommand(messageCommandArg);
                        ReplyToUserCommand(player, "Command sent successfully!");
                    }
                    else
                    {
                        SendPlayerNotAdminMessage(player);
                    }
                }

                return HookResult.Continue;
            });

            //Console.WriteLine($"[{ModuleName} {ModuleVersion} LOADED] MatchZy custom fork initialized successfully.");
        }
    }
}
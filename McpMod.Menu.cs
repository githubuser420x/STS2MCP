using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Daily;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Modifiers;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Connection;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Audio;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.CustomRun;
using MegaCrit.Sts2.Core.Nodes.Screens.DailyRun;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Platform.Steam;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;

namespace STS2_MCP;

public static partial class McpMod
{
    // -------------------------------------------------------------------------
    // GET /api/v1/menu
    // -------------------------------------------------------------------------

    private static Dictionary<string, object?> BuildMenuState()
    {
        // If a run is in progress, the caller should use /singleplayer or /multiplayer
        if (RunManager.Instance.IsInProgress)
        {
            bool isMp = IsMultiplayerRun();
            string mode = isMp ? "multiplayer" : "singleplayer";
            return new Dictionary<string, object?>
            {
                ["error"] = $"A {mode} run is in progress. Use /api/v1/{mode} instead."
            };
        }

        var root = ((SceneTree)Engine.GetMainLoop()).Root;

        // Check if we're in a lobby (character select / custom run / daily run screen)
        var lobby = FindActiveLobby(root);
        if (lobby != null)
            return BuildLobbyState(lobby, root);

        // Check if we're on the main menu
        var mainMenu = FindFirst<NMainMenu>(root);
        if (mainMenu == null)
            return new Dictionary<string, object?>
            {
                ["state"] = "unknown",
                ["message"] = "Not on the main menu and no run in progress."
            };

        return BuildMainMenuState();
    }

    private static Dictionary<string, object?> BuildMainMenuState()
    {
        var progress = SaveManager.Instance.Progress;
        var unlockState = SaveManager.Instance.GenerateUnlockStateFromProgress();

        // Characters
        var characters = new List<Dictionary<string, object?>>();
        foreach (var character in ModelDb.AllCharacters)
        {
            var stats = progress.GetOrCreateCharacterStats(character.Id);
            bool unlocked = unlockState.Characters.Contains(character);
            string? name = SafeGetText(() =>
                new MegaCrit.Sts2.Core.Localization.LocString("characters", character.CharacterSelectTitle));
            characters.Add(new Dictionary<string, object?>
            {
                ["id"] = character.Id.Entry,
                ["name"] = name ?? character.Id.Entry,
                ["unlocked"] = unlocked,
                ["max_ascension"] = stats.MaxAscension
            });
        }

        // Game types
        var gameTypes = new List<string> { "standard" };
        if (SaveManager.Instance.IsEpochRevealed<MegaCrit.Sts2.Core.Timeline.Epochs.DailyRunEpoch>())
            gameTypes.Add("daily");
        if (SaveManager.Instance.IsEpochRevealed<MegaCrit.Sts2.Core.Timeline.Epochs.CustomAndSeedsEpoch>())
            gameTypes.Add("custom");

        // Custom modifiers
        var goodMods = BuildModifierList(ModelDb.GoodModifiers);
        var badMods = BuildModifierList(ModelDb.BadModifiers);

        // Multiplayer game types
        var mpGameTypes = new List<string>();
        mpGameTypes.Add("host/standard");
        if (SaveManager.Instance.IsEpochRevealed<MegaCrit.Sts2.Core.Timeline.Epochs.DailyRunEpoch>())
            mpGameTypes.Add("host/daily");
        if (SaveManager.Instance.IsEpochRevealed<MegaCrit.Sts2.Core.Timeline.Epochs.CustomAndSeedsEpoch>())
            mpGameTypes.Add("host/custom");
        mpGameTypes.Add("join");

        return new Dictionary<string, object?>
        {
            ["state"] = "main_menu",
            ["singleplayer"] = new Dictionary<string, object?>
            {
                ["has_existing_save"] = SaveManager.Instance.HasRunSave,
                ["characters"] = characters,
                ["game_types"] = gameTypes,
                ["custom_modifiers"] = new Dictionary<string, object?>
                {
                    ["good"] = goodMods,
                    ["bad"] = badMods
                }
            },
            ["multiplayer"] = new Dictionary<string, object?>
            {
                ["has_existing_save"] = SaveManager.Instance.HasMultiplayerRunSave,
                ["characters"] = characters,
                ["max_ascension_level"] = progress.MaxMultiplayerAscension,
                ["game_types"] = mpGameTypes
            }
        };
    }

    private static List<Dictionary<string, object?>> BuildModifierList(
        IReadOnlyList<ModifierModel> modifiers)
    {
        var result = new List<Dictionary<string, object?>>();
        foreach (var mod in modifiers)
        {
            // CharacterCards is expanded into one entry per character,
            // matching NCustomRunModifiersList.GetAllModifiers()
            if (mod is CharacterCards)
            {
                foreach (var character in ModelDb.AllCharacters)
                {
                    var cc = (CharacterCards)mod.ToMutable();
                    cc.CharacterModel = character.Id;
                    result.Add(new Dictionary<string, object?>
                    {
                        ["id"] = $"{mod.Id.Entry}_{character.Id.Entry}",
                        ["name"] = SafeGetText(() => cc.Title),
                        ["description"] = SafeGetText(() => cc.Description)
                    });
                }
                continue;
            }
            result.Add(new Dictionary<string, object?>
            {
                ["id"] = mod.Id.Entry,
                ["name"] = SafeGetText(() => mod.Title),
                ["description"] = SafeGetText(() => mod.Description)
            });
        }
        return result;
    }

    private static StartRunLobby? FindActiveLobby(Node root)
    {
        var charSelect = FindFirst<NCharacterSelectScreen>(root);
        if (charSelect != null)
        {
            try { return charSelect.Lobby; }
            catch { /* lobby not initialized */ }
        }
        var customRun = FindFirst<NCustomRunScreen>(root);
        if (customRun != null)
        {
            try { return customRun.Lobby; }
            catch { /* lobby not initialized */ }
        }
        // NDailyRunScreen has a private _lobby field, check via reflection
        var dailyRun = FindFirst<NDailyRunScreen>(root);
        if (dailyRun != null)
        {
            try
            {
                var field = typeof(NDailyRunScreen).GetField("_lobby",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (field?.GetValue(dailyRun) is StartRunLobby dailyLobby)
                    return dailyLobby;
            }
            catch { /* no lobby */ }
        }
        return null;
    }

    private static Dictionary<string, object?> BuildLobbyState(StartRunLobby lobby, Node root)
    {
        bool isHost = lobby.NetService.Type == NetGameType.Host
                   || lobby.NetService.Type == NetGameType.Singleplayer;

        var players = new List<Dictionary<string, object?>>();
        foreach (var p in lobby.Players)
        {
            string? name = null;
            try { name = PlatformUtil.GetPlayerName(PlatformUtil.PrimaryPlatform, p.id); }
            catch { /* fallback */ }
            players.Add(new Dictionary<string, object?>
            {
                ["id"] = p.id,
                ["name"] = name ?? p.id.ToString(),
                ["slot"] = p.slotId,
                ["character"] = p.character?.Id.Entry,
                ["is_ready"] = p.isReady,
                ["is_local"] = p.id == lobby.NetService.NetId
            });
        }

        var modifierIds = new List<string>();
        foreach (var m in lobby.Modifiers)
            modifierIds.Add(m.Id.Entry);

        return new Dictionary<string, object?>
        {
            ["state"] = "lobby",
            ["lobby"] = new Dictionary<string, object?>
            {
                ["role"] = isHost ? "host" : "client",
                ["game_type"] = lobby.GameMode.ToString().ToLowerInvariant(),
                ["players"] = players,
                ["ascension"] = lobby.Ascension,
                ["max_ascension"] = lobby.MaxAscension,
                ["seed"] = lobby.Seed,
                ["modifiers"] = modifierIds,
                ["can_start"] = lobby.IsAboutToBeginGame()
            }
        };
    }

    // -------------------------------------------------------------------------
    // POST /api/v1/menu
    // -------------------------------------------------------------------------

    private static Dictionary<string, object?> ExecuteMenuAction(
        string action, Dictionary<string, JsonElement> parsed)
    {
        return action switch
        {
            "start_singleplayer" => ActionStartSingleplayer(parsed),
            "host_game" => ActionHostGame(parsed),
            "join_game" => ActionJoinGame(parsed),
            "abandon_run" => ActionAbandonRun(parsed),
            "continue_run" => ActionContinueRun(parsed),
            // Lobby actions
            "select_character" => ActionLobbySelectCharacter(parsed),
            "set_ascension" => ActionLobbySetAscension(parsed),
            "set_seed" => ActionLobbySetSeed(parsed),
            "set_modifiers" => ActionLobbySetModifiers(parsed),
            "set_ready" => ActionLobbySetReady(parsed),
            "start_game" => ActionLobbyStartGame(parsed),
            "leave_lobby" => ActionLeaveLobby(parsed),
            _ => Error($"Unknown menu action: {action}")
        };
    }

    // ---- start_singleplayer -------------------------------------------------

    private static Dictionary<string, object?> ActionStartSingleplayer(
        Dictionary<string, JsonElement> parsed)
    {
        if (RunManager.Instance.IsInProgress)
            return Error("A run is already in progress.");

        if (SaveManager.Instance.HasRunSave)
            return Error("An existing singleplayer save exists. Use 'continue_run' to resume or 'abandon_run' to delete it first.");

        if (!parsed.TryGetValue("type", out var typeElem))
            return Error("Missing 'type' field (standard, daily, custom).");
        string type = typeElem.GetString() ?? "";

        return type switch
        {
            "standard" => StartStandardRun(parsed),
            "custom" => StartCustomRun(parsed),
            "daily" => StartDailyRun(),
            _ => Error($"Unknown singleplayer type: {type}")
        };
    }

    private static Dictionary<string, object?> StartStandardRun(
        Dictionary<string, JsonElement> parsed)
    {
        var character = ResolveCharacter(parsed);
        if (character == null)
            return Error("Missing or invalid 'character'. Use character ID (e.g. IRONCLAD).");

        int ascension = ResolveAscension(parsed, character);
        string seed = ResolveSeed(parsed);
        var unlockState = SaveManager.Instance.GenerateUnlockStateFromProgress();
        var acts = ActModel.GetRandomList(seed, unlockState, false).ToList();

        TaskHelper.RunSafely(DoStartSingleplayerRun(character, acts,
            Array.Empty<ModifierModel>(), seed, ascension, null));

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"Starting standard run as {character.Id.Entry}...",
            ["character"] = character.Id.Entry,
            ["ascension"] = ascension,
            ["seed"] = seed
        };
    }

    private static Dictionary<string, object?> StartCustomRun(
        Dictionary<string, JsonElement> parsed)
    {
        var character = ResolveCharacter(parsed);
        if (character == null)
            return Error("Missing or invalid 'character'. Use character ID (e.g. IRONCLAD).");

        int ascension = ResolveAscension(parsed, character);
        string seed = ResolveSeed(parsed);

        // Resolve modifiers
        var modifiers = new List<ModifierModel>();
        if (parsed.TryGetValue("enabled_features", out var featElem)
            && featElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in featElem.EnumerateArray())
            {
                string? modId = item.GetString();
                if (modId == null) continue;
                var resolved = ResolveModifier(modId, parsed);
                if (resolved != null)
                    modifiers.Add(resolved);
            }
        }

        var unlockState = SaveManager.Instance.GenerateUnlockStateFromProgress();
        var acts = ActModel.GetRandomList(seed, unlockState, false).ToList();

        TaskHelper.RunSafely(DoStartSingleplayerRun(character, acts,
            modifiers, seed, ascension, null));

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"Starting custom run as {character.Id.Entry}...",
            ["character"] = character.Id.Entry,
            ["ascension"] = ascension,
            ["seed"] = seed,
            ["modifiers"] = modifiers.Select(m => m.Id.Entry).ToList()
        };
    }

    private static Dictionary<string, object?> StartDailyRun()
    {
        // Fetch daily time - this blocks briefly for the HTTP call
        TimeServerResult timeResult;
        try
        {
            var task = TimeServer.FetchDailyTime();
            var result = task.GetAwaiter().GetResult();
            if (result.HasValue)
            {
                timeResult = result.Value;
            }
            else
            {
                timeResult = new TimeServerResult
                {
                    serverTime = DateTimeOffset.UtcNow,
                    localReceivedTime = DateTimeOffset.UtcNow
                };
            }
        }
        catch
        {
            timeResult = new TimeServerResult
            {
                serverTime = DateTimeOffset.UtcNow,
                localReceivedTime = DateTimeOffset.UtcNow
            };
        }

        // Replicate NDailyRunScreen.SetupLobbyParams logic
        var serverTime = timeResult.serverTime;
        string dateStr = SeedHelper.CanonicalizeSeed(serverTime.ToString("dd_MM_yyyy"));
        string seed = SeedHelper.CanonicalizeSeed(serverTime.ToString("dd_MM_yyyy_1p"));

        var rng = new Rng((uint)StringHelper.GetDeterministicHashCode(dateStr));
        var charRng = new Rng(rng.NextUnsignedInt());
        var ascRng = new Rng(rng.NextUnsignedInt());
        var modRng = new Rng(rng.NextUnsignedInt());

        // Character: pick one per player slot (we only have 1 player for SP)
        var character = charRng.NextItem(ModelDb.AllCharacters)
            ?? ModelDb.Character<Ironclad>();

        // Ascension: 0-10
        int ascension = ascRng.NextInt(0, 11);

        // Modifiers: 2 good + 1 bad (replicating RollModifiers)
        var modifiers = RollDailyModifiers(modRng, character);

        var unlockState = new UnlockState(SaveManager.Instance.Progress);
        var acts = ActModel.GetRandomList(seed, unlockState, false).ToList();

        TaskHelper.RunSafely(DoStartSingleplayerRun(character!, acts,
            modifiers, seed, ascension, serverTime));

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"Starting daily run as {character.Id.Entry}...",
            ["character"] = character.Id.Entry,
            ["ascension"] = ascension,
            ["seed"] = seed,
            ["modifiers"] = modifiers.Select(m => m.Id.Entry).ToList()
        };
    }

    private static List<ModifierModel> RollDailyModifiers(Rng rng, CharacterModel playerChar)
    {
        var result = new List<ModifierModel>();
        var pool = ModelDb.GoodModifiers.ToList().StableShuffle(rng);

        for (int i = 0; i < 2; i++)
        {
            var canonical = rng.NextItem(pool);
            if (canonical == null) break;
            var mod = canonical.ToMutable();
            if (mod is CharacterCards cc)
            {
                // Pick a character that isn't the player's character
                var others = ModelDb.AllCharacters.Where(c => c != playerChar);
                var otherChar = rng.NextItem(others);
                if (otherChar != null) cc.CharacterModel = otherChar.Id;
            }
            result.Add(mod);
            pool.Remove(canonical);
            // Remove mutually exclusive modifiers from pool
            var exclusiveSet = ModelDb.MutuallyExclusiveModifiers
                .FirstOrDefault(s => s.Contains(canonical));
            if (exclusiveSet != null)
            {
                foreach (var excl in exclusiveSet)
                    pool.Remove(excl);
            }
        }

        var badMod = rng.NextItem(ModelDb.BadModifiers);
        if (badMod != null) result.Add(badMod.ToMutable());
        return result;
    }

    private static async Task DoStartSingleplayerRun(
        CharacterModel character, List<ActModel> acts,
        IReadOnlyList<ModifierModel> modifiers, string seed,
        int ascension, DateTimeOffset? dailyTime)
    {
        NAudioManager.Instance?.StopMusic();
        await NGame.Instance.Transition.FadeOut(0.8f,
            character.CharacterSelectTransitionPath);
        await NGame.Instance.StartNewSingleplayerRun(
            character, shouldSave: true, acts, modifiers, seed, ascension, dailyTime);
    }

    // ---- abandon_run --------------------------------------------------------

    private static Dictionary<string, object?> ActionAbandonRun(
        Dictionary<string, JsonElement> parsed)
    {
        if (!parsed.TryGetValue("type", out var typeElem))
            return Error("Missing 'type' field (singleplayer or multiplayer).");
        string type = typeElem.GetString() ?? "";

        if (type == "singleplayer")
            return AbandonSingleplayerRun();
        if (type == "multiplayer")
            return AbandonMultiplayerRun();
        return Error($"Unknown abandon type: {type}. Use 'singleplayer' or 'multiplayer'.");
    }

    private static Dictionary<string, object?> AbandonSingleplayerRun()
    {
        if (!SaveManager.Instance.HasRunSave)
            return Error("No singleplayer run save to abandon.");

        var readResult = SaveManager.Instance.LoadRunSave();
        if (readResult.Success && readResult.SaveData != null)
        {
            try
            {
                var save = readResult.SaveData;
                SaveManager.Instance.UpdateProgressWithRunData(save, victory: false);
                RunHistoryUtilities.CreateRunHistoryEntry(save, victory: false,
                    isAbandoned: true, save.PlatformType);
                if (save.DailyTime.HasValue)
                {
                    int score = ScoreUtility.CalculateScore(save, won: false);
                    TaskHelper.RunSafely(
                        DailyRunUtility.UploadScore(save.DailyTime.Value, score, save.Players));
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[STS2 MCP] Failed to update progress on abandon: {ex}");
            }
        }

        SaveManager.Instance.DeleteCurrentRun();

        // Refresh main menu buttons if visible
        var root = ((SceneTree)Engine.GetMainLoop()).Root;
        var mainMenu = FindFirst<NMainMenu>(root);
        mainMenu?.RefreshButtons();

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = "Singleplayer run abandoned."
        };
    }

    private static Dictionary<string, object?> AbandonMultiplayerRun()
    {
        if (!SaveManager.Instance.HasMultiplayerRunSave)
            return Error("No multiplayer run save to abandon.");

        var localPlayerId = PlatformUtil.GetLocalPlayerId(PlatformUtil.PrimaryPlatform);
        var readResult = SaveManager.Instance.LoadAndCanonicalizeMultiplayerRunSave(localPlayerId);
        if (readResult.Success && readResult.SaveData != null)
        {
            try
            {
                var save = readResult.SaveData;
                SaveManager.Instance.UpdateProgressWithRunData(save, victory: false);
                RunHistoryUtilities.CreateRunHistoryEntry(save, victory: false,
                    isAbandoned: true, save.PlatformType);
                if (save.DailyTime.HasValue)
                {
                    int score = ScoreUtility.CalculateScore(save, won: false);
                    TaskHelper.RunSafely(
                        DailyRunUtility.UploadScore(save.DailyTime.Value, score, save.Players));
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[STS2 MCP] Failed to update progress on MP abandon: {ex}");
            }
        }

        SaveManager.Instance.DeleteCurrentMultiplayerRun();

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = "Multiplayer run abandoned."
        };
    }

    // ---- continue_run -------------------------------------------------------

    private static Dictionary<string, object?> ActionContinueRun(
        Dictionary<string, JsonElement> parsed)
    {
        if (RunManager.Instance.IsInProgress)
            return Error("A run is already in progress.");

        if (!parsed.TryGetValue("type", out var typeElem))
            return Error("Missing 'type' field (singleplayer or multiplayer).");
        string type = typeElem.GetString() ?? "";

        if (type == "singleplayer")
            return ContinueSingleplayerRun();
        if (type == "multiplayer")
            return ContinueMultiplayerRun();
        return Error($"Unknown type: {type}. Use 'singleplayer' or 'multiplayer'.");
    }

    private static Dictionary<string, object?> ContinueSingleplayerRun()
    {
        if (!SaveManager.Instance.HasRunSave)
            return Error("No singleplayer run save to continue.");

        var readResult = SaveManager.Instance.LoadRunSave();
        if (!readResult.Success || readResult.SaveData == null)
            return Error("Failed to load singleplayer run save.");

        var save = readResult.SaveData;
        TaskHelper.RunSafely(DoContinueSingleplayerRun(save));

        string charId = save.Players.Count > 0 ? save.Players[0].CharacterId.Entry : "unknown";
        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"Continuing singleplayer run as {charId}..."
        };
    }

    private static async Task DoContinueSingleplayerRun(
        MegaCrit.Sts2.Core.Saves.SerializableRun save)
    {
        var runState = RunState.FromSerializable(save);
        RunManager.Instance.SetUpSavedSinglePlayer(runState, save);
        NAudioManager.Instance?.StopMusic();
        await NGame.Instance.Transition.FadeOut(0.8f,
            runState.Players[0].Character.CharacterSelectTransitionPath);
        NGame.Instance.ReactionContainer.InitializeNetworking(
            new NetSingleplayerGameService());
        await NGame.Instance.LoadRun(runState, save.PreFinishedRoom);
        await NGame.Instance.Transition.FadeIn();
    }

    private static Dictionary<string, object?> ContinueMultiplayerRun()
    {
        if (!SaveManager.Instance.HasMultiplayerRunSave)
            return Error("No multiplayer run save to continue.");

        var root = ((SceneTree)Engine.GetMainLoop()).Root;
        var mainMenu = FindFirst<NMainMenu>(root);
        if (mainMenu == null)
            return Error("Not on the main menu.");

        var localPlayerId = PlatformUtil.GetLocalPlayerId(PlatformUtil.PrimaryPlatform);
        var readResult = SaveManager.Instance.LoadAndCanonicalizeMultiplayerRunSave(localPlayerId);
        if (!readResult.Success || readResult.SaveData == null)
            return Error("Failed to load multiplayer run save.");

        // Drive the UI: open multiplayer submenu -> StartHost(savedRun)
        // This creates a host and pushes the appropriate load screen
        var mpSubmenu = mainMenu.OpenMultiplayerSubmenu();
        mpSubmenu.StartHost(readResult.SaveData);

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = "Hosting saved multiplayer run. Waiting for players to rejoin..."
        };
    }

    // ---- quit_run (called from /singleplayer and /multiplayer endpoints) -----

    internal static Dictionary<string, object?> ExecuteQuitRun(bool isMultiplayer)
    {
        if (!RunManager.Instance.IsInProgress)
            return Error("No run is in progress.");

        string mode = isMultiplayer ? "multiplayer" : "singleplayer";
        TaskHelper.RunSafely(DoQuitRun(isMultiplayer));

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"Quitting {mode} run and returning to main menu..."
        };
    }

    private static async Task DoQuitRun(bool isMultiplayer)
    {
        // If multiplayer and still connected, disconnect first
        if (isMultiplayer && RunManager.Instance.NetService.IsConnected)
            RunManager.Instance.NetService.Disconnect(NetError.Quit);

        NRunMusicController.Instance?.StopMusic();
        await NGame.Instance.ReturnToMainMenu();
    }

    // ---- host_game ----------------------------------------------------------

    private static Dictionary<string, object?> ActionHostGame(
        Dictionary<string, JsonElement> parsed)
    {
        if (RunManager.Instance.IsInProgress)
            return Error("A run is already in progress.");

        if (!parsed.TryGetValue("type", out var typeElem))
            return Error("Missing 'type' field (standard, daily, custom).");
        string type = typeElem.GetString() ?? "";

        GameMode gameMode = type switch
        {
            "standard" => GameMode.Standard,
            "daily" => GameMode.Daily,
            "custom" => GameMode.Custom,
            _ => GameMode.None
        };
        if (gameMode == GameMode.None)
            return Error($"Unknown game type: {type}. Use standard, daily, or custom.");

        var root = ((SceneTree)Engine.GetMainLoop()).Root;
        var mainMenu = FindFirst<NMainMenu>(root);
        if (mainMenu == null)
            return Error("Not on the main menu.");

        // Check if already in a lobby
        if (FindActiveLobby(root) != null)
            return Error("Already in a lobby. Use 'leave_lobby' first.");

        // Drive the UI: open multiplayer submenu -> fast host
        var mpSubmenu = mainMenu.OpenMultiplayerSubmenu();
        mpSubmenu.FastHost(gameMode);

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"Hosting {type} lobby. Waiting for players..."
        };
    }

    // ---- join_game ----------------------------------------------------------

    private static Dictionary<string, object?> ActionJoinGame(
        Dictionary<string, JsonElement> parsed)
    {
        if (RunManager.Instance.IsInProgress)
            return Error("A run is already in progress.");

        if (!parsed.TryGetValue("friend_name", out var nameElem))
            return Error("Missing 'friend_name' field.");
        string friendName = nameElem.GetString() ?? "";

        if (!SteamInitializer.Initialized)
            return Error("Steam is not initialized. Cannot join friends.");

        var root = ((SceneTree)Engine.GetMainLoop()).Root;
        var mainMenu = FindFirst<NMainMenu>(root);
        if (mainMenu == null)
            return Error("Not on the main menu.");

        if (FindActiveLobby(root) != null)
            return Error("Already in a lobby. Use 'leave_lobby' first.");

        // Find matching friend with open lobby
        var friendsTask = PlatformUtil.GetFriendsWithOpenLobbies(PlatformType.Steam);
        var friends = friendsTask.GetAwaiter().GetResult();

        ulong? matchedId = null;
        foreach (var fid in friends)
        {
            string name = PlatformUtil.GetPlayerName(PlatformType.Steam, fid);
            if (string.Equals(name, friendName, StringComparison.OrdinalIgnoreCase))
            {
                matchedId = fid;
                break;
            }
        }

        if (matchedId == null)
            return Error($"No friend named '{friendName}' with an open lobby found.");

        // Drive the UI: open multiplayer submenu -> join friend screen -> join
        var mpSubmenu = mainMenu.OpenMultiplayerSubmenu();
        var joinScreen = mpSubmenu.OnJoinFriendsPressed();
        var connInit = SteamClientConnectionInitializer.FromPlayer(matchedId.Value);
        TaskHelper.RunSafely(joinScreen.JoinGameAsync(connInit));

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"Joining {friendName}'s lobby..."
        };
    }

    // ---- Lobby actions ------------------------------------------------------

    private static StartRunLobby? GetLobbyOrNull()
    {
        var root = ((SceneTree)Engine.GetMainLoop()).Root;
        return FindActiveLobby(root);
    }

    private static Dictionary<string, object?> ActionLobbySelectCharacter(
        Dictionary<string, JsonElement> parsed)
    {
        var lobby = GetLobbyOrNull();
        if (lobby == null) return Error("Not in a lobby.");

        var character = ResolveCharacter(parsed);
        if (character == null)
            return Error("Missing or invalid 'character'. Use character ID (e.g. IRONCLAD).");

        // Find the UI button and click it so the illustration/info panel updates
        var root = ((SceneTree)Engine.GetMainLoop()).Root;
        var buttons = FindAll<NCharacterSelectButton>(root);
        var button = buttons.FirstOrDefault(b =>
            b.Character != null && b.Character.Id == character.Id);
        if (button != null)
        {
            button.Select();
        }
        else
        {
            // Fallback: update lobby data directly (no UI update)
            lobby.SetLocalCharacter(character);
        }

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"Character set to {character.Id.Entry}."
        };
    }

    private static Dictionary<string, object?> ActionLobbySetAscension(
        Dictionary<string, JsonElement> parsed)
    {
        var lobby = GetLobbyOrNull();
        if (lobby == null) return Error("Not in a lobby.");

        if (lobby.NetService.Type == NetGameType.Client)
            return Error("Only the host can change ascension level.");

        if (!parsed.TryGetValue("level", out var levelElem))
            return Error("Missing 'level' field.");
        int level = levelElem.GetInt32();
        level = Math.Clamp(level, 0, lobby.MaxAscension);

        lobby.SyncAscensionChange(level);
        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"Ascension set to {level}."
        };
    }

    private static Dictionary<string, object?> ActionLobbySetSeed(
        Dictionary<string, JsonElement> parsed)
    {
        var lobby = GetLobbyOrNull();
        if (lobby == null) return Error("Not in a lobby.");

        if (lobby.NetService.Type == NetGameType.Client)
            return Error("Only the host can change the seed.");
        if (lobby.GameMode != GameMode.Custom)
            return Error("Seed can only be set in custom mode.");

        string? seed = null;
        if (parsed.TryGetValue("seed", out var seedElem)
            && seedElem.ValueKind == JsonValueKind.String)
            seed = seedElem.GetString();

        lobby.SetSeed(seed);
        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = seed != null ? $"Seed set to {seed}." : "Seed cleared (random)."
        };
    }

    private static Dictionary<string, object?> ActionLobbySetModifiers(
        Dictionary<string, JsonElement> parsed)
    {
        var lobby = GetLobbyOrNull();
        if (lobby == null) return Error("Not in a lobby.");

        if (lobby.NetService.Type == NetGameType.Client)
            return Error("Only the host can change modifiers.");
        if (lobby.GameMode != GameMode.Custom)
            return Error("Modifiers can only be set in custom mode.");

        var modifiers = new List<ModifierModel>();
        if (parsed.TryGetValue("modifiers", out var modsElem)
            && modsElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in modsElem.EnumerateArray())
            {
                string? modId = item.GetString();
                if (modId == null) continue;
                var resolved = ResolveModifier(modId, parsed);
                if (resolved != null)
                    modifiers.Add(resolved);
            }
        }

        lobby.SetModifiers(modifiers);
        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"Modifiers set: [{string.Join(", ", modifiers.Select(m => m.Id.Entry))}]"
        };
    }

    private static Dictionary<string, object?> ActionLobbySetReady(
        Dictionary<string, JsonElement> parsed)
    {
        var lobby = GetLobbyOrNull();
        if (lobby == null) return Error("Not in a lobby.");

        bool ready = true;
        if (parsed.TryGetValue("ready", out var readyElem))
            ready = readyElem.GetBoolean();

        lobby.SetReady(ready);
        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = ready ? "Marked as ready." : "Marked as not ready."
        };
    }

    private static Dictionary<string, object?> ActionLobbyStartGame(
        Dictionary<string, JsonElement> parsed)
    {
        var lobby = GetLobbyOrNull();
        if (lobby == null) return Error("Not in a lobby.");

        if (lobby.NetService.Type == NetGameType.Client)
            return Error("Only the host can start the game.");

        // Check if all other players are ready
        bool othersReady = lobby.Players
            .Where(p => p.id != lobby.NetService.NetId)
            .All(p => p.isReady);

        if (!othersReady && lobby.Players.Count > 1)
            return Error("Not all players are ready.");

        // Setting host to ready triggers BeginRunIfAllPlayersReady
        lobby.SetReady(true);
        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = "Starting game..."
        };
    }

    private static Dictionary<string, object?> ActionLeaveLobby(
        Dictionary<string, JsonElement> parsed)
    {
        var root = ((SceneTree)Engine.GetMainLoop()).Root;
        var lobby = FindActiveLobby(root);
        if (lobby == null) return Error("Not in a lobby.");

        var mainMenu = FindFirst<NMainMenu>(root);
        if (mainMenu == null) return Error("Main menu not found.");

        // Pop all submenus to return to main menu
        while (mainMenu.SubmenuStack.SubmenusOpen)
            mainMenu.SubmenuStack.Pop();

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = "Left lobby."
        };
    }

    // ---- Helpers ------------------------------------------------------------

    private static CharacterModel? ResolveCharacter(Dictionary<string, JsonElement> parsed)
    {
        if (!parsed.TryGetValue("character", out var charElem))
            return null;
        string charId = charElem.GetString() ?? "";

        foreach (var character in ModelDb.AllCharacters)
        {
            if (string.Equals(character.Id.Entry, charId, StringComparison.OrdinalIgnoreCase))
                return character;
        }
        return null;
    }

    private static int ResolveAscension(Dictionary<string, JsonElement> parsed,
        CharacterModel character)
    {
        int ascension = 0;
        if (parsed.TryGetValue("ascension_level", out var ascElem))
            ascension = ascElem.GetInt32();

        int maxAscension = SaveManager.Instance.Progress
            .GetOrCreateCharacterStats(character.Id).MaxAscension;
        return Math.Clamp(ascension, 0, maxAscension);
    }

    private static string ResolveSeed(Dictionary<string, JsonElement> parsed)
    {
        if (parsed.TryGetValue("seed", out var seedElem)
            && seedElem.ValueKind == JsonValueKind.String)
        {
            string? seed = seedElem.GetString();
            if (!string.IsNullOrWhiteSpace(seed))
                return SeedHelper.CanonicalizeSeed(seed);
        }
        return SeedHelper.GetRandomSeed();
    }

    private static ModifierModel? ResolveModifier(string modId,
        Dictionary<string, JsonElement> parsed)
    {
        // Handle compound CHARACTER_CARDS_<CHARACTER> IDs
        // e.g. "CHARACTER_CARDS_IRONCLAD" -> CharacterCards with CharacterModel = IRONCLAD
        const string ccPrefix = "CHARACTER_CARDS_";
        if (modId.StartsWith(ccPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string charPart = modId[ccPrefix.Length..];
            var ccCanonical = ModelDb.GoodModifiers
                .FirstOrDefault(m => m is CharacterCards);
            if (ccCanonical == null) return null;
            var cc = (CharacterCards)ccCanonical.ToMutable();
            foreach (var ch in ModelDb.AllCharacters)
            {
                if (string.Equals(ch.Id.Entry, charPart, StringComparison.OrdinalIgnoreCase))
                {
                    cc.CharacterModel = ch.Id;
                    return cc;
                }
            }
            return null;
        }

        var allMods = ModelDb.GoodModifiers.Concat(ModelDb.BadModifiers);
        foreach (var canonical in allMods)
        {
            if (string.Equals(canonical.Id.Entry, modId, StringComparison.OrdinalIgnoreCase))
            {
                var mod = canonical.ToMutable();
                // Plain CHARACTER_CARDS without compound ID: use the run's character
                if (mod is CharacterCards cc2)
                {
                    string? ccCharId = null;
                    if (parsed.TryGetValue("modifier_character", out var mcElem))
                        ccCharId = mcElem.GetString();
                    else if (parsed.TryGetValue("character", out var cElem))
                        ccCharId = cElem.GetString();

                    if (ccCharId != null)
                    {
                        foreach (var ch in ModelDb.AllCharacters)
                        {
                            if (string.Equals(ch.Id.Entry, ccCharId,
                                StringComparison.OrdinalIgnoreCase))
                            {
                                cc2.CharacterModel = ch.Id;
                                break;
                            }
                        }
                    }
                }
                return mod;
            }
        }
        return null;
    }
}

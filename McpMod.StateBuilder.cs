using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Models.RelicPools;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.Timeline;
using MegaCrit.Sts2.Core.Nodes.Screens.ProfileScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;
using Godot;

namespace STS2_MCP;

public static partial class McpMod
{
    private static Dictionary<string, object?> BuildGameState()
    {
        var result = new Dictionary<string, object?>();

        if (!RunManager.Instance.IsInProgress)
        {
            result["state_type"] = "menu";

            // Detect which menu screen is active
            var tree = (Godot.Engine.GetMainLoop()) as SceneTree;
            if (tree?.Root != null)
            {
                // Check for tutorial FTUE popup
                var tutorialFtue = FindFirst<MegaCrit.Sts2.Core.Nodes.Ftue.NAcceptTutorialsFtue>(tree.Root);
                if (tutorialFtue != null && tutorialFtue.Visible)
                {
                    result["menu_screen"] = "tutorial_prompt";
                    result["message"] = "Enable Tutorials? Choose yes or no.";
                    result["options"] = new List<Dictionary<string, object?>>
                    {
                        new() { ["name"] = "no", ["enabled"] = true },
                        new() { ["name"] = "yes", ["enabled"] = true }
                    };
                }

                // Check for any other FTUE popup
                if (!result.ContainsKey("menu_screen"))
                {
                    var ftue = FindFirst<MegaCrit.Sts2.Core.Nodes.Ftue.NFtue>(tree.Root);
                    if (ftue != null && ftue.Visible)
                    {
                        result["menu_screen"] = "tutorial";
                        result["message"] = "Tutorial popup active. Use advance to dismiss.";
                    }
                }

                if (!result.ContainsKey("menu_screen"))
                {
                // Check for singleplayer submenu (Standard / Daily / Custom)
                var spSubmenu = FindFirst<NSingleplayerSubmenu>(tree.Root);
                if (spSubmenu != null && spSubmenu.Visible)
                {
                    result["menu_screen"] = "singleplayer";
                    result["message"] = "Select game mode.";

                    var modeOptions = new List<Dictionary<string, object?>>();
                    var modeFields = new[] { ("_standardButton", "standard"), ("_dailyButton", "daily"), ("_customButton", "custom") };
                    foreach (var (fieldName, label) in modeFields)
                    {
                        try
                        {
                            var btn = spSubmenu.GetType().GetField(fieldName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(spSubmenu);
                            if (btn is Control ctrl && ctrl.Visible)
                            {
                                var isEnabled = btn.GetType().GetProperty("IsEnabled")?.GetValue(btn) as bool?;
                                modeOptions.Add(new Dictionary<string, object?>
                                {
                                    ["name"] = label,
                                    ["enabled"] = isEnabled ?? true
                                });
                            }
                        }
                        catch { }
                    }
                    result["options"] = modeOptions;
                }
                // Check for multiplayer host submenu (Standard / Daily / Custom for multiplayer)
                else
                {
                    var mpHostSubmenu = FindFirst<NMultiplayerHostSubmenu>(tree.Root);
                    if (mpHostSubmenu != null && mpHostSubmenu.Visible)
                    {
                        result["menu_screen"] = "multiplayer_host";
                        result["message"] = "Multiplayer host: select game mode.";

                        var modeOptions = new List<Dictionary<string, object?>>();
                        var modeFields = new[] { ("_standardButton", "standard"), ("_dailyButton", "daily"), ("_customButton", "custom") };
                        foreach (var (fieldName, label) in modeFields)
                        {
                            try
                            {
                                var btn = mpHostSubmenu.GetType().GetField(fieldName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(mpHostSubmenu);
                                if (btn is Control ctrl && ctrl.Visible)
                                {
                                    var isEnabled = btn.GetType().GetProperty("IsEnabled")?.GetValue(btn) as bool?;
                                    modeOptions.Add(new Dictionary<string, object?>
                                    {
                                        ["name"] = label,
                                        ["enabled"] = isEnabled ?? true
                                    });
                                }
                            }
                            catch { }
                        }
                        result["options"] = modeOptions;
                    }
                    else
                    {
                        // Check for multiplayer submenu (Host / Join / Load / Abandon)
                        var mpSubmenu = FindFirst<NMultiplayerSubmenu>(tree.Root);
                        if (mpSubmenu != null && mpSubmenu.Visible)
                        {
                            result["menu_screen"] = "multiplayer";
                            result["message"] = "Multiplayer menu.";

                            var mpOptions = new List<Dictionary<string, object?>>();
                            var mpFields = new[] { ("_hostButton", "host"), ("_joinButton", "join"), ("_loadButton", "load"), ("_abandonButton", "abandon") };
                            foreach (var (fieldName, label) in mpFields)
                            {
                                try
                                {
                                    var btn = mpSubmenu.GetType().GetField(fieldName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(mpSubmenu);
                                    if (btn is Control ctrl && ctrl.Visible)
                                    {
                                        var isEnabled = btn.GetType().GetProperty("IsEnabled")?.GetValue(btn) as bool?;
                                        mpOptions.Add(new Dictionary<string, object?>
                                        {
                                            ["name"] = label,
                                            ["enabled"] = isEnabled ?? true
                                        });
                                    }
                                }
                                catch { }
                            }
                            result["options"] = mpOptions;
                        }
                    }
                }
                // Check for character select screen
                if (result.ContainsKey("menu_screen") == false)
                {
                    var charSelect = FindFirst<NCharacterSelectScreen>(tree.Root);
                    if (charSelect != null && charSelect.Visible)
                    {
                        result["menu_screen"] = "character_select";
                        result["message"] = "Select a character.";

                        var buttons = FindAll<NCharacterSelectButton>(charSelect);
                        var characters = new List<Dictionary<string, object?>>();
                        foreach (var btn in buttons)
                        {
                            try
                            {
                                if (btn.Character is { } cm)
                                {
                                    var charData = new Dictionary<string, object?>
                                    {
                                        ["name"] = SafeGetText(() => cm.Title),
                                        ["id"] = cm.Id.Entry,
                                        ["locked"] = btn.IsLocked,
                                        ["hp"] = cm.StartingHp,
                                        ["gold"] = cm.StartingGold,
                                        ["energy"] = cm.MaxEnergy,
                                        ["description"] = SafeGetText(() => cm.CardsModifierDescription),
                                    };

                                    // Starting relics
                                    var startRelics = new List<Dictionary<string, object?>>();
                                    foreach (var relic in cm.StartingRelics)
                                    {
                                        startRelics.Add(new Dictionary<string, object?>
                                        {
                                            ["name"] = SafeGetText(() => relic.Title),
                                            ["description"] = SafeGetText(() => relic.DynamicDescription)
                                        });
                                    }
                                    if (startRelics.Count > 0)
                                        charData["starting_relics"] = startRelics;

                                    // Starting deck summary
                                    var deckCards = new List<string>();
                                    foreach (var card in cm.StartingDeck)
                                        deckCards.Add(SafeGetText(() => card.Title) ?? "?");
                                    if (deckCards.Count > 0)
                                        charData["starting_deck"] = deckCards;

                                    // Known cards count from card pool
                                    try
                                    {
                                        var allCards = cm.CardPool?.AllCards;
                                        if (allCards != null)
                                            charData["total_cards"] = System.Linq.Enumerable.Count(allCards);
                                    }
                                    catch { }

                                    // Known relics count from relic pool
                                    try
                                    {
                                        var allRelics = cm.RelicPool?.AllRelics;
                                        if (allRelics != null)
                                            charData["total_relics"] = System.Linq.Enumerable.Count(allRelics);
                                    }
                                    catch { }

                                    // Known potions count from potion pool
                                    try
                                    {
                                        var allPotions = cm.PotionPool?.AllPotions;
                                        if (allPotions != null)
                                            charData["total_potions"] = System.Linq.Enumerable.Count(allPotions);
                                    }
                                    catch { }

                                    characters.Add(charData);
                                }
                            }
                            catch { }
                        }
                        if (characters.Count > 0)
                            result["characters"] = characters;
                    }
                    else
                    {
                        // Check for other screens
                        var timelineScreen = FindFirst<NTimelineScreen>(tree.Root);
                        var compendiumSubmenu = FindFirst<NCompendiumSubmenu>(tree.Root);
                        var settingsScreen = FindFirst<NSettingsScreen>(tree.Root);

                        if (timelineScreen != null && timelineScreen.Visible)
                        {
                            result["menu_screen"] = "timeline";
                            result["message"] = "Timeline screen.";

                            // Read epochs from ProgressState (stable, not hover-dependent)
                            try
                            {
                                var progress = SaveManager.Instance?.Progress;
                                if (progress != null)
                                {
                                    var epochList = new List<Dictionary<string, object?>>();
                                    foreach (var epoch in progress.Epochs)
                                    {
                                        var eraName = epoch.Id;
                                        // Clean up ID to readable name
                                        var name = System.Text.RegularExpressions.Regex.Replace(eraName, @"(\d+)$", "");
                                        name = System.Text.RegularExpressions.Regex.Replace(name, @"(?<=[a-z])(?=[A-Z])", " ");

                                        epochList.Add(new Dictionary<string, object?>
                                        {
                                            ["id"] = eraName,
                                            ["name"] = name,
                                            ["state"] = epoch.State.ToString(),
                                            ["obtained"] = epoch.ObtainDate
                                        });
                                    }

                                    // Count total slots from UI for hidden count
                                    var allSlots = FindAll<NEpochSlot>(timelineScreen);
                                    var completedCount = allSlots.Count(s => s.State.ToString() == "Complete" || s.State.ToString() == "Obtained");
                                    var lockedVisible = allSlots.Count(s => s.State.ToString() == "NotObtained");

                                    result["epochs"] = epochList;
                                    result["total_slots"] = allSlots.Count;
                                    result["completed_count"] = completedCount;
                                    result["locked_count"] = lockedVisible;
                                }
                            }
                            catch { }
                        }
                        else if (compendiumSubmenu != null && compendiumSubmenu.Visible)
                        {
                            result["menu_screen"] = "compendium";
                            result["message"] = "Compendium screen.";
                        }
                        else if (settingsScreen != null && settingsScreen.Visible)
                        {
                            result["menu_screen"] = "settings";
                            result["message"] = "Settings screen.";
                        }
                        else
                        {
                            var profileScreen = FindFirst<MegaCrit.Sts2.Core.Nodes.Screens.ProfileScreen.NProfileScreen>(tree.Root);
                            if (profileScreen != null && profileScreen.Visible)
                            {
                                result["menu_screen"] = "profile_select";
                                result["message"] = "Profile select screen.";
                                result["current_profile_id"] = SaveManager.Instance?.CurrentProfileId;
                            }
                        }
                        if (!result.ContainsKey("menu_screen"))
                        {
                            result["menu_screen"] = "main";
                            result["message"] = "Main menu.";

                        var mainMenu = FindFirst<NMainMenu>(tree.Root);
                        if (mainMenu != null)
                        {
                            var options = new List<string>();
                            var fields = new[] { "_continueButton", "_singleplayerButton", "_multiplayerButton", "_compendiumButton", "_timelineButton", "_settingsButton", "_quitButton" };
                            var labels = new[] { "continue", "singleplayer", "multiplayer", "compendium", "timeline", "settings", "quit" };
                            for (int i = 0; i < fields.Length; i++)
                            {
                                try
                                {
                                    var btn = mainMenu.GetType().GetField(fields[i], System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(mainMenu) as Control;
                                    if (btn != null && btn.Visible)
                                        options.Add(labels[i]);
                                }
                                catch { }
                            }
                            if (options.Count > 0)
                                result["options"] = options;
                        }
                        }
                    }
                }
            }
            } // close if (!result.ContainsKey("menu_screen"))
            else
            {
                result["message"] = "No run in progress.";
            }

            return result;
        }

        var runState = RunManager.Instance.DebugOnlyGetState();
        if (runState == null)
        {
            result["state_type"] = "unknown";
            return result;
        }

        // Card selection overlays can appear on top of any room (events, rest sites, combat)
        var topOverlay = NOverlayStack.Instance?.Peek();
        var currentRoom = runState.CurrentRoom;
        if (topOverlay is NCardGridSelectionScreen cardSelectScreen)
        {
            result["state_type"] = "card_select";
            result["card_select"] = BuildCardSelectState(cardSelectScreen, runState);
        }
        else if (topOverlay is NChooseACardSelectionScreen chooseCardScreen)
        {
            result["state_type"] = "card_select";
            result["card_select"] = BuildChooseCardState(chooseCardScreen, runState);
        }
        else if (topOverlay is NChooseARelicSelection relicSelectScreen)
        {
            result["state_type"] = "relic_select";
            result["relic_select"] = BuildRelicSelectState(relicSelectScreen, runState);
        }
        else if (topOverlay is NGameOverScreen gameOverScreen)
        {
            result["state_type"] = "game_over";
            result["game_over"] = new Dictionary<string, object?>
            {
                ["message"] = "Run ended.",
                ["options"] = new List<string> { "continue", "main_menu" }
            };
        }
        else if (topOverlay is IOverlayScreen
                 && topOverlay is not NRewardsScreen
                 && topOverlay is not NCardRewardSelectionScreen)
        {
            // Catch-all for unhandled overlays — prevents soft-locks
            result["state_type"] = "overlay";
            result["overlay"] = new Dictionary<string, object?>
            {
                ["screen_type"] = topOverlay.GetType().Name,
                ["message"] = $"An overlay ({topOverlay.GetType().Name}) is active. It may require manual interaction in-game."
            };
        }
        else if (currentRoom is CombatRoom combatRoom)
        {
            if (CombatManager.Instance.IsInProgress)
            {
                // Check for in-combat hand card selection (e.g., "Select a card to exhaust")
                var playerHand = NPlayerHand.Instance;
                if (playerHand != null && playerHand.IsInCardSelection)
                {
                    result["state_type"] = "hand_select";
                    result["hand_select"] = BuildHandSelectState(playerHand, runState);
                    result["battle"] = BuildBattleState(runState, combatRoom);
                }
                else
                {
                    result["state_type"] = combatRoom.RoomType.ToString().ToLower(); // monster, elite, boss
                    result["battle"] = BuildBattleState(runState, combatRoom);
                }
            }
            else
            {
                // After combat ends, check: map open (post-rewards) > overlays > fallback
                if (NMapScreen.Instance is { IsOpen: true })
                {
                    result["state_type"] = "map";
                    result["map"] = BuildMapState(runState);
                }
                else
                {
                    var overlay = NOverlayStack.Instance?.Peek();
                    if (overlay is NCardRewardSelectionScreen cardScreen)
                    {
                        result["state_type"] = "card_reward";
                        result["card_reward"] = BuildCardRewardState(cardScreen);
                    }
                    else if (overlay is NRewardsScreen rewardsScreen)
                    {
                        result["state_type"] = "combat_rewards";
                        result["rewards"] = BuildRewardsState(rewardsScreen, runState);
                    }
                    else
                    {
                        result["state_type"] = combatRoom.RoomType.ToString().ToLower();
                        result["message"] = "Combat ended. Waiting for rewards...";
                    }
                }
            }
        }
        else if (currentRoom is EventRoom eventRoom)
        {
            if (NMapScreen.Instance is { IsOpen: true })
            {
                result["state_type"] = "map";
                result["map"] = BuildMapState(runState);
            }
            else
            {
                result["state_type"] = "event";
                result["event"] = BuildEventState(eventRoom, runState);
            }
        }
        else if (currentRoom is MapRoom)
        {
            result["state_type"] = "map";
            result["map"] = BuildMapState(runState);
        }
        else if (currentRoom is MerchantRoom merchantRoom)
        {
            if (NMapScreen.Instance is { IsOpen: true })
            {
                result["state_type"] = "map";
                result["map"] = BuildMapState(runState);
            }
            else
            {
                // Auto-open the shopkeeper's inventory if not already open
                var merchUI = NMerchantRoom.Instance;
                if (merchUI != null && !merchUI.Inventory.IsOpen)
                {
                    merchUI.OpenInventory();
                }
                result["state_type"] = "shop";
                result["shop"] = BuildShopState(merchantRoom, runState);
            }
        }
        else if (currentRoom is RestSiteRoom restSiteRoom)
        {
            if (NMapScreen.Instance is { IsOpen: true })
            {
                result["state_type"] = "map";
                result["map"] = BuildMapState(runState);
            }
            else
            {
                result["state_type"] = "rest_site";
                result["rest_site"] = BuildRestSiteState(restSiteRoom, runState);
            }
        }
        else if (currentRoom is TreasureRoom treasureRoom)
        {
            if (NMapScreen.Instance is { IsOpen: true })
            {
                result["state_type"] = "map";
                result["map"] = BuildMapState(runState);
            }
            else
            {
                result["state_type"] = "treasure";
                result["treasure"] = BuildTreasureState(treasureRoom, runState);
            }
        }
        else
        {
            result["state_type"] = "unknown";
            result["room_type"] = currentRoom?.GetType().Name;
        }

        // Common run info
        result["run"] = new Dictionary<string, object?>
        {
            ["act"] = runState.CurrentActIndex + 1,
            ["floor"] = runState.TotalFloor,
            ["ascension"] = runState.AscensionLevel
        };

        // Always include full player data so external tools have it on every screen
        var _player = LocalContext.GetMe(runState);
        if (_player != null)
        {
            try
            {
                result["player"] = BuildPlayerState(_player);
            }
            catch (System.Exception e)
            {
                result["player_error"] = e.Message;
            }
        }

        // Always include map data so external tools can display it regardless of current screen
        if (result["state_type"] as string != "map")
        {
            try
            {
                result["map"] = BuildMapState(runState);
            }
            catch (System.Exception e)
            {
                result["map_error"] = e.Message;
            }
        }

        return result;
    }

    private static Dictionary<string, object?> BuildBattleState(RunState runState, CombatRoom combatRoom)
    {
        var combatState = CombatManager.Instance.DebugOnlyGetState();
        var battle = new Dictionary<string, object?>();

        if (combatState == null)
        {
            battle["error"] = "Combat state unavailable";
            return battle;
        }

        battle["round"] = combatState.RoundNumber;
        battle["turn"] = combatState.CurrentSide.ToString().ToLower();
        battle["is_play_phase"] = CombatManager.Instance.IsPlayPhase;

        // Player state
        var player = LocalContext.GetMe(runState);
        if (player != null)
        {
            battle["player"] = BuildPlayerState(player);
        }

        // Enemies
        var enemies = new List<Dictionary<string, object?>>();
        var entityCounts = new Dictionary<string, int>();
        foreach (var creature in combatState.Enemies)
        {
            if (creature.IsAlive)
            {
                enemies.Add(BuildEnemyState(creature, entityCounts));
            }
        }
        battle["enemies"] = enemies;

        return battle;
    }

    private static Dictionary<string, object?> BuildPlayerState(Player player)
    {
        var state = new Dictionary<string, object?>();
        var creature = player.Creature;
        var combatState = player.PlayerCombatState;

        state["character"] = SafeGetText(() => player.Character.Title);
        state["hp"] = creature.CurrentHp;
        state["max_hp"] = creature.MaxHp;
        state["block"] = creature.Block;

        if (combatState != null)
        {
            state["energy"] = combatState.Energy;
            state["max_energy"] = combatState.MaxEnergy;

            // Stars (The Regent's resource, conditionally shown)
            if (player.Character.ShouldAlwaysShowStarCounter || combatState.Stars > 0)
            {
                state["stars"] = combatState.Stars;
            }

            // Hand
            var hand = new List<Dictionary<string, object?>>();
            int cardIndex = 0;
            foreach (var card in combatState.Hand.Cards)
            {
                hand.Add(BuildCardState(card, cardIndex));
                cardIndex++;
            }
            state["hand"] = hand;

            // Pile counts
            state["draw_pile_count"] = combatState.DrawPile.Cards.Count;
            state["discard_pile_count"] = combatState.DiscardPile.Cards.Count;
            state["exhaust_pile_count"] = combatState.ExhaustPile.Cards.Count;

            // Pile contents
            state["draw_pile"] = BuildPileCardList(combatState.DrawPile.Cards, PileType.Draw);
            state["discard_pile"] = BuildPileCardList(combatState.DiscardPile.Cards, PileType.Discard);
            state["exhaust_pile"] = BuildPileCardList(combatState.ExhaustPile.Cards, PileType.Exhaust);

            // Orbs
            if (combatState.OrbQueue.Capacity > 0)
            {
                var orbs = new List<Dictionary<string, object?>>();
                foreach (var orb in combatState.OrbQueue.Orbs)
                {
                    // Populate SmartDescription placeholders with Focus-modified values,
                    // mirroring OrbModel.HoverTips getter (OrbModel.cs:92-94)
                    string? description = SafeGetText(() =>
                    {
                        var desc = orb.SmartDescription;
                        desc.Add("energyPrefix", orb.Owner.Character.CardPool.Title);
                        desc.Add("Passive", orb.PassiveVal);
                        desc.Add("Evoke", orb.EvokeVal);
                        return desc;
                    });
                    orbs.Add(new Dictionary<string, object?>
                    {
                        ["id"] = orb.Id.Entry,
                        ["name"] = SafeGetText(() => orb.Title),
                        ["description"] = description,
                        ["passive_val"] = orb.PassiveVal,
                        ["evoke_val"] = orb.EvokeVal,
                        ["keywords"] = BuildHoverTips(orb.HoverTips)
                    });
                }
                state["orbs"] = orbs;
                state["orb_slots"] = combatState.OrbQueue.Capacity;
                state["orb_empty_slots"] = combatState.OrbQueue.Capacity - combatState.OrbQueue.Orbs.Count;
            }
        }

        state["gold"] = player.Gold;

        // Powers (status effects)
        state["status"] = BuildPowersState(creature);

        // Relics
        var relics = new List<Dictionary<string, object?>>();
        foreach (var relic in player.Relics)
        {
            relics.Add(new Dictionary<string, object?>
            {
                ["id"] = relic.Id.Entry,
                ["name"] = SafeGetText(() => relic.Title),
                ["description"] = SafeGetText(() => relic.DynamicDescription),
                ["counter"] = relic.ShowCounter ? relic.DisplayAmount : null,
                ["keywords"] = BuildHoverTips(relic.HoverTipsExcludingRelic)
            });
        }
        state["relics"] = relics;

        // Potions
        var potions = new List<Dictionary<string, object?>>();
        int slotIndex = 0;
        foreach (var potion in player.PotionSlots)
        {
            if (potion != null)
            {
                potions.Add(new Dictionary<string, object?>
                {
                    ["id"] = potion.Id.Entry,
                    ["name"] = SafeGetText(() => potion.Title),
                    ["description"] = SafeGetText(() => potion.DynamicDescription),
                    ["slot"] = slotIndex,
                    ["can_use_in_combat"] = potion.Usage == PotionUsage.CombatOnly || potion.Usage == PotionUsage.AnyTime,
                    ["target_type"] = potion.TargetType.ToString(),
                    ["keywords"] = BuildHoverTips(potion.ExtraHoverTips)
                });
            }
            slotIndex++;
        }
        state["potions"] = potions;

        // Master deck (full card collection, always available)
        var deck = new List<Dictionary<string, object?>>();
        foreach (var card in player.Deck.Cards)
        {
            string costDisplay;
            if (card.EnergyCost.CostsX)
                costDisplay = "X";
            else
                costDisplay = card.EnergyCost.GetAmountToSpend().ToString();

            deck.Add(new Dictionary<string, object?>
            {
                ["id"] = card.Id.Entry,
                ["name"] = SafeGetText(() => card.Title),
                ["type"] = card.Type.ToString(),
                ["cost"] = costDisplay,
                ["description"] = SafeGetCardDescription(card),
                ["rarity"] = card.Rarity.ToString(),
                ["is_upgraded"] = card.IsUpgraded
            });
        }
        state["deck"] = deck;

        return state;
    }

    private static Dictionary<string, object?> BuildCardState(CardModel card, int index)
    {
        string costDisplay;
        if (card.EnergyCost.CostsX)
            costDisplay = "X";
        else
        {
            int cost = card.EnergyCost.GetAmountToSpend();
            costDisplay = cost.ToString();
        }

        card.CanPlay(out var unplayableReason, out _);

        // Star cost (The Regent's cards; CanonicalStarCost >= 0 means card has a star cost)
        string? starCostDisplay = null;
        if (card.HasStarCostX)
            starCostDisplay = "X";
        else if (card.CurrentStarCost >= 0)
            starCostDisplay = card.GetStarCostWithModifiers().ToString();

        return new Dictionary<string, object?>
        {
            ["index"] = index,
            ["id"] = card.Id.Entry,
            ["name"] = card.Title,
            ["type"] = card.Type.ToString(),
            ["cost"] = costDisplay,
            ["star_cost"] = starCostDisplay,
            ["description"] = SafeGetCardDescription(card),
            ["target_type"] = card.TargetType.ToString(),
            ["can_play"] = unplayableReason == UnplayableReason.None,
            ["unplayable_reason"] = unplayableReason != UnplayableReason.None ? unplayableReason.ToString() : null,
            ["is_upgraded"] = card.IsUpgraded,
            ["keywords"] = BuildHoverTips(card.HoverTips)
        };
    }

    private static List<Dictionary<string, object?>> BuildPileCardList(IEnumerable<CardModel> cards, PileType pile)
    {
        var list = new List<Dictionary<string, object?>>();
        foreach (var card in cards)
        {
            list.Add(new Dictionary<string, object?>
            {
                ["name"] = SafeGetText(() => card.Title),
                ["description"] = SafeGetCardDescription(card, pile)
            });
        }
        return list;
    }

    private static Dictionary<string, object?> BuildEnemyState(Creature creature, Dictionary<string, int> entityCounts)
    {
        var monster = creature.Monster;
        string baseId = monster?.Id.Entry ?? "unknown";

        // Generate entity_id like "jaw_worm_0"
        if (!entityCounts.TryGetValue(baseId, out int count))
            count = 0;
        entityCounts[baseId] = count + 1;
        string entityId = $"{baseId}_{count}";

        var state = new Dictionary<string, object?>
        {
            ["entity_id"] = entityId,
            ["combat_id"] = creature.CombatId,
            ["name"] = SafeGetText(() => monster?.Title),
            ["hp"] = creature.CurrentHp,
            ["max_hp"] = creature.MaxHp,
            ["block"] = creature.Block,
            ["status"] = BuildPowersState(creature)
        };

        // Intents
        if (monster?.NextMove is MoveState moveState)
        {
            var intents = new List<Dictionary<string, object?>>();
            foreach (var intent in moveState.Intents)
            {
                var intentData = new Dictionary<string, object?>
                {
                    ["type"] = intent.IntentType.ToString()
                };
                try
                {
                    var targets = creature.CombatState?.PlayerCreatures;
                    if (targets != null)
                    {
                        string label = intent.GetIntentLabel(targets, creature).GetFormattedText();
                        intentData["label"] = StripRichTextTags(label);

                        var hoverTip = intent.GetHoverTip(targets, creature);
                        if (hoverTip.Title != null)
                            intentData["title"] = StripRichTextTags(hoverTip.Title);
                        if (hoverTip.Description != null)
                            intentData["description"] = StripRichTextTags(hoverTip.Description);
                    }
                }
                catch { /* intent label may fail for some types */ }
                intents.Add(intentData);
            }
            state["intents"] = intents;
        }

        return state;
    }

    private static Dictionary<string, object?> BuildEventState(EventRoom eventRoom, RunState runState)
    {
        var state = new Dictionary<string, object?>();

        var player = LocalContext.GetMe(runState);
        if (player != null)
        {
            state["player"] = new Dictionary<string, object?>
            {
                ["character"] = SafeGetText(() => player.Character.Title),
                ["hp"] = player.Creature.CurrentHp,
                ["max_hp"] = player.Creature.MaxHp,
                ["gold"] = player.Gold
            };
        }

        var eventModel = eventRoom.CanonicalEvent;
        bool isAncient = eventModel is AncientEventModel;
        state["event_id"] = eventModel.Id.Entry;
        state["event_name"] = SafeGetText(() => eventModel.Title);
        state["is_ancient"] = isAncient;

        // Check dialogue state for ancients
        bool inDialogue = false;
        var uiRoom = NEventRoom.Instance;
        if (isAncient && uiRoom != null)
        {
            var ancientLayout = FindFirst<NAncientEventLayout>(uiRoom);
            if (ancientLayout != null)
            {
                var hitbox = ancientLayout.GetNodeOrNull<NClickableControl>("%DialogueHitbox");
                inDialogue = hitbox != null && hitbox.Visible && hitbox.IsEnabled;
            }
        }
        state["in_dialogue"] = inDialogue;

        // Event body text
        state["body"] = SafeGetText(() => eventModel.Description);

        // Options from UI
        var options = new List<Dictionary<string, object?>>();
        if (uiRoom != null)
        {
            var buttons = FindAll<NEventOptionButton>(uiRoom);
            int index = 0;
            foreach (var button in buttons)
            {
                var opt = button.Option;
                var optData = new Dictionary<string, object?>
                {
                    ["index"] = index,
                    ["title"] = SafeGetText(() => opt.Title),
                    ["description"] = SafeGetText(() => opt.Description),
                    ["is_locked"] = opt.IsLocked,
                    ["is_proceed"] = opt.IsProceed,
                    ["was_chosen"] = opt.WasChosen
                };
                if (opt.Relic != null)
                {
                    optData["relic_name"] = SafeGetText(() => opt.Relic.Title);
                    optData["relic_description"] = SafeGetText(() => opt.Relic.DynamicDescription);
                }
                optData["keywords"] = BuildHoverTips(opt.HoverTips);
                options.Add(optData);
                index++;
            }
        }
        state["options"] = options;

        return state;
    }

    private static Dictionary<string, object?> BuildRestSiteState(RestSiteRoom restSiteRoom, RunState runState)
    {
        var state = new Dictionary<string, object?>();

        var player = LocalContext.GetMe(runState);
        if (player != null)
        {
            state["player"] = new Dictionary<string, object?>
            {
                ["character"] = SafeGetText(() => player.Character.Title),
                ["hp"] = player.Creature.CurrentHp,
                ["max_hp"] = player.Creature.MaxHp,
                ["gold"] = player.Gold
            };
        }

        var options = new List<Dictionary<string, object?>>();
        int index = 0;
        foreach (var opt in restSiteRoom.Options)
        {
            options.Add(new Dictionary<string, object?>
            {
                ["index"] = index,
                ["id"] = opt.OptionId,
                ["name"] = SafeGetText(() => opt.Title),
                ["description"] = SafeGetText(() => opt.Description),
                ["is_enabled"] = opt.IsEnabled
            });
            index++;
        }
        state["options"] = options;

        var proceedButton = NRestSiteRoom.Instance?.ProceedButton;
        state["can_proceed"] = proceedButton?.IsEnabled ?? false;

        return state;
    }

    private static Dictionary<string, object?> BuildShopState(MerchantRoom merchantRoom, RunState runState)
    {
        var state = new Dictionary<string, object?>();

        var player = LocalContext.GetMe(runState);
        if (player != null)
        {
            state["player"] = new Dictionary<string, object?>
            {
                ["character"] = SafeGetText(() => player.Character.Title),
                ["hp"] = player.Creature.CurrentHp,
                ["max_hp"] = player.Creature.MaxHp,
                ["gold"] = player.Gold,
                ["potion_slots"] = player.PotionSlots.Count,
                ["open_potion_slots"] = player.PotionSlots.Count(s => s == null)
            };
        }

        var inventory = merchantRoom.Inventory;
        var items = new List<Dictionary<string, object?>>();
        int index = 0;

        // Cards
        foreach (var entry in inventory.CardEntries)
        {
            var item = new Dictionary<string, object?>
            {
                ["index"] = index,
                ["category"] = "card",
                ["cost"] = entry.Cost,
                ["is_stocked"] = entry.IsStocked,
                ["can_afford"] = entry.EnoughGold,
                ["on_sale"] = entry.IsOnSale
            };
            if (entry.CreationResult?.Card is { } card)
            {
                item["card_id"] = card.Id.Entry;
                item["card_name"] = SafeGetText(() => card.Title);
                item["card_type"] = card.Type.ToString();
                item["card_rarity"] = card.Rarity.ToString();
                item["card_description"] = SafeGetCardDescription(card, PileType.None);
                item["keywords"] = BuildHoverTips(card.HoverTips);
            }
            items.Add(item);
            index++;
        }

        // Relics
        foreach (var entry in inventory.RelicEntries)
        {
            var item = new Dictionary<string, object?>
            {
                ["index"] = index,
                ["category"] = "relic",
                ["cost"] = entry.Cost,
                ["is_stocked"] = entry.IsStocked,
                ["can_afford"] = entry.EnoughGold
            };
            if (entry.Model is { } relic)
            {
                item["relic_id"] = relic.Id.Entry;
                item["relic_name"] = SafeGetText(() => relic.Title);
                item["relic_description"] = SafeGetText(() => relic.DynamicDescription);
                item["keywords"] = BuildHoverTips(relic.HoverTipsExcludingRelic);
            }
            items.Add(item);
            index++;
        }

        // Potions
        foreach (var entry in inventory.PotionEntries)
        {
            var item = new Dictionary<string, object?>
            {
                ["index"] = index,
                ["category"] = "potion",
                ["cost"] = entry.Cost,
                ["is_stocked"] = entry.IsStocked,
                ["can_afford"] = entry.EnoughGold
            };
            if (entry.Model is { } potion)
            {
                item["potion_id"] = potion.Id.Entry;
                item["potion_name"] = SafeGetText(() => potion.Title);
                item["potion_description"] = SafeGetText(() => potion.DynamicDescription);
                item["keywords"] = BuildHoverTips(potion.ExtraHoverTips);
            }
            items.Add(item);
            index++;
        }

        // Card removal
        if (inventory.CardRemovalEntry is { } removal)
        {
            items.Add(new Dictionary<string, object?>
            {
                ["index"] = index,
                ["category"] = "card_removal",
                ["cost"] = removal.Cost,
                ["is_stocked"] = removal.IsStocked,
                ["can_afford"] = removal.EnoughGold
            });
        }

        state["items"] = items;

        var proceedButton = NMerchantRoom.Instance?.ProceedButton;
        state["can_proceed"] = proceedButton?.IsEnabled ?? false;

        return state;
    }

    private static Dictionary<string, object?> BuildMapState(RunState runState)
    {
        var state = new Dictionary<string, object?>();

        // Player summary
        var player = LocalContext.GetMe(runState);
        if (player != null)
        {
            int totalSlots = player.PotionSlots.Count;
            int openSlots = player.PotionSlots.Count(s => s == null);

            var relics = new List<Dictionary<string, object?>>();
            foreach (var relic in player.Relics)
            {
                relics.Add(new Dictionary<string, object?>
                {
                    ["id"] = relic.Id.Entry,
                    ["name"] = SafeGetText(() => relic.Title),
                    ["description"] = SafeGetText(() => relic.DynamicDescription),
                    ["counter"] = relic.ShowCounter ? relic.DisplayAmount : null,
                    ["keywords"] = BuildHoverTips(relic.HoverTipsExcludingRelic)
                });
            }

            var potions = new List<Dictionary<string, object?>>();
            int slotIndex = 0;
            foreach (var potion in player.PotionSlots)
            {
                if (potion != null)
                {
                    potions.Add(new Dictionary<string, object?>
                    {
                        ["id"] = potion.Id.Entry,
                        ["name"] = SafeGetText(() => potion.Title),
                        ["description"] = SafeGetText(() => potion.DynamicDescription),
                        ["slot"] = slotIndex,
                        ["can_use_in_combat"] = potion.Usage == PotionUsage.CombatOnly || potion.Usage == PotionUsage.AnyTime,
                        ["target_type"] = potion.TargetType.ToString(),
                        ["keywords"] = BuildHoverTips(potion.HoverTips)
                    });
                }
                slotIndex++;
            }

            state["player"] = new Dictionary<string, object?>
            {
                ["character"] = SafeGetText(() => player.Character.Title),
                ["hp"] = player.Creature.CurrentHp,
                ["max_hp"] = player.Creature.MaxHp,
                ["gold"] = player.Gold,
                ["potion_slots"] = totalSlots,
                ["open_potion_slots"] = openSlots,
                ["relics"] = relics,
                ["potions"] = potions
            };
        }

        var map = runState.Map;
        var visitedCoords = runState.VisitedMapCoords;

        // Current position
        if (visitedCoords.Count > 0)
        {
            var cur = visitedCoords[visitedCoords.Count - 1];
            state["current_position"] = new Dictionary<string, object?>
            {
                ["col"] = cur.col, ["row"] = cur.row,
                ["type"] = map.GetPoint(cur)?.PointType.ToString()
            };
        }

        // Visited path
        var visited = new List<Dictionary<string, object?>>();
        foreach (var coord in visitedCoords)
        {
            visited.Add(new Dictionary<string, object?>
            {
                ["col"] = coord.col, ["row"] = coord.row,
                ["type"] = map.GetPoint(coord)?.PointType.ToString()
            });
        }
        state["visited"] = visited;

        // Next options — read travelable state from UI nodes
        var nextOptions = new List<Dictionary<string, object?>>();
        var mapScreen = NMapScreen.Instance;
        if (mapScreen != null)
        {
            var travelable = FindAll<NMapPoint>(mapScreen)
                .Where(mp => mp.State == MapPointState.Travelable)
                .OrderBy(mp => mp.Point.coord.col)
                .ToList();

            int index = 0;
            foreach (var nmp in travelable)
            {
                var pt = nmp.Point;
                var option = new Dictionary<string, object?>
                {
                    ["index"] = index,
                    ["col"] = pt.coord.col,
                    ["row"] = pt.coord.row,
                    ["type"] = pt.PointType.ToString()
                };

                // 1-level lookahead
                var children = pt.Children
                    .OrderBy(c => c.coord.col)
                    .Select(c => new Dictionary<string, object?>
                    {
                        ["col"] = c.coord.col, ["row"] = c.coord.row,
                        ["type"] = c.PointType.ToString()
                    }).ToList();
                if (children.Count > 0)
                    option["leads_to"] = children;

                nextOptions.Add(option);
                index++;
            }
        }
        state["next_options"] = nextOptions;

        // Full map — all nodes organized for planning
        var nodes = new List<Dictionary<string, object?>>();

        // Starting point
        var start = map.StartingMapPoint;
        nodes.Add(BuildMapNode(start));

        // Grid nodes
        foreach (var pt in map.GetAllMapPoints())
            nodes.Add(BuildMapNode(pt));

        // Boss
        nodes.Add(BuildMapNode(map.BossMapPoint));
        if (map.SecondBossMapPoint != null)
            nodes.Add(BuildMapNode(map.SecondBossMapPoint));

        state["nodes"] = nodes;
        state["boss"] = new Dictionary<string, object?>
        {
            ["col"] = map.BossMapPoint.coord.col,
            ["row"] = map.BossMapPoint.coord.row
        };

        return state;
    }

    private static Dictionary<string, object?> BuildMapNode(MapPoint pt)
    {
        return new Dictionary<string, object?>
        {
            ["col"] = pt.coord.col,
            ["row"] = pt.coord.row,
            ["type"] = pt.PointType.ToString(),
            ["children"] = pt.Children
                .OrderBy(c => c.coord.col)
                .Select(c => new List<int> { c.coord.col, c.coord.row })
                .ToList()
        };
    }

    private static Dictionary<string, object?> BuildRewardsState(NRewardsScreen rewardsScreen, RunState runState)
    {
        var state = new Dictionary<string, object?>();

        // Player summary for decision-making context
        var player = LocalContext.GetMe(runState);
        if (player != null)
        {
            int totalSlots = player.PotionSlots.Count;
            int openSlots = player.PotionSlots.Count(s => s == null);
            state["player"] = new Dictionary<string, object?>
            {
                ["character"] = SafeGetText(() => player.Character.Title),
                ["hp"] = player.Creature.CurrentHp,
                ["max_hp"] = player.Creature.MaxHp,
                ["gold"] = player.Gold,
                ["potion_slots"] = totalSlots,
                ["open_potion_slots"] = openSlots
            };
        }

        // Reward items
        var rewardButtons = FindAll<NRewardButton>(rewardsScreen);
        var items = new List<Dictionary<string, object?>>();
        int index = 0;
        foreach (var button in rewardButtons)
        {
            if (button.Reward == null || !button.IsEnabled) continue;
            var reward = button.Reward;

            var item = new Dictionary<string, object?>
            {
                ["index"] = index,
                ["type"] = GetRewardTypeName(reward),
                ["description"] = SafeGetText(() => reward.Description)
            };

            // Type-specific details
            if (reward is GoldReward goldReward)
                item["gold_amount"] = goldReward.Amount;
            else if (reward is PotionReward potionReward && potionReward.Potion != null)
            {
                item["potion_id"] = potionReward.Potion.Id.Entry;
                item["potion_name"] = SafeGetText(() => potionReward.Potion.Title);
            }

            items.Add(item);
            index++;
        }
        state["items"] = items;

        // Proceed button
        var proceedButton = FindFirst<NProceedButton>(rewardsScreen);
        state["can_proceed"] = proceedButton?.IsEnabled ?? false;

        return state;
    }

    private static Dictionary<string, object?> BuildCardRewardState(NCardRewardSelectionScreen cardScreen)
    {
        var state = new Dictionary<string, object?>();

        var cardHolders = FindAllSortedByPosition<NCardHolder>(cardScreen);
        var cards = new List<Dictionary<string, object?>>();
        int index = 0;
        foreach (var holder in cardHolders)
        {
            var card = holder.CardModel;
            if (card == null) continue;

            string costDisplay = card.EnergyCost.CostsX
                ? "X"
                : card.EnergyCost.GetAmountToSpend().ToString();

            string? starCostDisplay = null;
            if (card.HasStarCostX)
                starCostDisplay = "X";
            else if (card.CurrentStarCost >= 0)
                starCostDisplay = card.GetStarCostWithModifiers().ToString();

            cards.Add(new Dictionary<string, object?>
            {
                ["index"] = index,
                ["id"] = card.Id.Entry,
                ["name"] = SafeGetText(() => card.Title),
                ["type"] = card.Type.ToString(),
                ["cost"] = costDisplay,
                ["star_cost"] = starCostDisplay,
                ["description"] = SafeGetCardDescription(card, PileType.None),
                ["rarity"] = card.Rarity.ToString(),
                ["is_upgraded"] = card.IsUpgraded,
                ["keywords"] = BuildHoverTips(card.HoverTips)
            });
            index++;
        }
        state["cards"] = cards;

        var altButtons = FindAll<NCardRewardAlternativeButton>(cardScreen);
        state["can_skip"] = altButtons.Count > 0;

        return state;
    }

    private static Dictionary<string, object?> BuildCardSelectState(NCardGridSelectionScreen screen, RunState runState)
    {
        var state = new Dictionary<string, object?>();

        // Screen type
        state["screen_type"] = screen switch
        {
            NDeckTransformSelectScreen => "transform",
            NDeckUpgradeSelectScreen => "upgrade",
            NDeckCardSelectScreen => "select",
            NSimpleCardSelectScreen => "simple_select",
            _ => screen.GetType().Name
        };

        // Player summary
        var player = LocalContext.GetMe(runState);
        if (player != null)
        {
            state["player"] = new Dictionary<string, object?>
            {
                ["character"] = SafeGetText(() => player.Character.Title),
                ["hp"] = player.Creature.CurrentHp,
                ["max_hp"] = player.Creature.MaxHp,
                ["gold"] = player.Gold
            };
        }

        // Prompt text from UI label
        var bottomLabel = screen.GetNodeOrNull("%BottomLabel");
        if (bottomLabel != null)
        {
            var textVariant = bottomLabel.Get("text");
            string? prompt = textVariant.VariantType != Godot.Variant.Type.Nil ? StripRichTextTags(textVariant.AsString()) : null;
            state["prompt"] = prompt;
        }

        // Cards in the grid (sorted by visual position — MoveToFront can reorder children)
        var cardHolders = FindAllSortedByPosition<NGridCardHolder>(screen);
        var cards = new List<Dictionary<string, object?>>();
        int index = 0;
        foreach (var holder in cardHolders)
        {
            var card = holder.CardModel;
            if (card == null) continue;

            cards.Add(new Dictionary<string, object?>
            {
                ["index"] = index,
                ["id"] = card.Id.Entry,
                ["name"] = SafeGetText(() => card.Title),
                ["type"] = card.Type.ToString(),
                ["cost"] = card.EnergyCost.CostsX ? "X" : card.EnergyCost.GetAmountToSpend().ToString(),
                ["description"] = SafeGetCardDescription(card, PileType.None),
                ["rarity"] = card.Rarity.ToString(),
                ["is_upgraded"] = card.IsUpgraded,
                ["keywords"] = BuildHoverTips(card.HoverTips)
            });
            index++;
        }
        state["cards"] = cards;

        // Preview container showing? (selection complete, awaiting confirm)
        // Upgrade screens use UpgradeSinglePreviewContainer / UpgradeMultiPreviewContainer
        var previewSingle = screen.GetNodeOrNull<Godot.Control>("%UpgradeSinglePreviewContainer");
        var previewMulti = screen.GetNodeOrNull<Godot.Control>("%UpgradeMultiPreviewContainer");
        var previewGeneric = screen.GetNodeOrNull<Godot.Control>("%PreviewContainer");
        bool previewShowing = (previewSingle?.Visible ?? false)
                            || (previewMulti?.Visible ?? false)
                            || (previewGeneric?.Visible ?? false);
        state["preview_showing"] = previewShowing;

        // Button states
        var closeButton = screen.GetNodeOrNull<NBackButton>("%Close");
        state["can_cancel"] = closeButton?.IsEnabled ?? false;

        // Confirm button — search all preview containers and main screen
        bool canConfirm = false;
        foreach (var container in new[] { previewSingle, previewMulti, previewGeneric })
        {
            if (container?.Visible == true)
            {
                var confirm = container.GetNodeOrNull<NConfirmButton>("Confirm")
                              ?? container.GetNodeOrNull<NConfirmButton>("%PreviewConfirm");
                if (confirm?.IsEnabled == true) { canConfirm = true; break; }
            }
        }
        if (!canConfirm)
        {
            var mainConfirm = screen.GetNodeOrNull<NConfirmButton>("Confirm")
                              ?? screen.GetNodeOrNull<NConfirmButton>("%Confirm");
            if (mainConfirm?.IsEnabled == true) canConfirm = true;
        }
        // Fallback: search entire screen tree for any enabled confirm button
        // (covers subclasses like NDeckEnchantSelectScreen)
        if (!canConfirm)
        {
            canConfirm = FindAll<NConfirmButton>(screen).Any(b => b.IsEnabled && b.IsVisibleInTree());
        }
        state["can_confirm"] = canConfirm;

        return state;
    }

    private static Dictionary<string, object?> BuildChooseCardState(NChooseACardSelectionScreen screen, RunState runState)
    {
        var state = new Dictionary<string, object?>();
        state["screen_type"] = "choose";

        var player = LocalContext.GetMe(runState);
        if (player != null)
        {
            state["player"] = new Dictionary<string, object?>
            {
                ["character"] = SafeGetText(() => player.Character.Title),
                ["hp"] = player.Creature.CurrentHp,
                ["max_hp"] = player.Creature.MaxHp,
                ["gold"] = player.Gold
            };
        }

        state["prompt"] = "Choose a card.";

        var cardHolders = FindAllSortedByPosition<NGridCardHolder>(screen);
        var cards = new List<Dictionary<string, object?>>();
        int index = 0;
        foreach (var holder in cardHolders)
        {
            var card = holder.CardModel;
            if (card == null) continue;

            cards.Add(new Dictionary<string, object?>
            {
                ["index"] = index,
                ["id"] = card.Id.Entry,
                ["name"] = SafeGetText(() => card.Title),
                ["type"] = card.Type.ToString(),
                ["cost"] = card.EnergyCost.CostsX ? "X" : card.EnergyCost.GetAmountToSpend().ToString(),
                ["description"] = SafeGetCardDescription(card, PileType.None),
                ["rarity"] = card.Rarity.ToString(),
                ["is_upgraded"] = card.IsUpgraded,
                ["keywords"] = BuildHoverTips(card.HoverTips)
            });
            index++;
        }
        state["cards"] = cards;

        var skipButton = screen.GetNodeOrNull<NClickableControl>("SkipButton");
        state["can_skip"] = skipButton?.IsEnabled == true && skipButton.Visible;
        state["preview_showing"] = false;
        state["can_confirm"] = false;
        state["can_cancel"] = state["can_skip"];

        return state;
    }

    private static Dictionary<string, object?> BuildHandSelectState(NPlayerHand hand, RunState runState)
    {
        var state = new Dictionary<string, object?>();

        // Mode
        state["mode"] = hand.CurrentMode switch
        {
            NPlayerHand.Mode.SimpleSelect => "simple_select",
            NPlayerHand.Mode.UpgradeSelect => "upgrade_select",
            _ => hand.CurrentMode.ToString()
        };

        // Prompt text from %SelectionHeader
        var headerLabel = hand.GetNodeOrNull<Godot.Control>("%SelectionHeader");
        if (headerLabel != null)
        {
            var textVariant = headerLabel.Get("text");
            string? prompt = textVariant.VariantType != Godot.Variant.Type.Nil
                ? StripRichTextTags(textVariant.AsString())
                : null;
            state["prompt"] = prompt;
        }

        // Selectable cards (visible holders in the hand)
        var selectableCards = new List<Dictionary<string, object?>>();
        int index = 0;
        foreach (var holder in hand.ActiveHolders)
        {
            var card = holder.CardModel;
            if (card == null) continue;

            selectableCards.Add(new Dictionary<string, object?>
            {
                ["index"] = index,
                ["id"] = card.Id.Entry,
                ["name"] = SafeGetText(() => card.Title),
                ["type"] = card.Type.ToString(),
                ["cost"] = card.EnergyCost.CostsX ? "X" : card.EnergyCost.GetAmountToSpend().ToString(),
                ["description"] = SafeGetCardDescription(card),
                ["is_upgraded"] = card.IsUpgraded,
                ["keywords"] = BuildHoverTips(card.HoverTips)
            });
            index++;
        }
        state["cards"] = selectableCards;

        // Already-selected cards (in the SelectedHandCardContainer)
        var selectedContainer = hand.GetNodeOrNull<Godot.Control>("%SelectedHandCardContainer");
        if (selectedContainer != null)
        {
            var selectedCards = new List<Dictionary<string, object?>>();
            var selectedHolders = FindAll<NSelectedHandCardHolder>(selectedContainer);
            int selIdx = 0;
            foreach (var holder in selectedHolders)
            {
                var card = holder.CardModel;
                if (card == null) continue;
                selectedCards.Add(new Dictionary<string, object?>
                {
                    ["index"] = selIdx,
                    ["name"] = SafeGetText(() => card.Title)
                });
                selIdx++;
            }
            if (selectedCards.Count > 0)
                state["selected_cards"] = selectedCards;
        }

        // Confirm button state
        var confirmBtn = hand.GetNodeOrNull<NConfirmButton>("%SelectModeConfirmButton");
        state["can_confirm"] = confirmBtn?.IsEnabled ?? false;

        return state;
    }

    private static Dictionary<string, object?> BuildRelicSelectState(NChooseARelicSelection screen, RunState runState)
    {
        var state = new Dictionary<string, object?>();

        var player = LocalContext.GetMe(runState);
        if (player != null)
        {
            state["player"] = new Dictionary<string, object?>
            {
                ["character"] = SafeGetText(() => player.Character.Title),
                ["hp"] = player.Creature.CurrentHp,
                ["max_hp"] = player.Creature.MaxHp,
                ["gold"] = player.Gold
            };
        }

        state["prompt"] = "Choose a relic.";

        var relicHolders = FindAll<NRelicBasicHolder>(screen);
        var relics = new List<Dictionary<string, object?>>();
        int index = 0;
        foreach (var holder in relicHolders)
        {
            var relic = holder.Relic?.Model;
            if (relic == null) continue;

            relics.Add(new Dictionary<string, object?>
            {
                ["index"] = index,
                ["id"] = relic.Id.Entry,
                ["name"] = SafeGetText(() => relic.Title),
                ["description"] = SafeGetText(() => relic.DynamicDescription),
                ["keywords"] = BuildHoverTips(relic.HoverTipsExcludingRelic)
            });
            index++;
        }
        state["relics"] = relics;

        var skipButton = screen.GetNodeOrNull<NClickableControl>("SkipButton");
        state["can_skip"] = skipButton?.IsEnabled == true && skipButton.Visible;

        return state;
    }

    private static Dictionary<string, object?> BuildTreasureState(TreasureRoom treasureRoom, RunState runState)
    {
        var state = new Dictionary<string, object?>();

        var player = LocalContext.GetMe(runState);
        if (player != null)
        {
            state["player"] = new Dictionary<string, object?>
            {
                ["character"] = SafeGetText(() => player.Character.Title),
                ["hp"] = player.Creature.CurrentHp,
                ["max_hp"] = player.Creature.MaxHp,
                ["gold"] = player.Gold
            };
        }

        var treasureUI = FindFirst<NTreasureRoom>(
            ((Godot.SceneTree)Godot.Engine.GetMainLoop()).Root);

        if (treasureUI == null)
        {
            state["message"] = "Treasure room loading...";
            return state;
        }

        // Auto-open chest if not yet opened
        var chestButton = treasureUI.GetNodeOrNull<NClickableControl>("Chest");
        if (chestButton is { IsEnabled: true })
        {
            chestButton.ForceClick();
            state["message"] = "Opening chest...";
            return state;
        }

        // Show relics available for picking
        var relicCollection = treasureUI.GetNodeOrNull<NTreasureRoomRelicCollection>("%RelicCollection");
        if (relicCollection?.Visible == true)
        {
            var holders = FindAll<NTreasureRoomRelicHolder>(relicCollection)
                .Where(h => h.IsEnabled && h.Visible)
                .ToList();

            var relics = new List<Dictionary<string, object?>>();
            int index = 0;
            foreach (var holder in holders)
            {
                var relic = holder.Relic?.Model;
                if (relic == null) continue;
                relics.Add(new Dictionary<string, object?>
                {
                    ["index"] = index,
                    ["id"] = relic.Id.Entry,
                    ["name"] = SafeGetText(() => relic.Title),
                    ["description"] = SafeGetText(() => relic.DynamicDescription),
                    ["rarity"] = relic.Rarity.ToString(),
                    ["keywords"] = BuildHoverTips(relic.HoverTipsExcludingRelic)
                });
                index++;
            }
            state["relics"] = relics;
        }

        state["can_proceed"] = treasureUI.ProceedButton?.IsEnabled ?? false;

        return state;
    }

    private static string GetRewardTypeName(Reward reward) => reward switch
    {
        GoldReward => "gold",
        PotionReward => "potion",
        RelicReward => "relic",
        CardReward => "card",
        SpecialCardReward => "special_card",
        CardRemovalReward => "card_removal",
        _ => reward.GetType().Name.ToLower()
    };

    private static List<Dictionary<string, object?>> BuildPowersState(Creature creature)
    {
        var powers = new List<Dictionary<string, object?>>();
        foreach (var power in creature.Powers)
        {
            if (!power.IsVisible) continue;

            // HoverTips resolves all dynamic vars (Amount, DynamicVars, etc.)
            // The first tip is the power's own description; the rest are extra keywords
            var allTips = power.HoverTips.ToList();
            string? resolvedDesc = null;
            var extraTips = new List<IHoverTip>();
            foreach (var tip in allTips)
            {
                if (tip.Id == power.Id.ToString())
                {
                    // This is the power's own hover tip — extract its resolved description
                    if (tip is HoverTip ht)
                        resolvedDesc = StripRichTextTags(ht.Description);
                }
                else
                {
                    extraTips.Add(tip);
                }
            }
            // Fallback to raw SmartDescription if HoverTips extraction failed
            resolvedDesc ??= SafeGetText(() => power.SmartDescription);

            powers.Add(new Dictionary<string, object?>
            {
                ["id"] = power.Id.Entry,
                ["name"] = SafeGetText(() => power.Title),
                ["amount"] = power.DisplayAmount,
                ["type"] = power.Type.ToString(),
                ["description"] = resolvedDesc,
                ["keywords"] = BuildHoverTips(extraTips)
            });
        }
        return powers;
    }

    internal static object BuildGlossaryCards()
    {
        if (!RunManager.Instance.IsInProgress)
            return new Dictionary<string, object?> { ["error"] = "No run in progress." };

        var runState = RunManager.Instance.DebugOnlyGetState();
        if (runState == null)
            return new Dictionary<string, object?> { ["error"] = "Could not read run state." };

        var result = new List<Dictionary<string, object?>>();
        var seen = new HashSet<string>();

        foreach (var player in runState.Players)
        {
            var pool = player.Character?.CardPool;
            if (pool == null) continue;
            var poolName = SafeGetText(() => pool.Title) ?? "Unknown";

            foreach (var card in pool.AllCards)
            {
                var id = card.Id.Entry;
                if (seen.Contains(id)) continue;
                seen.Add(id);

                string costDisplay;
                if (card.EnergyCost.CostsX)
                    costDisplay = "X";
                else
                    costDisplay = card.EnergyCost.GetAmountToSpend().ToString();

                result.Add(new Dictionary<string, object?>
                {
                    ["id"] = id,
                    ["name"] = SafeGetText(() => card.Title),
                    ["type"] = card.Type.ToString(),
                    ["cost"] = costDisplay,
                    ["description"] = SafeGetCardDescription(card),
                    ["rarity"] = card.Rarity.ToString(),
                    ["pool"] = poolName,
                    ["keywords"] = BuildHoverTips(card.HoverTips)
                });
            }
        }

        return result;
    }

    internal static object BuildGlossaryRelics()
    {
        if (!RunManager.Instance.IsInProgress)
            return new Dictionary<string, object?> { ["error"] = "No run in progress." };

        var runState = RunManager.Instance.DebugOnlyGetState();
        if (runState == null)
            return new Dictionary<string, object?> { ["error"] = "Could not read run state." };

        var result = new List<Dictionary<string, object?>>();
        var seen = new HashSet<string>();

        // Get relics from player's character pool
        foreach (var player in runState.Players)
        {
            var pool = player.Character?.RelicPool;
            if (pool == null) continue;
            var poolName = SafeGetText(() => player.Character.Title) ?? "Unknown";

            foreach (var relic in pool.AllRelics)
            {
                var id = relic.Id.Entry;
                if (seen.Contains(id)) continue;
                seen.Add(id);

                result.Add(new Dictionary<string, object?>
                {
                    ["id"] = id,
                    ["name"] = SafeGetText(() => relic.Title),
                    ["description"] = SafeGetText(() => relic.DynamicDescription),
                    ["rarity"] = relic.Rarity.ToString(),
                    ["pool"] = poolName,
                    ["keywords"] = BuildHoverTips(relic.HoverTipsExcludingRelic)
                });
            }
        }

        // Get shared relics from the grab bag (all relics available in this run)
        var grabBag = runState.SharedRelicGrabBag;
        if (grabBag != null && grabBag.IsPopulated)
        {
            // The grab bag doesn't expose a list, but we can get relics from the player's owned list
            // Fall back to enumerating all RelicModel subtypes with CanonicalInstance
        }

        // Enumerate all concrete RelicModel subtypes for a complete list
        foreach (var type in typeof(RelicModel).Assembly.GetTypes())
        {
            if (type.IsAbstract || !type.IsSubclassOf(typeof(RelicModel))) continue;
            try
            {
                var instance = (RelicModel)System.Activator.CreateInstance(type)!;
                if (instance.CanonicalInstance is not { } canonical) continue;
                var id = canonical.Id.Entry;
                if (seen.Contains(id)) continue;
                seen.Add(id);

                result.Add(new Dictionary<string, object?>
                {
                    ["id"] = id,
                    ["name"] = SafeGetText(() => canonical.Title),
                    ["description"] = SafeGetText(() => canonical.DynamicDescription),
                    ["rarity"] = canonical.Rarity.ToString(),
                    ["pool"] = canonical.Pool?.Id.Category ?? "Shared",
                    ["keywords"] = BuildHoverTips(canonical.HoverTipsExcludingRelic)
                });
            }
            catch { }
        }

        return result;
    }

    internal static object BuildGlossaryPotions()
    {
        if (!RunManager.Instance.IsInProgress)
            return new Dictionary<string, object?> { ["error"] = "No run in progress." };

        var runState = RunManager.Instance.DebugOnlyGetState();
        if (runState == null)
            return new Dictionary<string, object?> { ["error"] = "Could not read run state." };

        var result = new List<Dictionary<string, object?>>();
        var seen = new HashSet<string>();

        foreach (var player in runState.Players)
        {
            var pool = player.Character?.PotionPool;
            if (pool == null) continue;
            var poolName = SafeGetText(() => player.Character.Title) ?? "Unknown";

            foreach (var potion in pool.AllPotions)
            {
                var id = potion.Id.Entry;
                if (seen.Contains(id)) continue;
                seen.Add(id);

                result.Add(new Dictionary<string, object?>
                {
                    ["id"] = id,
                    ["name"] = SafeGetText(() => potion.Title),
                    ["description"] = SafeGetText(() => potion.DynamicDescription),
                    ["rarity"] = potion.Rarity.ToString(),
                    ["target_type"] = potion.TargetType.ToString(),
                    ["usage"] = potion.Usage.ToString(),
                    ["pool"] = poolName,
                    ["keywords"] = BuildHoverTips(potion.ExtraHoverTips)
                });
            }
        }

        return result;
    }

    internal static object BuildGlossaryKeywords()
    {
        if (!RunManager.Instance.IsInProgress)
            return new Dictionary<string, object?> { ["error"] = "No run in progress." };

        var runState = RunManager.Instance.DebugOnlyGetState();
        if (runState == null)
            return new Dictionary<string, object?> { ["error"] = "Could not read run state." };

        var keywords = new Dictionary<string, string>();

        foreach (var player in runState.Players)
        {
            // From cards
            var cardPool = player.Character?.CardPool;
            if (cardPool != null)
            {
                foreach (var card in cardPool.AllCards)
                    foreach (var tip in card.HoverTips)
                        if (tip is HoverTip ht)
                        {
                            var title = SafeGetText(() => ht.Title);
                            if (!string.IsNullOrEmpty(title))
                                keywords[title!] = SafeGetText(() => ht.Description) ?? "";
                        }
            }

            // From relics
            var relicPool = player.Character?.RelicPool;
            if (relicPool != null)
            {
                foreach (var relic in relicPool.AllRelics)
                    foreach (var tip in relic.HoverTips)
                        if (tip is HoverTip ht)
                        {
                            var title = SafeGetText(() => ht.Title);
                            if (!string.IsNullOrEmpty(title))
                                keywords[title!] = SafeGetText(() => ht.Description) ?? "";
                        }
            }

            // From potions
            var potionPool = player.Character?.PotionPool;
            if (potionPool != null)
            {
                foreach (var potion in potionPool.AllPotions)
                    foreach (var tip in potion.HoverTips)
                        if (tip is HoverTip ht)
                        {
                            var title = SafeGetText(() => ht.Title);
                            if (!string.IsNullOrEmpty(title))
                                keywords[title!] = SafeGetText(() => ht.Description) ?? "";
                        }
            }
        }

        var result = new List<Dictionary<string, object?>>();
        foreach (var kv in keywords.OrderBy(k => k.Key))
        {
            result.Add(new Dictionary<string, object?>
            {
                ["name"] = kv.Key,
                ["description"] = kv.Value
            });
        }

        return result;
    }

    internal static object BuildProfile()
    {
        var progress = SaveManager.Instance?.Progress;
        if (progress == null)
            return new Dictionary<string, object?> { ["error"] = "No profile data available." };

        var result = new Dictionary<string, object?>();

        // Character stats
        var characters = new List<Dictionary<string, object?>>();
        foreach (var kv in progress.CharacterStats)
        {
            var stats = kv.Value;
            characters.Add(new Dictionary<string, object?>
            {
                ["id"] = kv.Key.Entry,
                ["max_ascension"] = stats.MaxAscension,
                ["preferred_ascension"] = stats.PreferredAscension,
                ["total_wins"] = stats.TotalWins,
                ["total_losses"] = stats.TotalLosses,
                ["fastest_win_time"] = stats.FastestWinTime,
                ["best_win_streak"] = stats.BestWinStreak,
                ["current_win_streak"] = stats.CurrentWinStreak,
                ["playtime"] = stats.Playtime
            });
        }
        result["characters"] = characters;

        // Card stats (pick/skip/win/loss rates)
        var cards = new List<Dictionary<string, object?>>();
        foreach (var kv in progress.CardStats)
        {
            var stats = kv.Value;
            cards.Add(new Dictionary<string, object?>
            {
                ["id"] = kv.Key.Entry,
                ["times_picked"] = stats.TimesPicked,
                ["times_skipped"] = stats.TimesSkipped,
                ["times_won"] = stats.TimesWon,
                ["times_lost"] = stats.TimesLost
            });
        }
        result["card_stats"] = cards;

        // Encounter stats (with per-character breakdown)
        var encounters = new List<Dictionary<string, object?>>();
        foreach (var kv in progress.EncounterStats)
        {
            var enc = new Dictionary<string, object?>
            {
                ["id"] = kv.Key.Entry,
                ["total_wins"] = kv.Value.TotalWins,
                ["total_losses"] = kv.Value.TotalLosses
            };
            var fightStats = new List<Dictionary<string, object?>>();
            foreach (var fs in kv.Value.FightStats)
            {
                fightStats.Add(new Dictionary<string, object?>
                {
                    ["character"] = fs.Character.Entry,
                    ["wins"] = fs.Wins,
                    ["losses"] = fs.Losses
                });
            }
            if (fightStats.Count > 0)
                enc["by_character"] = fightStats;
            encounters.Add(enc);
        }
        result["encounter_stats"] = encounters;

        // Enemy stats (with per-character breakdown)
        var enemies = new List<Dictionary<string, object?>>();
        foreach (var kv in progress.EnemyStats)
        {
            var enemy = new Dictionary<string, object?>
            {
                ["id"] = kv.Key.Entry,
                ["total_wins"] = kv.Value.TotalWins,
                ["total_losses"] = kv.Value.TotalLosses
            };
            var fightStats = new List<Dictionary<string, object?>>();
            foreach (var fs in kv.Value.FightStats)
            {
                fightStats.Add(new Dictionary<string, object?>
                {
                    ["character"] = fs.Character.Entry,
                    ["wins"] = fs.Wins,
                    ["losses"] = fs.Losses
                });
            }
            if (fightStats.Count > 0)
                enemy["by_character"] = fightStats;
            enemies.Add(enemy);
        }
        result["enemy_stats"] = enemies;

        // Ancient stats
        var ancients = new List<Dictionary<string, object?>>();
        foreach (var kv in progress.AncientStats)
        {
            var anc = new Dictionary<string, object?>
            {
                ["id"] = kv.Key.Entry,
                ["total_visits"] = kv.Value.TotalVisits,
                ["total_wins"] = kv.Value.TotalWins,
                ["total_losses"] = kv.Value.TotalLosses
            };
            var charStats = new List<Dictionary<string, object?>>();
            foreach (var cs in kv.Value.CharStats)
            {
                charStats.Add(new Dictionary<string, object?>
                {
                    ["character"] = cs.Character.Entry,
                    ["wins"] = cs.Wins,
                    ["losses"] = cs.Losses
                });
            }
            if (charStats.Count > 0)
                anc["by_character"] = charStats;
            ancients.Add(anc);
        }
        result["ancient_stats"] = ancients;

        // Discovered items
        result["discovered_cards"] = progress.DiscoveredCards.Select(id => id.Entry).ToList();
        result["discovered_relics"] = progress.DiscoveredRelics.Select(id => id.Entry).ToList();
        result["discovered_potions"] = progress.DiscoveredPotions.Select(id => id.Entry).ToList();
        result["discovered_events"] = progress.DiscoveredEvents.Select(id => id.Entry).ToList();
        result["discovered_acts"] = progress.DiscoveredActs.Select(id => id.Entry).ToList();

        // Achievements
        var achievements = new List<Dictionary<string, object?>>();
        foreach (var kv in progress.UnlockedAchievements)
        {
            achievements.Add(new Dictionary<string, object?>
            {
                ["id"] = kv.Key,
                ["unlocked_at"] = kv.Value
            });
        }
        result["achievements"] = achievements;

        // Epochs (progression milestones)
        result["epochs"] = progress.Epochs.Select(e => new Dictionary<string, object?>
        {
            ["id"] = e.Id,
            ["state"] = e.State.ToString(),
            ["obtained"] = e.ObtainDate
        }).ToList();

        // Global stats
        result["total_playtime"] = progress.TotalPlaytime;
        result["total_unlocks"] = progress.TotalUnlocks;
        result["current_score"] = progress.CurrentScore;
        result["floors_climbed"] = progress.FloorsClimbed;
        result["architect_damage"] = progress.ArchitectDamage;
        result["total_wins"] = progress.Wins;
        result["total_losses"] = progress.Losses;
        result["fastest_victory"] = progress.FastestVictory;
        result["best_win_streak"] = progress.BestWinStreak;
        result["number_of_runs"] = progress.NumberOfRuns;

        return result;
    }

    internal static object BuildBestiary()
    {
        var result = new Dictionary<string, object?>();
        var bindFlags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

        // All monsters — use reflection to read properties without full instantiation
        var monsters = new List<Dictionary<string, object?>>();
        foreach (var type in typeof(MonsterModel).Assembly.GetTypes())
        {
            if (type.IsAbstract || !type.IsSubclassOf(typeof(MonsterModel)) || type.FullName!.Contains("+")) continue;

            var entry = new Dictionary<string, object?>
            {
                ["id"] = ModelId.SlugifyCategory(type.Name),
                ["class"] = type.Name,
            };

            // Try to get HP from overridden properties
            try
            {
                var instance = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(type);
                var minHp = type.GetProperty("MinInitialHp")?.GetValue(instance);
                var maxHp = type.GetProperty("MaxInitialHp")?.GetValue(instance);
                if (minHp != null) entry["min_hp"] = minHp;
                if (maxHp != null) entry["max_hp"] = maxHp;
            }
            catch { }

            // Get move names from method signatures
            var moves = new List<string>();
            foreach (var m in type.GetMethods(bindFlags))
            {
                if (m.Name.EndsWith("Move") && m.DeclaringType == type
                    && m.Name != "PerformMove" && m.Name != "RollMove"
                    && m.Name != "SetMoveImmediate")
                    moves.Add(m.Name.Replace("Move", ""));
            }
            if (moves.Count > 0)
                entry["moves"] = moves;

            monsters.Add(entry);
        }
        result["monsters"] = monsters;

        // All encounters — use reflection
        var encounters = new List<Dictionary<string, object?>>();
        foreach (var type in typeof(EncounterModel).Assembly.GetTypes())
        {
            if (type.IsAbstract || !type.IsSubclassOf(typeof(EncounterModel)) || type.FullName!.Contains("+")) continue;

            var entry = new Dictionary<string, object?>
            {
                ["id"] = ModelId.SlugifyCategory(type.Name),
                ["class"] = type.Name,
            };

            try
            {
                var instance = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(type);
                var roomType = type.GetProperty("RoomType")?.GetValue(instance);
                var isWeak = type.GetProperty("IsWeak")?.GetValue(instance);
                var minGold = type.GetProperty("MinGoldReward")?.GetValue(instance);
                var maxGold = type.GetProperty("MaxGoldReward")?.GetValue(instance);
                if (roomType != null) entry["room_type"] = roomType.ToString();
                if (isWeak != null) entry["is_weak"] = isWeak;
                if (minGold != null) entry["min_gold"] = minGold;
                if (maxGold != null) entry["max_gold"] = maxGold;
            }
            catch { }

            // Get possible monsters from AllPossibleMonsters property override
            try
            {
                var allMonstersMethod = type.GetProperty("AllPossibleMonsters");
                if (allMonstersMethod != null)
                {
                    // Read the method body to find monster type references
                    var monsterTypes = new List<string>();
                    foreach (var m in type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                    {
                        if (m.Name == "GenerateMonsters" && m.DeclaringType == type)
                        {
                            // Check constructor parameters or fields for monster references
                            break;
                        }
                    }
                    // Fall back: check fields that reference MonsterModel types
                    foreach (var f in type.GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                    {
                        if (f.FieldType.IsSubclassOf(typeof(MonsterModel)) || (f.FieldType.IsGenericType && f.FieldType.GetGenericArguments().Any(a => a.IsSubclassOf(typeof(MonsterModel)))))
                            monsterTypes.Add(f.FieldType.Name);
                    }
                    // Also check which monster types the encounter name suggests
                }
            }
            catch { }

            // Infer monsters from encounter name pattern (e.g., NibbitsWeak -> Nibbit)
            var baseName = type.Name.Replace("Normal", "").Replace("Weak", "").Replace("Elite", "").Replace("Boss", "");
            var matchingMonsters = new List<string>();
            foreach (var monsterEntry in monsters)
            {
                var mClass = monsterEntry["class"] as string ?? "";
                if (baseName.Contains(mClass) || mClass.Contains(baseName.TrimEnd('s')))
                    matchingMonsters.Add(mClass);
            }
            if (matchingMonsters.Count > 0)
                entry["likely_monsters"] = matchingMonsters;

            encounters.Add(entry);
        }
        result["encounters"] = encounters;

        return result;
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace STS2_MCP;

[ModInitializer("Initialize")]
public static partial class McpMod
{
    public const string Version = "0.1.0";

    private static HttpListener? _listener;
    private static Thread? _serverThread;
    private static readonly ConcurrentQueue<Action> _mainThreadQueue = new();
    internal static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static void Initialize()
    {
        try
        {
            // Connect to main thread process frame for action execution
            var tree = (SceneTree)Engine.GetMainLoop();
            tree.Connect(SceneTree.SignalName.ProcessFrame, Callable.From(ProcessMainThreadQueue));

            _listener = new HttpListener();
            try
            {
                // Try binding to all interfaces first (requires URL ACL: netsh http add urlacl url=http://+:15526/ user=Everyone)
                _listener.Prefixes.Add("http://+:15526/");
                _listener.Start();
            }
            catch
            {
                // Fall back to localhost-only if ACL not configured
                _listener = new HttpListener();
                _listener.Prefixes.Add("http://localhost:15526/");
                _listener.Prefixes.Add("http://127.0.0.1:15526/");
                _listener.Start();
            }

            _serverThread = new Thread(ServerLoop)
            {
                IsBackground = true,
                Name = "STS2_MCP_Server"
            };
            _serverThread.Start();

            GD.Print($"[STS2 MCP] v{Version} server started on http://localhost:15526/");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[STS2 MCP] Failed to start: {ex}");
        }
    }

    private static void ProcessMainThreadQueue()
    {
        int processed = 0;
        while (_mainThreadQueue.TryDequeue(out var action) && processed < 10)
        {
            try { action(); }
            catch (Exception ex) { GD.PrintErr($"[STS2 MCP] Main thread action error: {ex}"); }
            processed++;
        }
    }

    internal static Task<T> RunOnMainThread<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>();
        _mainThreadQueue.Enqueue(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    internal static Task RunOnMainThread(Action action)
    {
        var tcs = new TaskCompletionSource<bool>();
        _mainThreadQueue.Enqueue(() =>
        {
            try { action(); tcs.SetResult(true); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    private static void ServerLoop()
    {
        while (_listener?.IsListening == true)
        {
            try
            {
                var context = _listener.GetContext();
                // Handle each request asynchronously so we don't block the listener
                ThreadPool.QueueUserWorkItem(_ => HandleRequest(context));
            }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
        }
    }

    private static void HandleRequest(HttpListenerContext context)
    {
        try
        {
            var request = context.Request;
            var response = context.Response;
            response.Headers.Add("Access-Control-Allow-Origin", "*");
            response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

            if (request.HttpMethod == "OPTIONS")
            {
                response.StatusCode = 204;
                response.Close();
                return;
            }

            string path = request.Url?.AbsolutePath ?? "/";

            if (path == "/")
            {
                SendJson(response, new { message = $"Hello from STS2 MCP v{Version}", status = "ok" });
            }
            else if (path == "/api/v1/singleplayer")
            {
                // Hard-block singleplayer endpoint during multiplayer runs
                // to prevent calling the non-sync-safe end_turn path
                if (IsMultiplayerRun())
                {
                    SendError(response, 409,
                        "Multiplayer run is active. Use /api/v1/multiplayer instead.");
                    return;
                }

                if (request.HttpMethod == "GET")
                    HandleGetState(request, response);
                else if (request.HttpMethod == "POST")
                    HandlePostAction(request, response);
                else
                    SendError(response, 405, "Method not allowed");
            }
            else if (path == "/api/v1/multiplayer")
            {
                // Guard: reject multiplayer endpoint during singleplayer runs
                if (!IsMultiplayerRun())
                {
                    SendError(response, 409,
                        "Not in a multiplayer run. Use /api/v1/singleplayer instead.");
                    return;
                }

                if (request.HttpMethod == "GET")
                    HandleGetMultiplayerState(request, response);
                else if (request.HttpMethod == "POST")
                    HandlePostMultiplayerAction(request, response);
                else
                    SendError(response, 405, "Method not allowed");
            }
            else if (path == "/api/v1/settings")
            {
                if (request.HttpMethod == "GET")
                {
                    try
                    {
                        var dataTask = RunOnMainThread(() =>
                        {
                            var sm = SaveManager.Instance;
                            var settings = sm.SettingsSave;
                            var prefs = sm.PrefsSave;

                            return new Dictionary<string, object?>
                            {
                                ["display"] = new Dictionary<string, object?>
                                {
                                    ["fullscreen"] = settings.Fullscreen,
                                    ["resolution"] = $"{settings.WindowSize.X}x{settings.WindowSize.Y}",
                                    ["fps_limit"] = settings.FpsLimit,
                                    ["vsync"] = settings.VSync.ToString(),
                                    ["msaa"] = settings.Msaa,
                                    ["aspect_ratio"] = settings.AspectRatioSetting.ToString(),
                                    ["target_display"] = settings.TargetDisplay,
                                    ["limit_fps_background"] = settings.LimitFpsInBackground,
                                },
                                ["audio"] = new Dictionary<string, object?>
                                {
                                    ["master"] = settings.VolumeMaster,
                                    ["bgm"] = settings.VolumeBgm,
                                    ["sfx"] = settings.VolumeSfx,
                                    ["ambience"] = settings.VolumeAmbience,
                                },
                                ["gameplay"] = new Dictionary<string, object?>
                                {
                                    ["fast_mode"] = prefs.FastMode.ToString(),
                                    ["screen_shake"] = prefs.ScreenShakeOptionIndex,
                                    ["show_run_timer"] = prefs.ShowRunTimer,
                                    ["show_card_indices"] = prefs.ShowCardIndices,
                                    ["text_effects"] = prefs.TextEffectsEnabled,
                                    ["long_press"] = prefs.IsLongPressEnabled,
                                },
                                ["mods"] = new Dictionary<string, object?>
                                {
                                    ["enabled"] = settings.ModSettings?.PlayerAgreedToModLoading ?? false,
                                },
                                ["language"] = settings.Language,
                                ["skip_intro"] = settings.SkipIntroLogo,
                            };
                        });
                        SendJson(response, dataTask.GetAwaiter().GetResult());
                    }
                    catch (System.Exception ex)
                    {
                        SendError(response, 500, $"Failed to read settings: {ex.Message}");
                    }
                }
                else
                    SendError(response, 405, "Method not allowed");
            }
            else if (path == "/api/v1/profiles")
            {
                if (request.HttpMethod == "GET")
                    HandleGetProfiles(response);
                else if (request.HttpMethod == "POST")
                    HandlePostProfiles(request, response);
                else
                    SendError(response, 405, "Method not allowed");
            }
            else if (path == "/api/v1/profile")
            {
                if (request.HttpMethod == "GET")
                    HandleGetProfile(response);
                else
                    SendError(response, 405, "Method not allowed");
            }
            else if (path == "/api/v1/bestiary")
            {
                if (request.HttpMethod == "GET")
                {
                    try
                    {
                        var dataTask = RunOnMainThread(() => BuildBestiary());
                        var data = dataTask.GetAwaiter().GetResult();
                        SendJson(response, data);
                    }
                    catch (System.Exception ex)
                    {
                        SendError(response, 500, $"Failed to build bestiary: {ex.Message}");
                    }
                }
                else
                    SendError(response, 405, "Method not allowed");
            }
            else if (path == "/api/v1/glossary/cards")
            {
                if (request.HttpMethod == "GET")
                    HandleGetGlossaryCards(response);
                else
                    SendError(response, 405, "Method not allowed");
            }
            else if (path == "/api/v1/glossary/relics")
            {
                if (request.HttpMethod == "GET")
                    HandleGetGlossaryRelics(response);
                else
                    SendError(response, 405, "Method not allowed");
            }
            else if (path == "/api/v1/glossary/potions")
            {
                if (request.HttpMethod == "GET")
                    HandleGetGlossaryPotions(response);
                else
                    SendError(response, 405, "Method not allowed");
            }
            else if (path == "/api/v1/glossary/keywords")
            {
                if (request.HttpMethod == "GET")
                    HandleGetGlossaryKeywords(response);
                else
                    SendError(response, 405, "Method not allowed");
            }
            else
            {
                SendError(response, 404, "Not found");
            }
        }
        catch (Exception ex)
        {
            try
            {
                SendError(context.Response, 500, $"Internal error: {ex.Message}");
            }
            catch { /* response may already be closed */ }
        }
    }

    // Called on HTTP thread (not main thread) as a best-effort guard.
    // The try/catch handles race conditions during run transitions.
    // Authoritative checks happen inside RunOnMainThread lambdas.
    internal static bool IsMultiplayerRun()
    {
        try
        {
            return MegaCrit.Sts2.Core.Runs.RunManager.Instance.IsInProgress
                && MegaCrit.Sts2.Core.Runs.RunManager.Instance.NetService.Type.IsMultiplayer();
        }
        catch { return false; }
    }

    private static void HandleGetMultiplayerState(HttpListenerRequest request, HttpListenerResponse response)
    {
        string format = request.QueryString["format"] ?? "json";

        try
        {
            var stateTask = RunOnMainThread(() => BuildMultiplayerGameState());
            var state = stateTask.GetAwaiter().GetResult();

            if (format == "markdown")
            {
                string md = FormatAsMarkdown(state);
                SendText(response, md, "text/markdown");
            }
            else
            {
                SendJson(response, state);
            }
        }
        catch (Exception ex)
        {
            SendError(response, 500, $"Failed to read multiplayer game state: {ex.Message}");
        }
    }

    private static void HandlePostMultiplayerAction(HttpListenerRequest request, HttpListenerResponse response)
    {
        string body;
        using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
            body = reader.ReadToEnd();

        Dictionary<string, JsonElement>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body);
        }
        catch
        {
            SendError(response, 400, "Invalid JSON");
            return;
        }

        if (parsed == null || !parsed.TryGetValue("action", out var actionElem))
        {
            SendError(response, 400, "Missing 'action' field");
            return;
        }

        string action = actionElem.GetString() ?? "";

        try
        {
            var resultTask = RunOnMainThread(() => ExecuteMultiplayerAction(action, parsed));
            var result = resultTask.GetAwaiter().GetResult();
            SendJson(response, result);
        }
        catch (Exception ex)
        {
            SendError(response, 500, $"Multiplayer action failed: {ex.Message}");
        }
    }

    private static void HandleGetState(HttpListenerRequest request, HttpListenerResponse response)
    {
        string format = request.QueryString["format"] ?? "json";

        try
        {
            var stateTask = RunOnMainThread(() => BuildGameState());
            var state = stateTask.GetAwaiter().GetResult();

            if (format == "markdown")
            {
                string md = FormatAsMarkdown(state);
                SendText(response, md, "text/markdown");
            }
            else
            {
                SendJson(response, state);
            }
        }
        catch (Exception ex)
        {
            SendError(response, 500, $"Failed to read game state: {ex.Message}");
        }
    }

    private static void HandleGetProfiles(HttpListenerResponse response)
    {
        try
        {
            var dataTask = RunOnMainThread(() =>
            {
                var sm = SaveManager.Instance;
                var result = new Dictionary<string, object?>
                {
                    ["current_profile_id"] = sm.CurrentProfileId,
                    ["profiles"] = new List<Dictionary<string, object?>>()
                };

                // Check which profiles exist by looking for save directories
                var profiles = (List<Dictionary<string, object?>>)result["profiles"]!;
                for (int i = 1; i <= 3; i++)
                {
                    var profileData = new Dictionary<string, object?>
                    {
                        ["id"] = i,
                        ["is_current"] = i == sm.CurrentProfileId,
                    };

                    // Check if profile has progress data
                    try
                    {
                        var path = MegaCrit.Sts2.Core.Saves.Managers.ProgressSaveManager.GetProgressPathForProfile(i);
                        profileData["has_data"] = System.IO.File.Exists(path);
                        profileData["path"] = path;
                    }
                    catch
                    {
                        profileData["has_data"] = false;
                    }

                    profiles.Add(profileData);
                }

                return result;
            });
            var data = dataTask.GetAwaiter().GetResult();
            SendJson(response, data);
        }
        catch (System.Exception ex)
        {
            SendError(response, 500, $"Failed to get profiles: {ex.Message}");
        }
    }

    private static void HandlePostProfiles(HttpListenerRequest request, HttpListenerResponse response)
    {
        string body;
        using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
            body = reader.ReadToEnd();

        Dictionary<string, JsonElement>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body);
        }
        catch
        {
            SendError(response, 400, "Invalid JSON");
            return;
        }

        if (parsed == null || !parsed.TryGetValue("action", out var actionElem))
        {
            SendError(response, 400, "Missing 'action' field. Use: switch, delete");
            return;
        }

        string action = actionElem.GetString() ?? "";
        int profileId = parsed.TryGetValue("profile_id", out var idElem) ? idElem.GetInt32() : 0;

        try
        {
            var resultTask = RunOnMainThread(() =>
            {
                var sm = SaveManager.Instance;

                if (action == "switch")
                {
                    if (profileId < 1 || profileId > 3)
                        return new Dictionary<string, object?> { ["error"] = "profile_id must be 1-3" };
                    if (RunManager.Instance.IsInProgress)
                        return new Dictionary<string, object?> { ["error"] = "Cannot switch profiles during a run" };

                    // Use the proper UI flow: find profile screen and click the button
                    var tree = (Godot.Engine.GetMainLoop()) as Godot.SceneTree;
                    if (tree?.Root != null)
                    {
                        var profileScreen = FindFirst<MegaCrit.Sts2.Core.Nodes.Screens.ProfileScreen.NProfileScreen>(tree.Root);
                        if (profileScreen != null)
                        {
                            var buttons = profileScreen.GetType().GetField("_profileButtons",
                                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                                ?.GetValue(profileScreen) as System.Collections.IList;
                            if (buttons != null)
                            {
                                foreach (var btn in buttons)
                                {
                                    var btnId = btn.GetType().GetField("_profileId",
                                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                                        ?.GetValue(btn);
                                    if (btnId is int id && id == profileId)
                                    {
                                        var switchMethod = btn.GetType().GetMethod("SwitchToThisProfile",
                                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                                        switchMethod?.Invoke(btn, null);
                                        return new Dictionary<string, object?>
                                        {
                                            ["status"] = "ok",
                                            ["message"] = $"Switching to profile {profileId} (via UI)",
                                            ["current_profile_id"] = profileId
                                        };
                                    }
                                }
                            }
                        }

                        // Fallback: open profile screen first if not already open
                        var mainMenu = FindFirst<MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMainMenu>(tree.Root);
                        if (mainMenu != null)
                        {
                            mainMenu.OpenProfileScreen();
                            return new Dictionary<string, object?>
                            {
                                ["status"] = "ok",
                                ["message"] = "Opened profile screen. Send switch again to select profile.",
                            };
                        }
                    }

                    // Last resort: direct switch (requires restart)
                    sm.SwitchProfileId(profileId);
                    return new Dictionary<string, object?>
                    {
                        ["status"] = "ok",
                        ["message"] = $"Switched to profile {profileId} (requires restart)",
                        ["current_profile_id"] = sm.CurrentProfileId
                    };
                }
                else if (action == "delete")
                {
                    if (profileId < 1 || profileId > 3)
                        return new Dictionary<string, object?> { ["error"] = "profile_id must be 1-3" };
                    if (profileId == sm.CurrentProfileId)
                        return new Dictionary<string, object?> { ["error"] = "Cannot delete the active profile" };

                    sm.DeleteProfile(profileId);
                    return new Dictionary<string, object?>
                    {
                        ["status"] = "ok",
                        ["message"] = $"Deleted profile {profileId}"
                    };
                }

                return new Dictionary<string, object?> { ["error"] = $"Unknown action: {action}. Use: switch, delete" };
            });
            var result = resultTask.GetAwaiter().GetResult();
            SendJson(response, result);
        }
        catch (System.Exception ex)
        {
            SendError(response, 500, $"Profile action failed: {ex.Message}");
        }
    }

    private static void HandleGetProfile(HttpListenerResponse response)
    {
        try
        {
            var dataTask = RunOnMainThread(() => BuildProfile());
            var data = dataTask.GetAwaiter().GetResult();
            SendJson(response, data);
        }
        catch (System.Exception ex)
        {
            SendError(response, 500, $"Failed to build profile: {ex.Message}");
        }
    }

    private static void HandleGetGlossaryCards(HttpListenerResponse response)
    {
        try
        {
            var dataTask = RunOnMainThread(() => BuildGlossaryCards());
            var data = dataTask.GetAwaiter().GetResult();
            SendJson(response, data);
        }
        catch (System.Exception ex)
        {
            SendError(response, 500, $"Failed to build glossary: {ex.Message}");
        }
    }

    private static void HandleGetGlossaryKeywords(HttpListenerResponse response)
    {
        try
        {
            var dataTask = RunOnMainThread(() => BuildGlossaryKeywords());
            var data = dataTask.GetAwaiter().GetResult();
            SendJson(response, data);
        }
        catch (System.Exception ex)
        {
            SendError(response, 500, $"Failed to build glossary: {ex.Message}");
        }
    }

    private static void HandleGetGlossaryPotions(HttpListenerResponse response)
    {
        try
        {
            var dataTask = RunOnMainThread(() => BuildGlossaryPotions());
            var data = dataTask.GetAwaiter().GetResult();
            SendJson(response, data);
        }
        catch (System.Exception ex)
        {
            SendError(response, 500, $"Failed to build glossary: {ex.Message}");
        }
    }

    private static void HandleGetGlossaryRelics(HttpListenerResponse response)
    {
        try
        {
            var dataTask = RunOnMainThread(() => BuildGlossaryRelics());
            var data = dataTask.GetAwaiter().GetResult();
            SendJson(response, data);
        }
        catch (System.Exception ex)
        {
            SendError(response, 500, $"Failed to build glossary: {ex.Message}");
        }
    }

    private static void HandlePostAction(HttpListenerRequest request, HttpListenerResponse response)
    {
        string body;
        using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
            body = reader.ReadToEnd();

        Dictionary<string, JsonElement>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body);
        }
        catch
        {
            SendError(response, 400, "Invalid JSON");
            return;
        }

        if (parsed == null || !parsed.TryGetValue("action", out var actionElem))
        {
            SendError(response, 400, "Missing 'action' field");
            return;
        }

        string action = actionElem.GetString() ?? "";

        // Handle menu actions separately (no run required)
        if (action == "menu_select")
        {
            try
            {
                var option = parsed.TryGetValue("option", out var optElem) ? optElem.GetString() ?? "" : "";
                var resultTask = RunOnMainThread(() => ExecuteMenuSelect(option));
                var result = resultTask.GetAwaiter().GetResult();
                SendJson(response, result);
            }
            catch (Exception ex)
            {
                SendError(response, 500, $"Menu action failed: {ex.Message}");
            }
            return;
        }

        try
        {
            var resultTask = RunOnMainThread(() => ExecuteAction(action, parsed));
            var result = resultTask.GetAwaiter().GetResult();
            SendJson(response, result);
        }
        catch (Exception ex)
        {
            SendError(response, 500, $"Action failed: {ex.Message}");
        }
    }
}

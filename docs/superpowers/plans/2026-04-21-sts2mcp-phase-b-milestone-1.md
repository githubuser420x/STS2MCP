# STS2MCP Phase B Milestone 1 — Speed & Autonomy Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fork `githubuser420x/STS2MCP` gains autonomous run control (start/restart without human clicks), halves combat round-trips, and ships a persistent-session agent loop script for unattended N-run batches.

**Architecture:** Rebase on `romgenie/main` to inherit C# mod backend for autonomy (menu_select, game_over, timeline_advance, seed, profile, glossary). Layer Python MCP tool wrappers on top so Claude can call the new endpoints. Add `play_turn` batch tool and PR #34's round-trip merger. Replace interactive Claude Code with a `claude-agent-sdk` Python loop for unattended operation.

**Tech Stack:** C# 12 / .NET 9 (mod), Python 3.11+ + `uv` + `httpx` + `mcp` + `claude-agent-sdk` (MCP server and agent loop), Godot 4.x runtime (STS2 engine).

**Spec:** `docs/superpowers/specs/2026-04-21-sts2mcp-speed-and-autonomy-design.md`

**Reference codebase for Agent SDK pattern:** `C:\Users\user\Desktop\war room\warroom-shadowheart\agent_sdk_llm.py` — proven persistent `ClaudeSDKClient` implementation.

---

## Group 0 — Prerequisites

### Task 0.1: Install .NET 9 SDK

**Files:** none (tooling install)

- [ ] **Step 1: Check current SDK presence**

Run: `dotnet --list-sdks`
Expected: empty output (no SDK installed, confirmed on 2026-04-20).

- [ ] **Step 2: Install .NET 9 SDK via winget**

Run: `winget install --id Microsoft.DotNet.SDK.9 --accept-source-agreements --accept-package-agreements`
Expected: "Successfully installed"

- [ ] **Step 3: Open a fresh bash and verify**

In a new bash window (so PATH reloads):
Run: `dotnet --list-sdks`
Expected: at least one `9.0.x` line.

### Task 0.2: Verify upstream mod builds from source

**Files:**
- Read: `C:/Users/user/Desktop/STS2MCP/build.ps1`

- [ ] **Step 1: Read `build.ps1` to understand the build invocation**

- [ ] **Step 2: Run the build on current fork (Gennadiyev base)**

Run: `powershell -File "C:/Users/user/Desktop/STS2MCP/build.ps1"`
Expected: succeeds; produces `STS2_MCP.dll` in the build output dir.

If build fails: stop the plan here. Report the error before proceeding — the rest of the plan assumes a working local build.

---

## Group A — Rebase onto romgenie + safety patch

### Task A.1: Add romgenie remote and merge into fork main

**Files:**
- Modify (git state only): `C:/Users/user/Desktop/STS2MCP/.git/config` (adding remote)

- [ ] **Step 1: Add romgenie as a remote**

Run:
```bash
cd "C:/Users/user/Desktop/STS2MCP"
git remote add romgenie https://github.com/romgenie/STS2MCP.git
git fetch romgenie
```
Expected: fetches 41 commits from `romgenie/main`.

- [ ] **Step 2: Create a safety branch before merge**

Run:
```bash
git branch pre-romgenie-merge
```
Expected: creates `pre-romgenie-merge` pointing at current `main`. Kept as escape hatch if merge goes sideways.

- [ ] **Step 3: Merge romgenie/main into local main**

Run:
```bash
git merge romgenie/main --no-ff -m "Merge romgenie/main into fork for autonomous run control"
```
Expected: clean merge (no conflicts), because upstream-based fork and romgenie-based fork share `Gennadiyev/main` history.

If conflicts: abort (`git merge --abort`), then resolve by preferring romgenie's version of each conflicting file (they're the superset). Commit.

- [ ] **Step 4: Push**

Run: `git push origin main`
Expected: push succeeds.

### Task A.2: Rebuild the mod and smoke-test in game

**Files:**
- Read: build output from `build.ps1`

- [ ] **Step 1: Rebuild mod DLL after merge**

Run: `powershell -File "C:/Users/user/Desktop/STS2MCP/build.ps1"`
Expected: produces `STS2_MCP.dll`.

- [ ] **Step 2: Copy new DLL into game mods folder**

Run:
```bash
cp "C:/Users/user/Desktop/STS2MCP/bin/Release/net9.0/STS2_MCP.dll" "D:/Steam/steamapps/common/Slay the Spire 2/mods/STS2_MCP.dll"
```

If the output path in `bin/Release/net9.0` doesn't exist, adjust to whatever directory `build.ps1` produces (verified in Task 0.2).

- [ ] **Step 3: Restart the game and verify new endpoints respond**

User-action step: close and relaunch STS2, enable mods.

Run these five curls in order:
```bash
curl -s http://localhost:15526/
curl -s http://localhost:15526/api/v1/glossary/cards | head -c 200
curl -s http://localhost:15526/api/v1/glossary/relics | head -c 200
curl -s http://localhost:15526/api/v1/glossary/potions | head -c 200
curl -s http://localhost:15526/api/v1/glossary/keywords | head -c 200
```
Expected: all five return JSON (not 404). Root endpoint shows version `0.3.4` (romgenie hasn't bumped; spec flags this).

If any return 404 or 500: the merge dropped a route or the build is stale. Inspect `McpMod.cs` for the route, rerun build, re-copy DLL.

### Task A.3: Loopback-default binding

**Files:**
- Modify: `C:/Users/user/Desktop/STS2MCP/McpMod.cs` — function `StartHttpServer` (lines ~122-165 post-merge; verify range)

- [ ] **Step 1: Read the current `StartHttpServer` to confirm the attempt order**

Verify the three-tier attempt list (wildcard → IPv4 → loopback).

- [ ] **Step 2: Modify `StartHttpServer` so loopback is tried first unless opt-in flag set**

Replace the current `attempts` list construction with:

```csharp
private static void StartHttpServer(int port)
{
    var loopback = new[]
    {
        $"http://localhost:{port}/",
        $"http://127.0.0.1:{port}/"
    };

    var attempts = new List<string[]> { loopback };

    if (AllowLanBinding())
    {
        attempts.Add(new[] { $"http://+:{port}/" });
        var ipv4Prefixes = GetIpv4Prefixes(port);
        if (ipv4Prefixes.Count > 0)
            attempts.Add(ipv4Prefixes.ToArray());
    }

    Exception? lastError = null;
    foreach (var prefixes in attempts)
    {
        HttpListener? candidate = null;
        try
        {
            candidate = new HttpListener();
            foreach (var prefix in prefixes)
                candidate.Prefixes.Add(prefix);
            candidate.Start();
            _listener = candidate;
            _boundPrefixes = prefixes;
            return;
        }
        catch (Exception ex)
        {
            lastError = ex;
            try { candidate?.Close(); } catch { }
        }
    }

    throw new InvalidOperationException(
        $"Could not bind STS2 MCP on port {port}. Tried loopback" +
        (AllowLanBinding() ? ", wildcard, and IPv4" : " only (set \"allow_lan\": true in STS2_MCP.conf for LAN)") + ".",
        lastError);
}
```

- [ ] **Step 3: Add `AllowLanBinding()` reading from the same config file as `LoadPort`**

Insert after `LoadPort()`:

```csharp
private static bool AllowLanBinding()
{
    try
    {
        string? modDir = Path.GetDirectoryName(
            System.Reflection.Assembly.GetExecutingAssembly().Location);
        if (modDir == null) return false;
        string configPath = Path.Combine(modDir, ConfigFileName);
        if (!File.Exists(configPath)) return false;
        using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
        return doc.RootElement.TryGetProperty("allow_lan", out var el)
               && el.ValueKind == JsonValueKind.True;
    }
    catch { return false; }
}
```

- [ ] **Step 4: Rebuild and copy DLL**

Run the build + copy commands from Task A.2 Step 1-2.

- [ ] **Step 5: Verify default binding is loopback-only**

Restart game. Then:
```bash
# From the PC itself — loopback always works
curl -s http://localhost:15526/
# From another device on LAN (if available): http://<pc-ip>:15526/ should fail/time out.
```

Expected: localhost returns JSON; LAN access fails or times out (no response). If no second device available, inspect startup log — mod prints `server started on http://localhost:15526/, http://127.0.0.1:15526/` (no `http://+:15526/`).

- [ ] **Step 6: Commit**

```bash
git add McpMod.cs
git commit -m "fix(mod): bind loopback only by default; add allow_lan config flag"
```

---

## Group B — Python MCP expansions

### Task B.1: Cherry-pick PR #34 (round-trip merger)

**Files:**
- Modify: `mcp/server.py`

- [ ] **Step 1: Fetch the PR branch**

```bash
cd "C:/Users/user/Desktop/STS2MCP"
git fetch upstream pull/34/head:pr-34
```

- [ ] **Step 2: Inspect the diff to confirm it still applies cleanly**

Run: `git diff main..pr-34 -- mcp/server.py | head -80`
Expected: the diff we read during brainstorming (adds `_get_json_until_play_phase`, `_merge_post_result_and_state`, `_post_ok_then_merged_game_state`, and wraps `combat_play_card`, `rewards_claim`, etc. to return merged state).

- [ ] **Step 3: Cherry-pick the PR's commits**

Run:
```bash
git log pr-34 --not main --oneline
# copy commit SHAs in chronological order (oldest first), then:
git cherry-pick <sha1> <sha2> ...
```

If conflicts appear inside `mcp/server.py`: resolve by keeping PR #34's new helper functions intact and applying its wrapper pattern to any tools romgenie may have added. If the conflict is too gnarly, abort (`git cherry-pick --abort`) and instead manually port the three helper functions + wrap the action tools one by one.

- [ ] **Step 4: Run the MCP server standalone to confirm it imports**

```bash
cd mcp
uv run python -c "import server; print(len([t for t in dir(server) if not t.startswith('_')]))"
```
Expected: prints a positive integer (module imports, tool count non-zero). If it errors, fix the import issues before continuing.

- [ ] **Step 5: Commit**

```bash
git add mcp/server.py
git commit -m "feat(mcp): merge POST-action result with get_game_state (upstream PR #34)"
```

### Task B.2: Write test harness for new MCP tools

**Files:**
- Create: `mcp/tests/__init__.py` (empty)
- Create: `mcp/tests/conftest.py`
- Create: `mcp/tests/test_autonomy_tools.py`
- Modify: `mcp/pyproject.toml` (add `pytest`, `pytest-asyncio`, `respx` as dev deps)

- [ ] **Step 1: Add dev deps to `mcp/pyproject.toml`**

Insert under `[project]`:

```toml
[dependency-groups]
dev = [
    "pytest>=8.0",
    "pytest-asyncio>=0.23",
    "respx>=0.21",
]
```

- [ ] **Step 2: Sync dev deps**

Run: `cd mcp && uv sync --all-groups`
Expected: installs pytest, pytest-asyncio, respx.

- [ ] **Step 3: Write `mcp/tests/conftest.py`**

```python
"""Shared fixtures for MCP server tests.

`respx` stubs the httpx calls `server.py` makes to `http://localhost:15526`
so tests don't need a running game.
"""
import pytest
import respx


@pytest.fixture
def mock_mod():
    with respx.mock(base_url="http://localhost:15526", assert_all_called=False) as router:
        yield router
```

- [ ] **Step 4: Write `mcp/tests/test_autonomy_tools.py` with one placeholder test**

```python
"""Tests for autonomy tools added in Phase B Milestone 1."""
import pytest


@pytest.mark.asyncio
async def test_placeholder():
    assert True
```

- [ ] **Step 5: Verify the harness runs**

Run: `cd mcp && uv run pytest tests/ -v`
Expected: 1 test passes.

- [ ] **Step 6: Commit**

```bash
git add mcp/tests mcp/pyproject.toml mcp/uv.lock
git commit -m "test(mcp): add pytest + respx harness for MCP tool tests"
```

### Task B.3: Wire `start_run` MCP tool (TDD)

**Files:**
- Modify: `mcp/server.py`
- Modify: `mcp/tests/test_autonomy_tools.py`

**Design note:** `start_run` is a high-level composition: navigate menu → character select → embark. Internally it calls the raw `menu_select` HTTP endpoint. We'll add `menu_select` as a primitive first, then `start_run` as a convenience wrapper.

- [ ] **Step 1: Write failing test for `menu_select`**

Append to `test_autonomy_tools.py`:

```python
import server


@pytest.mark.asyncio
async def test_menu_select_posts_action(mock_mod):
    mock_mod.post("/api/v1/singleplayer").respond(
        json={"status": "ok", "message": "menu navigated"}
    )
    result = await server.menu_select(option="singleplayer")
    assert "ok" in result
    request = mock_mod.calls.last.request
    body = request.content.decode()
    assert '"action": "menu_select"' in body
    assert '"option": "singleplayer"' in body
```

- [ ] **Step 2: Run, verify it fails with AttributeError**

Run: `cd mcp && uv run pytest tests/test_autonomy_tools.py::test_menu_select_posts_action -v`
Expected: FAIL with `AttributeError: module 'server' has no attribute 'menu_select'`.

- [ ] **Step 3: Implement `menu_select` in `server.py`**

Append after the existing general-section tools (after `proceed_to_map`, around line 150):

```python
@mcp.tool()
async def menu_select(option: str) -> str:
    """[Menu] Select a main-menu option by string id.

    Valid options depend on menu state. Typical values: "singleplayer",
    "multiplayer", "profile", "settings", "quit".

    Args:
        option: the menu option id.
    """
    try:
        return await _post({"action": "menu_select", "option": option})
    except Exception as e:
        return _handle_error(e)
```

- [ ] **Step 4: Run, verify pass**

Run: `cd mcp && uv run pytest tests/test_autonomy_tools.py::test_menu_select_posts_action -v`
Expected: PASS.

- [ ] **Step 5: Write failing test for `start_run`**

```python
@pytest.mark.asyncio
async def test_start_run_sequence(mock_mod):
    """start_run should: select singleplayer, pick character, optionally set seed, embark."""
    calls = []
    def capture(request):
        import json as _json
        calls.append(_json.loads(request.content))
        return respx.MockResponse(json={"status": "ok"})
    mock_mod.post("/api/v1/singleplayer").mock(side_effect=capture)
    # Also stub the final get_game_state call to return in-run state
    mock_mod.get("/api/v1/singleplayer").respond(
        json={"state_type": "monster", "run": {"act": 1, "floor": 1, "ascension": 0}}
    )
    result = await server.start_run(character="ironclad", seed=12345)
    # Must send: menu_select singleplayer -> character_select ironclad -> embark seed=12345
    actions = [c["action"] for c in calls]
    assert "menu_select" in actions
    assert any(c.get("action") == "character_select" and c.get("character") == "ironclad" for c in calls)
    assert any(c.get("action") == "embark" and c.get("seed") == 12345 for c in calls)
```

- [ ] **Step 6: Run, verify fail**

Run: `cd mcp && uv run pytest tests/test_autonomy_tools.py::test_start_run_sequence -v`
Expected: FAIL with `AttributeError: module 'server' has no attribute 'start_run'`.

- [ ] **Step 7: Implement `start_run`**

```python
@mcp.tool()
async def start_run(
    character: str,
    seed: int | None = None,
    ascension: int = 0,
) -> str:
    """[Menu] Start a new singleplayer run programmatically.

    Drives: main menu -> singleplayer -> character select -> embark.
    Dismisses FTUE/tutorial prompts that appear along the way.

    Args:
        character: character id (e.g. "ironclad", "necrobinder").
        seed: optional integer seed for reproducible runs.
        ascension: ascension level (default 0).
    """
    try:
        await _post({"action": "menu_select", "option": "singleplayer"})
        await _post({"action": "character_select", "character": character})
        embark: dict = {"action": "embark", "ascension": ascension}
        if seed is not None:
            embark["seed"] = seed
        await _post(embark)
        return await _get({"format": "markdown"})
    except Exception as e:
        return _handle_error(e)
```

- [ ] **Step 8: Run, verify pass**

Run: `cd mcp && uv run pytest tests/test_autonomy_tools.py -v`
Expected: all pass.

- [ ] **Step 9: Verify against live game (manual)**

Restart Claude Code in a fresh terminal (MCP reload), from the main menu of STS2 ask Claude: *"Call start_run with character='ironclad', seed=12345."* Expected: game actually embarks.

If the action names (`menu_select`, `character_select`, `embark`) differ from what the romgenie C# backend accepts, adjust the Python payloads to match. Inspect `McpMod.Actions.cs` (post-merge) for the real action string keys.

- [ ] **Step 10: Commit**

```bash
git add mcp/server.py mcp/tests/test_autonomy_tools.py
git commit -m "feat(mcp): add menu_select and start_run MCP tools"
```

### Task B.4: Wire `game_over_*` and `timeline_advance` tools

**Files:**
- Modify: `mcp/server.py`
- Modify: `mcp/tests/test_autonomy_tools.py`

- [ ] **Step 1: Write failing tests**

```python
@pytest.mark.asyncio
async def test_game_over_return_to_menu(mock_mod):
    mock_mod.post("/api/v1/singleplayer").respond(json={"status": "ok"})
    result = await server.game_over_return_to_menu()
    body = mock_mod.calls.last.request.content.decode()
    assert '"action": "game_over_return_to_menu"' in body


@pytest.mark.asyncio
async def test_timeline_advance(mock_mod):
    mock_mod.post("/api/v1/singleplayer").respond(json={"status": "ok"})
    await server.timeline_advance()
    body = mock_mod.calls.last.request.content.decode()
    assert '"action": "timeline_advance"' in body
```

- [ ] **Step 2: Run, verify fail**

Run: `cd mcp && uv run pytest tests/test_autonomy_tools.py::test_game_over_return_to_menu tests/test_autonomy_tools.py::test_timeline_advance -v`
Expected: both FAIL with `AttributeError`.

- [ ] **Step 3: Implement**

Append to `server.py`:

```python
@mcp.tool()
async def game_over_continue() -> str:
    """[Game Over] Continue past the game-over screen (where the game allows)."""
    try:
        return await _post({"action": "game_over_continue"})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def game_over_return_to_menu() -> str:
    """[Game Over] Return to the main menu from the game-over screen."""
    try:
        return await _post({"action": "game_over_return_to_menu"})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def timeline_advance() -> str:
    """[Timeline] Auto-click through epoch reveal / timeline advance prompts."""
    try:
        return await _post({"action": "timeline_advance"})
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def dismiss_ftue() -> str:
    """[FTUE] Dismiss any active tutorial / first-time-user popup."""
    try:
        return await _post({"action": "dismiss_ftue"})
    except Exception as e:
        return _handle_error(e)
```

- [ ] **Step 4: Run, verify pass**

Run: `cd mcp && uv run pytest tests/test_autonomy_tools.py -v`

- [ ] **Step 5: Verify action-name mapping against `McpMod.Actions.cs`**

Grep the C# file for the actual registered action strings:

Run: `grep -n 'case "' McpMod.Actions.cs | head -30`

Compare the `action` strings we send above against what the switch/case accepts. Rename any that don't match.

- [ ] **Step 6: Commit**

```bash
git add mcp/server.py mcp/tests/test_autonomy_tools.py
git commit -m "feat(mcp): add game_over, timeline_advance, dismiss_ftue tools"
```

### Task B.5: Glossary and bestiary read-only tools

**Files:**
- Modify: `mcp/server.py`
- Modify: `mcp/tests/test_autonomy_tools.py`

- [ ] **Step 1: Write failing test**

```python
@pytest.mark.asyncio
async def test_get_glossary_cards(mock_mod):
    mock_mod.get("/api/v1/glossary/cards").respond(
        json={"cards": [{"id": "STRIKE", "name": "Strike"}]}
    )
    result = await server.get_glossary_cards()
    assert "STRIKE" in result
```

- [ ] **Step 2: Run, verify fail**

Run: `cd mcp && uv run pytest tests/test_autonomy_tools.py::test_get_glossary_cards -v`
Expected: FAIL.

- [ ] **Step 3: Implement four glossary tools + bestiary**

Append to `server.py`. These hit a different base path than the `/api/v1/singleplayer` endpoint — use raw `httpx` since `_get` as currently written targets the action endpoint.

Add helper near the other `_get`/`_post` helpers:

```python
async def _get_path(path: str) -> str:
    """GET an arbitrary path on the mod server."""
    async with httpx.AsyncClient() as client:
        r = await client.get(f"{_base_url}{path}", timeout=10.0)
        r.raise_for_status()
        return r.text
```

Then the tools:

```python
@mcp.tool()
async def get_glossary_cards() -> str:
    """[Reference] Static card glossary: id, name, rarity, cost, description. Cache-friendly."""
    try:
        return await _get_path("/api/v1/glossary/cards")
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def get_glossary_relics() -> str:
    """[Reference] Static relic glossary."""
    try:
        return await _get_path("/api/v1/glossary/relics")
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def get_glossary_potions() -> str:
    """[Reference] Static potion glossary."""
    try:
        return await _get_path("/api/v1/glossary/potions")
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def get_glossary_keywords() -> str:
    """[Reference] Static keyword glossary (status effects, mechanics)."""
    try:
        return await _get_path("/api/v1/glossary/keywords")
    except Exception as e:
        return _handle_error(e)


@mcp.tool()
async def get_bestiary() -> str:
    """[Reference] Enemy bestiary with HP, attacks, AI patterns."""
    try:
        return await _get_path("/api/v1/bestiary")
    except Exception as e:
        return _handle_error(e)
```

- [ ] **Step 4: Run, verify pass**

Run: `cd mcp && uv run pytest tests/test_autonomy_tools.py -v`
Expected: all pass.

- [ ] **Step 5: Live smoke-test one glossary tool**

With game running, in a fresh Claude Code session, ask: *"Call get_glossary_cards and show me the first three cards."* Expected: returns real card data.

- [ ] **Step 6: Commit**

```bash
git add mcp/server.py mcp/tests/test_autonomy_tools.py
git commit -m "feat(mcp): add glossary and bestiary read tools"
```

### Task B.6: `play_turn` batch tool

**Files:**
- Modify: `mcp/server.py`
- Modify: `mcp/tests/test_autonomy_tools.py`

- [ ] **Step 1: Write failing test — happy path (all actions execute)**

```python
@pytest.mark.asyncio
async def test_play_turn_all_execute(mock_mod):
    # Each POST returns ok; GET returns the same steady-state combat JSON
    mock_mod.post("/api/v1/singleplayer").respond(json={"status": "ok"})
    combat_state = {
        "state_type": "monster",
        "battle": {"is_play_phase": True, "hand_size": 3},
        "player": {"hp": 72, "hand": [{"id": "STRIKE"}, {"id": "DEFEND"}, {"id": "BASH"}]},
    }
    mock_mod.get("/api/v1/singleplayer").respond(json=combat_state)
    result = await server.play_turn(actions=[
        {"action": "play_card", "card_index": 2, "target": "JAW_WORM_0"},
        {"action": "play_card", "card_index": 1},
        {"action": "end_turn"},
    ])
    import json as _json
    data = _json.loads(result)
    assert data["executed"] == 3
    assert data["remaining"] == []
    assert data["reason"] is None
```

- [ ] **Step 2: Write failing test — interrupted by enemy death**

```python
@pytest.mark.asyncio
async def test_play_turn_stops_on_enemy_death(mock_mod):
    mock_mod.post("/api/v1/singleplayer").respond(json={"status": "ok"})
    states = iter([
        {"state_type": "monster", "battle": {"enemies": [{"id": "JAW_WORM_0", "hp": 10}]}},
        {"state_type": "monster", "battle": {"enemies": [{"id": "JAW_WORM_0", "hp": 0}]}},
    ])
    mock_mod.get("/api/v1/singleplayer").mock(
        side_effect=lambda req: respx.MockResponse(json=next(states))
    )
    result = await server.play_turn(actions=[
        {"action": "play_card", "card_index": 0, "target": "JAW_WORM_0"},
        {"action": "play_card", "card_index": 1, "target": "JAW_WORM_0"},
    ])
    import json as _json
    data = _json.loads(result)
    assert data["executed"] == 1
    assert len(data["remaining"]) == 1
    assert "died" in data["reason"].lower()
```

- [ ] **Step 3: Write failing test — interrupted by overlay (hand_select)**

```python
@pytest.mark.asyncio
async def test_play_turn_stops_on_overlay(mock_mod):
    mock_mod.post("/api/v1/singleplayer").respond(json={"status": "ok"})
    states = iter([
        {"state_type": "monster", "battle": {"is_play_phase": True}},
        {"state_type": "hand_select"},
    ])
    mock_mod.get("/api/v1/singleplayer").mock(
        side_effect=lambda req: respx.MockResponse(json=next(states))
    )
    result = await server.play_turn(actions=[
        {"action": "play_card", "card_index": 0},
        {"action": "play_card", "card_index": 1},
    ])
    import json as _json
    data = _json.loads(result)
    assert data["executed"] == 1
    assert data["reason"].startswith("overlay:")
```

- [ ] **Step 4: Run all three, verify fail**

Run: `cd mcp && uv run pytest tests/test_autonomy_tools.py -v -k play_turn`
Expected: three FAILs.

- [ ] **Step 5: Implement `play_turn`**

Append to `server.py`:

```python
INTERRUPTING_STATE_TYPES = {
    "hand_select", "card_select", "bundle_select", "relic_select",
    "rewards", "card_reward", "game_over", "map", "event", "rest_site",
    "shop", "treasure", "crystal_sphere",
}


def _enemy_ids_alive(state: dict) -> set[str]:
    battle = state.get("battle") or {}
    enemies = battle.get("enemies") or []
    return {
        e.get("id") for e in enemies
        if isinstance(e, dict) and (e.get("hp") or 0) > 0 and e.get("id")
    }


@mcp.tool()
async def play_turn(actions: list[dict]) -> str:
    """[Combat] Execute an ordered list of combat actions in one round-trip.

    Stops early and returns partial progress if any of these happens:
      - A POST returns status != "ok"
      - Game state leaves combat (state_type changes to anything in
        {hand_select, card_select, bundle_select, relic_select, rewards,
         card_reward, game_over, map, event, rest_site, shop, treasure,
         crystal_sphere})
      - An enemy that was alive before this call has died (so Claude can
        retarget instead of blindly playing into a dead entity)
      - Card draw occurred mid-turn (hand size grew between actions)

    Returns JSON: {executed: int, remaining: [...], state: <current>, reason: str|null}.

    Args:
        actions: list of dicts with shape {action: str, ...params}. Each dict
            is sent verbatim as the POST body.
    """
    import json as _json
    try:
        before_raw = await _get({"format": "json"})
        before = _json.loads(before_raw)
        enemies_alive_before = _enemy_ids_alive(before)
        hand_size_before = len((before.get("player") or {}).get("hand") or [])

        executed = 0
        reason: str | None = None
        for i, act in enumerate(actions):
            post_raw = await _post(act)
            try:
                post = _json.loads(post_raw)
            except Exception:
                post = {}
            if isinstance(post, dict) and post.get("status") == "error":
                reason = f"post_error: {post.get('message', 'unknown')}"
                break
            executed += 1

            state_raw = await _get({"format": "json"})
            state = _json.loads(state_raw)

            st = state.get("state_type")
            if st in INTERRUPTING_STATE_TYPES:
                reason = f"overlay: {st}"
                break

            enemies_now = _enemy_ids_alive(state)
            died = enemies_alive_before - enemies_now
            if died:
                reason = f"enemy_died: {','.join(sorted(died))}"
                break

            hand_size_now = len((state.get("player") or {}).get("hand") or [])
            if hand_size_now > hand_size_before:
                reason = f"card_drawn: hand {hand_size_before}->{hand_size_now}"
                break

            enemies_alive_before = enemies_now
            hand_size_before = hand_size_now

        final_state_raw = await _get({"format": "markdown"})
        return _json.dumps({
            "executed": executed,
            "remaining": actions[executed:],
            "reason": reason,
            "state": final_state_raw,
        }, ensure_ascii=False)
    except Exception as e:
        return _handle_error(e)
```

- [ ] **Step 6: Run the three tests, verify pass**

Run: `cd mcp && uv run pytest tests/test_autonomy_tools.py -v -k play_turn`

If the "card drawn" test (we didn't write one) is needed for coverage, add it. The three covered interruption types are the ones most likely to desync plans.

- [ ] **Step 7: Run the whole test suite**

Run: `cd mcp && uv run pytest tests/ -v`
Expected: all green.

- [ ] **Step 8: Commit**

```bash
git add mcp/server.py mcp/tests/test_autonomy_tools.py
git commit -m "feat(mcp): add play_turn batch action tool with interruption detection"
```

---

## Group C — Documentation

### Task C.1: Update AGENTS.md

**Files:**
- Modify: `C:/Users/user/Desktop/STS2MCP/AGENTS.md`

- [ ] **Step 1: Read current AGENTS.md to preserve structure**

- [ ] **Step 2: Remove the stale "call get_game_state twice" workaround**

Find the bullet under "State Polling" that says "Sometimes you need to call `get_game_state` twice" and delete it — PR #34 cherry-pick (Task B.1) handles this server-side.

- [ ] **Step 3: Add a new section "Run Control" before "General Strategy"**

```markdown
## Run Control (Autonomous Runs)

These tools let an agent start and restart runs without human clicks:

- `start_run(character, seed=None, ascension=0)` — embark from main menu. Use `seed` for reproducible runs.
- `menu_select(option)` — navigate main/submenus manually when `start_run` is overkill.
- `game_over_return_to_menu()` — after death, go back to the main menu so you can `start_run` again.
- `timeline_advance()` — click through epoch reveals on the timeline screen.
- `dismiss_ftue()` — clear a tutorial popup that's blocking interaction.

**Typical run loop:**
1. Call `get_game_state`. If `state_type == "menu"`, call `start_run(...)`.
2. Play until `state_type == "game_over"`.
3. Call `game_over_return_to_menu`, then back to step 1.
```

- [ ] **Step 4: Add "Batch Combat Turns" section after the Combat Sequencing block**

```markdown
### Batch Combat Turns (`play_turn`)

Prefer `play_turn(actions=[...])` over calling `combat_play_card` N times. It executes the ordered list server-side in a single MCP round-trip and stops early if anything unexpected happens (card drawn, enemy died, overlay opened).

Example:
```json
[
  {"action": "play_card", "card_index": 2, "target": "JAW_WORM_0"},
  {"action": "play_card", "card_index": 1},
  {"action": "end_turn"}
]
```

If the return says `executed < len(actions)`, read `reason` and `state` to plan the rest.
```

- [ ] **Step 5: Add "Reference Lookups" section**

```markdown
### Reference Lookups (cache once)

Card/relic/potion/keyword data is static within a run. Call these once per session (they're prompt-cache friendly) rather than relying on state payloads to re-send full descriptions:

- `get_glossary_cards()`, `get_glossary_relics()`, `get_glossary_potions()`, `get_glossary_keywords()`
- `get_bestiary()` — enemy reference (HP, attacks, patterns)
```

- [ ] **Step 6: Commit**

```bash
git add AGENTS.md
git commit -m "docs(agents): document autonomy tools, play_turn, glossary lookups"
```

---

## Group D — Agent SDK autonomous loop

### Task D.1: Scaffold `agent_loop/` package

**Files:**
- Create: `agent_loop/__init__.py` (empty)
- Create: `agent_loop/pyproject.toml`
- Create: `agent_loop/strategy_prompt.md`

- [ ] **Step 1: Create the package directory and pyproject**

`agent_loop/pyproject.toml`:

```toml
[project]
name = "sts2-agent-loop"
version = "0.1.0"
description = "Autonomous STS2 agent loop using claude-agent-sdk"
requires-python = ">=3.11"
dependencies = [
    "claude-agent-sdk>=0.1.0",
    "httpx>=0.25",
]

[project.scripts]
sts2-run = "agent_loop.runner:main"

[dependency-groups]
dev = [
    "pytest>=8.0",
    "pytest-asyncio>=0.23",
]
```

- [ ] **Step 2: Create empty `agent_loop/__init__.py`**

- [ ] **Step 3: Create `agent_loop/strategy_prompt.md`**

This is the system prompt the agent uses. Copy the "General Strategy" section from `AGENTS.md` verbatim, then append:

```markdown

## Operating Instructions

You are playing Slay the Spire 2 autonomously. Your tools are provided by the sts2 MCP server. On every prompt, you must either:
1. Take a game action via a tool call, OR
2. Return a short status line like "STATUS: dead — restart needed" so the loop can handle it.

**Do not narrate. Do not explain. Act.** One short sentence of reasoning max before a tool call; zero is better.

**Turn loop:**
- Call `get_game_state` to see where you are.
- In combat, prefer `play_turn([...])` over single card plays.
- In rewards/map/events, make one decision per prompt.
- When `state_type == "game_over"`, return "STATUS: dead".
```

- [ ] **Step 4: Sync dependencies**

```bash
cd agent_loop
uv sync --all-groups
```

If `claude-agent-sdk` isn't yet published on PyPI, install from source referencing `warroom-shadowheart/agent_sdk_llm.py` — inspect that file to find how the war-room project imports it and replicate.

- [ ] **Step 5: Commit**

```bash
cd ..
git add agent_loop/
git commit -m "feat(agent): scaffold agent_loop package and strategy prompt"
```

### Task D.2: SQLite run logger

**Files:**
- Create: `agent_loop/logger.py`
- Create: `agent_loop/tests/test_logger.py`
- Create: `agent_loop/tests/__init__.py` (empty)

- [ ] **Step 1: Write failing test**

`agent_loop/tests/test_logger.py`:

```python
import pytest
from pathlib import Path
from agent_loop.logger import RunLogger


def test_logger_creates_schema(tmp_path: Path):
    db = tmp_path / "runs.db"
    logger = RunLogger(db)
    logger.start_run(seed=12345, character="ironclad", effort="low", model="sonnet")
    logger.log_tool_call("get_game_state", {}, {"state_type": "monster"})
    logger.finish_run(floor_reached=3, died=True, tokens_in=1000, tokens_out=500)
    logger.close()

    import sqlite3
    conn = sqlite3.connect(db)
    rows = conn.execute("SELECT seed, character, floor_reached, died FROM runs").fetchall()
    assert len(rows) == 1
    assert rows[0] == (12345, "ironclad", 3, 1)
    tool_calls = conn.execute("SELECT tool_name FROM tool_calls").fetchall()
    assert tool_calls == [("get_game_state",)]
```

- [ ] **Step 2: Run, verify fail**

Run: `cd agent_loop && uv run pytest tests/ -v`
Expected: FAIL (`ModuleNotFoundError: agent_loop.logger`).

- [ ] **Step 3: Implement `agent_loop/logger.py`**

```python
"""SQLite logger for autonomous STS2 runs.

Schema:
  runs(id, seed, character, effort, model, started_at, ended_at,
       floor_reached, died, tokens_in, tokens_out)
  tool_calls(id, run_id, ts, tool_name, args_json, result_json)
"""
import json
import sqlite3
import time
from pathlib import Path


SCHEMA = """
CREATE TABLE IF NOT EXISTS runs (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    seed INTEGER,
    character TEXT,
    effort TEXT,
    model TEXT,
    started_at REAL,
    ended_at REAL,
    floor_reached INTEGER,
    died INTEGER,
    tokens_in INTEGER,
    tokens_out INTEGER
);

CREATE TABLE IF NOT EXISTS tool_calls (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id INTEGER REFERENCES runs(id),
    ts REAL,
    tool_name TEXT,
    args_json TEXT,
    result_json TEXT
);
"""


class RunLogger:
    def __init__(self, db_path: Path):
        self.conn = sqlite3.connect(db_path)
        self.conn.executescript(SCHEMA)
        self.conn.commit()
        self._run_id: int | None = None

    def start_run(self, seed: int, character: str, effort: str, model: str) -> None:
        cur = self.conn.execute(
            "INSERT INTO runs(seed, character, effort, model, started_at) "
            "VALUES (?, ?, ?, ?, ?)",
            (seed, character, effort, model, time.time()),
        )
        self._run_id = cur.lastrowid
        self.conn.commit()

    def log_tool_call(self, tool_name: str, args: dict, result: object) -> None:
        if self._run_id is None:
            raise RuntimeError("start_run must be called before log_tool_call")
        self.conn.execute(
            "INSERT INTO tool_calls(run_id, ts, tool_name, args_json, result_json) "
            "VALUES (?, ?, ?, ?, ?)",
            (
                self._run_id,
                time.time(),
                tool_name,
                json.dumps(args, ensure_ascii=False),
                json.dumps(result, ensure_ascii=False, default=str),
            ),
        )
        self.conn.commit()

    def finish_run(
        self,
        floor_reached: int,
        died: bool,
        tokens_in: int,
        tokens_out: int,
    ) -> None:
        if self._run_id is None:
            raise RuntimeError("start_run must be called before finish_run")
        self.conn.execute(
            "UPDATE runs SET ended_at=?, floor_reached=?, died=?, "
            "tokens_in=?, tokens_out=? WHERE id=?",
            (time.time(), floor_reached, 1 if died else 0,
             tokens_in, tokens_out, self._run_id),
        )
        self.conn.commit()
        self._run_id = None

    def close(self) -> None:
        self.conn.close()
```

- [ ] **Step 4: Run, verify pass**

Run: `cd agent_loop && uv run pytest tests/ -v`

- [ ] **Step 5: Commit**

```bash
git add agent_loop/logger.py agent_loop/tests/
git commit -m "feat(agent): SQLite run logger with tool-call trace"
```

### Task D.3: Runner with CLI

**Files:**
- Create: `agent_loop/runner.py`
- Read (reference only, no modification): `C:/Users/user/Desktop/war room/warroom-shadowheart/agent_sdk_llm.py`

- [ ] **Step 1: Read the reference file**

Read `warroom-shadowheart/agent_sdk_llm.py` top to bottom. Note:
- How `ClaudeSDKClient` is instantiated (model, system prompt, tools config).
- How messages are streamed and tool calls are intercepted.
- How session cleanup works.

- [ ] **Step 2: Write `agent_loop/runner.py`**

```python
"""Autonomous STS2 run loop.

Usage:
    uv run sts2-run --runs 5 --character ironclad --seed 12345 \\
        --model sonnet --effort low --log-dir ./logs
"""
import argparse
import asyncio
import random
from pathlib import Path

from claude_agent_sdk import ClaudeSDKClient, ClaudeAgentOptions

from agent_loop.logger import RunLogger


def load_strategy_prompt() -> str:
    here = Path(__file__).parent
    return (here / "strategy_prompt.md").read_text(encoding="utf-8")


async def play_one_run(
    client: ClaudeSDKClient,
    run_logger: RunLogger,
    seed: int,
    character: str,
    effort: str,
    model: str,
) -> tuple[int, bool, int, int]:
    """Play one run to death/victory. Returns (floor_reached, died, tokens_in, tokens_out)."""
    run_logger.start_run(seed=seed, character=character, effort=effort, model=model)

    prompt = (
        f"Start a new run. Use start_run(character={character!r}, seed={seed}). "
        f"Then play until game_over. Reply 'STATUS: dead' when you see state_type=game_over."
    )

    floor_reached = 0
    died = False
    tokens_in = 0
    tokens_out = 0

    await client.query(prompt)
    async for event in client.receive_response():
        tool_name = getattr(event, "tool_name", None)
        if tool_name:
            run_logger.log_tool_call(
                tool_name,
                getattr(event, "args", {}) or {},
                getattr(event, "result", None),
            )
            # Track floor from game_state tool results
            result = getattr(event, "result", None)
            if isinstance(result, str) and '"floor":' in result:
                import re
                m = re.search(r'"floor":\s*(\d+)', result)
                if m:
                    floor_reached = max(floor_reached, int(m.group(1)))

        text = getattr(event, "text", "") or ""
        if "STATUS: dead" in text or "state_type=game_over" in text.lower():
            died = True
            break

        usage = getattr(event, "usage", None)
        if usage:
            tokens_in += getattr(usage, "input_tokens", 0) or 0
            tokens_out += getattr(usage, "output_tokens", 0) or 0

    # Return to menu for next run
    await client.query("Call game_over_return_to_menu so we can start the next run.")
    async for _ in client.receive_response():
        pass

    run_logger.finish_run(
        floor_reached=floor_reached,
        died=died,
        tokens_in=tokens_in,
        tokens_out=tokens_out,
    )
    return floor_reached, died, tokens_in, tokens_out


async def amain(args: argparse.Namespace) -> None:
    log_dir = Path(args.log_dir)
    log_dir.mkdir(parents=True, exist_ok=True)
    run_logger = RunLogger(log_dir / "runs.db")

    options = ClaudeAgentOptions(
        model=args.model,
        system_prompt=load_strategy_prompt(),
        mcp_servers={"sts2": {"command": "uv", "args": [
            "run", "--directory", str(Path(__file__).parent.parent / "mcp"),
            "python", "server.py",
        ]}},
    )

    rng = random.Random(args.seed) if args.seed is not None else random.Random()

    async with ClaudeSDKClient(options=options) as client:
        for i in range(args.runs):
            seed = rng.randrange(2**31) if args.seed is None else args.seed + i
            floor, died, tin, tout = await play_one_run(
                client, run_logger,
                seed=seed, character=args.character,
                effort=args.effort, model=args.model,
            )
            print(f"[run {i+1}/{args.runs}] seed={seed} floor={floor} "
                  f"died={died} tokens_in={tin} tokens_out={tout}")

    run_logger.close()


def main() -> None:
    p = argparse.ArgumentParser(description="Run autonomous STS2 runs")
    p.add_argument("--runs", type=int, default=1)
    p.add_argument("--character", type=str, default="ironclad")
    p.add_argument("--seed", type=int, default=None,
                   help="Base seed; run N uses seed+N. Omit for random.")
    p.add_argument("--model", type=str, default="claude-sonnet-4-6")
    p.add_argument("--effort", type=str, default="low",
                   choices=["low", "medium", "high"])
    p.add_argument("--log-dir", type=str, default="./logs")
    args = p.parse_args()
    asyncio.run(amain(args))


if __name__ == "__main__":
    main()
```

Note on `ClaudeAgentOptions` / `effort`: the exact kwarg name depends on the installed `claude-agent-sdk` version. Cross-reference `warroom-shadowheart/agent_sdk_llm.py` and the SDK README at install time. Rename if different.

- [ ] **Step 3: Sync deps**

```bash
cd agent_loop
uv sync --all-groups
```

- [ ] **Step 4: Dry-run the CLI (arg parsing only)**

```bash
uv run sts2-run --help
```
Expected: help text prints without errors.

- [ ] **Step 5: Commit**

```bash
cd ..
git add agent_loop/runner.py
git commit -m "feat(agent): runner CLI with persistent ClaudeSDKClient and SQLite logging"
```

### Task D.4: End-to-end smoke run

**Files:** none (verification only)

- [ ] **Step 1: Launch STS2, enable mods, return to main menu**

User action.

- [ ] **Step 2: Run a single run**

```bash
cd "C:/Users/user/Desktop/STS2MCP/agent_loop"
uv run sts2-run --runs 1 --character ironclad --seed 12345 --effort low --log-dir ./logs
```
Expected: the game embarks, plays, dies (or wins), returns to menu; the terminal prints `[run 1/1] seed=12345 floor=X died=True tokens_in=... tokens_out=...`.

If the run hangs or desyncs: check `logs/runs.db` for the last tool call; diagnose from there. Common issues:
- Action name mismatches between Python tools and C# handlers (fix in server.py, rerun).
- MCP server crash (inspect stderr from `sts2-run`).
- `play_turn` interrupting on a state change we didn't anticipate (extend `INTERRUPTING_STATE_TYPES` or add detection for it).

- [ ] **Step 3: Inspect the log**

```bash
uv run python -c "import sqlite3; c=sqlite3.connect('./logs/runs.db'); print(c.execute('SELECT * FROM runs').fetchall()); print(len(c.execute('SELECT * FROM tool_calls').fetchall()), 'tool calls')"
```
Expected: one row in `runs`, many rows in `tool_calls`.

- [ ] **Step 4: Run the 3-run test**

```bash
uv run sts2-run --runs 3 --character ironclad --seed 10000 --effort low --log-dir ./logs
```
Expected: three runs recorded in `runs.db`. No human interaction between them.

If this passes, success criterion 4 from the spec is met.

- [ ] **Step 5: Commit any adjustments discovered during smoke test**

Final `git status` should be clean. If tweaks were needed to action names or tool signatures, commit those with descriptive messages.

---

## Completion checklist

When all Group D tasks pass the smoke test:

- [ ] Push to fork: `git push origin main`
- [ ] Record before/after wall-clock for one combat turn (spec success criterion 2). Update the spec with measured numbers as a trailing "Results" section.
- [ ] Update `AGENTS.md` if smoke-run surfaced new LLM pitfalls.
- [ ] Confirm `docs/raw-full.md` covers all new endpoints added in Group A. If missing, extend it.

---

## Scope reminder

Not in this milestone (see spec non-goals):
- Compact state format (`format: "compact"`)
- Model tiering / router layer
- Rule-based auto-play for forced combat moves
- Upstreaming PRs to `Gennadiyev/STS2MCP`

If any of these feel urgent after Milestone 1 lands, write a fresh spec.

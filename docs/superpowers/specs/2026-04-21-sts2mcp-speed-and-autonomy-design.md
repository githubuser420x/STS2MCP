# STS2MCP Fork — Speed & Autonomy (Phase B Milestone 1)

**Date:** 2026-04-21
**Fork:** `githubuser420x/STS2MCP`
**Upstream:** `Gennadiyev/STS2MCP` (basis: `Gennadiyev:main` @ v0.3.5-rc1)
**Related prior art:** `romgenie/STS2MCP` (41 commits ahead of upstream), upstream PR #34, upstream PR #44

## Motivation

A diagnostic run on Sonnet 4.6 with `/effort high` showed turns are slow **everywhere** — not just at strategic branch points. The per-turn bottleneck is LLM invocation time, not animation latency or protocol overhead. This means the highest-value changes attack two dimensions:

1. **Fewer, fatter LLM calls** — cut the number of round-trips per game turn.
2. **Unattended looping** — move from interactive Claude Code (one run at a time, human clicks through menus between runs) to a programmatic Agent SDK loop that can embark, play, detect death, and restart without human input.

Both depend on plumbing that upstream does not ship:

- Upstream has no action to start a run, no `game_over` state detection, no tutorial/FTUE auto-dismiss, no seed support, no glossary endpoints. `romgenie/STS2MCP` implements all of these in C# but did not expose them as Python MCP tools and was ignored by the maintainer (discussion #43, silent close of related work).
- Upstream PR #34 (open, unmerged) fuses a successful POST action with the subsequent `get_game_state` so combat plays become single round-trips. This is a free ~50% cut on combat MCP calls that nobody has pulled.

This design adopts `romgenie` as the mod base, closes the Python-side gap, and layers an autonomous Agent SDK loop on top.

## Goals

- Combat turns average ≤1 LLM call per card played (currently 2 per card — one for `play_card`, one for `get_game_state`).
- A single script can start N seeded runs unattended: embark → play → detect death → restart.
- Loopback-only network binding by default; LAN binding requires opt-in.
- Agent loop reuses the proven `claude-agent-sdk` pattern from `warroom-shadowheart/agent_sdk_llm.py`.

## Non-goals

- State format compression (`format: "compact"`) — deferred; insufficient evidence that state size is the active bottleneck.
- Model tiering / router layer (Haiku for tactical, Sonnet for strategic) — deferred; effort-level and call-count fixes may eliminate the need.
- Rule-based auto-play for forced combat moves — deferred; overlaps with `play_turn` batching and muddies measurement.
- Upstreaming to `Gennadiyev/STS2MCP` — out of scope. This is a private fork.
- C# mod changes beyond rebasing onto `romgenie` and the one safety patch in scope item 3 (loopback-default binding). Any other C# work surfacing during implementation breaks scope and triggers a re-plan.

## Scope (seven work items)

### 1. Rebase fork on `romgenie/main`

Add `romgenie` as a remote, merge `romgenie/main` into `origin/main` with a merge commit. Non-destructive. Captures:

- Mod: `menu_select`, `game_over` state detection, `timeline_advance`, seed support, profile list/switch/delete, FTUE/tutorial dismiss, richer state across screens (always include player+map data), glossary + bestiary REST endpoints.
- Rebuilds `STS2_MCP.dll` from romgenie's sources (requires .NET 9 SDK installed; not yet installed on the PC).

### 2. Wire romgenie's new HTTP endpoints into `mcp/server.py` as MCP tools

Without this, Claude cannot call any of romgenie's new backend. New tools (Python wrappers over existing REST):

- `start_run(character: str, seed: int | None = None, ascension: int = 0)` — navigates menu → character select → embark.
- `menu_select(option: str)` — generic menu nav.
- `game_over_continue()` / `game_over_return_to_menu()` — post-death flow.
- `get_glossary_cards()` / `get_glossary_relics()` / `get_glossary_potions()` / `get_glossary_keywords()` — static reference data, queried once per session and prompt-cached.
- `get_bestiary()` — static enemy reference.
- `profile_switch(index: int)` / `profile_list()` — profile management.
- `timeline_advance()` — auto-click epoch reveals.
- `dismiss_ftue()` — auto-dismiss any tutorial prompt.

Each tool is a thin `async def` that calls the existing REST endpoint via `httpx` and returns the JSON payload, matching the style of current tools in `server.py`.

### 3. Default to loopback binding with LAN opt-in

romgenie binds `http://+:{port}/` first. We change the attempt order in `McpMod.cs::StartHttpServer` so loopback (`127.0.0.1` + `localhost`) is tried first, and wildcard binding is attempted only when `allow_lan: true` is present in `STS2_MCP.conf`. This is a small C# change that does fall inside scope because it's a safety issue introduced by adopting romgenie.

### 4. Cherry-pick upstream PR #34

Pull commits from `Gennadiyev/STS2MCP#34` into the fork. This merges POST action results with `get_game_state` inside the Python MCP server and polls `is_play_phase` server-side. Eliminates the "call get_game_state twice" workaround baked into `AGENTS.md`. Cuts combat round-trips roughly in half.

### 5. New `play_turn` MCP tool

Takes an ordered list of combat actions:

```python
play_turn(actions=[
    {"action": "play_card", "card_index": 2, "target": "JAW_WORM_0"},
    {"action": "play_card", "card_index": 1},
    {"action": "end_turn"},
])
```

Execution:

1. Python iterates the list. For each action, POST to the mod.
2. After each POST, fetch state. If state differs from the expected trajectory in any of these ways, stop early and return `{executed: N, remaining: [...], state: <current>, reason: <why>}`:
   - New cards were drawn into hand (changes indices Claude planned against).
   - A `hand_select` / `card_select` / `bundle_select` overlay opened.
   - An enemy died (triggers powers, scales, relics; Claude may want to retarget).
   - Action returned `status: "error"`.
3. If the full list executes without interruption, return the final state.

Claude's turn loop becomes: `get_state → plan turn → play_turn → get_state`. Typical combat turn: 3–5 LLM calls collapse to 1.

### 6. Update `AGENTS.md`

Document the new tools (scope 2, 5) and the autonomy flow (start_run → play → game_over → start_run). Update the "Sometimes you need to call get_game_state twice" note to reflect that PR #34 cherry-pick has eliminated it. Keep the existing strategy guide sections.

### 7. Python Agent SDK loop (`agent_loop/runner.py`)

A standalone Python script that runs unattended runs. Based on the pattern in `warroom-shadowheart/agent_sdk_llm.py`:

- Single persistent `ClaudeSDKClient` session per loop invocation (warm cache, low TTFB).
- Loads the sts2 MCP server config and the full tool set.
- System prompt = distilled `AGENTS.md` strategy guide + current objectives.
- Main loop: call `start_run(character, seed)` → stream actions until `game_over` → log results to SQLite → if run count remaining, loop.
- CLI flags: `--runs N`, `--character ironclad|silent|...`, `--seed <int>`, `--model sonnet|haiku`, `--effort low|medium|high`, `--log-dir <path>`.
- Logs per run: seed, character, floor reached, tokens used (from SDK usage callbacks), decision log (every tool call + response), final HP / deck / relic snapshot.

This is how we go from "watching one run in a chat window" to "kick off 50 seeded runs overnight and analyse."

## Architecture

```
+-------------------------------+
| agent_loop/runner.py (new)    |  CLI / orchestration
| persistent ClaudeSDKClient    |
+---------------+---------------+
                | MCP stdio
                v
+-------------------------------+
| mcp/server.py (modified)      |  MCP tool surface
|  - existing tools             |
|  - PR#34 round-trip merger    |
|  - new: start_run, menu_...   |
|  - new: play_turn batch       |
|  - new: glossary_*, bestiary  |
+---------------+---------------+
                | HTTP localhost:15526
                v
+-------------------------------+
| STS2_MCP.dll (romgenie base)  |  C# mod inside STS2
|  + loopback-default patch (#3)|
+---------------+---------------+
                | in-process Godot/.NET
                v
            Slay the Spire 2
```

Execution path for one typical combat turn after the changes:

1. Runner asks SDK for an action.
2. Claude calls `get_game_state` → one MCP/HTTP round-trip.
3. Claude plans the whole turn, calls `play_turn([...])` — one MCP/HTTP call from Claude's perspective, N sequential HTTP POSTs from Python's.
4. Python returns final (or interrupted) state in the same MCP response — Claude gets the updated state for free without a separate `get_game_state`.

Between runs:

1. `game_over` state appears.
2. Runner logs run results.
3. Runner calls `game_over_return_to_menu` → `start_run(character, next_seed)`.
4. No human interaction required.

## Risks & tradeoffs

- **Building `STS2_MCP.dll` from romgenie's source requires .NET 9 SDK, not yet installed.** Install is a `winget install Microsoft.DotNet.SDK.9` step during implementation setup; it must be verified before scope 1 begins.
- **romgenie's fork is a maintained but independent line.** Future upstream changes (especially PR #44 structural refactor) will not automatically merge cleanly. Re-syncs will need manual attention.
- **`play_turn` correctness depends on correctly enumerating "unexpected" state changes.** If we under-specify the interruption conditions, Claude's plans desync from reality (plays the wrong card after a draw). Implementation must err toward interrupting too often, not too rarely.
- **LAN binding default change is in C#.** Requires a rebuild of the mod. Low-risk code change; still scope creep compared to a pure Python milestone.
- **Agent SDK loop uses the `claude` CLI's Max subscription auth.** Rate limits apply. An N=50 unattended run overnight may hit weekly caps on Max 5x. Log token usage per run; surface cumulative in the loop CLI.
- **Documentation rot.** `AGENTS.md` has to stay in sync with the growing tool surface. Update as part of each scope item, not at the end.

## Testing

- Smoke test after scope 1: mod builds, loads in STS2, `curl http://localhost:15526/` returns hello with romgenie's version. Existing tools still callable from a fresh Claude Code session.
- Per-tool unit check after scope 2: hit each new endpoint via `curl` against a running game, confirm JSON shape matches the MCP wrapper's expectations before wiring as an MCP tool.
- Manual A/B after scope 4 (PR #34): run five combat turns before and after the cherry-pick; compare total MCP-call count via mod-side logs.
- `play_turn` interruption test after scope 5: construct a scripted combat scenario where playing card A draws card B; verify `play_turn` stops at the draw, returns partial execution result, and updated state. Run this manually before declaring the tool done.
- End-to-end test after scope 7: `agent_loop/runner.py --runs 3 --character ironclad --seed 12345 --effort low`. Three seeded runs complete unattended; logs contain a full decision trace for each.

## Success criteria

1. A fresh Claude Code session (or `agent_loop/runner.py`) can start a run, play to completion, detect death, and start a second run without any human clicks in STS2.
2. Combat turn wall-clock on Sonnet `effort=low` shows a measurable drop versus the pre-change baseline captured today (2026-04-21).
3. `docs/raw-full.md` and `AGENTS.md` are updated to describe every MCP tool callable after the milestone lands.
4. An overnight run of `--runs 10 --character ironclad` completes without manual intervention and produces 10 SQLite-logged run records.

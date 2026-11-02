# PGR client mods

Pure Lua/table overrides loaded through PGR's own external loaders: no injected native code or `matrix.ab` changes. The client needs remote-config `Debug = 1`; turn on **Client mods (game debug mode)** under **SETTINGS → CLIENT MODS** and restart the server and game. The server defaults remain off. Debug enables extra client checks and is intended for local research, not normal play.

## Launcher workflow

Setup retains a source checkout under the launcher's local runtime root. **SETTINGS → CLIENT MODS** reads `<local runtime root>/checkout/Mods` directly, not a copied build snapshot. Setup refreshes that checkout even when it can reuse cached server binaries. Repository `Mods/` is the source of truth; no mods are embedded in the launcher executable.

Close the game before changing installed overrides. Turn on **Client mods (game debug mode)**, then switch each mod ON in its own row. Switch changes save and apply immediately—there is no Apply button. Restart the server and game afterward. Applying builds overrides from the selected client's shipped modules and installs them under that client's persistent data directory. If a shipped-module diff does not match the installed client, the whole affected mod is skipped with a compatibility reason; other compatible selected mods can still apply. A switch that is ON is not evidence that a skipped mod was installed. Errors appear in the status and activity log. The master switch must also be enabled for the client to load overrides; it does not enable the Proxima inspector.

The installed-file manifest is `Lua/Matrix/.ascnet-mods.json`, containing paths relative to the persistent data directory. Removal touches only tracked mod outputs, not unrelated Lua/table files. Existing untracked overrides are never overwritten: an output collision stops application rather than backing up or replacing the user's file. See [player diagnostics](../AscNet.Launcher/docs/player-diagnostics.md) for reporting apply errors.

Setup and Play reapply the saved master switch and mod selections before starting the server. **Restore** removes only tracked overrides and sets server Debug off; it retains the saved mod preferences, so the next Play reapplies them.

The target is resolved from the selected client's `PGR_Data/app.info` company/product values and Windows' LocalLow known folder, including folder redirection; it is not hardcoded to the EN `kurogame/PGR` directory.

## Included mods

`core/` is installed whenever client mods are installed (the launcher never shows a switch for it; the research builder always installs it). It has `"core": true` in `mod.json` and provides:

- `XAscNet/XAscNetSubMenu`: the only code that wraps `XUiConfigs.GetSystemSubMenuList` and `XFunctionManager.SkipInterface`. Mods add main-screen second-menu entries with `XAscNetSubMenu.Add{ key, skipId, title, subtitle, icon, open }`; re-adding a key replaces it, and the wrap survives hot reload. Entries are appended in Lua after the game's time filter (a table row would be hidden without an open server-sent TimeId) with the full `UiMainSubMenu` row shape (list columns are empty tables), and a skip ID that must not exist in the shipped `SkipFunctional.tab` (4.8: highest 11080106). IDs in use: `990001` GM Menu, `990002` Mod List.
- `XAscNet/XAscNetPanel`: the runtime-built UGUI overlay panel (`XAscNetPanel.New{...}`), shared by the GM menu and the Mod List. Esc or Close hides it.
- `XAscNet/XAscNetModList`: the **Mod List** entry in the main screen's second menu. It lists each installed mod (name, description, `[core]` marker, module and table counts, modules) with a **Reload** button that calls `XHotReload.Reload` for each of the mod's modules that is already loaded.
- `XManager/XLoginManager.diff`: appends a guarded `require` of `XAscNet/XAscNetModList`, so the entry exists before the main screen builds its grid.

Both builders also write `Lua/Matrix/XAscNet/XAscNetModCatalog.lua`, the generated list of installed mods (id, name, description, `core`, installed `modules` and `tables`; core first, then by id) that the Mod List reads. It is tracked in the manifest and removed with the other outputs; do not edit it.

`gm-menu/` revives the built-in `XManager/XGmTestManager` menu. A Lua UGUI panel (`XAscNet/XAscNetGmPanel`, built on core's panel) replaces the stripped C# panel, with English labels in `XAscNet/XAscNetGmLabels`. **Ctrl+Shift+D** toggles it; menus are built on first open. The main screen's second menu also has **GM Menu** (registered through `XAscNetSubMenu`). The extra **AscNet** submenu hot-reloads a module by name.

Commands needing stripped C# debug APIs or unsupported server GM requests report errors in the panel; installing the menu does not implement those commands.

## Authoring

Each direct child directory is a mod; its directory basename is its stable ID. `mod.json` supplies display metadata:

```json
{"name": "GM Menu", "description": "The game's built-in GM menu: Ctrl+Shift+D, or GM Menu in the main screen's second menu."}
```

The launcher shows the description on one line under the mod's switch (about 90 characters fit).

- `<name>/<Module/Path>.lua`: a new module, copied unchanged.
- `<name>/<Module/Path>.diff`: a unified diff against the shipped module. The launcher requires exact context, with no fuzz or offset. Generate with `diff -u --label X.lua --label X.lua original modified`.
- `<name>/Table/<path>.tab`: a whole replacement table at the exact requested path; header row, tab-separated, CRLF. Keep EN text ASCII (the client decodes GBK). Tables are indexed at game start, so restart after changes.

Directories beginning with `_` are ignored. Set `"core": true` only for always-installed foundations (hidden from the launcher's switches, listed first in the Mod List). Two selected mods cannot own the same output; core owns `XManager/XLoginManager`. Do not commit extracted shipped originals here.

## Research tools

From the parent research root (the directory containing `AscNet/`):

```sh
python3 tools/pgr_lua_mods.py extract XManager/XGmTestManager
python3 tools/pgr_lua_mods.py build
python3 tools/pgr_lua_mods.py clean
```

The tools read this repository's `AscNet/Mods/`; extraction still writes to research `mods/_original/`, which is never installed. Research `mods/login-marker/` remains a separate example, not part of the launcher catalogue. The Python builder applies every mod in this directory, rather than the launcher's per-mod switches.

The research builder syntax-checks Lua with `$LUAC`, `luac5.3`/`luac5.4`, or Homebrew `lua@5.4`; if unavailable it warns and skips that check. Lua 5.5 is unsuitable because it rejects valid shipped 5.3 code. Its default target is the Sikarugir Steam-AscNet prefix's `AppData/LocalLow/kurogame/PGR/Lua/Matrix`; use `--game-dir /path/to/game --target /path/to/Lua/Matrix` for another install. It replaces only the previously tracked set after building successfully. Do not use the research installer and launcher against the same live game simultaneously.

## Hot reload and live probes

With Debug enabled, use the overlay Lua console or the GM menu's AscNet submenu:

```lua
XHotReload.Reload("XManager/XGmTestManager")
```

Reload copies new functions into loaded modules while preserving class identities. UI changes appear the next time the screen opens; stored direct function references keep the old body. A load error is logged and the old module stays. Table changes still require restart.

The parent research tool `tools/pgr.py` can run Lua and hot-reload through Proxima. It also requires `Client.ShowInspector` and the inspector open (tap the player-info like count five times):

```sh
python3 tools/pgr.py lua 'return XMain.IsDebug'
python3 tools/pgr.py lua -f probe.lua
python3 tools/pgr.py reload XManager/XGmTestManager
python3 tools/pgr.py watch
python3 tools/pgr.py cmd '?'
```

`watch` observes `AscNet/Mods/`, rebuilds with the research installer, and hot-reloads changed modules. Proxima uses `ws://<host>:7378/api` and password `HaruGames`; it listens on **all interfaces**, so enable it only on a trusted network. Game Lua failures appear in the latest `PGR/log/` log with tracebacks.

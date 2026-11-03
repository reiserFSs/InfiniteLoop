---
name: pgr-lua-mods
description: Use for PGR client Lua/table mods and live in-game Lua probes through the debug loaders and the Proxima inspector (Scripts/pgr.py), incl. character bones/blend shapes.
---

# Preconditions
- Server config `Resources/Configs/config.json` (server cwd is `Resources/`): `"Client": { "Debug": true, "ShowInspector": true }`. Restart server AND game. Never commit these local values.
- persistentDataPath: Windows `%USERPROFILE%\AppData\LocalLow\kurogame\PGR`; on macOS/Linux inside the Wine prefix at the same relative path. The launcher resolves it from `PGR_Data/app.info` (other regions differ).
- Repo mod sources and the launcher workflow: `Mods/README.md`.

# Loaders (4.8 EN)
- Lua: `PGR/Lua/Matrix/<Module/Path>.lua` plain text, loaded before the shipped module; missing -> shipped, broken -> error.
- Tables: `PGR/Table/<exact requested path>` e.g. `Client/Ui/UiMainSubMenu.tab`: whole-table TSV, header row, list columns `Name[1]`, tab-separated, CRLF, ASCII (EN decodes GBK), case-sensitive, indexed at game start.
- Hot reload: `XHotReload.Reload("<module>")`. UI applies when the screen next opens; stored function references keep old bodies.
- Errors: newest `PGR/log/*.log`, `<Error>` lines with Lua tracebacks.

# Live harness: Scripts/pgr.py
Open the inspector in game (player info page, tap the like count 5 times). It listens on port 7378 on all interfaces (use only on a trusted network); `Scripts/pgr.py` handles the handshake.
```
python3 Scripts/pgr.py [--host H] lua '<code>'      # prints the chunk's value or a traceback (exit 1)
python3 Scripts/pgr.py lua -f probe.lua
python3 Scripts/pgr.py reload <module>
python3 Scripts/pgr.py watch                          # rebuild + hot-reload on save
python3 Scripts/pgr.py cmd ? | cmd ls <name> | ...    # raw Proxima commands
```
Keep state across calls in `rawset(_G, "__pgr_x", ...)`. Hooks and state vanish on game restart.

# Character models (4.8)
- Model root `R<n><Name>Md<skin>(Clone)`, SkinnedMeshRenderers per part (Upper, Body, Down, Hair, Face, Eye...); Biped rig `Bip001*`. Main-screen character is in scene `XGame`, inactive while other screens cover it.
- Meshes are not readable (no vertex edits); materials are per-instance clones. Face mesh has 65 blend shapes.
- The Animator does not write bone scale (`localScale` edits stick) but rewrites every face blend shape each frame: set them in a late update: `go:AddComponent(typeof(CS.XLuaBehaviour)).LuaLateUpdate = function() ... end`; clear with `= nil`.
- Persistent mods must reapply on model load.

# Pitfalls
- Lua-made data given to game code needs the full table-row shape (list columns = `{}`), or red-point checks crash a whole screen.
- Main-screen second-menu rows need an open server TimeId; append permanent entries in Lua after the filter.
- XClass tables with Debug: reassigning a method logs a duplicate definition; write into the class `vtbl` upvalue.
- Never `require` a game module early (top-level `XDataCenter` reads); a throw after `XLuaUiManager.Register` leaves an empty class.
- Use `GetComponent(typeof(CS.UnityEngine.UI.X))`, not string names. Boot-time code must never throw.
- Lint Lua with a 5.3/5.4 `luac -p` (newer Lua rejects valid 5.3 code).

# Player diagnostics (lucia / KRSDK)

Both native patches (`lucia.dll`, `PGR_Data/Plugins/KRSDK.dll`) append every diagnostic line to one file
beside `PGR.exe`:

```
<client folder>\ascnet-patch.log
```

(The same lines are also printed to stdout, which only exists when `PGR.exe` is started from a console or
Wine.) The file is cut when it grows past 1 MiB. Each launch appends, so send the **last launch**: everything
from the last `[lucia] OK KRSDK.bin identity` / `[lucia] FAILED KRSDK.bin identity` line to the end.

## What to send back

1. The region you play (EN / TW / KR / JP / CN).
2. All lines of `ascnet-patch.log` starting with `[lucia]` or `[KRSDK]` that contain `OK` or `FAILED`
   (or just the whole file).
3. If a line says `FAILED`, nothing else is needed: the reason and the looked-up name are in that line.

## Healthy launch (KR example)

```
[lucia] OK KRSDK.bin identity: package=com.herogame.pc.punishing.grayraven.kr game=G286 app=A1794 channel=240 channel_name=PC channel_op=null source=PGR_Data/Plugins/KRSDKRes/KRSDK.bin
[lucia] OK GameAssembly.dll module lookup: base=0x7FF8A1230000
[lucia] OK il2cpp exports: 12 il2cpp_* exports resolved from GameAssembly.dll
[lucia] OK il2cpp domain ready: assemblies=187 waited_ms=1200
[lucia] OK lookup assembly+class: UnityEngine.UnityWebRequestModule.dll!UnityEngine.Networking.UnityWebRequest
[lucia] OK lookup UnityEngine.UnityWebRequestModule.dll!UnityEngine.Networking.UnityWebRequest::InternalSetUrl(System.String)->System.Void: method at 0x7FF8A2345678
[lucia] OK routing origin: http://127.0.0.1:8080
[lucia] OK hook install: UnityEngine.Networking.UnityWebRequest::InternalSetUrl detour at 0x7FF8A2345678
[KRSDK] OK KRSDK.bin identity: package=com.herogame.pc.punishing.grayraven.kr game=G286 app=A1794 channel=240 channel_name=PC channel_op=null source=PGR_Data/Plugins/KRSDKRes/KRSDK.bin
[KRSDK] OK lucia.dll routing initialization
[lucia] OK first client config request: package=com.kurogame.punishing.grayraven.kr cdn_key=jqlCmYRizwT76uvX
```

What each region must show (from the client's own files, not from the server):

| Region | `KRSDK.bin` `game` / `app` | `first client config request` package |
| ------ | -------------------------- | ------------------------------------- |
| KR | `G286` / `A1794`, channel `240` | `com.kurogame.punishing.grayraven.kr` |
| JP | `G282` / `A1778`, channel `240` | `com.kurogame.punishing.grayraven.jp` |

If `first client config request` is missing, the game never asked for `client/config/...`: the hook is
not active, or the client talks to a host the patch does not route.

## Failure lines

Format: `[lucia|KRSDK] FAILED <what was looked up>: <reason> | send: ascnet-patch.log (beside PGR.exe) and the client region`

| Line starts with | Meaning |
| ---------------- | ------- |
| `[lucia] FAILED KRSDK.bin identity` | `PGR_Data/Plugins/KRSDKRes/KRSDK.bin` is missing/unreadable or lacks a `KR_*` key (the key is named). Client install incomplete. |
| `[lucia] FAILED GameAssembly.dll module lookup` | `lucia.dll` was loaded outside `PGR.exe`'s IL2CPP process. |
| `[lucia] FAILED native routing ...: ... GameAssembly.dll does not export `il2cpp_...`` | The named IL2CPP export is missing: not the 4.8.0 client, or `GameAssembly.dll` was modified. |
| `[lucia] FAILED native routing ...: il2cpp_domain_assembly_open could not open assembly ...` | The assembly named in the line is not in this client's metadata. |
| `[lucia] FAILED native routing ...: il2cpp_class_from_name: type ...` / `method ...` | The class or method named in the line is missing or has another signature: Unity build differs from 4.8.0. |
| `[lucia] FAILED native routing ...: installing the InternalSetUrl detour failed` | Another injector/overlay hooked the same function. Send the list of overlays/injectors you run. |
| `[lucia] FAILED il2cpp domain ready` | IL2CPP still had no assemblies after 60 s (lucia keeps waiting). |
| `[KRSDK] FAILED lucia.dll routing initialization` | `lucia.dll` is missing next to `PGR.exe`, blocked (antivirus), is an old build, or its own `[lucia] FAILED` line (above in the same file) explains why. |
| `[KRSDK] FAILED KRSDK.bin identity` / `kurosdk_getConfigInfo packaged config` | Same as the `[lucia] FAILED KRSDK.bin identity` row; login is refused without a channel. |
| `[KRSDK] FAILED login channel from KRSDK.bin` | `KR_ChannelID` could not be read at login. |

When `lucia` fails, native routing stays off and the client talks to the retail servers.

## launcher.log (beside `AscNet.Launcher.exe`)

Setup, Restore, Play and the official-client download/update each write one line naming the selected game folder, the
detected region (or why detection failed), the patch state before the action, and the outcome or the full error:

```
Setup: game=D:\Games\PGR region=CN patch=Unpatched
Play: failed: refusing installation: unsupported client: ...
```

The region comes from the SDK files: `KRSDKEx.dll` + `KRSDKRes/KRSDKConfig.json` without `KRSDKRes/KRSDK.bin` is CN, even
if a `KRSDK.dll` is present.

## Local server logs (`%LOCALAPPDATA%\AscNetLauncher\local\logs\`)

`server.log` and `mongod.log` hold the local backend's output. Each start moves the previous run to `server.previous.log`
(`mongod.previous.log`), and a file that reaches 32 MiB during a run is moved there too, so each log keeps at most two
files and the newest lines are always in `server.log`. Send both server files when reporting a bug.

How much the server writes is set by `VerboseLevel` in `%LOCALAPPDATA%\AscNetLauncher\local\config.json`; the change
applies the next time the launcher starts the server:

| `VerboseLevel` | Logged |
|---|---|
| `Silent` | No packet lines; other server messages (startup, warnings, errors) still appear. |
| `Normal` | One line per request (sizes only), response and push (names only). |
| `Debug` (default) | Same lines, with the full response/push content that bug reports need. |
| `SuperDebug` | Also the heartbeats, which every lower level skips. |

Leave the `GameServer` and `Database` entries unchanged; the launcher refuses to start the server if they no longer match Setup.

## Client mods

**SETTINGS → CLIENT MODS** reads the retained checkout's `Mods/` directory under the local runtime root, not an embedded
catalogue or a copied build directory. Setup refreshes this checkout even when reusing cached server binaries.
See [the mod catalogue and authoring guide](../../Mods/README.md).

Close `PGR.exe` before changing a selection. Turn on **Client mods (game debug mode)** for the external
Lua/table loaders, then switch individual mods on or off; every mod has its own ON/OFF row with its description.
Changes save and apply immediately; there is no Apply button. Restart the server and game afterward. The master
switch defaults off and does not enable the Proxima inspector. The GM menu opens with **Ctrl+Shift+D** or the main screen's second-menu
**GM Menu** button. Some GM commands need stripped debug APIs or unsupported server requests and report errors;
that is different from an installation failure.

When reporting a mod problem, include the selected client folder and region, selected mod IDs, whether the master
switch is enabled, the complete status/activity-log apply result or compatibility reason, and relevant `launcher.log` lines.
For a Lua error, also include the traceback from the newest log under the client's persistent data directory's
`log/`. The launcher resolves that directory using the selected client's `PGR_Data/app.info` company/product and the
Windows LocalLow known folder, respecting redirection; do not assume another region's `kurogame/PGR` path.
A diff mismatch means the shipped source differs from the mod's expected source; the whole affected
mod is skipped rather than approximately applied. Do not edit a diff merely to suppress the compatibility warning.

Installed overrides live under `Lua/Matrix/` and `Table/` in that persistent directory. The ownership manifest is
`Lua/Matrix/.ascnet-mods.json`; removal uses this manifest and preserves unrelated files. Do not delete the manifest
to uninstall a mod, or run the research installer concurrently with launcher mod actions.

Setup and Play reapply saved mod preferences before starting the server. **Restore** removes tracked overrides and
sets server Debug off, but retains the saved master switch and individual selections; the next Play reapplies them.

## China (CN) client

The CN client (战双帕弥什) has no `KRSDK.dll` and no `KRSDK.bin`, and a CN folder must not contain `KRSDK.dll`: Setup moves a
leftover one (an old patch or a manual copy) into `.ascnet-launcher/backups/` and Restore puts it back; it ships the official SDK
(`PGR_Data/Plugins/KRSDKEx.dll`, `libkrsdkcurl.dll`, `KRSDKRes/KRSDKConfig.json`). The Launcher installs only
`version.dll`, `lucia.dll`, `libraries.txt` and the generated `PGRBase.dll`; `PGR.exe`, `GameAssembly.dll`, `KRSDKEx.dll`
and `libkrsdkcurl.dll` are only hash-checked against `supported-client.json` (never modified or backed up), and
an unknown hash is reported as unsupported with the observed SHA-256. `lucia` redirects the SDK's `/sdkcom/*` requests in
process (a hook on `libkrsdkcurl.dll!kr_sdk_curl_easy_setopt`), so the official login UI stays in use.

Healthy CN lines in `ascnet-patch.log` (the same `routed` line appears per redirected SDK request, query strings are never logged):

```
[lucia] OK KRSDKConfig.json identity: package=com.kurogame.haru.hero game=G148 app=A1393 channel=19 channel_name=国内PC source=PGR_Data/Plugins/KRSDKRes/KRSDKConfig.json
[lucia] OK CN SDK hook install: PGR_Data/Plugins/libkrsdkcurl.dll!kr_sdk_curl_easy_setopt detour at 0x..., routing /sdkcom/* to http://127.0.0.1:8080
[lucia] routed https://sdkapi.kurogame.com/sdkcom/v2/sys/conf.lg -> http://127.0.0.1:8080/sdkcom/v2/sys/conf.lg
```

| Line starts with | Meaning |
| ---------------- | ------- |
| `[lucia] FAILED KRSDKConfig.json identity` | `KRSDKConfig.json` is missing/unreadable. |
| `[lucia] FAILED CN SDK routing (login stays on retail servers)` | `libkrsdkcurl.dll` could not be loaded or lacks `kr_sdk_curl_easy_setopt` (not the verified 4.8.0 CN SDK), or another injector hooked the same function. |

No `routed ... /sdkcom/...` line after the login window opens means the SDK never reached the hook. The SDK also keeps
its own cache under `%APPDATA%\KR_G148\A1393\` (agreement, `KRSDKCache.json`) and logs/cache under
`%USERPROFILE%\AppData\LocalLow\kurogame\战双帕弥什` (per the real player log); include them when reporting a CN login problem.

## FPS unlock and no camera fade

Both are plain keys of `ascnet-graphics.ini` (see the table below): `fps = <n>` and `nofade = on`. There are no
environment variables and no extra threads; the graphics worker applies them. The launcher has no controls for them.
Three ways to set them, all using the same file:

- The **AscNet** tab in the game's Settings screen applies and persists changes live; no separate Save is needed.
- The in-game overlay (`overlay = on`, Ctrl+Shift+G): **Unlock FPS** + target field (enabling uses the last value, else
  240; values below 1 are refused inline) and **No camera fade** apply live within 0.5 s; **Save to ascnet-graphics.ini**
  rewrites the whole file.
- Editing the file by hand (read at game start).

One-time migration: values saved by older launchers (FPS unlock, frame-rate field, no camera fade) are copied once into
the selected client's `ascnet-graphics.ini`, only for keys the file does not have yet, and then dropped from the
launcher's settings.

Same on EN/TW/KR/JP.

**Migration from the old bundle patch.** Earlier launchers injected a Lua hook (`PgrNativeFpsTimer`) into
`matrix.ab`'s `XUiMain.lua`. On every Check/startup the launcher looks for that hook; if found, and PGR.exe is not
running, it removes it (the usual same-size rewrite, original kept under `.ascnet-launcher/fps-backups/`), writes
`fps = <the hook's old value>` to `ascnet-graphics.ini`, and logs `Removed the old bundle FPS patch ...`. While PGR.exe
is running it logs `the old bundle FPS patch (N FPS) is still installed; close PGR.exe and press Check to remove it`
(the hook keeps working until then). Afterwards only the native path exists; the launcher never writes the bundle again.

Healthy lines (`fps = 240`, `nofade = on`):

```
[lucia] OK graphics options requested: fps = 240, nofade = on in ascnet-graphics.ini; applying once IL2CPP is ready
[lucia] OK lookup UnityEngine.Application: assembly=UnityEngine.CoreModule.dll
[lucia] OK lookup Application.set_targetFrameRate(Int32)/get_targetFrameRate(): static property accessors
[lucia] OK lookup UnityEngine.QualitySettings.vSyncCount: assembly=UnityEngine.CoreModule.dll
[lucia] OK lookup XCameraDitherDetector: assembly=Assembly-CSharp.dll
[lucia] OK lookup XCameraDitherDetector.UseCameraDither: static System.Boolean
[lucia] OK lookup XNpcDither: assembly=Assembly-CSharp.dll
[lucia] OK lookup XNpcDither.SetPlayerSelfDitherParameter(...PlayerSelfDitherParameter): static, 1 parameter; game value read from playerSelfDitherParameter
[lucia] OK FPS unlock: targetFrameRate=240, vSyncCount=0; the game's own values are targetFrameRate=-1, vSyncCount=1
[lucia] OK apply FPS unlock: targetFrameRate=240, vSyncCount=0 held every 0.5 s
[lucia] OK no camera fade: applied; the game's own values are UseCameraDither=true, playerSelfDitherParameter=PlayerSelfDither { ... }
[lucia] OK FPS unlock: removed, restored targetFrameRate=-1, vSyncCount=1        (option switched off in the overlay)
[lucia] OK no camera fade: removed, restored UseCameraDither=true, playerSelfDitherParameter=...
```

Ultrawide healthy lines (`ultrawide = on`; the first line repeats until the game's Lua has initialised):

```
[lucia] OK Lua bridge: XLuaEngine.Update at 0x... runs queued chunks through LuaEnv.DoString on the main thread
[lucia] OK lua ultrawide: the game's Lua is not initialised yet; retrying every 0.5 s
[lucia] OK ultrawide: was 1920x1080 windowed, ForceWindow=true; monitor 5120x1440; resolution table +[5120x1440, 2560x1080, 3440x1440]; requested fullscreen window
[lucia] OK ultrawide: screen now 5120x1440 fullscreen
```

Ultrawide failures: `FAILED Lua bridge: ...` (class, method or export missing in this build; ultrawide is unavailable and the
overlay shows the reason) and `FAILED lua ultrawide: <Lua error text>` (the chunk threw; shown in red in the overlay, the
game keeps its current display mode).

Failures only affect their own option; the game keeps its own value and routing is unaffected:
`FAILED ascnet-graphics.ini: line N: ...` (`fps` not a positive whole number, `nofade` not a flag),
`FAILED lookup FPS unlock` / `lookup QualitySettings.vSyncCount` (the unlock may then be capped by vsync) /
`FAILED lookup no camera fade` / `lookup XNpcDither...` (only the camera fade is disabled), and
`FAILED apply FPS unlock` / `write XCameraDitherDetector.UseCameraDither=false` / `apply XNpcDither...` (call threw, or
the value read back differently). The overlay shows the same problems in red under the option. Lines are logged on the
first result and on every later change, not every 0.5 s.

## Graphics options (`ascnet-graphics.ini`)

`ascnet-graphics.ini` beside `PGR.exe` is read by `lucia` at game start, however the game is launched (launcher,
CrossOver shortcut, script). One `key = value` per line, `#`/`;` comments. An absent file leaves all overrides off;
the Settings bridge still starts so the **AscNet** tab can enable options and create the file.

### In-game Settings → AscNet

The **AscNet** tab adds the common controls to the game's own Settings screen. Changes apply live and each
accepted change is automatically written to `ascnet-graphics.ini`; no separate Save or restart is needed.
Hand-edited ini changes are still read at game start, not watched while the game runs.

Controls are **Render scale** (0.25–4), **FPS limit** (30–360), **No camera fade**, **Ultrawide fullscreen**,
**UI in centred 16:9**, **Wider UI-scene FOV** and **Fight camera FOV** (0.5–2), **UI scale** (0.5–1.5),
**DPS meter**, **DPS meter window**, **Meter corner** (top right, top left, bottom right, bottom left),
**Meter size** (0.75–2), **Show skills and details**, **Hide meter outside fights**, and **Overlay**.
Optional numeric overrides have an enable toggle and a slider; disable one to return to the game's own value.
Save / Return to Default are hidden on this tab only; the game's own tabs keep their stock behavior.

A persistence failure leaves the accepted settings live for this session but not saved for the next launch; the
tab reports the failure. Saving uses the same path as the overlay, so the log prefix remains
`[lucia] OK overlay save: <path>` or `[lucia] FAILED overlay save: <reason>; the settings stay live but are not saved`.

The Ctrl+Shift+G overlay remains the advanced diagnostics surface: raw render-pipeline quality fields, per-option
errors, the Lua console, detailed DPS controls/results, and graphics dump controls. The Settings tab does not replace those tools.

If the required Lua/UI objects are unavailable or installation fails, the stock Settings UI remains usable;
the AscNet controls are unavailable rather than replacing the game's controls with a broken panel.

The tab is added to the game's own tab list (`XUiButtonGroup.AddButton`, a copy of the "Others" button) and its
panel is built on first open from the game's PC graphics and sound prefabs. Healthy lines:

```
[lucia] OK lua AscNet Settings: waiting for the Settings screen
[lucia] OK lua AscNet Settings: hooked the Settings screen
[lucia] OK lua AscNet Settings: tab added (index 13)
[lucia] OK lua AscNet Settings: templates found
[lucia] OK lua AscNet Settings: panel built (15 options)
```

Any failure is one `[lucia] FAILED lua AscNet Settings: <reason>` line; the AscNet tab is then hidden and the
game's Settings screen is left as the game built it. If Settings is first opened through a path other than
`XLuaUiManager.Open("UiSet")`, the tab appears from the next time Settings opens.

### Ini keys and advanced diagnostics

| Key | Effect |
|---|---|
| `renderscale = 2.0` | Internal render scale in every scene (0.25–4.0). The game itself sets 1.333 in menus/Babylonia and 1.0 in fights through `RenderPIplineQualityManger.SetupExtraRenderScale`; `lucia` detours that call (and `EnableExtraRenderScale`, which Babylonia switches off) so the chosen scale holds everywhere with no reset window. |
| `fps = 240` | Frame-rate unlock: a plain positive whole number (anything else is a `FAILED ascnet-graphics.ini: line N` and ignored; absent = the game's own frame rate). `lucia` holds `UnityEngine.Application.targetFrameRate = n` and `QualitySettings.vSyncCount = 0` (Unity ignores `targetFrameRate` while vsync paces the frame, and the game's own VSync option sets it) every 0.5 s. Removing the key restores the game's `targetFrameRate` and `vSyncCount`, recorded before the first write and again whenever the game changes them. The overlay shows `target <n>, vsync <v>` as read from the game. |
| `nofade = on` | Disables the character/camera proximity fade natively (`on`/`off`, `1`/`0`, `true`/`false`; absent = off): `XCameraDitherDetector.UseCameraDither = false` and `XNpcDither.SetPlayerSelfDitherParameter` with opaque values, re-applied every 0.5 s. Dying-enemy dissolves (`XDitherHelper`) are untouched. Turning it off restores `UseCameraDither` and the game's own `XNpcDither.playerSelfDitherParameter`, both read before the first write. Supersedes `tools/apply_pgr_nofade.py`. |
| `ultrawide = on` | Fullscreen at the monitor's own resolution (21:9, 32:9, e.g. 5120x1440) instead of the forced window (`on`/`off`, `1`/`0`, `true`/`false`; absent = off). The game gates fullscreen in Lua: `XUiPcManager.FullScreenableCheck` forces a window when the device resolution exceeds the largest row (2560x2048) of its cached `XUiPcConfig.GetTabUiPcResolution()` table. `lucia` runs Lua on the game's main thread (a detour on `XLuaEngine.Update`, which `XApplication.Update` reaches every frame; chunks go through `LuaEnv.DoString` on `XLuaEngine.Env`) once `XDataCenter.UiPcManager` exists: it adds the monitor's own size and 2560x1080 / 3440x1440 / 5120x1440 (those that fit) to that table, clears `XSettingHelper.ForceWindow` and calls the game's own `XUiPcManager.SetResolution(w, h, FullScreenWindow)` (so its saved display preferences stay consistent). Turning it off removes only the rows `lucia` added and re-runs `FullScreenableCheck()`, i.e. the game's own windowed fallback. No native size or aspect clamp exists in `XSettingHelper.SetResolution` / `XResolutionManager`. The HUD and menus are not re-laid-out: they follow the game's own safe-area handling. The overlay's **Display** section has the checkbox and shows `screen: WxH fullscreen/windowed` as read on the main thread. |
| `ui16x9 = on` | On screens wider than 16:9 (e.g. with `ultrawide`), keeps menu content and the fight HUD in a centred 16:9 area while the 3D world and full-screen art keep the full width (`on`/`off`; absent = off). Every UI canvas scales 1920x1080 with `Expand`, so at 32:9 it grows to 3840x1080 and edge-anchored content spreads. `lucia` uses the game's own safe area (the notch path), through the Lua bridge: `XResolutionManager.SetSafeArea(centred 16:9 rect, px)` + `RefreshResolution()` (so `OriginSafeArea` and `XAppPlatBridge.GetNotchSize()` follow), `XCustomUi.Instance:RefreshResolution()` (fight HUD `SafeScreenArea`) and `XSetManager.SetScreenOff()` (re-insets every live `SafeAreaContentPane`, keeping your screen-edge slider). Checked every 0.5 s and re-applied after resolution changes; switching it off restores the game's own safe area. Panels outside a safe pane (backgrounds, some popups) stay full width. Logs `OK ui16x9: safe area x,y WxH on WxH (was ...)` whenever it changes. Moving the UI cameras' viewport was tried and does not work: the canvases keep sizing from the screen and clicks land wrongly. |
| `uifov = 1.3` | Widens the vertical field of view of the War Zone map (`UiArenaNew3d`) and team select (`UiNewRoomSingle`) cameras by this factor, 0.5–2.0, applied as `2·atan(tan(fov/2)·factor)` (absent = the game's own). The lobby and character screens use the same camera names (`UiNearCamera`, `UiFarCamera`, `UiCamera`) under other scene prefabs and are left alone; cameras are matched by the scene prefab above them. The game keeps these at a fixed vertical FOV on every aspect (22.62° on the War Zone map, 26.99° on team select), so a taller screen shows them larger. Cameras with a `CinemachineBrain` get the active virtual camera's `m_Lens.FieldOfView` changed, others the camera's own; the game's value is remembered and re-read whenever the game changes it, and restored when the option is cleared. Checked every 0.5 s through the Lua bridge (new screens snap within that). Logs `OK uifov: <camera> <old>-><new>` per change. Wider than the game's framing can show stage edges. |
| `fightfov = 1.3` | Scales the perspective fight cameras used by normal `XRLManager` fights and `StatusSyncFight`/Babylonia by a factor from 0.5–2.0, using `2·atan(tan(fov/2)·factor)` (absent = the game's own). A filtered native camera-FOV setter transforms the game's current values, including skill/zoom animations; independent UI, lobby and cutscene cameras are excluded. Camera identity refreshes every 0.5 s, so entering a fight may take one tick to apply. Clearing the option passes through the game's next FOV write/frame. These paths are implemented but have not yet been exercised in-game; other fight-camera paths are not guaranteed. |
| `uiscale = 0.85` | Scales UI content relative to the game's own size (0.5–1.5; absent = the game's own). The Lua bridge changes the root `UiRoot` `CanvasScaler` in `ScaleWithScreenSize` mode from reference resolution 1920×1080 to `1920/factor` × `1080/factor`, and restores it when disabled. With `ui16x9`, the physical safe rectangle remains centred 16:9; UI adapter offsets are compensated separately for the chosen scale. Fight-HUD-specific custom offsets may remain; HUD layout and the combination with `ui16x9` still need in-game checking. |
| `<Setting>.<Field> = value` | Overrides one field of the render pipeline's runtime quality objects (`UniversalRenderPipelineAsset.RenderPipelineDefaultQualityData`, read as managed memory, never through Unity calls), e.g. `ShadowSetting.CascadeSize = 4096`. Names are the game's own and case-sensitive (see the overlay or dump). Values: `on`/`off`, numbers, enum names or numbers (`AntiAliasingSetting.DeferredAAMethod = SubpixelMorphologicalAntiAliasing`), comma lists for arrays (`ShadowSetting.CascadeFrameUpdate = 0,0,1,2`). Re-applied every 0.5 s; the game resets them on scene and quality changes. Removing an override restores the game's value. |
| `overlay = on` | In-game Dear ImGui window (hudhook D3D11 `Present` hook), toggled with **Ctrl+Shift+G**; the game gets no keyboard/mouse input while it is open. Shows scene, game-requested and effective render scale; edits render scale and every quality field live (changes apply within 0.5 s), marks overridden fields with the game's value and a Reset, shows each key's error in red, and **Save to ascnet-graphics.ini** writes the current options. Not all fields have a visible effect: `ForwardAAMethod` is ignored by deferred-rendered cameras (`DeferredAAMethod` applies), `ActiveRealTimeAreaLight` only matters in scenes with area lights, and no renderer reader was found for `AdditionalCharacterLighting`. |
| `dpsmeter = on` | Enables the DPS meter (`on`/`off`, `1`/`0`, `true`/`false`; absent = off). Normal fights only; DLC/Babylonia (`StatusSyncFight`) is unsupported. Per-character **recorded damage** comes from the game's live settlement accumulator, including owned summons attributed to their root master; it is not guaranteed actual enemy HP removed. The overlay shows damage-sorted, character-colored share bars, human-readable k/M totals, fight-time DPS and team peak-five-second DPS, with **In fight** / **Last fight** duration and spacing between characters in both meter views. Persisted controls select corner, compact visibility, hiding outside fights, compact scale (0.75–2), and optional details per character: crit rate (crit hits / hits, a double crit counts as a crit), biggest hit, peak five-second DPS, on-field DPS ("not controlled" for characters that were only swapped in for QTE/assists), healing / damage taken when non-zero, and the top five named skills by sampled hit damage. Skill names are resolved from the game's loaded character-skill tables; ambiguous matches show names joined with " / ", and unresolved damage IDs appear only in a capped **Other** label. Skill percentages use that character's **hit-sampled damage**, not settlement totals, so they need not match the character bar. The settlement record's per-element split (`DamageNormal` / `DamageMagic`) is not shown: its element sum exceeds the recorded total, so its meaning is not established. On-field time is sampled controlled-character time, not exact switch timing; summons can deal damage while their owner is off field, so on-field DPS is recorded character damage divided by sampled on-field time, not damage restricted to that time. Native hit details supplement, never replace, authoritative settlement totals; dropped hit samples are shown as incomplete hit details. The compact meter never captures gameplay input. |
| `dpscorner = topright` | Compact-meter corner: `topright` (default), `topleft`, `bottomright`, or `bottomleft`. |
| `dpsdetails = off` | Shows per-character damage details in both meter views when enabled (`on`/`off`, `1`/`0`, `true`/`false`; default = off). |
| `dpsscale = 1` | Compact-meter scale (0.75–2.0; default = 1). |
| `dpshideoutside = off` | Hides the compact meter outside active fights when enabled (`on`/`off`, `1`/`0`, `true`/`false`; default = off). |
| `dpscompact = on` | Shows the compact meter when the DPS meter is enabled (`on`/`off`, `1`/`0`, `true`/`false`; default = on). |
| `dump = 1` | Read-only: every render-pipeline quality object (`RenderPIplineQualityManger` statics, `GetRuntimeData()` and its `Quality_*` settings, `Is*()` flags), `XQualityManager.Instance`, `XResolutionManager` statics and the managed `UnityEngine.QualitySettings` getters, every 2 s; all values once, then only changes, tagged with the active Unity scene (`XGame` = menus, `FightScene`, `NormalScene` = Babylonia). |

The DPS denominator uses the game's `XFight.NonPausedTime`, excluding menu pauses. Peak five-second DPS is estimated
from cumulative damage samples every 0.5 s, not exact hit timings. The graphics overlay provides DPS controls and a
reset. Compact-meter position, visibility, scale, hide-outside-fights and detail preferences are shared with the
in-game Settings tab and persisted by saving; only non-default preferences are written to the ini. **Reset all**
restores their defaults. **Reset DPS** clears the recorded fight data without changing display preferences.

The overlay also has a Lua console for executing snippets on the game's main thread and viewing their results or errors.
The console is overlay-only: there is no ini key, and commands are not saved with graphics options.

Healthy lines:

```
[lucia] OK graphics options requested: renderscale = 2, 5 setting override(s) in ascnet-graphics.ini; applying once IL2CPP is ready
[lucia] OK render scale detours: RenderPIplineQualityManger.SetupExtraRenderScale at 0x..., EnableExtraRenderScale at 0x...
[lucia] OK render scale: RenderPIplineQualityManger.GetRenderScale() = 2
[lucia] OK graphics override ShadowSetting.CascadeSize: 2048 -> 4096 (scene="XGame"; re-applied whenever the game resets it)
[lucia] OK render scale: the game asked for 1 (scene="FightScene"); using 2
[lucia] OK render scale: the game switched the extra render scale off (scene="NormalScene"); kept on
[lucia] OK overlay: D3D11 Present hook installed; Ctrl+Shift+G toggles
[lucia] OK graphics options: the game is quitting; detached from IL2CPP and stopped touching the game
```

Every problem is a `FAILED` line and only that line is skipped; the game keeps its own value:
`FAILED ascnet-graphics.ini: line N: ...` (syntax, unknown key, `renderscale` / `uifov` / `fightfov` / `uiscale` out of range),
`FAILED graphics override X.Y: ... has no field ...; it has: ...` (lists the real names), `... is not a valid ...`,
`... is a divisor (1 = full resolution, 2 = half); 0 would crash the renderer` (any `*Downsample*` field below 1: the
SSAO pass divides the screen width by `SSAOSetting.Downsample`), `FAILED render scale ...` (detour or method missing),
`FAILED overlay ...` (the game runs without the overlay), `FAILED ... exit detection unavailable ...`. On quit `lucia`
catches `UnityEngine.Application.Internal_ApplicationQuit`, stops touching the game and detaches its thread from
IL2CPP; `il2cpp_shutdown` waits for attached threads, so without the detach the game would hang on exit.
Overrides change what the renderer is asked to do; a value the game never ships (e.g. huge shadow maps) can still cost
GPU time or render oddly, so change one key at a time. In the dump, `<threw>` means a getter is not ready yet (early
passes) and `<Name>` is an object that is named, not walked. Unity's `lodBias`, `maximumLODLevel`, texture limit,
anisotropic filtering and `maxQueuedFrames` accessors are stripped from the 4.8 build and cannot be read or set.

## Optional trace (developers)

`ASCNET_PATCH_TRACE=1` (the Launcher clears it, so set it only when running `PGR.exe` by hand) adds
`[lucia] OK lookup XRemoteConfig: assembly=... class=XRemoteConfig` / `[lucia] FAILED lookup XRemoteConfig ...`
(`XRemoteConfig` is a metadata dump only; no hook is installed on it and routing does not depend on it),
`remote-config ...` field/method dumps and the first 64 `InternalSetUrl call url=...` lines.

## Telemetry block

Always on. `lucia` hooks the DNS entry points of `ws2_32.dll` (`getaddrinfo`, `GetAddrInfoW`, `GetAddrInfoExW`,
`gethostbyname`) as soon as it loads and makes lookups of these hosts (and their subdomains) fail as if offline:
`wetest.net` (CrashSight crash/exception uploads, `pc.crashsight.wetest.net`) and the public-IP services the
client queries at start-up (`XNoticeManager.RequestIp`: `icanhazip.com`, `ipify.org`, `ifconfig.me`, `ifconfig.co`,
`inet-ip.info`; the client has no consumer of that IP, so it simply stays empty). A blocked lookup never reaches the
resolver: the call returns `WSAHOST_NOT_FOUND` (11001) with an empty result, exactly like an unknown host, so it does
not depend on how the player's network answers unknown names. Each blocked host is logged once with the module that
called it:

```
[lucia] OK telemetry blocked: ipv4.icanhazip.com (getaddrinfo) from UnityPlayer.dll; the lookup fails as if offline
```

Client feedback/log uploads are already routed to the local server, and the scroll notices
(`ScrollPicNotice.json`, `ScrollTextNotice.json`) are now routed to AscNet like the other notice files instead of
Kuro's CDN. ThinkingData, kurodata and PerfSight made no network calls in the 4.8 audit. Notice pictures under
`client/notice/pic/` still come from Kuro's CDN when a notice references them.

## Network audit (developers)

Set `ASCNET_PATCH_NETAUDIT=1` in the environment before launch to opt in to network auditing,
including early startup and native traffic. Apart from the telemetry block above it is observe-only: it does not
block connections or change routing. It logs each unique `api host[:port] from module` tuple (`module` is the
first non-system module on the call stack), up to 512 unique tuples per process.
Logging runs on a dedicated worker; if its bounded 512-event queue fills, audit events may be
dropped rather than blocking network calls, and a `FAILED` warning reports the loss.

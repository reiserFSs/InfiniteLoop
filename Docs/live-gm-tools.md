# Live GM Tools

## Use in the launcher

Start PGR with **PLAY**. While the game is open, bring the launcher to the foreground and open **GM TOOLS**. Enter the player's UID and choose **READ CURRENT LEVEL**. Enter an authored Commandant level and choose **SET LEVEL**. The panel reports the level confirmed by the server and whether the account is online. An increase reaches an online client immediately. A decrease sends the player back to sign-in because PGR's level notification accepts increases only.

The same panel can preview and complete a single guide, a guide group, or all eligible common guides for a signed-in account. Select **Preserve rewards**, **Grant rewards**, or **Skip without reward**. The panel uses the same eligibility, completion, and reward recovery as the chat commands. See [Guide completion and reward policies](guide-progression.md).

An offline level operation updates an existing character. Keep the launcher-managed server running to reach offline characters, for example by keeping PGR open at the login screen or signed in to a different account. The launcher stops the local services when PGR exits.

## Install the paired Windows build

1. Finish the current game session, then close PGR and the old launcher.
2. Extract the complete GM launcher bundle into a new writable folder. Keep `AscNetLauncher.exe`, its media and manifests, and the `server` folder together.
3. Open the new `AscNetLauncher.exe`. It reads the existing selected game, installed patch, ports and tools from the normal launcher directories. If Setup has never been completed, use the standard launcher to prepare the game first.
4. Choose **PLAY**, then open **GM TOOLS**. In the new folder's `launcher.log`, the `Started AscNet host` line identifies the exact .NET executable, `server/AscNet.dll` path and GM server version.

The paired build uses the normal `%LOCALAPPDATA%\AscNetLauncher\local\data\mongo` database and persistent `local\config.json`. The old launcher and its cached source build can be kept side by side. The new launcher requires one start of the paired server before GM Tools is available. Later level increases and guide operations run during the game session.

The bundle's `launcher.json` sets `bundledLocalServer` to `true`, so PLAY starts the adjacent server and checks its GM protocol before launching PGR. Automatic source and launcher checks are disabled for that paired folder. A regular build with the default config continues to start the server from `build-state.json` and enables GM Tools when that server advertises the protocol.

## Why a database-only edit reverted

Login loads a `Player` object into the TCP session. That object remains authoritative while the player is online. A database-only write can make a fresh read report the edited level while the active session still has its old value. Logout calls `Session.Save`, whose full player replacement can restore the stale level to MongoDB. The old `/level` command also relied on the session lifetime for persistence and ran the normal experience rollover path.

The GM service takes the same locks as packet dispatch, login and disconnect. It edits the active object, performs a checked player save, then sends `NotifyPlayerLevel`. It uses an existing saved record for offline players. An uncertain write is read back before confirmation. If neither outcome can be established, the connection closes without overwriting the durable record. Login loads the saved level, and transport reconnect replays the current level for GM-edited characters. The operation leaves experience, inventory, story and guide records to their own paths.

## Channel and verification

The launcher sends correlated JSON frames on its private server child's redirected stdin and receives replies on stdout. The server enables GM frames only for a managed child with redirected streams and loopback launch settings. The existing shutdown message still uses that pipe. The launcher drains stdout and stderr and waits for responses off the UI thread.

Build with the .NET 8 SDK and a Windows MSVC Rust toolchain.

```text
dotnet publish AscNet/AscNet.csproj -c Release -o <bundle>/server
cargo build --manifest-path AscNet.Launcher/Cargo.toml --locked --release
```

Use the Windows launcher build as `AscNetLauncher.exe`. Add the regular launcher media and supported-client manifest. Set `bundledLocalServer` to `true` in the bundle's copy of `launcher.json`.

The `--live-gm-only` compatibility test requires a disposable loopback MongoDB on a non-default port and an `ascnet_manual_full_` database in the test output config. It starts real .NET hosts and tests the pipe, game packets, two independent accounts, retries and restart recovery. The native PGR display and launcher button require separate Windows UI verification.

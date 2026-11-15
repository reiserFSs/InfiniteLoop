//! The launcher's state machine, free of any windowing toolkit.
//!
//! The UI thread owns one [`Controller`]. It forwards user intent to the command methods, calls
//! [`Controller::pump`] and [`Controller::tick`] from timers, and renders [`Controller::view`].
//! Worker threads (setup, play, downloads, ...) report back over a channel that `pump` drains, so
//! every state change happens on the UI thread and no locking is needed.
//!
//! Nothing here produces player-facing prose: the view carries enums and raw values ([`Text`],
//! [`Dialog`]) and the UI layer translates them.

use anyhow::{Context, Result};
use ascnet_launcher::{
    download::{self, Region},
    fps, graphics_ini,
    install::{self, PatchState},
    local::{self, LocalBuild, LocalRuntime},
    media::Music,
    mods,
    package::{self, PatchPackage},
    updater,
};
use serde::{Deserialize, Serialize};
use std::{
    collections::{BTreeMap, VecDeque},
    env, fs,
    path::{Path, PathBuf},
    sync::mpsc::{self, Receiver, Sender},
    thread,
    time::Duration,
};

mod files;
#[cfg(target_os = "macos")]
mod mac;
#[cfg(target_os = "macos")]
use mac::{Mac, StepProgress};
pub use files::{Kind, Slot, SlotLabel, Transfer, REGIONS};
use files::Files;

pub const LAUNCHER_VERSION: &str = env!("CARGO_PKG_VERSION");
const ACTIVITY_LINES: usize = 3;
const LOG_LIMIT: usize = 200;

#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct DistributorConfig {
    repository_url: String,
    branch: String,
}

#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Settings {
    selected_game: Option<PathBuf>,
    /// Legacy (pre-ini) frame-rate field: migration input only, never written once consumed.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    fps_value: Option<i32>,
    #[serde(default)]
    music_muted: bool,
    /// Pre-ini launcher versions kept these here; `migrate_legacy_graphics` moves them into the
    /// client's `ascnet-graphics.ini`. Written back only while still pending (no client folder yet).
    #[serde(default, skip_serializing_if = "is_false")]
    fps_enabled: bool,
    #[serde(default, skip_serializing_if = "is_false")]
    no_camera_fade: bool,
    /// Client language (edition) chosen in Game files; defaults from the OS UI language.
    #[serde(default)]
    region: Option<Region>,
    #[serde(default)]
    client_mods_enabled: bool,
    #[serde(default)]
    enabled_mods: BTreeMap<String, bool>,
    /// macOS: an existing CrossOver/Wine prefix the player adopted; Setup and Play use it instead of the AscNet bottle.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    adopted_prefix: Option<PathBuf>,
}
impl Default for Settings {
    fn default() -> Self {
        Self {
            selected_game: None,
            fps_value: None,
            music_muted: false,
            fps_enabled: false,
            no_camera_fade: false,
            region: None,
            client_mods_enabled: false,
            enabled_mods: BTreeMap::new(),
            adopted_prefix: None,
        }
    }
}
fn is_false(value: &bool) -> bool {
    !*value
}

/// One-time: moves legacy `fps_enabled` / `fps_value` / `no_camera_fade` into the selected client's ini (absent keys only).
fn migrate_legacy_graphics(settings: &mut Settings) {
    let Some(game) = settings.selected_game.as_deref() else { return };
    let fps_value = settings.fps_value.take();
    if !settings.fps_enabled && !settings.no_camera_fade {
        return;
    }
    let fps = settings.fps_enabled.then_some(fps_value.unwrap_or(240));
    let line = match graphics_ini::migrate(game, fps, settings.no_camera_fade) {
        Ok(written) => {
            settings.fps_enabled = false;
            settings.no_camera_fade = false;
            format!("Moved launcher settings into {}: {}", graphics_ini::FILE, if written.is_empty() { "nothing (keys already present)".to_owned() } else { written.join(", ") })
        }
        Err(e) => {
            settings.fps_value = fps_value;
            local::summarized_error("move FPS/no-camera-fade settings into the graphics ini", &e)
        }
    };
    let _ = local::launcher_log(&line);
}

#[allow(dead_code)]
#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct ServerStatus {
    schema_version: u32,
    server_version: String,
    online: bool,
    maintenance: bool,
    message: String,
    minimum_patch_version: Option<String>,
    minimum_launcher_version: Option<String>,
    supported_clients: Vec<SupportedClient>,
}
#[allow(dead_code)]
#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct SupportedClient {
    application_version: String,
    document_version: String,
    launch_module_version: String,
}

/// Why Play cannot start (or must stop); each maps to its own "what to do" step in the UI.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PlayBlock {
    NoGame,
    NeedsBuild,
    NeedsPatch,
    Stopping,
    AlreadyRunning,
    Maintenance,
    Offline,
    LauncherOutdated,
    PatchOutdated,
    UnsupportedSchema,
    UnsupportedClient,
}
impl std::fmt::Display for PlayBlock {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(match self {
            PlayBlock::NoGame => "Select a game folder",
            PlayBlock::NeedsBuild => "Run Setup / Update to build the local server",
            PlayBlock::NeedsPatch => "Run Setup / Update to install the current local patch",
            PlayBlock::Stopping => "The local server is stopping",
            PlayBlock::AlreadyRunning => "The local server is already running",
            PlayBlock::Maintenance => "Server is under maintenance",
            PlayBlock::Offline => "Server is offline",
            PlayBlock::LauncherOutdated => "Launcher update required",
            PlayBlock::PatchOutdated => "Patch update required",
            PlayBlock::UnsupportedSchema => "Unsupported server status schema",
            PlayBlock::UnsupportedClient => "This game application version is not supported by the local server",
        })
    }
}
impl std::error::Error for PlayBlock {}

/// What the launcher is busy with (drives the progress label).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Busy {
    Launcher,
    Inspect,
    Setup,
    Restore,
    Play,
    Mods,
    FilesInspect,
    Files(Kind),
    #[cfg(target_os = "macos")]
    CrossOver,
    #[cfg(target_os = "macos")]
    Bottle,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Screen {
    Home,
    Settings,
    Files,
}

/// Activity and status sentences. The UI layer owns the wording and the translations.
#[derive(Clone, Debug, PartialEq)]
#[cfg_attr(not(target_os = "macos"), allow(dead_code))]
pub enum Text {
    // Activity
    LauncherReady,
    GameFoundSteam,
    /// macOS: an existing prefix was adopted (its display name).
    GameAdopted(String),
    MusicUnavailable,
    VideoUnavailable,
    MusicChangeFailed,
    CheckingLauncher,
    DownloadingLauncherUpdate,
    LauncherUpdateRestarting,
    LauncherUpdateDeferred,
    LauncherCurrent(&'static str),
    LauncherCheckFailed,
    LauncherUpdateFailed,
    SetupStarting,
    MongoStarting,
    ServerStarting,
    ServerReady,
    ServerUpdateAvailable,
    ServerCurrent,
    ServerCheckFailed,
    ServerUpdateDeferred,
    ServerUpdateFailed,
    SetupDone,
    SetupFailed,
    Restored,
    RestoreFailed,
    PlayFailed,
    CheckFailed,
    ModsUpdated,
    ModsFailed,
    ElevationCancelled,
    PatchUnavailable,
    FpsMigrated,
    FpsMigrationFailed,
    ClosingAfterStop,
    ServicesStopping,
    ServicesStopped,
    ServicesStopFailed,
    FilesStarted(Kind, Region),
    FilesPhase(download::Phase),
    FilesDone { repaired: usize, downloaded: String, removed: usize, patch_files: usize },
    FilesNote(String),
    FilesPaused,
    FilesCancelled,
    FilesFailed,
    NextSetup,
    // Home sentence under the status rows
    HintSelectGame,
    HintSetup,
    HintUpdate,
    HintPlay,
    HintRunning,
    HintStopping,
    HintPatchUnsupported,
    HintPatchRepair,
    HintWorkingInspect,
    HintWorkingSetup,
    HintWorkingRestore,
    HintWorkingPlay,
    FilesRunning(Kind),
    // Mods status (Settings)
    ModsNone,
    ModsUnavailable(String),
    ModsHint,
    ModsApplying,
    ModsApplied { applied: String, skipped: String },
    ModsDisabled,
    ModsChangeFailed(String),
    // Game files info
    FilesChoose,
    FilesNotEmpty,
    FilesUnreadable(String),
    FilesEmptyFolder(Region),
    FilesResumable,
    FilesInspecting,
    FilesGame { region: Option<Region>, version: Option<String>, steam: bool, patched: bool, assumed: Region },
    FilesPlan { download: String, files: usize, needs: String, free: String, removed: usize },
    FilesPlanError(String),
    FilesInspectFailed(String),
    FilesStopping,
    // macOS: CrossOver and the AscNet bottle
    CrossOverPhase(usize),
    CrossOverInstalled,
    CrossOverFailed,
    BottleCreating,
    BottleReady,
    BottleFailed,
    WorkingBottle,
    HintInstallCrossOver,
    HintCreateBottle,
    HintRepairBottle,
    HintUseFound,
}

/// Something that needs the player's attention in a dialog; the raw error stays behind Details.
#[derive(Clone, Debug, PartialEq)]
#[cfg_attr(not(target_os = "macos"), allow(dead_code))]
pub enum Problem {
    Busy,
    GameRunning,
    GameStillRunning,
    GameCheck,
    NoFolder,
    InvalidFolder,
    Save,
    Stop,
    Check,
    Setup,
    Restore,
    Play,
    Blocked(PlayBlock),
    Files(Kind),
    /// The partial download couldn't be deleted; it stays resumable.
    CancelFailed,
    CrossOver,
    Bottle,
}

#[derive(Clone, Debug, PartialEq)]
pub enum Dialog {
    ConfirmSetup,
    ConfirmVerify(Kind),
    /// `saw_game`: PGR was running when the prompt opened (see `game_exit_stops_services`).
    ConfirmClose { saw_game: bool },
    /// Cancelling a download deletes the partial files; carries the size shown in the prompt.
    ConfirmCancel(String),
    /// macOS: CrossOver isn't installed; explains the trial and the licence before downloading it.
    ConfirmCrossOver,
    /// macOS: use an existing CrossOver/Wine prefix and its game folder.
    #[cfg(target_os = "macos")]
    ConfirmAdopt(ascnet_launcher::mac::discover::Candidate),
    Problem(Problem, String),
}

struct Work {
    generation: u64,
    result: Result<WorkResult>,
}
enum Event {
    Work(Work),
    Progress(String),
    Download(download::Progress),
    ServicesStopped(Result<()>),
    #[cfg(target_os = "macos")]
    Mac(StepProgress),
}
enum WorkResult {
    /// The update check's outcome, and (best effort) whether this executable is the official release build.
    LauncherChecked(Option<bool>, Result<Option<updater::StagedUpdate>>),
    Refresh {
        build: Option<LocalBuild>,
        package: Option<PatchPackage>,
        patch: Option<PatchState>,
        fps_migration: Result<Option<i32>>,
        update: Result<Option<bool>>,
        automatic: bool,
    },
    Prepared {
        build: LocalBuild,
        package: PatchPackage,
        patch: PatchState,
    },
    Restored,
    Mods { settings: Settings, status: Result<ModsApplied> },
    Client(files::Done),
    /// macOS: CrossOver was installed or the bottle prepared.
    #[cfg(target_os = "macos")]
    Mac,
    Launched {
        build: LocalBuild,
        package: PatchPackage,
        runtime: LocalRuntime,
        server: ServerStatus,
    },
}
enum ModsApplied {
    Applied { applied: Vec<String>, skipped: Vec<String> },
    Disabled,
}

// ----- the view -----------------------------------------------------------------------------

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PatchLabel {
    NotInstalled,
    Current,
    UpdateAvailable,
    Unsupported,
    RepairNeeded,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ServerLabel {
    Stopped,
    Stopping,
    Maintenance,
    Running,
    Offline,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[cfg_attr(not(target_os = "macos"), allow(dead_code))]
pub enum Primary {
    SelectGame,
    Setup,
    Update,
    Play,
    Working,
    Running,
    Stopping,
    InstallCrossOver,
    /// macOS: adopt the first install found in an existing prefix.
    UseFound,
    CreateBottle,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[cfg_attr(not(target_os = "macos"), allow(dead_code))]
pub enum CrossOverLabel {
    Missing,
    Working,
    Ready,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[cfg_attr(not(target_os = "macos"), allow(dead_code))]
pub enum BottleLabel {
    Missing,
    Working,
    Ready,
    Repair,
    /// An existing prefix chosen by the player.
    Adopted,
}
/// An install found in an existing prefix, offered on Home and in Game files.
pub struct FoundView {
    /// "Steam-AscNet (Sikarugir)"
    pub name: String,
    pub info: Text,
}
/// macOS only: the CrossOver and bottle rows on Home.
pub struct MacHome {
    pub crossover: CrossOverLabel,
    pub bottle: BottleLabel,
}

pub struct Home {
    /// Selected edition and the client version shown in the Game row.
    pub game: Option<(Region, String)>,
    /// Short version for the brand chip ("4.9").
    pub chip: String,
    pub patch: PatchLabel,
    pub server: ServerLabel,
    pub primary: Primary,
    pub primary_enabled: bool,
    pub hint: Text,
    /// Label while busy (the progress line replaces the hint).
    pub busy: Option<Text>,
    /// `Some(fraction)` for a determinate line, `None` = marquee. Only meaningful while busy.
    pub progress: Option<f32>,
    /// `Some` on macOS, where Home also lists CrossOver and the bottle.
    pub mac: Option<MacHome>,
    /// macOS: installs in existing prefixes the player can use (empty elsewhere).
    pub found: Vec<FoundView>,
}
pub struct ActivityLine {
    pub time: String,
    pub text: Text,
}
pub struct ModRow {
    pub name: String,
    pub description: String,
    pub on: bool,
}
pub struct SettingsView {
    pub folder: String,
    pub music_on: bool,
    pub can_check: bool,
    pub can_setup: bool,
    pub can_restore: bool,
    pub mods_on: bool,
    pub mods_editable: bool,
    pub mods: Vec<ModRow>,
    pub mods_status: Text,
}
pub struct FilesView {
    pub tiles_enabled: bool,
    pub folder: String,
    pub can_choose: bool,
    pub info: Text,
    pub plan: Vec<Text>,
    pub slots: Vec<Slot>,
    /// A job is running or paused: show the progress line.
    pub transfer: Option<Transfer>,
    pub working_label: Option<Text>,
    pub fraction: Option<f32>,
    pub paused: bool,
}
pub struct View {
    pub screen: Screen,
    pub language: Region,
    pub dialog: Option<Dialog>,
    pub home: Home,
    pub activity: Vec<ActivityLine>,
    pub settings: SettingsView,
    pub files: FilesView,
    pub official_build: Option<bool>,
}

// ----- the controller -----------------------------------------------------------------------

pub struct Controller {
    config: DistributorConfig,
    settings: Settings,
    build: Option<LocalBuild>,
    package: Option<PatchPackage>,
    runtime: Option<LocalRuntime>,
    patch: Option<PatchState>,
    server: Option<ServerStatus>,
    update_available: Option<bool>,
    /// `Some(true)` official build, `Some(false)` unofficial, `None` unknown (offline, no manifest): nothing is shown.
    official_build: Option<bool>,
    update_error: Option<String>,
    busy: Option<Busy>,
    /// Local server and MongoDB are shutting down after the game exited.
    stopping: bool,
    generation: u64,
    tx: Sender<Event>,
    rx: Receiver<Event>,
    music: Option<Music>,
    music_path: PathBuf,
    /// PGR.exe was running at the last game-watch tick; background music stays stopped meanwhile.
    game_running: bool,
    /// The user asked to close while shutdown was already running.
    close_when_stopped: bool,
    log: VecDeque<(String, Text)>,
    files: Files,
    screen: Screen,
    dialog: Option<Dialog>,
    mods: Vec<mods::ModInfo>,
    mods_status: Option<Text>,
    quit: bool,
    #[cfg(target_os = "macos")]
    mac: Mac,
}

impl Controller {
    /// Loads settings, discovers the game and starts the background music. Does no network work.
    pub fn new(exe_dir: &Path) -> Result<Self> {
        let config: DistributorConfig = serde_json::from_slice(
            &fs::read(exe_dir.join("launcher.json")).context("launcher.json is missing")?,
        )
        .context("launcher.json is invalid")?;
        let mut settings = load_settings().unwrap_or_default();
        if settings.selected_game.as_deref().is_some_and(|p| !crate::steam::valid_game_directory(p)) {
            settings.selected_game = None;
        }
        #[cfg_attr(target_os = "macos", allow(unused_mut))]
        let mut found_steam = false;
        // macOS never adopts a Steam copy by itself: the game lives in the AscNet bottle unless the player picks a folder.
        #[cfg(not(target_os = "macos"))]
        {
            if settings.selected_game.is_none() {
                settings.selected_game = crate::steam::discover_game()?;
                found_steam = settings.selected_game.is_some();
            }
        }
        #[cfg(target_os = "macos")]
        {
            let has_game = settings.selected_game.is_some();
            settings.adopted_prefix = settings.adopted_prefix.take().filter(|p| has_game && p.join("system.reg").is_file());
            ascnet_launcher::mac::bottle::Bottle::set_adopted(settings.adopted_prefix.clone());
        }
        migrate_legacy_graphics(&mut settings);
        save_settings(&settings)?;

        let music_path = exe_dir.join("background.wav");
        let (tx, rx) = mpsc::channel();
        let region = settings.region.unwrap_or_else(default_region);
        let mut controller = Self {
            config,
            settings,
            build: None,
            package: None,
            runtime: None,
            patch: None,
            server: None,
            update_available: None,
            official_build: None,
            update_error: None,
            busy: None,
            stopping: false,
            generation: 0,
            tx,
            rx,
            music: None,
            music_path,
            game_running: false,
            close_when_stopped: false,
            log: VecDeque::new(),
            files: Files::new(region),
            screen: Screen::Home,
            dialog: None,
            mods: Vec::new(),
            mods_status: None,
            quit: false,
            #[cfg(target_os = "macos")]
            mac: Mac::inspect(),
        };
        controller.log(Text::LauncherReady);
        if found_steam {
            controller.log(Text::GameFoundSteam);
        }
        if !controller.settings.music_muted {
            if let Err(error) = controller.start_music() {
                eprintln!("could not start background music: {error:#}");
                let _ = local::launcher_log(&format!("Background music unavailable: {error:#}"));
                controller.log(Text::MusicUnavailable);
            }
        }
        Ok(controller)
    }

    /// First work after the window is up: the launcher self-update check, then the local inspection.
    pub fn start(&mut self) {
        #[cfg(target_os = "macos")]
        self.mac_discover();
        self.start_launcher_check();
    }

    /// The background video could not start (detail is in launcher.log).
    pub fn video_unavailable(&mut self) {
        self.log(Text::VideoUnavailable);
    }

    /// The window should be destroyed (close confirmed, or a launcher update is restarting).
    pub fn should_quit(&self) -> bool {
        self.quit
    }

    // ----- timers -----

    /// Drains worker events; true when any arrived (the view changed). Call a few times a second.
    pub fn pump(&mut self) -> bool {
        let mut changed = false;
        while let Ok(event) = self.rx.try_recv() {
            changed = true;
            match event {
                Event::Work(work) => self.finish_work(work),
                Event::Download(progress) => self.files_progress(progress),
                Event::ServicesStopped(result) => self.finish_services_stopped(result),
                #[cfg(target_os = "macos")]
                Event::Mac(step) => self.mac_progress(step),
                Event::Progress(line) => {
                    let text = match line.as_str() {
                        "Starting local source setup" => Some(Text::SetupStarting),
                        "Starting MongoDB" => Some(Text::MongoStarting),
                        "Starting AscNet server" => Some(Text::ServerStarting),
                        "Local backend is ready" => Some(Text::ServerReady),
                        "Checking launcher releases…" => Some(Text::CheckingLauncher),
                        "Downloading and verifying launcher update…" => Some(Text::DownloadingLauncherUpdate),
                        _ => None,
                    };
                    if let Some(text) = text {
                        self.log(text);
                    }
                }
            }
        }
        changed
    }

    /// Polls for PGR.exe so the music pauses while the game runs and the services stop after it exits. ~Every 2 s.
    pub fn tick(&mut self) {
        let Ok(running) = install::game_running() else {
            return;
        };
        if running == self.game_running {
            return;
        }
        let was_running = self.game_running;
        self.game_running = running;
        if running {
            if let Some(music) = &self.music {
                music.set_paused(true);
                let _ = local::launcher_log("Game started; background music paused");
            }
            return;
        }
        if local::game_exit_stops_services(was_running, running) {
            self.stop_services_after_game();
        }
        if let Some(music) = &self.music {
            music.set_paused(false);
            let _ = local::launcher_log("Game closed; background music resumed");
        } else if !self.settings.music_muted {
            match self.start_music() {
                Ok(()) => {
                    let _ = local::launcher_log("Game closed; background music started");
                }
                Err(e) => {
                    let _ = local::launcher_log(&format!("Background music unavailable after the game closed: {e:#}"));
                }
            }
        }
    }

    // ----- home -----

    /// The big button on Home.
    pub fn primary(&mut self) {
        if self.dialog.is_some() || self.busy.is_some() || self.runtime.is_some() || self.stopping {
            return;
        }
        #[cfg(target_os = "macos")]
        {
            if self.mac_gate() {
                return;
            }
        }
        if self.settings.selected_game.is_none() {
            self.open_files();
        } else if self.update_available == Some(true)
            || self.build.is_none()
            || self.package.is_none()
            || !matches!(self.patch, Some(PatchState::Current))
        {
            self.request_setup();
        } else if self.commit_game() {
            self.start_play();
        }
    }

    pub fn open_settings(&mut self) {
        if self.dialog.is_some() {
            return;
        }
        self.screen = Screen::Settings;
        self.refresh_mods();
    }

    pub fn open_files(&mut self) {
        if self.dialog.is_some() || (self.busy.is_some() && !self.files.working()) {
            return;
        }
        self.screen = Screen::Files;
        #[cfg(target_os = "macos")]
        self.mac_offer_game_folder();
        self.files_opened();
    }

    /// Esc / the plate's close button.
    pub fn back(&mut self) {
        if self.dialog.is_some() {
            return;
        }
        self.screen = Screen::Home;
    }

    // ----- close flow -----

    /// The window's close button or a system close request.
    pub fn request_close(&mut self) {
        // A repeated request while the stop-services prompt is up must not open a second prompt.
        if matches!(self.dialog, Some(Dialog::ConfirmClose { .. })) {
            return;
        }
        match local::close_request(self.busy.is_some(), self.stopping, self.runtime.is_some()) {
            local::CloseRequest::Wait => self.dialog = Some(Dialog::Problem(Problem::Busy, String::new())),
            local::CloseRequest::AfterStop => {
                self.close_when_stopped = true;
                self.log(Text::ClosingAfterStop);
            }
            local::CloseRequest::StopServices => match install::game_running() {
                Ok(true) => self.dialog = Some(Dialog::Problem(Problem::GameStillRunning, String::new())),
                Err(e) => self.problem(Problem::GameCheck, &e),
                Ok(false) => self.dialog = Some(Dialog::ConfirmClose { saw_game: self.game_running }),
            },
            local::CloseRequest::Close => self.quit = true,
        }
    }

    /// Answers the open dialog: `true` = the confirm / OK button, `false` = cancel.
    pub fn answer(&mut self, confirm: bool) {
        let Some(dialog) = self.dialog.take() else { return };
        match (dialog, confirm) {
            (Dialog::ConfirmSetup, true) => self.start_prepare(),
            #[cfg(target_os = "macos")]
            (Dialog::ConfirmCrossOver, true) => self.mac_install_crossover(),
            (Dialog::ConfirmVerify(kind), true) => self.files_start_job(kind),
            #[cfg(target_os = "macos")]
            (Dialog::ConfirmAdopt(candidate), true) => self.mac_adopt(candidate),
            (Dialog::ConfirmCancel(_), true) => self.files_confirm_cancel(),
            (Dialog::ConfirmClose { .. }, true) => self.close_stopping_services(),
            (Dialog::ConfirmClose { saw_game }, false) => {
                // The game-watch tick skipped the stop while the prompt was up. If the game
                // exited meanwhile, stop now and stay open.
                if local::game_exit_stops_services(saw_game, self.game_running) {
                    self.stop_services_after_game();
                }
            }
            _ => {}
        }
    }

    fn close_stopping_services(&mut self) {
        if self.stopping {
            self.close_when_stopped = true;
            self.log(Text::ClosingAfterStop);
            return;
        }
        let mut owned_runtime = self.runtime.take();
        let stop_error = owned_runtime.as_mut().and_then(|runtime| runtime.stop().err());
        if let Some(e) = stop_error {
            self.runtime = owned_runtime;
            self.problem(Problem::Stop, &e);
            return;
        }
        self.quit = true;
    }

    fn stop_services_after_game(&mut self) {
        // The confirmation is open: it owns the decision, and taking the runtime now would
        // let the window close while `stop()` still owns the kill-on-close job.
        if matches!(self.dialog, Some(Dialog::ConfirmClose { .. })) || self.stopping {
            return;
        }
        let Some(mut runtime) = self.runtime.take() else { return };
        self.stopping = true;
        let events = self.tx.clone();
        self.log(Text::ServicesStopping);
        thread::spawn(move || {
            let result = runtime.stop();
            let _ = events.send(Event::ServicesStopped(result));
        });
    }

    fn finish_services_stopped(&mut self, result: Result<()>) {
        self.stopping = false;
        let close_when_stopped = std::mem::take(&mut self.close_when_stopped);
        match result {
            Ok(()) => self.log(Text::ServicesStopped),
            Err(error) => {
                let _ = local::summarized_error("stop the local server and MongoDB", &error);
                self.log(Text::ServicesStopFailed);
            }
        }
        if close_when_stopped {
            self.request_close();
        }
    }

    // ----- settings -----

    /// The Game folder picker returned (`None` = cancelled).
    pub fn game_folder_picked(&mut self, path: Option<PathBuf>) {
        let Some(path) = path else { return };
        if self.dialog.is_some() || self.busy.is_some() {
            return;
        }
        if !crate::steam::valid_game_directory(&path) {
            self.dialog = Some(Dialog::Problem(Problem::InvalidFolder, path.display().to_string()));
            return;
        }
        self.settings.selected_game = Some(path);
        if self.save() {
            self.patch = None;
            self.start_refresh(false, false);
        }
    }

    pub fn check_updates(&mut self) {
        if self.dialog.is_none() && self.commit_game() {
            self.start_refresh(true, false);
        }
    }

    /// Settings: set up again; Home: SETUP / UPDATE. Always confirmed first.
    pub fn request_setup(&mut self) {
        if self.dialog.is_none() && self.commit_game() {
            self.dialog = Some(Dialog::ConfirmSetup);
        }
    }

    pub fn restore(&mut self) {
        if self.dialog.is_none() {
            self.start_restore();
        }
    }

    pub fn set_music(&mut self, on: bool) {
        if self.dialog.is_some() {
            return;
        }
        let muted = !on;
        let change = if muted {
            self.music.take();
            Ok(())
        } else if self.music.is_none() && !self.game_running {
            // While PGR runs the setting is saved only; `tick` starts it when the game exits.
            self.start_music()
        } else {
            Ok(())
        };
        if change.is_err() {
            self.log(Text::MusicChangeFailed);
            return;
        }
        self.settings.music_muted = muted;
        self.save();
    }

    fn start_music(&mut self) -> Result<()> {
        self.music = Some(Music::start(&self.music_path)?);
        Ok(())
    }

    pub fn set_mods_enabled(&mut self, on: bool) {
        self.change_mods(|settings, _| settings.client_mods_enabled = on);
    }

    pub fn set_mod(&mut self, row: usize, on: bool) {
        self.change_mods(|settings, catalog| {
            if let Some(item) = catalog.get(row) {
                settings.enabled_mods.insert(item.id.clone(), on);
            }
        });
    }

    fn refresh_mods(&mut self) {
        match local::root().and_then(|root| mods::discover(&ascnet_launcher::setup::mods_directory(&root))) {
            Ok(catalog) => self.mods = catalog,
            Err(error) => {
                self.mods.clear();
                self.mods_status = Some(Text::ModsUnavailable(format!("{error:#}")));
            }
        }
    }

    fn change_mods(&mut self, edit: impl FnOnce(&mut Settings, &[mods::ModInfo])) {
        if self.dialog.is_some() {
            return;
        }
        if self.busy.is_some() || self.stopping {
            self.mods_status = Some(Text::ModsChangeFailed("Wait for the current launcher operation".to_owned()));
            return;
        }
        let mut settings = self.settings.clone();
        edit(&mut settings, &self.mods);
        let (generation, events) = self.begin(Busy::Mods);
        self.mods_status = Some(Text::ModsApplying);
        thread::spawn(move || {
            let status = (|| {
                let status = apply_mod_settings(&settings)?;
                save_settings(&settings)?;
                Ok(status)
            })();
            let _ = events.send(Event::Work(Work { generation, result: Ok(WorkResult::Mods { settings, status }) }));
        });
    }

    // ----- workers -----

    /// Marks the launcher busy and returns the id the worker must echo back.
    fn begin(&mut self, busy: Busy) -> (u64, Sender<Event>) {
        self.busy = Some(busy);
        self.generation += 1;
        (self.generation, self.tx.clone())
    }

    fn start_launcher_check(&mut self) {
        if let Some(reason) = updater::updates_suppressed() {
            let _ = local::launcher_log(&format!("Automatic updates skipped: {reason}"));
            self.start_refresh(true, false);
            return;
        }
        if self.busy.is_some() || self.runtime.is_some() || self.stopping {
            return;
        }
        let repository = self.config.repository_url.clone();
        let (generation, events) = self.begin(Busy::Launcher);
        thread::spawn(move || {
            let _ = events.send(Event::Progress("Checking launcher releases…".to_owned()));
            let mut official = None;
            let result = (|| {
                let Some(release) = updater::check(&repository, LAUNCHER_VERSION)? else {
                    official = updater::official_build(LAUNCHER_VERSION);
                    return Ok(None);
                };
                let _ = events.send(Event::Progress("Downloading and verifying launcher update…".to_owned()));
                updater::stage(&release).map(Some)
            })();
            let _ = events.send(Event::Work(Work { generation, result: Ok(WorkResult::LauncherChecked(official, result)) }));
        });
    }

    fn start_refresh(&mut self, check_remote: bool, automatic: bool) {
        if self.busy.is_some() {
            return;
        }
        let config = self.config.clone();
        let game = self.settings.selected_game.clone();
        let (generation, events) = self.begin(Busy::Inspect);
        thread::spawn(move || {
            let result = (|| {
                let build = local::load_build()?;
                let package = build.as_ref().map(|b| package::load_package(&b.patch_directory)).transpose()?;
                let patch = match (&game, &package) {
                    (Some(game), Some(package)) if crate::steam::valid_game_directory(game) => {
                        Some(install::inspect(game, package)?)
                    }
                    _ => None,
                };
                // One-time cutover: FPS used to be a bundle patch; a leftover hook is removed (and logged on completion).
                let fps_migration = match &game {
                    Some(game) if crate::steam::valid_game_directory(game) => fps::remove_legacy_hook(game),
                    _ => Ok(None),
                };
                let update = if check_remote {
                    local::check_update(&config.repository_url, &config.branch, build.as_ref())
                } else {
                    Ok(None)
                };
                Ok(WorkResult::Refresh { build, package, patch, fps_migration, update, automatic })
            })();
            let _ = events.send(Event::Work(Work { generation, result }));
        });
    }

    /// The game must be closed before files change; shows the dialog and returns false otherwise.
    fn game_closed(&mut self) -> bool {
        match install::game_running() {
            Ok(false) => true,
            Ok(true) => {
                self.dialog = Some(Dialog::Problem(Problem::GameRunning, String::new()));
                false
            }
            Err(e) => {
                self.problem(Problem::GameCheck, &e);
                false
            }
        }
    }

    fn start_prepare(&mut self) {
        if !self.game_closed() {
            return;
        }
        if self.busy.is_some() || self.runtime.is_some() || self.stopping {
            return;
        }
        let Some(game) = self.settings.selected_game.clone() else {
            self.dialog = Some(Dialog::Problem(Problem::NoFolder, String::new()));
            return;
        };
        let config = self.config.clone();
        let settings = self.settings.clone();
        let (generation, events) = self.begin(Busy::Setup);
        thread::spawn(move || {
            let result = (|| {
                let mut progress = |s: &str| {
                    let _ = events.send(Event::Progress(s.to_owned()));
                };
                install::log_action("Setup", &game, None, None);
                let build = local::prepare(&config.repository_url, &config.branch, &game, &mut progress)?;
                apply_mod_settings(&settings)?;
                let package = package::load_package(&build.patch_directory)?;
                let patch = install::inspect(&game, &package);
                install::log_action("Setup", &game, Some(&package), patch.as_ref().ok());
                let patch = patch?;
                Ok(WorkResult::Prepared { build, package, patch })
            })();
            let result = install::log_result("Setup", result);
            let _ = events.send(Event::Work(Work { generation, result }));
        });
    }

    fn start_restore(&mut self) {
        if !self.game_closed() {
            return;
        }
        if self.busy.is_some() || self.stopping {
            return;
        }
        let Some(game) = self.settings.selected_game.clone() else {
            self.dialog = Some(Dialog::Problem(Problem::NoFolder, String::new()));
            return;
        };
        let (generation, events) = self.begin(Busy::Restore);
        thread::spawn(move || {
            let result = (|| {
                install::log_action("Restore", &game, None, None);
                let progress_events = events.clone();
                install::restore_with_consent(&game, &mut |s| {
                    let _ = progress_events.send(Event::Progress(s.to_string()));
                })?;
                #[cfg(target_os = "macos")]
                ascnet_launcher::rosetta::restore(&game)?;
                mods::clean(&game, &mods::persistent_directory(&game)?)?;
                mods::set_debug(&local::root()?.join("config.json"), false)?;
                Ok(WorkResult::Restored)
            })();
            let result = install::log_result("Restore", result);
            let _ = events.send(Event::Work(Work { generation, result }));
        });
    }

    fn start_play(&mut self) {
        if self.busy.is_some() {
            return;
        }
        if let Err(block) = self.can_play() {
            self.dialog = Some(Dialog::Problem(Problem::Blocked(block), block.to_string()));
            return;
        }
        let game = self.settings.selected_game.clone().unwrap();
        let build = self.build.clone().unwrap();
        let package = self.package.clone().unwrap();
        let settings = self.settings.clone();
        let (generation, events) = self.begin(Busy::Play);
        thread::spawn(move || {
            let result = (|| {
                apply_mod_settings(&settings)?;
                let patch = install::inspect(&game, &package);
                install::log_action("Play", &game, Some(&package), patch.as_ref().ok());
                let patch = patch?;
                if !matches!(patch, PatchState::Current) {
                    return Err(PlayBlock::NeedsPatch.into());
                }
                install::install_with_consent(&game, &package, &mut |s| {
                    let _ = events.send(Event::Progress(s.to_string()));
                })?;
                let mut runtime = LocalRuntime::start(&build, &mut |s| {
                    let _ = events.send(Event::Progress(s.to_owned()));
                })?;
                let origin = local_origin(&build)?;
                let server = match fetch_status(&origin).and_then(|s| {
                    can_play_exact(&package, &patch, &s)?;
                    Ok(s)
                }) {
                    Ok(server) => server,
                    Err(e) => {
                        let _ = runtime.stop();
                        return Err(e);
                    }
                };
                install::launch(&game, &origin)?;
                Ok(WorkResult::Launched { build, package, runtime, server })
            })();
            let result = install::log_result("Play", result);
            let _ = events.send(Event::Work(Work { generation, result }));
        });
    }

    fn finish_work(&mut self, work: Work) {
        if self.files.working() {
            return self.files_finish(work);
        }
        if work.generation != self.generation {
            return;
        }
        let busy = self.busy.take();
        let mut automatic_prepare = false;
        let mut notes = Vec::new();
        match work.result {
            Ok(WorkResult::Mods { settings, status }) => match status {
                Ok(applied) => {
                    self.settings = settings;
                    self.mods_status = Some(match applied {
                        ModsApplied::Applied { applied, skipped } => {
                            Text::ModsApplied { applied: applied.join(", "), skipped: skipped.join(", ") }
                        }
                        ModsApplied::Disabled => Text::ModsDisabled,
                    });
                    notes.push(Text::ModsUpdated);
                }
                Err(error) => {
                    let _ = local::summarized_error("change client mods", &error);
                    self.mods_status = Some(Text::ModsChangeFailed(format!("{error:#}")));
                    notes.push(Text::ModsFailed);
                }
            },
            Ok(WorkResult::LauncherChecked(official, result)) => {
                self.official_build = official;
                let message = match result {
                    Ok(Some(staged)) if self.runtime.is_none() => {
                        match install::game_running().and_then(|running| {
                            anyhow::ensure!(!running, "PGR is running; launcher update deferred");
                            updater::launch_update(staged)
                        }) {
                            Ok(()) => {
                                self.busy = Some(Busy::Launcher);
                                self.quit = true;
                                self.log(Text::LauncherUpdateRestarting);
                                return;
                            }
                            Err(e) => {
                                let _ = local::summarized_error("apply launcher update", &e);
                                Text::LauncherUpdateFailed
                            }
                        }
                    }
                    Ok(Some(_)) => Text::LauncherUpdateDeferred,
                    Ok(None) => Text::LauncherCurrent(LAUNCHER_VERSION),
                    Err(e) => {
                        let _ = local::summarized_error("check for launcher updates", &e);
                        Text::LauncherCheckFailed
                    }
                };
                self.log(message);
                self.start_refresh(true, true);
                return;
            }
            Ok(WorkResult::Refresh { build, package, patch, fps_migration, update, automatic }) => {
                self.build = build;
                self.package = package;
                if let Some(PatchState::Unsupported(reason) | PatchState::RepairRequired(reason)) = &patch {
                    let _ = local::launcher_log(&format!("Game patch unavailable: {reason}"));
                    notes.push(Text::PatchUnavailable);
                }
                self.patch = patch;
                match fps_migration {
                    Ok(Some(value)) => {
                        let saved = match self.settings.selected_game.as_deref() {
                            Some(game) => graphics_ini::set_fps(game, value)
                                .map_err(|e| local::summarized_error("save the FPS setting after removing the old bundle FPS patch", &e)),
                            None => Err("Removed the old bundle FPS patch, but no game folder is selected to store the FPS setting".to_owned()),
                        };
                        match saved {
                            Ok(()) => {
                                let _ = local::launcher_log(&format!("Removed the old bundle FPS patch (backup kept); FPS unlock is now native, `fps = {value}` written to {}", graphics_ini::FILE));
                                notes.push(Text::FpsMigrated);
                            }
                            Err(line) => {
                                let _ = local::launcher_log(&line);
                                notes.push(Text::FpsMigrationFailed);
                            }
                        }
                    }
                    Ok(None) => {}
                    Err(e) => {
                        let _ = local::summarized_error("remove the old bundle FPS patch", &e);
                        notes.push(Text::FpsMigrationFailed);
                    }
                }
                match update {
                    Ok(value) => {
                        self.update_available = value;
                        self.update_error = None;
                    }
                    Err(e) => {
                        self.update_available = None;
                        self.update_error = Some(local::summarized_error("check for source updates", &e));
                    }
                }
                automatic_prepare = automatic && self.update_available == Some(true) && self.can_play().is_ok();
                // Not having asked for a remote check is not news.
                match (self.update_available, &self.update_error) {
                    (Some(true), _) => notes.push(Text::ServerUpdateAvailable),
                    (Some(false), _) => notes.push(Text::ServerCurrent),
                    (None, Some(_)) => notes.push(Text::ServerCheckFailed),
                    (None, None) => {}
                }
            }
            Ok(WorkResult::Prepared { build, package, patch }) => {
                self.build = Some(build);
                self.package = Some(package);
                self.patch = Some(patch);
                self.update_available = None;
                self.update_error = None;
                notes.push(Text::SetupDone);
            }
            Ok(WorkResult::Restored) => {
                self.patch = Some(PatchState::Unpatched);
                notes.push(Text::Restored);
            }
            Ok(WorkResult::Client(_)) => {}
            #[cfg(target_os = "macos")]
            Ok(WorkResult::Mac) => {
                let note = self.mac_finished(busy);
                notes.push(note);
            }
            Ok(WorkResult::Launched { build, package, runtime, server }) => {
                self.build = Some(build);
                self.package = Some(package);
                self.runtime = Some(runtime);
                self.server = Some(server);
                notes.push(Text::ServerReady);
            }
            Err(e) if e.downcast_ref::<install::ElevationCancelled>().is_some() => {
                self.log(Text::ElevationCancelled);
                return;
            }
            Err(e) => {
                let (problem, text) = match busy {
                    Some(Busy::Setup) => (Problem::Setup, Text::SetupFailed),
                    Some(Busy::Restore) => (Problem::Restore, Text::RestoreFailed),
                    Some(Busy::Play) => (Problem::Play, Text::PlayFailed),
                    #[cfg(target_os = "macos")]
                    Some(Busy::CrossOver) => (Problem::CrossOver, Text::CrossOverFailed),
                    #[cfg(target_os = "macos")]
                    Some(Busy::Bottle) => (Problem::Bottle, Text::BottleFailed),
                    _ => (Problem::Check, Text::CheckFailed),
                };
                let (problem, text) = match e.downcast_ref::<PlayBlock>() {
                    Some(block) => (Problem::Blocked(*block), text),
                    None => (problem, text),
                };
                self.log(text);
                self.problem(problem, &e);
                return;
            }
        }
        self.refresh_mods();
        for note in notes {
            self.log(note);
        }
        if automatic_prepare {
            match install::game_running() {
                Ok(false) => self.start_prepare(),
                Ok(true) => self.log(Text::ServerUpdateDeferred),
                Err(e) => {
                    let _ = local::summarized_error("start source update", &e);
                    self.log(Text::ServerUpdateFailed);
                }
            }
        }
    }

    // ----- state helpers -----

    fn services(&self) -> bool {
        self.runtime.is_some() || self.stopping
    }

    fn can_play(&self) -> Result<(), PlayBlock> {
        if self.settings.selected_game.is_none() {
            return Err(PlayBlock::NoGame);
        }
        if self.build.is_none() {
            return Err(PlayBlock::NeedsBuild);
        }
        if self.package.is_none() || !matches!(self.patch, Some(PatchState::Current)) {
            return Err(PlayBlock::NeedsPatch);
        }
        if self.stopping {
            return Err(PlayBlock::Stopping);
        }
        if self.runtime.is_some() {
            return Err(PlayBlock::AlreadyRunning);
        }
        Ok(())
    }

    /// Validates the selected folder before Setup / Play act on it.
    fn commit_game(&mut self) -> bool {
        let Some(game) = self.settings.selected_game.clone() else {
            self.dialog = Some(Dialog::Problem(Problem::NoFolder, String::new()));
            return false;
        };
        if !crate::steam::valid_game_directory(&game) {
            self.dialog = Some(Dialog::Problem(Problem::InvalidFolder, game.display().to_string()));
            return false;
        }
        self.save()
    }

    fn save(&mut self) -> bool {
        match save_settings(&self.settings) {
            Ok(()) => true,
            Err(e) => {
                self.problem(Problem::Save, &e);
                false
            }
        }
    }

    /// Opens an error dialog; the raw error goes to the log and behind Details.
    fn problem(&mut self, problem: Problem, error: &anyhow::Error) {
        let raw = local::logged_error(&format!("{error:#}"));
        self.dialog = Some(Dialog::Problem(problem, raw));
    }

    fn log(&mut self, text: Text) {
        if self.log.back().map_or(true, |(_, last)| *last != text) {
            if self.log.len() == LOG_LIMIT {
                self.log.pop_front();
            }
            self.log.push_back((clock(), text));
        }
    }

    // ----- the view -----

    pub fn view(&self) -> View {
        let selected = self.settings.selected_game.is_some();
        let version = self.client_version();
        let chip = version.split('.').take(2).collect::<Vec<_>>().join(".");
        let services = self.services();
        let can_launch = self.can_play().is_ok();
        let primary = if self.busy.is_some() {
            Primary::Working
        } else if self.stopping {
            Primary::Stopping
        } else if self.runtime.is_some() {
            Primary::Running
        } else if let Some(prerequisite) = self.platform_primary() {
            prerequisite
        } else if !selected {
            Primary::SelectGame
        } else if self.update_available == Some(true) {
            Primary::Update
        } else if !can_launch {
            Primary::Setup
        } else {
            Primary::Play
        };
        let patch = match &self.patch {
            None | Some(PatchState::Unpatched) => PatchLabel::NotInstalled,
            Some(PatchState::Current) => PatchLabel::Current,
            Some(PatchState::UpdateAvailable | PatchState::AdoptionRequired) => PatchLabel::UpdateAvailable,
            Some(PatchState::Unsupported(_)) => PatchLabel::Unsupported,
            Some(PatchState::RepairRequired(_)) => PatchLabel::RepairNeeded,
        };
        let server = if self.stopping {
            ServerLabel::Stopping
        } else {
            match (&self.runtime, &self.server) {
                (None, _) => ServerLabel::Stopped,
                (Some(_), Some(s)) if s.maintenance => ServerLabel::Maintenance,
                (Some(_), Some(s)) if s.online => ServerLabel::Running,
                (Some(_), _) => ServerLabel::Offline,
            }
        };
        let mac_home = self.platform_home();
        let hint = match (primary, &self.patch) {
            (Primary::Running, _) => Text::HintRunning,
            (Primary::Stopping, _) => Text::HintStopping,
            (Primary::InstallCrossOver, _) => Text::HintInstallCrossOver,
            (Primary::UseFound, _) => Text::HintUseFound,
            (Primary::CreateBottle, _) if matches!(&mac_home, Some(MacHome { bottle: BottleLabel::Repair, .. })) => Text::HintRepairBottle,
            (Primary::CreateBottle, _) => Text::HintCreateBottle,
            (Primary::SelectGame, _) => Text::HintSelectGame,
            (_, Some(PatchState::Unsupported(_))) => Text::HintPatchUnsupported,
            (_, Some(PatchState::RepairRequired(_))) => Text::HintPatchRepair,
            (Primary::Update, _) => Text::HintUpdate,
            (Primary::Setup, _) => Text::HintSetup,
            _ => Text::HintPlay,
        };
        let step = self.platform_step();
        let busy = step.map(|(text, _)| text.clone()).or_else(|| self.busy.map(Busy::label_text));
        let progress = match step {
            Some((_, fraction)) => *fraction,
            None => self.files.inflight().and_then(|_| self.files.fraction_value()),
        };
        let home = Home {
            game: selected.then(|| (self.files.region, version.clone())),
            chip,
            patch,
            server,
            primary,
            primary_enabled: self.busy.is_none() && !services,
            hint,
            busy,
            progress,
            mac: mac_home,
            found: self.platform_found(),
        };
        let mods_available = self.busy.is_none() && !self.stopping;
        let mods_status = self.mods_status.clone().unwrap_or(if self.mods.is_empty() { Text::ModsNone } else { Text::ModsHint });
        let settings = SettingsView {
            folder: self.settings.selected_game.as_deref().map(|p| p.display().to_string()).unwrap_or_default(),
            music_on: !self.settings.music_muted,
            can_check: self.busy.is_none(),
            can_setup: self.busy.is_none() && !services && selected,
            can_restore: self.busy.is_none() && !self.stopping && selected,
            mods_on: self.settings.client_mods_enabled,
            mods_editable: mods_available,
            mods: self
                .mods
                .iter()
                .map(|m| ModRow {
                    name: m.name.clone(),
                    description: m.description.clone(),
                    on: self.settings.enabled_mods.get(&m.id).copied().unwrap_or(false),
                })
                .collect(),
            mods_status,
        };
        View {
            screen: self.screen,
            language: self.files.region,
            dialog: self.dialog.clone(),
            home,
            activity: self
                .log
                .iter()
                .rev()
                .take(ACTIVITY_LINES)
                .map(|(time, text)| ActivityLine { time: time.clone(), text: text.clone() })
                .collect(),
            settings,
            files: self.files_view(),
            official_build: self.official_build,
        }
    }

    /// The client version this launcher targets until Setup builds the patch package.
    fn client_version(&self) -> String {
        let bundled = serde_json::from_slice::<serde_json::Value>(include_bytes!("../supported-client.json"))
            .ok()
            .and_then(|v| v["applicationVersion"].as_str().map(str::to_owned));
        self.package
            .as_ref()
            .map(|p| p.manifest.application_version.clone())
            .or(bundled)
            .unwrap_or_else(|| "unknown".to_owned())
    }
}

impl Busy {
    fn label_text(self) -> Text {
        // Busy labels reuse the Text space so the UI has one translation path.
        match self {
            Busy::Launcher => Text::CheckingLauncher,
            Busy::Inspect => Text::HintWorkingInspect,
            Busy::Setup => Text::HintWorkingSetup,
            Busy::Restore => Text::HintWorkingRestore,
            Busy::Play => Text::HintWorkingPlay,
            Busy::Mods => Text::ModsApplying,
            Busy::FilesInspect => Text::FilesInspecting,
            Busy::Files(kind) => Text::FilesRunning(kind),
            #[cfg(target_os = "macos")]
            Busy::CrossOver => Text::CrossOverPhase(0),
            #[cfg(target_os = "macos")]
            Busy::Bottle => Text::WorkingBottle,
        }
    }
}

/// The macOS offers of existing installs; Windows has none.
impl Controller {
    fn platform_found(&self) -> Vec<FoundView> {
        #[cfg(target_os = "macos")]
        {
            self.mac_found()
        }
        #[cfg(not(target_os = "macos"))]
        {
            Vec::new()
        }
    }
}

/// The macOS prerequisites; Windows has none, so these answer "nothing to show".
impl Controller {
    #[cfg(target_os = "macos")]
    fn platform_primary(&self) -> Option<Primary> {
        self.mac_primary()
    }
    #[cfg(not(target_os = "macos"))]
    fn platform_primary(&self) -> Option<Primary> {
        None
    }
    #[cfg(target_os = "macos")]
    fn platform_home(&self) -> Option<MacHome> {
        Some(self.mac_home())
    }
    #[cfg(not(target_os = "macos"))]
    fn platform_home(&self) -> Option<MacHome> {
        None
    }
    #[cfg(target_os = "macos")]
    fn platform_step(&self) -> Option<&(Text, Option<f32>)> {
        self.mac_step()
    }
    #[cfg(not(target_os = "macos"))]
    fn platform_step(&self) -> Option<&(Text, Option<f32>)> {
        None
    }
}

fn apply_mod_settings(settings: &Settings) -> Result<ModsApplied> {
    let game = settings.selected_game.as_deref().context("Select a game folder before changing client mods")?;
    let root = local::root()?;
    let config = root.join("config.json");
    let persistent = mods::persistent_directory(game)?;
    if settings.client_mods_enabled {
        mods::set_debug(&config, true)?;
        let selected: Vec<String> =
            settings.enabled_mods.iter().filter(|(_, enabled)| **enabled).map(|(id, _)| id.clone()).collect();
        let report = mods::apply(game, &persistent, &ascnet_launcher::setup::mods_directory(&root), &config, &selected)?;
        Ok(ModsApplied::Applied { applied: report.applied, skipped: report.skipped })
    } else {
        mods::set_debug(&config, false)?;
        mods::clean(game, &persistent)?;
        Ok(ModsApplied::Disabled)
    }
}

fn can_play_exact(package: &PatchPackage, patch: &PatchState, server: &ServerStatus) -> Result<(), PlayBlock> {
    if !matches!(patch, PatchState::Current) {
        return Err(PlayBlock::NeedsPatch);
    }
    if server.schema_version != 1 {
        return Err(PlayBlock::UnsupportedSchema);
    }
    if server.maintenance {
        return Err(PlayBlock::Maintenance);
    }
    if !server.online {
        return Err(PlayBlock::Offline);
    }
    if let Some(v) = &server.minimum_launcher_version {
        if package::compare_versions(LAUNCHER_VERSION, v).is_ok_and(|o| o == std::cmp::Ordering::Less) {
            return Err(PlayBlock::LauncherOutdated);
        }
    }
    if let Some(v) = &server.minimum_patch_version {
        if package::compare_versions(&package.manifest.version, v).is_ok_and(|o| o == std::cmp::Ordering::Less) {
            return Err(PlayBlock::PatchOutdated);
        }
    }
    if !server.supported_clients.iter().any(|c| c.application_version == package.manifest.application_version) {
        return Err(PlayBlock::UnsupportedClient);
    }
    Ok(())
}

fn local_origin(build: &LocalBuild) -> Result<String> {
    package::validate_server_origin(&format!("http://127.0.0.1:{}", build.sdk_port))
}

fn fetch_status(origin: &str) -> Result<ServerStatus> {
    let origin = package::validate_server_origin(origin)?;
    let mut response = download::wine_safe(
        reqwest::blocking::Client::builder()
            .no_proxy()
            .timeout(Duration::from_secs(15))
            .redirect(reqwest::redirect::Policy::none()),
    )
    .build()?
    .get(format!("{origin}/api/launcher/status"))
    .send()?
    .error_for_status()?;
    let length = response.content_length();
    if length.is_some_and(|n| n > 65_536) {
        anyhow::bail!("server status exceeds 64 KiB")
    }
    let bytes = download::read_body(&mut response, length, 65_536).context("reading server status")?;
    if bytes.len() > 65_536 {
        anyhow::bail!("server status exceeds 64 KiB")
    }
    if let Some(n) = length {
        if bytes.len() as u64 != n {
            anyhow::bail!("server status ended after {} bytes, expected {n}", bytes.len());
        }
    }
    let s: ServerStatus = serde_json::from_slice(&bytes)?;
    if s.schema_version != 1 {
        anyhow::bail!("unsupported server status schema {}", s.schema_version)
    }
    Ok(s)
}

fn settings_dir() -> Result<PathBuf> {
    Ok(local::data_base()?.join("AscNet/Launcher"))
}
fn load_settings() -> Result<Settings> {
    Ok(serde_json::from_slice(&fs::read(settings_dir()?.join("settings.json"))?)?)
}
fn save_settings(s: &Settings) -> Result<()> {
    let dir = settings_dir()?;
    fs::create_dir_all(&dir)?;
    let temp = dir.join("settings.tmp");
    fs::write(&temp, serde_json::to_vec_pretty(s)?)?;
    fs::rename(temp, dir.join("settings.json"))?;
    Ok(())
}

/// Local wall-clock "HH:MM" for the activity lines.
fn clock() -> String {
    #[cfg(windows)]
    {
        let t = unsafe { windows::Win32::System::SystemInformation::GetLocalTime() };
        format!("{:02}:{:02}", t.wHour, t.wMinute)
    }
    #[cfg(unix)]
    {
        let now = unsafe { libc::time(std::ptr::null_mut()) };
        let mut local: libc::tm = unsafe { std::mem::zeroed() };
        unsafe { libc::localtime_r(&now, &mut local) };
        format!("{:02}:{:02}", local.tm_hour, local.tm_min)
    }
}

/// Windows UI language -> client edition (zh-CN/zh-SG get the mainland client, other Chinese the Taiwan one).
#[cfg_attr(not(windows), allow(dead_code))]
pub fn region_for_langid(langid: u16) -> Region {
    match (langid & 0x3ff, langid) {
        (0x12, _) => Region::Kr,
        (0x11, _) => Region::Jp,
        (0x04, 0x0804 | 0x1004) => Region::Cn,
        (0x04, _) => Region::Tw,
        _ => Region::En,
    }
}
pub fn default_region() -> Region {
    #[cfg(windows)]
    {
        region_for_langid(unsafe { windows::Win32::Globalization::GetUserDefaultUILanguage() })
    }
    #[cfg(not(windows))]
    {
        Region::En
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn legacy_settings_default_mods_off_and_selection_round_trips() -> anyhow::Result<()> {
        let mut settings: Settings = serde_json::from_str(r#"{"selectedGame":null}"#)?;
        assert!(!settings.client_mods_enabled);
        assert!(settings.enabled_mods.is_empty());
        settings.client_mods_enabled = true;
        settings.enabled_mods.insert("console".to_owned(), true);
        settings.enabled_mods.insert("disabled".to_owned(), false);
        let restored: Settings = serde_json::from_slice(&serde_json::to_vec(&settings)?)?;
        assert!(restored.client_mods_enabled);
        assert_eq!(restored.enabled_mods, settings.enabled_mods);
        Ok(())
    }

    #[test]
    fn ui_language_picks_edition() {
        assert!(region_for_langid(0x0409) == Region::En);
        assert!(region_for_langid(0x0404) == Region::Tw);
        assert!(region_for_langid(0x0c04) == Region::Tw);
        assert!(region_for_langid(0x0804) == Region::Cn);
        assert!(region_for_langid(0x1004) == Region::Cn);
        assert!(region_for_langid(0x0412) == Region::Kr);
        assert!(region_for_langid(0x0411) == Region::Jp);
    }
}

//! Slint front end: renders `controller::View` into the `S` global, forwards user intent, and owns the
//! window, the background video, the renderer choice and the native pickers.
use crate::controller::{self as c, Controller};
use anyhow::Result;
use ascnet_launcher::{
    download::{Phase, Region},
    install, local,
    media::{Frame, Video},
    updater,
};
use slint::{winit_030::WinitWindowAccessor, ComponentHandle, Image, Model, ModelRc, Rgba8Pixel, SharedPixelBuffer, Timer, TimerMode, VecModel, Weak};
use std::{
    cell::RefCell,
    env,
    path::PathBuf,
    rc::Rc,
    sync::{
        atomic::{AtomicBool, Ordering},
        Arc,
    },
    time::Duration,
};

slint::include_modules!();

const POLL: Duration = Duration::from_millis(50);
/// PGR.exe is polled every `GAME_WATCH_TICKS` polls (~2 s).
const GAME_WATCH_TICKS: u32 = 40;
/// Set when a previous run could not start the GPU renderer.
const RENDERER_ENV: &str = "ASCNET_RENDERER";

type Shared = Rc<RefCell<Controller>>;

// ----- startup ------------------------------------------------------------------------------

pub fn run() -> Result<()> {
    let _singleton = single_instance()?;
    let exe_dir = local::resource_dir()?;
    let controller: Shared = Rc::new(RefCell::new(Controller::new(&exe_dir)?));

    // Wine has no usable GPU path for femtovg; everywhere else try the GPU renderer first.
    let renderer = env::var(RENDERER_ENV).unwrap_or_else(|_| if install::running_under_wine() { "software" } else { "femtovg" }.to_owned());
    slint::BackendSelector::new()
        .backend_name("winit".into())
        .renderer_name(renderer.clone())
        .select()
        .map_err(|e| anyhow::anyhow!("selecting the {renderer} renderer: {e}"))?;
    let app = App::new()?;
    if let Err(error) = app.show() {
        if renderer != "software" {
            // The GPU renderer could not start (old driver, remote desktop, ...). The platform is
            // chosen once per process, so run again with the software renderer.
            let _ = local::launcher_log(&format!("{renderer} renderer unavailable ({error}); restarting with the software renderer"));
            drop(app);
            drop(controller);
            drop(_singleton);
            std::process::Command::new(env::current_exe()?).args(env::args_os().skip(1)).env(RENDERER_ENV, "software").spawn()?;
            return Ok(());
        }
        return Err(error.into());
    }
    let _ = local::launcher_log(&format!("Launcher window shown ({renderer} renderer)"));

    let weak = app.as_weak();
    let art = start_background(&app, &exe_dir);
    if art.is_none() {
        controller.borrow_mut().video_unavailable();
    }
    bind(&app, &controller);
    render(&weak, &controller);

    // Screenshot aid for headless smoke runs (synthetic mouse input does not reach Wine on macOS).
    match env::var("ASCNET_UI_START").as_deref() {
        Ok("settings") => controller.borrow_mut().open_settings(),
        Ok("files") => controller.borrow_mut().open_files(),
        Ok("setup") => controller.borrow_mut().request_setup(),
        #[cfg(target_os = "macos")]
        Ok("crossover") => controller.borrow_mut().show_crossover_prompt(),
        _ => {}
    }
    render(&weak, &controller);

    updater::acknowledge_startup()?;
    controller.borrow_mut().start();
    render(&weak, &controller);

    let (tick_weak, tick_controller) = (weak.clone(), controller.clone());
    let mut ticks = 0u32;
    let mut minimized = false;
    let timer = Timer::default();
    timer.start(TimerMode::Repeated, POLL, move || {
        let mut changed = tick_controller.borrow_mut().pump();
        ticks += 1;
        if ticks % GAME_WATCH_TICKS == 0 {
            tick_controller.borrow_mut().tick();
            changed = true;
        }
        if let Some(app) = tick_weak.upgrade() {
            let now = app.window().is_minimized();
            if now != minimized {
                minimized = now;
                if let Some(video) = &art {
                    video.set_paused(now);
                }
            }
        }
        if changed {
            render(&tick_weak, &tick_controller);
        }
    });

    slint::run_event_loop()?;
    drop(timer);
    Ok(())
}

/// Shows the still image at once, then lets the decoded video replace it frame by frame.
fn start_background(app: &App, exe_dir: &std::path::Path) -> Option<Video> {
    let s = app.global::<S>();
    if let Ok(still) = Image::load_from_path(&exe_dir.join("background.bmp")) {
        s.set_art(still);
    }
    let weak = app.as_weak();
    // At most one frame waits in the event loop; a slow UI thread drops frames instead of queueing them.
    let pending = Arc::new(AtomicBool::new(false));
    match Video::start(&exe_dir.join("background.mp4"), move |frame: Frame| {
        if pending.swap(true, Ordering::AcqRel) {
            return;
        }
        let pending = pending.clone();
        let _ = weak.upgrade_in_event_loop(move |app| {
            let buffer = SharedPixelBuffer::<Rgba8Pixel>::clone_from_slice(&frame.rgba, frame.width, frame.height);
            app.global::<S>().set_art(Image::from_rgba8(buffer));
            pending.store(false, Ordering::Release);
        });
    }) {
        Ok(video) => Some(video),
        Err(error) => {
            let _ = local::launcher_log(&format!("Background video unavailable, showing the still image: {error:#}"));
            None
        }
    }
}

/// Wires every Slint callback to a controller method, then redraws.
fn bind(app: &App, controller: &Shared) {
    let s = app.global::<S>();
    let weak = app.as_weak();
    // `act!(|c| c.method())` / `act!(|c, x: T| c.method(x))`: run on the controller, then re-render.
    macro_rules! act {
        (|$c:ident $(, $arg:ident : $ty:ty)*| $body:expr) => {{
            let (controller, weak) = (controller.clone(), weak.clone());
            move |$($arg: $ty),*| {
                {
                    let $c = &mut *controller.borrow_mut();
                    $body;
                }
                render(&weak, &controller);
            }
        }};
    }
    s.on_primary_clicked(act!(|c| c.primary()));
    s.on_cancel_patch_sync(act!(|c| c.cancel_patch_sync()));
    s.on_open_settings(act!(|c| c.open_settings()));
    s.on_open_files(act!(|c| c.open_files()));
    s.on_back(act!(|c| c.back()));
    s.on_close(act!(|c| c.request_close()));
    s.on_dialog_answer(act!(|c, yes: bool| c.answer(yes)));
    s.on_check_updates(act!(|c| c.check_updates()));
    s.on_set_up_again(act!(|c| c.request_setup()));
    s.on_restore_files(act!(|c| c.restore()));
    s.on_music_toggled(act!(|c, on: bool| c.set_music(on)));
    s.on_use_running_mongo_toggled(act!(|c, on: bool| c.set_use_running_mongo(on)));
    s.on_mods_toggled(act!(|c, on: bool| c.set_mods_enabled(on)));
    s.on_mod_toggled(act!(|c, row: i32, on: bool| c.set_mod(row as usize, on)));
    s.on_open_official(|| open_official());
    s.on_region_picked(act!(|c, row: i32| c.files_set_region(c::REGIONS[row as usize])));
    s.on_slot_clicked(act!(|c, index: i32| c.files_action(index as usize)));

    let w = weak.clone();
    s.on_minimize(move || {
        if let Some(app) = w.upgrade() {
            app.window().set_minimized(true);
        }
    });
    let w = weak.clone();
    s.on_drag(move || {
        if let Some(app) = w.upgrade() {
            app.window().with_winit_window(|window| {
                let _ = window.drag_window();
            });
        }
    });
    let (controller_, w) = (controller.clone(), weak.clone());
    s.on_choose_game_folder(move || {
        pick_folder(&w, &controller_, MsgId::PickGameFolder, |c, path| c.game_folder_picked(path));
    });
    let (controller_, w) = (controller.clone(), weak.clone());
    s.on_choose_files_folder(move || {
        pick_folder(&w, &controller_, MsgId::PickFilesFolder, |c, path| c.files_folder_picked(path));
    });
    #[cfg(target_os = "macos")]
    {
        s.on_new_bottle(act!(|c| c.mac_new_bottle()));
        s.on_adopt_clicked(act!(|c, index: i32| c.adopt_offer(index as usize)));
        let (controller_, w) = (controller.clone(), weak.clone());
        s.on_choose_prefix(move || {
            pick_folder(&w, &controller_, MsgId::PickPrefixFolder, |c, path| c.prefix_picked(path));
        });
    }
    // Alt+F4 / the taskbar close go through the same confirmation as the close button.
    let (controller_, w) = (controller.clone(), weak.clone());
    app.window().on_close_requested(move || {
        controller_.borrow_mut().request_close();
        render(&w, &controller_);
        slint::CloseRequestResponse::KeepWindowShown
    });
}

/// Native folder dialog, parented to the launcher; the controller is not borrowed while it is open.
fn pick_folder(weak: &Weak<App>, controller: &Shared, title: MsgId, done: fn(&mut Controller, Option<PathBuf>)) {
    let Some(app) = weak.upgrade() else { return };
    let title = app.global::<Tx>().invoke_text(Msg { id: title, ..Default::default() });
    let window = app.window().window_handle();
    let (weak, controller) = (weak.clone(), controller.clone());
    let _ = slint::spawn_local(async move {
        let picked = rfd::AsyncFileDialog::new().set_title(title.as_str()).set_parent(&window).pick_folder().await;
        done(&mut controller.borrow_mut(), picked.map(|handle| handle.path().to_path_buf()));
        render(&weak, &controller);
    });
}

const OFFICIAL_URL: &str = "https://github.com/reiserFSs/InfiniteLoop";

/// Default browser; a failure is only logged (the same address is printed in the UI).
fn open_official() {
    #[cfg(windows)]
    let opened = {
        use windows::{core::{w, PCWSTR}, Win32::UI::{Shell::ShellExecuteW, WindowsAndMessaging::SW_SHOWNORMAL}};
        let url: Vec<u16> = OFFICIAL_URL.encode_utf16().chain(Some(0)).collect();
        // ShellExecuteW reports success with a value greater than 32.
        unsafe { ShellExecuteW(None, w!("open"), PCWSTR(url.as_ptr()), None, None, SW_SHOWNORMAL).0 as isize > 32 }
    };
    #[cfg(not(windows))]
    let opened = std::process::Command::new(if cfg!(target_os = "macos") { "open" } else { "xdg-open" })
        .arg(OFFICIAL_URL).spawn().is_ok();
    if !opened {
        let _ = local::launcher_log(&format!("Couldn't open {OFFICIAL_URL}"));
    }
}

// ----- rendering ----------------------------------------------------------------------------

fn render(weak: &Weak<App>, controller: &Shared) {
    let Some(app) = weak.upgrade() else { return };
    let (view, quit) = {
        let c = controller.borrow();
        (c.view(), c.should_quit())
    };
    if quit {
        let _ = slint::quit_event_loop();
        return;
    }
    let s = app.global::<S>();

    let language = region_index(view.language);
    if s.get_language() != language || s.get_launcher_version().is_empty() {
        apply_language(&s, view.language);
    }

    s.set_screen(match view.screen {
        c::Screen::Home => Screen::Home,
        c::Screen::Settings => Screen::Settings,
        c::Screen::Files => Screen::Files,
    });
    match &view.dialog {
        Some(dialog) => {
            let (id, details) = dialog_parts(dialog);
            s.set_dialog_id(id);
            s.set_dialog_details(details.into());
            let amount = match dialog {
                c::Dialog::ConfirmCancel(amount) => amount.clone(),
                #[cfg(target_os = "macos")]
                c::Dialog::ConfirmAdopt(candidate) => candidate.display_name(),
                _ => String::new(),
            };
            s.set_dialog_amount(amount.into());
            s.set_dialog_open(true);
        }
        None => s.set_dialog_open(false),
    }

    let home = &view.home;
    s.set_mac(home.mac.is_some());
    if let Some(mac) = &home.mac {
        s.set_crossover(match mac.crossover {
            c::CrossOverLabel::Missing => CrossOverLabel::Missing,
            c::CrossOverLabel::Working => CrossOverLabel::Working,
            c::CrossOverLabel::Ready => CrossOverLabel::Ready,
        });
        s.set_bottle(match mac.bottle {
            c::BottleLabel::Missing => BottleLabel::Missing,
            c::BottleLabel::Working => BottleLabel::Working,
            c::BottleLabel::Ready => BottleLabel::Ready,
            c::BottleLabel::Repair => BottleLabel::Repair,
            c::BottleLabel::Adopted => BottleLabel::Adopted,
        });
    }
    s.set_found(sync(
        s.get_found(),
        home.found.iter().map(|f| Found { name: f.name.as_str().into(), info: msg(&f.info) }).collect(),
    ));
    s.set_has_game(home.game.is_some());
    if let Some((region, version)) = &home.game {
        s.set_game_region(region_index(*region));
        s.set_game_version(version.as_str().into());
    }
    s.set_chip(home.chip.as_str().into());
    s.set_patch(match home.patch {
        c::PatchLabel::NotInstalled => PatchLabel::NotInstalled,
        c::PatchLabel::Current => PatchLabel::Current,
        c::PatchLabel::UpdateAvailable => PatchLabel::UpdateAvailable,
        c::PatchLabel::Unsupported => PatchLabel::Unsupported,
        c::PatchLabel::RepairNeeded => PatchLabel::RepairNeeded,
    });
    s.set_server(match home.server {
        c::ServerLabel::Stopped => ServerLabel::Stopped,
        c::ServerLabel::Stopping => ServerLabel::Stopping,
        c::ServerLabel::Maintenance => ServerLabel::Maintenance,
        c::ServerLabel::Running => ServerLabel::Running,
        c::ServerLabel::Offline => ServerLabel::Offline,
    });
    s.set_primary(match home.primary {
        c::Primary::SelectGame => Primary::SelectGame,
        c::Primary::Setup => Primary::Setup,
        c::Primary::Update => Primary::Update,
        c::Primary::Play => Primary::Play,
        c::Primary::Working => Primary::Working,
        c::Primary::Running => Primary::Running,
        c::Primary::Stopping => Primary::Stopping,
        c::Primary::InstallCrossOver => Primary::InstallCrossover,
        c::Primary::UseFound => Primary::UseFound,
        c::Primary::CreateBottle => Primary::CreateBottle,
    });
    s.set_primary_enabled(home.primary_enabled);
    s.set_hint(msg(&home.hint));
    s.set_build(match view.official_build {
        Some(true) => BuildLabel::Official,
        Some(false) => BuildLabel::Unofficial,
        None => BuildLabel::Unknown,
    });
    s.set_busy(home.busy.is_some());
    if let Some(busy) = &home.busy {
        s.set_busy_msg(msg(busy));
    }
    s.set_progress(home.progress.unwrap_or(-1.0));
    s.set_patch_sync(home.patch_sync);
    s.set_activity(sync(
        s.get_activity(),
        view.activity.iter().map(|line| Activity { time: line.time.as_str().into(), msg: msg(&line.text) }).collect(),
    ));

    let settings = &view.settings;
    s.set_folder(settings.folder.as_str().into());
    s.set_music_on(settings.music_on);
    s.set_use_running_mongo(settings.use_running_mongo);
    s.set_can_check(settings.can_check);
    s.set_can_setup(settings.can_setup);
    s.set_can_restore(settings.can_restore);
    s.set_mods_on(settings.mods_on);
    s.set_mods_editable(settings.mods_editable);
    s.set_mods(sync(
        s.get_mods(),
        settings
            .mods
            .iter()
            .map(|m| ModRow { name: m.name.as_str().into(), description: m.description.as_str().into(), on: m.on })
            .collect(),
    ));
    s.set_mods_status(msg(&settings.mods_status));

    let files = &view.files;
    s.set_tiles_enabled(files.tiles_enabled);
    s.set_files_folder(files.folder.as_str().into());
    s.set_files_can_choose(files.can_choose);
    s.set_files_info(msg(&files.info));
    s.set_files_plan(sync(s.get_files_plan(), files.plan.iter().map(msg).collect()));
    s.set_slots(sync(
        s.get_slots(),
        files
            .slots
            .iter()
            .map(|slot| SlotView {
                label: match slot.label {
                    c::SlotLabel::Download => SlotLabel::Download,
                    c::SlotLabel::Verify => SlotLabel::Verify,
                    c::SlotLabel::Update => SlotLabel::Update,
                    c::SlotLabel::Adopt => SlotLabel::Adopt,
                    c::SlotLabel::Pause => SlotLabel::Pause,
                    c::SlotLabel::Resume => SlotLabel::Resume,
                    c::SlotLabel::Cancel => SlotLabel::Cancel,
                    c::SlotLabel::Working => SlotLabel::Working,
                },
                enabled: slot.enabled,
                primary: slot.primary,
            })
            .collect(),
    ));
    s.set_has_transfer(files.transfer.is_some());
    if let Some(t) = &files.transfer {
        s.set_transfer(transfer(t));
    }
    s.set_has_working_label(files.working_label.is_some());
    if let Some(label) = &files.working_label {
        s.set_working_label(msg(label));
    }
    s.set_files_progress(files.fraction.unwrap_or(-1.0));
    s.set_files_paused(files.paused);
}

/// Writes `rows` into the existing list model in place. Replacing the model on every render
/// destroys and recreates the repeated elements, so a click pressed on one generation is never
/// released on the same element; during a download (several renders a second) buttons needed
/// a second click. Only a length change rebuilds the list.
fn sync<T: Clone + PartialEq + 'static>(current: ModelRc<T>, rows: Vec<T>) -> ModelRc<T> {
    if let Some(model) = current.as_any().downcast_ref::<VecModel<T>>() {
        if model.row_count() == rows.len() {
            for (index, row) in rows.into_iter().enumerate() {
                if model.row_data(index).as_ref() != Some(&row) {
                    model.set_row_data(index, row);
                }
            }
            return current;
        }
    }
    ModelRc::new(VecModel::from(rows))
}

/// Selects the translation and the face that covers its script. English falls back to the source strings.
fn apply_language(s: &S, region: Region) {
    let (code, font) = match region {
        Region::En => ("en", "Noto Sans"),
        Region::Tw => ("zh-TW", "Noto Sans TC"),
        Region::Kr => ("ko", "Noto Sans KR"),
        Region::Jp => ("ja", "Noto Sans JP"),
        Region::Cn => ("zh-CN", "Noto Sans SC"),
    };
    if let Err(error) = slint::select_bundled_translation(code) {
        let _ = local::launcher_log(&format!("UI language {code} unavailable: {error}"));
    }
    s.set_ui_font(font.into());
    s.set_language(region_index(region));
    s.set_launcher_version(c::LAUNCHER_VERSION.into());
}

fn region_index(region: Region) -> i32 {
    c::REGIONS.iter().position(|r| *r == region).unwrap_or(0) as i32
}

fn kind_index(kind: c::Kind) -> i32 {
    match kind {
        c::Kind::Install => 0,
        c::Kind::Repair => 1,
        c::Kind::Update => 2,
        c::Kind::Adopt => 3,
    }
}

fn phase_index(phase: Phase) -> i32 {
    match phase {
        Phase::FetchingIndex => 0,
        Phase::Verifying => 1,
        Phase::Downloading => 2,
        Phase::Extracting => 3,
        Phase::Deleting => 4,
        Phase::Finalizing => 5,
    }
}

fn transfer(t: &c::Transfer) -> Transfer {
    let (done, total) = t.bytes.clone().unwrap_or_default();
    let (files_done, files_total) = t.files.unwrap_or_default();
    Transfer {
        phase: phase_index(t.phase),
        percent: t.percent.map_or(-1, |p| p as i32),
        has_bytes: t.bytes.is_some(),
        done: done.into(),
        total: total.into(),
        eta: t.eta_minutes.map_or(-1, |m| m as i32),
        has_files: t.files.is_some(),
        files_done: files_done as i32,
        files_total: files_total as i32,
        current: t.current.clone().unwrap_or_default().into(),
    }
}

fn dialog_parts(dialog: &c::Dialog) -> (DialogId, String) {
    match dialog {
        c::Dialog::ConfirmSetup => (DialogId::ConfirmSetup, String::new()),
        c::Dialog::ConfirmVerify(_) => (DialogId::ConfirmVerify, String::new()),
        c::Dialog::ConfirmCancel(_) => (DialogId::ConfirmCancel, String::new()),
        c::Dialog::ConfirmCrossOver => (DialogId::ConfirmCrossover, String::new()),
        #[cfg(target_os = "macos")]
        c::Dialog::ConfirmAdopt(candidate) => (if candidate.steam { DialogId::ConfirmAdoptSteam } else { DialogId::ConfirmAdopt }, candidate.report()),
        c::Dialog::ConfirmClose { .. } => (DialogId::ConfirmClose, String::new()),
        c::Dialog::Problem(problem, details) => {
            use c::PlayBlock as B;
            let id = match problem {
                c::Problem::Busy => DialogId::Busy,
                c::Problem::GameRunning => DialogId::GameRunning,
                c::Problem::GameStillRunning => DialogId::GameStillRunning,
                c::Problem::GameCheck => DialogId::GameCheck,
                c::Problem::NoFolder => DialogId::NoFolder,
                c::Problem::InvalidFolder => DialogId::InvalidFolder,
                c::Problem::Save => DialogId::Save,
                c::Problem::Stop => DialogId::Stop,
                c::Problem::Check => DialogId::Check,
                c::Problem::Setup => DialogId::Setup,
                c::Problem::Restore => DialogId::Restore,
                c::Problem::Play => DialogId::Play,
                c::Problem::Files(_) => DialogId::FilesFailed,
                c::Problem::CancelFailed => DialogId::CancelFailed,
                c::Problem::CrossOver => DialogId::CrossoverFailed,
                c::Problem::Bottle => DialogId::BottleFailed,
                c::Problem::Blocked(block) => match block {
                    B::NoGame => DialogId::NoFolder,
                    B::NeedsBuild | B::NeedsPatch => DialogId::NeedsSetup,
                    B::Stopping | B::AlreadyRunning => DialogId::Busy,
                    B::Maintenance | B::Offline => DialogId::ServerUnavailable,
                    B::LauncherOutdated => DialogId::LauncherOutdated,
                    B::PatchOutdated => DialogId::PatchOutdated,
                    B::UnsupportedSchema => DialogId::ServerIncompatible,
                    B::UnsupportedClient => DialogId::ClientUnsupported,
                },
            };
            (id, details.clone())
        }
    }
}

/// Controller wording -> Slint `Msg` (`Tx.text` in state.slint owns the sentences).
fn msg(text: &c::Text) -> Msg {
    use c::Text as T;
    let id = |id| Msg { id, ..Default::default() };
    let with_a = |id, a: &str| Msg { id, a: a.into(), ..Default::default() };
    match text {
        T::LauncherReady => id(MsgId::LauncherReady),
        T::GameFoundSteam => id(MsgId::GameFoundSteam),
        T::HintUseFound => id(MsgId::HintUseFound),
        T::GameAdopted(name) => with_a(MsgId::GameAdopted, name),
        T::MongoReused(port) => with_a(MsgId::MongoReused, port),
        T::MongoStarted => id(MsgId::MongoStarted),
        T::ServerReused(origin) => with_a(MsgId::ServerReused, origin),
        T::ServerStarted => id(MsgId::ServerStarted),
        T::MusicUnavailable => id(MsgId::MusicUnavailable),
        T::VideoUnavailable => id(MsgId::VideoUnavailable),
        T::MusicChangeFailed => id(MsgId::MusicChangeFailed),
        T::CheckingLauncher => id(MsgId::CheckingLauncher),
        T::DownloadingLauncherUpdate => id(MsgId::DownloadingLauncherUpdate),
        T::LauncherUpdateRestarting => id(MsgId::LauncherUpdateRestarting),
        T::LauncherUpdateDeferred => id(MsgId::LauncherUpdateDeferred),
        T::LauncherCurrent(version) => with_a(MsgId::LauncherCurrent, version),
        T::LauncherCheckFailed => id(MsgId::LauncherCheckFailed),
        T::LauncherUpdateFailed => id(MsgId::LauncherUpdateFailed),
        T::SetupStarting => id(MsgId::SetupStarting),
        T::MongoStarting => id(MsgId::MongoStarting),
        T::ServerStarting => id(MsgId::ServerStarting),
        T::ServerReady => id(MsgId::ServerReady),
        T::ServerUpdateAvailable => id(MsgId::ServerUpdateAvailable),
        T::ServerCurrent => id(MsgId::ServerCurrent),
        T::ServerCheckFailed => id(MsgId::ServerCheckFailed),
        T::ServerUpdateDeferred => id(MsgId::ServerUpdateDeferred),
        T::ServerUpdateFailed => id(MsgId::ServerUpdateFailed),
        T::SetupDone => id(MsgId::SetupDone),
        T::SetupFailed => id(MsgId::SetupFailed),
        T::Restored => id(MsgId::Restored),
        T::RestoreFailed => id(MsgId::RestoreFailed),
        T::PlayFailed => id(MsgId::PlayFailed),
        T::CheckFailed => id(MsgId::CheckFailed),
        T::ModsUpdated => id(MsgId::ModsUpdated),
        T::ModsFailed => id(MsgId::ModsFailed),
        T::ElevationCancelled => id(MsgId::ElevationCancelled),
        T::PatchUnavailable => id(MsgId::PatchUnavailable),
        T::FpsMigrated => id(MsgId::FpsMigrated),
        T::FpsMigrationFailed => id(MsgId::FpsMigrationFailed),
        T::ClosingAfterStop => id(MsgId::ClosingAfterStop),
        T::ServicesStopping => id(MsgId::ServicesStopping),
        T::ServicesStopped => id(MsgId::ServicesStopped),
        T::ServicesStopFailed => id(MsgId::ServicesStopFailed),
        T::FilesStarted(kind, region) => Msg { id: MsgId::FilesStarted, n: kind_index(*kind), m: region_index(*region), ..Default::default() },
        T::FilesPhase(phase) => Msg { id: MsgId::FilesPhase, n: phase_index(*phase), ..Default::default() },
        T::FilesDone { downloaded, .. } => with_a(MsgId::FilesDone, downloaded),
        T::FilesNote(note) => with_a(MsgId::FilesNote, note),
        T::FilesPaused => id(MsgId::FilesPaused),
        T::FilesCancelled => id(MsgId::FilesCancelled),
        T::FilesFailed => id(MsgId::FilesFailed),
        T::NextSetup => id(MsgId::NextSetup),
        T::PatchFilesUpdating(size) => with_a(MsgId::PatchFilesUpdating, size),
        T::PatchFilesOutdated => id(MsgId::PatchFilesOutdated),
        T::HintSelectGame => id(MsgId::HintSelectGame),
        T::HintSetup => id(MsgId::HintSetup),
        T::HintUpdate => id(MsgId::HintUpdate),
        T::HintPlay => id(MsgId::HintPlay),
        T::HintRunning => id(MsgId::HintRunning),
        T::HintStopping => id(MsgId::HintStopping),
        T::HintPatchUnsupported => id(MsgId::HintPatchUnsupported),
        T::HintPatchRepair => id(MsgId::HintPatchRepair),
        T::HintWorkingInspect => id(MsgId::WorkingInspect),
        T::HintWorkingSetup => id(MsgId::WorkingSetup),
        T::HintWorkingRestore => id(MsgId::WorkingRestore),
        T::HintWorkingPlay => id(MsgId::WorkingPlay),
        T::FilesRunning(kind) => Msg { id: MsgId::FilesRunning, n: kind_index(*kind), ..Default::default() },
        T::ModsNone => id(MsgId::ModsNone),
        T::ModsUnavailable(error) => with_a(MsgId::ModsUnavailable, error),
        T::ModsHint => id(MsgId::ModsHint),
        T::ModsApplying => id(MsgId::ModsApplying),
        T::ModsApplied { applied, skipped } => Msg { id: MsgId::ModsApplied, a: applied.as_str().into(), b: skipped.as_str().into(), ..Default::default() },
        T::ModsDisabled => id(MsgId::ModsDisabled),
        T::ModsChangeFailed(error) => with_a(MsgId::ModsChangeFailed, error),
        T::FilesChoose => id(MsgId::FilesChoose),
        T::FilesNotEmpty => id(MsgId::FilesNotEmpty),
        T::FilesUnreadable(error) => with_a(MsgId::FilesUnreadable, error),
        T::FilesEmptyFolder(region) => Msg { id: MsgId::FilesEmptyFolder, m: region_index(*region), ..Default::default() },
        T::FilesResumable => id(MsgId::FilesResumable),
        T::FilesInspecting => id(MsgId::FilesInspecting),
        T::FilesGame { region, version, steam, patched, assumed } => Msg {
            id: MsgId::FilesGame,
            a: version.clone().unwrap_or_default().into(),
            n: *steam as i32 + 2 * *patched as i32,
            m: region.map_or(-1, region_index),
            k: region_index(*assumed),
            ..Default::default()
        },
        T::FilesPlan { download, files, needs, free, removed } => Msg {
            id: MsgId::FilesPlan,
            a: download.as_str().into(),
            b: needs.as_str().into(),
            c: free.as_str().into(),
            n: *files as i32,
            m: *removed as i32,
            ..Default::default()
        },
        T::FilesPlanError(error) => with_a(MsgId::FilesPlanError, error),
        T::FilesInspectFailed(error) => with_a(MsgId::FilesInspectFailed, error),
        T::FilesStopping => id(MsgId::FilesStopping),
        T::CrossOverPhase(phase) => Msg { id: MsgId::CrossoverPhase, n: *phase as i32, ..Default::default() },
        T::CrossOverInstalled => id(MsgId::CrossoverInstalled),
        T::CrossOverFailed => id(MsgId::CrossoverFailed),
        T::BottleCreating => id(MsgId::BottleCreating),
        T::BottleReady => id(MsgId::BottleReady),
        T::BottleFailed => id(MsgId::BottleFailed),
        T::WorkingBottle => id(MsgId::WorkingBottle),
        T::HintInstallCrossOver => id(MsgId::HintInstallCrossover),
        T::HintCreateBottle => id(MsgId::HintCreateBottle),
        T::HintRepairBottle => id(MsgId::HintRepairBottle),
    }
}

// ----- Win32 odds and ends --------------------------------------------------------------------

/// One launcher window per user session.
#[cfg(windows)]
fn single_instance() -> Result<impl Drop> {
    use windows::{
        core::w,
        Win32::{
            Foundation::{CloseHandle, GetLastError, ERROR_ALREADY_EXISTS, HANDLE},
            System::Threading::CreateMutexW,
        },
    };
    struct Mutex(HANDLE);
    impl Drop for Mutex {
        fn drop(&mut self) {
            unsafe {
                let _ = CloseHandle(self.0);
            }
        }
    }
    unsafe {
        let mutex = Mutex(CreateMutexW(None, true, w!("Local\\AscNetLauncherUI"))?);
        if GetLastError() == ERROR_ALREADY_EXISTS {
            anyhow::bail!("AscNet Launcher is already open")
        }
        Ok(mutex)
    }
}
#[cfg(not(windows))]
fn single_instance() -> Result<impl Drop> {
    struct Nothing;
    impl Drop for Nothing {
        fn drop(&mut self) {}
    }
    Ok(Nothing)
}

/// Last-resort error box for failures before (or without) the Slint window.
#[cfg_attr(not(windows), allow(dead_code))]
pub fn show_fatal(message: &str) {
    let message = local::logged_error(message);
    #[cfg(windows)]
    unsafe {
        use windows::{
            core::{w, PCWSTR},
            Win32::UI::WindowsAndMessaging::{MessageBoxW, MB_ICONERROR, MB_OK},
        };
        let text: Vec<u16> = message.encode_utf16().chain(Some(0)).collect();
        MessageBoxW(None, PCWSTR(text.as_ptr()), w!("AscNet Launcher"), MB_OK | MB_ICONERROR);
    }
    #[cfg(not(windows))]
    eprintln!("{message}");
}

#[cfg(test)]
mod tests {
    /// A `MsgId` with no branch in `Tx.text` renders an empty line; the compiler can't see that.
    #[test]
    fn every_message_id_has_wording() {
        let source = include_str!("../ui/state.slint");
        let enum_body = source.split("export enum MsgId {").nth(1).unwrap().split('}').next().unwrap();
        let ids: Vec<&str> = enum_body.split(|c: char| c == ',' || c.is_whitespace()).filter(|id| !id.is_empty() && *id != "none").collect();
        assert!(ids.len() > 50);
        let text_fn = source.split("pure public function text(").nth(1).unwrap().split("pure public function dialog-title").next().unwrap();
        for id in ids {
            assert!(text_fn.contains(&format!("MsgId.{id})")), "Tx.text has no branch for MsgId.{id}");
        }
    }
}

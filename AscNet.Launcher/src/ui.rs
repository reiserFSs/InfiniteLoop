use anyhow::{Context, Result};
use ascnet_launcher::{
    install::{self, PatchState},
    local::{self, LocalBuild, LocalRuntime},
    package::{self, PatchPackage},
};
use serde::{Deserialize, Serialize};
use std::{
    env, fs,
    io::Read,
    path::{Path, PathBuf},
    sync::{
        atomic::{AtomicU64, Ordering},
        mpsc::{self, Receiver, Sender},
        Arc, Mutex,
    },
    thread,
    time::Duration,
};
use windows::{
    core::{w, HRESULT, PCWSTR},
    Win32::{
        Foundation::*,
        Graphics::Gdi::*,
        System::{Com::*, LibraryLoader::GetModuleHandleW, Threading::CreateMutexW},
        UI::{
            Controls::*, Input::KeyboardAndMouse::EnableWindow, Shell::*, WindowsAndMessaging::*,
        },
    },
};

const WM_EVENT: u32 = WM_APP + 1;
const ID_BROWSE: i32 = 101;
const ID_CHECK: i32 = 102;
const ID_ACTION: i32 = 103;
const ID_RESTORE: i32 = 104;
const ID_PLAY: i32 = 105;
const ID_PATH: i32 = 110;
const ID_STATUS: i32 = 111;
const ID_DETAIL: i32 = 112;
const ID_PROGRESS: i32 = 113;
const ID_TITLE: i32 = 115;
const ID_SUBTITLE: i32 = 116;
const ID_PATH_LABEL: i32 = 118;
const LAUNCHER_VERSION: &str = env!("CARGO_PKG_VERSION");

#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct DistributorConfig {
    repository_url: String,
    branch: String,
}

#[derive(Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Settings {
    selected_game: Option<PathBuf>,
}

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
#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct SupportedClient {
    application_version: String,
    document_version: String,
    launch_module_version: String,
}

struct Model {
    config: DistributorConfig,
    settings: Settings,
    build: Option<LocalBuild>,
    package: Option<PatchPackage>,
    runtime: Option<LocalRuntime>,
    patch: Option<PatchState>,
    server: Option<ServerStatus>,
    update_available: Option<bool>,
    update_error: Option<String>,
    busy: bool,
    generation: Arc<AtomicU64>,
    events: Sender<Event>,
}
struct Window {
    model: Arc<Mutex<Model>>,
    events: Receiver<Event>,
    background: HBITMAP,
    panel_brush: HBRUSH,
}
struct UiMutex(HANDLE);
impl Drop for UiMutex {
    fn drop(&mut self) {
        unsafe {
            let _ = CloseHandle(self.0);
        }
    }
}
struct Work {
    generation: u64,
    result: Result<WorkResult>,
}
enum Event {
    Work(Work),
    Progress(String),
}
enum WorkResult {
    Refresh {
        build: Option<LocalBuild>,
        package: Option<PatchPackage>,
        patch: Option<PatchState>,
        update: Result<Option<bool>>,
    },
    Prepared {
        build: LocalBuild,
        package: PatchPackage,
        patch: PatchState,
    },
    Restored,
    Launched {
        build: LocalBuild,
        package: PatchPackage,
        runtime: LocalRuntime,
        server: ServerStatus,
    },
}

pub fn run() -> Result<()> {
    unsafe {
        CoInitializeEx(None, COINIT_APARTMENTTHREADED).ok()?;
        let result = run_inner();
        CoUninitialize();
        result
    }
}

unsafe fn run_inner() -> Result<()> {
    let _singleton = UiMutex(CreateMutexW(None, true, w!("Local\\AscNetLauncherUI"))?);
    if GetLastError() == ERROR_ALREADY_EXISTS {
        anyhow::bail!("AscNet Launcher is already open")
    }
    let exe_dir = env::current_exe()?
        .parent()
        .context("launcher executable has no parent")?
        .to_path_buf();
    let config: DistributorConfig = serde_json::from_slice(
        &fs::read(exe_dir.join("launcher.json")).context("launcher.json is missing")?,
    )
    .context("launcher.json is invalid")?;
    let mut settings = load_settings().unwrap_or_default();
    if settings
        .selected_game
        .as_deref()
        .is_some_and(|p| !crate::steam::valid_game_directory(p))
    {
        settings.selected_game = None;
    }
    if settings.selected_game.is_none() {
        settings.selected_game = crate::steam::discover_game()?;
    }
    save_settings(&settings)?;

    let instance = GetModuleHandleW(None)?;
    let class = w!("AscNetNativeLauncher");
    let cursor = LoadCursorW(None, IDC_ARROW)?;
    let wc = WNDCLASSW {
        hCursor: cursor,
        hInstance: instance.into(),
        lpszClassName: class,
        lpfnWndProc: Some(wndproc),
        hbrBackground: HBRUSH((COLOR_WINDOW.0 + 1) as isize),
        style: CS_HREDRAW | CS_VREDRAW,
        ..Default::default()
    };
    if RegisterClassW(&wc) == 0 {
        anyhow::bail!(
            "RegisterClassW failed: {}",
            windows::core::Error::from_win32()
        )
    }
    let (event_tx, event_rx) = mpsc::channel();
    let model = Arc::new(Mutex::new(Model {
        config,
        settings,
        build: None,
        package: None,
        runtime: None,
        patch: None,
        server: None,
        update_available: None,
        update_error: None,
        busy: false,
        generation: Arc::new(AtomicU64::new(0)),
        events: event_tx,
    }));
    let background = load_bitmap(&exe_dir.join("background.bmp"))?;
    let panel_brush = CreateSolidBrush(COLORREF(0x00201a18));
    if panel_brush.0 == 0 {
        let _ = DeleteObject(background);
        anyhow::bail!(
            "CreateSolidBrush failed: {}",
            windows::core::Error::from_win32()
        )
    }
    let state = Box::new(Window {
        model,
        events: event_rx,
        background,
        panel_brush,
    });
    let state_ptr = Box::into_raw(state);
    let hwnd = CreateWindowExW(
        WS_EX_CONTROLPARENT,
        class,
        w!("AscNet Launcher"),
        WS_OVERLAPPEDWINDOW | WS_VISIBLE,
        CW_USEDEFAULT,
        CW_USEDEFAULT,
        1000,
        640,
        HWND(0),
        HMENU(0),
        instance,
        Some(state_ptr.cast()),
    );
    if hwnd.0 == 0 {
        let state = Box::from_raw(state_ptr);
        let _ = DeleteObject(state.background);
        let _ = DeleteObject(state.panel_brush);
        anyhow::bail!(
            "CreateWindowExW failed: {}",
            windows::core::Error::from_win32()
        )
    }
    let _ = ShowWindow(hwnd, SW_SHOW);
    let _ = SetForegroundWindow(hwnd);
    start_refresh(hwnd, false);
    let mut msg = MSG::default();
    loop {
        let result = GetMessageW(&mut msg, None, 0, 0).0;
        if result == -1 {
            anyhow::bail!("GetMessageW failed: {}", windows::core::Error::from_win32())
        }
        if result == 0 {
            break;
        }
        if !IsDialogMessageW(hwnd, &msg).as_bool() {
            let _ = TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }
    Ok(())
}

unsafe extern "system" fn wndproc(hwnd: HWND, msg: u32, wp: WPARAM, lp: LPARAM) -> LRESULT {
    if msg == WM_NCCREATE {
        let create = &*(lp.0 as *const CREATESTRUCTW);
        SetWindowLongPtrW(hwnd, GWLP_USERDATA, create.lpCreateParams as isize);
    }
    let ptr = GetWindowLongPtrW(hwnd, GWLP_USERDATA) as *mut Window;
    match msg {
        WM_CREATE => {
            create_controls(hwnd);
            if !ptr.is_null() {
                let path = {
                    let m = (*ptr).model.lock().unwrap();
                    m.settings
                        .selected_game
                        .as_deref()
                        .map(|p| p.display().to_string())
                        .unwrap_or_default()
                };
                set_text(hwnd, ID_PATH, &path);
            }
            LRESULT(0)
        }
        WM_SIZE => {
            layout(
                hwnd,
                (lp.0 as u32 & 0xffff) as i32,
                ((lp.0 as u32 >> 16) & 0xffff) as i32,
            );
            LRESULT(0)
        }
        WM_GETMINMAXINFO => {
            let info = &mut *(lp.0 as *mut MINMAXINFO);
            info.ptMinTrackSize = POINT { x: 760, y: 560 };
            LRESULT(0)
        }
        WM_PAINT => {
            if !ptr.is_null() {
                paint(hwnd, &*ptr);
            }
            LRESULT(0)
        }
        WM_CTLCOLORSTATIC => {
            if ptr.is_null() {
                return DefWindowProcW(hwnd, msg, wp, lp);
            }
            let dc = HDC(wp.0 as isize);
            let _ = SetTextColor(dc, COLORREF(0x00ffffff));
            let _ = SetBkMode(dc, TRANSPARENT);
            LRESULT((*ptr).panel_brush.0)
        }
        WM_COMMAND => {
            if !ptr.is_null() {
                command(
                    hwnd,
                    &mut *ptr,
                    (wp.0 & 0xffff) as i32,
                    ((wp.0 >> 16) & 0xffff) as u16,
                );
            }
            LRESULT(0)
        }
        WM_EVENT => {
            if !ptr.is_null() {
                while let Ok(event) = (*ptr).events.try_recv() {
                    match event {
                        Event::Work(work) => finish_work(hwnd, &mut *ptr, work),
                        Event::Progress(text) => set_text(hwnd, ID_DETAIL, &text),
                    }
                }
            }
            LRESULT(0)
        }
        WM_CLOSE => {
            if !ptr.is_null() {
                let (busy, has_runtime) = {
                    let m = (*ptr).model.lock().unwrap();
                    (m.busy, m.runtime.is_some())
                };
                if busy {
                    show_fatal("Wait for the current operation to finish before closing.");
                    return LRESULT(0);
                }
                if has_runtime {
                    match install::game_running() {
                        Ok(true) => {
                            show_fatal("PGR is still running. Close the game before closing the launcher so its local server remains available.");
                            return LRESULT(0);
                        }
                        Err(e) => {
                            show_fatal(&format!(
                                "Cannot safely check whether PGR is running: {e:#}"
                            ));
                            return LRESULT(0);
                        }
                        Ok(false) => {}
                    }
                }
                if has_runtime
                    && MessageBoxW(
                        hwnd,
                        w!("Closing will stop the local AscNet server and MongoDB started by this launcher. Continue?"),
                        w!("Stop local services?"),
                        MB_OKCANCEL | MB_ICONWARNING,
                    ) != IDOK
                {
                    return LRESULT(0);
                }
                let mut owned_runtime = (*ptr).model.lock().unwrap().runtime.take();
                let stop_error = owned_runtime
                    .as_mut()
                    .and_then(|runtime| runtime.stop().err());
                if let Some(e) = stop_error {
                    (*ptr).model.lock().unwrap().runtime = owned_runtime;
                    show_fatal(&format!("Could not stop local services safely: {e:#}"));
                    return LRESULT(0);
                }
            }
            let _ = DestroyWindow(hwnd);
            LRESULT(0)
        }
        WM_DESTROY => {
            SetWindowLongPtrW(hwnd, GWLP_USERDATA, 0);
            if !ptr.is_null() {
                let state = Box::from_raw(ptr);
                let _ = DeleteObject(state.background);
                let _ = DeleteObject(state.panel_brush);
            }
            PostQuitMessage(0);
            LRESULT(0)
        }
        _ => DefWindowProcW(hwnd, msg, wp, lp),
    }
}

unsafe fn create_controls(hwnd: HWND) {
    let font = GetStockObject(DEFAULT_GUI_FONT);
    control(
        hwnd,
        w!("STATIC"),
        w!("ASCNET"),
        WS_VISIBLE,
        ID_TITLE,
        0,
        0,
        0,
        0,
    );
    control(
        hwnd,
        w!("STATIC"),
        w!("Native launcher for Punishing: Gray Raven"),
        WS_VISIBLE,
        ID_SUBTITLE,
        0,
        0,
        0,
        0,
    );
    control(
        hwnd,
        w!("STATIC"),
        w!("&Game folder"),
        WS_VISIBLE,
        ID_PATH_LABEL,
        0,
        0,
        0,
        0,
    );
    control(
        hwnd,
        w!("EDIT"),
        PCWSTR::null(),
        WS_VISIBLE | WS_TABSTOP | WINDOW_STYLE(ES_AUTOHSCROLL as u32) | WS_BORDER,
        ID_PATH,
        0,
        0,
        0,
        0,
    );
    control(
        hwnd,
        w!("BUTTON"),
        w!("&Browse…"),
        WS_VISIBLE | WS_TABSTOP,
        ID_BROWSE,
        0,
        0,
        0,
        0,
    );
    control(
        hwnd,
        w!("STATIC"),
        w!("Starting…"),
        WS_VISIBLE,
        ID_STATUS,
        0,
        0,
        0,
        0,
    );
    control(
        hwnd,
        w!("STATIC"),
        PCWSTR::null(),
        WS_VISIBLE,
        ID_DETAIL,
        0,
        0,
        0,
        0,
    );
    control(
        hwnd,
        PROGRESS_CLASSW,
        PCWSTR::null(),
        WS_VISIBLE,
        ID_PROGRESS,
        0,
        0,
        0,
        0,
    );
    control(
        hwnd,
        w!("BUTTON"),
        w!("&Check"),
        WS_VISIBLE | WS_TABSTOP,
        ID_CHECK,
        0,
        0,
        0,
        0,
    );
    control(
        hwnd,
        w!("BUTTON"),
        w!("&Setup / Update"),
        WS_VISIBLE | WS_TABSTOP,
        ID_ACTION,
        0,
        0,
        0,
        0,
    );
    control(
        hwnd,
        w!("BUTTON"),
        w!("&Restore"),
        WS_VISIBLE | WS_TABSTOP,
        ID_RESTORE,
        0,
        0,
        0,
        0,
    );
    control(
        hwnd,
        w!("BUTTON"),
        w!("&Play"),
        WS_VISIBLE | WS_TABSTOP | WINDOW_STYLE(BS_DEFPUSHBUTTON as u32),
        ID_PLAY,
        0,
        0,
        0,
        0,
    );
    for id in [
        ID_TITLE,
        ID_SUBTITLE,
        ID_PATH_LABEL,
        ID_PATH,
        ID_BROWSE,
        ID_STATUS,
        ID_DETAIL,
        ID_PROGRESS,
        ID_CHECK,
        ID_ACTION,
        ID_RESTORE,
        ID_PLAY,
    ] {
        let child = GetDlgItem(hwnd, id);
        if child.0 != 0 {
            let _ = SendMessageW(child, WM_SETFONT, WPARAM(font.0 as usize), LPARAM(1));
        }
    }
}

unsafe fn control(
    hwnd: HWND,
    class: PCWSTR,
    text: PCWSTR,
    style: WINDOW_STYLE,
    id: i32,
    x: i32,
    y: i32,
    w: i32,
    h: i32,
) -> HWND {
    CreateWindowExW(
        WINDOW_EX_STYLE::default(),
        class,
        text,
        WS_CHILD | style,
        x,
        y,
        w,
        h,
        hwnd,
        HMENU(id as isize),
        None,
        None,
    )
}
unsafe fn layout(hwnd: HWND, width: i32, height: i32) {
    let panel = width.clamp(400, 440);
    let x = 28;
    let content = panel - 56;
    let bottom = height - 62;
    for (id, y, w, h) in [
        (ID_TITLE, 30, content, 30),
        (ID_SUBTITLE, 64, content, 22),
        (ID_PATH_LABEL, 115, content, 20),
        (ID_PATH, 137, content - 88, 28),
        (ID_BROWSE, 137, 82, 28),
        (ID_STATUS, 190, content, 52),
        (ID_DETAIL, 250, content, 148),
        (ID_PROGRESS, 407, content, 16),
    ] {
        let cx = if id == ID_BROWSE { x + content - 82 } else { x };
        let _ = MoveWindow(GetDlgItem(hwnd, id), cx, y, w, h, true);
    }
    let bw = (content - 24) / 4;
    for (n, id) in [ID_CHECK, ID_ACTION, ID_RESTORE, ID_PLAY]
        .iter()
        .enumerate()
    {
        let _ = MoveWindow(
            GetDlgItem(hwnd, *id),
            x + n as i32 * (bw + 8),
            bottom,
            bw,
            34,
            true,
        );
    }
}

unsafe fn command(hwnd: HWND, state: &mut Window, id: i32, notification: u16) {
    if notification == EN_CHANGE as u16 && id == ID_PATH {
        {
            let mut m = state.model.lock().unwrap();
            if m.busy {
                return;
            }
            m.patch = None;
        }
        update_view(hwnd, &state.model);
        return;
    }
    match id {
        ID_BROWSE => match choose_folder(hwnd) {
            Ok(Some(path)) => {
                let text = path.display().to_string();
                {
                    let mut m = state.model.lock().unwrap();
                    m.settings.selected_game = Some(path);
                    if let Err(e) = save_settings(&m.settings) {
                        show_fatal(&format!("{e:#}"));
                        return;
                    }
                }
                set_text(hwnd, ID_PATH, &text);
                start_refresh(hwnd, false);
            }
            Ok(None) => {}
            Err(e) => show_fatal(&format!("{e:#}")),
        },
        ID_CHECK => {
            if commit_inputs(hwnd, state) {
                start_refresh(hwnd, true)
            }
        }
        ID_ACTION => {
            if commit_inputs(hwnd, state)
                && MessageBoxW(
                    hwnd,
                    w!("Setup runs source code from the configured repository and installs missing Git, .NET, MongoDB, Rust, and C++ build tools. Windows may request administrator approval.\r\n\r\nContinue?"),
                    w!("AscNet local setup"),
                    MB_OKCANCEL | MB_ICONWARNING,
                ) == IDOK
            {
                start_prepare(hwnd, state)
            }
        }
        ID_RESTORE => start_restore(hwnd, state),
        ID_PLAY => {
            if commit_inputs(hwnd, state) {
                start_play(hwnd, state)
            }
        }
        _ => {}
    }
}

unsafe fn commit_inputs(hwnd: HWND, state: &mut Window) -> bool {
    let game = PathBuf::from(match get_text(hwnd, ID_PATH) {
        Ok(s) => s,
        Err(e) => {
            show_fatal(&format!("{e:#}"));
            return false;
        }
    });
    if !crate::steam::valid_game_directory(&game) {
        show_fatal("Selected game folder does not contain PGR.exe");
        return false;
    }
    let mut m = state.model.lock().unwrap();
    if m.settings.selected_game.as_ref() != Some(&game) {
        m.settings.selected_game = Some(game);
        m.patch = None;
    }
    if let Err(e) = save_settings(&m.settings) {
        show_fatal(&format!("{e:#}"));
        return false;
    }
    true
}

fn start_refresh(hwnd: HWND, check_remote: bool) {
    let Some(model) = window_model(hwnd) else {
        return;
    };
    let (generation, events, config, game);
    {
        let mut m = model.lock().unwrap();
        if m.busy {
            return;
        }
        m.busy = true;
        generation = m.generation.fetch_add(1, Ordering::SeqCst) + 1;
        events = m.events.clone();
        config = m.config.clone();
        game = m.settings.selected_game.clone();
    }
    unsafe { set_busy(hwnd, true, "Inspecting local build and patch…") };
    thread::spawn(move || {
        let result = (|| {
            let build = local::load_build()?;
            let package = build
                .as_ref()
                .map(|b| package::load_package(&b.patch_directory))
                .transpose()?;
            let patch = match (&game, &package) {
                (Some(game), Some(package)) if crate::steam::valid_game_directory(game) => {
                    Some(install::inspect(game, package)?)
                }
                _ => None,
            };
            let update = if check_remote {
                local::check_update(&config.repository_url, &config.branch)
            } else {
                Ok(None)
            };
            Ok(WorkResult::Refresh {
                build,
                package,
                patch,
                update,
            })
        })();
        post_event(hwnd, &events, Event::Work(Work { generation, result }));
    });
}

fn start_prepare(hwnd: HWND, state: &mut Window) {
    match install::game_running() {
        Ok(true) => {
            show_fatal("Close PGR before setup or patch changes.");
            return;
        }
        Err(e) => {
            show_fatal(&format!("{e:#}"));
            return;
        }
        Ok(false) => {}
    }
    let model = state.model.clone();
    let (generation, events, config, game);
    {
        let mut m = model.lock().unwrap();
        if m.busy {
            return;
        }
        game = match m.settings.selected_game.clone() {
            Some(game) => game,
            None => {
                drop(m);
                show_fatal("Select a game folder");
                return;
            }
        };
        config = m.config.clone();
        m.busy = true;
        generation = m.generation.fetch_add(1, Ordering::SeqCst) + 1;
        events = m.events.clone();
    }
    unsafe {
        set_busy(
            hwnd,
            true,
            "Updating source and building local server and patch…",
        )
    };
    thread::spawn(move || {
        let result = (|| {
            let mut progress = |s: &str| post_progress(hwnd, &events, s);
            let build = local::prepare(&config.repository_url, &config.branch, &mut progress)?;
            let package = package::load_package(&build.patch_directory)?;
            install::install(&game, &package, &mut |s| post_progress(hwnd, &events, &s))?;
            let patch = install::inspect(&game, &package)?;
            Ok(WorkResult::Prepared {
                build,
                package,
                patch,
            })
        })();
        post_event(hwnd, &events, Event::Work(Work { generation, result }));
    });
}

fn start_restore(hwnd: HWND, state: &mut Window) {
    match install::game_running() {
        Ok(true) => {
            show_fatal("Close PGR before restoring retail files.");
            return;
        }
        Err(e) => {
            show_fatal(&format!("{e:#}"));
            return;
        }
        Ok(false) => {}
    }
    let model = state.model.clone();
    let (generation, events);
    {
        let mut m = model.lock().unwrap();
        if m.busy {
            return;
        }
        m.busy = true;
        generation = m.generation.fetch_add(1, Ordering::SeqCst) + 1;
        events = m.events.clone();
    }
    unsafe { set_busy(hwnd, true, "Restoring retail files…") };
    thread::spawn(move || {
        let result = (|| {
            let game = model
                .lock()
                .unwrap()
                .settings
                .selected_game
                .clone()
                .context("Select a game folder")?;
            install::restore(&game, &mut |s| post_progress(hwnd, &events, &s))?;
            Ok(WorkResult::Restored)
        })();
        post_event(hwnd, &events, Event::Work(Work { generation, result }));
    });
}

fn start_play(hwnd: HWND, state: &mut Window) {
    let model = state.model.clone();
    let prepared = {
        let mut m = model.lock().unwrap();
        if m.busy {
            return;
        }
        match can_play(&m) {
            Err(e) => Err(e),
            Ok(()) => {
                m.busy = true;
                Ok((
                    m.generation.fetch_add(1, Ordering::SeqCst) + 1,
                    m.events.clone(),
                    m.settings.selected_game.clone().unwrap(),
                    m.build.clone().unwrap(),
                    m.package.clone().unwrap(),
                ))
            }
        }
    };
    let (generation, events, game, build, package) = match prepared {
        Ok(values) => values,
        Err(e) => {
            show_fatal(&e.to_string());
            return;
        }
    };
    unsafe { set_busy(hwnd, true, "Starting MongoDB and local server…") };
    thread::spawn(move || {
        let result = (|| {
            let patch = install::inspect(&game, &package)?;
            if !matches!(patch, PatchState::Current) {
                anyhow::bail!("Run Setup / Update to install the current local patch")
            }
            let mut runtime =
                LocalRuntime::start(&build, &mut |s| post_progress(hwnd, &events, s))?;
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
            Ok(WorkResult::Launched {
                build,
                package,
                runtime,
                server,
            })
        })();
        post_event(hwnd, &events, Event::Work(Work { generation, result }));
    });
}

unsafe fn finish_work(hwnd: HWND, state: &mut Window, work: Work) {
    let mut m = state.model.lock().unwrap();
    if work.generation != m.generation.load(Ordering::SeqCst) {
        return;
    }
    m.busy = false;
    match work.result {
        Ok(WorkResult::Refresh {
            build,
            package,
            patch,
            update,
        }) => {
            m.build = build;
            m.package = package;
            m.patch = patch;
            match update {
                Ok(value) => {
                    m.update_available = value;
                    m.update_error = None
                }
                Err(e) => {
                    m.update_available = None;
                    m.update_error = Some(format!("{e:#}"))
                }
            }
        }
        Ok(WorkResult::Prepared {
            build,
            package,
            patch,
        }) => {
            m.build = Some(build);
            m.package = Some(package);
            m.patch = Some(patch);
            m.update_available = Some(false);
            m.update_error = None;
        }
        Ok(WorkResult::Restored) => m.patch = Some(PatchState::Unpatched),
        Ok(WorkResult::Launched {
            build,
            package,
            runtime,
            server,
        }) => {
            m.build = Some(build);
            m.package = Some(package);
            m.runtime = Some(runtime);
            m.server = Some(server);
        }
        Err(e) => {
            drop(m);
            update_view(hwnd, &state.model);
            set_text(hwnd, ID_DETAIL, &format!("Error: {e:#}"));
            show_fatal(&format!("{e:#}"));
            return;
        }
    }
    drop(m);
    update_view(hwnd, &state.model);
}

unsafe fn update_view(hwnd: HWND, model: &Arc<Mutex<Model>>) {
    let (
        patch_state,
        build,
        update_available,
        update_error,
        server,
        application_version,
        runtime,
        busy,
        can_restore,
        can_launch,
    ) = {
        let m = model.lock().unwrap();
        (
            m.patch.clone(),
            m.build.clone(),
            m.update_available,
            m.update_error.clone(),
            m.server.clone(),
            m.package
                .as_ref()
                .map(|p| p.manifest.application_version.clone()),
            m.runtime.is_some(),
            m.busy,
            m.settings.selected_game.is_some(),
            can_play(&m).is_ok(),
        )
    };
    let patch = match &patch_state {
        Some(PatchState::Unpatched) => "Not installed",
        Some(PatchState::Current) => "Current",
        Some(PatchState::UpdateAvailable) => "Update available",
        Some(PatchState::AdoptionRequired) => "Existing patch needs adoption",
        Some(PatchState::Unsupported(_)) => "Unsupported game build",
        Some(PatchState::RepairRequired(_)) => "Repair required",
        None => "Not checked",
    };
    let source = match (&build, update_available) {
        (None, _) => "Not built",
        (Some(_), Some(true)) => "Update available",
        (Some(_), Some(false)) => "Current",
        (Some(_), None) => "Built locally",
    };
    set_text(
        hwnd,
        ID_STATUS,
        &format!(
            "Source: {source}\r\nPatch: {patch} · Local server: {}",
            if runtime { "Running" } else { "Stopped" }
        ),
    );
    let mut detail = match &patch_state {
        Some(PatchState::Unsupported(x)) | Some(PatchState::RepairRequired(x)) => x.clone(),
        Some(PatchState::AdoptionRequired) => {
            "Existing patch can be adopted safely by Setup / Update.".into()
        }
        _ => String::new(),
    };
    if let Some(error) = &update_error {
        if !detail.is_empty() {
            detail.push_str("\r\n")
        }
        detail.push_str(&format!("Git update check failed: {error}"));
    } else if let Some(build) = &build {
        if !detail.is_empty() {
            detail.push_str("\r\n")
        }
        detail.push_str(&format!("Local revision {}", build.revision));
    }
    if let Some(server) = &server {
        if !detail.is_empty() {
            detail.push_str("\r\n")
        }
        detail.push_str(&server.message);
        if let Some(client) = application_version.as_deref().and_then(|version| {
            server
                .supported_clients
                .iter()
                .find(|client| client.application_version == version)
        }) {
            detail.push_str(&format!(
                "\r\nServer {} · client {} · document {} · launch {}",
                server.server_version,
                client.application_version,
                client.document_version,
                client.launch_module_version,
            ));
        }
    }
    set_text(hwnd, ID_DETAIL, &detail);
    set_text(
        hwnd,
        ID_ACTION,
        if update_available == Some(true) {
            "&Update"
        } else {
            "&Setup / Update"
        },
    );
    set_enabled(hwnd, ID_ACTION, !busy);
    set_enabled(hwnd, ID_RESTORE, !busy && can_restore);
    set_enabled(hwnd, ID_PLAY, !busy && can_launch);
    set_busy(hwnd, false, "");
}

fn can_play(m: &Model) -> Result<()> {
    m.settings
        .selected_game
        .as_ref()
        .context("Select a game folder")?;
    m.build
        .as_ref()
        .context("Run Setup / Update to build the local server")?;
    m.package
        .as_ref()
        .context("Run Setup / Update to build the local patch")?;
    if !matches!(m.patch, Some(PatchState::Current)) {
        anyhow::bail!("Run Setup / Update to install the current local patch")
    }
    if m.runtime.is_some() {
        anyhow::bail!("The local server is already running")
    }
    Ok(())
}

fn can_play_exact(package: &PatchPackage, patch: &PatchState, server: &ServerStatus) -> Result<()> {
    if !matches!(patch, PatchState::Current) {
        anyhow::bail!("Local patch is not current")
    }
    if server.schema_version != 1 {
        anyhow::bail!("Unsupported server status schema")
    }
    if server.maintenance {
        anyhow::bail!("Server is under maintenance")
    }
    if !server.online {
        anyhow::bail!("Server is offline")
    }
    if let Some(v) = &server.minimum_launcher_version {
        if package::compare_versions(LAUNCHER_VERSION, v)? == std::cmp::Ordering::Less {
            anyhow::bail!("Launcher update required")
        }
    }
    if let Some(v) = &server.minimum_patch_version {
        if package::compare_versions(&package.manifest.version, v)? == std::cmp::Ordering::Less {
            anyhow::bail!("Patch update required")
        }
    }
    if !server
        .supported_clients
        .iter()
        .any(|c| c.application_version == package.manifest.application_version)
    {
        anyhow::bail!("This game application version is not supported by the local server")
    }
    Ok(())
}

fn local_origin(build: &LocalBuild) -> Result<String> {
    package::validate_server_origin(&format!("http://127.0.0.1:{}", build.sdk_port))
}
fn post_event(hwnd: HWND, events: &Sender<Event>, event: Event) {
    if events.send(event).is_ok() {
        unsafe {
            let _ = PostMessageW(hwnd, WM_EVENT, WPARAM(0), LPARAM(0));
        }
    }
}
fn post_progress(hwnd: HWND, events: &Sender<Event>, text: &str) {
    post_event(hwnd, events, Event::Progress(text.to_owned()));
}
fn fetch_status(origin: &str) -> Result<ServerStatus> {
    let origin = package::validate_server_origin(origin)?;
    let mut response = reqwest::blocking::Client::builder()
        .no_proxy()
        .timeout(Duration::from_secs(15))
        .redirect(reqwest::redirect::Policy::none())
        .build()?
        .get(format!("{origin}/api/launcher/status"))
        .send()?
        .error_for_status()?;
    if response.content_length().is_some_and(|n| n > 65_536) {
        anyhow::bail!("server status exceeds 64 KiB")
    }
    let mut bytes = Vec::new();
    response.by_ref().take(65_537).read_to_end(&mut bytes)?;
    if bytes.len() > 65_536 {
        anyhow::bail!("server status exceeds 64 KiB")
    }
    let s: ServerStatus = serde_json::from_slice(&bytes)?;
    if s.schema_version != 1 {
        anyhow::bail!("unsupported server status schema {}", s.schema_version)
    }
    Ok(s)
}
fn settings_dir() -> Result<PathBuf> {
    Ok(
        PathBuf::from(env::var_os("LOCALAPPDATA").context("LOCALAPPDATA is unavailable")?)
            .join("AscNet/Launcher"),
    )
}
fn load_settings() -> Result<Settings> {
    Ok(serde_json::from_slice(&fs::read(
        settings_dir()?.join("settings.json"),
    )?)?)
}
fn save_settings(s: &Settings) -> Result<()> {
    let dir = settings_dir()?;
    fs::create_dir_all(&dir)?;
    let temp = dir.join("settings.tmp");
    fs::write(&temp, serde_json::to_vec_pretty(s)?)?;
    fs::rename(temp, dir.join("settings.json"))?;
    Ok(())
}

fn window_model(hwnd: HWND) -> Option<Arc<Mutex<Model>>> {
    unsafe {
        let p = GetWindowLongPtrW(hwnd, GWLP_USERDATA) as *mut Window;
        if p.is_null() {
            None
        } else {
            Some((*p).model.clone())
        }
    }
}
unsafe fn set_text(hwnd: HWND, id: i32, text: &str) {
    let wide = wide(text);
    let _ = SetWindowTextW(GetDlgItem(hwnd, id), PCWSTR(wide.as_ptr()));
}
unsafe fn get_text(hwnd: HWND, id: i32) -> Result<String> {
    let child = GetDlgItem(hwnd, id);
    let len = GetWindowTextLengthW(child);
    let mut text = vec![0u16; len as usize + 1];
    let copied = GetWindowTextW(child, &mut text);
    if copied == 0 && len != 0 {
        anyhow::bail!(
            "Could not read input: {}",
            windows::core::Error::from_win32()
        )
    }
    Ok(String::from_utf16(&text[..copied as usize])?)
}
unsafe fn set_enabled(hwnd: HWND, id: i32, on: bool) {
    let _ = EnableWindow(GetDlgItem(hwnd, id), on);
}
unsafe fn set_busy(hwnd: HWND, busy: bool, text: &str) {
    for id in [ID_PATH, ID_BROWSE, ID_CHECK, ID_ACTION, ID_RESTORE, ID_PLAY] {
        if busy || matches!(id, ID_PATH | ID_BROWSE | ID_CHECK) {
            set_enabled(hwnd, id, !busy)
        }
    }
    let _ = SendMessageW(
        GetDlgItem(hwnd, ID_PROGRESS),
        PBM_SETMARQUEE,
        WPARAM(busy as usize),
        LPARAM(30),
    );
    if !text.is_empty() {
        set_text(hwnd, ID_DETAIL, text)
    }
}
fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(Some(0)).collect()
}

unsafe fn choose_folder(owner: HWND) -> Result<Option<PathBuf>> {
    let dialog: IFileOpenDialog = CoCreateInstance(&FileOpenDialog, None, CLSCTX_INPROC_SERVER)?;
    dialog.SetOptions(dialog.GetOptions()? | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM)?;
    dialog.SetTitle(w!("Select the folder containing PGR.exe"))?;
    match dialog.Show(owner) {
        Ok(()) => {
            let item = dialog.GetResult()?;
            let raw = item.GetDisplayName(SIGDN_FILESYSPATH)?;
            let path = PathBuf::from(raw.to_string()?);
            CoTaskMemFree(Some(raw.0.cast()));
            if !crate::steam::valid_game_directory(&path) {
                anyhow::bail!("Selected folder does not contain PGR.exe")
            }
            Ok(Some(path))
        }
        Err(e) if e.code() == HRESULT::from_win32(ERROR_CANCELLED.0) => Ok(None),
        Err(e) => Err(e.into()),
    }
}
fn load_bitmap(path: &Path) -> Result<HBITMAP> {
    unsafe {
        let w = wide(&path.display().to_string());
        LoadImageW(
            None,
            PCWSTR(w.as_ptr()),
            IMAGE_BITMAP,
            0,
            0,
            LR_LOADFROMFILE,
        )
        .map(|h| HBITMAP(h.0))
        .with_context(|| {
            format!(
                "Launcher background is missing or unreadable: {}",
                path.display()
            )
        })
    }
}
unsafe fn paint(hwnd: HWND, state: &Window) {
    let mut ps = PAINTSTRUCT::default();
    let dc = BeginPaint(hwnd, &mut ps);
    let mut rect = RECT::default();
    let _ = GetClientRect(hwnd, &mut rect);
    let mem = CreateCompatibleDC(dc);
    let old = SelectObject(mem, state.background);
    let mut bm = BITMAP::default();
    let _ = GetObjectW(
        state.background,
        size_of::<BITMAP>() as i32,
        Some((&mut bm as *mut BITMAP).cast()),
    );
    let (dw, dh) = (rect.right.max(1), rect.bottom.max(1));
    let (src_w, src_h) = if bm.bmWidth as i64 * dh as i64 > bm.bmHeight as i64 * dw as i64 {
        (bm.bmHeight * dw / dh, bm.bmHeight)
    } else {
        (bm.bmWidth, bm.bmWidth * dh / dw)
    };
    let sx = (bm.bmWidth - src_w) / 2;
    let sy = (bm.bmHeight - src_h) / 2;
    let _ = SetStretchBltMode(dc, HALFTONE);
    let mut origin = POINT::default();
    let _ = SetBrushOrgEx(dc, 0, 0, Some(&mut origin));
    let _ = StretchBlt(dc, 0, 0, dw, dh, mem, sx, sy, src_w, src_h, SRCCOPY);
    let _ = SelectObject(mem, old);
    let _ = DeleteDC(mem);
    let panel = RECT {
        left: 0,
        top: 0,
        right: dw.clamp(400, 440),
        bottom: dh,
    };
    let _ = FillRect(dc, &panel, state.panel_brush);
    let _ = EndPaint(hwnd, &ps);
}
pub fn show_fatal(message: &str) {
    unsafe {
        let text = wide(message);
        MessageBoxW(
            None,
            PCWSTR(text.as_ptr()),
            w!("AscNet Launcher"),
            MB_OK | MB_ICONERROR,
        );
    }
}

use anyhow::{Context, Result};
use ascnet_launcher::{
    fps,
    install::{self, PatchState},
    local::{self, LocalBuild, LocalRuntime},
    package::{self, PatchPackage},
};
use serde::{Deserialize, Serialize};
use std::{
    collections::VecDeque,
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
    core::{w, PCWSTR, PWSTR},
    Win32::{
        Foundation::*,
        Graphics::{Dwm::*, Gdi::*},
        System::{
            Com::*, LibraryLoader::GetModuleHandleW, SystemServices::{SS_NOTIFY, SS_OWNERDRAW},
            Threading::CreateMutexW,
        },
        UI::{Controls::*, Input::KeyboardAndMouse::*, Shell::*, WindowsAndMessaging::*},
    },
};

const WM_EVENT: u32 = WM_APP + 1;
const ID_BROWSE: i32 = 101;
const ID_CHECK: i32 = 102;
const ID_ACTION: i32 = 103;
const ID_RESTORE: i32 = 104;
const ID_PLAY: i32 = 105;
const ID_PATH_FIELD: i32 = 109;
const ID_PATH: i32 = 110;
const ID_STATUS: i32 = 111;
const ID_DETAIL: i32 = 112;
const ID_PROGRESS: i32 = 113;
const ID_TITLE: i32 = 115;
const TIMER_PROGRESS: usize = 1;
const TIMER_ANIMATION: usize = 2;
const ID_SUBTITLE: i32 = 116;
const ID_PATH_LABEL: i32 = 118;
const ID_HOME_ACTION: i32 = 120;
const ID_SETTINGS: i32 = 121;
const ID_MINIMIZE: i32 = 122;
const ID_CLOSE: i32 = 123;
const ID_CARD_HEADING: i32 = 124;
const ID_FPS_ENABLED: i32 = 125;
const ID_FPS_VALUE: i32 = 126;
const ID_FPS_ACTION: i32 = 127;
const ID_FPS_STATUS: i32 = 128;
const ID_FPS_VALUE_FIELD: i32 = 129;
const ID_MUSIC_MUTED: i32 = 130;
const ID_GAME_TWEAKS: i32 = 131;
const ID_LAUNCHER_HEADING: i32 = 132;
const ID_FPS_LABEL: i32 = 133;
const ID_MUSIC_LABEL: i32 = 134;
const ID_FPS_UNIT: i32 = 135;
const CENTERED_EDIT_HEIGHT: i32 = 22;
const LAUNCHER_VERSION: &str = env!("CARGO_PKG_VERSION");

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
    #[serde(default = "default_fps")]
    fps_value: i32,
    #[serde(default)]
    music_muted: bool,
}
impl Default for Settings {
    fn default() -> Self {
        Self {
            selected_game: None,
            fps_value: default_fps(),
            music_muted: false,
        }
    }
}
const fn default_fps() -> i32 {
    240
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

struct Model {
    config: DistributorConfig,
    settings: Settings,
    build: Option<LocalBuild>,
    package: Option<PatchPackage>,
    runtime: Option<LocalRuntime>,
    patch: Option<PatchState>,
    fps: Option<Option<i32>>,
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
    overlay: HBITMAP,
    animation: crate::animation::Animation,
    music: Option<crate::animation::Music>,
    music_path: PathBuf,
    animation_sequence: u64,
    backdrop: HBITMAP,
    backdrop_size: SIZE,
    edit_brush: HBRUSH,
    button_brush: HBRUSH,
    button_hot_brush: HBRUSH,
    accent_brush: HBRUSH,
    muted_brush: HBRUSH,
    header_brush: HBRUSH,
    title_font: HFONT,
    heading_font: HFONT,
    body_font: HFONT,
    label_font: HFONT,
    progress_phase: i32,
    settings_open: bool,
    log: VecDeque<String>,
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
        fps: Option<i32>,
        update: Result<Option<bool>>,
    },
    Prepared {
        build: LocalBuild,
        package: PatchPackage,
        patch: PatchState,
    },
    Restored,
    FpsChanged(Option<i32>),
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
        hbrBackground: HBRUSH(GetStockObject(BLACK_BRUSH).0),
        style: CS_HREDRAW | CS_VREDRAW | CS_DBLCLKS,
        ..Default::default()
    };
    if RegisterClassW(&wc) == 0 {
        anyhow::bail!(
            "RegisterClassW failed: {}",
            windows::core::Error::from_win32()
        )
    }
    let (event_tx, event_rx) = mpsc::channel();
    let animation = crate::animation::Animation::start(&exe_dir.join("background.mp4"))
        .context("launcher background animation is unavailable")?;
    let music_path = exe_dir.join("background.wav");
    let (music, music_warning) = if settings.music_muted {
        (None, None)
    } else {
        match crate::animation::Music::start(&music_path) {
            Ok(music) => (Some(music), None),
            Err(error) => {
                eprintln!("could not start background music: {error:#}");
                (
                    None,
                    Some("Music unavailable; could not start background.wav.".to_owned()),
                )
            }
        }
    };
    let model = Arc::new(Mutex::new(Model {
        config,
        settings,
        build: None,
        package: None,
        runtime: None,
        patch: None,
        fps: None,
        server: None,
        update_available: None,
        update_error: None,
        busy: false,
        generation: Arc::new(AtomicU64::new(0)),
        events: event_tx,
    }));
    let background = load_bitmap(&exe_dir.join("background.bmp"))?;
    let edit_brush = CreateSolidBrush(COLORREF(0x00221c1b));
    let button_brush = CreateSolidBrush(COLORREF(0x00342c2b));
    let button_hot_brush = CreateSolidBrush(COLORREF(0x00483a38));
    let accent_brush = CreateSolidBrush(COLORREF(0x00ddd7cf));
    let header_brush = CreateSolidBrush(COLORREF(0x00171312));
    let muted_brush = CreateSolidBrush(COLORREF(0x00484442));
    let overlay_pixel: u32 = 0x00201a18;
    let overlay = CreateBitmap(1, 1, 1, 32, Some((&overlay_pixel as *const u32).cast()));
    let label_font = theme_font(12, FW_SEMIBOLD.0 as i32);
    let title_font = theme_font(42, FW_BOLD.0 as i32);
    let heading_font = theme_font(16, FW_SEMIBOLD.0 as i32);
    let body_font = theme_font(15, FW_NORMAL.0 as i32);
    if edit_brush.0 == 0
        || button_brush.0 == 0
        || button_hot_brush.0 == 0
        || accent_brush.0 == 0
        || header_brush.0 == 0
        || muted_brush.0 == 0
        || overlay.0 == 0
        || title_font.0 == 0
        || heading_font.0 == 0
        || body_font.0 == 0
        || label_font.0 == 0
    {
        for object in [
            overlay.0,
            edit_brush.0,
            button_brush.0,
            button_hot_brush.0,
            accent_brush.0,
            header_brush.0,
            muted_brush.0,
            title_font.0,
            heading_font.0,
            body_font.0,
            label_font.0,
        ] {
            if object != 0 {
                let _ = DeleteObject(HGDIOBJ(object));
            }
        }
        let _ = DeleteObject(background);
        anyhow::bail!("Could not create launcher theme resources")
    }
    let mut log = VecDeque::from(["Launcher ready".to_owned()]);
    if let Some(warning) = music_warning {
        log.push_back(warning);
    }
    let state = Box::new(Window {
        model,
        events: event_rx,
        background,
        animation,
        music,
        music_path,
        animation_sequence: 0,
        overlay,
        backdrop: HBITMAP(0),
        backdrop_size: SIZE::default(),
        edit_brush,
        button_brush,
        button_hot_brush,
        accent_brush,
        muted_brush,
        header_brush,
        title_font,
        heading_font,
        body_font,
        progress_phase: 0,
        label_font,
        settings_open: false,
        log,
    });
    let state_ptr = Box::into_raw(state);
    let hwnd = CreateWindowExW(
        WS_EX_CONTROLPARENT,
        class,
        w!("AscNet Launcher"),
        WS_POPUP | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_CLIPCHILDREN,
        CW_USEDEFAULT,
        CW_USEDEFAULT,
        1280,
        720,
        HWND(0),
        HMENU(0),
        instance,
        Some(state_ptr.cast()),
    );
    if hwnd.0 == 0 {
        let state = Box::from_raw(state_ptr);
        let _ = DeleteObject(state.background);
        delete_theme(&state);
        anyhow::bail!(
            "CreateWindowExW failed: {}",
            windows::core::Error::from_win32()
        )
    }
    let _ = SetTimer(hwnd, TIMER_ANIMATION, 16, None);
    let dark: i32 = 1;
    let _ = DwmSetWindowAttribute(
        hwnd,
        DWMWA_USE_IMMERSIVE_DARK_MODE,
        (&dark as *const i32).cast(),
        size_of::<i32>() as u32,
    );
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
            if !ptr.is_null() {
                create_controls(hwnd, &*ptr);
                let (path, fps_value, music_muted) = {
                    let m = (*ptr).model.lock().unwrap();
                    (
                        m.settings
                            .selected_game
                            .as_deref()
                            .map(|p| p.display().to_string())
                            .unwrap_or_default(),
                        m.settings.fps_value,
                        m.settings.music_muted,
                    )
                };
                set_text(hwnd, ID_PATH, &path);
                set_text(hwnd, ID_FPS_VALUE, &fps_value.to_string());
                let _ = SendMessageW(
                    GetDlgItem(hwnd, ID_MUSIC_MUTED),
                    BM_SETCHECK,
                    WPARAM(if !music_muted {
                        BST_CHECKED.0 as usize
                    } else {
                        BST_UNCHECKED.0 as usize
                    }),
                    LPARAM(0),
                );
                append_log(hwnd, &mut *ptr, "Launcher ready");
            }
            LRESULT(0)
        }
        WM_SIZE => {
            let width = (lp.0 as u32 & 0xffff) as i32;
            let height = ((lp.0 as u32 >> 16) & 0xffff) as i32;
            if !ptr.is_null() {
                let state = &mut *ptr;
                let minimized = wp.0 == SIZE_MINIMIZED as usize;
                state.animation.set_paused(minimized);
                if !minimized {
                    layout(hwnd, width, height, state.settings_open);
                    rebuild_backdrop(hwnd, state, width, height);
                }
            }
            LRESULT(0)
        }
        WM_GETMINMAXINFO => {
            let info = &mut *(lp.0 as *mut MINMAXINFO);
            info.ptMinTrackSize = POINT { x: 760, y: 560 };
            LRESULT(0)
        }
        WM_ERASEBKGND => LRESULT(1),
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
            let child = HWND(lp.0);
            let dc = HDC(wp.0 as isize);
            let id = GetDlgCtrlID(child);
            if id == ID_PATH_FIELD || id == ID_FPS_VALUE_FIELD {
                let _ = SetBkColor(dc, COLORREF(0x00221c1b));
                return LRESULT((*ptr).edit_brush.0);
            }
            let color = if id == ID_SUBTITLE || id == ID_PATH_LABEL || id == ID_DETAIL {
                COLORREF(0x00c9c5c2)
            } else {
                COLORREF(0x00f4f1ef)
            };
            let _ = SetTextColor(dc, color);
            let _ = SetBkMode(dc, TRANSPARENT);
            LRESULT(GetStockObject(NULL_BRUSH).0)
        }
        WM_CTLCOLOREDIT => {
            if ptr.is_null() {
                return DefWindowProcW(hwnd, msg, wp, lp);
            }
            let dc = HDC(wp.0 as isize);
            let _ = SetTextColor(dc, COLORREF(0x00f4f1ef));
            let _ = SetBkColor(dc, COLORREF(0x00221c1b));
            LRESULT((*ptr).edit_brush.0)
        }
        WM_DRAWITEM => {
            if !ptr.is_null() {
                draw_item(&*(lp.0 as *const DRAWITEMSTRUCT), &*ptr);
                return LRESULT(1);
            }
            DefWindowProcW(hwnd, msg, wp, lp)
        }
        WM_NCHITTEST => {
            let hit = DefWindowProcW(hwnd, msg, wp, lp);
            if hit.0 == HTCLIENT as isize {
                let mut point = POINT {
                    x: (lp.0 as i16) as i32,
                    y: ((lp.0 >> 16) as i16) as i32,
                };
                let _ = ScreenToClient(hwnd, &mut point);
                if point.y < 54 {
                    return LRESULT(HTCAPTION as isize);
                }
            }
            hit
        }
        WM_TIMER if wp.0 == TIMER_PROGRESS => {
            if !ptr.is_null() {
                (*ptr).progress_phase = ((*ptr).progress_phase + 4) % 140;
                let _ = InvalidateRect(GetDlgItem(hwnd, ID_PROGRESS), None, false);
            }
            LRESULT(0)
        }
        WM_TIMER if wp.0 == TIMER_ANIMATION => {
            if !ptr.is_null() {
                let state = &mut *ptr;
                let mut frame = None;
                let result = state.animation.with_frame(
                    state.animation_sequence,
                    |sequence, width, height, pixels| {
                        frame = frame_bitmap(hwnd, sequence, width, height, pixels);
                    },
                );
                match result {
                    Ok(true) => {
                        if let Some((sequence, bitmap)) = frame {
                            let old = std::mem::replace(&mut state.background, bitmap);
                            state.animation_sequence = sequence;
                            let _ = DeleteObject(old);
                            let mut rect = RECT::default();
                            let _ = GetClientRect(hwnd, &mut rect);
                            rebuild_backdrop(hwnd, state, rect.right, rect.bottom);
                            let _ = RedrawWindow(
                                hwnd,
                                None,
                                None,
                                RDW_INVALIDATE | RDW_UPDATENOW | RDW_ALLCHILDREN,
                            );
                        }
                    }
                    Ok(false) => {}
                    Err(error) => {
                        let _ = KillTimer(hwnd, TIMER_ANIMATION);
                        show_fatal(&format!("Background animation error: {error:#}"));
                    }
                }
            }
            LRESULT(0)
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
                        Event::Progress(text) => {
                            if matches!(
                                text.as_str(),
                                "Starting local source setup"
                                    | "Starting MongoDB"
                                    | "Starting AscNet server"
                                    | "Local backend is ready"
                            ) {
                                append_log(hwnd, &mut *ptr, &text);
                            }
                        }
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
                if state.backdrop.0 != 0 {
                    let _ = DeleteObject(state.backdrop);
                }
                delete_theme(&state);
            }
            PostQuitMessage(0);
            LRESULT(0)
        }
        _ => DefWindowProcW(hwnd, msg, wp, lp),
    }
}

unsafe extern "system" fn backdrop_control_subclass(
    hwnd: HWND,
    msg: u32,
    wp: WPARAM,
    lp: LPARAM,
    _id: usize,
    _data: usize,
) -> LRESULT {
    if msg == WM_ERASEBKGND {
        return LRESULT(1);
    }
    if msg != WM_PAINT {
        let result = DefSubclassProc(hwnd, msg, wp, lp);
        if matches!(GetDlgCtrlID(hwnd), ID_FPS_ENABLED | ID_MUSIC_MUTED)
            && matches!(msg, BM_SETCHECK | BM_SETSTATE | WM_SETFOCUS | WM_KILLFOCUS | WM_ENABLE)
        {
            let _ = InvalidateRect(hwnd, None, false);
        }
        return result;
    }
    let state = GetWindowLongPtrW(GetParent(hwnd), GWLP_USERDATA) as *const Window;
    if state.is_null() {
        return DefSubclassProc(hwnd, msg, wp, lp);
    }
    let mut ps = PAINTSTRUCT::default();
    let dc = BeginPaint(hwnd, &mut ps);
    let mut rect = RECT::default();
    let _ = GetClientRect(hwnd, &mut rect);
    let buffer = CreateCompatibleDC(dc);
    let bitmap = CreateCompatibleBitmap(dc, rect.right.max(1), rect.bottom.max(1));
    let buffered = buffer.0 != 0 && bitmap.0 != 0;
    let target = if buffered { buffer } else { dc };
    let old = if buffered {
        SelectObject(buffer, bitmap)
    } else {
        HGDIOBJ(0)
    };
    // Color queries also happen during partial native focus/state updates.
    // Restore only for a full paint, then present backdrop and native text together.
    paint_control_backdrop(hwnd, target, &*state);
    if matches!(GetDlgCtrlID(hwnd), ID_FPS_ENABLED | ID_MUSIC_MUTED) {
        draw_toggle(hwnd, target, rect, &*state);
    } else {
        let _ = DefSubclassProc(
            hwnd,
            WM_PRINTCLIENT,
            WPARAM(target.0 as usize),
            LPARAM(PRF_CLIENT as isize),
        );
    }
    if buffered {
        let _ = BitBlt(dc, 0, 0, rect.right, rect.bottom, buffer, 0, 0, SRCCOPY);
        let _ = SelectObject(buffer, old);
    }
    if bitmap.0 != 0 {
        let _ = DeleteObject(bitmap);
    }
    if buffer.0 != 0 {
        let _ = DeleteDC(buffer);
    }
    let _ = EndPaint(hwnd, &ps);
    LRESULT(0)
}

unsafe fn draw_toggle(hwnd: HWND, dc: HDC, rect: RECT, state: &Window) {
    let checked = SendMessageW(hwnd, BM_GETCHECK, WPARAM(0), LPARAM(0)).0
        == BST_CHECKED.0 as isize;
    let disabled = !IsWindowEnabled(hwnd).as_bool();
    let pressed = SendMessageW(hwnd, BM_GETSTATE, WPARAM(0), LPARAM(0)).0
        & BST_PUSHED as isize != 0;
    let _ = FillRect(dc, &rect, if checked { state.accent_brush } else { state.edit_brush });
    let _ = FrameRect(dc, &rect, if disabled { state.muted_brush } else { state.accent_brush });
    let _ = SetBkMode(dc, TRANSPARENT);
    let _ = SetTextColor(dc, if disabled {
        COLORREF(0x009a918d)
    } else if checked {
        COLORREF(0x00201a18)
    } else {
        COLORREF(0x00f4f1ef)
    });
    let old_font = SelectObject(dc, state.label_font);
    let mut text = if checked { [b'O' as u16, b'N' as u16, 0] } else { [b'O' as u16, b'F' as u16, b'F' as u16] };
    let mut text_rect = rect;
    if pressed {
        let _ = OffsetRect(&mut text_rect, 1, 1);
    }
    let _ = DrawTextW(dc, &mut text[..if checked { 2 } else { 3 }], &mut text_rect, DT_CENTER | DT_VCENTER | DT_SINGLELINE);
    let _ = SelectObject(dc, old_font);
    if GetFocus() == hwnd {
        let mut focus = rect;
        let _ = InflateRect(&mut focus, -4, -4);
        let _ = DrawFocusRect(dc, &focus);
    }
}

unsafe extern "system" fn button_subclass(
    hwnd: HWND,
    msg: u32,
    wp: WPARAM,
    lp: LPARAM,
    _id: usize,
    _data: usize,
) -> LRESULT {
    if msg == WM_ERASEBKGND {
        return LRESULT(1);
    }
    match msg {
        WM_MOUSEMOVE => {
            if GetWindowLongPtrW(hwnd, GWLP_USERDATA) == 0 {
                let _ = SetWindowLongPtrW(hwnd, GWLP_USERDATA, 1);
                let mut tracking = TRACKMOUSEEVENT {
                    cbSize: size_of::<TRACKMOUSEEVENT>() as u32,
                    dwFlags: TME_LEAVE,
                    hwndTrack: hwnd,
                    dwHoverTime: 0,
                };
                let _ = TrackMouseEvent(&mut tracking);
                let _ = InvalidateRect(hwnd, None, false);
            }
        }
        WM_MOUSELEAVE => {
            let _ = SetWindowLongPtrW(hwnd, GWLP_USERDATA, 0);
            let _ = InvalidateRect(hwnd, None, false);
        }
        _ => {}
    }
    DefSubclassProc(hwnd, msg, wp, lp)
}
unsafe fn create_controls(hwnd: HWND, state: &Window) {
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
        w!("PUNISHING: GRAY RAVEN"),
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
        w!("GAME DIRECTORY"),
        WS_VISIBLE,
        ID_PATH_LABEL,
        0,
        0,
        0,
        0,
    );
    control(
        hwnd,
        w!("STATIC"),
        PCWSTR::null(),
        WS_VISIBLE | WINDOW_STYLE(SS_NOTIFY.0),
        ID_PATH_FIELD,
        0,
        0,
        0,
        0,
    );
    control(
        hwnd,
        w!("EDIT"),
        PCWSTR::null(),
        WS_VISIBLE | WS_TABSTOP | WINDOW_STYLE((ES_AUTOHSCROLL | ES_CENTER) as u32),
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
        WS_VISIBLE | WS_TABSTOP | WINDOW_STYLE((BS_OWNERDRAW | BS_FLAT) as u32),
        ID_BROWSE,
        0,
        0,
        0,
        0,
    );
    control(
        hwnd,
        w!("BUTTON"),
        w!("Menu"),
        WS_VISIBLE | WS_TABSTOP | WINDOW_STYLE((BS_OWNERDRAW | BS_FLAT) as u32),
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
        w!("STATIC"),
        PCWSTR::null(),
        WS_VISIBLE | WINDOW_STYLE(SS_OWNERDRAW.0),
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
        WS_VISIBLE | WS_TABSTOP | WINDOW_STYLE((BS_OWNERDRAW | BS_FLAT) as u32),
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
        WS_VISIBLE | WS_TABSTOP | WINDOW_STYLE((BS_OWNERDRAW | BS_FLAT) as u32),
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
        WS_VISIBLE | WS_TABSTOP | WINDOW_STYLE((BS_OWNERDRAW | BS_FLAT) as u32),
        ID_RESTORE,
        0,
        0,
        0,
        0,
    );
    control(
        hwnd,
        w!("BUTTON"),
        w!("FPS &unlock"),
        WS_VISIBLE | WS_TABSTOP | WINDOW_STYLE(BS_AUTOCHECKBOX as u32),
        ID_FPS_ENABLED,
        0,
        0,
        0,
        0,
    );
    control(
        hwnd,
        w!("STATIC"),
        PCWSTR::null(),
        WS_VISIBLE | WINDOW_STYLE(SS_NOTIFY.0),
        ID_FPS_VALUE_FIELD,
        0,
        0,
        0,
        0,
    );
    control(
        hwnd,
        w!("EDIT"),
        w!("240"),
        WS_VISIBLE | WS_TABSTOP | WINDOW_STYLE((ES_AUTOHSCROLL | ES_CENTER | ES_NUMBER) as u32),
        ID_FPS_VALUE,
        0,
        0,
        0,
        0,
    );
    control(
        hwnd,
        w!("BUTTON"),
        w!("&Apply"),
        WS_VISIBLE | WS_TABSTOP | WINDOW_STYLE((BS_OWNERDRAW | BS_FLAT) as u32),
        ID_FPS_ACTION,
        0,
        0,
        0,
        0,
    );
    control(
        hwnd,
        w!("STATIC"),
        w!("Not inspected"),
        WS_VISIBLE,
        ID_FPS_STATUS,
        0,
        0,
        0,
        0,
    );
    control(
        hwnd,
        w!("BUTTON"),
        w!("Background &music"),
        WS_VISIBLE | WS_TABSTOP | WINDOW_STYLE(BS_AUTOCHECKBOX as u32),
        ID_MUSIC_MUTED,
        0,
        0,
        0,
        0,
    );
    for (id, text) in [
        (ID_GAME_TWEAKS, w!("GAME TWEAKS")),
        (ID_LAUNCHER_HEADING, w!("LAUNCHER")),
        (ID_FPS_LABEL, w!("FPS unlock")),
        (ID_MUSIC_LABEL, w!("Background music")),
        (ID_FPS_UNIT, w!("FPS")),
    ] {
        control(hwnd, w!("STATIC"), text, WS_VISIBLE, id, 0, 0, 0, 0);
    }
    control(
        hwnd,
        w!("BUTTON"),
        w!("&Play"),
        WS_VISIBLE | WS_TABSTOP | WINDOW_STYLE((BS_OWNERDRAW | BS_FLAT) as u32),
        ID_PLAY,
        0,
        0,
        0,
        0,
    );
    control(
        hwnd,
        w!("STATIC"),
        w!("SERVER LOG"),
        WS_VISIBLE,
        ID_CARD_HEADING,
        0,
        0,
        0,
        0,
    );
    for (id, text) in [
        (ID_SETTINGS, w!("SETTINGS")),
        (ID_MINIMIZE, w!("Minimize")),
        (ID_CLOSE, w!("Close")),
        (ID_HOME_ACTION, w!("SELECT GAME")),
    ] {
        control(
            hwnd,
            w!("BUTTON"),
            text,
            WS_VISIBLE | WS_TABSTOP | WINDOW_STYLE((BS_OWNERDRAW | BS_FLAT) as u32),
            id,
            0,
            0,
            0,
            0,
        );
    }
    for id in [
        ID_TITLE,
        ID_SUBTITLE,
        ID_PATH_LABEL,
        ID_DETAIL,
        ID_CARD_HEADING,
        ID_FPS_STATUS,
        ID_FPS_ENABLED,
        ID_MUSIC_MUTED,
        ID_GAME_TWEAKS,
        ID_LAUNCHER_HEADING,
        ID_FPS_LABEL,
        ID_MUSIC_LABEL,
        ID_FPS_UNIT,
    ] {
        let _ = SetWindowSubclass(GetDlgItem(hwnd, id), Some(backdrop_control_subclass), 1, 0);
    }
    for (id, font) in [
        (ID_TITLE, state.heading_font),
        (ID_SUBTITLE, state.body_font),
        (ID_PATH_LABEL, state.label_font),
        (ID_PATH, state.body_font),
        (ID_STATUS, state.body_font),
        (ID_DETAIL, state.body_font),
        (ID_CARD_HEADING, state.heading_font),
        (ID_BROWSE, state.body_font),
        (ID_CHECK, state.body_font),
        (ID_ACTION, state.heading_font),
        (ID_RESTORE, state.body_font),
        (ID_PLAY, state.heading_font),
        (ID_SETTINGS, state.label_font),
        (ID_MINIMIZE, state.heading_font),
        (ID_CLOSE, state.heading_font),
        (ID_HOME_ACTION, state.heading_font),
        (ID_FPS_ENABLED, state.body_font),
        (ID_FPS_VALUE, state.body_font),
        (ID_FPS_ACTION, state.body_font),
        (ID_FPS_STATUS, state.label_font),
        (ID_MUSIC_MUTED, state.body_font),
        (ID_GAME_TWEAKS, state.label_font),
        (ID_LAUNCHER_HEADING, state.label_font),
        (ID_FPS_LABEL, state.body_font),
        (ID_MUSIC_LABEL, state.body_font),
        (ID_FPS_UNIT, state.label_font),
    ] {
        let _ = SendMessageW(
            GetDlgItem(hwnd, id),
            WM_SETFONT,
            WPARAM(font.0 as usize),
            LPARAM(1),
        );
    }
    for id in [
        ID_BROWSE,
        ID_STATUS,
        ID_CHECK,
        ID_ACTION,
        ID_RESTORE,
        ID_PLAY,
        ID_SETTINGS,
        ID_MINIMIZE,
        ID_CLOSE,
        ID_HOME_ACTION,
        ID_FPS_ACTION,
    ] {
        let _ = SetWindowSubclass(GetDlgItem(hwnd, id), Some(button_subclass), 1, 0);
    }
    for id in [
        ID_PATH_LABEL,
        ID_PATH_FIELD,
        ID_PATH,
        ID_BROWSE,
        ID_CHECK,
        ID_RESTORE,
        ID_ACTION,
        ID_PLAY,
        ID_FPS_ENABLED,
        ID_FPS_VALUE_FIELD,
        ID_FPS_VALUE,
        ID_MUSIC_MUTED,
        ID_FPS_ACTION,
        ID_FPS_STATUS,
        ID_GAME_TWEAKS,
        ID_LAUNCHER_HEADING,
        ID_FPS_LABEL,
        ID_MUSIC_LABEL,
        ID_FPS_UNIT,
    ] {
        let _ = ShowWindow(GetDlgItem(hwnd, id), SW_HIDE);
    }
    let _ = SendMessageW(
        hwnd,
        DM_SETDEFID,
        WPARAM(ID_HOME_ACTION as usize),
        LPARAM(0),
    );
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
fn home_controls(width: i32, height: i32) -> (RECT, RECT, RECT) {
    let action_width = (width / 4).clamp(272, 360);
    let action = RECT {
        left: width - 24 - action_width,
        top: height - 76,
        right: width - 24,
        bottom: height - 24,
    };
    let menu = RECT {
        left: action.left - 56,
        top: action.top,
        right: action.left - 12,
        bottom: action.bottom,
    };
    let log = RECT {
        left: 24,
        top: height - 166,
        right: (menu.left - 24).min(664),
        bottom: height - 24,
    };
    (log, menu, action)
}

fn settings_rect(width: i32, height: i32) -> RECT {
    let panel_width = (width - 48).min(760);
    let panel_height = (height - 102).min(480);
    let left = (width - panel_width) / 2;
    let top = 54 + (height - 54 - panel_height) / 2;
    RECT {
        left,
        top,
        right: left + panel_width,
        bottom: top + panel_height,
    }
}

unsafe fn layout(hwnd: HWND, width: i32, height: i32, settings: bool) {
    let settings_panel = settings_rect(width, height);
    let settings_x = settings_panel.left + 40;
    let settings_content = settings_panel.right - settings_panel.left - 80;
    let (log, menu, action) = home_controls(width, height);
    let _ = MoveWindow(GetDlgItem(hwnd, ID_TITLE), 24, 13, 78, 28, true);
    let _ = MoveWindow(GetDlgItem(hwnd, ID_SUBTITLE), 112, 15, 240, 24, true);
    let _ = MoveWindow(GetDlgItem(hwnd, ID_CHECK), width - 270, 9, 100, 34, true);
    let _ = MoveWindow(GetDlgItem(hwnd, ID_SETTINGS), width - 158, 9, 46, 34, true);
    let _ = MoveWindow(GetDlgItem(hwnd, ID_MINIMIZE), width - 106, 9, 46, 34, true);
    let _ = MoveWindow(GetDlgItem(hwnd, ID_CLOSE), width - 54, 9, 46, 34, true);
    for id in [ID_STATUS, ID_DETAIL] {
        let _ = ShowWindow(
            GetDlgItem(hwnd, id),
            if settings { SW_HIDE } else { SW_SHOW },
        );
    }
    for id in [
        ID_PATH_LABEL,
        ID_PATH_FIELD,
        ID_PATH,
        ID_BROWSE,
        ID_RESTORE,
        ID_FPS_ENABLED,
        ID_FPS_VALUE_FIELD,
        ID_FPS_VALUE,
        ID_FPS_ACTION,
        ID_FPS_STATUS,
        ID_MUSIC_MUTED,
        ID_GAME_TWEAKS,
        ID_LAUNCHER_HEADING,
        ID_FPS_LABEL,
        ID_MUSIC_LABEL,
        ID_FPS_UNIT,
    ] {
        let _ = ShowWindow(
            GetDlgItem(hwnd, id),
            if settings { SW_SHOW } else { SW_HIDE },
        );
    }
    set_text(
        hwnd,
        ID_CARD_HEADING,
        if settings { "SETTINGS" } else { "SERVER LOG" },
    );
    for id in [ID_CHECK, ID_SETTINGS] {
        let _ = ShowWindow(
            GetDlgItem(hwnd, id),
            if settings { SW_SHOW } else { SW_HIDE },
        );
    }
    set_text(hwnd, ID_CHECK, "Check");
    if settings {
        let top = settings_panel.top;
        let _ = MoveWindow(
            GetDlgItem(hwnd, ID_CARD_HEADING),
            settings_x,
            top + 20,
            settings_content,
            30,
            true,
        );
        let _ = MoveWindow(
            GetDlgItem(hwnd, ID_PATH_LABEL),
            settings_x,
            top + 62,
            settings_content,
            20,
            true,
        );
        let browse_width = 112;
        let path_y = top + 86;
        let path_width = settings_content - browse_width - 12;
        let _ = MoveWindow(
            GetDlgItem(hwnd, ID_PATH_FIELD),
            settings_x,
            path_y,
            path_width,
            40,
            true,
        );
        let _ = MoveWindow(
            GetDlgItem(hwnd, ID_PATH),
            settings_x,
            path_y + (40 - CENTERED_EDIT_HEIGHT) / 2,
            path_width,
            CENTERED_EDIT_HEIGHT,
            true,
        );
        let _ = MoveWindow(
            GetDlgItem(hwnd, ID_BROWSE),
            settings_x + settings_content - browse_width,
            path_y,
            browse_width,
            40,
            true,
        );
        let half = (settings_content - 16) / 2;
        let _ = MoveWindow(
            GetDlgItem(hwnd, ID_CHECK),
            settings_x,
            top + 138,
            half,
            42,
            true,
        );
        let _ = MoveWindow(
            GetDlgItem(hwnd, ID_RESTORE),
            settings_x + half + 16,
            top + 138,
            half,
            42,
            true,
        );
        let fps_y = top + 220;
        let toggle_x = settings_x + settings_content - 72;
        for (id, y) in [(ID_GAME_TWEAKS, top + 194), (ID_LAUNCHER_HEADING, top + 294)] {
            let _ = MoveWindow(GetDlgItem(hwnd, id), settings_x, y, settings_content, 20, true);
        }
        let _ = MoveWindow(GetDlgItem(hwnd, ID_FPS_LABEL), settings_x, fps_y + 7, 164, 24, true);
        let _ = MoveWindow(GetDlgItem(hwnd, ID_MUSIC_LABEL), settings_x, top + 327, 220, 24, true);
        let _ = MoveWindow(GetDlgItem(hwnd, ID_FPS_UNIT), toggle_x - 152, fps_y + 9, 40, 22, true);
        let _ = MoveWindow(
            GetDlgItem(hwnd, ID_FPS_ENABLED),
            toggle_x,
            fps_y,
            72,
            36,
            true,
        );
        let _ = MoveWindow(
            GetDlgItem(hwnd, ID_FPS_VALUE_FIELD),
            toggle_x - 232,
            fps_y,
            72,
            36,
            true,
        );
        let _ = MoveWindow(
            GetDlgItem(hwnd, ID_FPS_VALUE),
            toggle_x - 232,
            fps_y + (36 - CENTERED_EDIT_HEIGHT) / 2,
            72,
            CENTERED_EDIT_HEIGHT,
            true,
        );
        let _ = MoveWindow(
            GetDlgItem(hwnd, ID_FPS_ACTION),
            toggle_x - 108,
            fps_y,
            96,
            36,
            true,
        );
        let _ = MoveWindow(
            GetDlgItem(hwnd, ID_FPS_STATUS),
            settings_x,
            fps_y + 40,
            settings_content,
            20,
            true,
        );
        let _ = MoveWindow(
            GetDlgItem(hwnd, ID_MUSIC_MUTED),
            toggle_x,
            top + 320,
            72,
            36,
            true,
        );
        let action_y = settings_panel.bottom - 72;
        let _ = MoveWindow(
            GetDlgItem(hwnd, ID_PROGRESS),
            settings_x,
            action_y - 16,
            settings_content,
            3,
            true,
        );
        let _ = MoveWindow(
            GetDlgItem(hwnd, ID_HOME_ACTION),
            settings_x,
            action_y,
            settings_content,
            52,
            true,
        );
    } else {
        let _ = MoveWindow(
            GetDlgItem(hwnd, ID_CARD_HEADING),
            log.left + 16,
            log.top + 14,
            log.right - log.left - 32,
            24,
            true,
        );
        let _ = MoveWindow(
            GetDlgItem(hwnd, ID_DETAIL),
            log.left + 16,
            log.top + 44,
            log.right - log.left - 32,
            log.bottom - log.top - 56,
            true,
        );
        let _ = MoveWindow(
            GetDlgItem(hwnd, ID_STATUS),
            menu.left,
            menu.top,
            menu.right - menu.left,
            menu.bottom - menu.top,
            true,
        );
        let _ = MoveWindow(
            GetDlgItem(hwnd, ID_PROGRESS),
            action.left,
            action.top - 7,
            action.right - action.left,
            3,
            true,
        );
        let _ = MoveWindow(
            GetDlgItem(hwnd, ID_HOME_ACTION),
            action.left,
            action.top,
            action.right - action.left,
            action.bottom - action.top,
            true,
        );
    }
    for id in [ID_ACTION, ID_PLAY] {
        let _ = ShowWindow(GetDlgItem(hwnd, id), SW_HIDE);
    }
}

unsafe fn command(hwnd: HWND, state: &mut Window, id: i32, notification: u16) {
    let id = if id == ID_STATUS { ID_SETTINGS } else { id };
    match id {
        ID_SETTINGS => {
            state.settings_open = !state.settings_open;
            let mut rect = RECT::default();
            let _ = GetClientRect(hwnd, &mut rect);
            layout(hwnd, rect.right, rect.bottom, state.settings_open);
            rebuild_backdrop(hwnd, state, rect.right, rect.bottom);
            set_text(
                hwnd,
                ID_SETTINGS,
                if state.settings_open {
                    "BACK"
                } else {
                    "SETTINGS"
                },
            );
            let _ = InvalidateRect(hwnd, None, false);
            return;
        }
        ID_MINIMIZE => {
            let _ = ShowWindow(hwnd, SW_MINIMIZE);
            return;
        }
        ID_CLOSE => {
            let _ = SendMessageW(hwnd, WM_CLOSE, WPARAM(0), LPARAM(0));
            return;
        }
        ID_HOME_ACTION => {
            let target = {
                let m = state.model.lock().unwrap();
                if m.busy || m.runtime.is_some() {
                    return;
                }
                if m.settings.selected_game.is_none() {
                    ID_BROWSE
                } else if m.build.is_none()
                    || m.package.is_none()
                    || !matches!(m.patch, Some(PatchState::Current))
                    || m.update_available == Some(true)
                {
                    ID_ACTION
                } else {
                    ID_PLAY
                }
            };
            command(hwnd, state, target, BN_CLICKED as u16);
            return;
        }
        _ => {}
    }
    // Static and button click notifications both use zero; scope by control ID.
    if notification == STN_CLICKED as u16
        && matches!(id, ID_PATH_FIELD | ID_FPS_VALUE_FIELD)
    {
        let edit = match id {
            ID_PATH_FIELD => ID_PATH,
            ID_FPS_VALUE_FIELD => ID_FPS_VALUE,
            _ => return,
        };
        let _ = SetFocus(GetDlgItem(hwnd, edit));
        return;
    }
    if notification == EN_CHANGE as u16 && id == ID_PATH {
        {
            let mut m = state.model.lock().unwrap();
            if m.busy {
                return;
            }
            m.patch = None;
            m.fps = None;
            m.generation.fetch_add(1, Ordering::SeqCst);
        }
        update_view(hwnd, &state.model);
        return;
    }
    if notification == BN_CLICKED as u16 && id == ID_FPS_ENABLED {
        let _ = InvalidateRect(GetDlgItem(hwnd, ID_FPS_ENABLED), None, false);
        return;
    }
    if notification == BN_CLICKED as u16 && id == ID_MUSIC_MUTED {
        let muted = !music_checked(hwnd);
        let previous = state.model.lock().unwrap().settings.music_muted;
        let change = if muted {
            state.music.take();
            Ok(())
        } else if state.music.is_none() {
            crate::animation::Music::start(&state.music_path).map(|music| {
                state.music = Some(music);
            })
        } else {
            Ok(())
        };
        if change.is_err() {
            let _ = SendMessageW(
                GetDlgItem(hwnd, ID_MUSIC_MUTED),
                BM_SETCHECK,
                WPARAM(if !previous {
                    BST_CHECKED.0 as usize
                } else {
                    BST_UNCHECKED.0 as usize
                }),
                LPARAM(0),
            );
            append_log(hwnd, state, "Background music change failed.");
            return;
        }
        let mut m = state.model.lock().unwrap();
        m.settings.music_muted = muted;
        if let Err(e) = save_settings(&m.settings) {
            show_fatal(&format!("{e:#}"));
        }
        return;
    }
    match id {
        ID_BROWSE => match choose_folder(hwnd) {
            Ok(Some(path)) => {
                let text = path.display().to_string();
                {
                    let mut m = state.model.lock().unwrap();
                    m.settings.selected_game = Some(path);
                    m.fps = None;
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
        ID_FPS_ACTION => start_fps_change(hwnd, state),
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
        m.fps = None;
        m.generation.fetch_add(1, Ordering::SeqCst);
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
            let fps = match &game {
                Some(game) if crate::steam::valid_game_directory(game) => fps::inspect(game)?,
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
                fps,
                update,
            })
        })();
        post_event(hwnd, &events, Event::Work(Work { generation, result }));
    });
}
fn start_fps_change(hwnd: HWND, state: &mut Window) {
    unsafe {
        if !commit_inputs(hwnd, state) {
            return;
        }
    }
    let enabled = unsafe { fps_checked(hwnd) };
    let value = if enabled {
        let text = match unsafe { get_text(hwnd, ID_FPS_VALUE) } {
            Ok(text) => text,
            Err(e) => {
                show_fatal(&format!("{e:#}"));
                return;
            }
        };
        match text.parse::<i32>() {
            Ok(value) if value > 0 => Some(value),
            _ => {
                show_fatal("FPS must be a positive whole number.");
                return;
            }
        }
    } else {
        None
    };
    let (game, generation, events) = {
        let mut m = state.model.lock().unwrap();
        if m.busy {
            return;
        }
        if let Some(value) = value {
            m.settings.fps_value = value;
            if let Err(e) = save_settings(&m.settings) {
                show_fatal(&format!("{e:#}"));
                return;
            }
        }
        let game = match m.settings.selected_game.clone() {
            Some(game) => game,
            None => {
                show_fatal("Select a game folder");
                return;
            }
        };
        m.busy = true;
        let generation = m.generation.fetch_add(1, Ordering::SeqCst) + 1;
        (game, generation, m.events.clone())
    };
    unsafe {
        set_busy(
            hwnd,
            true,
            if enabled {
                "Applying FPS tweak…"
            } else {
                "Disabling FPS tweak…"
            },
        )
    };
    thread::spawn(move || {
        let result = (|| {
            if let Some(value) = value {
                fps::apply(&game, value)?;
            } else {
                fps::disable(&game)?;
            }
            Ok(WorkResult::FpsChanged(fps::inspect(&game)?))
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
    let log = match &work.result {
        Ok(WorkResult::Refresh { .. }) => "Check complete",
        Ok(WorkResult::Prepared { .. }) => "Setup complete",
        Ok(WorkResult::Restored) => "Retail files restored",
        Ok(WorkResult::FpsChanged(Some(value))) => {
            if *value > 0 {
                "FPS tweak applied"
            } else {
                "FPS tweak updated"
            }
        }
        Ok(WorkResult::FpsChanged(None)) => "FPS tweak disabled",
        Ok(WorkResult::Launched { .. }) => "Local backend is ready",
        Err(_) => "Operation failed",
    };
    match work.result {
        Ok(WorkResult::Refresh {
            build,
            package,
            patch,
            fps,
            update,
        }) => {
            m.build = build;
            m.package = package;
            m.patch = patch;
            m.fps = Some(fps);
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
        Ok(WorkResult::FpsChanged(fps)) => m.fps = Some(fps),
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
            append_log(hwnd, state, log);
            update_view(hwnd, &state.model);
            show_fatal(&format!("{e:#}"));
            return;
        }
    }
    drop(m);
    append_log(hwnd, state, log);
    update_view(hwnd, &state.model);
}

unsafe fn update_view(hwnd: HWND, model: &Arc<Mutex<Model>>) {
    let (update_available, runtime, busy, can_restore, can_launch, fps_status) = {
        let m = model.lock().unwrap();
        (
            m.update_available,
            m.runtime.is_some(),
            m.busy,
            m.settings.selected_game.is_some(),
            can_play(&m).is_ok(),
            m.fps,
        )
    };
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
    let fps_value = fps_status.flatten();
    let _ = SendMessageW(
        GetDlgItem(hwnd, ID_FPS_ENABLED),
        BM_SETCHECK,
        WPARAM(if fps_value.is_some() {
            BST_CHECKED.0 as usize
        } else {
            BST_UNCHECKED.0 as usize
        }),
        LPARAM(0),
    );
    set_text(
        hwnd,
        ID_FPS_STATUS,
        &match fps_status {
            Some(Some(value)) => format!("Applied: {value} FPS"),
            Some(None) => "Applied: off".to_owned(),
            None => "Not inspected".to_owned(),
        },
    );
    set_enabled(hwnd, ID_FPS_ENABLED, !busy && fps_status.is_some());
    set_enabled(hwnd, ID_FPS_VALUE, !busy && can_restore);
    set_enabled(hwnd, ID_FPS_ACTION, !busy && fps_status.is_some());
    let home_text = if busy {
        "WORKING…"
    } else if runtime {
        "RUNNING"
    } else if !can_restore {
        "SELECT GAME"
    } else if update_available == Some(true) {
        "UPDATE"
    } else if !can_launch {
        "SETUP"
    } else {
        "PLAY"
    };
    set_text(hwnd, ID_HOME_ACTION, home_text);
    set_enabled(hwnd, ID_HOME_ACTION, !busy && !runtime);
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
unsafe fn append_log(hwnd: HWND, state: &mut Window, text: &str) {
    if state.log.back().map_or(true, |last| last != text) {
        if state.log.len() == 32 {
            state.log.pop_front();
        }
        state.log.push_back(text.to_owned());
    }
    set_text(
        hwnd,
        ID_DETAIL,
        &state
            .log
            .iter()
            .rev()
            .take(7)
            .rev()
            .cloned()
            .collect::<Vec<_>>()
            .join("\r\n"),
    );
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
    let child = GetDlgItem(hwnd, id);
    let _ = EnableWindow(child, on);
    let _ = InvalidateRect(child, None, false);
}
unsafe fn music_checked(hwnd: HWND) -> bool {
    SendMessageW(
        GetDlgItem(hwnd, ID_MUSIC_MUTED),
        BM_GETCHECK,
        WPARAM(0),
        LPARAM(0),
    )
    .0 == BST_CHECKED.0 as isize
}
unsafe fn set_busy(hwnd: HWND, busy: bool, _text: &str) {
    for id in [
        ID_PATH,
        ID_BROWSE,
        ID_CHECK,
        ID_ACTION,
        ID_RESTORE,
        ID_PLAY,
        ID_HOME_ACTION,
        ID_FPS_ENABLED,
        ID_FPS_VALUE,
        ID_FPS_ACTION,
    ] {
        if busy || matches!(id, ID_PATH | ID_BROWSE | ID_CHECK) {
            set_enabled(hwnd, id, !busy)
        }
    }
    let progress = GetDlgItem(hwnd, ID_PROGRESS);
    if busy {
        let _ = SetTimer(hwnd, TIMER_PROGRESS, 30, None);
    } else {
        let _ = KillTimer(hwnd, TIMER_PROGRESS);
    }
    let _ = SetWindowLongPtrW(progress, GWLP_USERDATA, busy as isize);
    let _ = InvalidateRect(progress, None, false);
}
unsafe fn fps_checked(hwnd: HWND) -> bool {
    SendMessageW(
        GetDlgItem(hwnd, ID_FPS_ENABLED),
        BM_GETCHECK,
        WPARAM(0),
        LPARAM(0),
    )
    .0 == BST_CHECKED.0 as isize
}

fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(Some(0)).collect()
}

unsafe fn choose_folder(owner: HWND) -> Result<Option<PathBuf>> {
    let mut display_name = [0u16; 260];
    let info = BROWSEINFOW {
        hwndOwner: owner,
        pszDisplayName: PWSTR(display_name.as_mut_ptr()),
        lpszTitle: w!("Select the folder containing PGR.exe"),
        ulFlags: BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE | BIF_NONEWFOLDERBUTTON,
        ..Default::default()
    };
    let pidl = SHBrowseForFolderW(&info);
    if pidl.is_null() {
        return Ok(None);
    }
    let mut path_buffer = [0u16; 260];
    let resolved = SHGetPathFromIDListW(pidl, &mut path_buffer).as_bool();
    CoTaskMemFree(Some(pidl.cast()));
    if !resolved {
        anyhow::bail!("Selected folder has no filesystem path")
    }
    let length = path_buffer
        .iter()
        .position(|character| *character == 0)
        .unwrap_or(path_buffer.len());
    let path = PathBuf::from(String::from_utf16(&path_buffer[..length])?);
    if !crate::steam::valid_game_directory(&path) {
        anyhow::bail!("Selected folder does not contain PGR.exe")
    }
    Ok(Some(path))
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
unsafe fn theme_font(points: i32, weight: i32) -> HFONT {
    CreateFontW(
        -points,
        0,
        0,
        0,
        weight,
        0,
        0,
        0,
        DEFAULT_CHARSET.0 as u32,
        OUT_DEFAULT_PRECIS.0 as u32,
        CLIP_DEFAULT_PRECIS.0 as u32,
        CLEARTYPE_QUALITY.0 as u32,
        (DEFAULT_PITCH.0 | FF_SWISS.0) as u32,
        w!("Segoe UI"),
    )
}

unsafe fn delete_theme(state: &Window) {
    for object in [
        state.overlay.0,
        state.edit_brush.0,
        state.button_brush.0,
        state.button_hot_brush.0,
        state.accent_brush.0,
        state.muted_brush.0,
        state.header_brush.0,
        state.title_font.0,
        state.heading_font.0,
        state.body_font.0,
        state.label_font.0,
    ] {
        let _ = DeleteObject(HGDIOBJ(object));
    }
}

unsafe fn paint_control_backdrop(child: HWND, dc: HDC, state: &Window) {
    if state.backdrop.0 == 0 {
        return;
    }
    let parent = GetParent(child);
    let mut origin = POINT::default();
    let _ = ClientToScreen(child, &mut origin);
    let _ = ScreenToClient(parent, &mut origin);
    let mut rect = RECT::default();
    let _ = GetClientRect(child, &mut rect);
    let mem = CreateCompatibleDC(dc);
    let old = SelectObject(mem, state.backdrop);
    let _ = BitBlt(
        dc,
        0,
        0,
        rect.right,
        rect.bottom,
        mem,
        origin.x,
        origin.y,
        SRCCOPY,
    );
    let _ = SelectObject(mem, old);
    let _ = DeleteDC(mem);
}

unsafe fn render_backdrop(dc: HDC, state: &Window, width: i32, height: i32) {
    let source = CreateCompatibleDC(dc);
    let old = SelectObject(source, state.background);
    let mut bm = BITMAP::default();
    let _ = GetObjectW(
        state.background,
        size_of::<BITMAP>() as i32,
        Some((&mut bm as *mut BITMAP).cast()),
    );
    let (src_w, src_h) = if bm.bmWidth as i64 * height as i64 > bm.bmHeight as i64 * width as i64 {
        (bm.bmHeight * width / height, bm.bmHeight)
    } else {
        (bm.bmWidth, bm.bmWidth * height / width)
    };
    let overscan_x = (src_w / 200).max(1);
    let overscan_y = (src_h / 200).max(1);
    let _ = SetStretchBltMode(dc, HALFTONE);
    let _ = StretchBlt(
        dc,
        0,
        0,
        width,
        height,
        source,
        (bm.bmWidth - src_w) / 2 + overscan_x,
        (bm.bmHeight - src_h) / 2 + overscan_y,
        src_w - overscan_x * 2,
        src_h - overscan_y * 2,
        SRCCOPY,
    );
    let _ = SelectObject(source, state.overlay);
    let blend = BLENDFUNCTION {
        BlendOp: AC_SRC_OVER as u8,
        BlendFlags: 0,
        SourceConstantAlpha: 215,
        AlphaFormat: 0,
    };
    if state.settings_open {
        let dim = BLENDFUNCTION {
            SourceConstantAlpha: 135,
            ..blend
        };
        let _ = AlphaBlend(dc, 0, 54, width, height - 54, source, 0, 0, 1, 1, dim);
        let panel = settings_rect(width, height);
        let _ = AlphaBlend(
            dc,
            panel.left,
            panel.top,
            panel.right - panel.left,
            panel.bottom - panel.top,
            source,
            0,
            0,
            1,
            1,
            blend,
        );
    } else {
        let (log, _, _) = home_controls(width, height);
        let _ = AlphaBlend(
            dc,
            log.left,
            log.top,
            log.right - log.left,
            log.bottom - log.top,
            source,
            0,
            0,
            1,
            1,
            blend,
        );
    }
    let _ = FillRect(
        dc,
        &RECT {
            left: 0,
            top: 0,
            right: width,
            bottom: 54,
        },
        state.header_brush,
    );
    let _ = SelectObject(source, old);
    let _ = DeleteDC(source);
}

unsafe fn frame_bitmap(
    hwnd: HWND,
    sequence: u64,
    width: i32,
    height: i32,
    pixels: &[u8],
) -> Option<(u64, HBITMAP)> {
    let expected = (width as usize)
        .checked_mul(height as usize)?
        .checked_mul(4)?;
    if width <= 0 || height <= 0 || pixels.len() != expected {
        return None;
    }
    let info = BITMAPINFO {
        bmiHeader: BITMAPINFOHEADER {
            biSize: size_of::<BITMAPINFOHEADER>() as u32,
            biWidth: width,
            biHeight: -height,
            biPlanes: 1,
            biBitCount: 32,
            biCompression: BI_RGB.0,
            ..Default::default()
        },
        ..Default::default()
    };
    let dc = GetDC(hwnd);
    let mut bits = std::ptr::null_mut();
    let bitmap = CreateDIBSection(dc, &info, DIB_RGB_COLORS, &mut bits, None, 0);
    let _ = ReleaseDC(hwnd, dc);
    let bitmap = bitmap.ok()?;
    if bits.is_null() {
        let _ = DeleteObject(bitmap);
        return None;
    }
    std::ptr::copy_nonoverlapping(pixels.as_ptr(), bits.cast(), pixels.len());
    Some((sequence, bitmap))
}

unsafe fn rebuild_backdrop(hwnd: HWND, state: &mut Window, width: i32, height: i32) {
    if width <= 0 || height <= 0 {
        return;
    }
    let dc = GetDC(hwnd);
    let bitmap = CreateCompatibleBitmap(dc, width, height);
    if bitmap.0 != 0 {
        let mem = CreateCompatibleDC(dc);
        let old = SelectObject(mem, bitmap);
        render_backdrop(mem, state, width, height);
        let _ = SelectObject(mem, old);
        let _ = DeleteDC(mem);
        if state.backdrop.0 != 0 {
            let _ = DeleteObject(state.backdrop);
        }
        state.backdrop = bitmap;
        state.backdrop_size = SIZE {
            cx: width,
            cy: height,
        };
    }
    let _ = ReleaseDC(hwnd, dc);
}

unsafe fn draw_settings_icon(dc: HDC, background: HBRUSH, back: bool, pressed: bool) {
    let offset = pressed as i32;
    let old_pen = SelectObject(dc, GetStockObject(DC_PEN));
    let _ = SetDCPenColor(dc, COLORREF(0x00f4f1ef));
    if back {
        let _ = MoveToEx(dc, 27 + offset, 10 + offset, None);
        let _ = LineTo(dc, 19 + offset, 17 + offset);
        let _ = LineTo(dc, 27 + offset, 24 + offset);
        let _ = MoveToEx(dc, 19 + offset, 17 + offset, None);
        let _ = LineTo(dc, 32 + offset, 17 + offset);
    } else {
        let points = [
            POINT { x: 21, y: 7 },
            POINT { x: 25, y: 7 },
            POINT { x: 27, y: 11 },
            POINT { x: 31, y: 10 },
            POINT { x: 33, y: 14 },
            POINT { x: 30, y: 17 },
            POINT { x: 32, y: 21 },
            POINT { x: 28, y: 24 },
            POINT { x: 25, y: 22 },
            POINT { x: 22, y: 26 },
            POINT { x: 18, y: 24 },
            POINT { x: 18, y: 20 },
            POINT { x: 13, y: 19 },
            POINT { x: 13, y: 15 },
            POINT { x: 17, y: 13 },
            POINT { x: 17, y: 9 },
        ]
        .map(|point| POINT {
            x: point.x + offset,
            y: point.y + offset,
        });
        let old_brush = SelectObject(dc, GetStockObject(DC_BRUSH));
        let _ = SetDCBrushColor(dc, COLORREF(0x00f4f1ef));
        let _ = Polygon(dc, &points);
        let _ = SelectObject(dc, background);
        let _ = Ellipse(dc, 20 + offset, 14 + offset, 26 + offset, 20 + offset);
        let _ = SelectObject(dc, old_brush);
    }
    let _ = SelectObject(dc, old_pen);
}

unsafe fn draw_item(item: &DRAWITEMSTRUCT, state: &Window) {
    if item.CtlID as i32 == ID_PROGRESS {
        paint_control_backdrop(item.hwndItem, item.hDC, state);
        if GetWindowLongPtrW(item.hwndItem, GWLP_USERDATA) != 0 {
            let width = item.rcItem.right - item.rcItem.left;
            let segment = (width * 28 / 100).max(24);
            let left = item.rcItem.left + state.progress_phase * (width + segment) / 140 - segment;
            let active = RECT {
                left: left.max(item.rcItem.left),
                top: item.rcItem.top,
                right: (left + segment).min(item.rcItem.right),
                bottom: item.rcItem.bottom,
            };
            if active.right > active.left {
                let _ = FillRect(item.hDC, &active, state.accent_brush);
            }
        }
        return;
    }
    let disabled = item.itemState.0 & ODS_DISABLED.0 != 0;
    let pressed = item.itemState.0 & ODS_SELECTED.0 != 0;
    let hot = item.itemState.0 & ODS_HOTLIGHT.0 != 0
        || GetWindowLongPtrW(item.hwndItem, GWLP_USERDATA) != 0;
    let primary = item.CtlID as i32 == ID_HOME_ACTION;
    let header_control = matches!(item.CtlID as i32, ID_SETTINGS | ID_MINIMIZE | ID_CLOSE)
        || (item.CtlID as i32 == ID_CHECK && !state.settings_open);
    let brush = if disabled {
        state.muted_brush
    } else if hot || pressed {
        state.button_hot_brush
    } else if primary {
        state.accent_brush
    } else if header_control {
        state.header_brush
    } else {
        state.button_brush
    };
    let _ = FillRect(item.hDC, &item.rcItem, brush);
    if item.CtlID as i32 == ID_SETTINGS {
        draw_settings_icon(item.hDC, brush, state.settings_open, pressed);
        return;
    }
    let mut text = [0u16; 64];
    let length = match item.CtlID as i32 {
        ID_STATUS => {
            text[0] = '≡' as u16;
            1
        }
        ID_MINIMIZE => {
            text[0] = '—' as u16;
            1
        }
        ID_CLOSE => {
            text[0] = '×' as u16;
            1
        }
        _ => GetWindowTextW(item.hwndItem, &mut text),
    };
    let _ = SetBkMode(item.hDC, TRANSPARENT);
    let dark_primary = primary && !hot && !pressed && !disabled;
    let _ = SetTextColor(
        item.hDC,
        if dark_primary {
            COLORREF(0x00201a18)
        } else if disabled {
            COLORREF(0x009a918d)
        } else {
            COLORREF(0x00f4f1ef)
        },
    );
    let old_font = SelectObject(
        item.hDC,
        if primary {
            state.heading_font
        } else {
            state.body_font
        },
    );
    let mut text_rect = item.rcItem;
    if pressed {
        let _ = OffsetRect(&mut text_rect, 1, 1);
    }
    let mut flags = DT_CENTER | DT_VCENTER | DT_SINGLELINE;
    if item.itemState.0 & ODS_NOACCEL.0 != 0 {
        flags |= DT_HIDEPREFIX;
    }
    let _ = DrawTextW(
        item.hDC,
        &mut text[..length as usize],
        &mut text_rect,
        flags,
    );
    let _ = SelectObject(item.hDC, old_font);
    if item.itemState.0 & ODS_FOCUS.0 != 0 {
        let mut focus = item.rcItem;
        let _ = InflateRect(&mut focus, -5, -5);
        let _ = DrawFocusRect(item.hDC, &focus);
    }
}

unsafe fn paint(hwnd: HWND, state: &Window) {
    let mut ps = PAINTSTRUCT::default();
    let dc = BeginPaint(hwnd, &mut ps);
    if state.backdrop.0 != 0 {
        let mem = CreateCompatibleDC(dc);
        let old = SelectObject(mem, state.backdrop);
        let _ = BitBlt(
            dc,
            0,
            0,
            state.backdrop_size.cx,
            state.backdrop_size.cy,
            mem,
            0,
            0,
            SRCCOPY,
        );
        let _ = SelectObject(mem, old);
        let _ = DeleteDC(mem);
    }
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

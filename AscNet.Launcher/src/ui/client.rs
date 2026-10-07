//! "Get game" / client management view: language (region) picker, folder picker, and the install / verify & repair /
//! update / adopt jobs of `download`, run on the launcher's worker/event mechanism.
use super::*;
use ascnet_launcher::download::{self, Detected, Job, Outcome, Phase, Plan, Progress, Region};
use std::{
    mem::discriminant,
    sync::atomic::AtomicBool,
    time::Instant,
};

pub(super) const ID_LANG_LABEL: i32 = 140;
/// Five consecutive IDs, one per entry of `REGIONS`.
pub(super) const ID_LANG: i32 = 141;
pub(super) const ID_HELP: i32 = 146;
pub(super) const ID_FOLDER_LABEL: i32 = 147;
pub(super) const ID_FOLDER_FIELD: i32 = 148;
pub(super) const ID_FOLDER: i32 = 149;
pub(super) const ID_CHOOSE: i32 = 150;
pub(super) const ID_INFO: i32 = 151;
/// Three consecutive IDs; their label/action depend on the folder state (`slots`).
pub(super) const ID_ACT: i32 = 152;
/// Opens this view from the settings panel.
pub(super) const ID_OPEN: i32 = 155;

const REGIONS: [Region; 5] = [Region::En, Region::Tw, Region::Kr, Region::Jp, Region::Cn];
const HELP: &str = "Each language is its own Kuro client (简体中文 is the mainland China client). The game downloads its remaining resources itself on its first start.";
const STATE_DIR: &str = ".ascnet-download";

fn names(region: Region) -> (&'static str, &'static str) {
    match region {
        Region::En => ("English", "Global"),
        Region::Tw => ("繁體中文", "Taiwan"),
        Region::Kr => ("한국어", "Korea"),
        Region::Jp => ("日本語", "Japan"),
        Region::Cn => ("简体中文", "China"),
    }
}
fn full_name(region: Region) -> String {
    let (name, place) = names(region);
    format!("{name} ({place})")
}

/// Windows UI language -> client edition (zh-CN/zh-SG get the mainland client, other Chinese the Taiwan one).
pub(super) fn region_for_langid(langid: u16) -> Region {
    match (langid & 0x3ff, langid) {
        (0x12, _) => Region::Kr,
        (0x11, _) => Region::Jp,
        (0x04, 0x0804 | 0x1004) => Region::Cn,
        (0x04, _) => Region::Tw,
        _ => Region::En,
    }
}
pub(super) fn default_region() -> Region {
    region_for_langid(unsafe { windows::Win32::Globalization::GetUserDefaultUILanguage() })
}

#[derive(Clone, Copy, PartialEq)]
enum Kind {
    Install,
    Repair,
    Update,
    Adopt,
}
impl Kind {
    fn job(self, dir: PathBuf) -> Job {
        match self {
            Kind::Install => Job::Install { dir },
            Kind::Repair => Job::Repair { dir },
            Kind::Update => Job::Update { dir },
            Kind::Adopt => Job::Adopt { dir },
        }
    }
    fn running_text(self) -> &'static str {
        match self {
            Kind::Install => "Downloading the game",
            Kind::Repair => "Verifying and repairing the game",
            Kind::Update => "Updating the game",
            Kind::Adopt => "Adopting the existing install",
        }
    }
}

enum Folder {
    None,
    Empty,
    /// Interrupted download: no PGR.exe yet, but the engine's state directory is there.
    Resumable,
    Game,
    Invalid(String),
}

#[derive(Clone, Copy)]
enum Action {
    Start(Kind),
    Pause,
    Cancel,
    Busy,
}
#[derive(Clone, Copy)]
struct Slot {
    label: &'static str,
    action: Action,
    enabled: bool,
    primary: bool,
}

enum Inflight {
    Inspect,
    Run(Kind),
}
#[derive(PartialEq)]
enum Stop {
    Pause,
    Cancel,
}

pub(super) enum Done {
    Inspected {
        dir: PathBuf,
        detected: Option<Result<Detected>>,
        plan: Option<Result<Plan>>,
        pinned: Option<String>,
    },
    Finished(Outcome),
    Stopped,
}

pub(super) struct State {
    pub open: bool,
    region: Region,
    dir: Option<PathBuf>,
    folder: Folder,
    detected: Option<Detected>,
    plan_line: Option<String>,
    pinned: Option<String>,
    inflight: Option<Inflight>,
    paused: Option<Kind>,
    stop: Option<Stop>,
    cancel: Arc<AtomicBool>,
    fraction: Option<(u64, u64)>,
    progress_text: String,
    last_phase: Option<std::mem::Discriminant<Phase>>,
    slots: [Option<Slot>; 3],
}
impl State {
    pub fn new(region: Region) -> Self {
        Self {
            open: false,
            region,
            dir: None,
            folder: Folder::None,
            detected: None,
            plan_line: None,
            pinned: None,
            inflight: None,
            paused: None,
            stop: None,
            cancel: Arc::new(AtomicBool::new(false)),
            fraction: None,
            progress_text: String::new(),
            last_phase: None,
            slots: [None; 3],
        }
    }
    /// A detected edition wins over the picker: verifying a TW install against EN files would "repair" it into EN.
    fn effective_region(&self) -> Region {
        match (&self.folder, self.detected.as_ref().and_then(|d| d.region)) {
            (Folder::Game, Some(region)) => region,
            _ => self.region,
        }
    }
    fn locked_region(&self) -> bool {
        matches!(self.folder, Folder::Game) && self.detected.as_ref().is_some_and(|d| d.region.is_some())
    }
    pub fn is_primary(&self, id: i32) -> bool {
        (ID_ACT..ID_ACT + 3).contains(&id) && self.slots[(id - ID_ACT) as usize].is_some_and(|s| s.primary)
    }
    /// Determinate fill for the shared progress bar; `None` falls back to the busy marquee.
    pub fn fraction(&self) -> Option<(u64, u64)> {
        self.fraction.filter(|&(_, total)| total > 0 && self.open)
    }
    pub fn working(&self) -> bool {
        self.inflight.is_some()
    }
}

pub(super) unsafe fn hide(hwnd: HWND) {
    for id in view_ids() {
        let _ = ShowWindow(GetDlgItem(hwnd, id), SW_HIDE);
    }
}

pub(super) unsafe fn create(hwnd: HWND, state: &Window) {
    let hidden_static = |id: i32, text: PCWSTR, style: u32, font: HFONT| {
        control(hwnd, w!("STATIC"), text, WINDOW_STYLE(style), id, 0, 0, 0, 0);
        let child = GetDlgItem(hwnd, id);
        let _ = SetWindowSubclass(child, Some(backdrop_control_subclass), 1, 0);
        let _ = SendMessageW(child, WM_SETFONT, WPARAM(font.0 as usize), LPARAM(1));
    };
    hidden_static(ID_LANG_LABEL, w!("LANGUAGE"), 0, state.label_font);
    hidden_static(ID_HELP, w!(""), 0, state.label_font);
    hidden_static(ID_FOLDER_LABEL, w!("INSTALL FOLDER"), 0, state.label_font);
    hidden_static(ID_INFO, w!(""), 0, state.body_font);
    control(hwnd, w!("STATIC"), PCWSTR::null(), WINDOW_STYLE(SS_NOTIFY.0), ID_FOLDER_FIELD, 0, 0, 0, 0);
    control(
        hwnd,
        w!("EDIT"),
        PCWSTR::null(),
        WS_TABSTOP | WINDOW_STYLE((ES_AUTOHSCROLL | ES_CENTER | ES_READONLY) as u32),
        ID_FOLDER,
        0,
        0,
        0,
        0,
    );
    let _ = SendMessageW(GetDlgItem(hwnd, ID_FOLDER), WM_SETFONT, WPARAM(state.body_font.0 as usize), LPARAM(1));
    let button = WS_TABSTOP | WINDOW_STYLE((BS_OWNERDRAW | BS_FLAT) as u32);
    let mut buttons = vec![
        (ID_CHOOSE, w!("&Choose…"), state.body_font),
        (ID_OPEN, w!("&Game client…"), state.body_font),
    ];
    for i in 0..REGIONS.len() as i32 {
        buttons.push((ID_LANG + i, w!(""), state.body_font));
    }
    for i in 0..3 {
        buttons.push((ID_ACT + i, w!(""), state.body_font));
    }
    for (id, text, font) in buttons {
        control(hwnd, w!("BUTTON"), text, button, id, 0, 0, 0, 0);
        let child = GetDlgItem(hwnd, id);
        let _ = SetWindowSubclass(child, Some(button_subclass), 1, 0);
        let _ = SendMessageW(child, WM_SETFONT, WPARAM(font.0 as usize), LPARAM(1));
    }
    set_text(hwnd, ID_HELP, HELP);
}

/// Every control owned by this view (visibility is switched as a group).
fn view_ids() -> Vec<i32> {
    let mut ids = vec![ID_LANG_LABEL, ID_HELP, ID_FOLDER_LABEL, ID_FOLDER_FIELD, ID_FOLDER, ID_CHOOSE, ID_INFO];
    ids.extend(ID_LANG..ID_LANG + REGIONS.len() as i32);
    ids.extend(ID_ACT..ID_ACT + 3);
    ids
}

/// Called from `layout`: positions this view's controls and hides everything the home/settings views own.
pub(super) unsafe fn layout(hwnd: HWND, width: i32, height: i32) {
    for id in [
        ID_STATUS, ID_DETAIL, ID_STATUS_LINE, ID_HOME_ACTION, ID_CHECK, ID_PATH_LABEL, ID_PATH_FIELD, ID_PATH,
        ID_BROWSE, ID_RESTORE, ID_RERUN_SETUP, ID_OPEN, ID_FPS_ENABLED, ID_FPS_VALUE_FIELD, ID_FPS_VALUE, ID_FPS_ACTION,
        ID_FPS_STATUS, ID_MUSIC_MUTED, ID_GAME_TWEAKS, ID_LAUNCHER_HEADING, ID_FPS_LABEL, ID_MUSIC_LABEL,
        ID_NOFADE, ID_NOFADE_LABEL, ID_FPS_UNIT,
    ] {
        let _ = ShowWindow(GetDlgItem(hwnd, id), SW_HIDE);
    }
    for id in view_ids().into_iter().chain([ID_SETTINGS, ID_CARD_HEADING, ID_PROGRESS]) {
        let _ = ShowWindow(GetDlgItem(hwnd, id), SW_SHOW);
    }
    let panel = settings_rect(width, height);
    let x = panel.left + 40;
    let content = panel.right - panel.left - 80;
    let top = panel.top;
    let place = |id: i32, x: i32, y: i32, w: i32, h: i32| {
        let _ = MoveWindow(GetDlgItem(hwnd, id), x, y, w, h, true);
    };
    place(ID_CARD_HEADING, x, top + 20, content, 30);
    place(ID_LANG_LABEL, x, top + 58, content, 20);
    let gap = 10;
    let lang_w = (content - (REGIONS.len() as i32 - 1) * gap) / REGIONS.len() as i32;
    for i in 0..REGIONS.len() as i32 {
        place(ID_LANG + i, x + i * (lang_w + gap), top + 80, lang_w, 54);
    }
    place(ID_HELP, x, top + 140, content, 36);
    place(ID_FOLDER_LABEL, x, top + 184, content, 20);
    let choose_w = 112;
    let field_w = content - choose_w - 12;
    place(ID_FOLDER_FIELD, x, top + 208, field_w, 40);
    place(ID_FOLDER, x, top + 208 + (40 - CENTERED_EDIT_HEIGHT) / 2, field_w, CENTERED_EDIT_HEIGHT);
    place(ID_CHOOSE, x + content - choose_w, top + 208, choose_w, 40);
    let action_y = panel.bottom - 72;
    place(ID_INFO, x, top + 260, content, action_y - 24 - (top + 260));
    place(ID_PROGRESS, x, action_y - 16, content, 3);
}

fn classify(dir: &Path) -> Folder {
    if crate::steam::valid_game_directory(dir) {
        return Folder::Game;
    }
    if dir.join(STATE_DIR).is_dir() {
        return Folder::Resumable;
    }
    match fs::read_dir(dir).map(|mut entries| entries.next().is_none()) {
        Ok(true) => Folder::Empty,
        Ok(false) => Folder::Invalid("This folder is not empty and has no PGR.exe. Choose an empty folder for a new install, or the folder of an existing game.".to_owned()),
        Err(e) => Folder::Invalid(format!("Cannot read this folder: {e}")),
    }
}

fn fmt_bytes(bytes: u64) -> String {
    const GIB: f64 = 1024.0 * 1024.0 * 1024.0;
    let b = bytes as f64;
    if b >= GIB {
        format!("{:.1} GB", b / GIB)
    } else {
        format!("{:.0} MB", b / (1024.0 * 1024.0))
    }
}

fn plan_line(plan: &Plan) -> String {
    let mut line = format!(
        "Download {} · {} files · needs {} free",
        fmt_bytes(plan.download_bytes),
        plan.files,
        fmt_bytes(plan.required_free_bytes)
    );
    if plan.delete > 0 {
        line += &format!(" · {} old files removed", plan.delete);
    }
    line += &format!(" ({} available)", fmt_bytes(plan.free_bytes));
    if let Some(note) = &plan.note {
        line += &format!("\n{note}");
    }
    line
}

fn info_text(s: &State) -> String {
    let mut text = match (&s.folder, &s.dir) {
        (Folder::None, _) | (_, None) => {
            "Choose an empty folder to download the game, or the folder of an existing install to verify, update or adopt it."
                .to_owned()
        }
        (Folder::Invalid(message), _) => message.clone(),
        (Folder::Empty, _) => format!("Empty folder: a full {} client will be installed here.", full_name(s.region)),
        (Folder::Resumable, _) => "An interrupted download was found here; it resumes from the files already verified.".to_owned(),
        (Folder::Game, _) => match &s.detected {
            None => "Inspecting the install…".to_owned(),
            Some(d) => {
                let mut parts = vec![
                    d.region.map_or("Unidentified edition".to_owned(), full_name),
                    d.version.clone().map_or("unknown version".to_owned(), |v| format!("version {v}")),
                ];
                if d.steam {
                    parts.push("Steam copy".to_owned());
                }
                if d.launcher_patched {
                    parts.push("AscNet patch installed".to_owned());
                }
                let mut text = parts.join(" · ");
                if d.region.is_none() {
                    text += &format!("\nVerifying treats it as {}; change the language above if that is wrong.", full_name(s.region));
                }
                text
            }
        },
    };
    if let Some(line) = &s.plan_line {
        text += &format!("\n{line}");
    }
    text
}

fn selected_game(state: &Window) -> Option<PathBuf> {
    state.model.lock().unwrap().settings.selected_game.clone()
}

fn compute_slots(s: &State, busy: bool, selected: Option<&Path>) -> [Option<Slot>; 3] {
    let slot = |label, action, enabled, primary| Some(Slot { label, action, enabled, primary });
    if busy {
        return match s.inflight {
            Some(Inflight::Run(_)) => [slot("PAUSE", Action::Pause, true, false), slot("CANCEL", Action::Cancel, true, false), None],
            _ => [slot("WORKING…", Action::Busy, false, false), None, None],
        };
    }
    if let Some(kind) = s.paused {
        return [slot("RESUME", Action::Start(kind), true, true), slot("CANCEL", Action::Cancel, true, false), None];
    }
    match s.folder {
        Folder::Empty | Folder::Resumable => [slot("DOWNLOAD", Action::Start(Kind::Install), true, true), None, None],
        Folder::Game => {
            let older = match (s.detected.as_ref().and_then(|d| d.version.as_deref()), s.pinned.as_deref()) {
                (Some(have), Some(want)) => package::compare_versions(have, want).is_ok_and(|o| o == std::cmp::Ordering::Less),
                _ => false,
            };
            let new_to_launcher = s.dir.as_deref() != selected;
            let ready = s.detected.is_some();
            [
                slot("VERIFY && REPAIR", Action::Start(Kind::Repair), ready, ready && !older && !new_to_launcher),
                slot("UPDATE", Action::Start(Kind::Update), ready && older, ready && older),
                slot("ADOPT", Action::Start(Kind::Adopt), ready && new_to_launcher, ready && new_to_launcher && !older),
            ]
        }
        _ => [slot("DOWNLOAD", Action::Busy, false, false), None, None],
    }
}

/// Re-applies state to the controls.
pub(super) unsafe fn refresh(hwnd: HWND, state: &mut Window) {
    let (busy, selected) = {
        let m = state.model.lock().unwrap();
        (m.busy, m.settings.selected_game.clone())
    };
    let c = &mut state.client;
    c.slots = compute_slots(c, busy, selected.as_deref());
    let locked = busy || c.locked_region();
    for i in 0..4 {
        set_enabled(hwnd, ID_LANG + i, !locked);
    }
    set_enabled(hwnd, ID_CHOOSE, !busy);
    set_text(hwnd, ID_FOLDER, &c.dir.as_deref().map(|p| p.display().to_string()).unwrap_or_default());
    if c.paused.is_some() && !c.progress_text.is_empty() {
        let text = format!("Paused · {}", c.progress_text);
        set_text(hwnd, ID_INFO, &text);
    } else if !(busy && matches!(c.inflight, Some(Inflight::Run(_)))) {
        let text = info_text(c);
        set_text(hwnd, ID_INFO, &text);
    }
    let mut rect = RECT::default();
    let _ = GetClientRect(hwnd, &mut rect);
    let panel = settings_rect(rect.right, rect.bottom);
    let x = panel.left + 40;
    let content = panel.right - panel.left - 80;
    let count = c.slots.iter().flatten().count() as i32;
    for (i, slot) in c.slots.iter().enumerate() {
        let id = ID_ACT + i as i32;
        let child = GetDlgItem(hwnd, id);
        match slot {
            Some(slot) if c.open => {
                set_text(hwnd, id, slot.label);
                let width = (content - (count - 1) * 12) / count;
                let _ = MoveWindow(child, x + i as i32 * (width + 12), panel.bottom - 72, width, 52, true);
                set_enabled(hwnd, id, slot.enabled);
                let _ = ShowWindow(child, SW_SHOW);
            }
            _ => {
                let _ = ShowWindow(child, SW_HIDE);
            }
        }
    }
    let _ = InvalidateRect(GetDlgItem(hwnd, ID_PROGRESS), None, false);
}

unsafe fn relayout(hwnd: HWND, state: &mut Window) {
    let mut rect = RECT::default();
    let _ = GetClientRect(hwnd, &mut rect);
    super::layout(hwnd, rect.right, rect.bottom, state.settings_open, state.client.open);
    rebuild_backdrop(hwnd, state, rect.right, rect.bottom);
    set_text(hwnd, ID_SETTINGS, if state.settings_open || state.client.open { "BACK" } else { "SETTINGS" });
    let _ = InvalidateRect(hwnd, None, false);
}

pub(super) unsafe fn open(hwnd: HWND, state: &mut Window) {
    if state.model.lock().unwrap().busy && !state.client.working() {
        return;
    }
    state.settings_open = false;
    state.client.open = true;
    set_text(hwnd, ID_CARD_HEADING, "GET GAME");
    // Offer the folder the player already selected so Verify / Update act on it directly.
    if state.client.dir.is_none() {
        if let Some(game) = selected_game(state) {
            set_folder(hwnd, state, game);
        }
    }
    relayout(hwnd, state);
    refresh(hwnd, state);
}

pub(super) unsafe fn close(hwnd: HWND, state: &mut Window) {
    state.client.open = false;
    relayout(hwnd, state);
    update_view(hwnd, &state.model);
}

unsafe fn set_folder(hwnd: HWND, state: &mut Window, dir: PathBuf) {
    let c = &mut state.client;
    c.folder = classify(&dir);
    c.dir = Some(dir);
    c.detected = None;
    c.plan_line = None;
    c.paused = None;
    c.fraction = None;
    c.progress_text.clear();
    refresh(hwnd, state);
    if matches!(state.client.folder, Folder::Empty | Folder::Resumable | Folder::Game) {
        start_inspect(hwnd, state);
    }
}

/// Detects the install and prices the job it would need; all network work stays off the UI thread.
unsafe fn start_inspect(hwnd: HWND, state: &mut Window) {
    let Some(dir) = state.client.dir.clone() else { return };
    let (generation, events) = {
        let mut m = state.model.lock().unwrap();
        if m.busy {
            return;
        }
        m.busy = true;
        (m.generation.fetch_add(1, Ordering::SeqCst) + 1, m.events.clone())
    };
    let c = &mut state.client;
    c.inflight = Some(Inflight::Inspect);
    let region = c.region;
    let game = matches!(c.folder, Folder::Game);
    set_busy(hwnd, true, "Inspecting…");
    refresh(hwnd, state);
    thread::spawn(move || {
        let result = (|| {
            let meta = metadata_path()?;
            let detected = game.then(|| download::detect(&dir));
            if let Some(detected) = &detected {
                let _ = local::launcher_log(&match detected {
                    Ok(d) => format!("Client inspect: dir={} region={:?} version={:?}", dir.display(), d.region.map(full_name), d.version),
                    Err(e) => format!("Client inspect: dir={} region detection failed: {e:#}", dir.display()),
                });
            }
            let region = detected.as_ref().and_then(|d| d.as_ref().ok()).and_then(|d| d.region).unwrap_or(region);
            let source = download::sources(&meta)?
                .into_iter()
                .find(|s| s.region() == region)
                .with_context(|| format!("no pinned client for {}", full_name(region)))?;
            let pinned = Some(source.version().to_owned());
            let job = match detected.as_ref().and_then(|d| d.as_ref().ok()) {
                None => Some(Job::Install { dir: dir.clone() }).filter(|_| !game),
                Some(d) => d
                    .version
                    .as_deref()
                    .filter(|v| package::compare_versions(v, source.version()).is_ok_and(|o| o == std::cmp::Ordering::Less))
                    .map(|_| Job::Update { dir: dir.clone() }),
            };
            let plan = job.map(|job| download::plan(&source, job));
            Ok(WorkResult::Client(Done::Inspected { dir, detected, plan, pinned }))
        })();
        post_event(hwnd, &events, Event::Work(Work { generation, result }));
    });
}

fn metadata_path() -> Result<PathBuf> {
    Ok(env::current_exe()?
        .parent()
        .context("launcher executable has no parent")?
        .join("supported-client.json"))
}

unsafe fn start_job(hwnd: HWND, state: &mut Window, kind: Kind) {
    let Some(dir) = state.client.dir.clone() else { return };
    match install::game_running() {
        Ok(true) => return show_fatal("Close PGR before changing its files."),
        Err(e) => return show_fatal(&format!("{e:#}")),
        Ok(false) => {}
    }
    if kind != Kind::Install
        && state.client.paused.is_none()
        && MessageBoxW(
            hwnd,
            w!("Every file in this folder is checked against Kuro's official files; files that differ are downloaded again. Files patched by AscNet are handled by the launcher's own patch state.\r\n\r\nContinue?"),
            w!("Verify game files"),
            MB_OKCANCEL | MB_ICONINFORMATION,
        ) != IDOK
    {
        return;
    }
    let (generation, events) = {
        let mut m = state.model.lock().unwrap();
        if m.busy || m.runtime.is_some() {
            return;
        }
        m.busy = true;
        (m.generation.fetch_add(1, Ordering::SeqCst) + 1, m.events.clone())
    };
    let c = &mut state.client;
    let region = c.effective_region();
    let cancel = c.cancel.clone();
    cancel.store(false, Ordering::SeqCst);
    c.inflight = Some(Inflight::Run(kind));
    c.paused = None;
    c.stop = None;
    c.last_phase = None;
    c.fraction = Some((0, 0));
    c.progress_text = format!("{}…", kind.running_text());
    set_text(hwnd, ID_INFO, &c.progress_text.clone());
    set_busy(hwnd, true, "");
    append_log(hwnd, state, &format!("{} ({})", kind.running_text(), full_name(region)));
    refresh(hwnd, state);
    thread::spawn(move || {
        let action = format!("Client {}", kind.running_text());
        let _ = local::launcher_log(&format!("{action}: dir={} region={}", dir.display(), full_name(region)));
        let result = (|| {
            let source = download::sources(&metadata_path()?)?
                .into_iter()
                .find(|s| s.region() == region)
                .with_context(|| format!("no pinned client for {}", full_name(region)))?;
            let plan = download::plan(&source, kind.job(dir))?;
            let mut last = (Instant::now(), None);
            download::run(plan, &cancel, &mut |p: Progress| {
                // Phase changes always go through; steady progress at most ~4 times a second.
                let phase = discriminant(&p.phase);
                if last.1 != Some(phase) || last.0.elapsed() >= Duration::from_millis(250) {
                    last = (Instant::now(), Some(phase));
                    post_event(hwnd, &events, Event::Download(p));
                }
            })
        })();
        let result = match result {
            Ok(outcome) => Ok(WorkResult::Client(Done::Finished(outcome))),
            Err(e) if cancel.load(Ordering::SeqCst) || e.downcast_ref::<download::Cancelled>().is_some() => Ok(WorkResult::Client(Done::Stopped)),
            Err(e) => Err(e),
        };
        let _ = local::launcher_log(&match &result {
            Ok(WorkResult::Client(Done::Stopped)) => format!("{action}: stopped"),
            Ok(_) => format!("{action}: ok"),
            Err(e) => format!("{action}: failed: {e:#}"),
        });
        post_event(hwnd, &events, Event::Work(Work { generation, result }));
    });
}

pub(super) unsafe fn on_progress(hwnd: HWND, state: &mut Window, p: Progress) {
    let c = &mut state.client;
    if !matches!(c.inflight, Some(Inflight::Run(_))) {
        return;
    }
    let label = match p.phase {
        Phase::FetchingIndex => "Fetching file list",
        Phase::Verifying => "Verifying files",
        Phase::Downloading => "Downloading",
        Phase::Extracting => "Extracting patch files",
        Phase::Deleting => "Removing old files",
        Phase::Finalizing => "Finalizing",
    };
    c.fraction = Some(if p.bytes_total > 0 {
        (p.bytes_done, p.bytes_total)
    } else {
        (p.files_done as u64, p.files_total as u64)
    });
    let mut text = format!("{label} · {}/{} files", p.files_done, p.files_total);
    if p.bytes_total > 0 {
        text += &format!(" · {} / {}", fmt_bytes(p.bytes_done), fmt_bytes(p.bytes_total));
    }
    if p.bytes_per_second > 0 {
        text += &format!(" · {}/s", fmt_bytes(p.bytes_per_second));
    }
    if let Some(current) = &p.current {
        text += &format!("\n{current}");
    }
    c.progress_text = text.clone();
    let new_phase = c.last_phase != Some(discriminant(&p.phase));
    c.last_phase = Some(discriminant(&p.phase));
    set_text(hwnd, ID_INFO, &text);
    let _ = InvalidateRect(GetDlgItem(hwnd, ID_PROGRESS), None, false);
    if new_phase {
        append_log(hwnd, state, &format!("{label}…"));
    }
}

/// Handles every worker result while this view has work in flight (success, stop, or error).
pub(super) unsafe fn finish(hwnd: HWND, state: &mut Window, work: Work) {
    {
        let mut m = state.model.lock().unwrap();
        if work.generation != m.generation.load(Ordering::SeqCst) {
            return;
        }
        m.busy = false;
    }
    let inflight = state.client.inflight.take();
    let kind = match inflight {
        Some(Inflight::Run(kind)) => Some(kind),
        _ => None,
    };
    match work.result {
        Ok(WorkResult::Client(Done::Inspected { dir, detected, plan, pinned })) => {
            let c = &mut state.client;
            if c.dir.as_deref() == Some(dir.as_path()) {
                c.pinned = pinned;
                let mut notes = Vec::new();
                match detected {
                    Some(Ok(d)) => {
                        if let Some(region) = d.region {
                            c.region = region;
                        }
                        c.detected = Some(d);
                    }
                    Some(Err(e)) => notes.push(format!("Could not inspect the install: {e:#}")),
                    None => {}
                }
                c.plan_line = match plan {
                    Some(Ok(plan)) => Some(plan_line(&plan)),
                    Some(Err(e)) => Some(format!("{e:#}")),
                    None => notes.pop(),
                };
                if matches!(c.folder, Folder::Game) && c.detected.is_none() {
                    // Detection failed: still allow verify against the picked language.
                    c.detected = Some(Detected { region: None, version: None, steam: false, launcher_patched: false });
                }
                let region = c.region;
                let mut m = state.model.lock().unwrap();
                if m.settings.region != Some(region) && matches!(c.folder, Folder::Game) {
                    m.settings.region = Some(region);
                    let _ = save_settings(&m.settings);
                }
            }
            set_busy(hwnd, false, "");
            refresh(hwnd, state);
        }
        Ok(WorkResult::Client(Done::Finished(outcome))) => {
            let dir = state.client.dir.clone();
            state.client.fraction = None;
            let mut message = format!(
                "Game files ready: {} repaired, {} downloaded, {} removed",
                outcome.repaired,
                fmt_bytes(outcome.downloaded_bytes),
                outcome.deleted
            );
            if !outcome.skipped_variants.is_empty() {
                message += &format!(" (left as is: {})", outcome.skipped_variants.join(", "));
            }
            append_log(hwnd, state, &message);
            let Some(dir) = dir.filter(|d| crate::steam::valid_game_directory(d)) else {
                set_busy(hwnd, false, "");
                refresh(hwnd, state);
                return;
            };
            {
                let mut m = state.model.lock().unwrap();
                m.settings.selected_game = Some(dir.clone());
                m.settings.region = Some(state.client.region);
                m.patch = None;
                m.generation.fetch_add(1, Ordering::SeqCst);
                if let Err(e) = save_settings(&m.settings) {
                    show_fatal(&format!("{e:#}"));
                }
            }
            set_text(hwnd, ID_PATH, &dir.display().to_string());
            state.client.open = false;
            relayout(hwnd, state);
            // Hand over to the normal flow: re-inspect the build/patch, then SETUP -> patch install -> PLAY.
            start_refresh(hwnd, true, false);
            append_log(hwnd, state, "Next: press SETUP to build the local server and patch");
        }
        Ok(WorkResult::Client(Done::Stopped)) => {
            let c = &mut state.client;
            let paused = c.stop.take() == Some(Stop::Pause);
            c.paused = kind.filter(|_| paused);
            if !paused {
                c.fraction = None;
            }
            set_busy(hwnd, false, "");
            append_log(
                hwnd,
                state,
                if paused { "Paused; press RESUME to continue (verified files are kept)" } else { "Cancelled; run it again to resume from the verified files" },
            );
            refresh(hwnd, state);
            if !paused {
                start_inspect(hwnd, state);
            }
        }
        Ok(_) => {}
        Err(e) => {
            let message = local::logged_error(&format!("{e:#}"));
            // A failed run is resumable: offer RESUME rather than losing the job.
            state.client.paused = kind;
            set_busy(hwnd, false, "");
            append_log(hwnd, state, "Game download failed");
            refresh(hwnd, state);
            show_fatal(&message);
        }
    }
}

/// Returns true when the command belonged to this view.
pub(super) unsafe fn command(hwnd: HWND, state: &mut Window, id: i32, notification: u16) -> bool {
    if notification == STN_CLICKED as u16 && id == ID_FOLDER_FIELD {
        return true;
    }
    match id {
        ID_OPEN => open(hwnd, state),
        ID_CHOOSE => match choose_folder(hwnd, w!("Select an empty folder for the game, or an existing game folder"), false) {
            Ok(Some(path)) => set_folder(hwnd, state, path),
            Ok(None) => {}
            Err(e) => show_fatal(&format!("{e:#}")),
        },
        i if (ID_LANG..ID_LANG + REGIONS.len() as i32).contains(&i) => {
            let region = REGIONS[(i - ID_LANG) as usize];
            if state.client.region != region {
                state.client.region = region;
                {
                    let mut m = state.model.lock().unwrap();
                    m.settings.region = Some(region);
                    if let Err(e) = save_settings(&m.settings) {
                        show_fatal(&format!("{e:#}"));
                    }
                }
                state.client.plan_line = None;
                state.client.paused = None;
                refresh(hwnd, state);
                if matches!(state.client.folder, Folder::Empty | Folder::Resumable) {
                    start_inspect(hwnd, state);
                }
            }
        }
        i if (ID_ACT..ID_ACT + 3).contains(&i) => match state.client.slots[(i - ID_ACT) as usize].map(|s| s.action) {
            Some(Action::Start(kind)) => start_job(hwnd, state, kind),
            Some(Action::Pause) | Some(Action::Cancel) => {
                let pause = matches!(state.client.slots[(i - ID_ACT) as usize].map(|s| s.action), Some(Action::Pause));
                if matches!(state.client.inflight, Some(Inflight::Run(_))) {
                    state.client.stop = Some(if pause { Stop::Pause } else { Stop::Cancel });
                    state.client.cancel.store(true, Ordering::SeqCst);
                    set_text(hwnd, ID_INFO, "Stopping after the current file chunk…");
                } else {
                    // Cancel while paused: drop the resumable job.
                    state.client.paused = None;
                    state.client.fraction = None;
                    refresh(hwnd, state);
                }
            }
            _ => {}
        },
        _ => return false,
    }
    true
}

/// Owner-draws a language button: native name large, region small; the selected edition is filled with the accent.
pub(super) unsafe fn draw_lang(item: &DRAWITEMSTRUCT, state: &Window) -> bool {
    let id = item.CtlID as i32;
    if !(ID_LANG..ID_LANG + 4).contains(&id) {
        return false;
    }
    let region = REGIONS[(id - ID_LANG) as usize];
    let selected = state.client.effective_region() == region;
    let disabled = item.itemState.0 & ODS_DISABLED.0 != 0;
    let pressed = item.itemState.0 & ODS_SELECTED.0 != 0;
    let hot = item.itemState.0 & ODS_HOTLIGHT.0 != 0 || GetWindowLongPtrW(item.hwndItem, GWLP_USERDATA) != 0;
    let brush = if selected {
        state.accent_brush
    } else if disabled {
        state.muted_brush
    } else if hot || pressed {
        state.button_hot_brush
    } else {
        state.button_brush
    };
    let _ = FillRect(item.hDC, &item.rcItem, brush);
    let _ = SetBkMode(item.hDC, TRANSPARENT);
    let (main, sub) = if selected {
        (COLORREF(0x00201a18), COLORREF(0x00483f3b))
    } else if disabled {
        (COLORREF(0x009a918d), COLORREF(0x009a918d))
    } else {
        (COLORREF(0x00f4f1ef), COLORREF(0x00c9c5c2))
    };
    let (name, place) = names(region);
    // Native names need a face with the script: Segoe UI has no CJK glyphs, and Wine (macOS players) has no font
    // linking to fill them in. Without any matching face the English name is shown instead of boxes.
    let (name, name_font) = match native_font(region) {
        Some(font) => (name, font),
        None => (english_name(region), state.heading_font),
    };
    let mut name_rect = RECT { top: item.rcItem.top + 7, bottom: item.rcItem.top + 33, ..item.rcItem };
    let mut place_rect = RECT { top: item.rcItem.top + 33, bottom: item.rcItem.bottom, ..item.rcItem };
    for (text, rect, font, color) in [
        (name, &mut name_rect, name_font, main),
        (place, &mut place_rect, state.label_font, sub),
    ] {
        let mut text = text.encode_utf16().collect::<Vec<u16>>();
        let old = SelectObject(item.hDC, font);
        let _ = SetTextColor(item.hDC, color);
        let _ = DrawTextW(item.hDC, &mut text, rect, DT_CENTER | DT_VCENTER | DT_SINGLELINE);
        let _ = SelectObject(item.hDC, old);
    }
    if item.itemState.0 & ODS_FOCUS.0 != 0 {
        let mut focus = item.rcItem;
        let _ = InflateRect(&mut focus, -5, -5);
        let _ = DrawFocusRect(item.hDC, &focus);
    }
    true
}

fn english_name(region: Region) -> &'static str {
    match region {
        Region::En => "English",
        Region::Tw => "Traditional Chinese",
        Region::Kr => "Korean",
        Region::Jp => "Japanese",
        Region::Cn => "Simplified Chinese",
    }
}

/// Installed font faces that cover each native name, Windows faces first, then the macOS faces Wine loads from the
/// system, then common Linux/Noto ones.
fn native_faces(region: Region) -> &'static [&'static str] {
    match region {
        Region::En => &["Segoe UI"],
        Region::Tw => &["Microsoft JhengHei UI", "Microsoft JhengHei", "PingFang TC", "Heiti TC", "Noto Sans CJK TC", "Noto Sans TC", "MingLiU"],
        Region::Kr => &["Malgun Gothic", "Apple SD Gothic Neo", "AppleGothic", "Noto Sans CJK KR", "Noto Sans KR", "Gulim"],
        // Wine on macOS lists Hiragino only under weight-suffixed family names.
        Region::Jp => &["Yu Gothic UI", "Meiryo UI", "Meiryo", "Hiragino Sans W6", "Hiragino Sans W3", "Hiragino Kaku Gothic ProN W6", "Hiragino Kaku Gothic ProN", "Noto Sans CJK JP", "Noto Sans JP", "MS UI Gothic"],
        Region::Cn => &["Microsoft YaHei UI", "PingFang SC", "Heiti SC", "Noto Sans CJK SC", "Noto Sans SC", "Microsoft YaHei", "SimHei"],
    }
}

/// The heading-sized font for a region's native name, created once per region; `None` when no listed face exists.
unsafe fn native_font(region: Region) -> Option<HFONT> {
    thread_local! {
        static FONTS: std::cell::RefCell<[Option<Option<HFONT>>; REGIONS.len()]> = const { std::cell::RefCell::new([None; REGIONS.len()]) };
    }
    let index = REGIONS.iter().position(|&r| r == region)?;
    FONTS.with(|fonts| {
        *fonts.borrow_mut()[index].get_or_insert_with(|| {
            let face = native_faces(region).iter().find(|face| face_installed(face))?;
            let wide: Vec<u16> = face.encode_utf16().chain(std::iter::once(0)).collect();
            let font = CreateFontW(-16, 0, 0, 0, FW_SEMIBOLD.0 as i32, 0, 0, 0, DEFAULT_CHARSET.0 as u32,
                OUT_DEFAULT_PRECIS.0 as u32, CLIP_DEFAULT_PRECIS.0 as u32, CLEARTYPE_QUALITY.0 as u32,
                (DEFAULT_PITCH.0 | FF_SWISS.0) as u32, PCWSTR(wide.as_ptr()));
            (font.0 != 0).then_some(font)
        })
    })
}

unsafe fn face_installed(face: &str) -> bool {
    unsafe extern "system" fn found(_: *const LOGFONTW, _: *const TEXTMETRICW, _: u32, flag: LPARAM) -> i32 {
        *(flag.0 as *mut bool) = true;
        0
    }
    let mut query = LOGFONTW { lfCharSet: DEFAULT_CHARSET, ..Default::default() };
    for (slot, unit) in query.lfFaceName.iter_mut().zip(face.encode_utf16().take(31)) {
        *slot = unit;
    }
    let mut exists = false;
    let dc = GetDC(None);
    EnumFontFamiliesExW(dc, &query, Some(found), LPARAM(&mut exists as *mut bool as isize), 0);
    ReleaseDC(None, dc);
    exists
}

#[cfg(test)]
mod tests {
    use super::*;

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

//! "Game files": the client's language (edition), install folder, and the install / verify & repair /
//! update / adopt jobs of `download`, run on the controller's worker/event mechanism.
use super::*;
use ascnet_launcher::download::{Detected, Job, Outcome, Phase, Plan, Progress};
use std::{
    mem::{discriminant, Discriminant},
    sync::{
        atomic::{AtomicBool, Ordering},
        Arc,
    },
    time::Instant,
};

pub const REGIONS: [Region; 5] = [Region::En, Region::Tw, Region::Kr, Region::Jp, Region::Cn];
const STATE_DIR: &str = ".ascnet-download";

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Kind {
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
    /// Plain English for launcher.log (the player-facing wording lives in the UI).
    fn log_name(self) -> &'static str {
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
    NotEmpty,
    Unreadable(String),
}

#[derive(Clone, Copy)]
enum Action {
    Start(Kind),
    Pause,
    Cancel,
    Busy,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum SlotLabel {
    Download,
    Verify,
    Update,
    Adopt,
    Pause,
    Resume,
    Cancel,
    Working,
}
#[derive(Clone, Copy)]
pub struct Slot {
    pub label: SlotLabel,
    action: Action,
    pub enabled: bool,
    pub primary: bool,
}

pub(super) enum Inflight {
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

/// A running job's latest progress, ready to render ("Downloading 32% · 2.1 of 71.5 GB · ~12 min").
#[derive(Clone, Debug, PartialEq)]
pub struct Transfer {
    pub phase: Phase,
    /// 0..=100 from bytes, else from files; `None` while neither total is known.
    pub percent: Option<u32>,
    pub bytes: Option<(String, String)>,
    pub eta_minutes: Option<u64>,
    pub files: Option<(usize, usize)>,
    pub current: Option<String>,
}

pub(super) struct Files {
    pub region: Region,
    dir: Option<PathBuf>,
    folder: Folder,
    detected: Option<Detected>,
    plan: Vec<Text>,
    pinned: Option<String>,
    inflight: Option<Inflight>,
    paused: Option<Kind>,
    stop: Option<Stop>,
    cancel: Arc<AtomicBool>,
    fraction: Option<(u64, u64)>,
    transfer: Option<Transfer>,
    last_phase: Option<Discriminant<Phase>>,
}
impl Files {
    pub fn new(region: Region) -> Self {
        Self {
            region,
            dir: None,
            folder: Folder::None,
            detected: None,
            plan: Vec::new(),
            pinned: None,
            inflight: None,
            paused: None,
            stop: None,
            cancel: Arc::new(AtomicBool::new(false)),
            fraction: None,
            transfer: None,
            last_phase: None,
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
    pub fn working(&self) -> bool {
        self.inflight.is_some()
    }
    pub fn inflight(&self) -> Option<&Inflight> {
        self.inflight.as_ref()
    }
    /// Determinate fill for a running job; `None` = busy marquee.
    pub fn fraction_value(&self) -> Option<f32> {
        self.fraction.filter(|&(_, total)| total > 0).map(|(done, total)| (done as f64 / total as f64).min(1.0) as f32)
    }
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
        Ok(false) => Folder::NotEmpty,
        Err(e) => Folder::Unreadable(e.to_string()),
    }
}

/// Bytes held by the launcher's partial-download state (links are counted, never followed).
fn partial_size(dir: &Path) -> u64 {
    fn walk(path: &Path) -> u64 {
        let Ok(entries) = fs::read_dir(path) else { return 0 };
        entries
            .flatten()
            .map(|e| match e.metadata() {
                Ok(m) if m.is_dir() => walk(&e.path()),
                Ok(m) => m.len(),
                Err(_) => 0,
            })
            .sum()
    }
    walk(&dir.join(STATE_DIR))
}

/// Deletes the launcher's own `.ascnet-download` folder (job marker and parts). Finished files are installed
/// straight to their final path by the download, so nothing else in `dir` belongs to a partial download.
fn discard_partial(dir: &Path) -> Result<()> {
    let state = dir.join(STATE_DIR);
    match fs::symlink_metadata(&state) {
        Ok(m) => anyhow::ensure!(m.is_dir(), "refusing to delete {}: not a folder", state.display()),
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(()),
        Err(e) => return Err(e.into()),
    }
    fs::remove_dir_all(&state).with_context(|| format!("deleting {} (is a file in use?)", state.display()))
}

const GIB: f64 = 1024.0 * 1024.0 * 1024.0;
const MIB: f64 = 1024.0 * 1024.0;

fn fmt_bytes(bytes: u64) -> String {
    let b = bytes as f64;
    if b >= GIB {
        format!("{:.1} GB", b / GIB)
    } else {
        format!("{:.0} MB", b / MIB)
    }
}

/// "2.1" and "71.5 GB": both in the total's unit, so "2.1 of 71.5 GB" reads naturally.
fn fmt_pair(done: u64, total: u64) -> (String, String) {
    if total as f64 >= GIB {
        (format!("{:.1}", done as f64 / GIB), format!("{:.1} GB", total as f64 / GIB))
    } else {
        (format!("{:.0}", done as f64 / MIB), format!("{:.0} MB", total as f64 / MIB))
    }
}

fn eta_minutes(done: u64, total: u64, bytes_per_second: u64) -> Option<u64> {
    (bytes_per_second > 0 && total > done).then(|| (total - done).div_ceil(bytes_per_second).div_ceil(60).max(1))
}

fn plan_text(plan: &Plan) -> Vec<Text> {
    let mut lines = vec![Text::FilesPlan {
        download: fmt_bytes(plan.download_bytes),
        files: plan.files,
        needs: fmt_bytes(plan.required_free_bytes),
        free: fmt_bytes(plan.free_bytes),
        removed: plan.delete,
    }];
    if let Some(note) = &plan.note {
        lines.push(Text::FilesNote(note.clone()));
    }
    lines
}

fn metadata_path() -> Result<PathBuf> {
    Ok(ascnet_launcher::local::resource_dir()?.join("supported-client.json"))
}

fn full_name(region: Region) -> &'static str {
    match region {
        Region::En => "English (Global)",
        Region::Tw => "繁體中文 (Taiwan)",
        Region::Kr => "한국어 (Korea)",
        Region::Jp => "日本語 (Japan)",
        Region::Cn => "简体中文 (China)",
    }
}

impl Controller {
    pub(super) fn files_view(&self) -> FilesView {
        let f = &self.files;
        let running = matches!(f.inflight, Some(Inflight::Run(_)));
        let busy = self.busy.is_some();
        let info = if running && f.stop.is_some() {
            Text::FilesStopping
        } else {
            match (&f.folder, &f.dir) {
                (Folder::None, _) | (_, None) => Text::FilesChoose,
                (Folder::NotEmpty, _) => Text::FilesNotEmpty,
                (Folder::Unreadable(e), _) => Text::FilesUnreadable(e.clone()),
                (Folder::Empty, _) => Text::FilesEmptyFolder(f.region),
                (Folder::Resumable, _) => Text::FilesResumable,
                (Folder::Game, _) => match &f.detected {
                    None => Text::FilesInspecting,
                    Some(d) => Text::FilesGame {
                        region: d.region,
                        version: d.version.clone(),
                        steam: d.steam,
                        patched: d.launcher_patched,
                        assumed: f.region,
                    },
                },
            }
        };
        FilesView {
            tiles_enabled: !busy && !f.locked_region(),
            folder: f.dir.as_deref().map(|p| p.display().to_string()).unwrap_or_default(),
            can_choose: !busy,
            info,
            plan: if running { Vec::new() } else { f.plan.clone() },
            slots: self.files_slots(),
            transfer: (running || f.paused.is_some()).then(|| f.transfer.clone()).flatten(),
            working_label: match f.inflight {
                Some(Inflight::Run(kind)) if f.transfer.is_none() => Some(Text::FilesRunning(kind)),
                Some(Inflight::Inspect) => Some(Text::FilesInspecting),
                _ => None,
            },
            fraction: f.fraction_value(),
            paused: f.paused.is_some() && !busy,
        }
    }

    fn files_slots(&self) -> Vec<Slot> {
        let s = &self.files;
        let slot = |label, action, enabled, primary| Slot { label, action, enabled, primary };
        let selected = self.settings.selected_game.as_deref();
        let slots = if self.busy.is_some() {
            match s.inflight {
                Some(Inflight::Run(_)) => vec![
                    slot(SlotLabel::Pause, Action::Pause, s.stop.is_none(), false),
                    slot(SlotLabel::Cancel, Action::Cancel, s.stop.is_none(), false),
                ],
                _ => vec![slot(SlotLabel::Working, Action::Busy, false, false)],
            }
        } else if let Some(kind) = s.paused {
            vec![slot(SlotLabel::Resume, Action::Start(kind), true, true), slot(SlotLabel::Cancel, Action::Cancel, true, false)]
        } else {
            match s.folder {
                Folder::Empty | Folder::Resumable => vec![slot(SlotLabel::Download, Action::Start(Kind::Install), true, true)],
                Folder::Game => {
                    let older = match (s.detected.as_ref().and_then(|d| d.version.as_deref()), s.pinned.as_deref()) {
                        (Some(have), Some(want)) => {
                            package::compare_versions(have, want).is_ok_and(|o| o == std::cmp::Ordering::Less)
                        }
                        _ => false,
                    };
                    let new_to_launcher = s.dir.as_deref() != selected;
                    let ready = s.detected.is_some();
                    vec![
                        slot(SlotLabel::Verify, Action::Start(Kind::Repair), ready, ready && !older && !new_to_launcher),
                        slot(SlotLabel::Update, Action::Start(Kind::Update), ready && older, ready && older),
                        slot(SlotLabel::Adopt, Action::Start(Kind::Adopt), ready && new_to_launcher, ready && new_to_launcher && !older),
                    ]
                }
                _ => vec![slot(SlotLabel::Download, Action::Busy, false, false)],
            }
        };
        slots
    }

    pub(super) fn files_opened(&mut self) {
        // Offer the folder the player already selected so Verify / Update act on it directly.
        if self.files.dir.is_none() {
            if let Some(game) = self.settings.selected_game.clone() {
                self.files_set_folder(game);
            }
        }
    }

    /// The install-folder picker returned (`None` = cancelled).
    pub fn files_folder_picked(&mut self, path: Option<PathBuf>) {
        if let (Some(path), None) = (path, &self.dialog) {
            if self.busy.is_none() {
                self.files_set_folder(path);
            }
        }
    }

    pub(super) fn files_set_folder(&mut self, dir: PathBuf) {
        let f = &mut self.files;
        f.folder = classify(&dir);
        f.dir = Some(dir);
        f.detected = None;
        f.plan.clear();
        f.paused = None;
        f.fraction = None;
        f.transfer = None;
        if matches!(f.folder, Folder::Empty | Folder::Resumable | Folder::Game) {
            self.files_inspect();
        }
    }

    pub fn files_set_region(&mut self, region: Region) {
        if self.dialog.is_some() || self.busy.is_some() || self.files.locked_region() || self.files.region == region {
            return;
        }
        self.files.region = region;
        self.settings.region = Some(region);
        self.save();
        self.files.plan.clear();
        self.files.paused = None;
        if matches!(self.files.folder, Folder::Empty | Folder::Resumable) {
            self.files_inspect();
        }
    }

    /// One of the action buttons, by its position in `FilesView::slots`.
    pub fn files_action(&mut self, index: usize) {
        if self.dialog.is_some() {
            return;
        }
        let Some(slot) = self.files_slots().get(index).copied().filter(|s| s.enabled) else { return };
        match slot.action {
            Action::Start(kind) => {
                if kind != Kind::Install && self.files.paused.is_none() {
                    if self.game_closed() {
                        self.dialog = Some(Dialog::ConfirmVerify(kind));
                    }
                } else {
                    self.files_start_job(kind);
                }
            }
            Action::Pause => {
                if matches!(self.files.inflight, Some(Inflight::Run(_))) {
                    self.files.stop = Some(Stop::Pause);
                    self.files.cancel.store(true, Ordering::SeqCst);
                }
            }
            // The download keeps going (or stays paused) until the player confirms.
            Action::Cancel => {
                let size = self.files.dir.as_deref().map_or(0, partial_size);
                self.dialog = Some(Dialog::ConfirmCancel(fmt_bytes(size)));
            }
            Action::Busy => {}
        }
    }

    /// The player confirmed "delete the download": a running worker is stopped first (the delete happens when it
    /// reports back, so nothing is removed under its feet); a paused job is deleted now.
    pub(super) fn files_confirm_cancel(&mut self) {
        if matches!(self.files.inflight, Some(Inflight::Run(_))) {
            self.files.stop = Some(Stop::Cancel);
            self.files.cancel.store(true, Ordering::SeqCst);
        } else if self.files.paused.is_some() {
            self.files_discard();
        }
    }

    /// Deletes the partial download; if that fails the job stays paused (resumable) and the player is told.
    fn files_discard(&mut self) {
        let Some(dir) = self.files.dir.clone() else { return };
        match discard_partial(&dir) {
            Ok(()) => {
                let f = &mut self.files;
                f.paused = None;
                f.fraction = None;
                f.transfer = None;
                f.folder = classify(&dir);
                self.log(Text::FilesCancelled);
                self.files_inspect();
            }
            Err(e) => {
                self.log(Text::FilesPaused);
                self.problem(Problem::CancelFailed, &e);
            }
        }
    }

    /// Detects the install and prices the job it would need; all network work stays off the UI thread.
    fn files_inspect(&mut self) {
        let Some(dir) = self.files.dir.clone() else { return };
        if self.busy.is_some() {
            return;
        }
        let (generation, events) = self.begin(Busy::FilesInspect);
        self.files.inflight = Some(Inflight::Inspect);
        let region = self.files.region;
        let game = matches!(self.files.folder, Folder::Game);
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
            let _ = events.send(Event::Work(Work { generation, result }));
        });
    }

    pub(super) fn files_start_job(&mut self, kind: Kind) {
        let Some(dir) = self.files.dir.clone() else { return };
        if !self.game_closed() || self.busy.is_some() || self.runtime.is_some() {
            return;
        }
        let region = self.files.effective_region();
        let cancel = self.files.cancel.clone();
        cancel.store(false, Ordering::SeqCst);
        let (generation, events) = self.begin(Busy::Files(kind));
        let f = &mut self.files;
        f.inflight = Some(Inflight::Run(kind));
        f.paused = None;
        f.stop = None;
        f.last_phase = None;
        f.fraction = Some((0, 0));
        f.transfer = None;
        self.log(Text::FilesStarted(kind, region));
        thread::spawn(move || {
            let action = format!("Client {}", kind.log_name());
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
                        let _ = events.send(Event::Download(p));
                    }
                })
            })();
            let result = match result {
                Ok(outcome) => Ok(WorkResult::Client(Done::Finished(outcome))),
                Err(e) if cancel.load(Ordering::SeqCst) || e.downcast_ref::<download::Cancelled>().is_some() => {
                    Ok(WorkResult::Client(Done::Stopped))
                }
                Err(e) => Err(e),
            };
            let _ = local::launcher_log(&match &result {
                Ok(WorkResult::Client(Done::Stopped)) => format!("{action}: stopped"),
                Ok(_) => format!("{action}: ok"),
                Err(e) => format!("{action}: failed: {e:#}"),
            });
            let _ = events.send(Event::Work(Work { generation, result }));
        });
    }

    pub(super) fn files_progress(&mut self, p: Progress) {
        let f = &mut self.files;
        if !matches!(f.inflight, Some(Inflight::Run(_))) {
            return;
        }
        let (done, total) = if p.bytes_total > 0 {
            (p.bytes_done, p.bytes_total)
        } else {
            (p.files_done as u64, p.files_total as u64)
        };
        f.fraction = Some((done, total));
        f.transfer = Some(Transfer {
            phase: p.phase,
            percent: (total > 0).then(|| (done.min(total) * 100 / total) as u32),
            bytes: (p.bytes_total > 0).then(|| fmt_pair(p.bytes_done, p.bytes_total)),
            eta_minutes: if p.bytes_total > 0 { eta_minutes(p.bytes_done, p.bytes_total, p.bytes_per_second) } else { None },
            files: (p.files_total > 0).then_some((p.files_done, p.files_total)),
            current: p.current,
        });
        if f.last_phase != Some(discriminant(&p.phase)) {
            f.last_phase = Some(discriminant(&p.phase));
            self.log(Text::FilesPhase(p.phase));
        }
    }

    /// Handles every worker result while this view has work in flight (success, stop, or error).
    pub(super) fn files_finish(&mut self, work: Work) {
        if work.generation != self.generation {
            return;
        }
        self.busy = None;
        let kind = match self.files.inflight.take() {
            Some(Inflight::Run(kind)) => Some(kind),
            _ => None,
        };
        match work.result {
            Ok(WorkResult::Client(Done::Inspected { dir, detected, plan, pinned })) => {
                if self.files.dir.as_deref() == Some(dir.as_path()) {
                    let f = &mut self.files;
                    f.pinned = pinned;
                    let mut inspect_error = None;
                    match detected {
                        Some(Ok(d)) => {
                            if let Some(region) = d.region {
                                f.region = region;
                            }
                            f.detected = Some(d);
                        }
                        Some(Err(e)) => inspect_error = Some(Text::FilesInspectFailed(format!("{e:#}"))),
                        None => {}
                    }
                    f.plan = match plan {
                        Some(Ok(plan)) => plan_text(&plan),
                        Some(Err(e)) => vec![Text::FilesPlanError(format!("{e:#}"))],
                        None => inspect_error.into_iter().collect(),
                    };
                    if matches!(f.folder, Folder::Game) && f.detected.is_none() {
                        // Detection failed: still allow verify against the picked language.
                        f.detected = Some(Detected { region: None, version: None, steam: false, launcher_patched: false });
                    }
                    let region = f.region;
                    if self.settings.region != Some(region) && matches!(self.files.folder, Folder::Game) {
                        self.settings.region = Some(region);
                        self.save();
                    }
                }
            }
            Ok(WorkResult::Client(Done::Finished(outcome))) => {
                self.files.fraction = None;
                self.files.transfer = None;
                let _ = local::launcher_log(&format!(
                    "Game files ready: {} repaired, {} downloaded, {} removed, {} patch files restored{}{}",
                    outcome.repaired,
                    fmt_bytes(outcome.downloaded_bytes),
                    outcome.deleted,
                    outcome.document_files,
                    if outcome.skipped_variants.is_empty() { String::new() } else { format!(" (left as is: {})", outcome.skipped_variants.join(", ")) },
                    outcome.document_notes.iter().map(|n| format!("\n{n}")).collect::<String>(),
                ));
                self.log(Text::FilesDone {
                    repaired: outcome.repaired,
                    downloaded: fmt_bytes(outcome.downloaded_bytes),
                    removed: outcome.deleted,
                    patch_files: outcome.document_files,
                });
                let Some(dir) = self.files.dir.clone().filter(|d| crate::steam::valid_game_directory(d)) else {
                    return;
                };
                self.settings.selected_game = Some(dir);
                self.settings.region = Some(self.files.region);
                self.patch = None;
                self.generation += 1;
                self.save();
                if self.screen == Screen::Files {
                    self.screen = Screen::Home;
                }
                // Hand over to the normal flow: re-inspect the build/patch, then SETUP -> patch install -> PLAY.
                self.start_refresh(true, false);
                self.log(Text::NextSetup);
            }
            Ok(WorkResult::Client(Done::Stopped)) => {
                let f = &mut self.files;
                if f.stop.take() == Some(Stop::Pause) {
                    f.paused = kind;
                    self.log(Text::FilesPaused);
                } else {
                    f.paused = kind;
                    self.files_discard();
                }
            }
            Ok(_) => {}
            Err(e) => {
                // A failed run is resumable: offer RESUME rather than losing the job.
                self.files.paused = kind;
                self.log(Text::FilesFailed);
                self.problem(Problem::Files(kind.unwrap_or(Kind::Install)), &e);
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn byte_pairs_share_the_total_unit() {
        let gib = 1024u64 * 1024 * 1024;
        assert_eq!(fmt_pair(gib * 21 / 10, gib * 715 / 10), ("2.1".to_owned(), "71.5 GB".to_owned()));
        assert_eq!(fmt_pair(5 * 1024 * 1024, 300 * 1024 * 1024), ("5".to_owned(), "300 MB".to_owned()));
    }

    #[test]
    fn eta_rounds_up_to_whole_minutes() {
        assert_eq!(eta_minutes(0, 100, 0), None);
        assert_eq!(eta_minutes(100, 100, 5), None);
        assert_eq!(eta_minutes(0, 1000, 100), Some(1));
        assert_eq!(eta_minutes(0, 60 * 100 * 12, 100), Some(12));
        assert_eq!(eta_minutes(0, 60 * 100 * 12 + 1, 100), Some(13));
    }

    #[test]
    fn folders_classify_by_content() {
        let dir = std::env::temp_dir().join(format!("ascnet-files-test-{}", std::process::id()));
        let _ = fs::remove_dir_all(&dir);
        fs::create_dir_all(&dir).unwrap();
        assert!(matches!(classify(&dir), Folder::Empty));
        fs::create_dir(dir.join(STATE_DIR)).unwrap();
        assert!(matches!(classify(&dir), Folder::Resumable));
        fs::remove_dir(dir.join(STATE_DIR)).unwrap();
        fs::write(dir.join("other.txt"), b"x").unwrap();
        assert!(matches!(classify(&dir), Folder::NotEmpty));
        fs::write(dir.join("PGR.exe"), b"x").unwrap();
        assert!(matches!(classify(&dir), Folder::Game));
        fs::remove_dir_all(&dir).unwrap();
    }

    fn controller(dir: &Path) -> Controller {
        let (tx, rx) = mpsc::channel();
        let mut c = Controller {
            config: DistributorConfig { repository_url: String::new(), branch: String::new() },
            settings: Settings::default(),
            build: None,
            package: None,
            runtime: None,
            patch: None,
            server: None,
            update_available: None,
            update_error: None,
            busy: None,
            stopping: false,
            generation: 0,
            tx,
            rx,
            music: None,
            music_path: PathBuf::new(),
            game_running: false,
            close_when_stopped: false,
            log: Default::default(),
            files: Files::new(Region::En),
            screen: Screen::Files,
            dialog: None,
            mods: Vec::new(),
            mods_status: None,
            quit: false,
            #[cfg(target_os = "macos")]
            mac: super::mac::Mac::inspect(),
        };
        c.files.dir = Some(dir.to_path_buf());
        c.files.folder = Folder::Resumable;
        c
    }

    /// A folder with a half-done download and an unrelated file next to it.
    fn partial_folder(name: &str) -> PathBuf {
        let dir = std::env::temp_dir().join(format!("ascnet-cancel-{name}-{}", std::process::id()));
        let _ = fs::remove_dir_all(&dir);
        fs::create_dir_all(dir.join(STATE_DIR).join("parts")).unwrap();
        fs::write(dir.join(STATE_DIR).join("job.json"), b"{}").unwrap();
        fs::write(dir.join(STATE_DIR).join("parts").join("a.part"), vec![0u8; 4096]).unwrap();
        fs::write(dir.join("keep.txt"), b"mine").unwrap();
        dir
    }

    fn click(c: &mut Controller, label: SlotLabel) {
        let index = c.files_slots().iter().position(|s| s.label == label).unwrap();
        c.files_action(index);
    }

    fn running(c: &mut Controller) {
        c.busy = Some(Busy::Files(Kind::Install));
        c.files.inflight = Some(Inflight::Run(Kind::Install));
    }

    fn stopped(c: &mut Controller) {
        let generation = c.generation;
        c.files_finish(Work { generation, result: Ok(WorkResult::Client(Done::Stopped)) });
    }

    #[test]
    fn cancel_confirmed_stops_the_worker_then_deletes_only_the_state() {
        let dir = partial_folder("confirm");
        let mut c = controller(&dir);
        running(&mut c);
        click(&mut c, SlotLabel::Cancel);
        assert!(matches!(&c.dialog, Some(Dialog::ConfirmCancel(size)) if size == "0 MB"));
        c.answer(true);
        assert!(c.files.cancel.load(Ordering::SeqCst));
        assert!(dir.join(STATE_DIR).exists(), "nothing is deleted before the worker has stopped");
        stopped(&mut c);
        assert!(!dir.join(STATE_DIR).exists());
        assert_eq!(fs::read(dir.join("keep.txt")).unwrap(), b"mine");
        assert!(c.files.paused.is_none() && c.dialog.is_none());
        fs::remove_dir_all(&dir).unwrap();
    }

    #[test]
    fn cancel_declined_keeps_the_download_running() {
        let dir = partial_folder("decline");
        let mut c = controller(&dir);
        running(&mut c);
        click(&mut c, SlotLabel::Cancel);
        c.answer(false);
        assert!(!c.files.cancel.load(Ordering::SeqCst));
        assert!(c.files.stop.is_none() && c.dialog.is_none());
        assert!(dir.join(STATE_DIR).exists());
        fs::remove_dir_all(&dir).unwrap();
    }

    #[test]
    fn pause_keeps_the_state_and_stays_resumable() {
        let dir = partial_folder("pause");
        let mut c = controller(&dir);
        running(&mut c);
        click(&mut c, SlotLabel::Pause);
        assert!(c.dialog.is_none());
        stopped(&mut c);
        assert_eq!(c.files.paused, Some(Kind::Install));
        assert!(dir.join(STATE_DIR).join("job.json").exists());
        fs::remove_dir_all(&dir).unwrap();
    }

    #[test]
    fn cancel_while_paused_deletes() {
        let dir = partial_folder("paused");
        let mut c = controller(&dir);
        c.files.paused = Some(Kind::Install);
        click(&mut c, SlotLabel::Cancel);
        assert!(matches!(c.dialog, Some(Dialog::ConfirmCancel(_))));
        assert!(dir.join(STATE_DIR).exists());
        c.answer(true);
        assert!(!dir.join(STATE_DIR).exists());
        assert!(dir.join("keep.txt").exists() && c.files.paused.is_none());
        fs::remove_dir_all(&dir).unwrap();
    }

    #[test]
    fn failed_delete_reports_and_stays_resumable() {
        let dir = partial_folder("fail");
        let mut c = controller(&dir);
        c.files.paused = Some(Kind::Install);
        // A file where the folder should be makes the delete refuse.
        fs::remove_dir_all(dir.join(STATE_DIR)).unwrap();
        fs::write(dir.join(STATE_DIR), b"x").unwrap();
        click(&mut c, SlotLabel::Cancel);
        c.answer(true);
        assert!(matches!(c.dialog, Some(Dialog::Problem(Problem::CancelFailed, _))));
        assert_eq!(c.files.paused, Some(Kind::Install));
        assert!(dir.join(STATE_DIR).exists());
        fs::remove_dir_all(&dir).unwrap();
    }
}

//! macOS steps that come before the shared flow: CrossOver (installed with the player's consent), then the
//! AscNet bottle. Everything after that (game files, Set up, Play) is the shared controller; the platform
//! differences there live in `local::prepare`, `install::launch` and `install::game_running`.
use super::{BottleLabel, Busy, Controller, CrossOverLabel, Dialog, Event, FoundView, MacHome, Primary, Problem, Text, Work, WorkResult};
use ascnet_launcher::{
    local,
    mac::{
        bottle::{self, Bottle},
        crossover,
        discover::{self, Candidate},
    },
    package,
};
use std::{fs, path::PathBuf, sync::atomic::AtomicBool, thread};

/// Most existing installs offered at once.
const MAX_OFFERS: usize = 3;

/// What the launcher knows about the macOS runtime.
pub(super) struct Mac {
    pub crossover: Option<crossover::Installed>,
    pub bottle: bottle::Status,
    /// Label and fraction of the running CrossOver or bottle step.
    pub step: Option<(Text, Option<f32>)>,
    /// The default game folder (inside the bottle) was already offered in Game files this run.
    pub offered_folder: bool,
    /// Installs found in existing CrossOver bottles and Wine prefixes.
    pub found: Vec<Candidate>,
}

pub(super) enum StepProgress {
    CrossOver(crossover::Progress),
    Bottle(bottle::Progress),
    /// The background search finished.
    Found(Vec<Candidate>),
    /// The player picked a prefix folder: what is in it.
    Picked(PathBuf, Vec<Candidate>),
}

impl Mac {
    pub fn inspect() -> Self {
        let crossover = crossover::detect();
        let bottle = match (&crossover, Bottle::active()) {
            (Some(_), Ok(bottle)) => bottle.status(),
            _ => bottle::Status::Missing,
        };
        Mac { crossover, bottle, step: None, offered_folder: false, found: Vec::new() }
    }
}

impl Controller {
    /// Home's big button while CrossOver or the bottle is missing; `None` once the shared flow takes over.
    pub(super) fn mac_primary(&self) -> Option<Primary> {
        primary_for(self.mac.crossover.is_some(), self.mac.bottle == bottle::Status::Ready, self.mac_offers().len())
    }

    /// The big button on Home: true when this press went to CrossOver or the bottle.
    pub(super) fn mac_gate(&mut self) -> bool {
        match self.mac_primary() {
            Some(Primary::InstallCrossOver) => self.dialog = Some(Dialog::ConfirmCrossOver),
            Some(Primary::UseFound) => self.adopt_offer(0),
            Some(_) => self.mac_prepare_bottle(),
            None => return false,
        }
        true
    }

    pub(super) fn mac_home(&self) -> MacHome {
        MacHome {
            crossover: match (&self.mac.crossover, self.busy) {
                (_, Some(Busy::CrossOver)) => CrossOverLabel::Working,
                (Some(_), _) => CrossOverLabel::Ready,
                (None, _) => CrossOverLabel::Missing,
            },
            bottle: match (&self.mac.bottle, self.busy) {
                (_, Some(Busy::Bottle)) => BottleLabel::Working,
                (bottle::Status::Ready, _) if self.settings.adopted_prefix.is_some() => BottleLabel::Adopted,
                (bottle::Status::Ready, _) => BottleLabel::Ready,
                (bottle::Status::NeedsRepair(_), _) => BottleLabel::Repair,
                (bottle::Status::Missing, _) => BottleLabel::Missing,
            },
        }
    }

    /// The label and fraction of the running step, shown in place of the generic busy line.
    pub(super) fn mac_step(&self) -> Option<&(Text, Option<f32>)> {
        self.mac.step.as_ref().filter(|_| matches!(self.busy, Some(Busy::CrossOver | Busy::Bottle)))
    }

    pub(super) fn mac_progress(&mut self, progress: StepProgress) {
        self.mac.step = Some(match progress {
            StepProgress::Found(found) => {
                self.mac.found = found;
                return;
            }
            StepProgress::Picked(path, found) => {
                match found.first().cloned() {
                    None => self.dialog = Some(Dialog::Problem(Problem::InvalidFolder, path.display().to_string())),
                    Some(first) => {
                        self.mac.found.retain(|c| !found.iter().any(|n| n.game_dir == c.game_dir && n.prefix == c.prefix));
                        self.mac.found.splice(0..0, found);
                        if self.dialog.is_none() && self.busy.is_none() {
                            self.dialog = Some(Dialog::ConfirmAdopt(first));
                        }
                    }
                }
                return;
            }
            StepProgress::CrossOver(p) => {
                use crossover::Phase;
                let (index, fraction) = match p.phase {
                    Phase::Downloading => (0, (p.total > 0).then(|| p.done as f32 / p.total as f32)),
                    Phase::Verifying => (1, None),
                    Phase::Unpacking => (2, None),
                    Phase::Installing => (3, None),
                };
                (Text::CrossOverPhase(index), fraction)
            }
            StepProgress::Bottle(p) => (Text::WorkingBottle, (p.total > 0).then(|| p.done as f32 / p.total as f32)),
        });
    }

    /// The consent dialog was confirmed: download CodeWeavers' installer, verify its signature, install it.
    pub(super) fn mac_install_crossover(&mut self) {
        if self.busy.is_some() {
            return;
        }
        let (generation, events) = self.begin(Busy::CrossOver);
        self.mac.step = Some((Text::CrossOverPhase(0), None));
        self.log(Text::CrossOverPhase(0));
        thread::spawn(move || {
            let result = (|| {
                let release = crossover::latest_release()?;
                let cancel = AtomicBool::new(false);
                crossover::install(&release, &cancel, &mut |p| {
                    let _ = events.send(Event::Mac(StepProgress::CrossOver(p)));
                })?;
                Ok(WorkResult::Mac)
            })();
            let _ = events.send(Event::Work(Work { generation, result }));
        });
    }

    fn mac_prepare_bottle(&mut self) {
        if self.busy.is_some() || !self.game_closed() {
            return;
        }
        let Some(cx) = self.mac.crossover.clone() else { return };
        let (generation, events) = self.begin(Busy::Bottle);
        self.mac.step = Some((Text::WorkingBottle, None));
        self.log(Text::BottleCreating);
        thread::spawn(move || {
            let result = (|| {
                let cancel = AtomicBool::new(false);
                Bottle::ascnet()?.ensure(&cx, &cancel, &mut |p| {
                    let _ = events.send(Event::Mac(StepProgress::Bottle(p)));
                })?;
                Ok(WorkResult::Mac)
            })();
            let _ = events.send(Event::Work(Work { generation, result }));
        });
    }

    /// `ASCNET_UI_START=crossover`: opens the consent prompt for a screenshot even where CrossOver is installed.
    pub fn show_crossover_prompt(&mut self) {
        if self.dialog.is_none() {
            self.dialog = Some(Dialog::ConfirmCrossOver);
        }
    }

    /// A CrossOver or bottle step finished: re-read the runtime and say what happened.
    pub(super) fn mac_finished(&mut self, busy: Option<Busy>) -> Text {
        self.mac = Mac { offered_folder: self.mac.offered_folder, found: std::mem::take(&mut self.mac.found), ..Mac::inspect() };
        if matches!(busy, Some(Busy::CrossOver)) {
            Text::CrossOverInstalled
        } else {
            Text::BottleReady
        }
    }

    /// Game files with no game yet: offer the bottle's own game folder (`C:\Games\Punishing Gray Raven`) once per run.
    pub(super) fn mac_offer_game_folder(&mut self) {
        if self.mac.offered_folder || self.settings.selected_game.is_some() || self.mac.bottle != bottle::Status::Ready {
            return;
        }
        let Ok(bottle) = Bottle::ascnet() else { return };
        let folder = bottle.game_dir();
        if fs::create_dir_all(&folder).is_ok() {
            self.mac.offered_folder = true;
            self.files_folder_picked(Some(folder));
        }
    }

    /// Looks for installs in existing prefixes in the background; the result arrives as [`StepProgress::Found`].
    pub(super) fn mac_discover(&self) {
        let events = self.tx.clone();
        thread::spawn(move || {
            let _ = events.send(Event::Mac(StepProgress::Found(discover::discover(patch_package().as_ref()))));
        });
    }

    /// The prefix picker returned (`None` = cancelled).
    pub fn prefix_picked(&mut self, path: Option<PathBuf>) {
        let Some(path) = path else { return };
        if self.dialog.is_some() || self.busy.is_some() {
            return;
        }
        let events = self.tx.clone();
        thread::spawn(move || {
            let found = discover::in_prefix(&path, patch_package().as_ref());
            let _ = events.send(Event::Mac(StepProgress::Picked(path, found)));
        });
    }

    /// Found installs that are not the selected game yet, as offered on Home and in Game files.
    fn mac_offers(&self) -> Vec<&Candidate> {
        let selected = self.settings.selected_game.as_deref().map(|p| p.canonicalize().unwrap_or_else(|_| p.to_path_buf()));
        self.mac.found.iter().filter(|c| selected.as_deref() != Some(c.game_dir.as_path())).take(MAX_OFFERS).collect()
    }

    pub(super) fn mac_found(&self) -> Vec<FoundView> {
        self.mac_offers()
            .into_iter()
            .map(|c| FoundView {
                name: c.display_name(),
                info: Text::FilesGame {
                    region: c.detected.as_ref().and_then(|d| d.region),
                    version: c.detected.as_ref().and_then(|d| d.version.clone()),
                    steam: c.steam,
                    patched: c.detected.as_ref().is_some_and(|d| d.launcher_patched),
                    assumed: self.files.region,
                },
            })
            .collect()
    }

    /// The quiet link next to "Use <install>": create the AscNet bottle anyway.
    pub fn mac_new_bottle(&mut self) {
        if self.dialog.is_none() {
            self.mac_prepare_bottle();
        }
    }

    /// "Use it" on offer `index`: asks first (Steam-owned installs get a warning).
    pub fn adopt_offer(&mut self, index: usize) {
        if self.dialog.is_some() || self.busy.is_some() {
            return;
        }
        if let Some(candidate) = self.mac_offers().get(index).map(|c| (*c).clone()) {
            self.dialog = Some(Dialog::ConfirmAdopt(candidate));
        }
    }

    /// Records the prefix and game folder; Setup, status and Play then use them. Nothing in the prefix changes.
    pub(super) fn mac_adopt(&mut self, candidate: Candidate) {
        if self.busy.is_some() {
            return;
        }
        self.settings.adopted_prefix = Some(candidate.prefix.clone());
        self.settings.selected_game = Some(candidate.game_dir.clone());
        if let Some(region) = candidate.detected.as_ref().and_then(|d| d.region) {
            self.settings.region = Some(region);
            self.files.region = region;
        }
        if !self.save() {
            return;
        }
        Bottle::set_adopted(self.settings.adopted_prefix.clone());
        self.mac = Mac { offered_folder: true, found: std::mem::take(&mut self.mac.found), ..Mac::inspect() };
        self.log(Text::GameAdopted(candidate.display_name()));
        self.files_set_folder(candidate.game_dir);
        self.patch = None;
        self.start_refresh(false, false);
    }
}

/// Home's prerequisite button: CrossOver first; then an install already found in an existing prefix is preferred
/// to creating a bottle; `None` once the shared flow (game, Set up, Play) takes over.
fn primary_for(crossover: bool, bottle_ready: bool, offers: usize) -> Option<Primary> {
    match (crossover, bottle_ready, offers) {
        (false, ..) => Some(Primary::InstallCrossOver),
        (_, true, _) => None,
        (_, false, 0) => Some(Primary::CreateBottle),
        (_, false, _) => Some(Primary::UseFound),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn found_installs_beat_creating_a_bottle() {
        assert_eq!(primary_for(false, false, 2), Some(Primary::InstallCrossOver));
        assert_eq!(primary_for(true, false, 0), Some(Primary::CreateBottle));
        assert_eq!(primary_for(true, false, 1), Some(Primary::UseFound));
        assert_eq!(primary_for(true, false, 3), Some(Primary::UseFound));
        // adopted or AscNet bottle ready: the shared flow takes over, whatever else was found
        assert_eq!(primary_for(true, true, 2), None);
    }
}

/// The built patch package, when Setup has produced one (needed to judge a game folder's patch state).
fn patch_package() -> Option<package::PatchPackage> {
    local::load_build().ok().flatten().and_then(|b| package::load_package(&b.patch_directory).ok())
}

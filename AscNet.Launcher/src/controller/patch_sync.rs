//! Keeps the game's in-game patch files (`document/{matrix,launch}`) at the pinned build before Set up and Play.
//!
//! The 4.9 client runs on whatever `document/` holds when the served index sha1 matches it, so a player on an older
//! build would otherwise only get the new files from Verify & Repair. Both flows run this first, on their worker
//! thread: check the install against the region's pin (indexes only when it is current), download the delta with the
//! engine's own restore path when it is not, and carry on whatever happens: a cancel, an error, a game that is
//! running or no network end in one Activity notice, never in a blocked Set up or Play.
use super::*;
use ascnet_launcher::download::Progress;
use std::{
    cell::RefCell,
    sync::{
        atomic::{AtomicBool, Ordering},
        Arc,
    },
    time::Instant,
};

/// The checks (index fetches) may not hold Set up / Play longer than this; the update itself runs to completion
/// or until the player cancels.
const CHECK_BUDGET: Duration = Duration::from_secs(30);

/// Controller state while the worker is in its patch-file step.
pub(super) struct PatchSync {
    pub(super) cancel: Arc<AtomicBool>,
    /// "Updating the game's patch files (N GB)…" once the check found work; the normal busy label before.
    label: Option<Text>,
    fraction: Option<f32>,
}

pub(super) enum PatchSyncEvent {
    Updating(String),
    Progress(Progress),
    /// The step is over (success or not); the rest of Set up / Play follows.
    Ended,
    Outdated,
}

impl Controller {
    /// Call right after `begin(Busy::Setup | Busy::Play)`; the returned flag goes to the worker.
    pub(super) fn begin_patch_sync(&mut self) -> Arc<AtomicBool> {
        let cancel = Arc::new(AtomicBool::new(false));
        self.patch_sync = Some(PatchSync { cancel: cancel.clone(), label: None, fraction: None });
        cancel
    }

    /// Home's Cancel link: stops the download after the current file. Downloaded files are kept (the next run resumes).
    pub fn cancel_patch_sync(&mut self) {
        if let Some(sync) = &self.patch_sync {
            sync.cancel.store(true, Ordering::SeqCst);
        }
    }

    pub(super) fn patch_sync_event(&mut self, event: PatchSyncEvent) {
        match event {
            PatchSyncEvent::Updating(size) => {
                let text = Text::PatchFilesUpdating(size);
                if let Some(sync) = &mut self.patch_sync {
                    sync.label = Some(text.clone());
                }
                self.log(text);
            }
            PatchSyncEvent::Progress(p) => {
                if let Some(sync) = &mut self.patch_sync {
                    let (done, total) = if p.bytes_total > 0 { (p.bytes_done, p.bytes_total) } else { (p.files_done as u64, p.files_total as u64) };
                    sync.fraction = (total > 0).then(|| (done.min(total) as f64 / total as f64) as f32);
                }
            }
            PatchSyncEvent::Ended => self.patch_sync = None,
            PatchSyncEvent::Outdated => self.log(Text::PatchFilesOutdated),
        }
    }

    /// (label override, fraction) for the Home busy line while the step is running.
    pub(super) fn patch_sync_view(&self) -> Option<(Option<Text>, Option<f32>)> {
        self.patch_sync.as_ref().map(|s| (s.label.clone(), s.fraction))
    }
}

/// The step itself, never failing its caller: `check` says how many bytes are missing (`None` = current, nothing to
/// do), `update` downloads them. Every failure, cancel included, becomes the "patch files out of date" notice.
pub(super) fn run(events: &Sender<Event>, check: impl FnOnce() -> Result<Option<u64>>, update: impl FnOnce(&mut dyn FnMut(Progress)) -> Result<usize>) {
    let send = |event| {
        let _ = events.send(Event::PatchSync(event));
    };
    let outcome = (|| {
        let Some(bytes) = check()? else { return Ok(()) };
        send(PatchSyncEvent::Updating(files::fmt_bytes(bytes)));
        let mut last = Instant::now() - Duration::from_secs(1);
        let fetched = update(&mut |p: Progress| {
            // Steady progress at most ~4 times a second, like the Game files screen.
            if last.elapsed() >= Duration::from_millis(250) {
                last = Instant::now();
                send(PatchSyncEvent::Progress(p));
            }
        })?;
        let _ = local::launcher_log(&format!("In-game patch files updated: {fetched} files restored"));
        Ok::<(), anyhow::Error>(())
    })();
    send(PatchSyncEvent::Ended);
    if let Err(e) = outcome {
        let cancelled = e.downcast_ref::<download::Cancelled>().is_some();
        let _ = local::launcher_log(&if cancelled { "In-game patch files: update cancelled".to_owned() } else { format!("In-game patch files not updated: {e:#}") });
        send(PatchSyncEvent::Outdated);
    }
}

/// Real wiring for `game`: its region's pin, checked and updated with the download engine. `cancel` is the player's
/// flag (Home's Cancel link); it also ends the check early.
pub(super) fn sync_game(game: &Path, fallback: Region, cancel: Arc<AtomicBool>, events: &Sender<Event>) {
    let pending = RefCell::new(None);
    // The engine polls one flag. The check gets its own, raised by the player's cancel or by the time budget, so a
    // stalled connection cannot hold Set up / Play; the update then runs on the player's flag alone.
    let check_cancel = Arc::new(AtomicBool::new(false));
    let checking = Arc::new(AtomicBool::new(true));
    {
        let (checking, check_cancel, cancel) = (checking.clone(), check_cancel.clone(), cancel.clone());
        thread::spawn(move || {
            let start = Instant::now();
            while checking.load(Ordering::SeqCst) {
                if cancel.load(Ordering::SeqCst) || start.elapsed() >= CHECK_BUDGET {
                    check_cancel.store(true, Ordering::SeqCst);
                    break;
                }
                thread::sleep(Duration::from_millis(100));
            }
        });
    }
    run(
        events,
        || {
            let result = (|| {
                let sources = download::sources(&files::metadata_path()?)?;
                let region = download::detect(game).ok().and_then(|d| d.region).unwrap_or(fallback);
                let source = sources.into_iter().find(|s| s.region() == region).with_context(|| format!("no pinned client for {region:?}"))?;
                let found = download::check_document(&source, game, &check_cancel).map_err(|e| {
                    if check_cancel.load(Ordering::SeqCst) && !cancel.load(Ordering::SeqCst) {
                        anyhow::anyhow!("checking the patch files took longer than {} s", CHECK_BUDGET.as_secs())
                    } else {
                        e
                    }
                })?;
                let bytes = found.as_ref().map(|p| p.bytes);
                *pending.borrow_mut() = found;
                Ok(bytes)
            })();
            checking.store(false, Ordering::SeqCst);
            result
        },
        |progress| download::update_document(pending.take().expect("check found work"), &cancel, progress),
    );
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::cell::Cell;

    /// What `run` reported, in order: "updating <size>", "progress", "ended", "outdated".
    fn drain(rx: &mpsc::Receiver<Event>) -> Vec<String> {
        rx.try_iter()
            .map(|event| match event {
                Event::PatchSync(PatchSyncEvent::Updating(size)) => format!("updating {size}"),
                Event::PatchSync(PatchSyncEvent::Progress(_)) => "progress".to_owned(),
                Event::PatchSync(PatchSyncEvent::Ended) => "ended".to_owned(),
                Event::PatchSync(PatchSyncEvent::Outdated) => "outdated".to_owned(),
                _ => "other".to_owned(),
            })
            .collect()
    }

    fn progress() -> Progress {
        Progress { phase: download::Phase::Downloading, files_done: 1, files_total: 2, bytes_done: 1, bytes_total: 2, current: None, bytes_per_second: 0 }
    }

    #[test]
    fn an_up_to_date_install_does_no_download_and_shows_no_notice() {
        let (tx, rx) = mpsc::channel();
        let updated = Cell::new(false);
        run(&tx, || Ok(None), |_| {
            updated.set(true);
            Ok(0)
        });
        assert!(!updated.get());
        assert_eq!(drain(&rx), ["ended"]);
    }

    #[test]
    fn an_outdated_install_downloads_the_delta_before_the_caller_goes_on() {
        let (tx, rx) = mpsc::channel();
        run(&tx, || Ok(Some(3 * 1024 * 1024 * 1024 / 2)), |emit| {
            emit(progress());
            Ok(12)
        });
        assert_eq!(drain(&rx), ["updating 1.5 GB", "progress", "ended"]);
    }

    #[test]
    fn cancel_failure_and_offline_end_in_one_notice_and_never_stop_the_caller() {
        let cancelled = || -> Result<usize> { Err(anyhow::Error::new(download::Cancelled)) };
        let (tx, rx) = mpsc::channel();
        run(&tx, || Ok(Some(1 << 20)), |_| cancelled());
        assert_eq!(drain(&rx), ["updating 1 MB", "ended", "outdated"]);

        let (tx, rx) = mpsc::channel();
        run(&tx, || Ok(Some(1 << 20)), |_| anyhow::bail!("hash mismatch"));
        assert_eq!(drain(&rx), ["updating 1 MB", "ended", "outdated"]);

        // Offline: the check itself fails; nothing is downloaded.
        let (tx, rx) = mpsc::channel();
        let updated = Cell::new(false);
        run(&tx, || anyhow::bail!("downloading the document index: connection refused"), |_| {
            updated.set(true);
            Ok(0)
        });
        assert!(!updated.get());
        assert_eq!(drain(&rx), ["ended", "outdated"]);
    }

    #[test]
    fn progress_is_throttled_to_a_few_events_a_second() {
        let (tx, rx) = mpsc::channel();
        run(&tx, || Ok(Some(10)), |emit| {
            for _ in 0..1000 {
                emit(progress());
            }
            Ok(1)
        });
        let events = drain(&rx);
        assert!(events.iter().filter(|e| *e == "progress").count() <= 1, "{events:?}");
    }
}

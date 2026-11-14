//! Finds CrossOver, and when the player agrees installs CodeWeavers' official build into /Applications.
//!
//! AscNet never ships CrossOver. The download URL comes from the same Sparkle feed CrossOver itself updates from
//! (`SUFeedURL` in its Info.plist, `https://www.codeweavers.com/xml/versions/cxmac.xml`): its newest `<enclosure>` is the
//! official `https://media.codeweavers.com/pub/crossover/cxmac/demo/crossover-<version>.zip` (the 14-day trial build; the
//! player enters their own licence in CrossOver). The archive is trusted only after `codesign --verify --deep --strict`,
//! `spctl --assess` (notarized Developer ID) and a `TeamIdentifier` equal to CodeWeavers' [`TEAM_ID`].
use anyhow::{anyhow, ensure, Context, Result};
use sha2::{Digest, Sha256};
use std::{
    fs,
    io::{Read, Write},
    path::{Path, PathBuf},
    process::Command,
    sync::atomic::{AtomicBool, Ordering::SeqCst},
    time::Duration,
};

/// CodeWeavers Inc.'s Apple Developer Team ID (`codesign -dv /Applications/CrossOver.app`).
pub const TEAM_ID: &str = "9C6B7X7Z8E";
pub const APPLICATIONS: &str = "/Applications";
const FEED: &str = "https://www.codeweavers.com/xml/versions/cxmac.xml";
const DOWNLOAD_HOST_PREFIX: &str = "https://media.codeweavers.com/";

/// Why an operation ended early: the `cancel` flag was raised. Root cause of the `Err`; partial files are removed.
#[derive(Debug)]
pub struct Cancelled;
impl std::fmt::Display for Cancelled {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str("cancelled")
    }
}
impl std::error::Error for Cancelled {}

pub fn is_cancelled(e: &anyhow::Error) -> bool {
    e.downcast_ref::<Cancelled>().is_some()
}

/// An installed CrossOver.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Installed {
    /// `…/CrossOver.app`
    pub app: PathBuf,
    /// `…/CrossOver.app/Contents/SharedSupport/CrossOver` (the `CX_ROOT`).
    pub root: PathBuf,
    /// `CFBundleShortVersionString`, e.g. `26.3.0`.
    pub version: String,
}

impl Installed {
    pub fn bin(&self, name: &str) -> PathBuf {
        self.root.join("bin").join(name)
    }

    fn open(app: &Path) -> Option<Installed> {
        let root = app.join("Contents/SharedSupport/CrossOver");
        if !root.join("bin/wineloader").is_file() || !root.join("bin/cxbottle").is_file() {
            return None;
        }
        let version = Command::new("/usr/bin/plutil")
            .args(["-extract", "CFBundleShortVersionString", "raw", "-o", "-"])
            .arg(app.join("Contents/Info.plist"))
            .output()
            .ok()
            .filter(|o| o.status.success())
            .map(|o| String::from_utf8_lossy(&o.stdout).trim().to_string())
            .filter(|v| !v.is_empty())
            .unwrap_or_else(|| "unknown".into());
        Some(Installed { app: app.to_path_buf(), root, version })
    }
}

/// `/Applications/CrossOver.app`, if it is there and usable.
pub fn detect() -> Option<Installed> {
    detect_in(Path::new(APPLICATIONS))
}

pub fn detect_in(applications: &Path) -> Option<Installed> {
    Installed::open(&applications.join("CrossOver.app"))
}

/// The newest official CrossOver release.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Release {
    pub version: String,
    pub url: String,
    /// Size of the zip in bytes, from the feed.
    pub length: u64,
}

pub fn latest_release() -> Result<Release> {
    let body = client()?.get(FEED).send().and_then(|r| r.error_for_status()).and_then(|r| r.text()).context("reading CodeWeavers' CrossOver release feed")?;
    parse_feed(&body)
}

fn parse_feed(xml: &str) -> Result<Release> {
    let start = xml.find("<enclosure ").context("the CrossOver feed has no download")?;
    let tag = &xml[start..start + xml[start..].find('>').context("malformed CrossOver feed")?];
    let attr = |name: &str| -> Result<String> {
        let key = format!("{name}=\"");
        let from = tag.find(&key).with_context(|| format!("the CrossOver feed entry has no {name}"))? + key.len();
        Ok(tag[from..from + tag[from..].find('"').context("malformed CrossOver feed")?].to_string())
    };
    let release = Release {
        url: attr("url")?,
        version: attr("sparkle:shortVersionString")?,
        length: attr("length")?.parse().context("bad length in the CrossOver feed")?,
    };
    ensure!(release.url.starts_with(DOWNLOAD_HOST_PREFIX) && release.url.ends_with(".zip"), "unexpected CrossOver download URL {}", release.url);
    Ok(release)
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Phase {
    Downloading,
    Verifying,
    Unpacking,
    Installing,
}

#[derive(Clone, Copy, Debug)]
pub struct Progress {
    pub phase: Phase,
    /// Bytes downloaded so far (0 outside `Downloading`).
    pub done: u64,
    pub total: u64,
}

/// Downloads, verifies and installs CrossOver into `/Applications`. Never replaces an existing `CrossOver.app`.
/// Blocking; ends with `Err(Cancelled)` (see [`is_cancelled`]) when `cancel` is raised.
pub fn install(release: &Release, cancel: &AtomicBool, progress: &mut dyn FnMut(Progress)) -> Result<Installed> {
    install_into(Path::new(APPLICATIONS), release, cancel, progress)
}

pub fn install_into(applications: &Path, release: &Release, cancel: &AtomicBool, progress: &mut dyn FnMut(Progress)) -> Result<Installed> {
    let target = applications.join("CrossOver.app");
    ensure!(!target.exists(), "{} already exists; not replacing it", target.display());
    // The staging folder lives next to the destination so the final move is an atomic rename on one volume.
    let staging = applications.join(format!(".AscNet-CrossOver-{}", uuid::Uuid::new_v4()));
    fs::create_dir(&staging).with_context(|| format!("cannot write to {}; install CrossOver yourself from codeweavers.com", applications.display()))?;
    let result = (|| {
        let zip = staging.join("crossover.zip");
        let total = release.length;
        download(&release.url, &zip, cancel, &mut |done, _| progress(Progress { phase: Phase::Downloading, done, total }))?;
        ensure!(fs::metadata(&zip)?.len() == release.length, "the CrossOver download is {} bytes, the feed says {}", fs::metadata(&zip)?.len(), release.length);
        progress(Progress { phase: Phase::Unpacking, done: 0, total: 0 });
        // `ditto` keeps symlinks, permissions and the code signature intact (a Rust unzip would not).
        run_ok(Command::new("/usr/bin/ditto").args(["-x", "-k"]).arg(&zip).arg(&staging), "unpacking CrossOver")?;
        fs::remove_file(&zip)?;
        let app = staging.join("CrossOver.app");
        ensure!(app.is_dir(), "the CrossOver archive does not contain CrossOver.app");
        progress(Progress { phase: Phase::Verifying, done: 0, total: 0 });
        verify_app(&app)?;
        if cancel.load(SeqCst) {
            return Err(Cancelled.into());
        }
        progress(Progress { phase: Phase::Installing, done: 0, total: 0 });
        ensure!(!target.exists(), "{} appeared while downloading; not replacing it", target.display());
        fs::rename(&app, &target).context("moving CrossOver.app into place")?;
        Installed::open(&target).ok_or_else(|| anyhow!("the installed CrossOver is incomplete"))
    })();
    let _ = fs::remove_dir_all(&staging);
    result
}

/// Verifies `app` is CodeWeavers' unmodified, notarized build.
pub fn verify_app(app: &Path) -> Result<()> {
    let verify = Command::new("/usr/bin/codesign").args(["--verify", "--deep", "--strict"]).arg(app).output().context("running codesign")?;
    ensure!(verify.status.success(), "CrossOver's code signature is invalid: {}", String::from_utf8_lossy(&verify.stderr).trim());
    // codesign prints its report on stderr.
    let info = Command::new("/usr/bin/codesign").args(["-d", "--verbose=2"]).arg(app).output().context("running codesign")?;
    let team = team_identifier(&String::from_utf8_lossy(&info.stderr));
    ensure!(team.as_deref() == Some(TEAM_ID), "CrossOver is signed by team {:?}, expected CodeWeavers ({TEAM_ID})", team);
    let assess = Command::new("/usr/sbin/spctl").args(["--assess", "--type", "execute"]).arg(app).output().context("running spctl")?;
    ensure!(assess.status.success(), "macOS does not accept CrossOver's signature: {}", String::from_utf8_lossy(&assess.stderr).trim());
    Ok(())
}

fn team_identifier(report: &str) -> Option<String> {
    report.lines().find_map(|l| l.strip_prefix("TeamIdentifier=")).map(|t| t.trim().to_string())
}

fn run_ok(command: &mut Command, what: &str) -> Result<()> {
    let out = command.output().with_context(|| what.to_string())?;
    ensure!(out.status.success(), "{what} failed: {}", String::from_utf8_lossy(&out.stderr).trim());
    Ok(())
}

fn client() -> Result<reqwest::blocking::Client> {
    reqwest::blocking::Client::builder()
        .user_agent(concat!("AscNetLauncher/", env!("CARGO_PKG_VERSION")))
        .connect_timeout(Duration::from_secs(15))
        .timeout(REQUEST_TIMEOUT)
        .build()
        .context("creating the HTTP client")
}

/// Largest single request. The blocking client can only bound a whole request, so a big file is fetched in ranges:
/// a stalled connection then blocks `cancel` for at most [`REQUEST_TIMEOUT`].
const STEP: u64 = 16 * 1024 * 1024;
const REQUEST_TIMEOUT: Duration = Duration::from_secs(60);

/// Streams `url` into `dest` (replacing it). Returns the final URL after redirects and the file's SHA-256 (hex).
/// `on_bytes(done, total)` is called about every 128 KiB (`total` is 0 until the server says). A raised `cancel` removes `dest` and ends with `Err(Cancelled)`.
pub(crate) fn download(url: &str, dest: &Path, cancel: &AtomicBool, on_bytes: &mut dyn FnMut(u64, u64)) -> Result<(String, String)> {
    let result = (|| {
        let client = client()?;
        let mut file = fs::File::create(dest).with_context(|| format!("creating {}", dest.display()))?;
        let mut hash = Sha256::new();
        let mut buf = vec![0u8; 256 * 1024];
        let mut done = 0u64;
        let mut final_url = String::new();
        let mut failures = 0;
        let mut known_total = 0u64;
        let mut announced = 0u64;
        'chunks: loop {
            if cancel.load(SeqCst) {
                return Err(Cancelled.into());
            }
            // One ranged request; any failure retries from `done`, so no byte is fetched twice.
            let attempt = (|| -> Result<bool> {
                let range = format!("bytes={done}-{}", done + STEP - 1);
                let mut response = client.get(url).header(reqwest::header::RANGE, range).send().and_then(|r| r.error_for_status()).with_context(|| format!("downloading {url}"))?;
                final_url = response.url().to_string();
                let partial = response.status() == reqwest::StatusCode::PARTIAL_CONTENT;
                // `Content-Range: bytes a-b/total`
                let total = response.headers().get(reqwest::header::CONTENT_RANGE).and_then(|v| v.to_str().ok()).and_then(|v| v.rsplit('/').next()).and_then(|t| t.parse::<u64>().ok());
                known_total = total.unwrap_or(known_total);
                ensure!(partial || done == 0, "the server stopped honouring range requests for {url}");
                loop {
                    if cancel.load(SeqCst) {
                        return Err(Cancelled.into());
                    }
                    let n = response.read(&mut buf).with_context(|| format!("downloading {url}"))?;
                    if n == 0 {
                        break;
                    }
                    file.write_all(&buf[..n])?;
                    hash.update(&buf[..n]);
                    done += n as u64;
                    if done - announced >= 128 * 1024 {
                        announced = done;
                        on_bytes(done, known_total);
                    }
                }
                // A 200 carried the whole file; a 206 is finished once we reach the total it announced.
                Ok(!partial || total.is_none_or(|t| done >= t))
            })();
            match attempt {
                Ok(true) => break 'chunks,
                Ok(false) => failures = 0,
                Err(e) if is_cancelled(&e) || failures >= 3 => return Err(e),
                Err(_) => {
                    failures += 1;
                    std::thread::sleep(Duration::from_secs(failures));
                }
            }
        }
        on_bytes(done, done);
        file.flush()?;
        Ok((final_url, hex(&hash.finalize())))
    })();
    if result.is_err() {
        let _ = fs::remove_file(dest);
    }
    result
}

pub(crate) fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_the_sparkle_feed() {
        let xml = r#"<item><enclosure url="https://media.codeweavers.com/pub/crossover/cxmac/demo/crossover-26.3.0.zip" sparkle:version="26.3.0.39832" sparkle:shortVersionString="26.3.0" type="application/octet-stream" length="387192026" /></item>"#;
        let r = parse_feed(xml).unwrap();
        assert_eq!((r.version.as_str(), r.length), ("26.3.0", 387192026));
        assert!(parse_feed(&xml.replace("media.codeweavers.com", "evil.example")).is_err());
    }

    #[test]
    fn reads_team_id() {
        assert_eq!(team_identifier("Identifier=x\nTeamIdentifier=9C6B7X7Z8E\n").as_deref(), Some("9C6B7X7Z8E"));
    }

    #[test]
    fn detects_the_installed_crossover() {
        // Only meaningful on this workstation; skipped elsewhere.
        if let Some(cx) = detect() {
            assert!(cx.bin("wineloader").is_file());
            assert!(cx.version.chars().next().is_some_and(|c| c.is_ascii_digit()), "{}", cx.version);
            verify_app(&cx.app).unwrap();
        }
    }

    /// Downloads the real 387 MB build into a temp "Applications" folder, verifies it and installs it there.
    /// `cargo test --offline mac::crossover -- --ignored --nocapture`
    #[test]
    #[ignore]
    fn downloads_and_verifies_the_official_build_into_tmp() {
        let release = latest_release().unwrap();
        eprintln!("{release:?}");
        let dir = std::env::temp_dir().join(format!("ascnet-cx-install-{}", uuid::Uuid::new_v4()));
        fs::create_dir_all(&dir).unwrap();
        let mut last = 0;
        let cx = install_into(&dir, &release, &AtomicBool::new(false), &mut |p| {
            if p.phase != Phase::Downloading || p.done / (64 << 20) != last {
                last = p.done / (64 << 20);
                eprintln!("{p:?}");
            }
        })
        .unwrap();
        eprintln!("installed {cx:?}");
        assert!(release.version.starts_with(&cx.version), "{} vs {}", cx.version, release.version); // plist says 26.3, feed 26.3.0
        assert!(install_into(&dir, &release, &AtomicBool::new(false), &mut |_| {}).is_err(), "must not replace");
        fs::remove_dir_all(&dir).unwrap();
    }
}

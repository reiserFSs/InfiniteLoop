//! Official PGR client management (EN/TW/KR/JP/CN): fresh install, verify/repair, update through Kuro's whole-file
//! patches, and adopting an existing official/Steam folder by repairing it in place. See docs/client-download.md.
//!
//! Sources are pinned in `supported-client.json` ("downloads"); the live index is only used to discover CDN hosts
//! when every pinned host fails. Every written file is MD5-verified (per chunk for big files), replaced atomically
//! (part file + rename) and resumable through `.ascnet-download/` in the install folder.

use anyhow::{anyhow, bail, ensure, Context, Result};
use serde::{Deserialize, Serialize};
use std::{
    collections::{BTreeMap, HashMap, HashSet},
    fs::{self, File, OpenOptions},
    io::{BufReader, Read, Seek, SeekFrom, Write},
    path::{Path, PathBuf},
    sync::{
        atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering::SeqCst},
        Arc, LazyLock, Mutex,
    },
    time::{Duration, Instant},
};

mod document;

const STATE_DIR: &str = ".ascnet-download";
const MARKER: &str = "job.json";
const KRSDK_BIN: &str = "PGR_Data/Plugins/KRSDKRes/KRSDK.bin";
const KRSDK_CONFIG: &str = "PGR_Data/Plugins/KRSDKRes/KRSDKConfig.json";
/// Largest single HTTP request; bounds how long a stalled connection can block cancel (timeout below).
const STEP: u64 = 16 * 1024 * 1024;
const REQUEST_TIMEOUT: Duration = Duration::from_secs(120);
const ROUNDS: usize = 3;
const SEGMENT_ATTEMPTS: usize = 4;
/// A CDN failing this many times in a row is tried last until it succeeds again.
const DEMOTE_AFTER: u32 = 3;
const WORKERS: usize = 8;
const RETRY_BASE_MS: u64 = if cfg!(test) { 1 } else { 500 };
/// Slack on top of the download + extraction estimate (temp files, filesystem overhead).
const SPACE_MARGIN: u64 = 512 * 1024 * 1024;
const MAX_LIST_BYTES: usize = 64 * 1024 * 1024;

// ───────────────────────────── public API ─────────────────────────────

#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Region {
    En,
    Tw,
    Kr,
    Jp,
    Cn,
}

impl Region {
    pub const ALL: [Region; 5] = [Region::En, Region::Tw, Region::Kr, Region::Jp, Region::Cn];
    pub fn label(self) -> &'static str {
        match self {
            Region::En => "EN",
            Region::Tw => "TW",
            Region::Kr => "KR",
            Region::Jp => "JP",
            Region::Cn => "CN",
        }
    }
    fn from_project(id: &str) -> Option<Region> {
        Some(match id {
            "G143" => Region::En,
            "G279" => Region::Tw,
            "G286" => Region::Kr,
            "G282" => Region::Jp,
            "G148" => Region::Cn,
            _ => return None,
        })
    }
}

/// The pinned official release of one region.
#[derive(Clone, Debug)]
pub struct ClientSource {
    region: Region,
    version: String,
    full_size: u64,
    data: Arc<SourceData>,
}

impl ClientSource {
    pub fn region(&self) -> Region {
        self.region
    }
    pub fn version(&self) -> &str {
        &self.version
    }
    pub fn full_size(&self) -> u64 {
        self.full_size
    }
}

#[derive(Clone, Debug)]
pub enum Job {
    Install { dir: PathBuf },
    Repair { dir: PathBuf },
    Update { dir: PathBuf },
    Adopt { dir: PathBuf },
}

impl Job {
    pub fn dir(&self) -> &Path {
        match self {
            Job::Install { dir } | Job::Repair { dir } | Job::Update { dir } | Job::Adopt { dir } => dir,
        }
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Detected {
    pub region: Option<Region>,
    pub version: Option<String>,
    pub steam: bool,
    pub launcher_patched: bool,
}

#[derive(Clone, Debug)]
pub struct Plan {
    /// The effective job: Update/Repair/Adopt may be rerouted (see `note`).
    pub job: Job,
    pub download_bytes: u64,
    pub required_free_bytes: u64,
    pub free_bytes: u64,
    pub files: usize,
    pub delete: usize,
    pub note: Option<String>,
    prep: Arc<Prep>,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Phase {
    FetchingIndex,
    Verifying,
    Downloading,
    Extracting,
    Deleting,
    Finalizing,
}

#[derive(Clone, Debug)]
pub struct Progress {
    pub phase: Phase,
    pub files_done: usize,
    pub files_total: usize,
    pub bytes_done: u64,
    pub bytes_total: u64,
    pub current: Option<String>,
    pub bytes_per_second: u64,
}

#[derive(Clone, Debug, Default)]
pub struct Outcome {
    /// Files that existed with wrong content (or were missing) and were fetched again by a verify pass.
    /// Zero for a fresh Install and for files replaced by an Update patch.
    pub repaired: usize,
    /// Bytes actually transferred (resumed bytes excluded).
    pub downloaded_bytes: u64,
    pub deleted: usize,
    /// Files that differ from the official index but were kept: "path (reason)".
    pub skipped_variants: Vec<String>,
    /// In-game document patch files (`document/{matrix,launch}`) fetched because they were missing or wrong.
    pub document_files: usize,
    /// Why part of the document patch was not restored (no pinned data for the region/build, CDN index drift).
    pub document_notes: Vec<String>,
}

/// Root cause of the `Err` returned by [`run`] when `cancel` stopped it; the folder stays resumable.
#[derive(Debug)]
pub struct Cancelled;
impl std::fmt::Display for Cancelled {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str("cancelled")
    }
}
impl std::error::Error for Cancelled {}

fn cancelled() -> anyhow::Error {
    Cancelled.into()
}
fn is_cancelled(e: &anyhow::Error) -> bool {
    e.downcast_ref::<Cancelled>().is_some()
}

/// Loads the pinned per-region sources ("downloads") from `supported-client.json`, in `Region::ALL` order.
pub fn sources(metadata_path: &Path) -> Result<Vec<ClientSource>> {
    let bytes = fs::read(metadata_path).with_context(|| format!("reading {}", metadata_path.display()))?;
    ensure!(bytes.len() <= 1024 * 1024, "supported-client.json is too large");
    let meta: Metadata = serde_json::from_slice(&bytes).context("invalid supported-client.json")?;
    let mut out = Vec::new();
    for region in Region::ALL {
        let raw = meta
            .downloads
            .get(&region)
            .ok_or_else(|| anyhow!("supported-client.json has no download source for {}", region.label()))?;
        out.push(raw.validate(region, &meta.application_version, &meta.originals)?);
    }
    Ok(out)
}

/// Identifies an install folder from its own files; never touches the network.
pub fn detect(dir: &Path) -> Result<Detected> {
    ensure!(dir.is_dir(), "not a folder: {}", dir.display());
    let read = |rel: &str| -> Option<String> {
        let path = safe_path(dir, rel).ok()?;
        fs::metadata(&path).ok().filter(|m| m.is_file() && m.len() <= 64 * 1024)?;
        Some(String::from_utf8_lossy(&fs::read(path).ok()?).into_owned())
    };
    // Global clients describe themselves in KRSDK.bin (`KEY=value`); the CN client has no KRSDK.bin but the
    // official SDK's plain-JSON KRSDKConfig.json with the same keys.
    let identity: BTreeMap<String, String> = match read(KRSDK_BIN) {
        Some(bin) => bin
            .lines()
            .filter_map(|l| l.trim().split_once('='))
            .map(|(k, v)| (k.trim().to_owned(), v.trim().to_owned()))
            .collect(),
        None => read(KRSDK_CONFIG)
            .and_then(|text| serde_json::from_str::<BTreeMap<String, serde_json::Value>>(text.trim_start_matches('\u{feff}')).ok())
            .map(|m| m.into_iter().filter_map(|(k, v)| Some((k, v.as_str()?.trim().to_owned()))).collect())
            .unwrap_or_default(),
    };
    let field = |key: &str| identity.get(key).cloned();
    let region = field("KR_ProjectId").and_then(|id| Region::from_project(&id)).or_else(|| {
        let package = field("KR_PackageName")?;
        Region::ALL.into_iter().find(|r| package.ends_with(&format!(".{}", r.label().to_ascii_lowercase())))
    });
    let version = read("version.json")
        .and_then(|text| {
            let rest = &text[text.find("package_version")? + "package_version".len()..];
            let rest = rest.trim_start_matches(|c: char| c == ':' || c == '"' || c.is_whitespace());
            let v: String = rest.chars().take_while(|c| c.is_ascii_digit() || *c == '.').collect();
            (!v.is_empty()).then_some(v)
        })
        .or_else(|| field("KR_GameVersion").filter(|v| !v.is_empty()));
    let steam = ["steam_appid.txt", "steam_api64.dll", "PGR_Data/Plugins/x86_64/steam_api64.dll"]
        .iter()
        .any(|f| dir.join(f).is_file())
        || dir.ancestors().any(|a| a.file_name().is_some_and(|n| n.eq_ignore_ascii_case("steamapps")));
    let launcher_patched = crate::install::managed_files(dir).map(|(files, _)| !files.is_empty()).unwrap_or(false);
    Ok(Detected { region, version, steam, launcher_patched })
}

/// Fetches the (cached) file lists and works out what `job` would do, from file existence and sizes only
/// (no hashing; `run` is MD5-authoritative). Errors are user-readable.
pub fn plan(source: &ClientSource, job: Job) -> Result<Plan> {
    let data = &source.data;
    let net = Net::new(data)?;
    let no_cancel = AtomicBool::new(false);
    let mut note = None;
    let (dir, route, detected) = match &job {
        Job::Install { dir } => {
            let dir = std::path::absolute(dir).with_context(|| format!("invalid folder {}", dir.display()))?;
            check_install_dir(&dir, source)?;
            (dir, Route::Install, None)
        }
        Job::Repair { dir } | Job::Update { dir } | Job::Adopt { dir } => {
            let dir = checked_root(dir)?;
            let detected = detect(&dir)?;
            if let Some(found) = detected.region {
                ensure!(
                    found == source.region,
                    "this folder is a {} client, not {}; pick the matching region",
                    found.label(),
                    source.region.label()
                );
            }
            let (_, pending) = crate::install::managed_files(&dir).unwrap_or_default();
            ensure!(!pending, "an interrupted AscNet patch transaction is pending in this folder; run Install patch or Restore first");
            if matches!(job, Job::Adopt { .. }) {
                ensure!(dir.join("PGR.exe").is_file(), "PGR.exe not found in {}; this is not a game folder", dir.display());
            } else if fs::read_dir(&dir)?.next().is_none() {
                bail!("the folder is empty; use Install instead");
            }
            (dir, Route::Repair, Some(detected))
        }
    };
    let full = get_list(&net, &data.index_file, &data.index_md5, &no_cancel).context("downloading the official file list")?;
    let mut patch = None;
    let mut route = route;
    if let Some(found) = detected.as_ref().and_then(|d| d.version.as_deref()) {
        match crate::package::compare_versions(found, &source.version) {
            Ok(std::cmp::Ordering::Greater) => bail!(
                "the installed client is {found}, newer than the supported {}; update AscNet Launcher instead",
                source.version
            ),
            Ok(std::cmp::Ordering::Less) => match data.patches.iter().find(|p| {
                crate::package::compare_versions(&p.version, found).is_ok_and(|o| o == std::cmp::Ordering::Equal)
            }) {
                Some(reference) => {
                    let list = get_list(&net, &reference.index_file, &reference.index_file_md5, &no_cancel)
                        .with_context(|| format!("downloading the {found} -> {} patch list", source.version))?;
                    validate_patch(&list)?;
                    patch = Some(Arc::new(Patch { reference: reference.clone(), list }));
                    route = Route::Patch;
                    note = Some(format!("Updating {found} -> {} with Kuro's patch.", source.version));
                }
                None => {
                    note = Some(format!(
                        "Kuro offers no patch from {found}; verifying and repairing the folder against {} instead.",
                        source.version
                    ))
                }
            },
            _ => {}
        }
    } else if !matches!(route, Route::Install) {
        note = Some("The installed version could not be read; verifying and repairing against the supported version.".into());
    }
    let effective = match (&job, route) {
        (Job::Install { .. }, _) => job.clone(),
        (_, Route::Patch) => Job::Update { dir: dir.clone() },
        (Job::Update { .. }, _) => Job::Repair { dir: dir.clone() },
        _ => with_dir(&job, dir.clone()),
    };
    let managed = Managed::new(data);
    let guard = Guard::new(data, &dir);
    let have = |dest: &str, size: u64| present(&dir, dest, size) || (guard.may_differ(dest) && safe_path(&dir, dest).is_ok_and(|p| p.is_file()));
    let scope: Vec<&Entry> = full.resource.iter().filter(|e| matches!(route, Route::Install) || !managed.covers(&e.dest)).collect();
    let mut download = 0u64;
    let mut extracted = 0u64;
    let mut files = 0usize;
    let mut delete = 0usize;
    let mut covered: HashSet<&str> = HashSet::new();
    if let Some(patch) = &patch {
        let archives = patch.list.archives();
        for entry in &patch.list.resource {
            covered.insert(&entry.dest);
            match archives.get(entry.dest.as_str()) {
                Some(members) => {
                    let missing: Vec<&Member> = members.iter().filter(|m| !have(&m.dest, m.size)).collect();
                    for m in members.iter() {
                        covered.insert(&m.dest);
                    }
                    if !missing.is_empty() {
                        download += entry.size;
                        extracted += missing.iter().map(|m| m.size).sum::<u64>();
                        files += missing.len();
                    }
                }
                None if !have(&entry.dest, entry.size) => {
                    download += entry.size;
                    files += 1;
                }
                None => {}
            }
        }
        for gone in &patch.list.delete_files {
            if !covered.contains(gone.as_str()) && safe_path(&dir, gone).is_ok_and(|p| p.is_file()) {
                delete += 1;
            }
        }
    }
    for entry in scope {
        if !covered.contains(entry.dest.as_str()) && !have(&entry.dest, entry.size) {
            download += entry.size;
            files += 1;
        }
    }
    let document = document::inspect(source, &net, &dir, &full, &no_cancel).context("checking the in-game document patch")?;
    let (document_bytes, document_files) = document::estimate(&document, &dir);
    download += document_bytes;
    files += document_files;
    if !document.notes.is_empty() {
        note = Some(note.into_iter().chain(document.notes).collect::<Vec<_>>().join("\n"));
    }
    let required = if download + extracted == 0 { 0 } else { download + extracted + SPACE_MARGIN };
    let free = free_space(&dir)?;
    ensure_space(free, required, &dir)?;
    Ok(Plan {
        job: effective,
        download_bytes: download,
        required_free_bytes: required,
        free_bytes: free,
        files,
        delete,
        note,
        prep: Arc::new(Prep { source: source.clone(), dir, route, full, patch }),
    })
}

/// Blocking; verifies by MD5, resumable after `cancel` or a crash. `cancel` ends with `Err(Cancelled)`.
pub fn run(plan: Plan, cancel: &AtomicBool, progress: &mut dyn FnMut(Progress)) -> Result<Outcome> {
    let prep = &*plan.prep;
    ensure!(!crate::install::game_running()?, "PGR.exe is running; close the game first");
    let data = &prep.source.data;
    if matches!(prep.route, Route::Install) {
        check_install_dir(&prep.dir, &prep.source)?;
        fs::create_dir_all(&prep.dir).with_context(|| format!("creating {}", prep.dir.display()))?;
    }
    let dir = checked_root(&prep.dir)?;
    let state = dir.join(STATE_DIR);
    if state.exists() {
        ensure!(!is_link(&fs::symlink_metadata(&state)?), "refusing link: {}", state.display());
    }
    fs::create_dir_all(state.join("parts")).context("creating the resume folder")?;
    let marker = Marker {
        kind: if matches!(prep.route, Route::Install) { "install" } else { "repair" }.into(),
        region: prep.source.region,
        version: prep.source.version.clone(),
    };
    fs::write(state.join(MARKER), serde_json::to_vec(&marker)?)?;

    let guard = Guard::new(data, &dir);
    let net = Net::new(data)?;
    let ctx = Ctx::new(cancel);
    let mut runner = Runner {
        ctx: &ctx,
        net: &net,
        dir: &dir,
        state: &state,
        guard: &guard,
        emit: progress,
        meter: Meter { t: Instant::now(), bytes: 0, ema: 0.0 },
        variants: Mutex::new(Vec::new()),
        outcome: Outcome::default(),
    };
    runner.ctx.begin(Phase::FetchingIndex, 0, 0);
    runner.tick();
    let managed = Managed::new(data);
    match prep.route {
        Route::Install => {
            let all: Vec<&Entry> = prep.full.resource.iter().collect();
            runner.fix(&all, &data.base_url, false)?;
        }
        Route::Repair => {
            let scope: Vec<&Entry> = prep.full.resource.iter().filter(|e| !managed.covers(&e.dest)).collect();
            runner.fix(&scope, &data.base_url, true)?;
        }
        Route::Patch => {
            let patch = prep.patch.as_ref().expect("patch route has a patch");
            runner.patch(patch)?;
            let scope: Vec<&Entry> = prep.full.resource.iter().filter(|e| !managed.covers(&e.dest)).collect();
            runner.fix(&scope, &data.base_url, true)?;
        }
    }
    runner.document(&prep.source, &prep.full)?;
    runner.ctx.begin(Phase::Finalizing, 0, 0);
    runner.tick();
    runner.outcome.downloaded_bytes = ctx.net_bytes.load(SeqCst);
    let mut variants = runner.variants.into_inner().unwrap();
    variants.sort();
    variants.dedup();
    runner.outcome.skipped_variants = variants;
    let outcome = runner.outcome;
    fs::remove_dir_all(&state).context("removing the resume folder")?;
    Ok(outcome)
}

// ───────────────────────────── manifest ─────────────────────────────

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Metadata {
    application_version: String,
    originals: BTreeMap<String, Vec<String>>,
    #[serde(default)]
    downloads: BTreeMap<Region, RawSource>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct RawSource {
    project_id: String,
    version: String,
    discovery: Vec<String>,
    cdn_list: Vec<String>,
    resources_exclude_path: Vec<String>,
    base_url: String,
    index_file: String,
    index_file_md5: String,
    size: u64,
    patches: Vec<RawPatch>,
    #[serde(default)]
    document_patch: Option<document::RawDocument>,
}

#[derive(Deserialize, Clone, Debug)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct RawPatch {
    version: String,
    base_url: String,
    index_file: String,
    index_file_md5: String,
    size: u64,
}

#[derive(Debug)]
struct SourceData {
    discovery: Vec<String>,
    cdns: Vec<String>,
    exclude: Vec<String>,
    base_url: String,
    index_file: String,
    index_md5: String,
    patches: Vec<RawPatch>,
    originals: BTreeMap<String, Vec<String>>,
    document: Option<document::Document>,
}

impl RawSource {
    fn validate(&self, region: Region, application_version: &str, originals: &BTreeMap<String, Vec<String>>) -> Result<ClientSource> {
        let label = region.label();
        ensure!(
            Region::from_project(&self.project_id) == Some(region),
            "{label}: project id {} does not belong to this region",
            self.project_id
        );
        ensure!(
            self.version == application_version,
            "{label}: pinned download version {} differs from applicationVersion {application_version}",
            self.version
        );
        ensure!(!self.cdn_list.is_empty(), "{label}: no CDN hosts");
        for url in self.cdn_list.iter().chain(&self.discovery) {
            check_host_url(url).with_context(|| format!("{label}: bad URL"))?;
        }
        for cdn in &self.cdn_list {
            ensure!(cdn.ends_with('/'), "{label}: CDN URL must end with '/': {cdn}");
        }
        for path in std::iter::once(&self.base_url).chain([&self.index_file]).chain(self.patches.iter().flat_map(|p| [&p.base_url, &p.index_file])) {
            check_url_path(path).with_context(|| format!("{label}: bad path"))?;
        }
        ensure!(self.base_url.ends_with('/'), "{label}: baseUrl must end with '/'");
        for md5 in std::iter::once(&self.index_file_md5).chain(self.patches.iter().map(|p| &p.index_file_md5)) {
            ensure!(is_md5(md5), "{label}: bad index MD5 {md5}");
        }
        for p in &self.patches {
            ensure!(p.base_url.ends_with('/'), "{label}: patch baseUrl must end with '/'");
            crate::package::compare_versions(&p.version, &p.version).with_context(|| format!("{label}: bad patch version {}", p.version))?;
            ensure!(p.size > 0, "{label}: empty patch {}", p.version);
        }
        for prefix in &self.resources_exclude_path {
            check_rel(prefix)?;
        }
        Ok(ClientSource {
            region,
            version: self.version.clone(),
            full_size: self.size,
            data: Arc::new(SourceData {
                discovery: self.discovery.clone(),
                cdns: self.cdn_list.clone(),
                exclude: self.resources_exclude_path.clone(),
                base_url: self.base_url.clone(),
                index_file: self.index_file.clone(),
                index_md5: self.index_file_md5.to_ascii_lowercase(),
                patches: self
                    .patches
                    .iter()
                    .map(|p| RawPatch { index_file_md5: p.index_file_md5.to_ascii_lowercase(), ..p.clone() })
                    .collect(),
                originals: originals.iter().map(|(k, v)| (k.clone(), v.iter().map(|h| h.to_ascii_lowercase()).collect())).collect(),
                document: self.document_patch.as_ref().map(|d| d.validate(label)).transpose()?,
            }),
        })
    }
}

/// https, or http only for numeric loopback (the test fixture server).
fn check_host_url(url: &str) -> Result<()> {
    let parsed = reqwest::Url::parse(url).with_context(|| format!("invalid URL {url}"))?;
    ensure!(parsed.username().is_empty() && parsed.password().is_none() && parsed.query().is_none() && parsed.fragment().is_none(), "unexpected URL parts in {url}");
    match parsed.scheme() {
        "https" if parsed.host_str().is_some() => Ok(()),
        "http" if parsed.host_str().and_then(|h| h.trim_matches(['[', ']']).parse::<std::net::IpAddr>().ok()).is_some_and(|ip| ip.is_loopback()) => Ok(()),
        _ => bail!("{url} must use HTTPS"),
    }
}

/// A URL path below a CDN root: relative, no dot segments, no query/fragment/backslash.
fn check_url_path(path: &str) -> Result<()> {
    ensure!(
        !path.is_empty()
            && !path.starts_with('/')
            && !path.contains(['\\', '?', '#', '\0'])
            && !path.split('/').any(|p| p == ".." || p == "."),
        "unsafe URL path {path:?}"
    );
    Ok(())
}

fn encode_path(path: &str) -> String {
    let mut out = String::with_capacity(path.len());
    for b in path.bytes() {
        if b.is_ascii_alphanumeric() || b"-._~/".contains(&b) {
            out.push(b as char);
        } else {
            out.push_str(&format!("%{b:02X}"));
        }
    }
    out
}

fn is_md5(s: &str) -> bool {
    s.len() == 32 && s.bytes().all(|b| b.is_ascii_hexdigit())
}

// ───────────────────────────── file lists ─────────────────────────────

#[derive(Deserialize, Clone, Debug)]
#[serde(deny_unknown_fields)]
struct Chunk {
    start: u64,
    end: u64,
    md5: String,
}

#[derive(Deserialize, Clone, Debug)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Entry {
    dest: String,
    md5: String,
    size: u64,
    #[serde(default)]
    chunk_infos: Option<Vec<Chunk>>,
    #[serde(default)]
    from_folder: Option<String>,
}

#[derive(Deserialize, Clone, Debug)]
#[serde(deny_unknown_fields)]
struct Member {
    dest: String,
    md5: String,
    size: u64,
}

#[derive(Deserialize, Clone, Debug)]
#[serde(deny_unknown_fields)]
struct ZipInfo {
    dest: String,
    entries: Vec<Member>,
}

/// Full file list or patch list (`deleteFiles`/`zipInfos` only appear in patches). Unknown fields are rejected so a
/// differential-patch format can never be mistaken for whole files.
#[derive(Deserialize, Debug)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FileList {
    resource: Vec<Entry>,
    #[serde(default)]
    delete_files: Vec<String>,
    #[serde(default)]
    zip_infos: Vec<ZipInfo>,
}

impl FileList {
    fn archives(&self) -> HashMap<&str, &Vec<Member>> {
        self.zip_infos.iter().map(|z| (z.dest.as_str(), &z.entries)).collect()
    }
}

#[derive(Debug)]
struct Patch {
    reference: RawPatch,
    list: Arc<FileList>,
}

fn validate_list(list: &FileList) -> Result<()> {
    let mut seen = HashSet::new();
    for e in &list.resource {
        check_rel(&e.dest)?;
        ensure!(is_md5(&e.md5), "bad MD5 for {}", e.dest);
        ensure!(seen.insert(e.dest.as_str()), "duplicate file in list: {}", e.dest);
        if let Some(chunks) = &e.chunk_infos {
            let mut next = 0u64;
            for c in chunks {
                ensure!(c.start == next && c.end >= c.start && is_md5(&c.md5), "malformed chunk table for {}", e.dest);
                next = c.end + 1;
            }
            ensure!(next == e.size && !chunks.is_empty(), "chunk table does not cover {}", e.dest);
        }
        if let Some(from) = &e.from_folder {
            check_url_path(from)?;
        }
    }
    for gone in &list.delete_files {
        check_rel(gone)?;
    }
    Ok(())
}

fn validate_patch(list: &FileList) -> Result<()> {
    let by_dest: HashSet<&str> = list.resource.iter().map(|e| e.dest.as_str()).collect();
    for z in &list.zip_infos {
        ensure!(by_dest.contains(z.dest.as_str()), "patch archive {} is not listed as a resource", z.dest);
        for m in &z.entries {
            check_rel(&m.dest)?;
            ensure!(is_md5(&m.md5), "bad MD5 for {}", m.dest);
        }
    }
    let archives = list.archives();
    for e in &list.resource {
        if !archives.contains_key(e.dest.as_str()) {
            ensure!(e.from_folder.is_some(), "patch entry {} has no download location", e.dest);
        }
    }
    Ok(())
}

static LISTS: LazyLock<Mutex<HashMap<String, Arc<FileList>>>> = LazyLock::new(Default::default);

/// Downloads a file list, authenticates it against its pinned MD5, and caches it by that MD5 for the process.
fn get_list(net: &Net, path: &str, md5: &str, cancel: &AtomicBool) -> Result<Arc<FileList>> {
    if let Some(list) = LISTS.lock().unwrap().get(md5) {
        return Ok(list.clone());
    }
    let bytes = net.get(path, cancel)?;
    let actual = md5_hex(&bytes);
    ensure!(actual == md5, "the file list {path} does not match its pinned MD5 (expected {md5}, got {actual}); refusing to use it");
    let list: FileList = serde_json::from_slice(&bytes).context("unsupported file list format (only whole-file patches are supported)")?;
    validate_list(&list)?;
    let list = Arc::new(list);
    LISTS.lock().unwrap().insert(md5.to_owned(), list.clone());
    Ok(list)
}

// ───────────────────────────── plan internals ─────────────────────────────

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Route {
    Install,
    Repair,
    Patch,
}

#[derive(Debug)]
struct Prep {
    source: ClientSource,
    dir: PathBuf,
    route: Route,
    full: Arc<FileList>,
    patch: Option<Arc<Patch>>,
}

fn with_dir(job: &Job, dir: PathBuf) -> Job {
    match job {
        Job::Install { .. } => Job::Install { dir },
        Job::Repair { .. } => Job::Repair { dir },
        Job::Update { .. } => Job::Update { dir },
        Job::Adopt { .. } => Job::Adopt { dir },
    }
}

#[derive(Serialize, Deserialize)]
struct Marker {
    kind: String,
    region: Region,
    version: String,
}

/// Install needs an empty folder, or one this very install already started (resume).
fn check_install_dir(dir: &Path, source: &ClientSource) -> Result<()> {
    let Ok(meta) = fs::symlink_metadata(dir) else { return Ok(()) };
    ensure!(meta.is_dir() && !is_link(&meta), "{} is not a regular folder", dir.display());
    if fs::read_dir(dir)?.next().is_none() {
        return Ok(());
    }
    let marker: Option<Marker> = fs::read(dir.join(STATE_DIR).join(MARKER)).ok().and_then(|b| serde_json::from_slice(&b).ok());
    match marker {
        Some(m) if m.kind == "install" && m.region == source.region && m.version == source.version => Ok(()),
        _ => bail!("{} is not empty. Install needs an empty folder; use Repair or Adopt for an existing install.", dir.display()),
    }
}

fn checked_root(dir: &Path) -> Result<PathBuf> {
    let meta = fs::symlink_metadata(dir).with_context(|| format!("cannot open {}", dir.display()))?;
    ensure!(meta.is_dir() && !is_link(&meta), "{} is not a regular folder", dir.display());
    fs::canonicalize(dir).with_context(|| format!("cannot open {}", dir.display()))
}

/// Resource files the game manages itself (`resourcesExcludePath`); Repair/Adopt never touch them.
struct Managed(Vec<String>);
impl Managed {
    fn new(data: &SourceData) -> Self {
        Managed(data.exclude.iter().map(|p| format!("{}/", p.trim_end_matches('/'))).collect())
    }
    fn covers(&self, dest: &str) -> bool {
        self.0.iter().any(|p| dest.starts_with(p.as_str()))
    }
}

fn present(dir: &Path, dest: &str, size: u64) -> bool {
    safe_path(dir, dest).ok().and_then(|p| fs::symlink_metadata(p).ok()).is_some_and(|m| m.is_file() && m.len() == size)
}

fn ensure_space(free: u64, required: u64, dir: &Path) -> Result<()> {
    ensure!(
        free >= required,
        "not enough free disk space for {}: need {:.1} GB, {:.1} GB available",
        dir.display(),
        required as f64 / 1e9,
        free as f64 / 1e9
    );
    Ok(())
}

fn free_space(path: &Path) -> Result<u64> {
    let mut existing = path;
    while !existing.exists() {
        existing = existing.parent().context("the folder has no existing parent")?;
    }
    #[cfg(windows)]
    {
        use std::os::windows::ffi::OsStrExt;
        use windows::{core::PCWSTR, Win32::Storage::FileSystem::GetDiskFreeSpaceExW};
        let wide: Vec<u16> = existing.as_os_str().encode_wide().chain(Some(0)).collect();
        let mut free = 0u64;
        unsafe { GetDiskFreeSpaceExW(PCWSTR(wide.as_ptr()), Some(&mut free), None, None) }
            .with_context(|| format!("reading free space of {}", existing.display()))?;
        Ok(free)
    }
    // ponytail: the launcher is Windows-only; other hosts (tests) report unlimited space.
    #[cfg(not(windows))]
    {
        let _ = existing;
        Ok(u64::MAX)
    }
}

// ───────────────────────────── paths ─────────────────────────────

/// A relative path from a CDN list: forward slashes, normal components only, no streams/drives/reserved endings.
fn check_rel(rel: &str) -> Result<()> {
    let bad = rel.is_empty()
        || rel.len() > 400
        || rel.starts_with('/')
        || rel.contains(['\\', ':', '\0'])
        || rel.chars().any(char::is_control)
        || rel.split('/').any(|p| p.is_empty() || p == "." || p == ".." || p.ends_with('.') || p.ends_with(' '));
    ensure!(!bad, "unsafe path in file list: {rel:?}");
    Ok(())
}

fn is_link(meta: &fs::Metadata) -> bool {
    #[cfg(windows)]
    {
        use std::os::windows::fs::MetadataExt;
        if meta.file_attributes() & 0x400 != 0 {
            return true;
        }
    }
    meta.file_type().is_symlink()
}

/// `root/rel` after validating `rel` and refusing links/reparse points in every existing component.
fn safe_path(root: &Path, rel: &str) -> Result<PathBuf> {
    check_rel(rel)?;
    let mut path = root.to_path_buf();
    for part in rel.split('/') {
        path.push(part);
        if let Ok(meta) = fs::symlink_metadata(&path) {
            ensure!(!is_link(&meta), "refusing link or reparse point: {}", path.display());
        }
    }
    Ok(path)
}

fn part_name(dest: &str) -> String {
    use sha2::{Digest, Sha256};
    let digest = Sha256::digest(dest.as_bytes());
    digest[..8].iter().map(|b| format!("{b:02x}")).collect()
}

/// Atomically moves a verified `temp` file (same volume) to `dest`.
fn install_file(temp: &Path, dest: &Path) -> Result<()> {
    if let Some(parent) = dest.parent() {
        fs::create_dir_all(parent).with_context(|| format!("creating {}", parent.display()))?;
    }
    if let Ok(meta) = fs::symlink_metadata(dest) {
        ensure!(meta.is_file() && !is_link(&meta), "refusing to replace non-regular {}", dest.display());
        if meta.permissions().readonly() {
            let mut perms = meta.permissions();
            #[allow(clippy::permissions_set_readonly_false)]
            perms.set_readonly(false);
            fs::set_permissions(dest, perms)?;
        }
    }
    fs::rename(temp, dest).with_context(|| format!("replacing {} (is it in use?)", dest.display()))
}

// ───────────────────────────── MD5 ─────────────────────────────

/// Kuro publishes MD5 only (no crate for it in the tree), so the hash is implemented here (RFC 1321).
#[derive(Clone)]
struct Md5 {
    state: [u32; 4],
    buf: [u8; 64],
    fill: usize,
    len: u64,
}

static MD5_K: LazyLock<[u32; 64]> = LazyLock::new(|| std::array::from_fn(|i| ((i as f64 + 1.0).sin().abs() * 4294967296.0) as u32));

impl Md5 {
    fn new() -> Self {
        Md5 { state: [0x67452301, 0xefcdab89, 0x98badcfe, 0x10325476], buf: [0; 64], fill: 0, len: 0 }
    }

    fn block(state: &mut [u32; 4], block: &[u8], k: &[u32; 64]) {
        const S: [u32; 16] = [7, 12, 17, 22, 5, 9, 14, 20, 4, 11, 16, 23, 6, 10, 15, 21];
        let m: [u32; 16] = std::array::from_fn(|i| u32::from_le_bytes(block[i * 4..i * 4 + 4].try_into().unwrap()));
        let [mut a, mut b, mut c, mut d] = *state;
        for i in 0..64 {
            let (f, g) = match i / 16 {
                0 => ((b & c) | (!b & d), i),
                1 => ((d & b) | (!d & c), (5 * i + 1) % 16),
                2 => (b ^ c ^ d, (3 * i + 5) % 16),
                _ => (c ^ (b | !d), (7 * i) % 16),
            };
            let f = f.wrapping_add(a).wrapping_add(k[i]).wrapping_add(m[g]);
            a = d;
            d = c;
            c = b;
            b = b.wrapping_add(f.rotate_left(S[(i / 16) * 4 + i % 4]));
        }
        for (s, v) in state.iter_mut().zip([a, b, c, d]) {
            *s = s.wrapping_add(v);
        }
    }

    fn update(&mut self, mut data: &[u8]) {
        let k = &*MD5_K;
        self.len += data.len() as u64;
        if self.fill > 0 {
            let take = (64 - self.fill).min(data.len());
            self.buf[self.fill..self.fill + take].copy_from_slice(&data[..take]);
            self.fill += take;
            data = &data[take..];
            if self.fill < 64 {
                return;
            }
            let block = self.buf;
            Self::block(&mut self.state, &block, k);
            self.fill = 0;
        }
        let mut blocks = data.chunks_exact(64);
        for block in &mut blocks {
            Self::block(&mut self.state, block, k);
        }
        let rest = blocks.remainder();
        self.buf[..rest.len()].copy_from_slice(rest);
        self.fill = rest.len();
    }

    fn finish(mut self) -> String {
        let bits = self.len.wrapping_mul(8);
        let mut pad = vec![0x80u8];
        pad.resize(1 + (119 - self.fill) % 64, 0);
        pad.extend_from_slice(&bits.to_le_bytes());
        self.update(&pad);
        self.state.iter().flat_map(|w| w.to_le_bytes()).map(|b| format!("{b:02x}")).collect()
    }
}

fn md5_hex(data: &[u8]) -> String {
    let mut m = Md5::new();
    m.update(data);
    m.finish()
}

/// MD5 of `len` bytes of `file` starting at `start`; errors if the file is shorter.
fn md5_range(file: &mut File, start: u64, len: u64) -> Result<String> {
    file.seek(SeekFrom::Start(start))?;
    let mut md5 = Md5::new();
    let mut buf = vec![0u8; 256 * 1024];
    let mut left = len;
    while left > 0 {
        let want = buf.len().min(left as usize);
        file.read_exact(&mut buf[..want])?;
        md5.update(&buf[..want]);
        left -= want as u64;
    }
    Ok(md5.finish())
}

fn md5_file(path: &Path) -> Result<String> {
    let mut file = File::open(path)?;
    let len = file.metadata()?.len();
    md5_range(&mut file, 0, len)
}

/// Digest a list entry is pinned with: Kuro's launcher lists use MD5, the in-game document indexes SHA-1.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Hash {
    Md5,
    Sha1,
}

impl Hash {
    /// Lowercase hex digest of `len` bytes of `file` starting at `start`; errors if the file is shorter.
    fn range(self, file: &mut File, start: u64, len: u64) -> Result<String> {
        match self {
            Hash::Md5 => md5_range(file, start, len),
            Hash::Sha1 => {
                use sha1::{Digest, Sha1};
                file.seek(SeekFrom::Start(start))?;
                let mut sha = Sha1::new();
                let mut buf = vec![0u8; 256 * 1024];
                let mut left = len;
                while left > 0 {
                    let want = buf.len().min(left as usize);
                    file.read_exact(&mut buf[..want])?;
                    sha.update(&buf[..want]);
                    left -= want as u64;
                }
                Ok(sha.finalize().iter().map(|b| format!("{b:02x}")).collect())
            }
        }
    }
}

// ───────────────────────────── network ─────────────────────────────

struct Net {
    client: reqwest::blocking::Client,
    discovery: Vec<String>,
    state: Mutex<NetState>,
}

struct NetState {
    cdns: Vec<(String, u32)>,
    discovered: bool,
}

impl Net {
    fn new(data: &SourceData) -> Result<Net> {
        Net::with_hosts(&data.discovery, &data.cdns)
    }

    fn with_hosts(discovery: &[String], cdns: &[String]) -> Result<Net> {
        let client = wine_safe(
            reqwest::blocking::Client::builder()
                .user_agent(concat!("AscNetLauncher/", env!("CARGO_PKG_VERSION")))
                .connect_timeout(Duration::from_secs(15))
                .timeout(REQUEST_TIMEOUT)
                .redirect(reqwest::redirect::Policy::limited(3)),
        )
        .build()
        .context("creating the HTTP client")?;
        Ok(Net {
            client,
            discovery: discovery.to_vec(),
            state: Mutex::new(NetState { cdns: cdns.iter().map(|c| (c.clone(), 0)).collect(), discovered: false }),
        })
    }

    /// Hosts in priority order; hosts that failed repeatedly in a row go last.
    fn order(&self) -> Vec<String> {
        let state = self.state.lock().unwrap();
        let mut hosts: Vec<(usize, &(String, u32))> = state.cdns.iter().enumerate().collect();
        hosts.sort_by_key(|(i, (_, fails))| (*fails >= DEMOTE_AFTER, *i));
        hosts.into_iter().map(|(_, (c, _))| c.clone()).collect()
    }

    fn mark(&self, cdn: &str, ok: bool) {
        if let Some(entry) = self.state.lock().unwrap().cdns.iter_mut().find(|(c, _)| c == cdn) {
            entry.1 = if ok { 0 } else { entry.1 + 1 };
        }
    }

    /// Adds the hosts of the live index (never its paths) once, after every pinned host failed.
    fn discover(&self) {
        {
            let mut state = self.state.lock().unwrap();
            if std::mem::replace(&mut state.discovered, true) {
                return;
            }
        }
        for url in &self.discovery {
            let Ok(response) = self.client.get(url).send().and_then(|r| r.error_for_status()) else { continue };
            let length = response.content_length();
            if length.is_some_and(|n| n > MAX_LIST_BYTES as u64) {
                continue;
            }
            let mut response = response;
            let Ok(body) = read_body(&mut response, length, MAX_LIST_BYTES) else { continue };
            if length.is_some_and(|n| body.len() as u64 != n) || body.len() > MAX_LIST_BYTES {
                continue;
            }
            let mut text = Vec::new();
            let raw = if body.starts_with(&[0x1f, 0x8b]) {
                if flate2::read::GzDecoder::new(&body[..]).take(MAX_LIST_BYTES as u64).read_to_end(&mut text).is_err() {
                    continue;
                }
                &text[..]
            } else {
                &body[..]
            };
            #[derive(Deserialize)]
            struct Cdn {
                url: String,
                #[serde(rename = "P", default)]
                p: u64,
            }
            #[derive(Deserialize)]
            struct Default {
                #[serde(rename = "cdnList")]
                cdn_list: Vec<Cdn>,
            }
            #[derive(Deserialize)]
            struct Index {
                default: Default,
            }
            let Ok(mut index) = serde_json::from_slice::<Index>(raw) else { continue };
            index.default.cdn_list.sort_by_key(|c| c.p);
            let mut state = self.state.lock().unwrap();
            for cdn in index.default.cdn_list {
                if cdn.url.ends_with('/') && check_host_url(&cdn.url).is_ok() && !state.cdns.iter().any(|(c, _)| *c == cdn.url) {
                    state.cdns.push((cdn.url, 0));
                }
            }
            return;
        }
    }

    /// Opens `path` (optionally `bytes=a-b`, inclusive) on the first CDN that answers correctly; rounds with backoff.
    fn open(&self, path: &str, range: Option<(u64, u64)>, cancel: &AtomicBool) -> Result<reqwest::blocking::Response> {
        self.open_host(path, range, cancel).map(|(_, response)| response)
    }

    fn open_host(&self, path: &str, range: Option<(u64, u64)>, cancel: &AtomicBool) -> Result<(String, reqwest::blocking::Response)> {
        let encoded = encode_path(path);
        let mut last = anyhow!("no CDN host configured");
        for round in 0..ROUNDS {
            for cdn in self.order() {
                if cancel.load(SeqCst) {
            return Err(cancelled());
        }
                let url = format!("{cdn}{encoded}");
                let mut request = self.client.get(&url);
                if let Some((a, b)) = range {
                    request = request.header(reqwest::header::RANGE, format!("bytes={a}-{b}"));
                }
                match request.send() {
                    Ok(response) => {
                        let status = response.status();
                        if status.as_u16() == if range.is_some() { 206 } else { 200 } {
                            self.mark(&cdn, true);
                            return Ok((cdn, response));
                        }
                        // A missing file is not the host's fault; server errors are.
                        self.mark(&cdn, status.as_u16() < 500 && status.as_u16() != 429);
                        last = anyhow!("{url}: HTTP {status}");
                    }
                    Err(e) => {
                        self.mark(&cdn, false);
                        last = anyhow!("{url}: {e}");
                    }
                }
            }
            if round == 0 {
                self.discover();
            }
            if round + 1 < ROUNDS {
                sleep_cancellable(retry_backoff(round), cancel)?;
            }
        }
        Err(last.context(format!("all download servers failed for {path}")))
    }

    fn get(&self, path: &str, cancel: &AtomicBool) -> Result<Vec<u8>> {
        let mut last = anyhow!("download failed");
        for _ in 0..ROUNDS {
            if cancel.load(SeqCst) {
                return Err(cancelled());
            }
            let (cdn, mut response) = self.open_host(path, None, cancel)?;
            let length = response.content_length();
            if length.is_some_and(|n| n > MAX_LIST_BYTES as u64) {
                bail!("{path} is unexpectedly large");
            }
            match read_body(&mut response, length, MAX_LIST_BYTES) {
                Ok(body) if body.len() <= MAX_LIST_BYTES && length.map(|n| body.len() as u64 == n).unwrap_or(true) => {
                    return Ok(body);
                }
                Ok(body) if body.len() > MAX_LIST_BYTES => bail!("{path} is unexpectedly large"),
                Ok(body) => {
                    self.demote(&cdn);
                    last = anyhow!("{path} ended after {} bytes, expected {}", body.len(), length.unwrap_or(0));
                }
                Err(error) => {
                    self.demote(&cdn);
                    last = anyhow!(error).context("connection lost");
                }
            }
        }
        Err(last)
    }

    /// One failed body is enough to stop preferring this host. `mark` only
    /// counts, and a host stays first until it has failed `DEMOTE_AFTER` times.
    fn demote(&self, cdn: &str) {
        if let Some(entry) = self.state.lock().unwrap().cdns.iter_mut().find(|(name, _)| name == cdn) {
            entry.1 = entry.1.max(DEMOTE_AFTER);
        }
    }
}

/// Under Wine, a socket read bigger than the bytes still coming does not
/// return until the client timeout. `Connection: close` makes the server end
/// the TCP stream after the body, which is what lets that read finish.
/// HTTP/1.1 is the version that honors the header. Windows keeps keep-alive.
pub fn wine_safe(builder: reqwest::blocking::ClientBuilder) -> reqwest::blocking::ClientBuilder {
    if !crate::install::running_under_wine() {
        return builder;
    }
    let mut headers = reqwest::header::HeaderMap::new();
    headers.insert(reqwest::header::CONNECTION, reqwest::header::HeaderValue::from_static("close"));
    builder.http1_only().default_headers(headers)
}

/// Reads a body in 64 KiB chunks and stops at a known Content-Length.
///
/// `Read::read_to_end` grows its buffer. Under Wine a read bigger than the
/// bytes still coming does not return until the client timeout, so a finished
/// file is reported as `error decoding response body`. Callers also use
/// [`wine_safe`] so the server closes after the body. Game downloads already
/// stop at a known range.
pub fn read_body(reader: &mut impl Read, content_length: Option<u64>, limit: usize) -> std::io::Result<Vec<u8>> {
    let stop = match content_length {
        Some(n) => {
            let n = usize::try_from(n).map_err(|_| std::io::Error::new(std::io::ErrorKind::InvalidData, "response exceeds limit"))?;
            if n > limit {
                return Err(std::io::Error::new(std::io::ErrorKind::InvalidData, "response exceeds limit"));
            }
            n
        }
        None => limit.saturating_add(1),
    };
    let mut body = Vec::new();
    let mut buf = [0u8; 64 * 1024];
    while body.len() < stop {
        let want = buf.len().min(stop - body.len());
        let n = reader.read(&mut buf[..want])?;
        if n == 0 {
            break;
        }
        body.extend_from_slice(&buf[..n]);
    }
    Ok(body)
}

pub(crate) fn retry_backoff(attempt: usize) -> Duration {
    Duration::from_millis((RETRY_BASE_MS << attempt.min(4)).min(8000))
}

fn sleep_cancellable(total: Duration, cancel: &AtomicBool) -> Result<()> {
    let end = Instant::now() + total;
    while Instant::now() < end {
        if cancel.load(SeqCst) {
            return Err(cancelled());
        }
        std::thread::sleep(Duration::from_millis(20).min(total));
    }
    Ok(())
}

// ───────────────────────────── shared progress ─────────────────────────────

struct Ctx<'a> {
    cancel: &'a AtomicBool,
    abort: AtomicBool,
    phase: Mutex<Phase>,
    files_done: AtomicUsize,
    files_total: AtomicUsize,
    bytes_done: AtomicU64,
    bytes_total: AtomicU64,
    /// Bytes actually transferred over the network (resumed bytes excluded).
    net_bytes: AtomicU64,
    extracting: AtomicUsize,
    current: Mutex<Option<String>>,
}

struct Meter {
    t: Instant,
    bytes: u64,
    ema: f64,
}

impl<'a> Ctx<'a> {
    fn new(cancel: &'a AtomicBool) -> Self {
        Ctx {
            cancel,
            abort: AtomicBool::new(false),
            phase: Mutex::new(Phase::FetchingIndex),
            files_done: AtomicUsize::new(0),
            files_total: AtomicUsize::new(0),
            bytes_done: AtomicU64::new(0),
            bytes_total: AtomicU64::new(0),
            net_bytes: AtomicU64::new(0),
            extracting: AtomicUsize::new(0),
            current: Mutex::new(None),
        }
    }

    fn stop(&self) -> bool {
        self.cancel.load(SeqCst) || self.abort.load(SeqCst)
    }

    fn begin(&self, phase: Phase, files: usize, bytes: u64) {
        *self.phase.lock().unwrap() = phase;
        self.files_done.store(0, SeqCst);
        self.files_total.store(files, SeqCst);
        self.bytes_done.store(0, SeqCst);
        self.bytes_total.store(bytes, SeqCst);
        *self.current.lock().unwrap() = None;
    }

    fn set_current(&self, name: &str) {
        *self.current.lock().unwrap() = Some(name.to_owned());
    }

    fn snapshot(&self, meter: &mut Meter) -> Progress {
        let net = self.net_bytes.load(SeqCst);
        let dt = meter.t.elapsed();
        if dt >= Duration::from_millis(250) {
            let instant = net.saturating_sub(meter.bytes) as f64 / dt.as_secs_f64();
            meter.ema = if meter.ema == 0.0 { instant } else { 0.7 * meter.ema + 0.3 * instant };
            meter.t = Instant::now();
            meter.bytes = net;
        }
        let mut phase = *self.phase.lock().unwrap();
        if phase == Phase::Downloading && self.extracting.load(SeqCst) > 0 {
            phase = Phase::Extracting;
        }
        Progress {
            phase,
            files_done: self.files_done.load(SeqCst),
            files_total: self.files_total.load(SeqCst),
            bytes_done: self.bytes_done.load(SeqCst),
            bytes_total: self.bytes_total.load(SeqCst),
            current: self.current.lock().unwrap().clone(),
            bytes_per_second: meter.ema as u64,
        }
    }
}

/// Runs `work` over `items` on `workers` threads while the calling thread reports progress. The first error stops
/// everything; a raised cancel flag always surfaces as `Cancelled`.
fn pool<T: Sync, R: Send>(
    ctx: &Ctx,
    items: &[T],
    workers: usize,
    work: &(dyn Fn(&T) -> Result<R> + Sync),
    tick: &mut dyn FnMut(),
) -> Result<Vec<R>> {
    let next = AtomicUsize::new(0);
    let alive = AtomicUsize::new(workers);
    let results = Mutex::new(Vec::with_capacity(items.len()));
    let failure: Mutex<Option<anyhow::Error>> = Mutex::new(None);
    std::thread::scope(|scope| {
        for _ in 0..workers {
            scope.spawn(|| {
                while !ctx.stop() {
                    let i = next.fetch_add(1, SeqCst);
                    let Some(item) = items.get(i) else { break };
                    match work(item) {
                        Ok(r) => results.lock().unwrap().push(r),
                        Err(e) => {
                            let mut slot = failure.lock().unwrap();
                            if slot.is_none() {
                                *slot = Some(e);
                            }
                            ctx.abort.store(true, SeqCst);
                        }
                    }
                }
                alive.fetch_sub(1, SeqCst);
            });
        }
        while alive.load(SeqCst) > 0 {
            std::thread::sleep(Duration::from_millis(100));
            tick();
        }
    });
    tick();
    if ctx.cancel.load(SeqCst) {
        return Err(cancelled());
    }
    if let Some(e) = failure.into_inner().unwrap() {
        return Err(e);
    }
    Ok(results.into_inner().unwrap())
}

// ───────────────────────────── variant policy ─────────────────────────────

/// Decides whether a file that differs from the official index is a deliberate variant to keep.
struct Guard {
    /// supported-client.json `originals`: path -> accepted retail SHA-256 values (lowercase).
    allow: BTreeMap<String, Vec<String>>,
    /// Files AscNet patched (path -> installed SHA-256), from the launcher state.
    managed: BTreeMap<String, String>,
    steam: bool,
}

impl Guard {
    fn new(data: &SourceData, dir: &Path) -> Guard {
        let managed = crate::install::managed_files(dir).map(|(files, _)| files).unwrap_or_default();
        let steam = detect(dir).is_ok_and(|d| d.steam);
        Guard { allow: data.originals.clone(), managed, steam }
    }

    /// Could a file at `dest` legitimately differ from the index? (Used for size-only estimates.)
    fn may_differ(&self, dest: &str) -> bool {
        self.allow.contains_key(dest) || self.managed.contains_key(dest) || (self.steam && dest == KRSDK_BIN)
    }

    fn variant(&self, dest: &str, path: &Path) -> Result<Option<&'static str>> {
        if self.steam && dest == KRSDK_BIN {
            return Ok(Some("Steam build"));
        }
        let allowed = self.allow.get(dest);
        let patched = self.managed.get(dest);
        if allowed.is_none() && patched.is_none() {
            return Ok(None);
        }
        let sha = crate::package::sha256_file(path)?;
        if patched.is_some_and(|p| p.eq_ignore_ascii_case(&sha)) {
            return Ok(Some("AscNet patch"));
        }
        if allowed.is_some_and(|a| a.contains(&sha)) {
            return Ok(Some("accepted retail variant"));
        }
        Ok(None)
    }
}

enum Verdict {
    Ok,
    Variant(&'static str),
    Fetch,
}

fn verdict(dir: &Path, guard: &Guard, dest: &str, size: u64, md5: &str) -> Result<Verdict> {
    let path = safe_path(dir, dest)?;
    let meta = match fs::symlink_metadata(&path) {
        Ok(meta) => meta,
        Err(_) => return Ok(Verdict::Fetch),
    };
    ensure!(meta.is_file() && !is_link(&meta), "refusing non-regular file {}", path.display());
    if meta.len() == size && md5_file(&path)? == md5 {
        return Ok(Verdict::Ok);
    }
    Ok(guard.variant(dest, &path)?.map_or(Verdict::Fetch, Verdict::Variant))
}

// ───────────────────────────── execution ─────────────────────────────

struct Item {
    dest: String,
    size: u64,
    /// Expected digest (hex) in `hash`'s algorithm.
    md5: String,
    hash: Hash,
    chunks: Option<Vec<Chunk>>,
    /// URL path below the CDN root.
    url: String,
    /// Some = a krzip archive; only these members still need extracting.
    members: Option<Vec<Member>>,
    repair: bool,
}

struct Runner<'a> {
    ctx: &'a Ctx<'a>,
    net: &'a Net,
    dir: &'a Path,
    state: &'a Path,
    guard: &'a Guard,
    emit: &'a mut dyn FnMut(Progress),
    meter: Meter,
    variants: Mutex<Vec<String>>,
    outcome: Outcome,
}

impl Runner<'_> {
    fn tick(&mut self) {
        let snapshot = self.ctx.snapshot(&mut self.meter);
        (self.emit)(snapshot);
    }

    fn pool<T: Sync, R: Send>(&mut self, items: &[T], workers: usize, work: &(dyn Fn(&T) -> Result<R> + Sync)) -> Result<Vec<R>> {
        let ctx = self.ctx;
        let (emit, meter) = (&mut *self.emit, &mut self.meter);
        pool(ctx, items, workers.max(1), work, &mut || emit(ctx.snapshot(meter)))
    }

    fn note_variant(&self, dest: &str, reason: &str) {
        self.variants.lock().unwrap().push(format!("{dest} ({reason})"));
    }

    /// Verify `entries` by MD5, then fetch what is missing or corrupt from `base`.
    fn fix(&mut self, entries: &[&Entry], base: &str, repair: bool) -> Result<()> {
        let total: u64 = entries.iter().map(|e| e.size).sum();
        self.ctx.begin(Phase::Verifying, entries.len(), total);
        let threads = std::thread::available_parallelism().map_or(4, |n| n.get()).clamp(2, WORKERS);
        let (ctx, dir, guard) = (self.ctx, self.dir, self.guard);
        let verdicts = self.pool(entries, threads, &|e: &&Entry| {
            ctx.set_current(&e.dest);
            let v = verdict(dir, guard, &e.dest, e.size, &e.md5)?;
            ctx.files_done.fetch_add(1, SeqCst);
            ctx.bytes_done.fetch_add(e.size, SeqCst);
            Ok((e.dest.as_str(), v))
        })?;
        let mut fetch: HashSet<&str> = HashSet::new();
        for (dest, v) in verdicts {
            match v {
                Verdict::Ok => {}
                Verdict::Variant(reason) => self.note_variant(dest, reason),
                Verdict::Fetch => {
                    fetch.insert(dest);
                }
            }
        }
        let mut items: Vec<Item> = entries
            .iter()
            .filter(|e| fetch.contains(e.dest.as_str()))
            .map(|e| Item {
                dest: e.dest.clone(),
                size: e.size,
                md5: e.md5.clone(),
                chunks: e.chunk_infos.clone(),
                url: format!("{base}{}", e.dest),
                members: None,
                hash: Hash::Md5,
                repair,
            })
            .collect();
        items.sort_by_key(|i| std::cmp::Reverse(i.size));
        self.download(items)
    }

    fn download(&mut self, items: Vec<Item>) -> Result<()> {
        self.download_via(self.net, items)
    }

    fn download_via(&mut self, net: &Net, items: Vec<Item>) -> Result<()> {
        let total: u64 = items.iter().map(|i| i.size).sum();
        self.ctx.begin(Phase::Downloading, items.len(), total);
        let (ctx, dir, state, guard) = (self.ctx, self.dir, self.state, self.guard);
        let notes = Mutex::new(Vec::new());
        let repaired = AtomicUsize::new(0);
        self.pool(&items, WORKERS.min(items.len()), &|item: &Item| {
            ctx.set_current(&item.dest);
            fetch_item(ctx, net, dir, state, guard, item, &notes)?;
            if item.repair {
                repaired.fetch_add(1, SeqCst);
            }
            ctx.files_done.fetch_add(1, SeqCst);
            Ok(())
        })?;
        self.outcome.repaired += repaired.load(SeqCst);
        for (dest, reason) in notes.into_inner().unwrap() {
            self.note_variant(&dest, reason);
        }
        Ok(())
    }

    /// Apply a whole-file patch: fetch/extract what the new version changed, then delete what it removed.
    fn patch(&mut self, patch: &Patch) -> Result<()> {
        let list = &patch.list;
        let archives = list.archives();
        let total: u64 = list.resource.iter().map(|e| e.size).sum();
        self.ctx.begin(Phase::Verifying, list.resource.len(), total);
        let (ctx, dir, guard) = (self.ctx, self.dir, self.guard);
        let archives = &archives;
        let checked = self.pool(&list.resource, WORKERS, &|e: &Entry| {
            ctx.set_current(&e.dest);
            let result = match archives.get(e.dest.as_str()) {
                None => match verdict(dir, guard, &e.dest, e.size, &e.md5)? {
                    Verdict::Fetch => (Some(None), Vec::new()),
                    Verdict::Variant(reason) => (None, vec![(e.dest.clone(), reason)]),
                    Verdict::Ok => (None, Vec::new()),
                },
                Some(members) => {
                    let mut needed = Vec::new();
                    let mut variants = Vec::new();
                    for m in members.iter() {
                        match verdict(dir, guard, &m.dest, m.size, &m.md5)? {
                            Verdict::Fetch => needed.push(m.clone()),
                            Verdict::Variant(reason) => variants.push((m.dest.clone(), reason)),
                            Verdict::Ok => {}
                        }
                    }
                    ((!needed.is_empty()).then_some(Some(needed)), variants)
                }
            };
            ctx.files_done.fetch_add(1, SeqCst);
            ctx.bytes_done.fetch_add(e.size, SeqCst);
            Ok((e.dest.clone(), result))
        })?;
        let mut wanted: HashMap<String, Option<Vec<Member>>> = HashMap::new();
        for (dest, (fetch, variants)) in checked {
            if let Some(members) = fetch {
                wanted.insert(dest, members);
            }
            for (path, reason) in variants {
                self.note_variant(&path, reason);
            }
        }
        let mut items = Vec::new();
        for e in &list.resource {
            let Some(members) = wanted.remove(e.dest.as_str()) else {
                continue;
            };
            let from = e.from_folder.clone().unwrap_or_else(|| patch.reference.base_url.clone());
            items.push(Item {
                dest: e.dest.clone(),
                size: e.size,
                md5: e.md5.clone(),
                chunks: e.chunk_infos.clone(),
                url: format!("{from}{}", e.dest),
                members,
                hash: Hash::Md5,
                repair: false,
            });
        }
        items.sort_by_key(|i| std::cmp::Reverse(i.size));
        self.download(items)?;

        // Never delete something this same patch just wrote or replaced.
        let mut keep: HashSet<&str> = list.resource.iter().map(|e| e.dest.as_str()).collect();
        keep.extend(list.zip_infos.iter().flat_map(|z| z.entries.iter().map(|m| m.dest.as_str())));
        self.ctx.begin(Phase::Deleting, list.delete_files.len(), 0);
        for gone in &list.delete_files {
            if self.ctx.cancel.load(SeqCst) {
            return Err(cancelled());
        }
            self.ctx.files_done.fetch_add(1, SeqCst);
            if keep.contains(gone.as_str()) || self.guard.managed.contains_key(gone) {
                continue;
            }
            let path = safe_path(self.dir, gone)?;
            let Ok(meta) = fs::symlink_metadata(&path) else { continue };
            ensure!(meta.is_file() && !is_link(&meta), "refusing to delete non-regular {}", path.display());
            self.ctx.set_current(gone);
            fs::remove_file(&path).with_context(|| format!("deleting {}", path.display()))?;
            self.outcome.deleted += 1;
            // Prune folders emptied by the deletion, never above the install folder.
            let mut parent = path.parent();
            while let Some(p) = parent.filter(|p| *p != self.dir && p.starts_with(self.dir)) {
                if fs::remove_dir(p).is_err() {
                    break;
                }
                parent = p.parent();
            }
            if self.outcome.deleted % 512 == 0 {
                self.tick();
            }
        }
        self.tick();
        Ok(())
    }
}

type Notes = Mutex<Vec<(String, &'static str)>>;

/// Download (resumable, verified) one item and put it in place: a plain file is renamed into the install folder, a
/// krzip archive has its listed members extracted with per-entry MD5 and is then discarded.
fn fetch_item(ctx: &Ctx, net: &Net, dir: &Path, state: &Path, guard: &Guard, item: &Item, notes: &Notes) -> Result<()> {
    let part = state.join("parts").join(format!("{}.part", part_name(&item.dest)));
    download_file(ctx, net, &item.url, item.size, item.hash, &item.md5, item.chunks.as_deref(), &part)?;
    match &item.members {
        None => install_file(&part, &safe_path(dir, &item.dest)?),
        Some(members) => {
            ctx.extracting.fetch_add(1, SeqCst);
            let result = extract(ctx, dir, state, guard, &part, members, notes);
            ctx.extracting.fetch_sub(1, SeqCst);
            result?;
            let _ = fs::remove_file(&part);
            Ok(())
        }
    }
}

fn extract(ctx: &Ctx, dir: &Path, state: &Path, guard: &Guard, archive: &Path, members: &[Member], notes: &Notes) -> Result<()> {
    let mut zip = zip::ZipArchive::new(BufReader::new(File::open(archive)?)).context("the downloaded archive is not a valid zip")?;
    for m in members {
        if ctx.stop() {
            return Err(cancelled());
        }
        ctx.set_current(&m.dest);
        let target = safe_path(dir, &m.dest)?;
        // Resume after a crash mid-extraction: finished members are already in place.
        match verdict(dir, guard, &m.dest, m.size, &m.md5)? {
            Verdict::Ok => continue,
            Verdict::Variant(reason) => {
                notes.lock().unwrap().push((m.dest.clone(), reason));
                continue;
            }
            Verdict::Fetch => {}
        }
        let mut entry = zip.by_name(&m.dest).with_context(|| format!("{} is missing from the archive", m.dest))?;
        let temp = state.join("parts").join(format!("{}.x", part_name(&m.dest)));
        let mut out = File::create(&temp)?;
        let mut md5 = Md5::new();
        let mut buf = vec![0u8; 256 * 1024];
        let mut written = 0u64;
        loop {
            if ctx.stop() {
            return Err(cancelled());
        }
            let n = (&mut entry).take(m.size + 1 - written).read(&mut buf)?;
            if n == 0 {
                break;
            }
            written += n as u64;
            ensure!(written <= m.size, "{} is larger than its listed size", m.dest);
            md5.update(&buf[..n]);
            out.write_all(&buf[..n])?;
        }
        drop(out);
        let actual = md5.finish();
        if written != m.size || actual != m.md5 {
            let _ = fs::remove_file(&temp);
            bail!("{} extracted from the archive does not match its checksum", m.dest);
        }
        install_file(&temp, &target)?;
    }
    Ok(())
}

/// Leaves a complete, verified copy of the file in `part`. Chunked files resume at chunk granularity (verified
/// prefix is kept); plain files resume from the existing length and are discarded if the final MD5 disagrees.
#[allow(clippy::too_many_arguments)]
fn download_file(ctx: &Ctx, net: &Net, url: &str, size: u64, hash: Hash, md5: &str, chunks: Option<&[Chunk]>, part: &Path) -> Result<()> {
    let mut file = OpenOptions::new().read(true).write(true).create(true).truncate(false).open(part)?;
    let have = file.metadata()?.len();
    let mut segments: Vec<(u64, u64, &str)> = match chunks {
        Some(chunks) => chunks.iter().map(|c| (c.start, c.end + 1, c.md5.as_str())).collect(),
        None => vec![(0, size, md5)],
    };
    // ponytail: files are fetched sequentially per worker; split chunks across workers if throughput needs it.
    let mut resume = 0u64;
    if chunks.is_some() {
        for &(start, end, sum) in &segments {
            if ctx.stop() {
            return Err(cancelled());
        }
            if have >= end && hash.range(&mut file, start, end - start)? == sum {
                resume = end;
                ctx.bytes_done.fetch_add(end - start, SeqCst);
            } else {
                break;
            }
        }
        segments.retain(|&(start, _, _)| start >= resume);
    } else {
        resume = have.min(size);
        ctx.bytes_done.fetch_add(resume, SeqCst);
        if have > size {
            file.set_len(size)?;
        }
    }
    for (start, end, sum) in segments {
        fetch_segment(ctx, net, url, &mut file, hash, start, end, sum, if chunks.is_some() { start } else { resume })?;
    }
    file.set_len(size)?;
    Ok(())
}

/// Fetches bytes `start..end` into `file`, retrying (failing over CDNs inside `Net::open`) until they hash to `md5`.
#[allow(clippy::too_many_arguments)]
fn fetch_segment(ctx: &Ctx, net: &Net, url: &str, file: &mut File, hash: Hash, start: u64, end: u64, md5: &str, resume_from: u64) -> Result<()> {
    let mut at = resume_from.clamp(start, end);
    let mut last = anyhow!("download failed");
    for attempt in 0..SEGMENT_ATTEMPTS {
        if ctx.stop() {
            return Err(cancelled());
        }
        file.set_len(at)?;
        match transfer(ctx, net, url, file, at, end) {
            Ok(()) => {
                if hash.range(file, start, end - start)? == md5 {
                    return Ok(());
                }
                // Corrupt data: forget the segment (and its progress) and fetch it again.
                ctx.bytes_done.fetch_sub((end - start).min(ctx.bytes_done.load(SeqCst)), SeqCst);
                at = start;
                last = anyhow!("checksum mismatch for {url} bytes {start}-{}", end - 1);
            }
            Err(e) if is_cancelled(&e) => return Err(e),
            Err(e) => {
                at = file.stream_position().unwrap_or(start).clamp(start, end);
                last = e;
            }
        }
        sleep_cancellable(retry_backoff(attempt), ctx.cancel)?;
    }
    Err(last.context(format!("giving up on {url}")))
}

/// Streams `from..end` of `url` into `file` (positioned by this call) using bounded range requests.
fn transfer(ctx: &Ctx, net: &Net, url: &str, file: &mut File, from: u64, end: u64) -> Result<()> {
    file.seek(SeekFrom::Start(from))?;
    let mut pos = from;
    let mut buf = vec![0u8; 64 * 1024];
    while pos < end {
        let stop = (pos + STEP).min(end);
        let mut response = net.open(url, Some((pos, stop - 1)), ctx.cancel)?;
        let mut left = stop - pos;
        while left > 0 {
            if ctx.stop() {
            return Err(cancelled());
        }
            let want = buf.len().min(left as usize);
            let n = response.read(&mut buf[..want]).context("connection lost")?;
            ensure!(n > 0, "connection closed early");
            file.write_all(&buf[..n])?;
            left -= n as u64;
            pos += n as u64;
            ctx.bytes_done.fetch_add(n as u64, SeqCst);
            ctx.net_bytes.fetch_add(n as u64, SeqCst);
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn retry_backoff_doubles_then_stops_growing() {
        assert_eq!(retry_backoff(0), Duration::from_millis(RETRY_BASE_MS));
        assert_eq!(retry_backoff(1), Duration::from_millis(RETRY_BASE_MS * 2));
        assert_eq!(retry_backoff(2), Duration::from_millis(RETRY_BASE_MS * 4));
        assert_eq!(retry_backoff(3), Duration::from_millis(RETRY_BASE_MS * 8));
        assert_eq!(retry_backoff(4), Duration::from_millis(RETRY_BASE_MS * 16));
        assert_eq!(retry_backoff(20), retry_backoff(4));
    }

    use serde_json::{json, Value};
    use sha2::{Digest, Sha256};
    use std::io::{BufRead, BufWriter};
    use std::net::{TcpListener, TcpStream};

    struct Stop {
        data: &'static [u8],
        pos: usize,
        past: Arc<AtomicUsize>,
    }

    impl Read for Stop {
        fn read(&mut self, buf: &mut [u8]) -> std::io::Result<usize> {
            if self.pos >= self.data.len() {
                self.past.fetch_add(1, SeqCst);
                return Ok(0);
            }
            let n = buf.len().min(self.data.len() - self.pos);
            buf[..n].copy_from_slice(&self.data[self.pos..self.pos + n]);
            self.pos += n;
            Ok(n)
        }
    }

    #[test]
    fn read_body_stops_at_content_length() {
        let past = Arc::new(AtomicUsize::new(0));
        let mut reader = Stop { data: b"abcdefghij", pos: 0, past: past.clone() };
        let body = read_body(&mut reader, Some(10), 1024).unwrap();
        assert_eq!(body, b"abcdefghij");
        assert_eq!(past.load(SeqCst), 0);
    }

    #[test]
    fn read_body_returns_before_a_keep_alive_server_closes() {
        let body = b"finished-download-body-0123456789";
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let addr = listener.local_addr().unwrap();
        std::thread::spawn(move || {
            let Ok((mut stream, _)) = listener.accept() else { return };
            let mut buf = [0u8; 4096];
            let _ = stream.read(&mut buf);
            let header = format!(
                "HTTP/1.1 200 OK\r\nContent-Length: {}\r\nConnection: keep-alive\r\n\r\n",
                body.len()
            );
            let _ = stream.write_all(header.as_bytes());
            let _ = stream.write_all(body);
            let _ = stream.flush();
            std::thread::sleep(Duration::from_secs(15));
        });
        let client = reqwest::blocking::Client::builder().timeout(Duration::from_secs(8)).no_proxy().build().unwrap();
        let started = Instant::now();
        let mut response = client.get(format!("http://{addr}/file")).send().unwrap();
        let length = response.content_length();
        let got = read_body(&mut response, length, 1024 * 1024).unwrap();
        assert_eq!(got, body);
        assert!(started.elapsed() < Duration::from_secs(2), "still waiting after the body: {:?}", started.elapsed());
    }

    const PREFIX: &str = "PGR_Data/StreamingAssets/resource";
    const SDK_DLL: &str = "PGR_Data/Plugins/KRSDK.dll";

    // ── local HTTP fixture ──

    struct Req {
        path: String,
        range: Option<(u64, u64)>,
    }
    struct Resp {
        status: u16,
        body: Vec<u8>,
        content_range: Option<String>,
    }
    type Hook = Arc<dyn Fn(&Req, Resp) -> Resp + Send + Sync>;

    fn serve(handler: impl Fn(Req) -> Resp + Send + Sync + 'static) -> String {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let addr = listener.local_addr().unwrap();
        let handler = Arc::new(handler);
        std::thread::spawn(move || {
            for stream in listener.incoming().flatten() {
                let handler = handler.clone();
                std::thread::spawn(move || connection(stream, &*handler));
            }
        });
        format!("http://{addr}/")
    }

    fn connection(stream: TcpStream, handler: &dyn Fn(Req) -> Resp) {
        let mut reader = std::io::BufReader::new(stream.try_clone().unwrap());
        let mut first = String::new();
        if reader.read_line(&mut first).unwrap_or(0) == 0 {
            return;
        }
        let path = first.split_whitespace().nth(1).unwrap_or("/").trim_start_matches('/').to_owned();
        let mut range = None;
        loop {
            let mut line = String::new();
            if reader.read_line(&mut line).unwrap_or(0) == 0 || line.trim().is_empty() {
                break;
            }
            if let Some(v) = line.to_ascii_lowercase().strip_prefix("range: bytes=") {
                let (a, b) = v.trim().split_once('-').unwrap();
                range = Some((a.parse().unwrap(), b.parse().unwrap()));
            }
        }
        let resp = handler(Req { path, range });
        if resp.status == 0 {
            return; // drop the connection
        }
        let mut out = BufWriter::new(stream);
        let _ = write!(out, "HTTP/1.1 {} X\r\nContent-Length: {}\r\nConnection: close\r\n", resp.status, resp.body.len());
        if let Some(r) = resp.content_range {
            let _ = write!(out, "Content-Range: {r}\r\n");
        }
        let _ = write!(out, "\r\n");
        let _ = out.write_all(&resp.body);
        let _ = out.flush();
    }

    fn ok(status: u16, body: Vec<u8>) -> Resp {
        Resp { status, body, content_range: None }
    }

    struct World {
        store: Arc<Mutex<HashMap<String, Vec<u8>>>>,
        log: Arc<Mutex<Vec<(String, u64)>>>,
        hook: Arc<Mutex<Option<Hook>>>,
        base: String,
        full_md5: String,
        patches: Vec<RawPatch>,
    }

    fn blob(seed: u8, len: usize) -> Vec<u8> {
        let mut x = seed as u32 + 1;
        (0..len)
            .map(|_| {
                x = x.wrapping_mul(1664525).wrapping_add(1013904223);
                (x >> 24) as u8
            })
            .collect()
    }

    fn entry(dest: &str, data: &[u8], chunk: Option<usize>) -> Value {
        let mut v = json!({"dest": dest, "md5": md5_hex(data), "size": data.len()});
        if let Some(c) = chunk {
            v["chunkInfos"] = data
                .chunks(c)
                .enumerate()
                .map(|(i, part)| json!({"start": i * c, "end": i * c + part.len() - 1, "md5": md5_hex(part)}))
                .collect();
        }
        v
    }

    fn sha(data: &[u8]) -> String {
        format!("{:x}", Sha256::digest(data))
    }

    impl World {
        /// `files`: (dest, data, chunk size). Publishes them under `r/zip/` plus the list at `r/indexFile.json`.
        fn new(files: &[(&str, Vec<u8>, Option<usize>)]) -> World {
            let store: Arc<Mutex<HashMap<String, Vec<u8>>>> = Default::default();
            let log: Arc<Mutex<Vec<(String, u64)>>> = Default::default();
            let hook: Arc<Mutex<Option<Hook>>> = Default::default();
            let list = json!({"resource": files.iter().map(|(d, b, c)| entry(d, b, *c)).collect::<Vec<_>>()});
            let list = serde_json::to_vec(&list).unwrap();
            {
                let mut s = store.lock().unwrap();
                for (d, b, _) in files {
                    s.insert(format!("r/zip/{d}"), b.clone());
                }
                s.insert("r/indexFile.json".into(), list.clone());
            }
            let base = Self::spawn(&store, &log, &hook);
            World { store, log, hook, base, full_md5: md5_hex(&list), patches: vec![] }
        }

        fn spawn(store: &Arc<Mutex<HashMap<String, Vec<u8>>>>, log: &Arc<Mutex<Vec<(String, u64)>>>, hook: &Arc<Mutex<Option<Hook>>>) -> String {
            let (store, log, hook) = (store.clone(), log.clone(), hook.clone());
            serve(move |req| {
                log.lock().unwrap().push((req.path.clone(), req.range.map_or(0, |r| r.0)));
                let data = store.lock().unwrap().get(&req.path).cloned();
                let resp = match (data, req.range) {
                    (None, _) => ok(404, vec![]),
                    (Some(d), None) => ok(200, d),
                    (Some(d), Some((a, b))) => Resp {
                        status: 206,
                        content_range: Some(format!("bytes {a}-{b}/{}", d.len())),
                        body: d[a as usize..=(b as usize).min(d.len() - 1)].to_vec(),
                    },
                };
                let hook = hook.lock().unwrap().clone();
                match hook {
                    Some(h) => h(&req, resp),
                    None => resp,
                }
            })
        }

        fn set_hook(&self, hook: impl Fn(&Req, Resp) -> Resp + Send + Sync + 'static) {
            *self.hook.lock().unwrap() = Some(Arc::new(hook));
        }

        fn requests_for(&self, path: &str) -> Vec<u64> {
            self.log.lock().unwrap().iter().filter(|(p, _)| p == &format!("r/zip/{path}")).map(|(_, s)| *s).collect()
        }

        /// Patch from `from` containing whole files (published under `r/zip/` like Kuro's `fromFolder`), krzip
        /// archives (`name`, zip bytes, members) and delete paths.
        fn add_patch(&mut self, from: &str, files: &[(&str, Vec<u8>)], krzips: &[(&str, Vec<u8>, Vec<(&str, Vec<u8>)>)], delete: &[&str]) {
            let mut resource: Vec<Value> = Vec::new();
            for (d, b) in files {
                let mut e = entry(d, b, None);
                e["fromFolder"] = json!("r/zip/");
                resource.push(e);
                self.store.lock().unwrap().insert(format!("r/zip/{d}"), b.clone());
            }
            let mut zip_infos = Vec::new();
            for (name, bytes, members) in krzips {
                resource.push(entry(name, bytes, Some(100)));
                self.store.lock().unwrap().insert(format!("p/{from}/resources/{name}"), bytes.clone());
                zip_infos.push(json!({"dest": name, "entries": members.iter().map(|(d, b)| entry(d, b, None)).collect::<Vec<_>>()}));
            }
            let list = serde_json::to_vec(&json!({"resource": resource, "deleteFiles": delete, "zipInfos": zip_infos})).unwrap();
            self.store.lock().unwrap().insert(format!("p/{from}/indexFile.json"), list.clone());
            self.patches.push(RawPatch {
                version: from.into(),
                base_url: format!("p/{from}/resources/"),
                index_file: format!("p/{from}/indexFile.json"),
                index_file_md5: md5_hex(&list),
                size: 1,
            });
        }

        fn source_with(&self, cdns: Vec<String>, discovery: Vec<String>, originals: &[(&str, &[Vec<u8>])]) -> ClientSource {
            ClientSource {
                region: Region::En,
                version: "4.8.0".into(),
                full_size: 0,
                data: Arc::new(SourceData {
                    discovery,
                    cdns,
                    exclude: vec![PREFIX.into()],
                    base_url: "r/zip/".into(),
                    index_file: "r/indexFile.json".into(),
                    index_md5: self.full_md5.clone(),
                    patches: self.patches.clone(),
                    originals: originals.iter().map(|(k, v)| (k.to_string(), v.iter().map(|b| sha(b)).collect())).collect(),
                    document: None,
                }),
            }
        }

        fn source(&self) -> ClientSource {
            self.source_with(vec![self.base.clone()], vec![], &[])
        }
    }

    struct Tmp(PathBuf);
    impl Tmp {
        fn new() -> Tmp {
            let p = std::env::temp_dir().join(format!("ascnet-dl-{}", uuid::Uuid::new_v4()));
            fs::create_dir_all(&p).unwrap();
            Tmp(p)
        }
        fn path(&self, rel: &str) -> PathBuf {
            self.0.join(rel)
        }
        fn write(&self, rel: &str, data: &[u8]) {
            let p = self.path(rel);
            fs::create_dir_all(p.parent().unwrap()).unwrap();
            fs::write(p, data).unwrap();
        }
        fn read(&self, rel: &str) -> Vec<u8> {
            fs::read(self.path(rel)).unwrap()
        }
    }
    impl Drop for Tmp {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }

    fn sdk_bin(project: &str, product: &str) -> Vec<u8> {
        format!("KR_GameName=x\r\nKR_GameVersion=4.8.0\r\nKR_ProductId={product}\r\nKR_ProjectId={project}\r\n").into_bytes()
    }

    /// The standard 4.8.0 test release.
    fn release() -> Vec<(&'static str, Vec<u8>, Option<usize>)> {
        vec![
            ("PGR.exe", blob(1, 3000), None),
            ("GameAssembly.dll", blob(2, 5000), None),
            (SDK_DLL, blob(3, 700), None),
            (KRSDK_BIN, sdk_bin("G143", "A1728"), None),
            ("version.json", b"{package_version:4.8.0}\n".to_vec(), None),
            ("PGR_Data/big.bin", blob(4, 5 * 4096 + 123), Some(4096)),
            (&"PGR_Data/StreamingAssets/resource/matrix/a.uab", blob(5, 900), None),
        ]
    }

    fn no_cancel() -> AtomicBool {
        AtomicBool::new(false)
    }

    fn install(world: &World, dir: &Path) -> Outcome {
        let plan = plan(&world.source(), Job::Install { dir: dir.into() }).unwrap();
        run(plan, &no_cancel(), &mut |_| {}).unwrap()
    }

    fn assert_release(tmp: &Tmp, files: &[(&str, Vec<u8>, Option<usize>)]) {
        for (d, b, _) in files {
            assert_eq!(&tmp.read(d), b, "{d}");
        }
    }

    // ── tests ──

    #[test]
    fn md5_matches_rfc1321_vectors() {
        assert_eq!(md5_hex(b""), "d41d8cd98f00b204e9800998ecf8427e");
        assert_eq!(md5_hex(b"abc"), "900150983cd24fb0d6963f7d28e17f72");
        assert_eq!(md5_hex(b"12345678901234567890123456789012345678901234567890123456789012345678901234567890"), "57edf4a22be3c955ac49da2e2107b67a");
        assert_eq!(md5_hex(&vec![b'a'; 1_000_000]), "7707d6ae4e027c70eea2a935c2296f21");
        // Streaming across block boundaries equals one shot.
        let data = blob(9, 1000);
        let mut m = Md5::new();
        for part in data.chunks(37) {
            m.update(part);
        }
        assert_eq!(m.finish(), md5_hex(&data));
    }

    #[test]
    fn pinned_sources_cover_all_regions_and_match_the_supported_version() {
        let found = sources(&Path::new(env!("CARGO_MANIFEST_DIR")).join("supported-client.json")).unwrap();
        assert_eq!(found.iter().map(|s| s.region()).collect::<Vec<_>>(), Region::ALL);
        for s in &found {
            assert_eq!(s.version(), "4.9.1");
            assert!(s.full_size() > 60_000_000_000, "{:?}", s.region());
            assert!(s.data.patches.len() >= 13 && s.data.cdns.len() >= 3);
            assert!(s.data.originals.contains_key("PGR.exe"));
        }
        let cn = found.iter().find(|s| s.region() == Region::Cn).unwrap();
        assert!(cn.data.discovery.len() == 2 && cn.data.originals.contains_key("PGR_Data/Plugins/KRSDKEx.dll"));
    }

    #[test]
    fn unsafe_paths_are_rejected() {
        for bad in ["../x", "/abs", "a/../b", "C:/x", "a\\b", "", "a//b", "a/./b", "x:stream", "trail./x", "a/b ", "nul\0x"] {
            assert!(check_rel(bad).is_err(), "{bad:?}");
        }
        assert!(check_rel("PGR_Data/Plugins/x86_64/a.dll").is_ok());
        assert!(check_url_path("a/../b").is_err() && check_url_path("/a").is_err() && check_url_path("a?x").is_err());
        let tmp = Tmp::new();
        #[cfg(unix)]
        {
            fs::create_dir(tmp.path("real")).unwrap();
            std::os::unix::fs::symlink(tmp.path("real"), tmp.path("link")).unwrap();
            assert!(safe_path(&tmp.0, "link/file").is_err());
        }
        assert!(safe_path(&tmp.0, "ok/file").is_ok());
    }

    #[test]
    fn free_space_check_reports_both_numbers() {
        assert!(ensure_space(10, 10, Path::new("D:/x")).is_ok());
        let e = ensure_space(1_000_000_000, 80_000_000_000, Path::new("D:/x")).unwrap_err().to_string();
        assert!(e.contains("80.0 GB") && e.contains("1.0 GB"), "{e}");
    }

    #[test]
    fn detect_reads_region_version_steam_and_patch_state() {
        let tmp = Tmp::new();
        assert_eq!(detect(&tmp.0).unwrap(), Detected { region: None, version: None, steam: false, launcher_patched: false });
        tmp.write(KRSDK_BIN, &sdk_bin("G286", "A1794"));
        tmp.write("version.json", b"{package_version:4.7.0}\n");
        let d = detect(&tmp.0).unwrap();
        assert_eq!((d.region, d.version.as_deref(), d.steam, d.launcher_patched), (Some(Region::Kr), Some("4.7.0"), false, false));
        tmp.write("steam_appid.txt", b"1");
        tmp.write(".ascnet-launcher/state.json", br#"{"schemaVersion":1,"releaseVersion":"0.4.0","originals":{},"files":{"PGRBase.dll":{"original":null,"installed":"ab","backup":null}}}"#);
        let d = detect(&tmp.0).unwrap();
        assert!(d.steam && d.launcher_patched);
        // No version.json: the KRSDK.bin version is used.
        fs::remove_file(tmp.path("version.json")).unwrap();
        assert_eq!(detect(&tmp.0).unwrap().version.as_deref(), Some("4.8.0"));
    }

    #[test]
    fn fresh_install_downloads_verifies_and_cleans_up() {
        let files = release();
        let world = World::new(&files);
        let tmp = Tmp::new();
        let dir = tmp.path("game");
        let plan = plan(&world.source(), Job::Install { dir: dir.clone() }).unwrap();
        let total: u64 = files.iter().map(|f| f.1.len() as u64).sum();
        assert_eq!((plan.download_bytes, plan.files, plan.delete), (total, files.len(), 0));
        assert_eq!(plan.required_free_bytes, total + SPACE_MARGIN);
        let mut phases = Vec::new();
        let outcome = run(plan, &no_cancel(), &mut |p| phases.push(p.phase)).unwrap();
        assert_eq!((outcome.downloaded_bytes, outcome.repaired, outcome.deleted), (total, 0, 0));
        assert_release(&Tmp(dir.clone()), &files);
        assert!(!dir.join(STATE_DIR).exists());
        assert!(phases.contains(&Phase::Verifying) && phases.contains(&Phase::Downloading) && phases.last() == Some(&Phase::Finalizing));
        std::mem::forget(Tmp(dir)); // owned by `tmp`
    }

    #[test]
    fn install_refuses_a_non_empty_folder() {
        let world = World::new(&release());
        let tmp = Tmp::new();
        tmp.write("somefile.txt", b"x");
        let e = plan(&world.source(), Job::Install { dir: tmp.0.clone() }).unwrap_err().to_string();
        assert!(e.contains("not empty"), "{e}");
    }

    #[test]
    fn cancel_then_resume_continues_at_chunk_granularity() {
        let files = release();
        let world = World::new(&files);
        let cancel = Arc::new(AtomicBool::new(false));
        let flag = cancel.clone();
        // Cancel the moment chunk 3 of big.bin is requested.
        let fired = AtomicBool::new(false);
        world.set_hook(move |req, resp| {
            if req.path.ends_with("big.bin") && req.range.is_some_and(|r| r.0 == 3 * 4096) && !fired.swap(true, SeqCst) {
                flag.store(true, SeqCst);
            }
            resp
        });
        let tmp = Tmp::new();
        let p = plan(&world.source(), Job::Install { dir: tmp.0.clone() }).unwrap();
        let err = run(p.clone(), &cancel, &mut |_| {}).unwrap_err();
        assert!(is_cancelled(&err), "{err:#}");
        assert!(tmp.path(STATE_DIR).join(MARKER).exists());
        assert!(!tmp.path("PGR_Data/big.bin").exists());
        let before = world.requests_for("PGR_Data/big.bin").len();
        cancel.store(false, SeqCst);
        // The folder is not empty any more, but it is this install's own: planning again resumes it.
        let p = plan(&world.source(), Job::Install { dir: tmp.0.clone() }).unwrap();
        let outcome = run(p, &cancel, &mut |_| {}).unwrap();
        assert_release(&tmp, &files);
        let second: Vec<u64> = world.requests_for("PGR_Data/big.bin")[before..].to_vec();
        assert_eq!(second.first(), Some(&(3 * 4096)), "resumed from chunk 3, got {second:?}");
        assert!(second.iter().all(|s| *s >= 3 * 4096));
        assert!(outcome.downloaded_bytes < files.iter().map(|f| f.1.len() as u64).sum::<u64>());
    }

    #[test]
    fn a_corrupt_chunk_is_refetched_alone() {
        let files = release();
        let world = World::new(&files);
        let hits = Arc::new(AtomicUsize::new(0));
        let counter = hits.clone();
        world.set_hook(move |req, mut resp| {
            if req.path.ends_with("big.bin") && req.range.is_some_and(|r| r.0 == 2 * 4096) && counter.fetch_add(1, SeqCst) == 0 {
                resp.body[10] ^= 0xff;
            }
            resp
        });
        let tmp = Tmp::new();
        install(&world, &tmp.0);
        assert_release(&tmp, &files);
        assert_eq!(hits.load(SeqCst), 2);
        assert_eq!(world.requests_for("PGR_Data/big.bin").iter().filter(|s| **s == 0).count(), 1);
    }

    #[test]
    fn a_corrupt_plain_file_is_refetched() {
        let files = release();
        let world = World::new(&files);
        let hits = Arc::new(AtomicUsize::new(0));
        let counter = hits.clone();
        world.set_hook(move |req, mut resp| {
            if req.path.ends_with("GameAssembly.dll") && counter.fetch_add(1, SeqCst) == 0 {
                resp.body[0] ^= 1;
            }
            resp
        });
        let tmp = Tmp::new();
        install(&world, &tmp.0);
        assert_release(&tmp, &files);
    }

    #[test]
    fn a_persistently_corrupt_file_fails_without_installing_it() {
        let files = release();
        let world = World::new(&files);
        world.set_hook(|req, mut resp| {
            if req.path.ends_with("PGR.exe") {
                resp.body[0] ^= 1;
            }
            resp
        });
        let tmp = Tmp::new();
        let p = plan(&world.source(), Job::Install { dir: tmp.0.clone() }).unwrap();
        let e = run(p, &no_cancel(), &mut |_| {}).unwrap_err();
        assert!(format!("{e:#}").contains("checksum"), "{e:#}");
        assert!(!tmp.path("PGR.exe").exists());
    }

    #[test]
    fn failover_skips_dead_and_broken_cdns_in_priority_order() {
        let files = release();
        let world = World::new(&files);
        let broken = serve(|_| ok(503, vec![]));
        let dead = {
            let l = TcpListener::bind("127.0.0.1:0").unwrap();
            format!("http://{}/", l.local_addr().unwrap())
        };
        let drop_all = serve(|_| ok(0, vec![]));
        let tmp = Tmp::new();
        let source = world.source_with(vec![dead, broken, drop_all, world.base.clone()], vec![], &[]);
        let p = plan(&source, Job::Install { dir: tmp.0.clone() }).unwrap();
        run(p, &no_cancel(), &mut |_| {}).unwrap();
        assert_release(&tmp, &files);
    }

    #[test]
    fn live_index_is_only_used_to_discover_hosts_when_pinned_ones_fail() {
        let files = release();
        let world = World::new(&files);
        let dead = {
            let l = TcpListener::bind("127.0.0.1:0").unwrap();
            format!("http://{}/", l.local_addr().unwrap())
        };
        let index = json!({"default": {"cdnList": [{"url": world.base, "P": 0}, {"url": "http://evil.example/", "P": 1}]}}).to_string();
        let mut gz = flate2::write::GzEncoder::new(Vec::new(), flate2::Compression::fast());
        gz.write_all(index.as_bytes()).unwrap();
        let gz = gz.finish().unwrap();
        let discovery = serve(move |_| ok(200, gz.clone()));
        let tmp = Tmp::new();
        let source = world.source_with(vec![dead], vec![discovery], &[]);
        let p = plan(&source, Job::Install { dir: tmp.0.clone() }).unwrap();
        run(p, &no_cancel(), &mut |_| {}).unwrap();
        assert_release(&tmp, &files);
    }

    #[test]
    fn a_list_that_does_not_match_its_pinned_md5_is_refused() {
        let mut files = release();
        files[0].0 = "PGR2.exe"; // unique content so no other test caches this MD5
        let world = World::new(&files);
        let mut source = world.source();
        let mut data = (*source.data).clone_data();
        data.index_md5 = md5_hex(b"the list the maintainers pinned");
        source.data = Arc::new(data);
        let tmp = Tmp::new();
        let e = plan(&source, Job::Install { dir: tmp.0.clone() }).unwrap_err();
        assert!(format!("{e:#}").contains("pinned MD5"), "{e:#}");
    }

    #[test]
    fn lists_with_escaping_paths_or_unknown_patch_formats_are_rejected() {
        let tmp = Tmp::new();
        let game = tmp.path("game");
        for (label, entry_path, extra) in [
            ("dotdot", "../evil.txt", None),
            ("abs", "/evil.txt", None),
            ("backslash", "..\\evil.txt", None),
            ("drive", "C:evil.txt", None),
            ("diff", "ok.bin", Some(("hdiffPatch", json!("x.diff")))),
        ] {
            let mut item = json!({"dest": entry_path, "md5": md5_hex(b"x"), "size": 1});
            if let Some((k, v)) = extra {
                item[k] = v;
            }
            let list = serde_json::to_vec(&json!({"resource": [item]})).unwrap();
            let world = World::new(&[("filler.bin", label.as_bytes().to_vec(), None)]);
            world.store.lock().unwrap().insert("r/indexFile.json".into(), list.clone());
            let mut source = world.source();
            let mut data = (*source.data).clone_data();
            data.index_md5 = md5_hex(&list);
            source.data = Arc::new(data);
            let e = plan(&source, Job::Install { dir: game.clone() }).unwrap_err();
            let text = format!("{e:#}");
            assert!(text.contains("unsafe path") || text.contains("unsupported file list format"), "{label}: {text}");
        }
        assert!(!tmp.path("evil.txt").exists() && !tmp.0.join("../evil.txt").exists());
    }

    #[test]
    fn traversal_in_patch_delete_list_and_archive_members_is_rejected() {
        for bad in ["../outside.txt", "a/../../outside.txt"] {
            let files = release();
            let mut world = World::new(&files);
            world.add_patch("4.7.0", &[], &[], &[bad]);
            let tmp = Tmp::new();
            tmp.write(KRSDK_BIN, &sdk_bin("G143", "A1728"));
            tmp.write("version.json", b"{package_version:4.7.0}\n");
            let outside = tmp.0.parent().unwrap().join("outside.txt");
            fs::write(&outside, b"keep").unwrap();
            let e = plan(&world.source(), Job::Update { dir: tmp.0.clone() }).unwrap_err();
            assert!(format!("{e:#}").contains("unsafe path"), "{e:#}");
            assert_eq!(fs::read(&outside).unwrap(), b"keep");
            fs::remove_file(outside).unwrap();
        }
        let files = release();
        let mut world = World::new(&files);
        world.add_patch("4.7.0", &[], &[("0.krzip", vec![1, 2, 3], vec![("../escape.bin", vec![1])])], &[]);
        let tmp = Tmp::new();
        tmp.write("version.json", b"{package_version:4.7.0}\n");
        tmp.write(KRSDK_BIN, &sdk_bin("G143", "A1728"));
        assert!(format!("{:#}", plan(&world.source(), Job::Update { dir: tmp.0.clone() }).unwrap_err()).contains("unsafe path"));
    }

    fn installed(world: &World) -> Tmp {
        let tmp = Tmp::new();
        let dir = tmp.path("g");
        install(world, &dir);
        // Move the finished install into the temp root so Tmp::drop cleans it.
        for entry in fs::read_dir(&dir).unwrap().flatten() {
            fs::rename(entry.path(), tmp.0.join(entry.file_name())).unwrap();
        }
        fs::remove_dir(&dir).unwrap();
        tmp
    }

    #[test]
    fn repair_fixes_broken_files_and_keeps_variants_extras_and_game_managed_files() {
        let files = release();
        let world = World::new(&files);
        let tmp = installed(&world);
        // Broken: one chunked file, one missing.
        let mut big = tmp.read("PGR_Data/big.bin");
        big[5000] ^= 0xff;
        tmp.write("PGR_Data/big.bin", &big);
        fs::remove_file(tmp.path("version.json")).unwrap();
        // Variants that must survive: accepted retail PGR.exe, AscNet-patched KRSDK.dll, Steam KRSDK.bin.
        let retail_variant = blob(77, 3100);
        tmp.write("PGR.exe", &retail_variant);
        let patched = blob(78, 800);
        tmp.write(SDK_DLL, &patched);
        tmp.write(".ascnet-launcher/state.json", format!(r#"{{"schemaVersion":1,"releaseVersion":"0.4.0","originals":{{}},"files":{{"{SDK_DLL}":{{"original":null,"installed":"{}","backup":null}}}}}}"#, sha(&patched)).as_bytes());
        let steam_bin = sdk_bin("G143", "A1855");
        tmp.write(KRSDK_BIN, &steam_bin);
        tmp.write("steam_appid.txt", b"1");
        // Extras and game-managed resources are left alone, even when they differ from the index.
        tmp.write("steam_api64.dll", b"steam");
        tmp.write(&format!("{PREFIX}/matrix/a.uab"), b"newer resource from the game");
        tmp.write(&format!("{PREFIX}/matrix/extra.uab"), b"x");

        let source = world.source_with(vec![world.base.clone()], vec![], &[("PGR.exe", &[retail_variant.clone()])]);
        let p = plan(&source, Job::Repair { dir: tmp.0.clone() }).unwrap();
        assert!(matches!(p.job, Job::Repair { .. }) && p.note.is_none());
        let outcome = run(p, &no_cancel(), &mut |_| {}).unwrap();
        assert_eq!(outcome.repaired, 2);
        assert_eq!(tmp.read("PGR_Data/big.bin"), files[5].1);
        assert_eq!(tmp.read("version.json"), files[4].1);
        assert_eq!(tmp.read("PGR.exe"), retail_variant);
        assert_eq!(tmp.read(SDK_DLL), patched);
        assert_eq!(tmp.read(KRSDK_BIN), steam_bin);
        assert_eq!(tmp.read("steam_api64.dll"), b"steam");
        assert_eq!(tmp.read(&format!("{PREFIX}/matrix/a.uab")), b"newer resource from the game");
        assert_eq!(
            outcome.skipped_variants,
            ["PGR.exe (accepted retail variant)".to_owned(), format!("{SDK_DLL} (AscNet patch)"), format!("{KRSDK_BIN} (Steam build)")]
        );
        assert!(!tmp.path(STATE_DIR).exists());
        // Everything downloaded was only the two broken files (plus the chunked file's bytes).
        assert!(outcome.downloaded_bytes <= (files[5].1.len() + files[4].1.len()) as u64);
    }

    /// The CN client has no KRSDK.bin/KRSDK.dll: identity comes from KRSDKConfig.json, and the official SDK files are
    /// ordinary index files, while the launcher-patched PGRBase.dll and its extra files survive a repair.
    #[test]
    fn cn_client_is_detected_from_krsdk_config_and_repaired_beside_the_launcher_patch() {
        const CONFIG: &str = "PGR_Data/Plugins/KRSDKRes/KRSDKConfig.json";
        const EX: &str = "PGR_Data/Plugins/KRSDKEx.dll";
        let config = "\u{feff}{\r\n   \"KR_GameVersion\" : \"4.8.0\",\r\n   \"KR_PackageName\" : \"com.kurogame.haru.hero\",\r\n   \"KR_ProjectId\" : \"G148\",\r\n   \"KR_ChannelId\" : \"19\"\r\n}\r\n";
        let files: Vec<(&str, Vec<u8>, Option<usize>)> = vec![
            ("PGR.exe", blob(1, 3000), None),
            ("PGRBase.dll", blob(2, 900), None),
            (EX, blob(3, 700), None),
            (CONFIG, config.as_bytes().to_vec(), None),
        ];
        let world = World::new(&files);
        let mut source = world.source();
        source.region = Region::Cn;
        let tmp = Tmp::new();
        let outcome = run(plan(&source, Job::Install { dir: tmp.path("game") }).unwrap(), &no_cancel(), &mut |_| {}).unwrap();
        assert_eq!(outcome.deleted, 0);
        let game = Tmp(tmp.path("game"));
        assert_release(&game, &files);
        let d = detect(&game.0).unwrap();
        assert_eq!((d.region, d.version.as_deref()), (Some(Region::Cn), Some("4.8.0")));

        let patched = blob(9, 910);
        game.write("PGRBase.dll", &patched);
        game.write("version.dll", b"proxy");
        game.write("lucia.dll", b"lucia");
        game.write(EX, b"broken");
        game.write(".ascnet-launcher/state.json", format!(r#"{{"schemaVersion":1,"releaseVersion":"0.4.0","originals":{{}},"files":{{"PGRBase.dll":{{"original":null,"installed":"{}","backup":null}}}}}}"#, sha(&patched)).as_bytes());
        let outcome = run(plan(&source, Job::Repair { dir: game.0.clone() }).unwrap(), &no_cancel(), &mut |_| {}).unwrap();
        assert_eq!(outcome.repaired, 1);
        assert_eq!(game.read(EX), files[2].1);
        assert_eq!(game.read("PGRBase.dll"), patched);
        assert_eq!((game.read("version.dll"), game.read("lucia.dll")), (b"proxy".to_vec(), b"lucia".to_vec()));
        let e = plan(&world.source(), Job::Repair { dir: game.0.clone() }).unwrap_err().to_string();
        assert!(e.contains("CN client, not EN"), "{e}");
        std::mem::forget(game); // owned by `tmp`
    }

    #[test]
    fn cn_detection_ignores_unparseable_config_and_reads_a_minimal_one() {
        let tmp = Tmp::new();
        tmp.write("PGR_Data/Plugins/KRSDKRes/KRSDKConfig.json", b"{not json");
        assert_eq!(detect(&tmp.0).unwrap().region, None);
        tmp.write("PGR_Data/Plugins/KRSDKRes/KRSDKConfig.json", br#"{"KR_ProjectId":"G148"}"#);
        let d = detect(&tmp.0).unwrap();
        assert_eq!((d.region, d.version), (Some(Region::Cn), None));
    }

    #[test]
    fn repair_refuses_a_folder_of_another_region() {
        let world = World::new(&release());
        let tmp = Tmp::new();
        tmp.write(KRSDK_BIN, &sdk_bin("G279", "A1760"));
        tmp.write("PGR.exe", b"x");
        let e = plan(&world.source(), Job::Adopt { dir: tmp.0.clone() }).unwrap_err().to_string();
        assert!(e.contains("TW client, not EN"), "{e}");
    }

    #[test]
    fn adopting_a_steam_like_install_changes_nothing() {
        let files = release();
        let world = World::new(&files);
        let tmp = Tmp::new();
        for (d, b, _) in &files {
            tmp.write(d, b);
        }
        // Steam differences: its own KRSDK.dll / KRSDK.bin, plus steam files absent from the index.
        let steam_dll = blob(90, 710);
        tmp.write(SDK_DLL, &steam_dll);
        tmp.write(KRSDK_BIN, &sdk_bin("G143", "A1855"));
        tmp.write("steam_appid.txt", b"1");
        tmp.write("PGR_Data/Plugins/x86_64/steam_api64.dll", b"steam");
        let source = world.source_with(vec![world.base.clone()], vec![], &[(SDK_DLL, &[steam_dll.clone()])]);
        let d = detect(&tmp.0).unwrap();
        assert!(d.steam && d.region == Some(Region::En) && d.version.as_deref() == Some("4.8.0"));
        let p = plan(&source, Job::Adopt { dir: tmp.0.clone() }).unwrap();
        assert_eq!((p.download_bytes, p.files, p.delete), (0, 0, 0));
        assert_eq!(p.required_free_bytes, 0);
        let before = world.log.lock().unwrap().len();
        let outcome = run(p, &no_cancel(), &mut |_| {}).unwrap();
        assert_eq!((outcome.downloaded_bytes, outcome.repaired), (0, 0));
        assert_eq!(outcome.skipped_variants, [format!("{SDK_DLL} (accepted retail variant)"), format!("{KRSDK_BIN} (Steam build)")]);
        assert_eq!(world.log.lock().unwrap().len(), before, "adopt of an intact install must not hit the CDN");
        assert_eq!(tmp.read(SDK_DLL), steam_dll);
        assert_eq!(tmp.read("PGR_Data/Plugins/x86_64/steam_api64.dll"), b"steam");
    }

    fn krzip(members: &[(&str, &[u8])]) -> Vec<u8> {
        let mut zip = zip::ZipWriter::new(std::io::Cursor::new(Vec::new()));
        let options = zip::write::SimpleFileOptions::default().compression_method(zip::CompressionMethod::Deflated);
        for (name, data) in members {
            zip.start_file(*name, options).unwrap();
            zip.write_all(data).unwrap();
        }
        zip.finish().unwrap().into_inner()
    }

    /// An install of 4.7.0 and a 4.7.0 -> 4.8.0 patch (whole file, krzip, deletes) towards the standard release.
    fn old_install_and_world() -> (World, Tmp, Vec<(&'static str, Vec<u8>, Option<usize>)>) {
        let files = release();
        let mut world = World::new(&files);
        let members: Vec<(&str, Vec<u8>)> = vec![
            ("PGR.exe", files[0].1.clone()),
            ("version.json", files[4].1.clone()),
            (KRSDK_BIN, files[3].1.clone()),
            (SDK_DLL, files[2].1.clone()),
        ];
        let archive = krzip(&members.iter().map(|(n, b)| (*n, b.as_slice())).collect::<Vec<_>>());
        let uab = format!("{PREFIX}/matrix/a.uab");
        world.add_patch(
            "4.7.0",
            &[("GameAssembly.dll", files[1].1.clone()), (uab.as_str(), files[6].1.clone())],
            &[("0.krzip", archive, members)],
            &[&format!("{PREFIX}/matrix/old.uab"), "gone.txt", "GameAssembly.dll", "never-existed.txt"],
        );
        let tmp = Tmp::new();
        tmp.write("PGR.exe", b"old exe");
        tmp.write("GameAssembly.dll", b"old assembly");
        tmp.write("version.json", b"{package_version:4.7.0}\n");
        tmp.write(KRSDK_BIN, &sdk_bin("G143", "A1728").iter().map(|b| *b).chain(*b"old").collect::<Vec<_>>());
        tmp.write(SDK_DLL, &files[2].1); // already current
        tmp.write("gone.txt", b"obsolete");
        tmp.write("keep.txt", b"user file");
        tmp.write(&format!("{PREFIX}/matrix/old.uab"), b"obsolete resource");
        tmp.write("PGR_Data/big.bin", &files[5].1); // unchanged by the patch
        (world, tmp, files)
    }

    #[test]
    fn update_applies_whole_files_krzip_entries_and_deletes() {
        let (world, tmp, files) = old_install_and_world();
        let p = plan(&world.source(), Job::Repair { dir: tmp.0.clone() }).unwrap();
        assert!(matches!(p.job, Job::Update { .. }), "older version with a patch is rerouted: {:?}", p.job);
        assert!(p.note.as_deref().unwrap().contains("4.7.0"));
        assert_eq!(p.delete, 2);
        assert!(p.download_bytes > 0 && p.files >= 4);
        let mut phases = Vec::new();
        let outcome = run(p, &no_cancel(), &mut |pr| phases.push(pr.phase)).unwrap();
        assert!(phases.contains(&Phase::Extracting) || phases.contains(&Phase::Downloading));
        assert_release(&tmp, &files);
        assert!(!tmp.path("gone.txt").exists() && !tmp.path(&format!("{PREFIX}/matrix/old.uab")).exists());
        assert_eq!(tmp.read("keep.txt"), b"user file");
        assert_eq!(outcome.deleted, 2);
        assert_eq!(outcome.repaired, 0);
        assert!(!tmp.path(STATE_DIR).exists());
    }

    #[test]
    fn update_keeps_launcher_patched_files_even_when_the_archive_contains_them() {
        let (world, tmp, files) = old_install_and_world();
        let patched = blob(55, 801);
        tmp.write(SDK_DLL, &patched);
        tmp.write(".ascnet-launcher/state.json", format!(r#"{{"schemaVersion":1,"releaseVersion":"0.4.0","originals":{{}},"files":{{"{SDK_DLL}":{{"original":"{}","installed":"{}","backup":null}}}}}}"#, sha(&files[2].1), sha(&patched)).as_bytes());
        let p = plan(&world.source(), Job::Update { dir: tmp.0.clone() }).unwrap();
        let outcome = run(p, &no_cancel(), &mut |_| {}).unwrap();
        assert_eq!(tmp.read(SDK_DLL), patched);
        assert_eq!(outcome.skipped_variants, [format!("{SDK_DLL} (AscNet patch)")]);
        assert_eq!(tmp.read("PGR.exe"), files[0].1);
    }

    #[test]
    fn update_resumes_after_a_cancel_inside_the_archive() {
        let (world, tmp, files) = old_install_and_world();
        let cancel = Arc::new(AtomicBool::new(false));
        let flag = cancel.clone();
        let fired = AtomicBool::new(false);
        world.set_hook(move |req, resp| {
            if req.path.ends_with("0.krzip") && req.range.is_some_and(|r| r.0 == 200) && !fired.swap(true, SeqCst) {
                flag.store(true, SeqCst);
            }
            resp
        });
        let p = plan(&world.source(), Job::Update { dir: tmp.0.clone() }).unwrap();
        assert!(is_cancelled(&run(p, &cancel, &mut |_| {}).unwrap_err()));
        assert_eq!(tmp.read("PGR.exe"), b"old exe", "nothing is installed from an unverified archive");
        cancel.store(false, SeqCst);
        let p = plan(&world.source(), Job::Update { dir: tmp.0.clone() }).unwrap();
        run(p, &cancel, &mut |_| {}).unwrap();
        assert_release(&tmp, &files);
    }

    #[test]
    fn update_without_a_patch_for_the_installed_version_repairs_instead() {
        let (world, tmp, _) = old_install_and_world();
        tmp.write("version.json", b"{package_version:4.5.0}\n");
        let p = plan(&world.source(), Job::Update { dir: tmp.0.clone() }).unwrap();
        assert!(matches!(p.job, Job::Repair { .. }));
        assert!(p.note.as_deref().unwrap().contains("no patch"), "{:?}", p.note);
        run(p, &no_cancel(), &mut |_| {}).unwrap();
        assert_eq!(tmp.read("PGR.exe"), release()[0].1);
    }

    #[test]
    fn a_newer_installed_client_is_refused() {
        let (world, tmp, _) = old_install_and_world();
        tmp.write("version.json", b"{package_version:4.9.0}\n");
        let e = plan(&world.source(), Job::Repair { dir: tmp.0.clone() }).unwrap_err().to_string();
        assert!(e.contains("newer than the supported 4.8.0"), "{e}");
    }

    impl SourceData {
        fn clone_data(&self) -> SourceData {
            SourceData {
                discovery: self.discovery.clone(),
                cdns: self.cdns.clone(),
                exclude: self.exclude.clone(),
                base_url: self.base_url.clone(),
                index_file: self.index_file.clone(),
                index_md5: self.index_md5.clone(),
                patches: self.patches.clone(),
                originals: self.originals.clone(),
                document: self.document.clone(),
            }
        }
    }

    // ── in-game document patch ──

    const DOC: &str = "PGR_Data/StreamingAssets/document";
    const DOC_BASE: &str = "client/patch/k3y/com.test.en/4.9.0/standalone/Release";

    fn sha1h(data: &[u8]) -> String {
        format!("{:x}", sha1::Sha1::digest(data))
    }

    /// A launcher release plus a patch CDN. matrix: `a` is held by the package (same sha1), `b` is flagged
    /// package-never-holds, `c` differs from the package copy, `d` is absent from the package and belongs to build
    /// 1709830 (version path 4.9.1). launch: `l1` held, `l2` needed.
    struct Docs {
        world: World,
        source: ClientSource,
        files: Vec<(&'static str, Vec<u8>, Option<usize>)>,
        /// document files that must end up on disk: (path below the install, bytes)
        needed: Vec<(String, Vec<u8>)>,
        matrix_index: Vec<u8>,
        launch_index: Vec<u8>,
        store: Arc<Mutex<HashMap<String, Vec<u8>>>>,
        log: Arc<Mutex<Vec<(String, u64)>>>,
    }

    fn docs(extra_matrix_row: Option<&str>) -> Docs {
        let (a, b, c, c_pkg, d) = (blob(11, 3000), blob(12, 5000), blob(13, 700), blob(14, 700), blob(15, 9000));
        let (l1, l2) = (blob(16, 400), blob(17, 600));
        let (fa, fb, fc, fd) = ("aaaaaaaaaaaaaaaa.uab", "bbbbbbbbbbbbbbbb.uab", "cccccccccccccccc.uab", "dddddddddddddddd.uab");
        let (f1, f2) = ("1111111111111111.uab", "2222222222222222.uab");
        let row = |f: &'static str, data: &[u8], svn: u64, flag: u64| (f, sha1h(data), data.len() as u64, svn, flag);
        let mut cdn_matrix = vec![row(fa, &a, 1739029, 0), row(fb, &b, 1739029, 0), row(fc, &c, 1739029, 0), row(fd, &d, 1709830, 0)];
        if let Some(name) = extra_matrix_row {
            cdn_matrix.push((name, sha1h(b"evil"), 4, 1739029, 0));
        }
        let pkg_matrix = [row(fa, &a, 1739029, 0), row(fb, &b, 1739029, 1), row(fc, &c_pkg, 1739029, 0)];
        let cdn_launch = [row(f1, &l1, 1739029, 0), row(f2, &l2, 1739029, 0)];
        let pkg_launch = [row(f1, &l1, 1739029, 0)];
        let matrix_index = crate::bundle::tests::synthetic_index(&cdn_matrix);
        let launch_index = crate::bundle::tests::synthetic_index(&cdn_launch);

        let mut files = release();
        files.push(("PGR_Data/StreamingAssets/resource/matrix/InPkgIndex", crate::bundle::tests::synthetic_index(&pkg_matrix), None));
        files.push(("PGR_Data/StreamingAssets/resource/launch/index", crate::bundle::tests::synthetic_index(&pkg_launch), None));
        let world = World::new(&files);

        let store: Arc<Mutex<HashMap<String, Vec<u8>>>> = Default::default();
        let log: Arc<Mutex<Vec<(String, u64)>>> = Default::default();
        {
            let mut s = store.lock().unwrap();
            s.insert(format!("{DOC_BASE}/1739029/matrix/index"), matrix_index.clone());
            s.insert(format!("{DOC_BASE}/1739029/launch/index"), launch_index.clone());
            for (f, data) in [(fb, &b), (fc, &c), (fa, &a)] {
                s.insert(format!("{DOC_BASE}/1739029/matrix/{f}"), data.clone());
            }
            s.insert("client/patch/k3y/com.test.en/4.9.1/standalone/Release/1709830/matrix/dddddddddddddddd.uab".into(), d.clone());
            s.insert(format!("{DOC_BASE}/1739029/launch/{f2}"), l2.clone());
        }
        let cdn = World::spawn(&store, &log, &Default::default());
        let raw: document::RawDocument = serde_json::from_value(json!({
            "cdns": [cdn],
            "key": "k3y",
            "identifier": "com.test.en",
            "defaultVersion": "4.9.0",
            "versions": {"1709830": "4.9.1"},
            "defaultBuild": "1739029",
            "builds": {"1739029": {"matrixSha1": sha1h(&matrix_index), "launchSha1": sha1h(&launch_index)}}
        }))
        .unwrap();
        let base = world.source();
        let mut data = base.data.clone_data();
        data.document = Some(raw.validate("EN").unwrap());
        let source = ClientSource { data: Arc::new(data), ..base };
        let needed = vec![
            (format!("{DOC}/matrix/{fb}"), b),
            (format!("{DOC}/matrix/{fc}"), c),
            (format!("{DOC}/matrix/{fd}"), d),
            (format!("{DOC}/launch/{f2}"), l2),
        ];
        Docs { world, source, files, needed, matrix_index, launch_index, store, log }
    }

    fn install_docs(d: &Docs) -> Tmp {
        let tmp = Tmp::new();
        let p = plan(&d.source, Job::Install { dir: tmp.path("g") }).unwrap();
        run(p, &no_cancel(), &mut |_| {}).unwrap();
        tmp
    }

    #[test]
    fn install_restores_exactly_the_files_the_package_does_not_hold_and_writes_the_indexes() {
        let d = docs(None);
        let tmp = Tmp::new();
        let dir = tmp.path("g");
        let p = plan(&d.source, Job::Install { dir: dir.clone() }).unwrap();
        let listed: u64 = d.files.iter().map(|f| f.1.len() as u64).sum();
        let patch: u64 = d.needed.iter().map(|(_, b)| b.len() as u64).sum();
        assert_eq!((p.download_bytes, p.files), (listed + patch, d.files.len() + 4));
        let outcome = run(p, &no_cancel(), &mut |_| {}).unwrap();
        assert_eq!((outcome.document_files, outcome.downloaded_bytes), (4, listed + patch));
        assert!(outcome.document_notes.is_empty(), "{:?}", outcome.document_notes);
        for (rel, bytes) in &d.needed {
            assert_eq!(&tmp.read(&format!("g/{rel}")), bytes, "{rel}");
        }
        assert_eq!(tmp.read(&format!("g/{DOC}/matrix/index")), d.matrix_index);
        assert_eq!(tmp.read(&format!("g/{DOC}/launch/index")), d.launch_index);
        // Held by the package: not copied into document/.
        assert!(!tmp.path(&format!("g/{DOC}/matrix/aaaaaaaaaaaaaaaa.uab")).exists());
        assert!(!tmp.path(&format!("g/{DOC}/launch/1111111111111111.uab")).exists());
        assert!(!dir.join(STATE_DIR).exists());
    }

    #[test]
    fn repair_refetches_only_missing_or_wrong_document_files_and_keeps_extras() {
        let d = docs(None);
        let tmp = install_docs(&d);
        let dir = tmp.path("g");
        let b = format!("g/{DOC}/matrix/bbbbbbbbbbbbbbbb.uab");
        let mut bad = tmp.read(&b);
        bad[100] ^= 0xff; // same size, wrong sha1
        tmp.write(&b, &bad);
        fs::remove_file(tmp.path(&format!("g/{DOC}/matrix/dddddddddddddddd.uab"))).unwrap();
        fs::remove_file(tmp.path(&format!("g/{DOC}/launch/index"))).unwrap();
        tmp.write(&format!("g/{DOC}/matrix/extra.uab"), b"not in the index");
        d.log.lock().unwrap().clear();

        let p = plan(&d.source, Job::Repair { dir: dir.clone() }).unwrap();
        assert!(p.note.is_none());
        assert_eq!((p.files, p.download_bytes), (1, 9000));
        let outcome = run(p, &no_cancel(), &mut |_| {}).unwrap();
        assert_eq!(outcome.document_files, 2);
        for (rel, bytes) in &d.needed {
            assert_eq!(&tmp.read(&format!("g/{rel}")), bytes, "{rel}");
        }
        assert_eq!(tmp.read(&format!("g/{DOC}/launch/index")), d.launch_index);
        assert_eq!(tmp.read(&format!("g/{DOC}/matrix/extra.uab")), b"not in the index");
        let asked: Vec<String> = d.log.lock().unwrap().iter().map(|(p, _)| p.clone()).collect();
        for good in ["cccccccccccccccc.uab", "2222222222222222.uab"] {
            assert!(!asked.iter().any(|p| p.ends_with(good)), "{good} was valid and must not be fetched again: {asked:?}");
        }
        assert!(asked.iter().any(|p| p.ends_with("bbbbbbbbbbbbbbbb.uab")) && asked.iter().any(|p| p.ends_with("/1709830/matrix/dddddddddddddddd.uab")));
    }

    #[test]
    fn a_broken_package_index_is_repaired_before_the_document_diff() {
        let d = docs(None);
        let tmp = install_docs(&d);
        let pkg = format!("g/{PREFIX}/matrix/InPkgIndex");
        tmp.write(&pkg, b"damaged");
        fs::remove_file(tmp.path(&format!("g/{DOC}/matrix/bbbbbbbbbbbbbbbb.uab"))).unwrap();
        let p = plan(&d.source, Job::Repair { dir: tmp.path("g") }).unwrap();
        let outcome = run(p, &no_cancel(), &mut |_| {}).unwrap();
        assert_eq!(outcome.document_files, 1);
        assert_eq!(tmp.read(&pkg), d.files.iter().find(|f| f.0.ends_with("InPkgIndex")).unwrap().1);
    }

    #[test]
    fn ver_and_client2cdn_decide_where_the_files_come_from() {
        let d = docs(None);
        let tmp = Tmp::new();
        let dir = tmp.path("g");
        run(plan(&d.world.source(), Job::Install { dir: dir.clone() }).unwrap(), &no_cancel(), &mut |_| {}).unwrap();
        // Move every patch file to another key/identifier/version path, as a differently built client would use.
        {
            let mut s = d.store.lock().unwrap();
            let moved: Vec<(String, Vec<u8>)> = s.drain().collect();
            for (path, bytes) in moved {
                let parts: Vec<&str> = path.split('/').collect();
                let version = if parts[7] == "1739029" { "9.9.9" } else { "8.8.8" };
                s.insert(format!("client/patch/ALT/com.alt/{version}/standalone/Release/{}", parts[7..].join("/")), bytes);
            }
        }
        tmp.write(&format!("g/{DOC}/ver"), br#"{"LeastVersionFile":"1739029","VersionPath":{"1739029":{"version":"9.9.9","key":"ALT"},"1709830":{"version":"8.8.8","key":"ALT"}}}"#);
        tmp.write(&format!("g/{DOC}/client2cdn"), br#"{"1739029":{"KuroGameIdentifier":"com.alt"},"1709830":{"KuroGameIdentifier":"com.alt"}}"#);
        let outcome = run(plan(&d.source, Job::Repair { dir }).unwrap(), &no_cancel(), &mut |_| {}).unwrap();
        assert_eq!(outcome.document_files, 4);
        for (rel, bytes) in &d.needed {
            assert_eq!(&tmp.read(&format!("g/{rel}")), bytes, "{rel}");
        }
    }

    #[test]
    fn a_build_without_a_pin_and_a_drifted_index_are_reported_not_restored() {
        let d = docs(None);
        let tmp = Tmp::new();
        let dir = tmp.path("g");
        run(plan(&d.world.source(), Job::Install { dir: dir.clone() }).unwrap(), &no_cancel(), &mut |_| {}).unwrap();
        tmp.write(&format!("g/{DOC}/ver"), br#"{"LeastVersionFile":"5"}"#);
        let p = plan(&d.source, Job::Repair { dir: dir.clone() }).unwrap();
        assert!(p.note.as_deref().unwrap().contains("build 5"), "{:?}", p.note);
        let outcome = run(p, &no_cancel(), &mut |_| {}).unwrap();
        assert_eq!(outcome.document_files, 0);
        assert!(outcome.document_notes[0].contains("build 5"));
        assert!(!tmp.path(&format!("g/{DOC}/matrix")).exists());

        fs::remove_file(tmp.path(&format!("g/{DOC}/ver"))).unwrap();
        d.store.lock().unwrap().insert(format!("{DOC_BASE}/1739029/matrix/index"), b"a different index".to_vec());
        let outcome = run(plan(&d.source, Job::Repair { dir }).unwrap(), &no_cancel(), &mut |_| {}).unwrap();
        assert_eq!(outcome.document_files, 1); // launch still restored
        assert!(outcome.document_notes[0].contains("matrix document index") && outcome.document_notes[0].contains("changed"), "{:?}", outcome.document_notes);
        assert!(!tmp.path(&format!("g/{DOC}/matrix")).exists());
    }

    #[test]
    fn a_region_without_a_pin_says_so() {
        let world = World::new(&release());
        let tmp = Tmp::new();
        let outcome = install(&world, &tmp.path("g"));
        assert_eq!(outcome.document_files, 0);
        assert!(outcome.document_notes[0].contains("no in-game document patch is pinned"), "{:?}", outcome.document_notes);
        assert!(!tmp.path(&format!("g/{DOC}")).exists());
    }

    #[test]
    fn a_file_that_fails_its_sha1_is_never_installed_and_the_index_stays_unwritten() {
        let d = docs(None);
        let mut wrong = d.needed[2].1.clone(); // dddd…
        wrong[0] ^= 0xff;
        d.store.lock().unwrap().insert("client/patch/k3y/com.test.en/4.9.1/standalone/Release/1709830/matrix/dddddddddddddddd.uab".into(), wrong);
        let tmp = Tmp::new();
        let dir = tmp.path("g");
        let error = run(plan(&d.source, Job::Install { dir }).unwrap(), &no_cancel(), &mut |_| {}).unwrap_err();
        assert!(format!("{error:#}").contains("giving up"), "{error:#}");
        assert!(!tmp.path(&format!("g/{DOC}/matrix/index")).exists());
        assert!(!tmp.path(&format!("g/{DOC}/matrix/dddddddddddddddd.uab")).exists());
        // launch is a separate tree and already complete.
        assert_eq!(tmp.read(&format!("g/{DOC}/launch/index")), d.launch_index);
    }

    #[test]
    fn index_names_that_escape_document_are_rejected() {
        for name in ["../evil", "sub/evil", "..", "a\\b"] {
            let d = docs(Some(name));
            let tmp = Tmp::new();
            let error = format!("{:#}", plan(&d.source, Job::Install { dir: tmp.path("g") }).err().expect(name));
            assert!(error.contains("unsafe file name"), "{name}: {error}");
            assert!(!tmp.path("evil").exists());
        }
    }

    #[test]
    fn pins_are_validated() {
        let ok = json!({"cdns": ["https://cdn.example/prod/"], "key": "k", "identifier": "id", "defaultVersion": "4.9.0", "defaultBuild": "1",
            "builds": {"1": {"matrixSha1": "a".repeat(40), "launchSha1": "b".repeat(40)}}});
        let check = |patch: &dyn Fn(&mut Value)| {
            let mut v = ok.clone();
            patch(&mut v);
            serde_json::from_value::<document::RawDocument>(v).map_err(anyhow::Error::from).and_then(|r| r.validate("EN").map(|_| ()))
        };
        assert!(check(&|_| {}).is_ok());
        assert!(check(&|v| v["cdns"] = json!(["http://cdn.example/prod/"])).is_err());
        assert!(check(&|v| v["cdns"] = json!(["https://cdn.example/prod"])).is_err());
        assert!(check(&|v| v["key"] = json!("a/b")).is_err());
        assert!(check(&|v| v["defaultBuild"] = json!("2")).is_err());
        assert!(check(&|v| v["builds"]["1"]["matrixSha1"] = json!("abc")).is_err());
        assert!(check(&|v| v["extra"] = json!(1)).is_err());
    }

    /// Manual: Kuro's real patch CDN for every pinned region's build. `cargo test -- --ignored live_document`.
    /// Checks both indexes against the pins on every CDN host, then downloads and sha1-verifies the smallest file
    /// each tree still needs on an empty install.
    #[test]
    #[ignore]
    fn live_document_patch_matches_the_pins() {
        for region in Region::ALL {
            live_document_patch_matches_the_pins_for(region);
        }
    }

    fn live_document_patch_matches_the_pins_for(region: Region) {
        eprintln!("{}: checking the pinned document patch", region.label());
        let found = sources(&Path::new(env!("CARGO_MANIFEST_DIR")).join("supported-client.json")).unwrap();
        let source = found.iter().find(|s| s.region == region).unwrap();
        let doc = source.data.document.as_ref().expect("every region pins the document patch");
        let net = Net::new(&source.data).unwrap();
        let cancel = no_cancel();
        let full = get_list(&net, &source.data.index_file, &source.data.index_md5, &cancel).unwrap();
        let empty = Tmp::new();
        let plan = document::inspect(source, &net, &empty.path("g"), &full, &cancel).unwrap();
        assert!(plan.notes.is_empty(), "{:?}", plan.notes);
        assert_eq!(plan.trees.len(), 2);
        let locator = plan.locator.as_ref().unwrap();
        // inspect() already matched the first answering host's indexes to the pins; every host must serve the same bytes.
        let mut seen: HashMap<&str, String> = HashMap::new();
        for host in &doc.cdns {
            let one = Net::with_hosts(&[], std::slice::from_ref(host)).unwrap();
            for t in &plan.trees {
                let bytes = one.get(&locator.url(&doc.default_build, t.tree, "index"), &cancel).unwrap();
                assert_eq!(seen.entry(t.tree).or_insert_with(|| sha1h(&bytes)), &sha1h(&bytes), "{host} {}", t.tree);
                assert!(!t.need.is_empty(), "{}: the package holds everything?", t.tree);
            }
        }
        let patch_net = plan.net.as_ref().unwrap();
        for t in &plan.trees {
            let row = t.need.iter().min_by_key(|r| r.size).unwrap();
            eprintln!("{}: {} files need restoring, smallest {} ({} bytes)", t.tree, t.need.len(), row.file, row.size);
            let ctx = Ctx::new(&cancel);
            let part = empty.path("part");
            let url = locator.url(&row.svn.to_string(), t.tree, &row.file);
            download_file(&ctx, patch_net, &url, row.size, Hash::Sha1, &row.sha1, None, &part).unwrap();
            assert_eq!(Hash::Sha1.range(&mut File::open(&part).unwrap(), 0, row.size).unwrap(), row.sha1);
            fs::remove_file(&part).unwrap();
        }
        // The plan on an empty folder asks for the same files on top of the launcher list.
        let (bytes, files) = document::estimate(&plan, &empty.path("g"));
        eprintln!("document patch for an empty install: {files} files, {bytes} bytes");
        assert!(files > 0 && bytes > 0);
    }

    /// Manual: hits Kuro's real CDN (indexes and a few small files only). `cargo test -- --ignored live`.
    #[test]
    #[ignore]
    fn live_cdn_index_and_small_files() {
        let found = sources(&Path::new(env!("CARGO_MANIFEST_DIR")).join("supported-client.json")).unwrap();
        for source in found {
            let net = Net::new(&source.data).unwrap();
            let cancel = no_cancel();
            let full = get_list(&net, &source.data.index_file, &source.data.index_md5, &cancel).unwrap();
            assert!(full.resource.iter().any(|e| e.dest == "PGR.exe"));
            let fresh = std::env::temp_dir().join(format!("ascnet-live-{}", uuid::Uuid::new_v4()));
            let p = plan(&source, Job::Install { dir: fresh }).unwrap();
            eprintln!("{} install plan: {} bytes (pinned size {}), {} files, needs {}", source.region.label(), p.download_bytes, source.full_size, p.files, p.required_free_bytes);
            let listed = full.resource.iter().map(|e| e.size).sum::<u64>();
            // A pinned document patch adds the CDN files the package does not hold on top of the launcher list.
            assert!(if source.data.document.is_some() { p.download_bytes > listed } else { p.download_bytes == listed }, "{} {listed}", p.download_bytes);
            for p in &source.data.patches {
                let list = get_list(&net, &p.index_file, &p.index_file_md5, &cancel).unwrap();
                validate_patch(&list).unwrap();
            }
            let tmp = Tmp::new();
            let identity = if source.region == Region::Cn { KRSDK_CONFIG } else { KRSDK_BIN };
            for dest in ["version.json", identity, "PGR_Data/app.info"] {
                let e = full.resource.iter().find(|e| e.dest == dest).unwrap();
                let ctx = Ctx::new(&cancel);
                let part = tmp.path("part");
                download_file(&ctx, &net, &format!("{}{}", source.data.base_url, e.dest), e.size, Hash::Md5, &e.md5, e.chunk_infos.as_deref(), &part).unwrap();
                assert_eq!(md5_file(&part).unwrap(), e.md5);
            }
            // Every pinned CDN host must answer a ranged request for a real file.
            let probe = format!("{}{}", source.data.base_url, "version.json");
            for cdn in &source.data.cdns {
                let one = Net { client: net.client.clone(), discovery: vec![], state: Mutex::new(NetState { cdns: vec![(cdn.clone(), 0)], discovered: true }) };
                let mut response = one.open(&probe, Some((0, 7)), &cancel).unwrap();
                let length = response.content_length();
                let body = read_body(&mut response, length, 8).unwrap();
                assert_eq!(body.len(), 8, "{cdn}");
            }
            eprintln!("{} ok: {} files, {} patches", source.region.label(), full.resource.len(), source.data.patches.len());
        }
    }
}

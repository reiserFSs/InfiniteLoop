//! In-game document patch: restores `PGR_Data/StreamingAssets/document/{matrix,launch}` from Kuro's patch CDN.
//!
//! Port of `Scripts/restore_document_patch.py`. The 4.9 client loads `document/<tree>/index` when its sha1 equals
//! the served `IndexSha1`/`LaunchIndexSha1` and then maps every file the package index (`resource/matrix/InPkgIndex`,
//! `resource/launch/index`) does not already hold with the same sha1 to `document/<tree>/<file>`. The newest
//! audio/video/InjectFix patch is therefore a hash-exact document tree plus the CDN's own index; nothing is
//! downloaded in game.
//!
//! URL scheme (xmoduleupdateinfo.lua `GetUrlByVersion`):
//! `<cdn>client/patch/<key>/<KuroGameIdentifier>/<version>/standalone/Release/<svn>/<tree>/<file>`, with key/version
//! from `document/ver` and the identifier from `document/client2cdn` when present, else from the pinned source.
//! The index itself is authenticated by the sha1 pinned in supported-client.json (`documentPatch.builds`); every file
//! by the sha1 in that index. Index files are written last per tree, so an interrupted run never leaves an index
//! pointing at missing files. Files that are not in the index are left alone.

use super::*;
use crate::bundle::{index_table, Entry as Row};

const STREAMING: &str = "PGR_Data/StreamingAssets";
/// Tree and its package-side index (`XModuleUpdateInfo.ApplicationIndex`); launch first: it is small.
const TREES: [(&str, &str); 2] = [("launch", "index"), ("matrix", "InPkgIndex")];

#[derive(Deserialize, Clone, Debug)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub(super) struct RawDocument {
    /// Patch CDN roots, https, ending in '/' (the `PrimaryCdns`/`SecondaryCdns` of the region's config.tab).
    cdns: Vec<String>,
    key: String,
    identifier: String,
    default_version: String,
    /// svn build -> version path segment, for builds that differ from `defaultVersion`.
    #[serde(default)]
    versions: BTreeMap<String, String>,
    /// Build used when `document/ver` is absent (a fresh install).
    default_build: String,
    /// svn build -> sha1 of the CDN's matrix/launch index (= `IndexSha1`/`LaunchIndexSha1` the server serves).
    builds: BTreeMap<String, RawBuild>,
}

#[derive(Deserialize, Clone, Debug)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct RawBuild {
    matrix_sha1: String,
    launch_sha1: String,
}

#[derive(Debug, Clone)]
pub(super) struct Document {
    pub(super) cdns: Vec<String>,
    key: String,
    identifier: String,
    default_version: String,
    versions: BTreeMap<String, String>,
    pub(super) default_build: String,
    builds: BTreeMap<String, RawBuild>,
}

fn is_segment(s: &str) -> bool {
    !s.is_empty() && s.len() <= 64 && s != "." && s != ".." && s.bytes().all(|b| b.is_ascii_alphanumeric() || b"._-".contains(&b))
}

fn is_sha1(s: &str) -> bool {
    s.len() == 40 && s.bytes().all(|b| b.is_ascii_hexdigit())
}

impl RawDocument {
    pub(super) fn validate(&self, label: &str) -> Result<Document> {
        ensure!(!self.cdns.is_empty(), "{label}: documentPatch has no CDN hosts");
        for cdn in &self.cdns {
            check_host_url(cdn).with_context(|| format!("{label}: bad documentPatch CDN"))?;
            ensure!(cdn.ends_with('/'), "{label}: documentPatch CDN must end with '/': {cdn}");
        }
        for s in [&self.key, &self.identifier, &self.default_version].into_iter().chain(self.versions.values()) {
            ensure!(is_segment(s), "{label}: bad documentPatch path segment {s:?}");
        }
        for (build, sums) in &self.builds {
            ensure!(is_segment(build) && build.bytes().all(|b| b.is_ascii_digit()), "{label}: bad document build {build:?}");
            ensure!(is_sha1(&sums.matrix_sha1) && is_sha1(&sums.launch_sha1), "{label}: bad document index sha1 for build {build}");
        }
        for build in self.versions.keys().chain([&self.default_build]) {
            ensure!(build.bytes().all(|b| b.is_ascii_digit()) && !build.is_empty(), "{label}: bad document build {build:?}");
        }
        ensure!(self.builds.contains_key(&self.default_build), "{label}: documentPatch defaultBuild {} has no pinned index", self.default_build);
        let lower = |sums: &RawBuild| RawBuild { matrix_sha1: sums.matrix_sha1.to_ascii_lowercase(), launch_sha1: sums.launch_sha1.to_ascii_lowercase() };
        Ok(Document {
            cdns: self.cdns.clone(),
            key: self.key.clone(),
            identifier: self.identifier.clone(),
            default_version: self.default_version.clone(),
            versions: self.versions.clone(),
            default_build: self.default_build.clone(),
            builds: self.builds.iter().map(|(k, v)| (k.clone(), lower(v))).collect(),
        })
    }
}

/// Where the files of each svn build live: pinned defaults overridden by the install's own `document/ver` and
/// `document/client2cdn` (their values are untrusted and only used when they are plain path segments).
pub(super) struct Locator {
    key: String,
    identifier: String,
    version: String,
    /// svn -> (key, version) from `document/ver` `VersionPath`.
    paths: BTreeMap<String, (Option<String>, Option<String>)>,
    /// svn -> KuroGameIdentifier from `document/client2cdn`.
    identifiers: BTreeMap<String, String>,
    pinned_versions: BTreeMap<String, String>,
}

impl Locator {
    fn base(&self, svn: &str) -> String {
        let (key, version) = self.paths.get(svn).cloned().unwrap_or_default();
        let key = key.unwrap_or_else(|| self.key.clone());
        let version = version.or_else(|| self.pinned_versions.get(svn).cloned()).unwrap_or_else(|| self.version.clone());
        let identifier = self.identifiers.get(svn).unwrap_or(&self.identifier);
        format!("client/patch/{key}/{identifier}/{version}/standalone/Release/{svn}")
    }

    pub(super) fn url(&self, svn: &str, tree: &str, name: &str) -> String {
        format!("{}/{tree}/{name}", self.base(svn))
    }
}

fn read_json(dir: &Path, name: &str) -> Option<serde_json::Value> {
    let rel = format!("{STREAMING}/document/{name}");
    let path = safe_path(dir, &rel).ok()?;
    fs::metadata(&path).ok().filter(|m| m.is_file() && m.len() <= 1024 * 1024)?;
    let text = String::from_utf8(fs::read(path).ok()?).ok()?;
    serde_json::from_str(text.trim_start_matches('\u{feff}')).ok()
}

/// (build, locator) for this install: `document/ver` `LeastVersionFile` or the pinned default build.
fn locate(doc: &Document, dir: &Path) -> (String, Locator) {
    let segment = |v: Option<&serde_json::Value>| v.and_then(|v| v.as_str()).filter(|s| is_segment(s)).map(str::to_owned);
    let ver = read_json(dir, "ver");
    let build = segment(ver.as_ref().and_then(|v| v.get("LeastVersionFile"))).filter(|b| b.bytes().all(|c| c.is_ascii_digit())).unwrap_or_else(|| doc.default_build.clone());
    let mut paths = BTreeMap::new();
    if let Some(map) = ver.as_ref().and_then(|v| v.get("VersionPath")).and_then(|v| v.as_object()) {
        for (svn, info) in map {
            if is_segment(svn) {
                paths.insert(svn.clone(), (segment(info.get("key")), segment(info.get("version"))));
            }
        }
    }
    let mut identifiers = BTreeMap::new();
    if let Some(map) = read_json(dir, "client2cdn").as_ref().and_then(|v| v.as_object()) {
        for (svn, info) in map {
            if let (true, Some(id)) = (is_segment(svn), segment(info.get("KuroGameIdentifier"))) {
                identifiers.insert(svn.clone(), id);
            }
        }
    }
    let locator = Locator {
        key: doc.key.clone(),
        identifier: doc.identifier.clone(),
        version: doc.default_version.clone(),
        paths,
        identifiers,
        pinned_versions: doc.versions.clone(),
    };
    (build, locator)
}

pub(super) struct TreePlan {
    pub(super) tree: &'static str,
    /// The CDN's index bytes, sha1-authenticated against the pin.
    index: Vec<u8>,
    sha1: String,
    /// Files document/ must hold: in the CDN index but not in the package index.
    pub(super) need: Vec<Row>,
}

impl TreePlan {
    pub(super) fn rel(&self, name: &str) -> String {
        format!("{STREAMING}/document/{}/{name}", self.tree)
    }
}

pub(super) struct DocPlan {
    pub(super) net: Option<Net>,
    pub(super) locator: Option<Locator>,
    pub(super) trees: Vec<TreePlan>,
    /// Reasons something was not restored (no pin, index drift).
    pub(super) notes: Vec<String>,
}

fn sha1_hex(data: &[u8]) -> String {
    use sha1::{Digest, Sha1};
    Sha1::digest(data).iter().map(|b| format!("{b:02x}")).collect()
}

fn package_rel(tree: &str, name: &str) -> String {
    format!("{STREAMING}/resource/{tree}/{name}")
}

/// The package index, MD5-checked against the official list: the local file, else the official download.
fn package_index(net: &Net, data: &SourceData, full: &FileList, dir: &Path, rel: &str, cancel: &AtomicBool) -> Result<Vec<u8>> {
    let entry = full.resource.iter().find(|e| e.dest == rel).with_context(|| format!("{rel} is not in the official file list"))?;
    if present(dir, rel, entry.size) {
        if let Ok(bytes) = fs::read(safe_path(dir, rel)?) {
            if md5_hex(&bytes) == entry.md5 {
                return Ok(bytes);
            }
        }
    }
    let bytes = net.get(&format!("{}{rel}", data.base_url), cancel)?;
    ensure!(bytes.len() as u64 == entry.size && md5_hex(&bytes) == entry.md5, "the downloaded {rel} does not match the official file list");
    Ok(bytes)
}

/// What `document/` still needs, from the CDN indexes and the package indexes. `net` is the launcher's main CDN
/// (package indexes); the patch CDN gets its own `Net`. Pin drift or a missing pin is a note, not an error.
pub(super) fn inspect(source: &ClientSource, net: &Net, dir: &Path, full: &FileList, cancel: &AtomicBool) -> Result<DocPlan> {
    let data = &source.data;
    let mut plan = DocPlan { net: None, locator: None, trees: Vec::new(), notes: Vec::new() };
    let Some(doc) = &data.document else { return Ok(plan) };
    let (build, locator) = locate(doc, dir);
    let Some(pin) = doc.builds.get(&build) else {
        plan.notes.push(format!("document build {build} has no pinned index; its patch files were not checked"));
        return Ok(plan);
    };
    let patch_net = Net::with_hosts(&[], &doc.cdns)?;
    for (tree, package) in TREES {
        let pinned = if tree == "matrix" { &pin.matrix_sha1 } else { &pin.launch_sha1 };
        let index = patch_net.get(&locator.url(&build, tree, "index"), cancel).with_context(|| format!("downloading the {tree} document index"))?;
        let actual = sha1_hex(&index);
        if actual != *pinned {
            plan.notes.push(format!("{tree} document index of build {build} changed on the CDN (sha1 {actual}, pinned {pinned}); not restored"));
            continue;
        }
        let files = index_table(&index).with_context(|| format!("reading the {tree} document index"))?;
        let package = index_table(&package_index(net, data, full, dir, &package_rel(tree, package), cancel)?)
            .with_context(|| format!("reading the package {tree} index"))?;
        let held: HashMap<&str, &Row> = package.iter().map(|r| (r.file.as_str(), r)).collect();
        let mut seen = HashSet::new();
        let mut need = Vec::new();
        for mut row in files {
            ensure!(!row.file.contains('/') && check_rel(&row.file).is_ok(), "unsafe file name in the {tree} document index: {:?}", row.file);
            ensure!(is_sha1(&row.sha1), "bad sha1 for {} in the {tree} document index", row.file);
            ensure!(seen.insert(row.file.clone()), "duplicate {} in the {tree} document index", row.file);
            row.sha1.make_ascii_lowercase();
            // XModuleUpdateManager:IsInnerResource: the package holds a file iff same name, flag != 1, same sha1.
            if held.get(row.file.as_str()).is_some_and(|p| p.flag != 1 && p.sha1.eq_ignore_ascii_case(&row.sha1)) {
                continue;
            }
            need.push(row);
        }
        plan.trees.push(TreePlan { tree, index, sha1: pinned.clone(), need });
    }
    plan.net = Some(patch_net);
    plan.locator = Some(locator);
    Ok(plan)
}

/// Bytes and files still missing by size (the planning convention: no hashing).
pub(super) fn estimate(plan: &DocPlan, dir: &Path) -> (u64, usize) {
    let mut total = (0, 0);
    for t in &plan.trees {
        for row in t.need.iter().filter(|r| !present(dir, &t.rel(&r.file), r.size)) {
            total.0 += row.size;
            total.1 += 1;
        }
    }
    total
}

fn sha1_matches(dir: &Path, rel: &str, size: u64, sha1: &str) -> Result<bool> {
    let path = safe_path(dir, rel)?;
    let Ok(meta) = fs::symlink_metadata(&path) else { return Ok(false) };
    ensure!(meta.is_file() && !is_link(&meta), "refusing non-regular file {}", path.display());
    if meta.len() != size {
        return Ok(false);
    }
    Ok(Hash::Sha1.range(&mut File::open(&path)?, 0, size)? == sha1)
}

impl Runner<'_> {
    /// Restores both document trees for the install's build. Resumable; the index of a tree goes in last.
    pub(super) fn document(&mut self, source: &ClientSource, full: &FileList) -> Result<()> {
        if source.data.document.is_none() {
            self.outcome.document_notes.push(format!("{}: no in-game document patch is pinned for this region; it was not restored", source.region.label()));
            return Ok(());
        }
        // The diff needs the package indexes the official list ships; the game-managed resource folder is not
        // otherwise repaired, so make sure these two small files are intact.
        let mut entries = Vec::new();
        for (tree, package) in TREES {
            let rel = package_rel(tree, package);
            entries.push(full.resource.iter().find(|e| e.dest == rel).with_context(|| format!("{rel} is not in the official file list"))?);
        }
        self.fix(&entries, &source.data.base_url, false)?;
        self.ctx.begin(Phase::FetchingIndex, 0, 0);
        self.tick();
        let plan = inspect(source, self.net, self.dir, full, self.ctx.cancel)?;
        self.outcome.document_notes.extend(plan.notes.iter().cloned());
        let (Some(net), Some(locator)) = (&plan.net, &plan.locator) else { return Ok(()) };
        for t in &plan.trees {
            self.restore_tree(net, locator, t)?;
        }
        Ok(())
    }

    fn restore_tree(&mut self, net: &Net, locator: &Locator, t: &TreePlan) -> Result<()> {
        let total: u64 = t.need.iter().map(|r| r.size).sum();
        self.ctx.begin(Phase::Verifying, t.need.len(), total);
        let threads = std::thread::available_parallelism().map_or(4, |n| n.get()).clamp(2, WORKERS);
        let (ctx, dir) = (self.ctx, self.dir);
        let verdicts = self.pool(&t.need, threads, &|r: &Row| {
            ctx.set_current(&r.file);
            let good = sha1_matches(dir, &t.rel(&r.file), r.size, &r.sha1)?;
            ctx.files_done.fetch_add(1, SeqCst);
            ctx.bytes_done.fetch_add(r.size, SeqCst);
            Ok((r.file.clone(), good))
        })?;
        let stale: HashSet<String> = verdicts.into_iter().filter(|(_, good)| !good).map(|(file, _)| file).collect();
        let mut items: Vec<Item> = t
            .need
            .iter()
            .filter(|r| stale.contains(&r.file))
            .map(|r| Item {
                dest: t.rel(&r.file),
                size: r.size,
                md5: r.sha1.clone(),
                hash: Hash::Sha1,
                chunks: None,
                url: locator.url(&r.svn.to_string(), t.tree, &r.file),
                members: None,
                repair: false,
            })
            .collect();
        items.sort_by_key(|i| std::cmp::Reverse(i.size));
        let fetched = items.len();
        self.download_via(net, items)?;
        self.outcome.document_files += fetched;

        let rel = t.rel("index");
        if !sha1_matches(self.dir, &rel, t.index.len() as u64, &t.sha1)? {
            let temp = self.state.join("parts").join(format!("{}.idx", part_name(&rel)));
            fs::write(&temp, &t.index)?;
            install_file(&temp, &safe_path(self.dir, &rel)?)?;
        }
        Ok(())
    }
}

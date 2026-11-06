use anyhow::{anyhow, bail, Context, Result};
use reqwest::Url;
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::{
    cmp::Ordering,
    collections::{BTreeMap, BTreeSet},
    fs::{self, File as FsFile},
    io::Read,
    path::{Component, Path, PathBuf},
};

const SCHEMA_VERSION: u32 = 1;
const MAX_METADATA_BYTES: u64 = 1024 * 1024;
const MAX_FILE_BYTES: u64 = 8 * 1024 * 1024 * 1024;
const REQUIRED_FILES: [(&str, &str); 4] = [
    ("version.dll", "version.dll"),
    ("lucia.dll", "lucia.dll"),
    ("PGR_Data/Plugins/KRSDK.dll", "KRSDK.dll"),
    ("libraries.txt", "libraries.txt"),
];
pub const KRSDK: &str = "PGR_Data/Plugins/KRSDK.dll";
const KRSDK_EX: &str = "PGR_Data/Plugins/KRSDKEx.dll";
const KRSDK_CURL: &str = "PGR_Data/Plugins/libkrsdkcurl.dll";
const KRSDK_BIN: &str = "PGR_Data/Plugins/KRSDKRes/KRSDK.bin";
const KRSDK_CONFIG: &str = "PGR_Data/Plugins/KRSDKRes/KRSDKConfig.json";
const REQUIRED_ORIGINALS: [&str; 5] = ["PGR.exe", "GameAssembly.dll", KRSDK, KRSDK_EX, KRSDK_CURL];

/// Global (EN/TW/KR/JP) clients ship `KRSDK.dll`, which the patch replaces; the CN client ships the official
/// `KRSDKEx.dll` + `libkrsdkcurl.dll`, which are only validated (lucia redirects their HTTP calls in-process).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Region {
    Global,
    Cn,
}

/// The CN SDK layout (`KRSDKEx.dll` + `KRSDKConfig.json`, no `KRSDK.bin`); a stray `KRSDK.dll` does not change it.
pub fn is_cn_layout(game: &Path) -> bool {
    game.join(KRSDK_EX).exists() && game.join(KRSDK_CONFIG).exists() && !game.join(KRSDK_BIN).exists()
}

/// Classifies the folder by its SDK files; a CN folder must also carry the allowlisted SDK pair.
pub fn client_region(application_version: &str, originals: &BTreeMap<String, Vec<String>>, game: &Path) -> Result<Region> {
    if !is_cn_layout(game) && game.join(KRSDK).exists() {
        return Ok(Region::Global);
    }
    if !game.join(KRSDK_EX).exists() {
        bail!("unsupported client: neither {KRSDK} nor {KRSDK_EX} found");
    }
    for path in [KRSDK_EX, KRSDK_CURL] {
        let hash = sha256_file(&game.join(path)).ok();
        if !hash.as_ref().is_some_and(|h| originals.get(path).is_some_and(|allowed| allowed.contains(h))) {
            bail!("{}", unsupported_client(application_version, path, hash.as_deref()));
        }
    }
    Ok(Region::Cn)
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Manifest {
    pub schema_version: u32,
    pub version: String,
    pub application_version: String,
    pub originals: BTreeMap<String, Vec<String>>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub pgr_base: Option<PgrBaseBuild>,
    pub files: Vec<File>,
}

impl Manifest {
    pub fn accepts_original(&self, path: &str, hash: Option<&str>) -> bool {
        hash.is_some_and(|hash| {
            self.originals
                .get(path)
                .is_some_and(|allowed| allowed.iter().any(|expected| expected == hash))
        })
    }

    /// A stock file the game's own updater may have written over a managed patch file: an accepted original
    /// (KRSDK.dll) or a known stock PGRBase.dll.
    pub fn is_stock(&self, path: &str, hash: Option<&str>) -> bool {
        self.accepts_original(path, hash)
            || (path == "PGRBase.dll"
                && hash.is_some_and(|hash| self.pgr_base.as_ref().is_some_and(|b| b.originals.iter().any(|o| o == hash))))
    }

    /// Stock-file hashes compiled into this launcher (used by restore, which has no package).
    pub fn bundled() -> Result<Manifest> {
        let metadata = parse_supported_client(include_bytes!("../supported-client.json"))?;
        Ok(Manifest {
            schema_version: SCHEMA_VERSION,
            version: env!("CARGO_PKG_VERSION").to_owned(),
            application_version: metadata.application_version,
            originals: metadata.originals,
            pgr_base: metadata.pgr_base,
            files: Vec::new(),
        })
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct PgrBaseBuild {
    pub originals: Vec<String>,
    pub unity_players: Vec<String>,
    /// Stock export-entry bytes seen across builds (global, CN); recovery tries each against `originals`.
    pub original_export_jumps: Vec<[u8; 5]>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct File {
    pub path: String,
    pub source: String,
    pub sha256: String,
    pub size: u64,
}

#[derive(Debug, Clone)]
#[non_exhaustive]
pub struct PatchPackage {
    pub manifest: Manifest,
    pub directory: PathBuf,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct SupportedClient {
    application_version: String,
    originals: BTreeMap<String, Vec<String>>,
    #[serde(default)]
    patch_version: Option<String>,
    #[serde(default)]
    pgr_base: Option<PgrBaseBuild>,
    /// Official-client sources, validated by `download::sources` (the patch package never reads them).
    #[serde(default)]
    #[allow(dead_code)]
    downloads: Option<serde_json::Value>,
}

pub fn refresh_supported_client(directory: &Path, source: &Path) -> Result<()> {
    let bytes = read_bounded(source, MAX_METADATA_BYTES)?;
    parse_supported_client(&bytes).context("invalid bundled supported-client.json")?;
    reject_links(directory, "supported-client.json")?;
    let destination = directory.join("supported-client.json");
    if read_bounded(&destination, MAX_METADATA_BYTES)? != bytes {
        fs::write(&destination, bytes)
            .with_context(|| format!("refresh {}", destination.display()))?;
    }
    Ok(())
}

/// Preflight before setup: the bundled metadata must accept the game's unpatched binaries.
pub fn check_supported_client(source: &Path, game: &Path) -> Result<()> {
    let metadata = parse_supported_client(&read_bounded(source, MAX_METADATA_BYTES)?)
        .context("invalid bundled supported-client.json")?;
    for path in ["PGR.exe", "GameAssembly.dll"] {
        let hash = sha256_file(&game.join(path)).ok();
        if !metadata.originals.get(path).is_some_and(|allowed| hash.as_ref().is_some_and(|h| allowed.contains(h))) {
            bail!("{}", unsupported_client(&metadata.application_version, path, hash.as_deref()));
        }
    }
    client_region(&metadata.application_version, &metadata.originals, game)?;
    Ok(())
}

/// Names the observed hash so players can compare it with the allowlist (EN/TW/KR/JP/CN share one list).
pub fn unsupported_client(application_version: &str, path: &str, observed: Option<&str>) -> String {
    let observed = observed.map_or("file missing or unreadable".to_owned(), |hash| format!("SHA-256 {hash}"));
    format!("{path} is not the supported {application_version} client ({observed}) — update the game if it is older, or the launcher if the game is newer")
}

fn parse_supported_client(bytes: &[u8]) -> Result<SupportedClient> {
    let metadata: SupportedClient =
        serde_json::from_slice(bytes).context("invalid supported-client.json")?;
    validate_supported_client(&metadata)?;
    if let Some(version) = &metadata.patch_version {
        parse_version(version).context("invalid patch version")?;
    }
    Ok(metadata)
}

pub fn load_package(directory: &Path) -> Result<PatchPackage> {
    let directory = fs::canonicalize(directory)
        .with_context(|| format!("open patch package {}", directory.display()))?;
    if !directory.is_dir() {
        bail!("patch package is not a directory: {}", directory.display());
    }
    let metadata_path = directory.join("supported-client.json");
    reject_links(&directory, "supported-client.json")?;
    let metadata = parse_supported_client(&read_bounded(&metadata_path, MAX_METADATA_BYTES)?)?;
    let version = metadata
        .patch_version
        .unwrap_or_else(|| env!("CARGO_PKG_VERSION").to_owned());

    let mut files = Vec::with_capacity(REQUIRED_FILES.len());
    for (path, source) in REQUIRED_FILES {
        reject_links(&directory, source)?;
        let source_path = directory.join(source);
        let info = fs::metadata(&source_path)
            .with_context(|| format!("inspect patch payload {}", source_path.display()))?;
        if !info.is_file() || info.len() > MAX_FILE_BYTES {
            bail!("invalid patch payload {source}");
        }
        files.push(File {
            path: path.into(),
            source: source.into(),
            sha256: sha256_file(&source_path)?,
            size: info.len(),
        });
    }
    Ok(PatchPackage {
        manifest: Manifest {
            schema_version: SCHEMA_VERSION,
            version,
            application_version: metadata.application_version,
            originals: metadata.originals,
            pgr_base: metadata.pgr_base,
            files,
        },
        directory,
    })
}

fn validate_supported_client(metadata: &SupportedClient) -> Result<()> {
    if metadata.application_version.is_empty()
        || metadata.application_version.len() > 128
        || metadata.application_version.chars().any(char::is_control)
    {
        bail!("invalid application version");
    }
    let expected: BTreeSet<_> = REQUIRED_ORIGINALS.into_iter().collect();
    if metadata
        .originals
        .keys()
        .map(String::as_str)
        .collect::<BTreeSet<_>>()
        != expected
    {
        bail!("supported-client.json must contain exactly the required original hashes");
    }
    for key in REQUIRED_ORIGINALS {
        let hashes = metadata
            .originals
            .get(key)
            .ok_or_else(|| anyhow!("missing original hashes for {key}"))?;
        if hashes.is_empty() {
            bail!("missing original hashes for {key}");
        }
        for hash in hashes {
            validate_hash(hash)?;
        }
    }
    if let Some(build) = &metadata.pgr_base {
        if build.original_export_jumps.is_empty() || build.original_export_jumps.iter().any(|jump| jump[0] != 0xe9) {
            bail!("PGRBase stock export preimages must be a non-empty list of relative jumps");
        }
        for hashes in [&build.originals, &build.unity_players] {
            if hashes.is_empty() {
                bail!("PGRBase startup patch requires verified stock PGRBase and UnityPlayer hashes");
            }
            for hash in hashes {
                validate_hash(hash)?;
            }
        }
    }
    Ok(())
}

pub fn compare_versions(a: &str, b: &str) -> Result<Ordering> {
    Ok(parse_version(a)?.cmp(&parse_version(b)?))
}

pub fn validate_server_origin(origin: &str) -> Result<String> {
    if origin.trim() != origin || origin.ends_with('/') {
        bail!("server origin must not contain whitespace or a trailing slash");
    }
    let url = Url::parse(origin).context("invalid server origin")?;
    if url.username() != ""
        || url.password().is_some()
        || url.query().is_some()
        || url.fragment().is_some()
    {
        bail!("server origin must not contain credentials, query, or fragment");
    }
    if url.path() != "/" {
        bail!("server origin must not contain a path");
    }
    match url.scheme() {
        "https" if url.host_str().is_some() => {}
        "http" if is_loopback(url.host_str().unwrap_or("")) => {}
        _ => bail!("server origin must use HTTPS (HTTP is allowed only for numeric loopback)"),
    }
    if url.port_or_known_default().is_none() {
        bail!("server origin has no valid port");
    }
    Ok(origin.to_owned())
}

pub fn sha256_file(path: &Path) -> Result<String> {
    let mut file = FsFile::open(path).with_context(|| format!("open {}", path.display()))?;
    let mut hash = Sha256::new();
    let mut buffer = [0u8; 64 * 1024];
    loop {
        let n = file
            .read(&mut buffer)
            .with_context(|| format!("read {}", path.display()))?;
        if n == 0 {
            break;
        }
        hash.update(&buffer[..n]);
    }
    Ok(format!("{:x}", hash.finalize()))
}

fn parse_version(value: &str) -> Result<Vec<u32>> {
    if value.is_empty() || value.len() > 64 {
        bail!("version must be 1 to 64 characters");
    }
    let parts: Vec<_> = value.split('.').collect();
    if parts.len() > 4 {
        bail!("version must contain one to four numeric components");
    }
    let mut parsed = parts
        .into_iter()
        .map(|part| {
            if part.is_empty()
                || (part.len() > 1 && part.starts_with('0'))
                || !part.bytes().all(|b| b.is_ascii_digit())
            {
                bail!("invalid version component");
            }
            part.parse::<u32>()
                .context("version component is too large")
        })
        .collect::<Result<Vec<_>>>()?;
    while parsed.len() > 1 && parsed.last() == Some(&0) {
        parsed.pop();
    }
    Ok(parsed)
}

fn validate_relative_path(value: &str) -> Result<()> {
    if value.is_empty()
        || value.len() > 240
        || value.chars().any(|c| ['\\', '?', '#', ':'].contains(&c))
        || value.starts_with('/')
        || Path::new(value)
            .components()
            .any(|part| !matches!(part, Component::Normal(_)))
        || value.split('/').any(|part| {
            part.is_empty()
                || part.ends_with('.')
                || part.ends_with(' ')
                || part.chars().any(char::is_control)
        })
    {
        bail!("invalid relative path {value:?}");
    }
    Ok(())
}

fn reject_links(root: &Path, relative: &str) -> Result<()> {
    validate_relative_path(relative)?;
    let mut path = root.to_path_buf();
    for component in relative.split('/') {
        path.push(component);
        if fs::symlink_metadata(&path)
            .with_context(|| format!("inspect {}", path.display()))?
            .file_type()
            .is_symlink()
        {
            bail!("patch package contains a link: {}", path.display());
        }
    }
    Ok(())
}

fn validate_hash(value: &str) -> Result<()> {
    if value.len() != 64
        || !value
            .bytes()
            .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
    {
        bail!("SHA256 must be 64 lowercase hexadecimal characters");
    }
    Ok(())
}

fn read_bounded(path: &Path, limit: u64) -> Result<Vec<u8>> {
    let file = FsFile::open(path).with_context(|| format!("open {}", path.display()))?;
    if file.metadata()?.len() > limit {
        bail!("{} exceeds size limit", path.display());
    }
    let mut bytes = Vec::new();
    file.take(limit + 1).read_to_end(&mut bytes)?;
    if bytes.len() as u64 > limit {
        bail!("{} exceeds size limit", path.display());
    }
    Ok(bytes)
}

fn is_loopback(host: &str) -> bool {
    host.parse::<std::net::IpAddr>()
        .is_ok_and(|ip| ip.is_loopback())
}

#[cfg(test)]
mod tests {
    use super::*;
    use uuid::Uuid;

    fn package() -> PathBuf {
        let root = std::env::temp_dir().join(format!("ascnet-package-test-{}", Uuid::new_v4()));
        fs::create_dir(&root).unwrap();
        for (_, source) in REQUIRED_FILES {
            fs::write(root.join(source), source).unwrap();
        }
        fs::write(
            root.join("supported-client.json"),
            serde_json::to_vec(&serde_json::json!({
                "applicationVersion": "4.7.0",
                "patchVersion": "2.0.0",
                "originals": {
                    "PGR.exe": ["00".repeat(32)],
                    "GameAssembly.dll": ["11".repeat(32), "33".repeat(32)],
                    "PGR_Data/Plugins/KRSDK.dll": ["22".repeat(32)],
                    "PGR_Data/Plugins/KRSDKEx.dll": ["44".repeat(32)],
                    "PGR_Data/Plugins/libkrsdkcurl.dll": ["55".repeat(32)]
                }
            }))
            .unwrap(),
        )
        .unwrap();
        root
    }

    #[test]
    fn setup_preflight_rejects_unsupported_client_with_version_guidance() {
        let root = package();
        let game = root.join("game");
        fs::create_dir(&game).unwrap();
        fs::write(game.join("PGR.exe"), b"exe").unwrap();
        fs::write(game.join("GameAssembly.dll"), b"asm").unwrap();
        let metadata = root.join("supported-client.json");
        let error = check_supported_client(&metadata, &game).unwrap_err().to_string();
        assert_eq!(error, format!("PGR.exe is not the supported 4.7.0 client (SHA-256 {}) — update the game if it is older, or the launcher if the game is newer", sha256_file(&game.join("PGR.exe")).unwrap()));
        assert!(check_supported_client(&metadata, &game.join("missing")).unwrap_err().to_string().contains("(file missing or unreadable)"));
        let mut value: serde_json::Value = serde_json::from_slice(&fs::read(&metadata).unwrap()).unwrap();
        value["originals"]["PGR.exe"] = serde_json::json!([sha256_file(&game.join("PGR.exe")).unwrap()]);
        fs::write(&metadata, serde_json::to_vec(&value).unwrap()).unwrap();
        assert!(check_supported_client(&metadata, &game).unwrap_err().to_string().starts_with("GameAssembly.dll is not"));
        value["originals"]["GameAssembly.dll"] = serde_json::json!([sha256_file(&game.join("GameAssembly.dll")).unwrap()]);
        fs::write(&metadata, serde_json::to_vec(&value).unwrap()).unwrap();
        assert!(check_supported_client(&metadata, &game).unwrap_err().to_string().contains("neither"));
        fs::create_dir_all(game.join("PGR_Data/Plugins")).unwrap();
        fs::write(game.join(KRSDK), b"sdk").unwrap();
        check_supported_client(&metadata, &game).unwrap();
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn local_package_is_constructed_and_payload_changes_are_detected() {
        let root = package();
        let package = load_package(&root).unwrap();
        assert_eq!(package.manifest.version, "2.0.0");
        assert_eq!(package.manifest.files.len(), 4);
        fs::remove_file(root.join("version.dll")).unwrap();
        assert!(load_package(&root).is_err());
        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn supported_assembly_identities_are_exact() {
        let root = package();
        fs::write(
            root.join("supported-client.json"),
            include_bytes!("../supported-client.json"),
        )
        .unwrap();
        let package = load_package(&root).unwrap();
        for hash in [
            "910a2988f5819641ba3d0b5fcbcb8659e088b5899e7f3b22f4098efcfe1c4d5e",
            "ea70a4d72cd11fd9cfdaf9408ae79ab7e926ed1da8593a6d3c62db8f1283dbbd",
            "9defd05a6c7e6c3172bdc8f55bf9b7e70fba4ba355c92348f0996a82d56654ba",
            "ccb048f28e779237a6dc07841e0bb96857c6903dc3ec21cdbec3fde71c3d8b33",
            "48d5374b26608d57d2bf34a81350578285a3cb3c02957615336901990c3efb82",
        ] {
            assert!(package.manifest.accepts_original("GameAssembly.dll", Some(hash)));
            assert!(!package.manifest.accepts_original("PGR.exe", Some(hash)));
            assert!(!package.manifest.accepts_original("PGR_Data/Plugins/KRSDK.dll", Some(hash)));
        }
        assert!(!package.manifest.accepts_original("GameAssembly.dll", Some(&"00".repeat(32))));
        assert!(!package.manifest.accepts_original("GameAssembly.dll", None));
        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn kr_and_jp_retail_identities_are_accepted_from_bundled_metadata() {
        let root = package();
        fs::write(root.join("supported-client.json"), include_bytes!("../supported-client.json")).unwrap();
        let manifest = load_package(&root).unwrap().manifest;
        // SHA-256 of the 4.8.0 retail files (KR G286/50011, JP G282/50007); KRSDK.dll/PGRBase/UnityPlayer are shared.
        let kr = ("2cd82d51830b2e8ac373162aa2fbb9106ad3760906685abb51611770e1eb310e", "ccb048f28e779237a6dc07841e0bb96857c6903dc3ec21cdbec3fde71c3d8b33");
        let jp = ("2dd7b482516c0be745fb4e258f3523fb41e02ae16b82e5baae96e0b50c90c026", "48d5374b26608d57d2bf34a81350578285a3cb3c02957615336901990c3efb82");
        for (exe, assembly) in [kr, jp] {
            assert!(manifest.accepts_original("PGR.exe", Some(exe)));
            assert!(manifest.accepts_original("GameAssembly.dll", Some(assembly)));
            assert!(!manifest.accepts_original("PGR.exe", Some(assembly)));
            assert!(!manifest.accepts_original("GameAssembly.dll", Some(exe)));
        }
        assert!(manifest.accepts_original(
            "PGR_Data/Plugins/KRSDK.dll",
            Some("59a1d02def4c18ece4467cc4cf7b1da264055f1f09be94dafc61c6765b37465f")
        ));
        let base = manifest.pgr_base.unwrap();
        assert!(base.originals.contains(&"fbb01424adb76a2b4ea206eb550977276b2e30e5b316a32e2b5af7d90ceba9fe".into()));
        assert!(base.unity_players.contains(&"5a913706320879d1c189053dafc8bc6e29bddc15f5a4564d4326be225d9c91e5".into()));
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn bundled_metadata_upgrades_scalar_cache_without_trusting_old_hashes() {
        let root = package();
        let cached = root.join("supported-client.json");
        let source = root.join("bundled.json");
        let mut old: serde_json::Value =
            serde_json::from_slice(&fs::read(&cached).unwrap()).unwrap();
        for hashes in old["originals"].as_object_mut().unwrap().values_mut() {
            *hashes = hashes[0].clone();
        }
        fs::write(&cached, serde_json::to_vec(&old).unwrap()).unwrap();
        fs::write(&source, include_bytes!("../supported-client.json")).unwrap();
        refresh_supported_client(&root, &source).unwrap();
        let upgraded = load_package(&root).unwrap();
        let shipped: SupportedClient =
            parse_supported_client(include_bytes!("../supported-client.json")).unwrap();
        assert_eq!(upgraded.manifest.originals, shipped.originals);
        assert!(!upgraded.manifest.accepts_original("PGR.exe", Some(&"00".repeat(32))));
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn malformed_bundled_metadata_preserves_cache() {
        let root = package();
        let cached = root.join("supported-client.json");
        let source = root.join("bundled.json");
        let original = fs::read(&cached).unwrap();
        let mut invalid: serde_json::Value = serde_json::from_slice(&original).unwrap();
        invalid["patchVersion"] = serde_json::json!("1.02");
        for bytes in [b"{".to_vec(), serde_json::to_vec(&invalid).unwrap()] {
            fs::write(&source, bytes).unwrap();
            assert!(refresh_supported_client(&root, &source).is_err());
            assert_eq!(fs::read(&cached).unwrap(), original);
            assert_eq!(load_package(&root).unwrap().manifest.version, "2.0.0");
        }
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn pgrbase_metadata_requires_stock_identity_and_jump_preimage() {
        let root = package();
        let path = root.join("supported-client.json");
        let mut metadata: serde_json::Value = serde_json::from_slice(&fs::read(&path).unwrap()).unwrap();
        for build in [
            serde_json::json!({"originals": [], "unityPlayers": ["11".repeat(32)], "originalExportJumps": [[233, 0, 0, 0, 0]]}),
            serde_json::json!({"originals": ["00".repeat(32)], "unityPlayers": [], "originalExportJumps": [[233, 0, 0, 0, 0]]}),
            serde_json::json!({"originals": ["00".repeat(32)], "unityPlayers": ["11".repeat(32)], "originalExportJumps": [[233, 0, 0, 0, 0], [144, 0, 0, 0, 0]]}),
            serde_json::json!({"originals": ["00".repeat(32)], "unityPlayers": ["11".repeat(32)], "originalExportJumps": []}),
        ] {
            metadata["pgrBase"] = build;
            fs::write(&path, serde_json::to_vec(&metadata).unwrap()).unwrap();
            assert!(load_package(&root).is_err());
        }
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn cn_retail_identities_are_accepted_from_bundled_metadata_and_lookalikes_rejected() {
        let root = package();
        fs::write(root.join("supported-client.json"), include_bytes!("../supported-client.json")).unwrap();
        let manifest = load_package(&root).unwrap().manifest;
        let cn = [
            ("PGR.exe", "489e674af8c981c083a022b1b268d9fec27f4f2d93f9f1ccb655ad0c0b9a3179"),
            ("GameAssembly.dll", "af68b08ff132a4ea598c057752491b5854f7ccf8944140225ca0d5ed492c8873"),
            (KRSDK_EX, "fa6c877d5f4ebba42728c8765c2fe40bb9cc0995fde7219bc663cdedd727e0a9"),
            (KRSDK_CURL, "c5278160ce6b91af59e8a8d317632564be8c95922248f21cc2768701f7fddc91"),
        ];
        for (path, hash) in cn {
            assert!(manifest.accepts_original(path, Some(hash)), "{path}");
            let mut lookalike = hash.to_owned();
            lookalike.replace_range(63.., if hash.ends_with('0') { "1" } else { "0" });
            assert!(!manifest.accepts_original(path, Some(&lookalike)), "{path}");
            // A CN hash is only valid in its own slot.
            for (other, _) in cn.iter().filter(|(other, _)| *other != path) {
                assert!(!manifest.accepts_original(other, Some(hash)), "{path} in {other}");
            }
            assert!(!manifest.accepts_original(KRSDK, Some(hash)), "{path} in KRSDK.dll");
        }
        let base = manifest.pgr_base.unwrap();
        assert!(base.originals.contains(&"6c2abc3486218a2ee21ecbe55540e3f2083022a339a2e10b698eaa4293f9102d".into()));
        assert_eq!(base.original_export_jumps, [[233, 174, 101, 44, 1], [233, 11, 27, 0, 1]]);
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn sdk_original_keys_are_exactly_the_three_sdk_slots() {
        let root = package();
        let path = root.join("supported-client.json");
        let base: serde_json::Value = serde_json::from_slice(&fs::read(&path).unwrap()).unwrap();
        let mut broken = vec![];
        for key in [KRSDK, KRSDK_EX, KRSDK_CURL] {
            let mut missing = base.clone();
            missing["originals"].as_object_mut().unwrap().remove(key);
            let mut empty = base.clone();
            empty["originals"][key] = serde_json::json!([]);
            broken.extend([missing, empty]);
        }
        let mut extra = base.clone();
        extra["originals"]["PGR_Data/Plugins/other.dll"] = serde_json::json!(["66".repeat(32)]);
        broken.push(extra);
        for metadata in broken {
            fs::write(&path, serde_json::to_vec(&metadata).unwrap()).unwrap();
            assert!(load_package(&root).is_err());
        }
        fs::write(&path, serde_json::to_vec(&base).unwrap()).unwrap();
        load_package(&root).unwrap();
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn region_follows_the_sdk_files_and_cn_sdk_pair_must_be_allowlisted() {
        let game = std::env::temp_dir().join(format!("ascnet-region-test-{}", Uuid::new_v4()));
        fs::create_dir_all(game.join("PGR_Data/Plugins")).unwrap();
        let originals = |ex: &[u8], curl: &[u8]| -> BTreeMap<String, Vec<String>> {
            let hash = |bytes: &[u8]| format!("{:x}", Sha256::digest(bytes));
            BTreeMap::from([(KRSDK_EX.into(), vec![hash(ex)]), (KRSDK_CURL.into(), vec![hash(curl)])])
        };
        let neither = client_region("4.8.0", &originals(b"ex", b"curl"), &game).unwrap_err().to_string();
        assert!(neither.contains("neither") && neither.contains("KRSDK.dll") && neither.contains("KRSDKEx.dll"), "{neither}");
        fs::write(game.join(KRSDK_EX), b"ex").unwrap();
        fs::write(game.join(KRSDK_CURL), b"curl").unwrap();
        assert_eq!(client_region("4.8.0", &originals(b"ex", b"curl"), &game).unwrap(), Region::Cn);
        let observed = sha256_file(&game.join(KRSDK_CURL)).unwrap();
        let error = client_region("4.8.0", &originals(b"ex", b"other"), &game).unwrap_err().to_string();
        assert!(error.starts_with("PGR_Data/Plugins/libkrsdkcurl.dll is not the supported 4.8.0 client") && error.contains(&observed), "{error}");
        fs::remove_file(game.join(KRSDK_CURL)).unwrap();
        assert!(client_region("4.8.0", &originals(b"ex", b"curl"), &game).unwrap_err().to_string().contains("file missing"));
        // KRSDK.dll wins unless the CN SDK layout is present: a global client needs no CN pair.
        fs::write(game.join(KRSDK), b"sdk").unwrap();
        assert_eq!(client_region("4.8.0", &BTreeMap::new(), &game).unwrap(), Region::Global);
        // A leftover AscNet KRSDK.dll in a CN folder (KRSDKEx + KRSDKConfig.json, no KRSDK.bin) stays CN...
        fs::write(game.join(KRSDK_CURL), b"curl").unwrap();
        fs::create_dir_all(game.join("PGR_Data/Plugins/KRSDKRes")).unwrap();
        fs::write(game.join(KRSDK_CONFIG), b"{}").unwrap();
        assert_eq!(client_region("4.8.0", &originals(b"ex", b"curl"), &game).unwrap(), Region::Cn);
        assert!(client_region("4.8.0", &originals(b"ex", b"other"), &game).is_err());
        // ...but a KRSDK.bin (global/Steam) wins.
        fs::write(game.join(KRSDK_BIN), b"KR_ProjectId=G143").unwrap();
        assert_eq!(client_region("4.8.0", &BTreeMap::new(), &game).unwrap(), Region::Global);
        fs::remove_dir_all(game).unwrap();
    }

    #[test]
    fn versions_paths_and_origins_are_strict() {
        assert_eq!(compare_versions("1.2.0", "1.2").unwrap(), Ordering::Equal);
        assert!(compare_versions("1.02", "1.2").is_err());
        assert!(validate_relative_path("../x").is_err());
        assert!(validate_server_origin("http://127.0.0.1:5000").is_ok());
        assert!(validate_server_origin("http://example.com").is_err());
    }
}

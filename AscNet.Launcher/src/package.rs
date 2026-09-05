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
const REQUIRED_ORIGINALS: [&str; 3] = ["PGR.exe", "GameAssembly.dll", "PGR_Data/Plugins/KRSDK.dll"];

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Manifest {
    pub schema_version: u32,
    pub version: String,
    pub application_version: String,
    pub originals: BTreeMap<String, String>,
    pub files: Vec<File>,
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
    originals: BTreeMap<String, String>,
    #[serde(default)]
    patch_version: Option<String>,
}

pub fn load_package(directory: &Path) -> Result<PatchPackage> {
    let directory = fs::canonicalize(directory)
        .with_context(|| format!("open patch package {}", directory.display()))?;
    if !directory.is_dir() {
        bail!("patch package is not a directory: {}", directory.display());
    }
    let metadata_path = directory.join("supported-client.json");
    reject_links(&directory, "supported-client.json")?;
    let metadata: SupportedClient =
        serde_json::from_slice(&read_bounded(&metadata_path, MAX_METADATA_BYTES)?)
            .context("invalid supported-client.json")?;
    validate_supported_client(&metadata)?;
    let version = metadata
        .patch_version
        .unwrap_or_else(|| env!("CARGO_PKG_VERSION").to_owned());
    parse_version(&version).context("invalid patch version")?;

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
        validate_hash(
            metadata
                .originals
                .get(key)
                .ok_or_else(|| anyhow!("missing original hash for {key}"))?,
        )?;
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
                    "PGR.exe": "00".repeat(32),
                    "GameAssembly.dll": "11".repeat(32),
                    "PGR_Data/Plugins/KRSDK.dll": "22".repeat(32)
                }
            }))
            .unwrap(),
        )
        .unwrap();
        root
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
    fn versions_paths_and_origins_are_strict() {
        assert_eq!(compare_versions("1.2.0", "1.2").unwrap(), Ordering::Equal);
        assert!(compare_versions("1.02", "1.2").is_err());
        assert!(validate_relative_path("../x").is_err());
        assert!(validate_server_origin("http://127.0.0.1:5000").is_ok());
        assert!(validate_server_origin("http://example.com").is_err());
    }
}

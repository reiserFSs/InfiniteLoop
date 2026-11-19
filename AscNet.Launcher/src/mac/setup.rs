//! macOS local source setup: the counterpart of `crate::setup` (Windows). Installs the .NET 8 SDK and
//! MongoDB from their official arm64 archives, Rust through rustup plus `cargo-xwin`, then builds the
//! AscNet server (`dotnet publish`) and the Windows patch DLLs. The Windows patch DLLs are cross-built:
//! `AscNet.Patch` with `cargo xwin`, the C version loader with Apple clang and rust-lld against the same
//! xwin sysroot. Git and the C compiler come from the Xcode Command Line Tools.
//! `Runtime` starts and stops MongoDB and the server in one process group.
use crate::backend::{MongoPlan, Plan, ServerPlan};
use crate::local::{
    mongo_arguments, pump_output, wait_server, wait_tcp, LocalBuild, RotatingLog,
    BACKEND_LOG_LIMIT, STOP_TIMEOUT,
};
use crate::setup::{
    capture_command, choose_ports, download_file, ensure_time, git_text, install_runtime_config,
    first_sha256, note, prepare_config, read_download_text, read_previous_state, run_logged,
    scratch_file, update_checkout,
};
use anyhow::{bail, Context, Result};
use serde_json::Value;
use std::{
    fs,
    io::Write,
    os::unix::process::CommandExt,
    path::{Path, PathBuf},
    process::{Child, Command, Stdio},
    thread,
    time::{Duration, Instant},
};

const RUST_VERSION: &str = "1.92.0";
const RUST_TOOLCHAIN: &str = "1.92.0-aarch64-apple-darwin";
const WINDOWS_TARGET: &str = "x86_64-pc-windows-msvc";
const CARGO_XWIN_VERSION: &str = "0.23.1";
const RUSTUP_INIT_URL: &str = "https://static.rust-lang.org/rustup/dist/aarch64-apple-darwin/rustup-init";
const DOTNET_RELEASES: &str = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/8.0/releases.json";
const MONGO_CATALOG: &str = "https://downloads.mongodb.org/full.json";
const NUGET_INDEX: &str = "https://api.nuget.org/v3/index.json";
/// Developer ID team that signs the .NET host (Microsoft Corporation).
const DOTNET_TEAM: &str = "UBF8T346G9";
/// Developer ID team that signs MongoDB's macOS binaries (MongoDB, Inc.).
const MONGO_TEAM: &str = "4XWMY46275";
const CLANG: &str = "/usr/bin/clang";
/// Rust tools see only this PATH after the toolchain bin: Apple clang, never a Homebrew LLVM.
const SYSTEM_PATH: &str = "/usr/bin:/bin:/usr/sbin:/sbin";

type Progress<'a> = &'a mut dyn FnMut(&str);
type Log<'a> = &'a mut dyn FnMut(&str) -> Result<()>;

/// Same contract as `crate::setup::run`; leaves `build-state.pending.json` for the launcher to validate.
pub fn run(
    root: &Path,
    repository: &str,
    branch: &str,
    launcher_dir: &Path,
    progress: Progress,
    log: Log,
    deadline: Instant,
) -> Result<()> {
    ensure_time(deadline)?;
    fs::create_dir_all(root.join("tmp"))?;
    let root = &fs::canonicalize(root).with_context(|| format!("resolve {}", root.display()))?;
    let checkout = root.join("checkout");
    let git = ensure_git(deadline, progress, log)?;
    note(progress, log, "Updating source checkout")?;
    update_checkout(&git, &checkout, repository, branch, deadline, progress, log)?;
    let revision = git_text(&git, Some(&checkout), &["rev-parse".into(), "HEAD".into()], "git rev-parse HEAD", deadline, progress, log)?;
    if revision.len() != 40 || !revision.bytes().all(|byte| byte.is_ascii_hexdigit()) {
        bail!("git returned an invalid revision");
    }
    let previous = read_previous_state(&root.join("build-state.json"))?;
    let tools = root.join("tools");
    fs::create_dir_all(&tools)?;
    let dotnet = ensure_dotnet(root, &tools, deadline, progress, log)?;
    ensure_rust(root, &tools, deadline, progress, log)?;
    let mongod = ensure_mongo(root, &tools, previous.as_ref(), deadline, progress, log)?;
    fs::create_dir_all(root.join("data").join("mongo"))?;
    fs::create_dir_all(root.join("logs"))?;
    let (sdk_port, game_port, mongo_port) = choose_ports(root, previous.as_ref())?;
    let config_path = root.join("config.json");
    prepare_config(&checkout, &config_path, game_port, mongo_port)?;
    let build_root = root.join("build");
    fs::create_dir_all(&build_root)?;
    let final_dir = build_root.join(&revision);
    let stage = build_root.join(format!("{revision}.tmp-{}", std::process::id()));
    let complete = final_dir.join("server").join("AscNet.dll").is_file()
        && crate::package::load_package(&final_dir.join("patch")).is_ok();
    if !complete && final_dir.exists() {
        note(progress, log, &format!("Cached build {revision} is incomplete; rebuilding it"))?;
    }
    if !complete {
        let _ = fs::remove_dir_all(&stage);
        fs::create_dir_all(&stage)?;
        let built = (|| -> Result<()> {
            publish_server(&dotnet, root, &checkout, &stage, &config_path, deadline, progress, log)?;
            build_patch(root, &tools, &checkout, &stage, deadline, progress, log)?;
            build_version_loader(&tools, &checkout, &stage, launcher_dir, deadline, progress, log)?;
            let _ = fs::remove_dir_all(stage.join("dotnet-artifacts"));
            let _ = fs::remove_dir_all(stage.join("loader"));
            if final_dir.exists() {
                fs::remove_dir_all(&final_dir)?;
            }
            fs::rename(&stage, &final_dir).context("move staged build into place")
        })();
        if built.is_err() {
            let _ = fs::remove_dir_all(&stage);
        }
        built?;
    }
    let server_directory = final_dir.join("server");
    let state = LocalBuild {
        schema_version: crate::local::SCHEMA_VERSION,
        revision,
        repository: repository.to_owned(),
        dotnet,
        mongod,
        server_directory: server_directory.clone(),
        resource_directory: server_directory,
        patch_directory: final_dir.join("patch"),
        sdk_port,
        game_port,
        mongo_port,
    };
    let mut bytes = serde_json::to_vec_pretty(&state)?;
    bytes.push(b'\n');
    let pending = root.join("build-state.pending.json");
    fs::write(&pending, bytes).with_context(|| format!("write {}", pending.display()))?;
    note(progress, log, &format!("AscNet local build prepared for launcher validation: {}", state.revision))?;
    note(progress, log, &format!("Pending state: {}", pending.display()))
}

/// Git and Apple clang both come from the Command Line Tools; `/usr/bin/git` is a stub that opens an
/// installer dialog without them, so ask `xcode-select` first.
fn ensure_git(deadline: Instant, progress: Progress, log: Log) -> Result<PathBuf> {
    let mut probe = Command::new("/usr/bin/xcode-select");
    probe.arg("-p");
    capture_command(&mut probe, "xcode-select -p", deadline, false, progress, log)
        .context("The Xcode Command Line Tools are required for Git and the C compiler. Run `xcode-select --install`, then run Setup again.")?;
    let git = PathBuf::from("/usr/bin/git");
    let mut version = Command::new(&git);
    version.arg("--version").env("GIT_TERMINAL_PROMPT", "0");
    capture_command(&mut version, "git --version", deadline, false, progress, log)?;
    Ok(git)
}

/// Official archives are served over HTTPS and published with a checksum, which `download_file` verifies.
/// Unpack into a sibling `.partial` directory, then rename it into place.
fn extract_tar_gz(archive: &Path, destination: &Path) -> Result<()> {
    let mut partial = destination.as_os_str().to_owned();
    partial.push(".partial");
    let partial = PathBuf::from(partial);
    let _ = fs::remove_dir_all(&partial);
    fs::create_dir_all(&partial)?;
    let unpacked = (|| -> Result<()> {
        let decoder = flate2::read::GzDecoder::new(fs::File::open(archive)?);
        let mut tar = tar::Archive::new(decoder);
        for entry in tar.entries().context("read archive")? {
            let mut entry = entry?;
            if !entry.unpack_in(&partial)? {
                bail!("archive entry escapes the destination: {}", entry.path()?.display());
            }
        }
        if destination.exists() {
            fs::remove_dir_all(destination)?;
        }
        fs::rename(&partial, destination).with_context(|| format!("install {}", destination.display()))
    })();
    if unpacked.is_err() {
        let _ = fs::remove_dir_all(&partial);
    }
    unpacked
}

/// `codesign --verify` proves the binary is intact; the team identifier proves who signed it.
fn verify_signature(binary: &Path, team: &str, deadline: Instant, progress: Progress, log: Log) -> Result<()> {
    let mut verify = Command::new("/usr/bin/codesign");
    verify.args(["--verify", "--strict"]).arg(binary);
    capture_command(&mut verify, "codesign --verify", deadline, false, progress, log)?;
    let mut show = Command::new("/usr/bin/codesign");
    show.args(["-dvv"]).arg(binary);
    let output = capture_command(&mut show, "codesign -dvv", deadline, false, progress, log)?;
    let text = String::from_utf8_lossy(&output.stderr);
    if !text.lines().any(|line| line.trim() == format!("TeamIdentifier={team}")) {
        bail!("{} is not signed by team {team}", binary.display());
    }
    Ok(())
}

fn dotnet_sdk8(dotnet: &Path, deadline: Instant, progress: Progress, log: Log) -> Option<String> {
    if !dotnet.is_file() {
        return None;
    }
    let mut command = Command::new(dotnet);
    command.arg("--list-sdks");
    dotnet_env(&mut command, dotnet.parent()?.parent()?);
    let output = capture_command(&mut command, "dotnet --list-sdks", deadline, false, progress, log).ok()?;
    let text = String::from_utf8_lossy(&output.stdout);
    text.lines().map(str::trim).find(|line| line.starts_with("8.")).and_then(|line| line.split_whitespace().next()).map(str::to_owned)
}

/// Keep the user's `~/.dotnet` and `~/.nuget` untouched: everything stays under the local root.
fn dotnet_env(command: &mut Command, root: &Path) {
    command
        .env("DOTNET_CLI_TELEMETRY_OPTOUT", "1")
        .env("DOTNET_NOLOGO", "1")
        .env("DOTNET_SKIP_FIRST_TIME_EXPERIENCE", "1")
        .env("DOTNET_CLI_HOME", root.join("tools").join("dotnet-home"))
        .env("NUGET_PACKAGES", root.join("tools").join("nuget"));
}

/// (url, sha512) of the newest 8.0 SDK tarball for osx-arm64.
fn select_dotnet_sdk(document: &str) -> Result<(String, String)> {
    let parsed: Value = serde_json::from_str(document).context("invalid .NET release metadata")?;
    let files = parsed["releases"].as_array().into_iter().flatten().filter_map(|release| release["sdk"]["files"].as_array()).flatten();
    for file in files {
        if file["name"].as_str() != Some("dotnet-sdk-osx-arm64.tar.gz") {
            continue;
        }
        let url = file["url"].as_str().context(".NET metadata is missing the macOS SDK URL")?;
        let hash = file["hash"].as_str().unwrap_or("").trim().to_ascii_lowercase();
        if hash.len() != 128 || !hash.bytes().all(|byte| byte.is_ascii_hexdigit()) {
            bail!(".NET metadata did not provide a SHA-512 for {url}");
        }
        return Ok((url.to_owned(), hash));
    }
    bail!(".NET 8 metadata contains no osx-arm64 SDK tarball");
}

fn ensure_dotnet(root: &Path, tools: &Path, deadline: Instant, progress: Progress, log: Log) -> Result<PathBuf> {
    let destination = tools.join("dotnet");
    let dotnet = destination.join("dotnet");
    if dotnet_sdk8(&dotnet, deadline, progress, log).is_some() {
        return Ok(dotnet);
    }
    note(progress, log, "Installing the .NET 8 SDK")?;
    let (url, sha512) = select_dotnet_sdk(&read_download_text(DOTNET_RELEASES, deadline, progress, log)?)?;
    let archive = scratch_file(root, "dotnet-sdk.tar.gz");
    download_file(&url, &archive, None, Some(&sha512), root, deadline, progress, log)?;
    let extracted = extract_tar_gz(&archive, &destination);
    let _ = fs::remove_file(&archive);
    extracted.context("extract .NET SDK")?;
    verify_signature(&dotnet, DOTNET_TEAM, deadline, progress, log)?;
    if dotnet_sdk8(&dotnet, deadline, progress, log).is_none() {
        bail!(".NET 8 SDK was installed but dotnet could not run it.");
    }
    Ok(dotnet)
}

fn rustup_home(tools: &Path) -> PathBuf {
    tools.join("rustup")
}

fn cargo_home(tools: &Path) -> PathBuf {
    tools.join("cargo")
}

fn rust_command(program: &Path, tools: &Path) -> Command {
    let mut command = Command::new(program);
    for key in ["RUSTC", "RUSTC_WRAPPER", "RUSTFLAGS", "RUSTDOC", "CARGO", "CARGO_ENCODED_RUSTFLAGS", "CARGO_TARGET_DIR", "CARGO_BUILD_TARGET", "CARGO_BUILD_RUSTFLAGS"] {
        command.env_remove(key);
    }
    command
        .env("RUSTUP_HOME", rustup_home(tools))
        .env("CARGO_HOME", cargo_home(tools))
        .env("RUSTUP_TOOLCHAIN", RUST_VERSION)
        .env("XWIN_CACHE_DIR", tools.join("xwin"))
        .env("PATH", format!("{}:{SYSTEM_PATH}", cargo_home(tools).join("bin").display()));
    command
}

/// rustup-init (verified against Rust's published checksum) installs the pinned toolchain with the
/// Windows target; `cargo install --locked` then builds the pinned `cargo-xwin` from crates.io.
fn ensure_rust(root: &Path, tools: &Path, deadline: Instant, progress: Progress, log: Log) -> Result<()> {
    let cargo = cargo_home(tools).join("bin").join("cargo");
    let std_ready = rustup_home(tools).join("toolchains").join(RUST_TOOLCHAIN).join("lib").join("rustlib").join(WINDOWS_TARGET).join("lib").is_dir();
    if !(cargo.is_file() && std_ready) {
        note(progress, log, &format!("Installing Rust {RUST_VERSION}"))?;
        let sidecar = read_download_text(&format!("{RUSTUP_INIT_URL}.sha256"), deadline, progress, log)?;
        let sha256 = first_sha256(&sidecar).context("Rust's published checksum for rustup-init is missing")?;
        // rustup-init decides what to do from its own file name, so it keeps that name in a scratch folder.
        let scratch = scratch_file(root, "rustup");
        let init = scratch.join("rustup-init");
        download_file(RUSTUP_INIT_URL, &init, Some(&sha256), None, root, deadline, progress, log)?;
        let installed = (|| -> Result<()> {
            use std::os::unix::fs::PermissionsExt;
            fs::set_permissions(&init, fs::Permissions::from_mode(0o755))?;
            let mut command = rust_command(&init, tools);
            command.env("RUSTUP_INIT_SKIP_PATH_CHECK", "yes").args([
                "-y", "--no-modify-path", "--profile", "minimal", "--default-toolchain", RUST_VERSION, "-t", WINDOWS_TARGET,
            ]);
            run_logged(&mut command, "Installing Rust", deadline, progress, log)
        })();
        let _ = fs::remove_dir_all(&scratch);
        installed?;
    }
    if !cargo_home(tools).join("bin").join("cargo-xwin").is_file() {
        note(progress, log, &format!("Installing cargo-xwin {CARGO_XWIN_VERSION}"))?;
        let mut command = rust_command(&cargo, tools);
        command.args(["install", "--locked", "--version", CARGO_XWIN_VERSION, "cargo-xwin"]);
        run_logged(&mut command, "Installing cargo-xwin", deadline, progress, log)?;
    }
    Ok(())
}

/// (version, url, sha256) of the newest stable 8.0.x arm64 community tarball.
fn select_mongo_archive(document: &str) -> Result<(String, String, Option<String>)> {
    let catalog: Value = serde_json::from_str(document).context("invalid MongoDB download metadata")?;
    let patch = |version: &str| -> Option<u32> {
        let parts: Vec<&str> = version.split('.').collect();
        (parts.len() == 3 && parts[0] == "8" && parts[1] == "0").then(|| parts[2].parse().ok()).flatten()
    };
    let newest = catalog["versions"]
        .as_array()
        .into_iter()
        .flatten()
        .filter_map(|entry| Some((patch(entry["version"].as_str()?)?, entry)))
        .max_by_key(|(patch, _)| *patch)
        .map(|(_, entry)| entry)
        .context("MongoDB metadata contains no stable 8.0 release.")?;
    let version = newest["version"].as_str().unwrap_or_default();
    let archive = newest["downloads"]
        .as_array()
        .into_iter()
        .flatten()
        .find(|download| download["target"] == "macos" && download["arch"] == "arm64" && download["edition"] == "base")
        .map(|download| &download["archive"])
        .with_context(|| format!("MongoDB metadata contains no macOS arm64 Community tarball for {version}."))?;
    let url = archive["url"].as_str().context("MongoDB archive URL is missing")?;
    let sha256 = archive["sha256"].as_str().map(str::trim).filter(|value| value.len() == 64 && value.bytes().all(|byte| byte.is_ascii_hexdigit())).map(str::to_ascii_lowercase);
    Ok((version.to_owned(), url.to_owned(), sha256))
}

fn ensure_mongo(root: &Path, tools: &Path, previous: Option<&Value>, deadline: Instant, progress: Progress, log: Log) -> Result<PathBuf> {
    if let Some(pinned) = previous.and_then(|state| state.get("mongod")).and_then(Value::as_str) {
        let pinned = PathBuf::from(pinned);
        if !pinned.starts_with(tools) {
            bail!("Active build-state mongod is outside the owned tools directory: {}", pinned.display());
        }
        if pinned.is_file() {
            return Ok(pinned);
        }
        if fs::read_dir(root.join("data").join("mongo")).is_ok_and(|mut entries| entries.next().is_some()) {
            bail!("The pinned MongoDB executable is missing ({}), but persistent database files exist. Restore that owned MongoDB version; setup will not upgrade the database engine opportunistically.", pinned.display());
        }
    }
    if let Some(existing) = fs::read_dir(tools)?.flatten().map(|entry| entry.path().join("bin").join("mongod")).find(|path| path.is_file() && path.to_string_lossy().contains("/mongodb-")) {
        return Ok(existing);
    }
    note(progress, log, "Installing MongoDB 8.0")?;
    let (version, url, sha256) = select_mongo_archive(&read_download_text(MONGO_CATALOG, deadline, progress, log)?)?;
    let sha256 = match sha256 {
        Some(hash) => hash,
        None => first_sha256(&read_download_text(&format!("{url}.sha256"), deadline, progress, log)?)
            .with_context(|| format!("MongoDB's official metadata did not provide a valid SHA-256 for {url}"))?,
    };
    let archive = scratch_file(root, "mongodb.tgz");
    download_file(&url, &archive, Some(&sha256), None, root, deadline, progress, log)?;
    let destination = tools.join(format!("mongodb-{version}"));
    let extracted = extract_tar_gz(&archive, &destination);
    let _ = fs::remove_file(&archive);
    extracted.context("extract MongoDB")?;
    // The tarball has one top folder (`mongodb-macos-aarch64-<version>`) holding bin/mongod.
    let mongod = fs::read_dir(&destination)?
        .flatten()
        .map(|entry| entry.path().join("bin").join("mongod"))
        .find(|path| path.is_file())
        .context("The verified MongoDB archive did not contain bin/mongod.")?;
    verify_signature(&mongod, MONGO_TEAM, deadline, progress, log)?;
    Ok(mongod)
}

fn publish_server(dotnet: &Path, root: &Path, checkout: &Path, stage: &Path, config: &Path, deadline: Instant, progress: Progress, log: Log) -> Result<()> {
    note(progress, log, "Publishing AscNet server")?;
    let server = stage.join("server");
    let mut command = Command::new(dotnet);
    command
        .arg("publish")
        .arg(checkout.join("AscNet").join("AscNet.csproj"))
        .args(["-c", "Release", "-o"])
        .arg(&server)
        .arg("--artifacts-path")
        .arg(stage.join("dotnet-artifacts"))
        .args(["--source", NUGET_INDEX]);
    dotnet_env(&mut command, root);
    run_logged(&mut command, "Publishing AscNet server", deadline, progress, log)?;
    if !server.join("Configs").join("version_config.json").is_file() {
        bail!("Published server is missing Configs/version_config.json.");
    }
    fs::create_dir_all(server.join("Configs"))?;
    fs::copy(config, server.join("Configs").join("config.json"))?;
    Ok(())
}

/// lucia.dll and KRSDK.dll, cross-built for Windows with `cargo xwin`.
fn build_patch(root: &Path, tools: &Path, checkout: &Path, stage: &Path, deadline: Instant, progress: Progress, log: Log) -> Result<()> {
    note(progress, log, "Building client patch")?;
    let target = root.join("cargo-target");
    let mut command = rust_command(&cargo_home(tools).join("bin").join("cargo"), tools);
    command
        .args(["xwin", "build", "--manifest-path"])
        .arg(checkout.join("AscNet.Patch").join("Cargo.toml"))
        .args(["--locked", "--release", "--target", WINDOWS_TARGET, "--target-dir"])
        .arg(&target);
    run_logged(&mut command, "Building client patch", deadline, progress, log)?;
    let release = target.join(WINDOWS_TARGET).join("release");
    let patch = stage.join("patch");
    fs::create_dir_all(&patch)?;
    fs::copy(release.join("lucia.dll"), patch.join("lucia.dll")).context("copy lucia.dll")?;
    // The crate is named krsdk, so current checkouts emit krsdk.dll; the game still loads KRSDK.dll.
    let sdk_built = release.join("krsdk.dll");
    let sdk_built = if sdk_built.is_file() { sdk_built } else { release.join("KRSDK.dll") };
    fs::copy(&sdk_built, patch.join("KRSDK.dll")).context("copy KRSDK.dll")?;
    Ok(())
}

/// version.dll: the C loader compiled by Apple clang (cl-compatible driver) against the xwin sysroot
/// that `build_patch` just populated, linked with the toolchain's rust-lld in link.exe mode.
fn build_version_loader(tools: &Path, checkout: &Path, stage: &Path, launcher_dir: &Path, deadline: Instant, progress: Progress, log: Log) -> Result<()> {
    note(progress, log, "Building version loader")?;
    let sysroot = tools.join("xwin").join("xwin");
    let (crt, sdk) = (sysroot.join("crt"), sysroot.join("sdk"));
    let lld = rustup_home(tools).join("toolchains").join(RUST_TOOLCHAIN).join("lib/rustlib/aarch64-apple-darwin/bin/rust-lld");
    let source = checkout.join("AscNet.Patch").join("VersionShim").join("src");
    let loader = stage.join("loader");
    fs::create_dir_all(&loader)?;
    let object = loader.join("dllmain.obj");
    let mut compile = Command::new(CLANG);
    compile
        .args(["--driver-mode=cl", "--target=x86_64-pc-windows-msvc", "/nologo", "/O1", "/GS-", "/c", "/DNDEBUG", "/DVERSIONSHIM_EXPORTS", "/D_WINDOWS", "/D_USRDLL", "/DPROJECT_NAME=\"VersionShim\""])
        .arg(format!("/Fo{}", object.display()));
    for include in [crt.join("include"), sdk.join("include").join("ucrt"), sdk.join("include").join("um"), sdk.join("include").join("shared")] {
        compile.arg("-imsvc").arg(include);
    }
    compile.arg(source.join("dllmain.c"));
    run_logged(&mut compile, "Compiling version loader", deadline, progress, log)?;
    let mut link = Command::new(&lld);
    link.args(["-flavor", "link", "/DLL", "/NODEFAULTLIB", "/ENTRY:DllMain", "/SUBSYSTEM:WINDOWS"])
        .arg(format!("/OUT:{}", loader.join("VersionShim.dll").display()))
        .arg(&object)
        .arg(format!("/LIBPATH:{}", sdk.join("lib").join("um").join("x86_64").display()))
        .args(["kernel32.lib", "user32.lib"]);
    run_logged(&mut link, "Linking version loader", deadline, progress, log)?;
    let patch = stage.join("patch");
    fs::create_dir_all(&patch)?;
    fs::copy(loader.join("VersionShim.dll"), patch.join("version.dll")).context("copy version.dll")?;
    fs::write(patch.join("libraries.txt"), "*PGR.exe\nlucia.dll\n")?;
    fs::copy(launcher_dir.join("supported-client.json"), patch.join("supported-client.json")).context("copy supported-client.json")?;
    Ok(())
}

extern "C" {
    fn kill(pid: i32, signal: i32) -> i32;
}

/// Everything started by `Runtime` shares one process group, the macOS counterpart of the Windows job:
/// dropping the guard kills whatever is left.
struct Group(i32);

impl Drop for Group {
    fn drop(&mut self) {
        // SAFETY: kill(2) with a negative pid signals the group this launcher created.
        unsafe { kill(-self.0, 9) };
    }
}

struct Owned(Child);

impl Drop for Owned {
    fn drop(&mut self) {
        let _ = self.0.kill();
        let _ = self.0.wait();
    }
}

/// Returns true when the child had to be killed.
fn wait_or_kill(child: &mut Child, timeout: Duration) -> Result<bool> {
    let deadline = Instant::now() + timeout;
    while child.try_wait()?.is_none() && Instant::now() < deadline {
        thread::sleep(Duration::from_millis(100));
    }
    if child.try_wait()?.is_none() {
        let _ = child.kill();
        let _ = child.wait();
        return Ok(true);
    }
    Ok(false)
}

/// The native MongoDB and AscNet server for one game session; what `plan` found already running is not started,
/// stopped or shut down. The caller holds the launcher's operation lock for as long as this lives.
pub struct Runtime {
    server: Option<Owned>,
    mongo: Option<Owned>,
    dotnet: PathBuf,
    server_dll: PathBuf,
    mongo_port: u16,
    stopped: bool,
    group: Option<Group>,
}

impl Runtime {
    pub fn start(root: &Path, build: &LocalBuild, plan: &Plan, progress: Progress) -> Result<Self> {
        fs::create_dir_all(root.join("logs"))?;
        let mut runtime = Runtime {
            server: None,
            mongo: None,
            dotnet: build.dotnet.clone(),
            server_dll: build.server_directory.join("AscNet.dll"),
            mongo_port: build.mongo_port,
            stopped: false,
            group: None,
        };
        if plan.server == ServerPlan::Own {
            let database = match plan.mongo {
                MongoPlan::Reuse(port) => Some(port),
                _ => None,
            };
            install_runtime_config(root, &build.server_directory, build.game_port, build.mongo_port, database)?;
        }
        if plan.mongo == MongoPlan::Own {
            fs::create_dir_all(root.join("data/mongo"))?;
            progress("Starting MongoDB");
            let mongo_log = RotatingLog::open(root.join("logs/mongod.log"), BACKEND_LOG_LIMIT)?;
            let mut command = Command::new(&build.mongod);
            command.args(mongo_arguments(&root.join("data/mongo"), build.mongo_port, false)).stdout(Stdio::piped()).stderr(Stdio::piped());
            let mut mongo = Owned(runtime.spawn(&mut command).context("start MongoDB")?);
            pump_output(&mut mongo.0, &mongo_log);
            runtime.mongo = Some(mongo);
            wait_tcp(&mut runtime.mongo.as_mut().expect("just set").0, build.mongo_port, "MongoDB")?;
        }
        if plan.server == ServerPlan::Own {
            progress("Starting AscNet server");
            let origin = format!("http://127.0.0.1:{}", build.sdk_port);
            let server_log = RotatingLog::open(root.join("logs/server.log"), BACKEND_LOG_LIMIT)?;
            let mut command = Command::new(&build.dotnet);
            command
                .arg(build.server_directory.join("AscNet.dll"))
                .args(["--urls", &origin])
                .current_dir(&build.resource_directory)
                .env("ASCNET_GATE_FALLBACK_USERNAME", "")
                .env("ASCNET_PUBLIC_HTTP_ORIGIN", &origin)
                .env("ASCNET_GAME_BIND_ADDRESS", "127.0.0.1")
                .env("ASCNET_MANAGED_STDIN", "1")
                .stdin(Stdio::piped())
                .stdout(Stdio::piped())
                .stderr(Stdio::piped());
            let mut server = Owned(runtime.spawn(&mut command).context("start AscNet server")?);
            pump_output(&mut server.0, &server_log);
            runtime.server = Some(server);
            wait_server(&mut runtime.server.as_mut().expect("just set").0, &origin, build.game_port)?;
        }
        progress("Local backend is ready");
        Ok(runtime)
    }

    /// Starts `command` in the runtime's process group (the first child creates it).
    fn spawn(&mut self, command: &mut Command) -> Result<Child> {
        let child = command.process_group(self.group.as_ref().map_or(0, |g| g.0)).spawn()?;
        if self.group.is_none() {
            self.group = Some(Group(child.id() as i32));
        }
        Ok(child)
    }

    /// Server first (`shutdown` on its managed stdin), then MongoDB through the server's own helper,
    /// the same order as Windows; only what this runtime started. Anything still alive afterwards is killed with
    /// the process group.
    pub fn stop(&mut self) -> Result<()> {
        if self.stopped {
            return Ok(());
        }
        let mut server_request = Ok(());
        let mut server_forced = Ok(false);
        if let Some(server) = &mut self.server {
            server_request = server
                .0
                .stdin
                .as_mut()
                .context("AscNet server stdin is unavailable")
                .and_then(|stdin| stdin.write_all(b"shutdown\n").and_then(|()| stdin.flush()).context("request AscNet server shutdown"));
            server_forced = wait_or_kill(&mut server.0, STOP_TIMEOUT);
        }
        let (mut helper_forced, mut mongo_forced) = (Ok(false), Ok(false));
        if let (Some(mongo), Some(group)) = (&mut self.mongo, &self.group) {
            helper_forced = (|| -> Result<bool> {
                let mut helper = Owned(
                    Command::new(&self.dotnet)
                        .arg(&self.server_dll)
                        .args(["--shutdown-local-mongo", &self.mongo_port.to_string()])
                        .current_dir(self.server_dll.parent().context("AscNet server DLL has no directory")?)
                        .stdout(Stdio::null())
                        .stderr(Stdio::null())
                        .process_group(group.0)
                        .spawn()
                        .context("start owned MongoDB shutdown helper")?,
                );
                wait_or_kill(&mut helper.0, STOP_TIMEOUT)
            })();
            mongo_forced = wait_or_kill(&mut mongo.0, STOP_TIMEOUT);
        }
        self.stopped = true;
        if let Some(group) = &self.group {
            // SAFETY: see `Group::drop`; sweeps stragglers such as dotnet child processes.
            unsafe { kill(-group.0, 9) };
        }
        if server_forced? || helper_forced? || mongo_forced? {
            bail!("local runtime did not stop gracefully; forced termination was required");
        }
        server_request
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::{env, net::TcpStream};

    /// Everything found running is left alone: nothing is started, and stopping the runtime touches no foreign process.
    #[test]
    fn a_runtime_that_reuses_everything_starts_and_stops_nothing() {
        let root = env::temp_dir().join(format!("ascnet-mac-reuse-{}", uuid::Uuid::new_v4()));
        let (sdk, game) = (std::net::TcpListener::bind(("127.0.0.1", 0)).unwrap(), std::net::TcpListener::bind(("127.0.0.1", 0)).unwrap());
        let (sdk_port, game_port) = (sdk.local_addr().unwrap().port(), game.local_addr().unwrap().port());
        let missing = root.join("missing");
        let build = LocalBuild {
            schema_version: 1,
            revision: "x".into(),
            repository: "x".into(),
            dotnet: missing.clone(),
            mongod: missing.clone(),
            server_directory: missing.clone(),
            resource_directory: missing.clone(),
            patch_directory: missing,
            sdk_port: 1,
            game_port: 2,
            mongo_port: 3,
        };
        let plan = Plan { server: ServerPlan::Reuse { sdk: sdk_port, game: game_port }, mongo: MongoPlan::NotNeeded };
        let mut messages = Vec::new();
        let mut runtime = Runtime::start(&root, &build, &plan, &mut |m| messages.push(m.to_owned())).unwrap();
        assert!(runtime.server.is_none() && runtime.mongo.is_none() && runtime.group.is_none());
        assert_eq!(messages, ["Local backend is ready"], "no \"Starting …\" step for anything that already runs");
        runtime.stop().unwrap();
        assert!(TcpStream::connect(("127.0.0.1", sdk_port)).is_ok() && TcpStream::connect(("127.0.0.1", game_port)).is_ok());
        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn dotnet_selector_takes_the_osx_arm64_tarball() {
        let hash = "a".repeat(128);
        let document = format!(
            r#"{{"releases":[{{"sdk":{{"files":[
                {{"name":"dotnet-sdk-osx-arm64.pkg","url":"https://x/pkg","hash":"{hash}"}},
                {{"name":"dotnet-sdk-osx-arm64.tar.gz","url":"https://x/sdk.tar.gz","hash":"{hash}"}}]}}}}]}}"#
        );
        assert_eq!(select_dotnet_sdk(&document).unwrap(), ("https://x/sdk.tar.gz".into(), hash));
        assert!(select_dotnet_sdk(r#"{"releases":[]}"#).is_err());
    }

    #[test]
    fn mongo_selector_prefers_newest_stable_arm64_community_tarball() {
        let sha = "b".repeat(64);
        let entry = |version: &str, arch: &str, edition: &str| {
            format!(
                r#"{{"version":"{version}","downloads":[{{"target":"macos","arch":"{arch}","edition":"{edition}","archive":{{"url":"https://x/{version}-{arch}-{edition}.tgz","sha256":"{sha}"}}}}]}}"#
            )
        };
        let document = format!(
            r#"{{"versions":[{},{},{},{},{}]}}"#,
            entry("8.0.9", "arm64", "base"),
            entry("8.0.10", "x86_64", "base"),
            entry("8.0.10", "arm64", "enterprise"),
            entry("8.0.10", "arm64", "base"),
            entry("8.0.11-rc0", "arm64", "base")
        );
        let (version, url, hash) = select_mongo_archive(&document).unwrap();
        assert_eq!((version.as_str(), url.as_str(), hash), ("8.0.10", "https://x/8.0.10-arm64-base.tgz", Some(sha)));
    }

    #[test]
    fn tar_extraction_rejects_escaping_entries_and_installs_atomically() {
        let dir = std::env::temp_dir().join(format!("ascnet-mac-setup-{}", uuid::Uuid::new_v4()));
        fs::create_dir_all(&dir).unwrap();
        let archive = dir.join("a.tar.gz");
        let mut tar = tar::Builder::new(flate2::write::GzEncoder::new(fs::File::create(&archive).unwrap(), flate2::Compression::fast()));
        let mut header = tar::Header::new_gnu();
        header.set_size(2);
        header.set_mode(0o755);
        header.set_cksum();
        tar.append_data(&mut header, "top/bin/tool", &b"ok"[..]).unwrap();
        tar.into_inner().unwrap().finish().unwrap();
        let destination = dir.join("out");
        extract_tar_gz(&archive, &destination).unwrap();
        assert_eq!(fs::read(destination.join("top/bin/tool")).unwrap(), b"ok");
        assert!(!dir.join("out.partial").exists());
        fs::remove_dir_all(dir).unwrap();
    }

    /// Acceptance run: `ASCNET_MAC_SETUP_ROOT=/tmp/x cargo test --lib mac::setup::tests::full_setup -- --ignored --nocapture`.
    /// Clones the enclosing checkout (or `ASCNET_MAC_SETUP_REPOSITORY`) into the root, installs the toolchain, builds, starts a private
    /// mongod and server on free ports, queries the server, and stops it.
    #[test]
    #[ignore]
    fn full_setup_in_a_throwaway_root() {
        let root = PathBuf::from(env::var("ASCNET_MAC_SETUP_ROOT").expect("ASCNET_MAC_SETUP_ROOT"));
        let launcher = PathBuf::from(env!("CARGO_MANIFEST_DIR"));
        let repository = env::var("ASCNET_MAC_SETUP_REPOSITORY").unwrap_or_else(|_| launcher.parent().unwrap().to_str().unwrap().to_owned());
        let repository = repository.as_str();
        let branch = String::from_utf8(Command::new("git").args(["-C", repository, "branch", "--show-current"]).output().unwrap().stdout).unwrap();
        let started = Instant::now();
        let mut log = |line: &str| -> Result<()> {
            eprintln!("[{:>5.0}s] {line}", started.elapsed().as_secs_f32());
            Ok(())
        };
        run(&root, repository, branch.trim(), &launcher, &mut |_| {}, &mut log, started + Duration::from_secs(3600)).unwrap();
        eprintln!("setup took {:?}", started.elapsed());
        let build: LocalBuild = serde_json::from_slice(&fs::read(root.join("build-state.pending.json")).unwrap()).unwrap();
        crate::package::load_package(&build.patch_directory).unwrap();
        let begun = Instant::now();
        let mut runtime = Runtime::start(&root, &build, &Plan::OWN, &mut |message| eprintln!("{message}")).unwrap();
        eprintln!("start took {:?}", begun.elapsed());
        let body = reqwest::blocking::get(format!("http://127.0.0.1:{}/api/launcher/status", build.sdk_port)).unwrap().text().unwrap();
        eprintln!("status: {body}");
        let begun = Instant::now();
        runtime.stop().unwrap();
        eprintln!("stop took {:?}", begun.elapsed());
        assert!(TcpStream::connect(("127.0.0.1", build.mongo_port)).is_err());
        assert!(TcpStream::connect(("127.0.0.1", build.sdk_port)).is_err());
    }
}

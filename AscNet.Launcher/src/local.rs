use crate::gm_control::{GmControl, GmRequest};
use anyhow::{bail, Context, Result};
use reqwest::Url;
use serde::{Deserialize, Serialize};
use std::{
    env, fs,
    io::{Read, Write},
    net::{IpAddr, Ipv4Addr, SocketAddr, TcpListener, TcpStream},
    path::{Path, PathBuf},
    process::{Child, Command, Stdio},
    sync::{
        atomic::{AtomicBool, Ordering},
        Arc, Mutex,
    },
    thread,
    time::{Duration, Instant, SystemTime, UNIX_EPOCH},
};
#[cfg(test)]
use std::{
    io::{BufRead, BufReader},
    sync::mpsc,
};

pub(crate) const SCHEMA_VERSION: u32 = 1;
const START_TIMEOUT: Duration = Duration::from_secs(45);
const SETUP_TIMEOUT: Duration = Duration::from_secs(60 * 60);
const STOP_TIMEOUT: Duration = Duration::from_secs(10);
static OPERATION: AtomicBool = AtomicBool::new(false);
static LOG_WRITE: Mutex<()> = Mutex::new(());
/// A backend log past this size becomes `<name>.previous.log` and a new file starts.
const BACKEND_LOG_LIMIT: u64 = 32 * 1024 * 1024;

struct LauncherLog {
    path: PathBuf,
    file: fs::File,
}

impl LauncherLog {
    fn open(path: PathBuf) -> Result<Self> {
        let file = fs::OpenOptions::new()
            .create(true)
            .append(true)
            .open(&path)
            .with_context(|| format!("open launcher log {}", path.display()))?;
        Ok(Self { path, file })
    }

    fn beside_executable() -> Result<Self> {
        Self::open(
            env::current_exe()?
                .parent()
                .context("launcher executable has no parent directory")?
                .join("launcher.log"),
        )
    }

    fn write(&mut self, message: &str) -> Result<()> {
        let _guard = LOG_WRITE.lock().unwrap_or_else(|e| e.into_inner());
        let timestamp = SystemTime::now().duration_since(UNIX_EPOCH)?.as_millis();
        writeln!(self.file, "[{timestamp} unix-ms] {message}")
            .and_then(|_| self.file.flush())
            .with_context(|| format!("write launcher log {}", self.path.display()))
    }

    fn finish<T>(&mut self, result: Result<T>) -> Result<T> {
        let message = match &result {
            Ok(_) => "Setup attempt completed".to_owned(),
            Err(error) => format!("Setup attempt failed: {error:#}"),
        };
        let logged = self.write(&message).and_then(|_| {
            self.file.sync_data()
                .with_context(|| format!("flush launcher log {}", self.path.display()))
        });
        match (result, logged) {
            (Err(error), Err(log_error)) => Err(error.context(format!("{log_error:#}"))),
            (result, Ok(())) => result,
            (Ok(_), Err(error)) => Err(error),
        }
    }
}

pub fn launcher_log(message: &str) -> Result<()> {
    LauncherLog::beside_executable()?.write(message)
}

pub fn logged_error(message: &str) -> String {
    match launcher_log(&format!("Launcher error: {message}")) {
        Ok(()) => message.to_owned(),
        Err(error) => format!("{message}\n\nCould not save launcher diagnostics: {error:#}"),
    }
}

/// Logs the full error chain; returns a one-line summary for the activity log.
pub fn summarized_error(action: &str, error: &anyhow::Error) -> String {
    let _ = launcher_log(&format!("Launcher error: Couldn't {action}: {error:#}"));
    summarize_error(action, error)
}

fn summarize_error(action: &str, error: &anyhow::Error) -> String {
    let chain = format!("{error:#}").to_ascii_lowercase();
    // ponytail: substring classifier over reqwest/io/git text; downcast if messages drift.
    const OFFLINE: [&str; 11] = [
        "dns error",
        "host not found",
        "no such host",
        "failed to lookup",
        "could not resolve host",
        "tunnel error",
        "timed out",
        "connection refused",
        "connection reset",
        "(connect)",
        "unable to access",
    ];
    if OFFLINE.iter().any(|marker| chain.contains(marker)) {
        return format!("Couldn't {action} — offline or GitHub unreachable");
    }
    let top = error.to_string();
    let cause = if top.contains("url (") { error.root_cause().to_string() } else { top };
    format!("Couldn't {action}: {}", cause.lines().next().unwrap_or_default())
}

#[cfg(test)]
mod summary_tests {
    use super::{mongo_arguments, summarize_error};
    use anyhow::anyhow;
    use std::path::Path;

    #[test]
    fn network_failures_collapse_to_offline() {
        let error = anyhow!("dns error: Host not found. (os error 11001)")
            .context("client error (Connect): tunnel error")
            .context("error sending request for url (https://api.github.com/x)");
        assert_eq!(
            summarize_error("check for launcher updates", &error),
            "Couldn't check for launcher updates — offline or GitHub unreachable"
        );
    }

    #[test]
    fn other_failures_keep_first_line_without_url_chain() {
        let error = anyhow!("HTTP status 404").context("error sending request for url (https://x/y)");
        assert_eq!(summarize_error("do it", &error), "Couldn't do it: HTTP status 404");
        let error = anyhow!("disk full").context("Staged update is invalid\nsecond line");
        assert_eq!(summarize_error("do it", &error), "Couldn't do it: Staged update is invalid");
    }

    #[test]
    fn wine_mongod_disables_diagnostic_collection() {
        let owned = mongo_arguments(Path::new(r"C:\data\mongo"), 27017, false);
        let plain: Vec<_> = owned.iter().map(String::as_str).collect();
        assert_eq!(plain, ["--bind_ip", "127.0.0.1", "--dbpath", r"C:\data\mongo", "--port", "27017"]);
        let wine = mongo_arguments(Path::new(r"C:\data\mongo"), 27017, true);
        assert_eq!(wine[wine.len() - 2], "--setParameter");
        assert_eq!(wine.last().map(String::as_str), Some("diagnosticDataCollectionEnabled=false"));
    }

    #[test]
    fn services_stop_only_when_the_game_exits() {
        assert!(super::game_exit_stops_services(true, false));
        assert!(!super::game_exit_stops_services(false, false));
        assert!(!super::game_exit_stops_services(false, true));
        assert!(!super::game_exit_stops_services(true, true));
    }

    #[test]
    fn close_waits_for_an_in_flight_stop() {
        use super::CloseRequest;
        assert_eq!(super::close_request(true, false, true), CloseRequest::Wait);
        assert_eq!(super::close_request(true, true, false), CloseRequest::Wait);
        assert_eq!(super::close_request(false, true, false), CloseRequest::AfterStop);
        assert_eq!(super::close_request(false, true, true), CloseRequest::AfterStop);
        assert_eq!(super::close_request(false, false, true), CloseRequest::StopServices);
        assert_eq!(super::close_request(false, false, false), CloseRequest::Close);
    }
}
#[derive(Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct LocalBuild {
    pub schema_version: u32,
    pub revision: String,
    pub repository: String,
    pub dotnet: PathBuf,
    pub mongod: PathBuf,
    pub server_directory: PathBuf,
    pub resource_directory: PathBuf,
    pub patch_directory: PathBuf,
    pub sdk_port: u16,
    pub game_port: u16,
    pub mongo_port: u16,
}

pub fn root() -> Result<PathBuf> {
    let base = env::var_os("LOCALAPPDATA").context("LOCALAPPDATA is not set")?;
    let base = PathBuf::from(base);
    if !base.is_absolute() {
        bail!("LOCALAPPDATA must be an absolute path");
    }
    Ok(base.join("AscNetLauncher").join("local"))
}

pub fn load_build() -> Result<Option<LocalBuild>> {
    let root = root()?;
    let state = root.join("build-state.json");
    let bytes = match fs::read(&state) {
        Ok(bytes) => bytes,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(None),
        Err(error) => return Err(error).with_context(|| format!("read {}", state.display())),
    };
    let build: LocalBuild = serde_json::from_slice(&bytes).context("invalid local build state")?;
    validate_build(&root, &build)?;
    crate::package::refresh_supported_client(
        &build.patch_directory,
        &env::current_exe()?
            .parent()
            .context("launcher executable has no parent directory")?
            .join("supported-client.json"),
    )?;
    Ok(Some(build))
}

pub fn prepare(
    repository: &str,
    branch: &str,
    game: &Path,
    progress: &mut dyn FnMut(&str),
) -> Result<LocalBuild> {
    let mut log = LauncherLog::beside_executable()?;
    log.write("Setup attempt started")?;
    let result = prepare_logged(repository, branch, game, progress, &mut log);
    log.finish(result)
}

#[cfg_attr(not(windows), allow(unreachable_code, unused_variables))]
fn prepare_logged(
    repository: &str,
    branch: &str,
    game: &Path,
    progress: &mut dyn FnMut(&str),
    log: &mut LauncherLog,
) -> Result<LocalBuild> {
    validate_repository(repository)?;
    validate_branch(branch)?;
    #[cfg(not(windows))]
    bail!("local source setup is supported on Windows only");
    let _operation = operation_lock()?;
    let root = root()?;
    fs::create_dir_all(&root).with_context(|| format!("create {}", root.display()))?;
    #[cfg(windows)]
    let _setup_lock = open_setup_lock(&root)?;
    let launcher_dir = env::current_exe()?
        .parent()
        .context("launcher executable has no parent directory")?
        .to_path_buf();
    let supported = launcher_dir.join("supported-client.json");
    if !supported.is_file() {
        bail!("supported client manifest is missing: {}", supported.display());
    }
    // Refuse before setup installs dependencies for a client we cannot patch.
    crate::package::check_supported_client(&supported, game)?;

    progress("Starting local source setup");
    crate::setup::run(
        &root,
        repository,
        branch,
        &launcher_dir,
        progress,
        &mut |line| log.write(line),
        Instant::now() + SETUP_TIMEOUT,
    )?;
    let pending = root.join("build-state.pending.json");
    let bytes = fs::read(&pending).with_context(|| format!("read {}", pending.display()))?;
    let build: LocalBuild =
        serde_json::from_slice(&bytes).context("invalid pending local build state")?;
    validate_build(&root, &build)?;
    crate::package::refresh_supported_client(
        &build.patch_directory,
        &launcher_dir.join("supported-client.json"),
    )?;
    let package = crate::package::load_package(&build.patch_directory)
        .context("validate prepared patch package")?;
    fs::OpenOptions::new()
        .write(true)
        .open(&pending)?
        .sync_all()
        .context("flush pending local build state")?;
    crate::install::install_with_consent(game, &package, &mut |message| progress(&message))?;
    anyhow::ensure!(
        matches!(crate::install::inspect(game, &package)?, crate::install::PatchState::Current),
        "prepared patch did not become current"
    );
    #[cfg(windows)]
    atomic_replace(&pending, &root.join("build-state.json"))?;
    Ok(build)
}

#[cfg(test)]
fn capture_setup(
    child: &mut Child,
    log: &mut LauncherLog,
    progress: &mut dyn FnMut(&str),
    timeout: Duration,
) -> Result<()> {
    let (send, receive) = mpsc::channel();
    stream_lines(
        child.stdout.take().context("capture local setup output")?,
        send.clone(),
    );
    stream_lines(
        child.stderr.take().context("capture local setup errors")?,
        send.clone(),
    );
    drop(send);
    let deadline = Instant::now() + timeout;
    loop {
        if Instant::now() >= deadline {
            terminate_child(child);
            bail!("local setup timed out after {} seconds", timeout.as_secs());
        }
        match receive.recv_timeout(Duration::from_millis(250)) {
            Ok(line) => {
                let line = line.context("read local setup output")?;
                log.write(&line)?;
                progress(&line);
            }
            Err(mpsc::RecvTimeoutError::Disconnected) => break,
            Err(mpsc::RecvTimeoutError::Timeout) => {}
        }
    }
    let status = loop {
        if let Some(status) = child.try_wait().context("wait for local setup")? {
            break status;
        }
        if Instant::now() >= deadline {
            terminate_child(child);
            bail!("local setup timed out after {} seconds", timeout.as_secs());
        }
        thread::sleep(Duration::from_millis(100));
    };
    log.write(&format!("Setup process exited with {status}"))?;
    if !status.success() {
        bail!("local setup failed with {status}; see {}", log.path.display());
    }
    Ok(())
}

#[cfg(test)]
mod log_tests {
    use super::*;

    #[test]
    fn failed_setup_retains_both_streams_across_attempts() {
        let directory = env::temp_dir().join(format!("ascnet-log-{}", uuid::Uuid::new_v4()));
        fs::create_dir(&directory).unwrap();
        let path = directory.join("launcher.log");
        for _ in 0..2 {
            let mut log = LauncherLog::open(path.clone()).unwrap();
            log.write("Setup attempt started").unwrap();
            #[cfg(windows)]
            let mut command = {
                let mut command = Command::new("cmd.exe");
                command.args(["/D", "/C", "echo compiler-output & echo dependency-failure 1>&2 & exit /b 7"]);
                command
            };
            #[cfg(not(windows))]
            let mut command = {
                let mut command = Command::new("sh");
                command.args(["-c", "printf 'compiler-output\\n'; printf 'dependency-failure\\n' >&2; exit 7"]);
                command
            };
            let mut child = OwnedChild(command.stdout(Stdio::piped()).stderr(Stdio::piped()).spawn().unwrap());
            // The visible UI may discard every detailed line.
            let result = capture_setup(&mut child.0, &mut log, &mut |_| {}, Duration::from_secs(10));
            let error = log.finish(result).unwrap_err();
            assert!(format!("{error:#}").contains("local setup failed"));
        }
        let contents = fs::read_to_string(&path).unwrap();
        assert_eq!(contents.matches("compiler-output").count(), 2);
        assert_eq!(contents.matches("dependency-failure").count(), 2);
        assert_eq!(contents.matches("Setup attempt started").count(), 2);
        assert_eq!(contents.matches("Setup process exited with").count(), 2);
        assert_eq!(contents.matches("Setup attempt failed: local setup failed").count(), 2);
        fs::remove_dir_all(directory).unwrap();
    }

    #[test]
    fn backend_log_keeps_the_newest_output_in_at_most_two_files() {
        let directory = env::temp_dir().join(format!("ascnet-backend-log-{}", uuid::Uuid::new_v4()));
        fs::create_dir(&directory).unwrap();
        let path = directory.join("server.log");
        let previous = directory.join("server.previous.log");
        fs::write(&path, "last-run\n").unwrap();
        let log = RotatingLog::open(path.clone(), 4096).unwrap();
        assert_eq!(fs::read_to_string(&previous).unwrap(), "last-run\n", "a start keeps the last run");
        // ~10 KB of output against a 4 KB limit rotates at least twice during the run.
        #[cfg(windows)]
        let mut command = {
            let mut command = Command::new("cmd.exe");
            command.args(["/D", "/C", "for /L %i in (1,1,1000) do @echo line-%i"]);
            command
        };
        #[cfg(not(windows))]
        let mut command = {
            let mut command = Command::new("sh");
            command.args(["-c", "i=1; while [ $i -le 1000 ]; do echo line-$i; i=$((i+1)); done"]);
            command
        };
        let mut child = OwnedChild(command.stdout(Stdio::piped()).stderr(Stdio::piped()).spawn().unwrap());
        pump_output(&mut child.0, &log);
        child.0.wait().unwrap();
        // The pump may be between rename and create, so a missing file just means "not yet".
        let deadline = Instant::now() + Duration::from_secs(5);
        while !fs::read_to_string(&path).unwrap_or_default().contains("line-1000") && Instant::now() < deadline {
            thread::sleep(Duration::from_millis(20));
        }
        let current = fs::read_to_string(&path).unwrap();
        let older = fs::read_to_string(&previous).unwrap();
        assert!(current.contains("line-1000"), "newest output is kept");
        assert!(!older.contains("last-run") && !older.contains("line-1\n"), "the size limit rotated during the run");
        assert!(current.len() <= 4096 && older.len() <= 4096);
        assert_eq!(fs::read_dir(&directory).unwrap().count(), 2);
        fs::remove_dir_all(directory).unwrap();
    }
}

pub fn check_update(
    repository: &str,
    branch: &str,
    installed: Option<&LocalBuild>,
) -> Result<Option<bool>> {
    validate_repository(repository)?;
    validate_branch(branch)?;
    let Some(installed) = installed else {
        return Ok(None);
    };
    if installed.repository != repository {
        bail!("active build belongs to a different repository; run Setup manually");
    }
    let checkout = root()?.join("checkout");
    if !checkout.join(".git").is_dir() {
        return Ok(None);
    }
    let Some(git) = git_executable() else {
        return Ok(None);
    };
    check_checkout_update(&git, &checkout, repository, branch, &installed.revision)
}

fn check_checkout_update(
    git: &Path,
    checkout: &Path,
    repository: &str,
    branch: &str,
    installed_revision: &str,
) -> Result<Option<bool>> {
    for (args, expected) in [
        (["remote", "get-url", "origin"].as_slice(), repository),
        (["branch", "--show-current"].as_slice(), branch),
    ] {
        let mut command = Command::new(&git);
        command.arg("-C").arg(&checkout).args(args);
        let output = command_output_timeout(command, Duration::from_secs(15))?;
        if !output.status.success()
            || text_output(&output.stdout, "checkout identity")? != expected
        {
            bail!("checkout repository or branch differs; run Setup manually");
        }
    }
    let mut remote_command = Command::new(&git);
    let remote_ref = format!("refs/heads/{branch}");
    remote_command
        .env("GIT_TERMINAL_PROMPT", "0")
        .env("GCM_INTERACTIVE", "Never")
        .args([
            "-c",
            "credential.interactive=false",
            "ls-remote",
            "--exit-code",
            repository,
            &remote_ref,
        ]);
    let output = command_output_timeout(remote_command, Duration::from_secs(30))
        .context("check remote source revision")?;
    if !output.status.success() {
        bail!("git could not find remote branch {branch}");
    }
    let mut fields = text_output(&output.stdout, "remote git revision")?.split_whitespace();
    let remote = fields.next().context("git returned no remote revision")?;
    if fields.next() != Some(remote_ref.as_str()) || fields.next().is_some() {
        bail!("git returned an unexpected remote branch");
    }
    if remote.len() != 40 || !remote.bytes().all(|b| b.is_ascii_hexdigit()) {
        bail!("git returned an invalid remote revision");
    }
    Ok(Some(!installed_revision.eq_ignore_ascii_case(remote)))
}

#[cfg(test)]
mod update_tests {
    use super::*;

    #[test]
    fn inherited_handle_snapshot_keeps_only_this_process() {
        let mut snapshot = vec![0u8; 16 + 40 * 3];
        snapshot[0..8].copy_from_slice(&3usize.to_ne_bytes());
        let mut write = |index: usize, pid: usize, handle: usize, attributes: u32| {
            let start = 16 + index * 40;
            snapshot[start + 8..start + 16].copy_from_slice(&pid.to_ne_bytes());
            snapshot[start + 16..start + 24].copy_from_slice(&handle.to_ne_bytes());
            snapshot[start + 32..start + 36].copy_from_slice(&attributes.to_ne_bytes());
        };
        write(0, 42, 0x100, 0x2);
        write(1, 42, 0x200, 0);
        write(2, 7, 0x300, 0x2);
        assert_eq!(inherited_handle_values(&snapshot, 42), vec![0x100]);
        assert!(inherited_handle_values(&snapshot[..10], 42).is_empty());
    }

    #[test]
    fn advanced_checkout_does_not_hide_failed_build() {
        let directory = env::temp_dir().join(format!("ascnet-source-{}", uuid::Uuid::new_v4()));
        fs::create_dir(&directory).unwrap();
        let git = |args: &[&str]| {
            let output = Command::new("git").arg("-C").arg(&directory).args(args).output().unwrap();
            assert!(output.status.success(), "{}", String::from_utf8_lossy(&output.stderr));
            String::from_utf8(output.stdout).unwrap().trim().to_owned()
        };
        git(&["init", "--initial-branch=master"]);
        git(&["-c", "commit.gpgsign=false", "-c", "user.name=Launcher Test", "-c", "user.email=launcher@example.invalid",
            "commit", "--allow-empty", "-m", "active build"]);
        let active_revision = git(&["rev-parse", "HEAD"]);
        git(&["-c", "commit.gpgsign=false", "-c", "user.name=Launcher Test", "-c", "user.email=launcher@example.invalid",
            "commit", "--allow-empty", "-m", "checkout advanced before failed build"]);
        let remote_revision = git(&["rev-parse", "HEAD"]);
        let repository = directory.to_str().unwrap();
        git(&["remote", "add", "origin", repository]);

        assert_eq!(
            check_checkout_update(Path::new("git"), &directory, repository, "master", &active_revision).unwrap(),
            Some(true),
            "checkout and remote match, but active build still needs updating",
        );
        assert_eq!(
            check_checkout_update(Path::new("git"), &directory, repository, "master", &remote_revision).unwrap(),
            Some(false),
        );
        fs::remove_dir_all(directory).unwrap();
    }
}

pub struct LocalRuntime {
    server: OwnedChild,
    control: GmControl,
    gm_version: Option<String>,
    mongo: OwnedChild,
    dotnet: PathBuf,
    server_dll: PathBuf,
    mongo_port: u16,
    stopped: bool,
    #[cfg(windows)]
    job: JobHandle,
    _operation: Option<OperationPermit>,
}

impl LocalRuntime {
    pub fn start(build: &LocalBuild, progress: &mut dyn FnMut(&str)) -> Result<Self> {
        Self::start_with_bundle(build, false, progress)
    }

    pub fn start_with_bundle(
        build: &LocalBuild,
        bundled_server: bool,
        progress: &mut dyn FnMut(&str),
    ) -> Result<Self> {
        #[cfg(not(windows))]
        {
            let _ = (build, bundled_server, progress);
            bail!("the local runtime is supported on Windows only");
        }
        #[cfg(windows)]
        {
            use std::os::windows::process::CommandExt;
            use windows::Win32::System::Threading::CREATE_NO_WINDOW;

            let operation = operation_lock()?;
            let root = root()?;
            validate_build(&root, build)?;
            let (server_directory, resource_directory) = if bundled_server {
                let exe = env::current_exe()?;
                let server = exe.parent().context("launcher executable has no parent directory")?.join("server");
                anyhow::ensure!(server.join("AscNet.dll").is_file() && server.join("Configs/version_config.json").is_file(),
                    "The bundled server is missing beside the launcher. Extract the complete launcher bundle.");
                (server.clone(), server)
            } else {
                (build.server_directory.clone(), build.resource_directory.clone())
            };
            let server_dll = server_directory.join("AscNet.dll");
            crate::setup::install_runtime_config(&root, &server_directory, build.game_port, build.mongo_port)?;
            let _ports = reserve_ports([build.mongo_port, build.sdk_port, build.game_port])?;
            fs::create_dir_all(root.join("data/mongo"))?;
            fs::create_dir_all(root.join("logs"))?;
            let job = create_job()?;

            progress("Starting MongoDB");
            drop(_ports);
            let mongo_log = RotatingLog::open(root.join("logs/mongod.log"), BACKEND_LOG_LIMIT)?;
            let mongo_args = mongo_arguments(
                &root.join("data/mongo"),
                build.mongo_port,
                crate::install::running_under_wine(),
            );
            let mut mongo = OwnedChild(
                Command::new(&build.mongod)
                    .args(&mongo_args)
                    .stdout(Stdio::piped())
                    .stderr(Stdio::piped())
                    .creation_flags(CREATE_NO_WINDOW.0)
                    .spawn()
                    .context("start MongoDB")?,
            );
            pump_output(&mut mongo.0, &mongo_log);
            assign_to_job(&job, &mongo.0)?;
            wait_tcp(&mut mongo.0, build.mongo_port, "MongoDB")?;

            progress("Starting AscNet server");
            let origin = format!("http://127.0.0.1:{}", build.sdk_port);
            let server_log = RotatingLog::open(root.join("logs/server.log"), BACKEND_LOG_LIMIT)?;
            let mut server = OwnedChild(spawn_hidden(
                Command::new(&build.dotnet)
                    .arg(&server_dll)
                    .args(["--urls", &origin])
                    .current_dir(&resource_directory)
                    .env("ASCNET_GATE_FALLBACK_USERNAME", "")
                    .env("ASCNET_PUBLIC_HTTP_ORIGIN", &origin)
                    .env("ASCNET_GAME_BIND_ADDRESS", "127.0.0.1")
                    .env("ASCNET_MANAGED_STDIN", "1")
                    .env("ASCNET_MANAGED_GM", "1")
                    .stdin(Stdio::piped())
                    .stdout(Stdio::piped())
                    .stderr(Stdio::piped()),
            ).context("start AscNet server")?);
            let log = server_log.clone();
            let control = GmControl::attach(
                server.0.stdin.take().context("AscNet server stdin is unavailable")?,
                server.0.stdout.take().context("AscNet server stdout is unavailable")?,
                move |line| if let Ok(mut log) = log.lock() { let _ = log.write(line); },
            );
            pump_output(&mut server.0, &server_log);
            assign_to_job(&job, &server.0)?;
            wait_server(&mut server.0, &origin, build.game_port)?;
            let hello_timeout = if bundled_server { Duration::from_secs(5) } else { Duration::from_millis(1500) };
            let gm_version = control
                .request_with_timeout(GmRequest::Hello, hello_timeout)
                .ok()
                .and_then(|reply| reply.server_version);
            let _ = launcher_log(&format!("Started AscNet host {} DLL {} GM protocol {}",
                build.dotnet.display(), server_dll.display(), gm_version.as_deref().unwrap_or("unavailable")));
            anyhow::ensure!(!bundled_server || gm_version.is_some(), "The bundled server did not advertise launcher GM control.");
            progress("Local backend is ready");
            Ok(Self {
                server,
                control,
                gm_version,
                mongo,
                dotnet: build.dotnet.clone(),
                server_dll,
                mongo_port: build.mongo_port,
                stopped: false,
                job,
                _operation: Some(operation),
            })
        }
    }

    pub fn gm_control(&self) -> Result<GmControl> {
        if self.gm_version.is_none() {
            bail!("This server build does not support GM Tools. Start the matching launcher and server bundle.");
        }
        Ok(self.control.clone())
    }

    pub fn stop(&mut self) -> Result<()> {
        #[cfg(windows)]
        {
            use std::os::windows::process::CommandExt;
            use windows::Win32::System::Threading::CREATE_NO_WINDOW;
            if self.stopped {
                return Ok(());
            }
            let server_request = self.control.shutdown();
            let server_result = wait_or_kill(&mut self.server.0, STOP_TIMEOUT);

            let helper_result = (|| -> Result<bool> {
                let mut shutdown = OwnedChild(
                    Command::new(&self.dotnet)
                        .arg(&self.server_dll)
                        .args(["--shutdown-local-mongo", &self.mongo_port.to_string()])
                        .current_dir(
                            self.server_dll
                                .parent()
                                .context("AscNet server DLL has no directory")?,
                        )
                        .stdout(Stdio::null())
                        .stderr(Stdio::null())
                        .creation_flags(CREATE_NO_WINDOW.0)
                        .spawn()
                        .context("start owned MongoDB shutdown helper")?,
                );
                assign_to_job(&self.job, &shutdown.0)?;
                wait_or_kill(&mut shutdown.0, STOP_TIMEOUT)
            })();
            let mongo_result = wait_or_kill(&mut self.mongo.0, STOP_TIMEOUT);
            self.stopped = true;
            self._operation.take();
            let server_forced = server_result?;
            let helper_forced = helper_result?;
            let mongo_forced = mongo_result?;
            if server_forced || helper_forced || mongo_forced {
                bail!("local runtime did not stop gracefully; forced termination was required");
            }
            server_request?;
        }
        Ok(())
    }
}

/// The local server and MongoDB are for the game session. Stop them when
/// PGR.exe goes from running to exited, not while it is still open and not on
/// a poll that never saw it running.
pub fn game_exit_stops_services(was_running: bool, is_running: bool) -> bool {
    was_running && !is_running
}

/// What closing the launcher should do with the local server and MongoDB.
/// A stop that is already running must finish before the window is destroyed:
/// the runtime owns a job that kills those processes when the process exits.
#[derive(Debug, PartialEq, Eq)]
pub enum CloseRequest {
    /// Setup, play, or another operation still holds the launcher.
    Wait,
    /// Shutdown is in progress. Destroy the window only after it finishes.
    AfterStop,
    /// Services are still held and are not shutting down.
    StopServices,
    /// Nothing local is running.
    Close,
}

pub fn close_request(busy: bool, stopping: bool, has_runtime: bool) -> CloseRequest {
    if busy {
        CloseRequest::Wait
    } else if stopping {
        CloseRequest::AfterStop
    } else if has_runtime {
        CloseRequest::StopServices
    } else {
        CloseRequest::Close
    }
}

fn mongo_arguments(dbpath: &Path, port: u16, wine: bool) -> Vec<String> {
    let mut args = vec![
        "--bind_ip".to_owned(),
        "127.0.0.1".to_owned(),
        "--dbpath".to_owned(),
        dbpath.display().to_string(),
        "--port".to_owned(),
        port.to_string(),
    ];
    if wine {
        args.push("--setParameter".to_owned());
        args.push("diagnosticDataCollectionEnabled=false".to_owned());
    }
    args
}

enum GitProbe {
    Ready,
    IncludeDepth,
    Failed,
}

fn probe_git(path: &Path) -> GitProbe {
    if !path.is_file() {
        return GitProbe::Failed;
    }
    let mut command = Command::new(path);
    command.arg("--version").env("GIT_TERMINAL_PROMPT", "0");
    match command_output_timeout(command, Duration::from_secs(20)) {
        Ok(output) if output.status.success() => GitProbe::Ready,
        Ok(output) => {
            let text = format!(
                "{}\n{}",
                String::from_utf8_lossy(&output.stdout),
                String::from_utf8_lossy(&output.stderr)
            );
            if text.contains("exceeded maximum include depth") {
                GitProbe::IncludeDepth
            } else {
                GitProbe::Failed
            }
        }
        Err(_) => GitProbe::Failed,
    }
}

fn git_candidates() -> Vec<PathBuf> {
    let mut candidates = Vec::new();
    if let Some(path) = env::var_os("PATH") {
        candidates.extend(env::split_paths(&path).map(|directory| directory.join("git.exe")));
    }
    for key in ["ProgramFiles", "ProgramFiles(x86)", "LOCALAPPDATA"] {
        if let Some(base) = env::var_os(key) {
            let base = PathBuf::from(base);
            candidates.push(base.join("Git/cmd/git.exe"));
            candidates.push(base.join("Programs/Git/cmd/git.exe"));
        }
    }
    if let Ok(root) = root() {
        candidates.push(root.join("tools/git/cmd/git.exe"));
    }
    candidates
}

pub(crate) fn git_executable() -> Option<PathBuf> {
    for candidate in git_candidates() {
        match probe_git(&candidate) {
            GitProbe::Ready => return Some(candidate),
            GitProbe::IncludeDepth
                if crate::install::running_under_wine() && crate::setup::repair_git_include(&candidate) =>
            {
                if matches!(probe_git(&candidate), GitProbe::Ready) {
                    return Some(candidate);
                }
            }
            _ => {}
        }
    }
    None
}

struct OwnedChild(Child);

impl Drop for OwnedChild {
    fn drop(&mut self) {
        terminate_child(&mut self.0);
    }
}

struct OperationPermit;

impl Drop for OperationPermit {
    fn drop(&mut self) {
        OPERATION.store(false, Ordering::Release);
    }
}

/// Backend stdout/stderr go through pipes into this file instead of an ever-growing append, so a long
/// session (the server logs every packet at Debug) can't fill the disk. Each start keeps the last run as
/// `<name>.previous.log`; within a run the same rotation happens at `limit`, so the newest lines always
/// survive and at most two files exist.
#[cfg_attr(not(windows), allow(dead_code))]
struct RotatingLog {
    path: PathBuf,
    previous: PathBuf,
    file: fs::File,
    written: u64,
    limit: u64,
}

#[cfg_attr(not(windows), allow(dead_code))]
impl RotatingLog {
    fn open(path: PathBuf, limit: u64) -> Result<Arc<Mutex<Self>>> {
        let previous = path.with_extension("previous.log");
        if path.exists() {
            fs::rename(&path, &previous).with_context(|| format!("rotate {}", path.display()))?;
        }
        let file = fs::File::create(&path).with_context(|| format!("create {}", path.display()))?;
        Ok(Arc::new(Mutex::new(Self { path, previous, file, written: 0, limit })))
    }

    fn rotate(&mut self) -> std::io::Result<()> {
        // rename replaces the old previous log; Rust opens files with FILE_SHARE_DELETE, so the live file
        // can be renamed while this handle is still open.
        fs::rename(&self.path, &self.previous)?;
        self.file = fs::File::create(&self.path)?;
        self.written = 0;
        Ok(())
    }

    fn write(&mut self, mut bytes: &[u8]) -> std::io::Result<()> {
        while !bytes.is_empty() {
            if self.written >= self.limit {
                self.rotate()?;
            }
            let take = bytes.len().min((self.limit - self.written) as usize);
            self.file.write_all(&bytes[..take])?;
            self.written += take as u64;
            bytes = &bytes[take..];
        }
        Ok(())
    }
}

/// Drains both pipes until the child exits. Write errors are ignored so a full disk or locked file never
/// blocks the child on a full pipe.
#[cfg_attr(not(windows), allow(dead_code))]
fn pump_output(child: &mut Child, log: &Arc<Mutex<RotatingLog>>) {
    fn pump(mut stream: impl Read + Send + 'static, log: Arc<Mutex<RotatingLog>>) {
        thread::spawn(move || {
            let mut buffer = vec![0u8; 64 * 1024];
            while let Ok(read @ 1..) = stream.read(&mut buffer) {
                if let Ok(mut log) = log.lock() {
                    let _ = log.write(&buffer[..read]);
                }
            }
        });
    }
    if let Some(stdout) = child.stdout.take() {
        pump(stdout, log.clone());
    }
    if let Some(stderr) = child.stderr.take() {
        pump(stderr, log.clone());
    }
}

#[cfg(test)]
fn stream_lines<R: std::io::Read + Send + 'static>(
    stream: R,
    send: mpsc::Sender<std::io::Result<String>>,
) {
    thread::spawn(move || {
        let mut stream = BufReader::new(stream);
        let mut bytes = Vec::new();
        loop {
            bytes.clear();
            match stream.read_until(b'\n', &mut bytes) {
                Ok(0) => break,
                Ok(_) => {
                    let line = String::from_utf8_lossy(&bytes)
                        .trim_end_matches(&['\r', '\n'][..])
                        .to_owned();
                    if send.send(Ok(line)).is_err() {
                        break;
                    }
                }
                Err(error) => {
                    let _ = send.send(Err(error));
                    break;
                }
            }
        }
    });
}

fn operation_lock() -> Result<OperationPermit> {
    OPERATION
        .compare_exchange(false, true, Ordering::Acquire, Ordering::Relaxed)
        .map(|_| OperationPermit)
        .map_err(|_| anyhow::anyhow!("another local setup or runtime operation is active"))
}
#[cfg(windows)]
fn open_setup_lock(root: &Path) -> Result<fs::File> {
    use std::os::windows::fs::OpenOptionsExt;
    fs::OpenOptions::new()
        .read(true)
        .write(true)
        .create(true)
        .share_mode(0)
        .open(root.join("setup.lock"))
        .context("another local setup is already active")
}

fn validate_repository(repository: &str) -> Result<()> {
    if repository.trim() != repository {
        bail!("repository URL contains surrounding whitespace");
    }
    let url = Url::parse(repository).context("invalid repository URL")?;
    if url.scheme() != "https"
        || url.host_str() != Some("github.com")
        || url.username() != ""
        || url.password().is_some()
        || url.query().is_some()
        || url.fragment().is_some()
        || !url.path().ends_with(".git")
        || url.path_segments().map(|s| s.count()) != Some(2)
    {
        bail!("repository must be an HTTPS github.com owner/repository.git URL");
    }
    Ok(())
}

fn validate_branch(branch: &str) -> Result<()> {
    if branch.is_empty()
        || branch.len() > 255
        || branch.starts_with('-')
        || branch.starts_with('.')
        || branch.starts_with('/')
        || branch.ends_with('.')
        || branch.ends_with('/')
        || branch.contains("..")
        || branch.contains("@{")
        || branch.contains("//")
        || branch
            .bytes()
            .any(|b| b <= b' ' || b == 0x7f || b"~^:?*[\\".contains(&b))
    {
        bail!("invalid git branch name");
    }
    Ok(())
}

fn validate_build(root: &Path, build: &LocalBuild) -> Result<()> {
    if build.schema_version != SCHEMA_VERSION {
        bail!("unsupported local build schema {}", build.schema_version);
    }
    validate_repository(&build.repository)?;
    if build.revision.len() != 40 || !build.revision.bytes().all(|b| b.is_ascii_hexdigit()) {
        bail!("invalid local build revision");
    }
    if build.sdk_port == 0
        || build.game_port == 0
        || build.mongo_port == 0
        || build.sdk_port == build.game_port
        || build.sdk_port == build.mongo_port
        || build.game_port == build.mongo_port
    {
        bail!("local build ports must be distinct non-zero ports");
    }
    let canonical_root = root
        .canonicalize()
        .with_context(|| format!("resolve {}", root.display()))?;
    for (name, path) in [
        ("serverDirectory", &build.server_directory),
        ("resourceDirectory", &build.resource_directory),
        ("patchDirectory", &build.patch_directory),
    ] {
        if !path.is_absolute()
            || !path.is_dir()
            || !path.canonicalize()?.starts_with(&canonical_root)
        {
            bail!("{name} must be an existing directory beneath the local root");
        }
    }
    for (name, path) in [("dotnet", &build.dotnet), ("mongod", &build.mongod)] {
        if !path.is_absolute() || !path.is_file() {
            bail!("{name} must be an existing absolute executable path");
        }
    }
    if !build.server_directory.join("AscNet.dll").is_file() {
        bail!("serverDirectory does not contain AscNet.dll");
    }
    Ok(())
}

fn reserve_ports(ports: [u16; 3]) -> Result<Vec<TcpListener>> {
    ports
        .into_iter()
        .map(|port| {
            TcpListener::bind((Ipv4Addr::LOCALHOST, port))
                .with_context(|| format!("local port {port} is already occupied"))
        })
        .collect()
}

fn wait_tcp(child: &mut Child, port: u16, name: &str) -> Result<()> {
    let deadline = Instant::now() + START_TIMEOUT;
    loop {
        if let Some(status) = child.try_wait()? {
            bail!("{name} exited before becoming ready ({status})");
        }
        if TcpStream::connect_timeout(
            &SocketAddr::new(IpAddr::V4(Ipv4Addr::LOCALHOST), port),
            Duration::from_millis(250),
        )
        .is_ok()
        {
            return Ok(());
        }
        if Instant::now() >= deadline {
            bail!("timed out waiting for {name} on port {port}");
        }
        thread::sleep(Duration::from_millis(100));
    }
}

fn wait_server(child: &mut Child, origin: &str, game_port: u16) -> Result<()> {
    let client = crate::download::wine_safe(
        reqwest::blocking::Client::builder().no_proxy().timeout(Duration::from_secs(2)),
    )
    .build()?;
    let deadline = Instant::now() + START_TIMEOUT;
    loop {
        if let Some(status) = child.try_wait()? {
            bail!("AscNet server exited before becoming ready ({status})");
        }
        let game_ready = TcpStream::connect_timeout(
            &SocketAddr::new(IpAddr::V4(Ipv4Addr::LOCALHOST), game_port),
            Duration::from_millis(100),
        )
        .is_ok();
        let api_ready = client
            .get(format!("{origin}/api/launcher/status"))
            .send()
            .and_then(reqwest::blocking::Response::error_for_status)
            .ok()
            .and_then(|mut response| {
                let length = response.content_length();
                if length.is_some_and(|n| n > 65_536) {
                    return None;
                }
                let bytes = crate::download::read_body(&mut response, length, 65_536).ok()?;
                if bytes.len() > 65_536 || length.is_some_and(|n| bytes.len() as u64 != n) {
                    return None;
                }
                serde_json::from_slice::<serde_json::Value>(&bytes).ok()
            })
            .and_then(|value| value.get("schemaVersion").and_then(serde_json::Value::as_u64))
            == Some(1);
        if game_ready && api_ready {
            return Ok(());
        }
        if Instant::now() >= deadline {
            bail!("timed out waiting for AscNet game and launcher-status endpoints");
        }
        thread::sleep(Duration::from_millis(150));
    }
}

fn text_output<'a>(bytes: &'a [u8], description: &str) -> Result<&'a str> {
    std::str::from_utf8(bytes)
        .with_context(|| format!("{description} is not UTF-8"))
        .map(str::trim)
}

fn terminate_child(child: &mut Child) {
    let _ = child.kill();
    let _ = child.wait();
}

/// The launcher has no console, so console children would each open their own window.
pub(crate) fn hide_console(command: &mut Command) -> &mut Command {
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        command.creation_flags(windows::Win32::System::Threading::CREATE_NO_WINDOW.0);
    }
    command
}

/// Spawns `command` without a console window.
///
/// Rust 1.95's `CommandExt::inherit_handles` is nightly-only, and `Command`
/// otherwise passes every inheritable handle to the child. For the duration
/// of `CreateProcess`, inheritable handles already open in this process are
/// marked non-inheritable. The standard pipes created inside `spawn` stay
/// inheritable. A failed handle snapshot still spawns.
pub(crate) fn spawn_hidden(command: &mut Command) -> std::io::Result<Child> {
    hide_console(command);
    #[cfg(windows)]
    let _restore = suspend_inherited_handles();
    command.spawn()
}

/// `SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX` values whose `OBJ_INHERIT` bit is set.
/// The snapshot is the 64-bit layout Wine writes for `SystemExtendedHandleInformation`.
fn inherited_handle_values(snapshot: &[u8], pid: usize) -> Vec<isize> {
    const HEADER: usize = 16;
    const ENTRY: usize = 40;
    const OBJ_INHERIT: u32 = 0x2;
    if snapshot.len() < HEADER || size_of::<usize>() != 8 {
        return Vec::new();
    }
    let count = usize::from_ne_bytes(snapshot[0..8].try_into().unwrap());
    let mut handles = Vec::new();
    for index in 0..count {
        let start = HEADER + index * ENTRY;
        if start + ENTRY > snapshot.len() {
            break;
        }
        let owner = usize::from_ne_bytes(snapshot[start + 8..start + 16].try_into().unwrap());
        let handle = usize::from_ne_bytes(snapshot[start + 16..start + 24].try_into().unwrap());
        let attributes = u32::from_ne_bytes(snapshot[start + 32..start + 36].try_into().unwrap());
        if owner == pid && handle != 0 && attributes & OBJ_INHERIT != 0 {
            handles.push(handle as isize);
        }
    }
    handles
}

#[cfg(windows)]
struct SuspendedHandles(Vec<windows::Win32::Foundation::HANDLE>);

#[cfg(windows)]
impl Drop for SuspendedHandles {
    fn drop(&mut self) {
        use windows::Win32::Foundation::{SetHandleInformation, HANDLE_FLAG_INHERIT};
        for handle in &self.0 {
            unsafe {
                let _ = SetHandleInformation(*handle, HANDLE_FLAG_INHERIT.0, HANDLE_FLAG_INHERIT);
            }
        }
    }
}

#[cfg(windows)]
fn suspend_inherited_handles() -> SuspendedHandles {
    use windows::Win32::Foundation::{SetHandleInformation, HANDLE, HANDLE_FLAG_INHERIT, HANDLE_FLAGS};
    let Some(snapshot) = extended_handle_snapshot() else {
        return SuspendedHandles(Vec::new());
    };
    let pid = unsafe { windows::Win32::System::Threading::GetCurrentProcessId() } as usize;
    let mut restore = Vec::new();
    for value in inherited_handle_values(&snapshot, pid) {
        let handle = HANDLE(value);
        if unsafe { SetHandleInformation(handle, HANDLE_FLAG_INHERIT.0, HANDLE_FLAGS(0)).is_ok() } {
            restore.push(handle);
        }
    }
    SuspendedHandles(restore)
}

#[cfg(windows)]
fn extended_handle_snapshot() -> Option<Vec<u8>> {
    use windows::Wdk::System::SystemInformation::{NtQuerySystemInformation, SYSTEM_INFORMATION_CLASS};
    use windows::Win32::Foundation::STATUS_INFO_LENGTH_MISMATCH;
    const SYSTEM_EXTENDED_HANDLE_INFORMATION: SYSTEM_INFORMATION_CLASS = SYSTEM_INFORMATION_CLASS(64);
    let mut buffer = vec![0u8; 256 * 1024];
    for _ in 0..6 {
        let mut length = 0u32;
        let status = unsafe {
            NtQuerySystemInformation(
                SYSTEM_EXTENDED_HANDLE_INFORMATION,
                buffer.as_mut_ptr().cast(),
                buffer.len() as u32,
                &mut length,
            )
        };
        if status.is_ok() {
            let end = (length as usize).min(buffer.len());
            buffer.truncate(end);
            return Some(buffer);
        }
        if status != STATUS_INFO_LENGTH_MISMATCH {
            return None;
        }
        let next = (length as usize).max(buffer.len().saturating_mul(2));
        if next <= buffer.len() || next > 32 * 1024 * 1024 {
            return None;
        }
        buffer.resize(next, 0);
    }
    None
}

fn command_output_timeout(mut command: Command, timeout: Duration) -> Result<std::process::Output> {
    command.stdout(Stdio::piped()).stderr(Stdio::piped());
    let mut child = spawn_hidden(&mut command).context("start command")?;
    let deadline = Instant::now() + timeout;
    loop {
        if child.try_wait()?.is_some() {
            return child.wait_with_output().context("collect command output");
        }
        if Instant::now() >= deadline {
            terminate_child(&mut child);
            bail!("command timed out after {} seconds", timeout.as_secs());
        }
        thread::sleep(Duration::from_millis(100));
    }
}
#[cfg(windows)]
pub(crate) fn atomic_replace(source: &Path, destination: &Path) -> Result<()> {
    use std::os::windows::ffi::OsStrExt;
    use windows::{
        core::PCWSTR,
        Win32::Storage::FileSystem::{
            MoveFileExW, MOVEFILE_REPLACE_EXISTING, MOVEFILE_WRITE_THROUGH,
        },
    };
    let source: Vec<u16> = source.as_os_str().encode_wide().chain(Some(0)).collect();
    let destination: Vec<u16> = destination
        .as_os_str()
        .encode_wide()
        .chain(Some(0))
        .collect();
    unsafe {
        MoveFileExW(
            PCWSTR(source.as_ptr()),
            PCWSTR(destination.as_ptr()),
            MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH,
        )
        .context("activate local build state")
    }
}

#[cfg(windows)]
pub(crate) struct JobHandle(windows::Win32::Foundation::HANDLE);

#[cfg(windows)]
impl Drop for JobHandle {
    fn drop(&mut self) {
        if !self.0.is_invalid() {
            unsafe {
                let _ = windows::Win32::Foundation::CloseHandle(self.0);
            }
        }
    }
}

#[cfg(windows)]
pub(crate) fn create_job() -> Result<JobHandle> {
    use windows::Win32::System::JobObjects::{
        CreateJobObjectW, JobObjectExtendedLimitInformation, SetInformationJobObject,
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION, JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
    };
    unsafe {
        let job = JobHandle(CreateJobObjectW(None, None)?);
        let mut info = JOBOBJECT_EXTENDED_LIMIT_INFORMATION::default();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        SetInformationJobObject(
            job.0,
            JobObjectExtendedLimitInformation,
            &info as *const _ as _,
            std::mem::size_of_val(&info) as u32,
        )?;
        Ok(job)
    }
}

#[cfg(windows)]
pub(crate) fn assign_to_job(job: &JobHandle, child: &Child) -> Result<()> {
    use std::os::windows::io::AsRawHandle;
    use windows::Win32::{Foundation::HANDLE, System::JobObjects::AssignProcessToJobObject};
    unsafe {
        AssignProcessToJobObject(job.0, HANDLE(child.as_raw_handle() as isize))
            .context("contain local process in launcher job")
    }
}

#[cfg(windows)]
fn wait_or_kill(child: &mut Child, timeout: Duration) -> Result<bool> {
    let deadline = Instant::now() + timeout;
    while child.try_wait()?.is_none() && Instant::now() < deadline {
        thread::sleep(Duration::from_millis(100));
    }
    if child.try_wait()?.is_none() {
        terminate_child(child);
        return Ok(true);
    }
    Ok(false)
}

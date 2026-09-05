use anyhow::{bail, Context, Result};
use reqwest::Url;
use serde::Deserialize;
use std::{
    env, fs,
    io::{BufRead, BufReader, Write},
    net::{IpAddr, Ipv4Addr, SocketAddr, TcpListener, TcpStream},
    path::{Path, PathBuf},
    process::{Child, Command, Stdio},
    sync::{
        atomic::{AtomicBool, Ordering},
        mpsc,
    },
    thread,
    time::{Duration, Instant},
};

const SCHEMA_VERSION: u32 = 1;
const START_TIMEOUT: Duration = Duration::from_secs(45);
const SETUP_TIMEOUT: Duration = Duration::from_secs(60 * 60);
const STOP_TIMEOUT: Duration = Duration::from_secs(10);
static OPERATION: AtomicBool = AtomicBool::new(false);
#[derive(Clone, Deserialize)]
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
    Ok(Some(build))
}

pub fn prepare(
    repository: &str,
    branch: &str,
    progress: &mut dyn FnMut(&str),
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
    let script = env::current_exe()?
        .parent()
        .context("launcher executable has no parent directory")?
        .join("setup-local.ps1");
    if !script.is_file() {
        bail!("local setup script is missing: {}", script.display());
    }

    progress("Starting local source setup");
    #[cfg(windows)]
    let setup_job = create_job()?;
    let mut child = OwnedChild(
        Command::new("powershell.exe")
            .args([
                "-NoLogo",
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
            ])
            .arg(&script)
            .arg("-Root")
            .arg(&root)
            .arg("-Repository")
            .arg(repository)
            .arg("-Branch")
            .arg(branch)
            .arg("-SetupLockHeld")
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .spawn()
            .context("start PowerShell local setup")?,
    );
    #[cfg(windows)]
    assign_to_job(&setup_job, &child.0)?;
    let (send, receive) = mpsc::channel();
    stream_lines(
        child
            .0
            .stdout
            .take()
            .context("capture local setup output")?,
        send.clone(),
    );
    stream_lines(
        child
            .0
            .stderr
            .take()
            .context("capture local setup errors")?,
        send.clone(),
    );
    drop(send);
    let deadline = Instant::now() + SETUP_TIMEOUT;
    loop {
        if Instant::now() >= deadline {
            terminate_child(&mut child.0);
            bail!("local setup timed out after one hour");
        }
        match receive.recv_timeout(Duration::from_millis(250)) {
            Ok(line) => progress(&line.context("read local setup output")?),
            Err(mpsc::RecvTimeoutError::Disconnected) => break,
            Err(mpsc::RecvTimeoutError::Timeout) => {}
        }
    }
    let status = loop {
        if let Some(status) = child.0.try_wait().context("wait for local setup")? {
            break status;
        }
        if Instant::now() >= deadline {
            terminate_child(&mut child.0);
            bail!("local setup timed out after one hour");
        }
        thread::sleep(Duration::from_millis(100));
    };
    if !status.success() {
        bail!("local setup failed with {status}");
    }
    let pending = root.join("build-state.pending.json");
    let bytes = fs::read(&pending).with_context(|| format!("read {}", pending.display()))?;
    let build: LocalBuild =
        serde_json::from_slice(&bytes).context("invalid pending local build state")?;
    validate_build(&root, &build)?;
    crate::package::load_package(&build.patch_directory)
        .context("validate prepared patch package")?;
    fs::OpenOptions::new()
        .write(true)
        .open(&pending)?
        .sync_all()
        .context("flush pending local build state")?;
    #[cfg(windows)]
    atomic_replace(&pending, &root.join("build-state.json"))?;
    Ok(build)
}

pub fn check_update(repository: &str, branch: &str) -> Result<Option<bool>> {
    validate_repository(repository)?;
    validate_branch(branch)?;
    let checkout = root()?.join("checkout");
    if !checkout.join(".git").is_dir() {
        return Ok(None);
    }
    let Some(git) = git_executable() else {
        return Ok(None);
    };
    let mut local = Command::new(&git);
    local
        .args(["-C"])
        .arg(&checkout)
        .args(["rev-parse", "HEAD"]);
    let output = match command_output_timeout(local, Duration::from_secs(15)) {
        Ok(output) if output.status.success() => output,
        Ok(_) | Err(_) => return Ok(None),
    };
    let head = text_output(&output.stdout, "local git revision")?.to_owned();
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
    let remote = remote.to_owned();
    Ok(Some(head != remote))
}

pub struct LocalRuntime {
    server: OwnedChild,
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
        #[cfg(not(windows))]
        {
            let _ = (build, progress);
            bail!("the local runtime is supported on Windows only");
        }
        #[cfg(windows)]
        {
            use std::os::windows::process::CommandExt;
            use windows::Win32::System::Threading::CREATE_NO_WINDOW;

            let operation = operation_lock()?;
            let root = root()?;
            validate_build(&root, build)?;
            let _ports = reserve_ports([build.mongo_port, build.sdk_port, build.game_port])?;
            fs::create_dir_all(root.join("data/mongo"))?;
            fs::create_dir_all(root.join("logs"))?;
            let job = create_job()?;

            progress("Starting MongoDB");
            drop(_ports);
            let mongo_log = fs::OpenOptions::new()
                .create(true)
                .append(true)
                .open(root.join("logs/mongod.log"))?;
            let mut mongo = OwnedChild(
                Command::new(&build.mongod)
                    .args(["--bind_ip", "127.0.0.1", "--dbpath"])
                    .arg(root.join("data/mongo"))
                    .arg("--port")
                    .arg(build.mongo_port.to_string())
                    .stdout(mongo_log.try_clone()?)
                    .stderr(mongo_log)
                    .creation_flags(CREATE_NO_WINDOW.0)
                    .spawn()
                    .context("start MongoDB")?,
            );
            assign_to_job(&job, &mongo.0)?;
            wait_tcp(&mut mongo.0, build.mongo_port, "MongoDB")?;

            progress("Starting AscNet server");
            let origin = format!("http://127.0.0.1:{}", build.sdk_port);
            let server_log = fs::OpenOptions::new()
                .create(true)
                .append(true)
                .open(root.join("logs/server.log"))?;
            let mut server = OwnedChild(
                Command::new(&build.dotnet)
                    .arg(build.server_directory.join("AscNet.dll"))
                    .args(["--urls", &origin])
                    .current_dir(&build.resource_directory)
                    .env("ASCNET_GATE_FALLBACK_USERNAME", "")
                    .env("ASCNET_PUBLIC_HTTP_ORIGIN", &origin)
                    .env("ASCNET_GAME_BIND_ADDRESS", "127.0.0.1")
                    .env("ASCNET_MANAGED_STDIN", "1")
                    .stdin(Stdio::piped())
                    .stdout(server_log.try_clone()?)
                    .stderr(server_log)
                    .creation_flags(CREATE_NO_WINDOW.0)
                    .spawn()
                    .context("start AscNet server")?,
            );
            assign_to_job(&job, &server.0)?;
            wait_server(&mut server.0, &origin, build.game_port)?;
            progress("Local backend is ready");
            Ok(Self {
                server,
                mongo,
                dotnet: build.dotnet.clone(),
                server_dll: build.server_directory.join("AscNet.dll"),
                mongo_port: build.mongo_port,
                stopped: false,
                job,
                _operation: Some(operation),
            })
        }
    }

    pub fn stop(&mut self) -> Result<()> {
        #[cfg(windows)]
        {
            use std::os::windows::process::CommandExt;
            use windows::Win32::System::Threading::CREATE_NO_WINDOW;
            if self.stopped {
                return Ok(());
            }
            let server_request = (|| -> Result<()> {
                let stdin = self
                    .server
                    .0
                    .stdin
                    .as_mut()
                    .context("AscNet server stdin is unavailable")?;
                stdin
                    .write_all(b"shutdown\n")
                    .context("request AscNet server shutdown")?;
                stdin
                    .flush()
                    .context("flush AscNet server shutdown request")
            })();
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
fn git_executable() -> Option<PathBuf> {
    if let Some(path) = env::var_os("PATH").and_then(|path| {
        env::split_paths(&path)
            .map(|directory| directory.join("git.exe"))
            .find(|candidate| candidate.is_file())
    }) {
        return Some(path);
    }
    ["ProgramFiles", "ProgramFiles(x86)", "LOCALAPPDATA"]
        .into_iter()
        .filter_map(env::var_os)
        .map(PathBuf::from)
        .flat_map(|base| {
            [
                base.join("Git/cmd/git.exe"),
                base.join("Programs/Git/cmd/git.exe"),
            ]
        })
        .find(|candidate| candidate.is_file())
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
    let client = reqwest::blocking::Client::builder()
        .no_proxy()
        .timeout(Duration::from_secs(2))
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
            .and_then(|response| response.json::<serde_json::Value>())
            .ok()
            .and_then(|value| {
                value
                    .get("schemaVersion")
                    .and_then(serde_json::Value::as_u64)
            })
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

fn command_output_timeout(mut command: Command, timeout: Duration) -> Result<std::process::Output> {
    let mut child = command
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .context("start command")?;
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
fn atomic_replace(source: &Path, destination: &Path) -> Result<()> {
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
struct JobHandle(windows::Win32::Foundation::HANDLE);

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
fn create_job() -> Result<JobHandle> {
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
fn assign_to_job(job: &JobHandle, child: &Child) -> Result<()> {
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

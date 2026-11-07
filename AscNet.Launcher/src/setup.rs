//! Local source setup runs inside the launcher. It installs Git, the .NET 8 SDK,
//! Rust, and MongoDB from official archives and never starts PowerShell or WinGet.
//! Under Wine it also unpacks the MSVC toolset and Windows SDK from the Visual
//! Studio release channel instead of running the Build Tools installer.
#![cfg_attr(not(windows), allow(dead_code))]
use anyhow::{bail, Context, Result};
use base64::Engine;
use serde::Deserialize;
use serde_json::Value;
use sha2::{Digest, Sha256, Sha512};
use std::{
    env, fs,
    io::{ErrorKind, Read, Seek, SeekFrom, Write},
    net::{Ipv4Addr, TcpListener},
    path::{Component, Path, PathBuf},
    process::{Child, Command, Stdio},
    sync::mpsc,
    thread,
    time::{Duration, Instant},
};
#[cfg(test)]
use std::cell::Cell;

const RUST_VERSION: &str = "1.92.0";
const RUST_HOST: &str = "x86_64-pc-windows-msvc";
const MINGIT_URL: &str = "https://github.com/git-for-windows/git/releases/download/v2.56.0.windows.1/MinGit-2.56.0-64-bit.zip";
const MINGIT_SHA256: &str = "064b440ff870ed5198527e8f3a92cdf5bd2fd0fedf5e718af95e3fdaddeff718";
const DOTNET_RELEASES: &str = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/8.0/releases.json";
const MONGO_CATALOG: &str = "https://downloads.mongodb.org/full.json";
const VS_BOOTSTRAPPER: &str = "https://aka.ms/vs/17/release/vs_buildtools.exe";
const VS_CHANNEL: &str = "https://aka.ms/vs/17/release/channel";
const NUGET_INDEX: &str = "https://api.nuget.org/v3/index.json";
/// A connection that closes before the body arrives is fetched again.
/// Four attempts, and the pause between them, match the game downloader.
/// A checksum mismatch or the setup deadline still fails on the first try.
const DOWNLOAD_ATTEMPTS: u32 = 4;

#[cfg(test)]
thread_local! {
    static ALLOW_PLAIN_HTTP: Cell<bool> = const { Cell::new(false) };
}

struct RustComponent {
    url: &'static str,
    sha256: &'static str,
}

const RUST_COMPONENTS: [RustComponent; 3] = [
    RustComponent {
        url: "https://static.rust-lang.org/dist/2025-12-11/rustc-1.92.0-x86_64-pc-windows-msvc.tar.gz",
        sha256: "4f321ca1903b8b08ceb07c0305f9f0320bc711ff6e285d726b8ef1dda774935d",
    },
    RustComponent {
        url: "https://static.rust-lang.org/dist/2025-12-11/cargo-1.92.0-x86_64-pc-windows-msvc.tar.gz",
        sha256: "9f6178ddcaed7c9190c454db5da801e2fe6c8fde8600258a9bdd0e107e81ac3b",
    },
    RustComponent {
        url: "https://static.rust-lang.org/dist/2025-12-11/rust-std-1.92.0-x86_64-pc-windows-msvc.tar.gz",
        sha256: "3af377fb29083c2117e85e6a107aedb008bd76929c22d56ce45c5be1be3a2540",
    },
];

pub fn run(
    root: &Path,
    repository: &str,
    branch: &str,
    launcher_dir: &Path,
    progress: &mut dyn FnMut(&str),
    log: &mut dyn FnMut(&str) -> Result<()>,
    deadline: Instant,
) -> Result<()> {
    #[cfg(windows)]
    {
        return run_windows(root, repository, branch, launcher_dir, progress, log, deadline);
    }
    #[cfg(not(windows))]
    {
        let _ = (root, repository, branch, launcher_dir, progress, log, deadline);
        bail!("local source setup is supported on Windows only");
    }
}

/// Use the retained checkout directly so Setup updates mods even when reusing a cached build.
pub fn mods_directory(root: &Path) -> PathBuf {
    root.join("checkout").join("Mods")
}

pub(crate) fn without_program_files_git_include(text: &str) -> String {
    let newline = if text.contains("\r\n") { "\r\n" } else { "\n" };
    let mut kept = Vec::new();
    for line in text.split('\n') {
        let line = line.trim_end_matches('\r');
        let key = line.trim().to_ascii_lowercase().replace('\\', "/");
        if key.starts_with("path") && key.contains("c:/program files/git/etc/gitconfig") {
            continue;
        }
        kept.push(line);
    }
    let mut joined = kept.join(newline);
    if text.ends_with('\n') {
        joined.push_str(newline);
    }
    joined
}

pub(crate) fn repair_git_include(git: &Path) -> bool {
    for config in gitconfig_candidates(git) {
        if !config.is_file() {
            continue;
        }
        let Ok(text) = fs::read_to_string(&config) else {
            continue;
        };
        let stripped = without_program_files_git_include(&text);
        if stripped == text {
            continue;
        }
        if fs::write(&config, stripped).is_err() {
            continue;
        }
        return true;
    }
    false
}

fn gitconfig_candidates(git: &Path) -> Vec<PathBuf> {
    let mut out = Vec::new();
    let mut cursor = git.parent();
    for _ in 0..4 {
        let Some(dir) = cursor else { break };
        out.push(dir.join("etc").join("gitconfig"));
        cursor = dir.parent();
    }
    out
}

pub(crate) fn path_is_within(path: &Path, root: &Path) -> bool {
    let path = normalize_path_key(path);
    let mut root = normalize_path_key(root);
    if root.is_empty() {
        return false;
    }
    if !root.ends_with('\\') {
        root.push('\\');
    }
    path.starts_with(&root)
}

fn normalize_path_key(path: &Path) -> String {
    path.to_string_lossy().replace('/', "\\").to_ascii_lowercase()
}

fn note(
    progress: &mut dyn FnMut(&str),
    log: &mut dyn FnMut(&str) -> Result<()>,
    message: &str,
) -> Result<()> {
    progress(message);
    log(message)
}

fn ensure_time(deadline: Instant) -> Result<()> {
    if Instant::now() >= deadline {
        bail!("local setup timed out");
    }
    Ok(())
}

fn command_line(command: &Command) -> String {
    let mut parts = vec![command.get_program().to_string_lossy().into_owned()];
    parts.extend(command.get_args().map(|arg| arg.to_string_lossy().into_owned()));
    parts.join(" ")
}

fn contain(child: &Child) -> Result<()> {
    #[cfg(windows)]
    {
        SETUP_JOB.with(|slot| {
            let borrowed = slot.borrow();
            if let Some(job) = borrowed.as_ref() {
                crate::local::assign_to_job(job, child)
            } else {
                Ok(())
            }
        })?;
    }
    #[cfg(not(windows))]
    {
        let _ = child;
    }
    Ok(())
}

fn stream_lines<R: Read + Send + 'static>(stream: R, send: mpsc::Sender<std::io::Result<String>>) {
    thread::spawn(move || {
        let mut stream = std::io::BufReader::new(stream);
        let mut bytes = Vec::new();
        loop {
            bytes.clear();
            match std::io::BufRead::read_until(&mut stream, b'\n', &mut bytes) {
                Ok(0) => break,
                Ok(_) => {
                    let line = String::from_utf8_lossy(&bytes)
                        .trim_end_matches(['\r', '\n'])
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

fn run_logged(
    command: &mut Command,
    description: &str,
    deadline: Instant,
    progress: &mut dyn FnMut(&str),
    log: &mut dyn FnMut(&str) -> Result<()>,
) -> Result<()> {
    let shown = command_line(command);
    run_logged_shown(command, &shown, description, deadline, progress, log)
}

fn run_logged_shown(
    command: &mut Command,
    shown: &str,
    description: &str,
    deadline: Instant,
    progress: &mut dyn FnMut(&str),
    log: &mut dyn FnMut(&str) -> Result<()>,
) -> Result<()> {
    ensure_time(deadline)?;
    note(progress, log, &format!("+ {shown}"))?;
    command.stdout(Stdio::piped()).stderr(Stdio::piped());
    let mut child = crate::local::spawn_hidden(command).with_context(|| format!("start {description}"))?;
    if let Err(error) = contain(&child) {
        let _ = child.kill();
        let _ = child.wait();
        return Err(error);
    }
    let (send, receive) = mpsc::channel();
    stream_lines(child.stdout.take().context("capture command output")?, send.clone());
    stream_lines(child.stderr.take().context("capture command errors")?, send);
    loop {
        if Instant::now() >= deadline {
            let _ = child.kill();
            let _ = child.wait();
            bail!("local setup timed out");
        }
        match receive.recv_timeout(Duration::from_millis(200)) {
            Ok(line) => {
                let line = line.context("read command output")?;
                if !line.is_empty() {
                    log(&line)?;
                    progress(&line);
                }
            }
            Err(mpsc::RecvTimeoutError::Timeout) => {}
            Err(mpsc::RecvTimeoutError::Disconnected) => break,
        }
    }
    let status = loop {
        if let Some(status) = child.try_wait().with_context(|| format!("wait for {description}"))? {
            break status;
        }
        if Instant::now() >= deadline {
            let _ = child.kill();
            let _ = child.wait();
            bail!("local setup timed out");
        }
        thread::sleep(Duration::from_millis(100));
    };
    if !status.success() {
        bail!("{description} failed with exit code {}", status.code().unwrap_or(-1));
    }
    Ok(())
}

fn capture_command(
    command: &mut Command,
    description: &str,
    deadline: Instant,
    log_stdout: bool,
    progress: &mut dyn FnMut(&str),
    log: &mut dyn FnMut(&str) -> Result<()>,
) -> Result<std::process::Output> {
    ensure_time(deadline)?;
    note(progress, log, &format!("+ {}", command_line(command)))?;
    command.stdout(Stdio::piped()).stderr(Stdio::piped());
    let mut child = crate::local::spawn_hidden(command).with_context(|| format!("start {description}"))?;
    if let Err(error) = contain(&child) {
        let _ = child.kill();
        let _ = child.wait();
        return Err(error);
    }
    let mut stdout = child.stdout.take().context("capture command output")?;
    let mut stderr = child.stderr.take().context("capture command errors")?;
    let stdout_thread = thread::spawn(move || {
        let mut bytes = Vec::new();
        let _ = stdout.read_to_end(&mut bytes);
        bytes
    });
    let stderr_thread = thread::spawn(move || {
        let mut bytes = Vec::new();
        let _ = stderr.read_to_end(&mut bytes);
        bytes
    });
    loop {
        if child.try_wait().with_context(|| format!("wait for {description}"))?.is_some() {
            break;
        }
        if Instant::now() >= deadline {
            let _ = child.kill();
            let _ = child.wait();
            bail!("local setup timed out");
        }
        thread::sleep(Duration::from_millis(100));
    }
    let status = child.wait().with_context(|| format!("wait for {description}"))?;
    let stdout = stdout_thread.join().unwrap_or_default();
    let stderr = stderr_thread.join().unwrap_or_default();
    for stream in [stderr.as_slice(), if log_stdout { stdout.as_slice() } else { &[] }] {
        for line in String::from_utf8_lossy(stream).lines() {
            if !line.is_empty() {
                log(line)?;
                progress(line);
            }
        }
    }
    if !status.success() {
        bail!("{description} failed with exit code {}", status.code().unwrap_or(-1));
    }
    Ok(std::process::Output { status, stdout, stderr })
}

fn git_text(
    git: &Path,
    checkout: Option<&Path>,
    args: &[String],
    description: &str,
    deadline: Instant,
    progress: &mut dyn FnMut(&str),
    log: &mut dyn FnMut(&str) -> Result<()>,
) -> Result<String> {
    let mut command = Command::new(git);
    command.env("GIT_TERMINAL_PROMPT", "0").env("GCM_INTERACTIVE", "Never");
    // `-c` has to precede -C and the subcommand.
    let mut ordered = vec!["-c".to_owned(), "credential.interactive=false".to_owned()];
    if let Some(checkout) = checkout {
        ordered.push("-C".to_owned());
        ordered.push(checkout.display().to_string());
    }
    ordered.extend(args.iter().cloned());
    command.args(&ordered);
    let output = capture_command(&mut command, description, deadline, false, progress, log)?;
    Ok(String::from_utf8_lossy(&output.stdout).trim().to_owned())
}

fn update_checkout(
    git: &Path,
    checkout: &Path,
    repository: &str,
    branch: &str,
    deadline: Instant,
    progress: &mut dyn FnMut(&str),
    log: &mut dyn FnMut(&str) -> Result<()>,
) -> Result<()> {
    if !checkout.exists() {
        if let Some(parent) = checkout.parent() {
            fs::create_dir_all(parent).with_context(|| format!("create {}", parent.display()))?;
        }
        let mut command = Command::new(git);
        command
            .env("GIT_TERMINAL_PROMPT", "0")
            .env("GCM_INTERACTIVE", "Never")
            .args([
                "-c",
                "credential.interactive=false",
                "clone",
                "-c",
                "core.autocrlf=false",
                "--single-branch",
                "--branch",
                branch,
                "--",
                repository,
            ])
            .arg(checkout);
        if let Err(error) = run_logged(&mut command, "Repository clone", deadline, progress, log) {
            if checkout.exists() {
                let _ = fs::remove_dir_all(checkout);
            }
            return Err(error);
        }
    }
    if !checkout.join(".git").exists() {
        bail!("Checkout path is not a Git repository: {}", checkout.display());
    }
    let origin = git_text(git, Some(checkout), &["remote".into(), "get-url".into(), "origin".into()], "git remote get-url origin", deadline, progress, log)?;
    if origin != repository {
        bail!("Checkout origin is '{origin}', expected exactly '{repository}'. Refusing to replace it.");
    }
    let current = git_text(git, Some(checkout), &["branch".into(), "--show-current".into()], "git branch --show-current", deadline, progress, log)?;
    if current != branch {
        bail!("Checkout is on branch '{current}', expected '{branch}'. Switch it manually; setup will not reset your work.");
    }
    let dirty = git_text(
        git,
        Some(checkout),
        &["status".into(), "--porcelain".into(), "--untracked-files=normal".into()],
        "git status --porcelain",
        deadline,
        progress,
        log,
    )?;
    if !dirty.is_empty() {
        bail!("Checkout has local changes. Commit or remove them before updating; setup will not reset, clean, or stash files.\n{dirty}");
    }
    let mut fetch = Command::new(git);
    fetch
        .env("GIT_TERMINAL_PROMPT", "0")
        .env("GCM_INTERACTIVE", "Never")
        .args(["-c", "credential.interactive=false", "-C"])
        .arg(checkout)
        .args(["fetch", "origin", branch]);
    if let Err(error) = run_logged(&mut fetch, "Fetch repository update", deadline, progress, log) {
        // A clean checkout already on the branch can be built when GitHub is
        // unreachable. The next setup that can fetch still fast-forwards.
        let local = git_text(git, Some(checkout), &["rev-parse".into(), "--short".into(), "HEAD".into()], "git rev-parse --short HEAD", deadline, progress, log)?;
        note(progress, log, &format!("WARNING: could not reach {repository} ({error:#}); continuing with the local checkout at {local}."))?;
        return Ok(());
    }
    // A reachable remote is never reset over local commits. Name them and how to recover.
    let local_only = git_text(
        git,
        Some(checkout),
        &["log".into(), "--oneline".into(), "FETCH_HEAD..HEAD".into()],
        "git log --oneline FETCH_HEAD..HEAD",
        deadline,
        progress,
        log,
    )?;
    if !local_only.is_empty() {
        bail!(
            "The source checkout has commits that are not on {repository} ({branch}), so it cannot be updated:\n{local_only}\n\
             Setup will not discard them. To continue, either reset it (this deletes those commits):\n  \
             git -C \"{}\" reset --hard FETCH_HEAD\n\
             or delete the folder \"{}\" and run Setup again to download a fresh copy.",
            checkout.display(),
            checkout.display()
        );
    }
    let mut merge = Command::new(git);
    merge
        .env("GIT_TERMINAL_PROMPT", "0")
        .env("GCM_INTERACTIVE", "Never")
        .args(["-c", "credential.interactive=false", "-C"])
        .arg(checkout)
        .args(["merge", "--ff-only", "FETCH_HEAD"]);
    run_logged(&mut merge, "Fast-forward repository update", deadline, progress, log)
}

fn http_client(deadline: Instant) -> Result<reqwest::blocking::Client> {
    ensure_time(deadline)?;
    let https_only = {
        #[cfg(test)]
        {
            !ALLOW_PLAIN_HTTP.with(Cell::get)
        }
        #[cfg(not(test))]
        {
            true
        }
    };
    Ok(crate::download::wine_safe(
        reqwest::blocking::Client::builder()
            .user_agent(concat!("AscNetLauncher/", env!("CARGO_PKG_VERSION")))
            .https_only(https_only)
            .connect_timeout(Duration::from_secs(15))
            .timeout(Duration::from_secs(120))
            .redirect(reqwest::redirect::Policy::limited(10)),
    )
    .build()?)
}

fn pause_before_retry(failed_attempt: u32, deadline: Instant) -> Result<()> {
    let wait = crate::download::retry_backoff(failed_attempt.saturating_sub(1) as usize);
    let now = Instant::now();
    if now >= deadline {
        bail!("local setup timed out");
    }
    thread::sleep(wait.min(deadline.saturating_duration_since(now)));
    ensure_time(deadline)
}

fn download_file(
    url: &str,
    dest: &Path,
    expect_sha256: Option<&str>,
    expect_sha512: Option<&str>,
    root: &Path,
    deadline: Instant,
    progress: &mut dyn FnMut(&str),
    log: &mut dyn FnMut(&str) -> Result<()>,
) -> Result<String> {
    ensure_time(deadline)?;
    if let Some(parent) = dest.parent() {
        fs::create_dir_all(parent)?;
    }
    // Scratch archives are deleted after extraction. A verified copy stays in
    // the cache, keyed by the expected hash, and the next setup hard-links it.
    let cache = root.join("cache");
    if let Some(hash) = reuse_hashed_file(dest, dest, expect_sha256, expect_sha512)? {
        remember_cache(&cache, dest, expect_sha256, expect_sha512)?;
        note(progress, log, &format!("Reusing {}", display_name(dest)))?;
        return Ok(hash);
    }
    if let Some(key) = cache_key(expect_sha256, expect_sha512) {
        let cached = cache.join(key);
        if let Some(hash) = reuse_hashed_file(&cached, dest, expect_sha256, expect_sha512)? {
            note(progress, log, &format!("Reusing {}", display_name(&cached)))?;
            return Ok(hash);
        }
    }
    let mut last_error = None;
    for attempt in 1..=DOWNLOAD_ATTEMPTS {
        match receive_download(url, dest, expect_sha256, expect_sha512, &cache, deadline, progress, log) {
            Ok(hash) => return Ok(hash),
            Err(error) if attempt < DOWNLOAD_ATTEMPTS && download_interrupted(&error) => {
                note(progress, log, &format!("Download interrupted ({}); retrying {url}", error.root_cause()))?;
                pause_before_retry(attempt, deadline)?;
                last_error = Some(error);
            }
            Err(error) => return Err(error),
        }
    }
    Err(last_error.expect("an interrupted download keeps its last error"))
}

fn receive_download(
    url: &str,
    dest: &Path,
    expect_sha256: Option<&str>,
    expect_sha512: Option<&str>,
    cache: &Path,
    deadline: Instant,
    progress: &mut dyn FnMut(&str),
    log: &mut dyn FnMut(&str) -> Result<()>,
) -> Result<String> {
    ensure_time(deadline)?;
    let partial = partial_path(dest);
    let result = receive_download_inner(url, dest, &partial, expect_sha256, expect_sha512, cache, deadline, progress, log);
    if let Err(error) = &result {
        if !download_interrupted(error) {
            let _ = fs::remove_file(&partial);
        }
    }
    result
}

struct DownloadBody {
    response: reqwest::blocking::Response,
    /// Offset in the destination file where this body continues. Zero means the body is the whole file.
    start_at: u64,
}

fn open_download(url: &str, deadline: Instant, have: u64) -> Result<DownloadBody> {
    fn send(url: &str, deadline: Instant, have: u64) -> Result<reqwest::blocking::Response> {
        ensure_time(deadline)?;
        let mut request = http_client(deadline)?.get(url);
        if have > 0 {
            request = request.header(reqwest::header::RANGE, format!("bytes={have}-"));
        }
        request.send().with_context(|| format!("download {url}"))
    }
    let response = send(url, deadline, have)?;
    if response.status() == reqwest::StatusCode::RANGE_NOT_SATISFIABLE && have > 0 {
        let response = send(url, deadline, 0)?.error_for_status().with_context(|| format!("download {url}"))?;
        return Ok(DownloadBody { response, start_at: 0 });
    }
    let status = response.status();
    let response = response.error_for_status().with_context(|| format!("download {url}"))?;
    let start_at = if status == reqwest::StatusCode::PARTIAL_CONTENT { have } else { 0 };
    Ok(DownloadBody { response, start_at })
}

fn hash_prefix(file: &mut fs::File, len: u64) -> Result<(Sha256, Sha512)> {
    file.seek(SeekFrom::Start(0)).context("read partial download")?;
    let mut sha256 = Sha256::new();
    let mut sha512 = Sha512::new();
    let mut left = len;
    let mut buffer = [0u8; 64 * 1024];
    while left > 0 {
        let want = buffer.len().min(left as usize);
        let count = file.read(&mut buffer[..want]).context("read partial download")?;
        if count == 0 {
            bail!("partial download ended early");
        }
        sha256.update(&buffer[..count]);
        sha512.update(&buffer[..count]);
        left -= count as u64;
    }
    Ok((sha256, sha512))
}

fn receive_download_inner(
    url: &str,
    dest: &Path,
    partial: &Path,
    expect_sha256: Option<&str>,
    expect_sha512: Option<&str>,
    cache: &Path,
    deadline: Instant,
    progress: &mut dyn FnMut(&str),
    log: &mut dyn FnMut(&str) -> Result<()>,
) -> Result<String> {
    note(progress, log, &format!("Downloading {url}"))?;
    let mut file = fs::OpenOptions::new().read(true).write(true).create(true).open(partial).with_context(|| format!("create {}", partial.display()))?;
    let have = file.metadata().with_context(|| format!("read {}", partial.display()))?.len();
    let opened = open_download(url, deadline, have)?;
    let start_at = if opened.start_at > have { 0 } else { opened.start_at };
    if start_at == 0 && have > 0 {
        file.set_len(0).with_context(|| format!("reset {}", partial.display()))?;
    }
    let (mut sha256, mut sha512) = hash_prefix(&mut file, start_at)?;
    file.seek(SeekFrom::Start(start_at)).with_context(|| format!("write {}", partial.display()))?;
    let mut response = opened.response;
    let body_len = response.content_length();
    let mut buffer = [0u8; 64 * 1024];
    let mut received = start_at;
    let mut body_got = 0u64;
    loop {
        if Instant::now() >= deadline {
            drop(file);
            bail!("local setup timed out");
        }
        // Content-Length is the end of this body. Another read waits out the
        // client timeout on a keep-alive connection after the body is complete.
        if body_len.is_some_and(|n| body_got >= n) {
            break;
        }
        let want = body_len.map(|n| buffer.len().min((n - body_got) as usize)).unwrap_or(buffer.len());
        let count = response.read(&mut buffer[..want]).with_context(|| format!("read {url}"))?;
        if count == 0 {
            break;
        }
        file.write_all(&buffer[..count])?;
        sha256.update(&buffer[..count]);
        sha512.update(&buffer[..count]);
        received += count as u64;
        body_got += count as u64;
    }
    if let Some(n) = body_len {
        if body_got != n {
            drop(file);
            return Err(std::io::Error::new(ErrorKind::UnexpectedEof, format!("download {url} ended after {received} bytes, expected {}", start_at + n)).into());
        }
    }
    file.sync_all()?;
    drop(file);
    let actual256 = format!("{:x}", sha256.finalize());
    let actual512 = format!("{:x}", sha512.finalize());
    if let Some(expected) = expect_sha256 {
        if !actual256.eq_ignore_ascii_case(expected) {
            bail!("checksum mismatch for {url} (expected {expected}, received {actual256})");
        }
    }
    if let Some(expected) = expect_sha512 {
        if !actual512.eq_ignore_ascii_case(expected) {
            bail!("checksum mismatch for {url} (expected {expected}, received {actual512})");
        }
    }
    let _ = fs::remove_file(dest);
    fs::rename(partial, dest).with_context(|| format!("store {}", dest.display()))?;
    remember_cache(cache, dest, expect_sha256, expect_sha512)?;
    Ok(actual256)
}

/// A dropped transfer can be fetched again. The body error from static.rust-lang.org
/// is `end of file before message length reached`: the socket closed before
/// Content-Length bytes arrived. Reqwest wraps that as `ErrorKind::Other`.
fn download_interrupted(error: &anyhow::Error) -> bool {
    for cause in error.chain() {
        if cause.to_string().contains("local setup timed out") {
            return false;
        }
        if let Some(io_error) = cause.downcast_ref::<std::io::Error>() {
            if matches!(
                io_error.kind(),
                ErrorKind::UnexpectedEof
                    | ErrorKind::ConnectionReset
                    | ErrorKind::ConnectionAborted
                    | ErrorKind::BrokenPipe
                    | ErrorKind::TimedOut
                    | ErrorKind::Interrupted
            ) {
                return true;
            }
        }
        if let Some(http) = cause.downcast_ref::<reqwest::Error>() {
            if http.is_timeout() || http.is_connect() || http.is_request() || http.is_body() || http.is_decode() {
                return true;
            }
            if matches!(http.status().map(|status| status.as_u16()), Some(408 | 429 | 500 | 502 | 503 | 504)) {
                return true;
            }
        }
        if cause.to_string().contains("end of file before message length reached") {
            return true;
        }
    }
    false
}

fn display_name(path: &Path) -> String {
    path.file_name().map(|name| name.to_string_lossy().into_owned()).unwrap_or_else(|| path.display().to_string())
}

fn cache_key(expect_sha256: Option<&str>, expect_sha512: Option<&str>) -> Option<String> {
    let key = expect_sha256.or(expect_sha512)?.trim();
    let hashed = (key.len() == 64 || key.len() == 128) && key.bytes().all(|byte| byte.is_ascii_hexdigit());
    if hashed { Some(key.to_ascii_lowercase()) } else { None }
}

fn hash_file(path: &Path) -> Result<(String, String)> {
    let mut file = fs::File::open(path).with_context(|| format!("hash {}", path.display()))?;
    let mut sha256 = Sha256::new();
    let mut sha512 = Sha512::new();
    let mut buffer = [0u8; 64 * 1024];
    loop {
        let count = file.read(&mut buffer).with_context(|| format!("hash {}", path.display()))?;
        if count == 0 {
            break;
        }
        sha256.update(&buffer[..count]);
        sha512.update(&buffer[..count]);
    }
    Ok((format!("{:x}", sha256.finalize()), format!("{:x}", sha512.finalize())))
}

fn digests_match(sha256: &str, sha512: &str, expect_sha256: Option<&str>, expect_sha512: Option<&str>) -> bool {
    if expect_sha256.is_none() && expect_sha512.is_none() {
        return false;
    }
    let sha256_ok = expect_sha256.map(|expected| sha256.eq_ignore_ascii_case(expected.trim())).unwrap_or(true);
    let sha512_ok = expect_sha512.map(|expected| sha512.eq_ignore_ascii_case(expected.trim())).unwrap_or(true);
    sha256_ok && sha512_ok
}

/// Copies `source` onto `dest` when `source` is a regular file whose digest
/// matches the expected hash. Returns the file's SHA-256.
fn reuse_hashed_file(source: &Path, dest: &Path, expect_sha256: Option<&str>, expect_sha512: Option<&str>) -> Result<Option<String>> {
    if !source.is_file() {
        return Ok(None);
    }
    let (sha256, sha512) = hash_file(source)?;
    if !digests_match(&sha256, &sha512, expect_sha256, expect_sha512) {
        return Ok(None);
    }
    if source != dest {
        if let Some(parent) = dest.parent() {
            fs::create_dir_all(parent)?;
        }
        if dest.exists() {
            fs::remove_file(dest)?;
        }
        if fs::hard_link(source, dest).is_err() {
            fs::copy(source, dest).with_context(|| format!("reuse {}", source.display()))?;
        }
    }
    Ok(Some(sha256))
}

fn remember_cache(cache: &Path, source: &Path, expect_sha256: Option<&str>, expect_sha512: Option<&str>) -> Result<()> {
    let Some(key) = cache_key(expect_sha256, expect_sha512) else { return Ok(()) };
    fs::create_dir_all(cache)?;
    let cached = cache.join(key);
    if cached == source {
        return Ok(());
    }
    if cached.exists() {
        let _ = fs::remove_file(&cached);
    }
    if fs::hard_link(source, &cached).is_err() {
        fs::copy(source, &cached).with_context(|| format!("cache {}", source.display()))?;
    }
    Ok(())
}

fn partial_path(dest: &Path) -> PathBuf {
    let mut name = dest.as_os_str().to_owned();
    name.push(".partial");
    PathBuf::from(name)
}

fn read_download_text(
    url: &str,
    deadline: Instant,
    progress: &mut dyn FnMut(&str),
    log: &mut dyn FnMut(&str) -> Result<()>,
) -> Result<String> {
    let mut have = Vec::new();
    let mut last_error = None;
    for attempt in 1..=DOWNLOAD_ATTEMPTS {
        match read_download_text_more(url, deadline, &mut have) {
            Ok(()) => {
                if have.len() > 128 * 1024 * 1024 {
                    bail!("download {url} exceeds 128 MiB");
                }
                return Ok(String::from_utf8_lossy(&have).into_owned());
            }
            Err(error) if attempt < DOWNLOAD_ATTEMPTS && download_interrupted(&error) => {
                note(progress, log, &format!("Download interrupted ({}); retrying {url}", error.root_cause()))?;
                pause_before_retry(attempt, deadline)?;
                last_error = Some(error);
            }
            Err(error) => return Err(error),
        }
    }
    Err(last_error.expect("an interrupted download keeps its last error"))
}

fn read_download_text_more(url: &str, deadline: Instant, have: &mut Vec<u8>) -> Result<()> {
    ensure_time(deadline)?;
    let opened = open_download(url, deadline, have.len() as u64)?;
    if opened.start_at == 0 {
        have.clear();
    }
    let mut response = opened.response;
    let body_len = response.content_length();
    let mut buffer = [0u8; 64 * 1024];
    let mut body_got = 0u64;
    loop {
        if Instant::now() >= deadline {
            bail!("local setup timed out");
        }
        // A read error still leaves the bytes already copied into `have`, so the
        // next attempt can ask for the remainder. `read_body` would drop them.
        if body_len.is_some_and(|n| body_got >= n) {
            break;
        }
        let room = (128 * 1024 * 1024usize).saturating_sub(have.len());
        if room == 0 {
            bail!("download {url} exceeds 128 MiB");
        }
        let want = body_len.map(|n| buffer.len().min((n - body_got) as usize)).unwrap_or(buffer.len()).min(room);
        let count = response.read(&mut buffer[..want]).with_context(|| format!("read {url}"))?;
        if count == 0 {
            break;
        }
        have.extend_from_slice(&buffer[..count]);
        body_got += count as u64;
    }
    if let Some(n) = body_len {
        if body_got != n {
            return Err(std::io::Error::new(
                ErrorKind::UnexpectedEof,
                format!("download {url} ended after {} bytes, expected {}", have.len(), have.len() - body_got as usize + n as usize),
            )
            .into());
        }
    }
    ensure_time(deadline)?;
    Ok(())
}

fn first_sha256(text: &str) -> Option<String> {
    let bytes = text.as_bytes();
    let mut index = 0;
    while index + 64 <= bytes.len() {
        let window = &bytes[index..index + 64];
        let hex = window.iter().all(|byte| byte.is_ascii_hexdigit());
        let before = index == 0 || !bytes[index - 1].is_ascii_hexdigit();
        let after = index + 64 == bytes.len() || !bytes[index + 64].is_ascii_hexdigit();
        if hex && before && after {
            return Some(text[index..index + 64].to_ascii_lowercase());
        }
        index += 1;
    }
    None
}

/// The unique part goes first so the name keeps its extension: ShellExecute needs `.exe` to start the VS bootstrapper.
fn scratch_file(root: &Path, name: &str) -> PathBuf {
    root.join("tmp").join(format!("{}-{}-{name}", std::process::id(), uuid::Uuid::new_v4()))
}

#[derive(Debug)]
struct SdkArchive {
    url: String,
    sha512: String,
}

#[derive(Deserialize)]
struct DotnetReleases {
    #[serde(default)]
    releases: Vec<DotnetRelease>,
}
#[derive(Deserialize)]
struct DotnetRelease {
    sdk: Option<DotnetSdk>,
}
#[derive(Deserialize)]
struct DotnetSdk {
    #[serde(default)]
    files: Vec<DotnetFile>,
}
#[derive(Deserialize)]
struct DotnetFile {
    name: Option<String>,
    url: Option<String>,
    hash: Option<String>,
}

fn select_dotnet_sdk(document: &str) -> Result<SdkArchive> {
    let parsed: DotnetReleases = serde_json::from_str(document).context("invalid .NET release metadata")?;
    for release in &parsed.releases {
        let Some(sdk) = &release.sdk else { continue };
        for file in &sdk.files {
            if file.name.as_deref() != Some("dotnet-sdk-win-x64.zip") {
                continue;
            }
            let url = file.url.clone().context(".NET metadata is missing the Windows SDK zip URL")?;
            let hash = file.hash.as_deref().unwrap_or("").trim().to_ascii_lowercase();
            if hash.len() != 128 || !hash.bytes().all(|byte| byte.is_ascii_hexdigit()) {
                bail!(".NET metadata did not provide a SHA-512 for {url}");
            }
            return Ok(SdkArchive { url, sha512: hash });
        }
    }
    bail!(".NET 8 metadata contains no Windows x64 SDK zip");
}

#[derive(Debug)]
struct MongoArchiveChoice {
    version: String,
    url: String,
    sha256: Option<String>,
}

#[derive(Deserialize)]
struct MongoCatalog {
    #[serde(default)]
    versions: Vec<MongoVersion>,
}
#[derive(Deserialize)]
struct MongoVersion {
    version: String,
    #[serde(default)]
    downloads: Vec<MongoDownload>,
}
#[derive(Deserialize)]
struct MongoDownload {
    target: Option<String>,
    arch: Option<String>,
    edition: Option<String>,
    archive: Option<MongoArchiveMeta>,
}
#[derive(Deserialize)]
struct MongoArchiveMeta {
    url: Option<String>,
    sha256: Option<String>,
}

fn select_mongo_archive(document: &str) -> Result<MongoArchiveChoice> {
    let catalog: MongoCatalog = serde_json::from_str(document).context("invalid MongoDB download metadata")?;
    let mut matches: Vec<&MongoVersion> = catalog.versions.iter().filter(|version| mongo_80(&version.version)).collect();
    if matches.is_empty() {
        bail!("MongoDB metadata contains no stable 8.0 release.");
    }
    matches.sort_by(|left, right| mongo_parts(&right.version).cmp(&mongo_parts(&left.version)));
    let version = matches[0];
    let mut downloads: Vec<&MongoDownload> = version.downloads.iter().filter(|download| mongo_windows_zip(download)).collect();
    if downloads.is_empty() {
        bail!("MongoDB metadata contains no Windows x64 Community ZIP for {}.", version.version);
    }
    downloads.sort_by_key(|download| if download.edition.as_deref() == Some("community") { 0 } else { 1 });
    let chosen = downloads[0];
    let url = chosen.archive.as_ref().and_then(|archive| archive.url.clone()).context("MongoDB archive URL is missing")?;
    let sha256 = chosen.archive.as_ref().and_then(|archive| archive.sha256.as_deref()).map(str::trim).filter(|value| value.len() == 64 && value.bytes().all(|byte| byte.is_ascii_hexdigit())).map(|value| value.to_ascii_lowercase());
    Ok(MongoArchiveChoice { version: version.version.clone(), url, sha256 })
}

fn mongo_80(version: &str) -> bool {
    let mut parts = version.split('.');
    let Some(major) = parts.next() else { return false };
    let Some(minor) = parts.next() else { return false };
    let Some(patch) = parts.next() else { return false };
    parts.next().is_none() && major == "8" && minor == "0" && !patch.is_empty() && patch.bytes().all(|byte| byte.is_ascii_digit())
}

fn mongo_parts(version: &str) -> Vec<u32> {
    version.split('.').filter_map(|part| part.parse().ok()).collect()
}

fn mongo_windows_zip(download: &MongoDownload) -> bool {
    download.target.as_deref() == Some("windows")
        && download.arch.as_deref() == Some("x86_64")
        && matches!(download.edition.as_deref(), Some("base" | "community"))
        && download.archive.as_ref().and_then(|archive| archive.url.as_deref()).is_some_and(|url| url.to_ascii_lowercase().ends_with(".zip"))
}

fn pem_certificates(text: &str) -> Result<Vec<Vec<u8>>> {
    let mut certs = Vec::new();
    let mut rest = text;
    const BEGIN: &str = "-----BEGIN CERTIFICATE-----";
    const END: &str = "-----END CERTIFICATE-----";
    while let Some(start) = rest.find(BEGIN) {
        let after = &rest[start + BEGIN.len()..];
        let end = after.find(END).context("truncated certificate")?;
        certs.push(decode_base64(&after[..end])?);
        rest = &after[end + END.len()..];
    }
    Ok(certs)
}

fn decode_base64(input: &str) -> Result<Vec<u8>> {
    let stripped: String = input.chars().filter(|character| !character.is_whitespace()).collect();
    base64::engine::general_purpose::STANDARD.decode(stripped).context("invalid certificate encoding")
}

fn parse_environment_block(text: &str) -> Vec<(String, String)> {
    let mut pairs = Vec::new();
    for line in text.split('\n') {
        let line = line.trim_end_matches('\r');
        let Some((key, value)) = line.split_once('=') else { continue };
        if key.is_empty() || key.contains('\0') {
            continue;
        }
        pairs.push((key.to_owned(), value.to_owned()));
    }
    pairs
}

fn rust_payload_relative(path: &Path) -> Option<PathBuf> {
    let mut components = path.components();
    components.next()?;
    components.next()?;
    let relative: PathBuf = components.collect();
    if relative.as_os_str().is_empty() || relative.components().any(|component| matches!(component, Component::ParentDir | Component::RootDir | Component::Prefix(_))) {
        return None;
    }
    Some(relative)
}

fn apply_network(config: &mut Value, game_port: u16, mongo_port: u16) {
    if !config.is_object() {
        *config = Value::Object(serde_json::Map::new());
    }
    let object = config.as_object_mut().expect("config object");
    let game = object.entry("GameServer").or_insert_with(|| Value::Object(serde_json::Map::new()));
    if !game.is_object() {
        *game = Value::Object(serde_json::Map::new());
    }
    game["Host"] = Value::String("127.0.0.1".into());
    game["Port"] = Value::from(game_port);
    let database = object.entry("Database").or_insert_with(|| Value::Object(serde_json::Map::new()));
    if !database.is_object() {
        *database = Value::Object(serde_json::Map::new());
    }
    database["Host"] = Value::String("127.0.0.1".into());
    database["Port"] = Value::from(mongo_port);
    database["Name"] = Value::String("asc_net".into());
}

fn network_matches(config: &Value, game_port: u16, mongo_port: u16) -> bool {
    json_string(config, &["GameServer", "Host"]).as_deref() == Some("127.0.0.1")
        && json_u16(config, &["GameServer", "Port"]) == Some(game_port)
        && json_string(config, &["Database", "Host"]).as_deref() == Some("127.0.0.1")
        && json_u16(config, &["Database", "Port"]) == Some(mongo_port)
        && json_string(config, &["Database", "Name"]).as_deref() == Some("asc_net")
}

/// Copies the persistent `config.json` into the server build before each start, so edits such as
/// `VerboseLevel` apply on the next start instead of only after the next rebuild. Network fields are
/// owned by Setup and must still match the build.
pub(crate) fn install_runtime_config(root: &Path, server_directory: &Path, game_port: u16, mongo_port: u16) -> Result<()> {
    let source = root.join("config.json");
    let bytes = fs::read(&source).with_context(|| format!("read {}", source.display()))?;
    let config: Value = serde_json::from_slice(&bytes).with_context(|| format!("{} is not valid JSON", source.display()))?;
    if !network_matches(&config, game_port, mongo_port) {
        bail!("{} has changed GameServer/Database settings; restore them or run Setup again", source.display());
    }
    let target = server_directory.join("Configs").join("config.json");
    if fs::read(&target).ok().as_deref() != Some(bytes.as_slice()) {
        write_atomic(&target, &bytes)?;
    }
    Ok(())
}

fn json_string(value: &Value, path: &[&str]) -> Option<String> {
    let mut current = value;
    for key in path {
        current = current.get(*key)?;
    }
    current.as_str().map(str::to_owned)
}

fn json_u16(value: &Value, path: &[&str]) -> Option<u16> {
    let mut current = value;
    for key in path {
        current = current.get(*key)?;
    }
    let number = current.as_u64()?;
    u16::try_from(number).ok().filter(|port| *port >= 1)
}

fn free_port(excluded: &[u16]) -> Result<u16> {
    for _ in 0..64 {
        let listener = TcpListener::bind((Ipv4Addr::LOCALHOST, 0)).context("allocate a free loopback port")?;
        let port = listener.local_addr()?.port();
        drop(listener);
        if port != 0 && !excluded.contains(&port) {
            return Ok(port);
        }
    }
    bail!("could not allocate a free loopback port");
}

fn assert_port_free(port: u16, label: &str) -> Result<()> {
    if port == 0 {
        bail!("Persisted ports must each be between 1 and 65535.");
    }
    match TcpListener::bind((Ipv4Addr::LOCALHOST, port)) {
        Ok(listener) => {
            drop(listener);
            Ok(())
        }
        Err(_) => bail!("{label} port {port} is already in use. Stop that process or preserve the current build and choose new ports; setup will not adopt or stop it."),
    }
}

fn find_named(dir: &Path, name: &str) -> Result<Option<PathBuf>> {
    if !dir.exists() {
        return Ok(None);
    }
    let mut stack = vec![dir.to_path_buf()];
    while let Some(current) = stack.pop() {
        for entry in fs::read_dir(&current).with_context(|| format!("read {}", current.display()))? {
            let entry = entry?;
            let path = entry.path();
            let matches = path.file_name().and_then(|file| file.to_str()).is_some_and(|file| file.eq_ignore_ascii_case(name));
            if matches && path.is_file() {
                return Ok(Some(path));
            }
            if path.is_dir() {
                stack.push(path);
            }
        }
    }
    Ok(None)
}

fn directory_has_child(dir: &Path) -> Result<bool> {
    if !dir.is_dir() {
        return Ok(false);
    }
    Ok(fs::read_dir(dir)?.next().is_some())
}

fn copy_dir(from: &Path, to: &Path) -> Result<()> {
    fs::create_dir_all(to)?;
    for entry in fs::read_dir(from)? {
        let entry = entry?;
        let dest = to.join(entry.file_name());
        let kind = entry.file_type()?;
        if kind.is_dir() {
            copy_dir(&entry.path(), &dest)?;
        } else if kind.is_file() {
            fs::copy(entry.path(), &dest)?;
        }
    }
    Ok(())
}

fn write_atomic(path: &Path, bytes: &[u8]) -> Result<()> {
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent)?;
    }
    let temporary = partial_path(path);
    {
        let mut file = fs::File::create(&temporary)?;
        file.write_all(bytes)?;
        file.sync_all()?;
    }
    #[cfg(windows)]
    {
        crate::local::atomic_replace(&temporary, path)?;
    }
    #[cfg(not(windows))]
    {
        fs::rename(&temporary, path)?;
    }
    Ok(())
}

fn quoted_response_file(path: &Path) -> String {
    // cl.exe and link.exe treat these quotes as @file syntax, not as Windows
    // argument quotes. Escaping them makes Wine search for a name that
    // includes the quote characters (D8022). Pass the string with raw_arg.
    let text = path.display().to_string().replace('\\', "/");
    format!("@\"{text}\"")
}

fn env_key_eq(left: &str, right: &str) -> bool {
    left.eq_ignore_ascii_case(right)
}

fn env_set(env: &mut Vec<(String, String)>, key: &str, value: &str) {
    if let Some((_, existing)) = env.iter_mut().find(|(name, _)| env_key_eq(name, key)) {
        *existing = value.to_owned();
        return;
    }
    env.push((key.to_owned(), value.to_owned()));
}

/// Build the environment for cargo, cl, and link.
///
/// A vcvars `set` dump is already a complete environment, so it replaces the
/// parent block. The portable toolchain list is only the compiler overlay, and
/// clearing the process environment there drops SystemRoot, TEMP, and the user
/// profile those tools use when the dump is absent.
fn compiler_process_env(
    inherited: &[(String, String)],
    compiler_vars: &[(String, String)],
    toolchain_bin: &str,
    rustc: &str,
    wine: bool,
    full_environment: bool,
) -> Vec<(String, String)> {
    let mut env = Vec::new();
    if !full_environment {
        for (key, value) in inherited {
            if env_key_eq(key, "RUSTUP_TOOLCHAIN") || env_key_eq(key, "RUSTC") || env_key_eq(key, "Path") {
                continue;
            }
            env_set(&mut env, key, value);
        }
    }
    let mut compiler_path = String::new();
    let mut path_key = "Path".to_owned();
    for (key, value) in compiler_vars {
        if env_key_eq(key, "Path") {
            path_key = key.clone();
            compiler_path = value.clone();
            continue;
        }
        if env_key_eq(key, "RUSTUP_TOOLCHAIN") || env_key_eq(key, "RUSTC") {
            continue;
        }
        env_set(&mut env, key, value);
    }
    let mut parts = Vec::new();
    if !toolchain_bin.is_empty() {
        parts.push(toolchain_bin.to_owned());
    }
    if !compiler_path.is_empty() {
        parts.push(compiler_path);
    }
    if !full_environment {
        if let Some((_, inherited_path)) = inherited.iter().find(|(key, _)| env_key_eq(key, "Path")) {
            if !inherited_path.is_empty() {
                parts.push(inherited_path.clone());
            }
        }
    }
    env_set(&mut env, &path_key, &parts.join(";"));
    env_set(&mut env, "RUSTC", rustc);
    if wine {
        env_set(&mut env, "CARGO_BUILD_JOBS", "1");
    }
    env
}

fn quote_windows_arg(value: &str) -> String {
    let mut output = String::from("\"");
    let mut slashes = 0;
    for character in value.chars() {
        if character == '\\' {
            slashes += 1;
            continue;
        }
        output.extend(std::iter::repeat('\\').take(if character == '"' { slashes * 2 + 1 } else { slashes }));
        output.push(character);
        slashes = 0;
    }
    output.extend(std::iter::repeat('\\').take(slashes * 2));
    output.push('"');
    output
}

#[cfg(windows)]
thread_local! {
    static SETUP_JOB: std::cell::RefCell<Option<crate::local::JobHandle>> = const { std::cell::RefCell::new(None) };
}

#[cfg(windows)]
struct JobGuard;
#[cfg(windows)]
impl Drop for JobGuard {
    fn drop(&mut self) {
        SETUP_JOB.with(|slot| {
            slot.borrow_mut().take();
        });
    }
}

#[cfg(windows)]
fn run_windows(
    root: &Path,
    repository: &str,
    branch: &str,
    launcher_dir: &Path,
    progress: &mut dyn FnMut(&str),
    log: &mut dyn FnMut(&str) -> Result<()>,
    deadline: Instant,
) -> Result<()> {
    let job = crate::local::create_job()?;
    SETUP_JOB.with(|slot| *slot.borrow_mut() = Some(job));
    let _guard = JobGuard;
    ensure_time(deadline)?;
    fs::create_dir_all(root.join("tmp"))?;
    let checkout = root.join("checkout");
    let git = ensure_git(root, deadline, progress, log)?;
    note(progress, log, "Updating source checkout")?;
    update_checkout(&git, &checkout, repository, branch, deadline, progress, log)?;
    let revision = git_text(&git, Some(&checkout), &["rev-parse".into(), "HEAD".into()], "git rev-parse HEAD", deadline, progress, log)?;
    if revision.len() != 40 || !revision.bytes().all(|byte| byte.is_ascii_hexdigit()) {
        bail!("git returned an invalid revision");
    }
    let previous = read_previous_state(&root.join("build-state.json"))?;
    ensure_time(deadline)?;
    let dotnet = ensure_dotnet(root, deadline, progress, log)?;
    let rust = ensure_rust(root, deadline, progress, log)?;
    let tools = ensure_compiler(root, deadline, progress, log)?;
    let tool_dir = root.join("tools");
    fs::create_dir_all(&tool_dir)?;
    let mongod = ensure_mongo(root, &tool_dir, previous.as_ref(), deadline, progress, log)?;
    fs::create_dir_all(root.join("data").join("mongo"))?;
    fs::create_dir_all(root.join("logs"))?;
    let (sdk_port, game_port, mongo_port) = choose_ports(root, previous.as_ref())?;
    let config_path = root.join("config.json");
    prepare_config(&checkout, &config_path, game_port, mongo_port)?;
    let build_root = root.join("build");
    fs::create_dir_all(&build_root)?;
    let final_dir = build_root.join(&revision);
    let stage = build_root.join(format!("{revision}.tmp-{}", std::process::id()));
    // A cached build is reused only while its whole patch package is present: antivirus software can quarantine
    // version.dll after the build, which left every later Setup failing on the same missing file.
    let complete = final_dir.join("server").join("AscNet.dll").is_file()
        && crate::package::load_package(&final_dir.join("patch")).is_ok();
    if !complete && final_dir.exists() {
        note(progress, log, &format!("Cached build {revision} is incomplete; rebuilding it"))?;
    }
    if !complete {
        if stage.exists() {
            fs::remove_dir_all(&stage)?;
        }
        fs::create_dir_all(&stage)?;
        let built = (|| -> Result<()> {
            publish_server(&dotnet, &checkout, &stage, &config_path, deadline, progress, log)?;
            build_patch(&rust, &tools, &checkout, root, &stage, deadline, progress, log)?;
            build_version_shim(&tools, &checkout, &stage, launcher_dir, deadline, progress, log)?;
            let _ = fs::remove_dir_all(stage.join("dotnet-artifacts"));
            let _ = fs::remove_dir_all(stage.join("loader"));
            let _ = fs::remove_dir_all(stage.join("loader-obj"));
            if final_dir.exists() {
                fs::remove_dir_all(&final_dir)?;
            }
            fs::rename(&stage, &final_dir).context("move staged build into place")?;
            Ok(())
        })();
        if built.is_err() {
            let _ = fs::remove_dir_all(&stage);
        }
        built?;
    }
    let server_directory = std::path::absolute(final_dir.join("server"))?;
    let patch_directory = std::path::absolute(final_dir.join("patch"))?;
    let state = crate::local::LocalBuild {
        schema_version: crate::local::SCHEMA_VERSION,
        revision,
        repository: repository.to_owned(),
        dotnet: std::path::absolute(dotnet)?,
        mongod: std::path::absolute(mongod)?,
        server_directory: server_directory.clone(),
        resource_directory: server_directory,
        patch_directory,
        sdk_port,
        game_port,
        mongo_port,
    };
    let mut bytes = serde_json::to_vec_pretty(&state)?;
    bytes.push(b'\n');
    let pending = root.join("build-state.pending.json");
    write_atomic(&pending, &bytes)?;
    note(progress, log, &format!("AscNet local build prepared for launcher validation: {}", state.revision))?;
    note(progress, log, &format!("Pending state: {}", pending.display()))?;
    Ok(())
}

#[cfg(windows)]
fn read_previous_state(path: &Path) -> Result<Option<Value>> {
    match fs::read(path) {
        Ok(bytes) => serde_json::from_slice(&bytes).with_context(|| format!("Existing build-state.json is invalid: {}", path.display())).map(Some),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(None),
        Err(error) => Err(error).with_context(|| format!("read {}", path.display())),
    }
}

#[cfg(windows)]
fn ensure_git(root: &Path, deadline: Instant, progress: &mut dyn FnMut(&str), log: &mut dyn FnMut(&str) -> Result<()>) -> Result<PathBuf> {
    if let Some(git) = crate::local::git_executable() {
        return Ok(git);
    }
    note(progress, log, "Installing portable Git")?;
    let archive = scratch_file(root, "mingit.zip");
    download_file(MINGIT_URL, &archive, Some(MINGIT_SHA256), None, root, deadline, progress, log)?;
    let destination = root.join("tools").join("git");
    if destination.exists() {
        fs::remove_dir_all(&destination)?;
    }
    fs::create_dir_all(&destination)?;
    zip::ZipArchive::new(fs::File::open(&archive)?).context("open MinGit archive")?.extract(&destination).context("extract MinGit")?;
    let _ = fs::remove_file(&archive);
    let config = destination.join("etc").join("gitconfig");
    if let Ok(text) = fs::read_to_string(&config) {
        fs::write(&config, without_program_files_git_include(&text))?;
    }
    let git = destination.join("cmd").join("git.exe");
    if !git.is_file() {
        bail!("MinGit archive did not contain cmd\\git.exe");
    }
    let mut version = Command::new(&git);
    version.arg("--version").env("GIT_TERMINAL_PROMPT", "0");
    capture_command(&mut version, "git --version", deadline, false, progress, log)?;
    Ok(git)
}

#[cfg(windows)]
fn dotnet_candidates(root: &Path) -> Vec<PathBuf> {
    let mut candidates = Vec::new();
    if let Some(path) = env::var_os("PATH") {
        candidates.extend(env::split_paths(&path).map(|dir| dir.join("dotnet.exe")));
    }
    if let Some(base) = env::var_os("ProgramFiles") {
        candidates.push(PathBuf::from(base).join("dotnet").join("dotnet.exe"));
    }
    candidates.push(root.join("tools").join("dotnet").join("dotnet.exe"));
    candidates
}

#[cfg(windows)]
fn dotnet_sdk8(dotnet: &Path, deadline: Instant, progress: &mut dyn FnMut(&str), log: &mut dyn FnMut(&str) -> Result<()>) -> Result<Option<String>> {
    if !dotnet.is_file() {
        return Ok(None);
    }
    let mut command = Command::new(dotnet);
    command.args(["--list-sdks"]).env("DOTNET_CLI_TELEMETRY_OPTOUT", "1").env("DOTNET_NOLOGO", "1").env("DOTNET_SKIP_FIRST_TIME_EXPERIENCE", "1");
    let output = match capture_command(&mut command, "dotnet --list-sdks", deadline, false, progress, log) {
        Ok(output) => output,
        Err(_) => return Ok(None),
    };
    let text = String::from_utf8_lossy(&output.stdout);
    Ok(text.lines().map(str::trim).find(|line| line.starts_with("8.")).and_then(|line| line.split_whitespace().next()).map(str::to_owned))
}

#[cfg(windows)]
fn ensure_dotnet(root: &Path, deadline: Instant, progress: &mut dyn FnMut(&str), log: &mut dyn FnMut(&str) -> Result<()>) -> Result<PathBuf> {
    for candidate in dotnet_candidates(root) {
        if dotnet_sdk8(&candidate, deadline, progress, log)?.is_some() {
            return Ok(std::path::absolute(candidate)?);
        }
    }
    note(progress, log, "Installing the .NET 8 SDK")?;
    let metadata = read_download_text(DOTNET_RELEASES, deadline, progress, log)?;
    let selected = select_dotnet_sdk(&metadata)?;
    let archive = scratch_file(root, "dotnet-sdk.zip");
    download_file(&selected.url, &archive, None, Some(&selected.sha512), root, deadline, progress, log)?;
    let destination = root.join("tools").join("dotnet");
    if destination.exists() {
        fs::remove_dir_all(&destination)?;
    }
    fs::create_dir_all(&destination)?;
    zip::ZipArchive::new(fs::File::open(&archive)?).context("open .NET SDK archive")?.extract(&destination).context("extract .NET SDK")?;
    let _ = fs::remove_file(&archive);
    let dotnet = destination.join("dotnet.exe");
    if dotnet_sdk8(&dotnet, deadline, progress, log)?.is_none() {
        bail!(".NET 8 SDK was installed but dotnet.exe could not be found. Restart the launcher and retry.");
    }
    Ok(std::path::absolute(dotnet)?)
}

#[cfg(windows)]
fn rust_root(root: &Path) -> PathBuf {
    root.join("tools").join(format!("rust-{RUST_VERSION}-{RUST_HOST}"))
}

/// `lib/rustlib/<host>` appears as soon as rustc is unpacked. The standard library
/// is a separate archive, so a finished install is the one that contains `libstd`.
fn rust_installation_ready(root: &Path) -> bool {
    root.join("bin").join("cargo.exe").is_file()
        && root.join("bin").join("rustc.exe").is_file()
        && rust_std_rlib(root)
}

fn rust_std_rlib(root: &Path) -> bool {
    let lib = root.join("lib").join("rustlib").join(RUST_HOST).join("lib");
    let Ok(entries) = fs::read_dir(lib) else { return false };
    entries.flatten().any(|entry| {
        let name = entry.file_name();
        let name = name.to_string_lossy();
        entry.path().is_file() && name.starts_with("libstd-") && name.ends_with(".rlib")
    })
}

fn publish_written_file(output: &Path, write: impl FnOnce(&mut fs::File) -> std::io::Result<()>) -> Result<()> {
    if let Some(parent) = output.parent() {
        fs::create_dir_all(parent)?;
    }
    let mut temporary_name = output.as_os_str().to_owned();
    temporary_name.push(".partial");
    let temporary = PathBuf::from(temporary_name);
    let result = (|| -> Result<()> {
        let mut file = fs::File::create(&temporary).with_context(|| format!("create {}", temporary.display()))?;
        write(&mut file).with_context(|| format!("write {}", output.display()))?;
        file.sync_all()?;
        drop(file);
        if output.exists() {
            fs::remove_file(output)?;
        }
        fs::rename(&temporary, output).with_context(|| format!("install {}", output.display()))?;
        Ok(())
    })();
    if result.is_err() {
        let _ = fs::remove_file(&temporary);
    }
    result
}

#[cfg(windows)]
fn ensure_rust(root: &Path, deadline: Instant, progress: &mut dyn FnMut(&str), log: &mut dyn FnMut(&str) -> Result<()>) -> Result<PathBuf> {
    let destination = rust_root(root);
    if rust_installation_ready(&destination) {
        return Ok(destination);
    }
    note(progress, log, &format!("Installing Rust {RUST_VERSION}"))?;
    if destination.exists() {
        fs::remove_dir_all(&destination)?;
    }
    let mut stage_name = destination.file_name().context("Rust install path has no file name")?.to_os_string();
    stage_name.push(".partial");
    let stage = destination.with_file_name(stage_name);
    if stage.exists() {
        fs::remove_dir_all(&stage)?;
    }
    fs::create_dir_all(&stage)?;
    let installed = (|| -> Result<()> {
        for component in RUST_COMPONENTS {
            ensure_time(deadline)?;
            let name = component.url.rsplit('/').next().unwrap_or("rust-component.tar.gz");
            let archive = scratch_file(root, name);
            download_file(component.url, &archive, Some(component.sha256), None, root, deadline, progress, log)?;
            unpack_rust_component(&archive, &stage)?;
            let _ = fs::remove_file(&archive);
        }
        if !rust_installation_ready(&stage) {
            bail!("Rust {RUST_VERSION} archive did not produce cargo.exe, rustc.exe, and the MSVC standard library");
        }
        if let Some(parent) = destination.parent() {
            fs::create_dir_all(parent)?;
        }
        fs::rename(&stage, &destination).with_context(|| format!("install Rust into {}", destination.display()))?;
        Ok(())
    })();
    if installed.is_err() {
        let _ = fs::remove_dir_all(&stage);
    }
    installed?;
    Ok(destination)
}

fn unpack_rust_component(archive: &Path, dest: &Path) -> Result<()> {
    let file = fs::File::open(archive)?;
    let decoder = flate2::read::GzDecoder::new(file);
    let mut tar = tar::Archive::new(decoder);
    for entry in tar.entries().context("read Rust component archive")? {
        let mut entry = entry?;
        let path = entry.path()?.into_owned();
        let Some(relative) = rust_payload_relative(&path) else { continue };
        if !entry.header().entry_type().is_file() {
            if entry.header().entry_type().is_dir() {
                fs::create_dir_all(dest.join(&relative))?;
            }
            continue;
        }
        let output = dest.join(&relative);
        publish_written_file(&output, |file| std::io::copy(&mut entry, file).map(|_| ()))?;
    }
    Ok(())
}

#[cfg(windows)]
struct Compiler {
    vars: Vec<(String, String)>,
    msbuild: Option<PathBuf>,
    wine: bool,
    full_environment: bool,
}

#[cfg(windows)]
fn vcvars_bat(root: &Path) -> PathBuf {
    root.join("VC").join("Auxiliary").join("Build").join("vcvars64.bat")
}

#[cfg(windows)]
fn compiler_from_vcvars(msvc: &Path, deadline: Instant, progress: &mut dyn FnMut(&str), log: &mut dyn FnMut(&str) -> Result<()>) -> Result<Compiler> {
    let bat = vcvars_bat(msvc);
    let vars = capture_vcvars(&bat, deadline, progress, log)?;
    register_windows_sdk(msvc)?;
    Ok(Compiler { vars, msbuild: None, wine: true, full_environment: true })
}

#[cfg(windows)]
fn compiler_from_tree(msvc: &Path) -> Result<Compiler> {
    let vars = crate::msvc::compiler_vars(msvc)?;
    register_windows_sdk(msvc)?;
    Ok(Compiler { vars, msbuild: None, wine: true, full_environment: false })
}

#[cfg(windows)]
fn safe_archive_name(name: &str) -> String {
    let base = name.rsplit(['/', '\\']).next().unwrap_or(name);
    let mut clean = String::new();
    for character in base.chars() {
        if character.is_ascii_alphanumeric() || matches!(character, '.' | '-' | '_') {
            clean.push(character);
        } else {
            clean.push('_');
        }
    }
    if clean.is_empty() { "package.bin".to_owned() } else { clean }
}

#[cfg(windows)]
fn install_portable_msvc(root: &Path, dest: &Path, deadline: Instant, progress: &mut dyn FnMut(&str), log: &mut dyn FnMut(&str) -> Result<()>) -> Result<()> {
    let channel = read_download_text(VS_CHANNEL, deadline, progress, log)?;
    let info = crate::msvc::channel_info(&channel)?;
    note(progress, log, &format!("Visual Studio license: {}", info.license))?;
    note(progress, log, "Installing the MSVC toolset and Windows SDK")?;
    let manifest = read_download_text(&info.manifest_url, deadline, progress, log)?;
    let plan = crate::msvc::plan(&manifest)?;
    note(progress, log, &format!("MSVC {} and Windows SDK {}", plan.toolset, plan.sdk_version))?;
    let stage = scratch_file(root, "msvc");
    let downloads = scratch_file(root, "msvc-pkg");
    fs::create_dir_all(&stage)?;
    fs::create_dir_all(&downloads)?;
    let installed = (|| -> Result<()> {
        for payload in &plan.vsix {
            ensure_time(deadline)?;
            let archive = downloads.join(safe_archive_name(&payload.file_name));
            download_file(&payload.url, &archive, Some(&payload.sha256), None, root, deadline, progress, log)?;
            crate::msvc::extract_vsix(&archive, &stage)?;
            let _ = fs::remove_file(&archive);
        }
        for name in &plan.msi_names {
            ensure_time(deadline)?;
            let payload = crate::msvc::find_payload(&plan.sdk_payloads, name)?;
            let msi_path = downloads.join(safe_archive_name(name));
            download_file(&payload.url, &msi_path, Some(&payload.sha256), None, root, deadline, progress, log)?;
            let bytes = fs::read(&msi_path).with_context(|| format!("read {}", msi_path.display()))?;
            let cabinets = crate::msvc::cabinet_names(&bytes);
            if cabinets.is_empty() {
                bail!("{name} did not reference any cabinet files");
            }
            for cabinet in &cabinets {
                let cab_payload = crate::msvc::find_payload(&plan.sdk_payloads, cabinet)?;
                let cab_path = downloads.join(safe_archive_name(cabinet));
                download_file(&cab_payload.url, &cab_path, Some(&cab_payload.sha256), None, root, deadline, progress, log)?;
            }
            note(progress, log, &format!("Unpacking {name}"))?;
            crate::msvc::extract_msi(&msi_path, &downloads, &stage)?;
            let _ = fs::remove_file(&msi_path);
        }
        crate::msvc::remove_telemetry(&stage);
        crate::msvc::compiler_vars(&stage)?;
        Ok(())
    })();
    if let Err(error) = installed {
        let _ = fs::remove_dir_all(&stage);
        let _ = fs::remove_dir_all(&downloads);
        return Err(error);
    }
    let _ = fs::remove_dir_all(&downloads);
    if dest.exists() {
        fs::remove_dir_all(dest)?;
    }
    if let Some(parent) = dest.parent() {
        fs::create_dir_all(parent)?;
    }
    fs::rename(&stage, dest).with_context(|| format!("install MSVC into {}", dest.display()))?;
    Ok(())
}

#[cfg(windows)]
fn ensure_compiler(root: &Path, deadline: Instant, progress: &mut dyn FnMut(&str), log: &mut dyn FnMut(&str) -> Result<()>) -> Result<Compiler> {
    let wine = crate::install::running_under_wine();
    if wine {
        if let Some(msvc) = env::var_os("ASCNET_MSVC") {
            let msvc = PathBuf::from(msvc);
            let bat = vcvars_bat(&msvc);
            if !bat.is_file() {
                bail!("ASCNET_MSVC is set but {} is missing", bat.display());
            }
            return compiler_from_vcvars(&msvc, deadline, progress, log);
        }
        let preset = PathBuf::from(r"C:\msvc");
        if vcvars_bat(&preset).is_file() {
            return compiler_from_vcvars(&preset, deadline, progress, log);
        }
        let bundled = root.join("tools").join("msvc");
        if !crate::msvc::is_ready(&bundled) {
            install_portable_msvc(root, &bundled, deadline, progress, log)?;
        }
        return compiler_from_tree(&bundled);
    }
    if let Some(found) = locate_vs(deadline, progress, log)? {
        return vs_or_portable_compiler(root, found, deadline, progress, log);
    }
    note(progress, log, "Installing Visual Studio 2022 Build Tools")?;
    install_build_tools(root, None, deadline, progress, log)?;
    let found = locate_vs(deadline, progress, log)?.context("Visual Studio Build Tools C++ workload was installed but MSBuild with v143 C++ tools was not found. Restart the launcher and retry.")?;
    vs_or_portable_compiler(root, found, deadline, progress, log)
}

/// A Visual Studio install with the C++ tools but no Windows SDK passes vswhere and vcvars, then fails
/// every link with LNK1181 kernel32.lib. Use the launcher's own MSVC toolset and Windows SDK instead.
#[cfg(windows)]
fn vs_or_portable_compiler(root: &Path, found: VsInstall, deadline: Instant, progress: &mut dyn FnMut(&str), log: &mut dyn FnMut(&str) -> Result<()>) -> Result<Compiler> {
    let vars = capture_vcvars(&found.bat, deadline, progress, log)?;
    if lib_has_windows_sdk(&vars) {
        return Ok(Compiler { vars, msbuild: Some(found.msbuild), wine: false, full_environment: true });
    }
    note(progress, log, &format!("{} has no Windows SDK (kernel32.lib is not on LIB); using the launcher's own MSVC toolset and Windows SDK", found.bat.display()))?;
    let bundled = root.join("tools").join("msvc");
    if !crate::msvc::is_ready(&bundled) {
        install_portable_msvc(root, &bundled, deadline, progress, log)?;
    }
    Ok(Compiler { vars: crate::msvc::compiler_vars(&bundled)?, msbuild: None, wine: false, full_environment: false })
}

/// True when a LIB directory from the compiler environment holds the Windows SDK's kernel32.lib.
fn lib_has_windows_sdk(vars: &[(String, String)]) -> bool {
    vars.iter()
        .filter(|(key, _)| env_key_eq(key, "LIB"))
        .flat_map(|(_, value)| value.split(';'))
        .map(str::trim)
        .filter(|dir| !dir.is_empty())
        .any(|dir| Path::new(dir).join("kernel32.lib").is_file())
}

#[cfg(windows)]
struct VsInstall {
    bat: PathBuf,
    msbuild: PathBuf,
}

#[cfg(windows)]
fn vswhere_path() -> Option<PathBuf> {
    ["ProgramFiles(x86)", "ProgramFiles"].into_iter().filter_map(env::var_os).map(PathBuf::from).map(|base| base.join("Microsoft Visual Studio").join("Installer").join("vswhere.exe")).find(|path| path.is_file())
}

#[cfg(windows)]
fn vswhere_text(vswhere: &Path, args: &[&str], deadline: Instant, progress: &mut dyn FnMut(&str), log: &mut dyn FnMut(&str) -> Result<()>) -> Result<String> {
    let mut command = Command::new(vswhere);
    command.args(args);
    let output = capture_command(&mut command, "vswhere", deadline, false, progress, log)?;
    Ok(String::from_utf8_lossy(&output.stdout).trim().to_owned())
}

#[cfg(windows)]
fn locate_vs(deadline: Instant, progress: &mut dyn FnMut(&str), log: &mut dyn FnMut(&str) -> Result<()>) -> Result<Option<VsInstall>> {
    let Some(vswhere) = vswhere_path() else { return Ok(None) };
    let install = vswhere_text(&vswhere, &["-latest", "-version", "[17.0,18.0)", "-products", "*", "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64", "-property", "installationPath"], deadline, progress, log)?;
    let install = install.lines().next().unwrap_or("").trim();
    if !install.is_empty() {
        let root = PathBuf::from(install);
        let bat = root.join("VC").join("Auxiliary").join("Build").join("vcvars64.bat");
        let msbuild = root.join("MSBuild").join("Current").join("Bin").join("MSBuild.exe");
        if bat.is_file() && msbuild.is_file() {
            return Ok(Some(VsInstall { bat, msbuild }));
        }
        let found = vswhere_text(&vswhere, &["-latest", "-version", "[17.0,18.0)", "-products", "*", "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64", "-find", r"MSBuild\**\Bin\MSBuild.exe"], deadline, progress, log)?;
        if let Some(msbuild) = found.lines().map(str::trim).find(|line| !line.is_empty()).map(PathBuf::from) {
            if bat.is_file() && msbuild.is_file() {
                return Ok(Some(VsInstall { bat, msbuild }));
            }
        }
    }
    let build_tools = vswhere_text(&vswhere, &["-latest", "-version", "[17.0,18.0)", "-products", "Microsoft.VisualStudio.Product.BuildTools", "-property", "installationPath"], deadline, progress, log)?;
    let build_tools = build_tools.lines().next().unwrap_or("").trim();
    if !build_tools.is_empty() {
        let setup = vswhere.parent().context("vswhere has no directory")?.join("setup.exe");
        if !setup.is_file() {
            bail!("Visual Studio Installer is missing; repair it before adding the C++ workload.");
        }
        install_build_tools_at(&setup, Some(PathBuf::from(build_tools)), deadline, progress, log)?;
        return locate_vs_installed(&vswhere, deadline, progress, log);
    }
    Ok(None)
}

#[cfg(windows)]
fn locate_vs_installed(vswhere: &Path, deadline: Instant, progress: &mut dyn FnMut(&str), log: &mut dyn FnMut(&str) -> Result<()>) -> Result<Option<VsInstall>> {
    let install = vswhere_text(vswhere, &["-latest", "-version", "[17.0,18.0)", "-products", "*", "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64", "-property", "installationPath"], deadline, progress, log)?;
    let install = install.lines().next().unwrap_or("").trim();
    if install.is_empty() {
        return Ok(None);
    }
    let root = PathBuf::from(install);
    let bat = root.join("VC").join("Auxiliary").join("Build").join("vcvars64.bat");
    let mut msbuild = root.join("MSBuild").join("Current").join("Bin").join("MSBuild.exe");
    if !msbuild.is_file() {
        let found = vswhere_text(vswhere, &["-latest", "-version", "[17.0,18.0)", "-products", "*", "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64", "-find", r"MSBuild\**\Bin\MSBuild.exe"], deadline, progress, log)?;
        if let Some(path) = found.lines().map(str::trim).find(|line| !line.is_empty()) {
            msbuild = PathBuf::from(path);
        }
    }
    if bat.is_file() && msbuild.is_file() {
        Ok(Some(VsInstall { bat, msbuild }))
    } else {
        Ok(None)
    }
}

#[cfg(windows)]
fn install_build_tools(root: &Path, install_path: Option<PathBuf>, deadline: Instant, progress: &mut dyn FnMut(&str), log: &mut dyn FnMut(&str) -> Result<()>) -> Result<()> {
    let bootstrapper = scratch_file(root, "vs_buildtools.exe");
    let hash = download_file(VS_BOOTSTRAPPER, &bootstrapper, None, None, root, deadline, progress, log)?;
    note(progress, log, &format!("Visual Studio Build Tools bootstrapper SHA-256 {hash}"))?;
    install_build_tools_at(&bootstrapper, install_path, deadline, progress, log)?;
    let _ = fs::remove_file(&bootstrapper);
    Ok(())
}

#[cfg(windows)]
fn install_build_tools_at(program: &Path, install_path: Option<PathBuf>, deadline: Instant, progress: &mut dyn FnMut(&str), log: &mut dyn FnMut(&str) -> Result<()>) -> Result<()> {
    note(progress, log, "Checking the Visual Studio installer signature")?;
    verify_microsoft_authenticode(program)?;
    let mut args = Vec::new();
    if let Some(path) = install_path {
        args.extend(["modify".into(), "--installPath".into(), path.display().to_string()]);
    }
    args.extend(["--wait".into(), "--passive".into(), "--norestart".into(), "--add".into(), "Microsoft.VisualStudio.Workload.VCTools".into(), "--includeRecommended".into()]);
    let parameters = args.iter().map(|arg| quote_windows_arg(arg)).collect::<Vec<_>>().join(" ");
    let code = elevate_and_wait(program, &parameters, deadline, progress, log)?;
    if code == 3010 {
        bail!("Visual Studio C++ workload was added but Windows must restart before setup can continue.");
    }
    if code != 0 {
        bail!("Adding Visual Studio 2022 C++ workload failed with exit code {code}.");
    }
    Ok(())
}

#[cfg(windows)]
fn elevate_and_wait(program: &Path, parameters: &str, deadline: Instant, progress: &mut dyn FnMut(&str), log: &mut dyn FnMut(&str) -> Result<()>) -> Result<u32> {
    use std::os::windows::ffi::OsStrExt;
    use windows::core::{w, PCWSTR};
    use windows::Win32::Foundation::{CloseHandle, WAIT_OBJECT_0, ERROR_CANCELLED};
    use windows::Win32::System::Threading::{GetExitCodeProcess, WaitForSingleObject};
    use windows::Win32::UI::Shell::{ShellExecuteExW, SEE_MASK_NOASYNC, SEE_MASK_NOCLOSEPROCESS, SHELLEXECUTEINFOW};
    use windows::Win32::UI::WindowsAndMessaging::SW_SHOWNORMAL;
    ensure_time(deadline)?;
    note(progress, log, "Requesting administrator approval for Visual Studio Build Tools")?;
    let file: Vec<u16> = program.as_os_str().encode_wide().chain(Some(0)).collect();
    let params: Vec<u16> = parameters.encode_utf16().chain(Some(0)).collect();
    let mut info = SHELLEXECUTEINFOW {
        cbSize: std::mem::size_of::<SHELLEXECUTEINFOW>() as u32,
        fMask: SEE_MASK_NOCLOSEPROCESS | SEE_MASK_NOASYNC,
        lpVerb: w!("runas"),
        lpFile: PCWSTR(file.as_ptr()),
        lpParameters: PCWSTR(params.as_ptr()),
        nShow: SW_SHOWNORMAL.0,
        ..Default::default()
    };
    if let Err(error) = unsafe { ShellExecuteExW(&mut info) } {
        if error.code() == windows::core::HRESULT::from_win32(ERROR_CANCELLED.0) || error.code() == windows::core::HRESULT::from_win32(740) {
            bail!("Visual Studio C++ workload elevation was cancelled or could not start: {error}");
        }
        return Err(error).context("Visual Studio C++ workload elevation was cancelled or could not start");
    }
    let result = (|| -> Result<u32> {
        let remaining = deadline.saturating_duration_since(Instant::now()).as_millis().min(u32::MAX as u128) as u32;
        let wait = unsafe { WaitForSingleObject(info.hProcess, remaining) };
        if wait != WAIT_OBJECT_0 {
            bail!("local setup timed out");
        }
        let mut code = 1u32;
        unsafe { GetExitCodeProcess(info.hProcess, &mut code)? };
        Ok(code)
    })();
    unsafe { let _ = CloseHandle(info.hProcess); }
    result
}

/// `cmd.exe /c` removes one leading quote and the last quote. Rust's normal
/// argument quoting would turn the inner quotes into `\"`, which cmd leaves in
/// the path. The raw command line keeps the quotes cmd expects.
fn vcvars_command(bat: &str) -> Result<String> {
    if bat.contains('"') || bat.contains('\n') || bat.contains('\r') {
        bail!("MSVC vcvars path contains a quote: {bat}");
    }
    let script = format!("call \"{bat}\" >nul && set");
    Ok(format!("/d /c \"{script}\""))
}

fn accepts_authenticode_signer(name: &str) -> bool {
    name == "Microsoft Corporation"
}

/// `WinVerifyTrust` with whole-chain revocation, then the embedded signer name.
/// A failure here returns before anything is elevated. Wine never reaches this:
/// it unpacks the portable toolset instead of running the bootstrapper.
#[cfg(windows)]
fn verify_microsoft_authenticode(program: &Path) -> Result<()> {
    trust_authenticode_file(program)?;
    let signer = authenticode_signer_name(program)?;
    if !accepts_authenticode_signer(&signer) {
        bail!("Visual Studio installer is not signed by Microsoft Corporation ({signer})");
    }
    Ok(())
}

#[cfg(windows)]
fn trust_authenticode_file(program: &Path) -> Result<()> {
    use std::os::windows::ffi::OsStrExt;
    use windows::core::PCWSTR;
    use windows::Win32::Foundation::HWND;
    use windows::Win32::Security::WinTrust::{
        WinVerifyTrust, WINTRUST_ACTION_GENERIC_VERIFY_V2, WINTRUST_DATA, WINTRUST_FILE_INFO,
        WTD_CHOICE_FILE, WTD_REVOKE_WHOLECHAIN, WTD_STATEACTION_VERIFY, WTD_UI_NONE,
    };
    let wide: Vec<u16> = program.as_os_str().encode_wide().chain(Some(0)).collect();
    let mut file_info = WINTRUST_FILE_INFO {
        cbStruct: std::mem::size_of::<WINTRUST_FILE_INFO>() as u32,
        pcwszFilePath: PCWSTR(wide.as_ptr()),
        ..Default::default()
    };
    let mut action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
    let mut data = WINTRUST_DATA {
        cbStruct: std::mem::size_of::<WINTRUST_DATA>() as u32,
        dwUIChoice: WTD_UI_NONE,
        fdwRevocationChecks: WTD_REVOKE_WHOLECHAIN,
        dwUnionChoice: WTD_CHOICE_FILE,
        dwStateAction: WTD_STATEACTION_VERIFY,
        ..Default::default()
    };
    data.Anonymous.pFile = &mut file_info;
    // CLOSE frees the provider state from VERIFY. The file path has to stay alive until then.
    struct CloseTrust {
        action: *mut windows::core::GUID,
        data: *mut WINTRUST_DATA,
    }
    impl Drop for CloseTrust {
        fn drop(&mut self) {
            use windows::Win32::Foundation::HWND;
            use windows::Win32::Security::WinTrust::{WinVerifyTrust, WTD_STATEACTION_CLOSE};
            unsafe {
                (*self.data).dwStateAction = WTD_STATEACTION_CLOSE;
                WinVerifyTrust(HWND(0), self.action, self.data.cast());
            }
        }
    }
    let close = CloseTrust {
        action: std::ptr::addr_of_mut!(action),
        data: std::ptr::addr_of_mut!(data),
    };
    let status = unsafe { WinVerifyTrust(HWND(0), close.action, close.data.cast()) };
    drop(close);
    if status != 0 {
        bail!("Visual Studio installer signature was not trusted ({:#010x}): {}", status as u32, program.display());
    }
    Ok(())
}

#[cfg(windows)]
fn authenticode_signer_name(program: &Path) -> Result<String> {
    use std::os::windows::ffi::OsStrExt;
    use windows::Win32::Security::Cryptography::{
        CertFindCertificateInStore, CertGetNameStringW, CryptMsgGetParam, CryptQueryObject,
        CERT_CONTEXT, CERT_FIND_SUBJECT_CERT,
        CERT_INFO, CERT_NAME_SIMPLE_DISPLAY_TYPE, CERT_QUERY_CONTENT_FLAG_PKCS7_SIGNED_EMBED,
        CERT_QUERY_FORMAT_FLAG_BINARY, CERT_QUERY_OBJECT_FILE, CMSG_SIGNER_INFO, CMSG_SIGNER_INFO_PARAM,
        HCERTSTORE, PKCS_7_ASN_ENCODING, X509_ASN_ENCODING,
    };
    let wide: Vec<u16> = program.as_os_str().encode_wide().chain(Some(0)).collect();
    let mut store = HCERTSTORE::default();
    let mut message: *mut core::ffi::c_void = std::ptr::null_mut();
    unsafe {
        CryptQueryObject(
            CERT_QUERY_OBJECT_FILE,
            wide.as_ptr().cast(),
            CERT_QUERY_CONTENT_FLAG_PKCS7_SIGNED_EMBED,
            CERT_QUERY_FORMAT_FLAG_BINARY,
            0,
            None,
            None,
            None,
            Some(&mut store),
            Some(&mut message),
            None,
        )
    }
    .with_context(|| format!("read the signature on {}", program.display()))?;
    struct SignedFile {
        store: HCERTSTORE,
        message: *mut core::ffi::c_void,
    }
    impl Drop for SignedFile {
        fn drop(&mut self) {
            use windows::Win32::Security::Cryptography::{CertCloseStore, CryptMsgClose, HCERTSTORE};
            unsafe {
                if !self.message.is_null() {
                    let _ = CryptMsgClose(Some(self.message));
                    self.message = std::ptr::null_mut();
                }
                if !self.store.is_invalid() {
                    let _ = CertCloseStore(self.store, 0);
                    self.store = HCERTSTORE::default();
                }
            }
        }
    }
    let signed = SignedFile { store, message };
    let mut size = 0u32;
    unsafe { CryptMsgGetParam(signed.message, CMSG_SIGNER_INFO_PARAM, 0, None, &mut size) }
        .with_context(|| format!("read the signer on {}", program.display()))?;
    if (size as usize) < std::mem::size_of::<CMSG_SIGNER_INFO>() {
        bail!("Visual Studio installer signer is incomplete");
    }
    let mut buffer = vec![0u64; (size as usize).div_ceil(std::mem::size_of::<u64>())];
    unsafe {
        CryptMsgGetParam(
            signed.message,
            CMSG_SIGNER_INFO_PARAM,
            0,
            Some(buffer.as_mut_ptr().cast()),
            &mut size,
        )
    }
    .with_context(|| format!("read the signer on {}", program.display()))?;
    if (size as usize) > buffer.len() * std::mem::size_of::<u64>() || (size as usize) < std::mem::size_of::<CMSG_SIGNER_INFO>() {
        bail!("Visual Studio installer signer is incomplete");
    }
    let signer = unsafe { &*buffer.as_ptr().cast::<CMSG_SIGNER_INFO>() };
    if signer.Issuer.cbData == 0 || signer.Issuer.pbData.is_null() || signer.SerialNumber.cbData == 0 || signer.SerialNumber.pbData.is_null() {
        bail!("Visual Studio installer signer has no certificate identity");
    }
    let mut info = CERT_INFO::default();
    info.Issuer = signer.Issuer;
    info.SerialNumber = signer.SerialNumber;
    let cert = unsafe {
        CertFindCertificateInStore(
            signed.store,
            X509_ASN_ENCODING | PKCS_7_ASN_ENCODING,
            0,
            CERT_FIND_SUBJECT_CERT,
            Some((&info as *const CERT_INFO).cast()),
            None,
        )
    };
    if cert.is_null() {
        bail!("Visual Studio installer signature has no signer certificate: {}", std::io::Error::last_os_error());
    }
    struct Certificate(*mut CERT_CONTEXT);
    impl Drop for Certificate {
        fn drop(&mut self) {
            use windows::Win32::Security::Cryptography::CertFreeCertificateContext;
            if !self.0.is_null() {
                unsafe { let _ = CertFreeCertificateContext(Some(self.0)); }
                self.0 = std::ptr::null_mut();
            }
        }
    }
    let cert = Certificate(cert);
    let mut chars = [0u16; 256];
    let count = unsafe { CertGetNameStringW(cert.0, CERT_NAME_SIMPLE_DISPLAY_TYPE, 0, None, Some(&mut chars)) };
    if count <= 1 || count as usize > chars.len() || chars[count as usize - 1] != 0 {
        bail!("Visual Studio installer signer name could not be read");
    }
    let name = String::from_utf16(&chars[..count as usize - 1])
        .with_context(|| format!("read the signer name on {}", program.display()))?;
    Ok(name)
}

#[cfg(windows)]
fn capture_vcvars(bat: &Path, deadline: Instant, progress: &mut dyn FnMut(&str), log: &mut dyn FnMut(&str) -> Result<()>) -> Result<Vec<(String, String)>> {
    let argument = vcvars_command(&bat.display().to_string())?;
    let mut command = Command::new("cmd.exe");
    {
        use std::os::windows::process::CommandExt;
        command.raw_arg(&argument);
    }
    let output = capture_command(&mut command, "Import MSVC environment", deadline, false, progress, log)?;
    let vars = parse_environment_block(&String::from_utf8_lossy(&output.stdout));
    if vars.is_empty() {
        bail!("vcvars64.bat did not produce an environment: {}", bat.display());
    }
    Ok(vars)
}

#[cfg(windows)]
fn register_windows_sdk(msvc: &Path) -> Result<()> {
    let include = msvc.join("Windows Kits").join("10").join("Include");
    let Some(version) = highest_sdk_version(&include) else { return Ok(()) };
    let mut kits = msvc.join("Windows Kits").join("10");
    let mut text = kits.display().to_string();
    if !text.ends_with('\\') {
        text.push('\\');
    }
    let _ = &mut kits;
    for key in [
        r"SOFTWARE\Microsoft\Windows Kits\Installed Roots",
        r"SOFTWARE\Wow6432Node\Microsoft\Windows Kits\Installed Roots",
    ] {
        reg_set_sz(key, "KitsRoot10", &text)?;
    }
    for key in [
        r"SOFTWARE\Microsoft\Microsoft SDKs\Windows\v10.0",
        r"SOFTWARE\Wow6432Node\Microsoft\Microsoft SDKs\Windows\v10.0",
    ] {
        reg_set_sz(key, "InstallationFolder", &text)?;
        reg_set_sz(key, "ProductVersion", &version)?;
    }
    Ok(())
}

#[cfg(windows)]
fn highest_sdk_version(include: &Path) -> Option<String> {
    let mut best: Option<(Vec<u32>, String)> = None;
    for entry in fs::read_dir(include).ok()?.flatten() {
        let name = entry.file_name().to_string_lossy().to_string();
        if !name.starts_with("10.") || !entry.path().is_dir() {
            continue;
        }
        let numbers: Vec<u32> = name.split('.').map(|part| part.parse().unwrap_or(0)).collect();
        if best.as_ref().map(|(current, _)| numbers > *current).unwrap_or(true) {
            best = Some((numbers, name));
        }
    }
    best.map(|(_, name)| name)
}

#[cfg(windows)]
fn reg_set_sz(key: &str, name: &str, value: &str) -> Result<()> {
    use windows::core::PCWSTR;
    use windows::Win32::System::Registry::{RegCloseKey, RegCreateKeyExW, RegSetValueExW, HKEY, HKEY_LOCAL_MACHINE, KEY_SET_VALUE, KEY_WOW64_64KEY, REG_OPTION_NON_VOLATILE, REG_SZ};
    let sub: Vec<u16> = key.encode_utf16().chain(Some(0)).collect();
    let mut handle = HKEY(0);
    unsafe {
        RegCreateKeyExW(HKEY_LOCAL_MACHINE, PCWSTR(sub.as_ptr()), 0, PCWSTR::null(), REG_OPTION_NON_VOLATILE, KEY_SET_VALUE | KEY_WOW64_64KEY, None, &mut handle, None)
            .ok()
            .with_context(|| format!("create registry key {key}"))?;
    }
    let value_name: Vec<u16> = name.encode_utf16().chain(Some(0)).collect();
    let data: Vec<u8> = value.encode_utf16().chain(Some(0)).flat_map(|unit| unit.to_le_bytes()).collect();
    let result = unsafe { RegSetValueExW(handle, PCWSTR(value_name.as_ptr()), 0, REG_SZ, Some(&data)).ok().with_context(|| format!("set registry value {key}\\{name}")) };
    unsafe { let _ = RegCloseKey(handle); }
    result
}

#[cfg(windows)]
fn ensure_mongo(root: &Path, tools: &Path, previous: Option<&Value>, deadline: Instant, progress: &mut dyn FnMut(&str), log: &mut dyn FnMut(&str) -> Result<()>) -> Result<PathBuf> {
    if let Some(state) = previous {
        if let Some(pinned) = state.get("mongod").and_then(Value::as_str) {
            let pinned = std::path::absolute(pinned)?;
            if !path_is_within(&pinned, tools) {
                bail!("Active build-state mongod is outside the owned tools directory: {}", pinned.display());
            }
            if pinned.is_file() {
                return Ok(pinned);
            }
            if directory_has_child(&root.join("data").join("mongo"))? {
                bail!("The pinned MongoDB executable is missing ({}), but persistent database files exist. Restore that owned MongoDB version; setup will not upgrade the database engine opportunistically.", pinned.display());
            }
        }
    }
    if let Some(existing) = find_named(tools, "mongod.exe")? {
        return Ok(existing);
    }
    note(progress, log, "Installing MongoDB 8.0")?;
    let catalog = read_download_text(MONGO_CATALOG, deadline, progress, log)?;
    let selected = select_mongo_archive(&catalog)?;
    let sha256 = match selected.sha256 {
        Some(hash) => hash,
        None => {
            let sidecar = read_download_text(&format!("{}.sha256", selected.url), deadline, progress, log)?;
            first_sha256(&sidecar).with_context(|| format!("MongoDB's official metadata did not provide a valid SHA-256 for {}", selected.url))?
        }
    };
    let archive = scratch_file(root, "mongodb.zip");
    download_file(&selected.url, &archive, Some(&sha256), None, root, deadline, progress, log)?;
    let extracted = scratch_file(root, "mongodb");
    fs::create_dir_all(&extracted)?;
    let extracted_ok = (|| -> Result<PathBuf> {
        zip::ZipArchive::new(fs::File::open(&archive)?).context("open MongoDB archive")?.extract(&extracted).context("extract MongoDB")?;
        let mongod = find_named(&extracted, "mongod.exe")?.context("The verified MongoDB archive did not contain mongod.exe.")?;
        let distribution = mongod.parent().and_then(Path::parent).context("MongoDB archive layout is missing bin\\mongod.exe")?;
        let destination = tools.join(format!("mongodb-{}", selected.version));
        if destination.exists() {
            fs::remove_dir_all(&destination)?;
        }
        fs::create_dir_all(&destination)?;
        let folder_name = distribution.file_name().context("MongoDB distribution has no folder name")?;
        copy_dir(distribution, &destination.join(folder_name))?;
        find_named(&destination, "mongod.exe")?.context("MongoDB extraction did not produce an executable.")
    })();
    let _ = fs::remove_file(&archive);
    let _ = fs::remove_dir_all(&extracted);
    extracted_ok
}

#[cfg(windows)]
fn choose_ports(root: &Path, previous: Option<&Value>) -> Result<(u16, u16, u16)> {
    let ports = if let Some(state) = previous {
        let sdk = json_u16(state, &["sdkPort"]).context("Existing build-state.json is invalid: sdkPort")?;
        let game = json_u16(state, &["gamePort"]).context("Existing build-state.json is invalid: gamePort")?;
        let mongo = json_u16(state, &["mongoPort"]).context("Existing build-state.json is invalid: mongoPort")?;
        (sdk, game, mongo)
    } else if root.join("config.json").is_file() {
        let config: Value = serde_json::from_slice(&fs::read(root.join("config.json"))?).context("Persistent config.json has invalid GameServer/Database ports")?;
        let game = json_u16(&config, &["GameServer", "Port"]).context("Persistent config.json has invalid GameServer/Database ports")?;
        let mongo = json_u16(&config, &["Database", "Port"]).context("Persistent config.json has invalid GameServer/Database ports")?;
        let sdk = free_port(&[game, mongo])?;
        (sdk, game, mongo)
    } else {
        let sdk = free_port(&[])?;
        let game = free_port(&[sdk])?;
        let mongo = free_port(&[sdk, game])?;
        (sdk, game, mongo)
    };
    if ports.0 == ports.1 || ports.0 == ports.2 || ports.1 == ports.2 {
        bail!("Persisted SDK, game, and MongoDB ports must be distinct.");
    }
    assert_port_free(ports.0, "SDK")?;
    assert_port_free(ports.1, "Game")?;
    assert_port_free(ports.2, "MongoDB")?;
    Ok(ports)
}

#[cfg(windows)]
fn prepare_config(checkout: &Path, config_path: &Path, game_port: u16, mongo_port: u16) -> Result<()> {
    if !config_path.is_file() {
        let source = fs::read(checkout.join("Resources").join("Configs").join("config.json")).context("read checkout config.json")?;
        let mut config: Value = serde_json::from_slice(&source).context("checkout config.json is invalid")?;
        if !config.is_object() {
            bail!("checkout config.json is invalid");
        }
        apply_network(&mut config, game_port, mongo_port);
        let mut bytes = serde_json::to_vec_pretty(&config)?;
        bytes.push(b'\n');
        write_atomic(config_path, &bytes)?;
    }
    let config: Value = serde_json::from_slice(&fs::read(config_path)?).context("Persistent config.json is invalid")?;
    if !network_matches(&config, game_port, mongo_port) {
        bail!("Persistent config.json network fields do not match the reserved local ports. Restore GameServer/Database loopback settings; setup will not overwrite user configuration.");
    }
    Ok(())
}

/// MSBuild's out-of-proc nodes deadlock on Wine pipes. These switches keep
/// `dotnet publish` in this process: one CPU, no reused node, no build server.
fn apply_wine_publish_limits(command: &mut Command) {
    command
        .args(["-m:1", "-nodeReuse:false", "--disable-build-servers"])
        .env("MSBUILDDISABLENODEREUSE", "1")
        .env("DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER", "1");
}

#[cfg(windows)]
fn publish_server(dotnet: &Path, checkout: &Path, stage: &Path, config: &Path, deadline: Instant, progress: &mut dyn FnMut(&str), log: &mut dyn FnMut(&str) -> Result<()>) -> Result<()> {
    if crate::install::running_under_wine() {
        import_nuget_roots(dotnet, deadline, progress, log)?;
    }
    note(progress, log, "Publishing AscNet server")?;
    let server = stage.join("server");
    let mut command = Command::new(dotnet);
    command
        .args(["publish"])
        .arg(checkout.join("AscNet").join("AscNet.csproj"))
        .args(["-c", "Release", "-o"])
        .arg(&server)
        .arg("--artifacts-path")
        .arg(stage.join("dotnet-artifacts"))
        .args(["--source", NUGET_INDEX])
        .env("DOTNET_CLI_TELEMETRY_OPTOUT", "1")
        .env("DOTNET_NOLOGO", "1")
        .env("DOTNET_SKIP_FIRST_TIME_EXPERIENCE", "1");
    if crate::install::running_under_wine() {
        command.env("NUGET_CERT_REVOCATION_MODE", "offline");
        // Worker nodes (/nodemode:1 /nodeReuse:true) talk over pipes. Under Wine
        // they stop after "Determining projects to restore" and never open a
        // NuGet connection. One in-proc node does the restore in this process.
        apply_wine_publish_limits(&mut command);
    }
    run_logged(&mut command, "Publishing AscNet server", deadline, progress, log)?;
    if !server.join("Configs").join("version_config.json").is_file() {
        bail!("Published server is missing Configs\\version_config.json.");
    }
    fs::create_dir_all(server.join("Configs"))?;
    fs::copy(config, server.join("Configs").join("config.json"))?;
    Ok(())
}

#[cfg(windows)]
fn import_nuget_roots(dotnet: &Path, deadline: Instant, progress: &mut dyn FnMut(&str), log: &mut dyn FnMut(&str) -> Result<()>) -> Result<()> {
    let marker = crate::local::root()?.parent().context("local root has no parent")?.join("nuget-trust-imported.txt");
    if marker.is_file() {
        return Ok(());
    }
    let version = dotnet_sdk8(dotnet, deadline, progress, log)?.context("installed dotnet.exe does not provide a .NET 8 SDK")?;
    let roots = dotnet.parent().context("dotnet.exe has no directory")?.join("sdk").join(version).join("trustedroots");
    let mut imported = 0usize;
    if roots.is_dir() {
        for entry in fs::read_dir(&roots)? {
            let entry = entry?;
            if !entry.path().is_file() {
                continue;
            }
            let Ok(text) = fs::read_to_string(entry.path()) else { continue };
            for certificate in pem_certificates(&text)? {
                if import_certificate(&certificate)? {
                    imported += 1;
                }
            }
        }
    }
    if imported == 0 {
        bail!("NuGet trust import found no certificates under {}", roots.display());
    }
    if let Some(parent) = marker.parent() {
        fs::create_dir_all(parent)?;
    }
    fs::write(&marker, format!("{imported}\n"))?;
    note(progress, log, &format!("Imported {imported} NuGet trust roots"))?;
    Ok(())
}

#[cfg(windows)]
fn import_certificate(encoded: &[u8]) -> Result<bool> {
    use windows::Win32::Security::Cryptography::{CertAddEncodedCertificateToStore, CertCloseStore, CertOpenStore, CERT_QUERY_ENCODING_TYPE, CERT_STORE_ADD_USE_EXISTING, CERT_STORE_OPEN_EXISTING_FLAG, CERT_STORE_PROV_SYSTEM_W, CERT_SYSTEM_STORE_CURRENT_USER_ID, CERT_SYSTEM_STORE_LOCAL_MACHINE_ID, CERT_SYSTEM_STORE_LOCATION_SHIFT, PKCS_7_ASN_ENCODING, X509_ASN_ENCODING, CERT_OPEN_STORE_FLAGS, HCRYPTPROV_LEGACY};
    let name: Vec<u16> = "ROOT".encode_utf16().chain(Some(0)).collect();
    let encoding = CERT_QUERY_ENCODING_TYPE(X509_ASN_ENCODING.0 | PKCS_7_ASN_ENCODING.0);
    let locations = [
        CERT_OPEN_STORE_FLAGS((CERT_SYSTEM_STORE_LOCAL_MACHINE_ID << CERT_SYSTEM_STORE_LOCATION_SHIFT) | CERT_STORE_OPEN_EXISTING_FLAG.0),
        CERT_OPEN_STORE_FLAGS((CERT_SYSTEM_STORE_CURRENT_USER_ID << CERT_SYSTEM_STORE_LOCATION_SHIFT) | CERT_STORE_OPEN_EXISTING_FLAG.0),
    ];
    let mut last = None;
    for flags in locations {
        let opened = unsafe { CertOpenStore(CERT_STORE_PROV_SYSTEM_W, encoding, HCRYPTPROV_LEGACY(0), flags, Some(name.as_ptr().cast())) };
        let store = match opened {
            Ok(store) => store,
            Err(error) => {
                last = Some(error);
                continue;
            }
        };
        let added = unsafe { CertAddEncodedCertificateToStore(store, encoding, encoded, CERT_STORE_ADD_USE_EXISTING, None) };
        unsafe { let _ = CertCloseStore(store, 0); }
        match added {
            Ok(()) => return Ok(true),
            Err(error) => last = Some(error),
        }
    }
    match last {
        Some(error) => Err(error).context("import NuGet trust certificate"),
        None => bail!("could not open a certificate store for NuGet trust"),
    }
}

#[cfg(windows)]
fn build_patch(rust: &Path, compiler: &Compiler, checkout: &Path, root: &Path, stage: &Path, deadline: Instant, progress: &mut dyn FnMut(&str), log: &mut dyn FnMut(&str) -> Result<()>) -> Result<()> {
    let cargo = rust.join("bin").join("cargo.exe");
    let rustc = rust.join("bin").join("rustc.exe");
    let target = root.join("cargo-target");
    let attempts = if compiler.wine { 3 } else { 1 };
    let mut last = None;
    for attempt in 1..=attempts {
        ensure_time(deadline)?;
        note(progress, log, "Building client patch")?;
        let mut command = Command::new(&cargo);
        apply_build_env(&mut command, compiler, &rust.join("bin"), &rustc);
        command
            .arg("build")
            .arg("--manifest-path")
            .arg(checkout.join("AscNet.Patch").join("Cargo.toml"))
            .args(["--locked", "--release", "--target", RUST_HOST, "--target-dir"])
            .arg(&target);
        match run_logged(&mut command, "Building client patch", deadline, progress, log) {
            Ok(()) => {
                last = None;
                break;
            }
            Err(error) => {
                last = Some(error);
                if attempt == attempts {
                    break;
                }
                note(progress, log, &format!("cargo build failed (attempt {attempt} of {attempts}); retrying"))?;
            }
        }
    }
    if let Some(error) = last {
        return Err(error);
    }
    let release = target.join(RUST_HOST).join("release");
    let patch = stage.join("patch");
    fs::create_dir_all(&patch)?;
    fs::copy(release.join("lucia.dll"), patch.join("lucia.dll")).context("copy lucia.dll")?;
    // The crate is named krsdk, so current checkouts emit krsdk.dll. The game
    // still loads KRSDK.dll, and an older checkout still emits that name.
    let sdk_built = release.join("krsdk.dll");
    let sdk_built = if sdk_built.is_file() { sdk_built } else { release.join("KRSDK.dll") };
    fs::copy(&sdk_built, patch.join("KRSDK.dll")).context("copy KRSDK.dll")?;
    Ok(())
}

#[cfg(windows)]
fn apply_build_env(command: &mut Command, compiler: &Compiler, toolchain_bin: &Path, rustc: &Path) {
    let inherited = env::vars().collect::<Vec<_>>();
    let block = compiler_process_env(
        &inherited,
        &compiler.vars,
        &toolchain_bin.display().to_string(),
        &rustc.display().to_string(),
        compiler.wine,
        compiler.full_environment,
    );
    if compiler.full_environment {
        command.env_clear();
    } else {
        command.env_remove("RUSTUP_TOOLCHAIN");
    }
    for (key, value) in block {
        command.env(key, value);
    }
}

#[cfg(windows)]
fn build_version_shim(compiler: &Compiler, checkout: &Path, stage: &Path, launcher_dir: &Path, deadline: Instant, progress: &mut dyn FnMut(&str), log: &mut dyn FnMut(&str) -> Result<()>) -> Result<()> {
    note(progress, log, "Building version loader")?;
    let patch = stage.join("patch");
    fs::create_dir_all(&patch)?;
    let loader = stage.join("loader");
    fs::create_dir_all(&loader)?;
    if let Some(msbuild) = &compiler.msbuild {
        let object = stage.join("loader-obj");
        fs::create_dir_all(&object)?;
        let mut command = Command::new(msbuild);
        apply_build_env(&mut command, compiler, Path::new(""), Path::new("rustc.exe"));
        command.env_remove("RUSTC");
        let out_dir = loader.display().to_string().replace('\\', "/");
        let int_dir = object.display().to_string().replace('\\', "/");
        command
            .arg(checkout.join("AscNet.Patch").join("VersionShim").join("src").join("VersionShim.vcxproj"))
            .args(["/m:1", "/p:Configuration=Release", "/p:Platform=x64"])
            // No inner quotes: Rust's Windows quoting escapes them to \" and MSBuild then sees them as path characters.
            .arg(format!("/p:OutDir={out_dir}/"))
            .arg(format!("/p:IntDir={int_dir}/"));
        run_logged(&mut command, "Building version loader", deadline, progress, log)?;
        fs::copy(loader.join("VersionShim.dll"), patch.join("version.dll")).context("copy version.dll")?;
    } else {
        compile_version_shim_with_cl(compiler, checkout, &loader, deadline, progress, log)?;
        fs::copy(loader.join("VersionShim.dll"), patch.join("version.dll")).context("copy version.dll")?;
    }
    fs::write(patch.join("libraries.txt"), "*PGR.exe\nlucia.dll\n")?;
    fs::copy(launcher_dir.join("supported-client.json"), patch.join("supported-client.json")).context("copy supported-client.json")?;
    Ok(())
}

#[cfg(windows)]
fn compile_version_shim_with_cl(compiler: &Compiler, checkout: &Path, loader: &Path, deadline: Instant, progress: &mut dyn FnMut(&str), log: &mut dyn FnMut(&str) -> Result<()>) -> Result<()> {
    let path = compiler.vars.iter().find(|(key, _)| key.eq_ignore_ascii_case("Path")).map(|(_, value)| value.as_str()).unwrap_or("");
    let cl = find_on_windows_path(path, "cl.exe").context("vcvars64 did not provide cl.exe")?;
    let link = find_on_windows_path(path, "link.exe").context("vcvars64 did not provide link.exe")?;
    let source = checkout.join("AscNet.Patch").join("VersionShim").join("src");
    let object = loader.join("dllmain.obj");
    let response = loader.join("cl.rsp");
    let object_text = object.display().to_string().replace('\\', "/");
    fs::write(&response, format!("/nologo\n/O1\n/GS-\n/c\n/DNDEBUG\n/DVERSIONSHIM_EXPORTS\n/D_WINDOWS\n/D_USRDLL\n/DPROJECT_NAME=\\\"VersionShim\\\"\n/Fo\"{object_text}\"\ndllmain.c\n"))?;
    let mut compile = Command::new(&cl);
    apply_build_env(&mut compile, compiler, Path::new(""), Path::new("rustc.exe"));
    compile.env_remove("RUSTC");
    compile.current_dir(&source);
    let compile_argument = quoted_response_file(&response);
    {
        use std::os::windows::process::CommandExt;
        compile.raw_arg(&compile_argument);
    }
    let compile_shown = command_line(&compile);
    run_logged_shown(&mut compile, &compile_shown, "Compiling version loader", deadline, progress, log)?;
    let produced = loader.join("VersionShim.dll");
    let link_response = loader.join("link.rsp");
    let out = produced.display().to_string().replace('\\', "/");
    let obj = object.display().to_string().replace('\\', "/");
    fs::write(&link_response, format!("/DLL\n/NODEFAULTLIB\n/ENTRY:DllMain\n/SUBSYSTEM:WINDOWS\n/OUT:\"{out}\"\n\"{obj}\"\nkernel32.lib\nuser32.lib\n"))?;
    let mut linker = Command::new(link);
    apply_build_env(&mut linker, compiler, Path::new(""), Path::new("rustc.exe"));
    linker.env_remove("RUSTC");
    linker.current_dir(&source);
    let link_argument = quoted_response_file(&link_response);
    {
        use std::os::windows::process::CommandExt;
        linker.raw_arg(&link_argument);
    }
    let link_shown = command_line(&linker);
    run_logged_shown(&mut linker, &link_shown, "Linking version loader", deadline, progress, log)?;
    Ok(())
}

#[cfg(windows)]
fn find_on_windows_path(path: &str, name: &str) -> Option<PathBuf> {
    path.split(';').map(str::trim).filter(|dir| !dir.is_empty()).map(|dir| PathBuf::from(dir).join(name)).find(|candidate| candidate.is_file())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn lib_has_windows_sdk_needs_kernel32_on_lib() {
        let root = std::env::temp_dir().join(format!("ascnet-lib-sdk-{}", std::process::id()));
        let vc = root.join("VC/lib/x64");
        let um = root.join("Windows Kits/10/Lib/10.0.26100.0/um/x64");
        fs::create_dir_all(&vc).unwrap();
        fs::create_dir_all(&um).unwrap();
        fs::write(vc.join("libcmt.lib"), b"lib").unwrap();
        fs::write(um.join("kernel32.lib"), b"lib").unwrap();
        let vc_only = vec![("LIB".to_owned(), format!("{};", vc.display()))];
        assert!(!lib_has_windows_sdk(&vc_only), "VC tools without the SDK must not count");
        assert!(!lib_has_windows_sdk(&[]), "no LIB at all");
        let with_sdk = vec![("Lib".to_owned(), format!("{};{}", vc.display(), um.display()))];
        assert!(lib_has_windows_sdk(&with_sdk), "kernel32.lib on LIB (any key case)");
        let _ = fs::remove_dir_all(&root);
    }

    #[test]
    fn dotnet_selector_uses_the_first_windows_sdk_zip() {
        let document = r#"{
            "channel-version": "8.0",
            "releases": [
                {"release-version": "8.0.1", "sdk": {"version": "8.0.1", "files": [
                    {"name": "dotnet-sdk-win-x64.exe", "rid": "win-x64", "url": "https://example.invalid/sdk.exe", "hash": "aa"},
                    {"name": "dotnet-sdk-win-x64.zip", "rid": "win-x64", "url": "https://example.invalid/first.zip", "hash": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}
                ]}},
                {"sdk": {"files": [
                    {"name": "dotnet-sdk-win-x64.zip", "url": "https://example.invalid/second.zip", "hash": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}
                ]}}
            ]
        }"#;
        let selected = select_dotnet_sdk(document).unwrap();
        assert_eq!(selected.url, "https://example.invalid/first.zip");
        assert_eq!(selected.sha512.len(), 128);
    }

    #[test]
    fn mongo_selector_prefers_newest_community_zip() {
        let document = r#"{
            "versions": [
                {"version": "8.1.2", "downloads": [{"target": "windows", "arch": "x86_64", "edition": "community", "archive": {"url": "https://example.invalid/81.zip", "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}}]},
                {"version": "8.0.4", "downloads": [
                    {"target": "windows", "arch": "x86_64", "edition": "base", "archive": {"url": "https://example.invalid/base.zip", "sha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}},
                    {"target": "linux", "arch": "x86_64", "edition": "community", "archive": {"url": "https://example.invalid/linux.tgz", "sha256": "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"}},
                    {"target": "windows", "arch": "x86_64", "edition": "community", "archive": {"url": "https://example.invalid/community.zip", "sha256": "DDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDD"}}
                ]},
                {"version": "7.0.14", "downloads": [{"target": "windows", "arch": "x86_64", "edition": "community", "archive": {"url": "https://example.invalid/7.zip", "sha256": "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"}}]}
            ]
        }"#;
        let selected = select_mongo_archive(document).unwrap();
        assert_eq!(selected.version, "8.0.4");
        assert_eq!(selected.url, "https://example.invalid/community.zip");
        assert_eq!(selected.sha256.as_deref(), Some("dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"));
    }

    #[test]
    fn mongo_selector_leaves_missing_checksum_for_the_sidecar() {
        let document = r#"{"versions":[{"version":"8.0.10","downloads":[{"target":"windows","arch":"x86_64","edition":"community","archive":{"url":"https://example.invalid/810.zip","sha256":""}}]}]}"#;
        let selected = select_mongo_archive(document).unwrap();
        assert_eq!(selected.version, "8.0.10");
        assert!(selected.sha256.is_none());
        assert_eq!(first_sha256("abc\n0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\n").as_deref(), Some("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"));
    }

    #[test]
    fn gitconfig_strip_removes_only_the_program_files_include() {
        let text = "[include]\r\n\tpath = C:/Program Files/Git/etc/gitconfig\r\n\tpath = C:/Other/gitconfig\r\n\tPath = C:\\Program Files\\Git\\etc\\gitconfig\r\n";
        let stripped = without_program_files_git_include(text);
        assert!(!stripped.to_ascii_lowercase().contains("program files/git"));
        assert!(stripped.contains("C:/Other/gitconfig"));
        assert!(stripped.contains("[include]"));
    }

    #[test]
    fn owned_tool_path_requires_a_boundary() {
        assert!(path_is_within(Path::new(r"C:\AscNet\tools\mongo\mongod.exe"), Path::new(r"C:\AscNet\tools")));
        assert!(path_is_within(Path::new("/tmp/Tools/mongo/mongod.exe"), Path::new("/tmp/tools")));
        assert!(!path_is_within(Path::new(r"C:\AscNet\tools-other\mongod.exe"), Path::new(r"C:\AscNet\tools")));
        assert!(!path_is_within(Path::new(r"C:\AscNet\tools"), Path::new(r"C:\AscNet\tools")));
    }

    #[test]
    fn pem_decoder_reads_a_certificate_body() {
        let body = pem_certificates("-----BEGIN CERTIFICATE-----\nAQID\nBA==\n-----END CERTIFICATE-----\nnoise\n").unwrap();
        assert_eq!(body, vec![vec![1, 2, 3, 4]]);
    }

    #[test]
    fn environment_block_keeps_equals_in_values() {
        let pairs = parse_environment_block("Path=C:\\a;C:\\b\r\nINCLUDE=C:\\inc\r\nnot a pair\r\n");
        assert_eq!(pairs, vec![("Path".into(), r"C:\a;C:\b".into()), ("INCLUDE".into(), r"C:\inc".into())]);
    }

    #[test]
    fn response_file_argument_quotes_windows_paths() {
        assert_eq!(quoted_response_file(Path::new(r"C:\Users\A B\cl.rsp")), "@\"C:/Users/A B/cl.rsp\"");
    }

    #[test]
    fn vcvars_command_keeps_cmd_quotes_around_a_spaced_path() {
        let line = vcvars_command(r"C:\Program Files (x86)\x\vcvars64.bat").unwrap();
        assert_eq!(line, "/d /c \"call \"C:\\Program Files (x86)\\x\\vcvars64.bat\" >nul && set\"");
        assert!(!line.contains("\\\""));
        assert!(vcvars_command("C:\\Program Files\\x\\\"vcvars64.bat").is_err());
    }

    #[test]
    fn authenticode_signer_is_microsoft_corporation() {
        assert!(accepts_authenticode_signer("Microsoft Corporation"));
        assert!(!accepts_authenticode_signer("Microsoft Corporation "));
        assert!(!accepts_authenticode_signer("Contoso"));
    }

    #[test]
    fn rust_install_is_ready_only_when_the_standard_library_is_present() {
        let root = env::temp_dir().join(format!("ascnet-rust-ready-{}", uuid::Uuid::new_v4()));
        fs::create_dir_all(root.join("bin")).unwrap();
        fs::write(root.join("bin").join("cargo.exe"), b"cargo").unwrap();
        fs::write(root.join("bin").join("rustc.exe"), b"rustc").unwrap();
        fs::create_dir_all(root.join("lib").join("rustlib").join(RUST_HOST)).unwrap();
        assert!(!rust_installation_ready(&root));
        let lib = root.join("lib").join("rustlib").join(RUST_HOST).join("lib");
        fs::create_dir_all(&lib).unwrap();
        fs::create_dir(lib.join("libstd-not-a-file.rlib")).unwrap();
        assert!(!rust_installation_ready(&root));
        fs::write(lib.join("libstd-test.rlib"), b"std").unwrap();
        assert!(rust_installation_ready(&root));
        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn rust_component_publish_leaves_no_partial_file() {
        let root = env::temp_dir().join(format!("ascnet-rust-unpack-{}", uuid::Uuid::new_v4()));
        fs::create_dir_all(&root).unwrap();
        let archive = root.join("component.tar.gz");
        let mut encoded = Vec::new();
        {
            let mut encoder = flate2::write::GzEncoder::new(&mut encoded, flate2::Compression::default());
            let mut builder = tar::Builder::new(&mut encoder);
            let payload = b"std-bytes";
            let mut header = tar::Header::new_gnu();
            header.set_size(payload.len() as u64);
            header.set_mode(0o644);
            header.set_cksum();
            let name = format!("rust-std-{RUST_VERSION}-{RUST_HOST}/rust-std-{RUST_HOST}/lib/rustlib/{RUST_HOST}/lib/libstd-test.rlib");
            builder.append_data(&mut header, name, &payload[..]).unwrap();
            builder.finish().unwrap();
        }
        fs::write(&archive, encoded).unwrap();
        let dest = root.join("stage");
        unpack_rust_component(&archive, &dest).unwrap();
        let installed = dest.join("lib").join("rustlib").join(RUST_HOST).join("lib").join("libstd-test.rlib");
        assert_eq!(fs::read(&installed).unwrap(), b"std-bytes");
        assert!(!PathBuf::from(format!("{}.partial", installed.display())).exists());
        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn hashed_file_is_reused_when_the_digest_matches() {
        let root = env::temp_dir().join(format!("ascnet-cache-{}", uuid::Uuid::new_v4()));
        fs::create_dir_all(&root).unwrap();
        let bytes = b"already-on-disk";
        let sha256 = format!("{:x}", Sha256::digest(bytes));
        let sha512 = format!("{:x}", Sha512::digest(bytes));
        let cached = root.join("cache").join(&sha256);
        fs::create_dir_all(cached.parent().unwrap()).unwrap();
        fs::write(&cached, bytes).unwrap();
        let dest = root.join("tmp").join("package.bin");
        let reused = reuse_hashed_file(&cached, &dest, Some(&sha256), None).unwrap().unwrap();
        assert_eq!(reused, sha256);
        assert_eq!(fs::read(&dest).unwrap(), bytes);
        let same = reuse_hashed_file(&dest, &dest, Some(&sha256.to_ascii_uppercase()), None).unwrap().unwrap();
        assert_eq!(same, sha256);
        let by_sha512 = reuse_hashed_file(&cached, &root.join("other.bin"), None, Some(&sha512)).unwrap().unwrap();
        assert_eq!(by_sha512, sha256);
        let wrong = "ab".repeat(32);
        assert!(reuse_hashed_file(&cached, &root.join("nope.bin"), Some(&wrong), None).unwrap().is_none());
        assert!(!root.join("nope.bin").exists());
        assert!(cache_key(None, None).is_none());
        assert_eq!(cache_key(Some(&sha256), None).as_deref(), Some(sha256.as_str()));
        let _ = fs::remove_dir_all(&root);
    }

    #[test]
    fn rust_component_paths_drop_the_two_leading_directories() {
        let relative = rust_payload_relative(Path::new("rustc-1.92.0-x86_64-pc-windows-msvc/rustc/bin/rustc.exe")).unwrap();
        assert_eq!(relative, PathBuf::from("bin/rustc.exe"));
        assert!(rust_payload_relative(Path::new("rustc-1.92.0/rustc/../outside")).is_none());
    }

    #[test]
    fn network_config_preserves_other_fields_and_checks_the_reserved_ports() {
        let mut config = serde_json::json!({"SkipCommonGuides": false, "VerboseLevel": "Debug"});
        apply_network(&mut config, 2335, 27017);
        assert_eq!(config["SkipCommonGuides"], false);
        assert!(network_matches(&config, 2335, 27017));
        assert!(!network_matches(&config, 2336, 27017));
    }

    #[test]
    fn edited_config_reaches_the_server_on_start_unless_its_network_changed() {
        let root = env::temp_dir().join(format!("ascnet-runtime-config-{}", uuid::Uuid::new_v4()));
        let server = root.join("build").join("server");
        fs::create_dir_all(server.join("Configs")).unwrap();
        let mut config = serde_json::json!({"VerboseLevel": "Debug"});
        apply_network(&mut config, 2335, 27017);
        fs::write(server.join("Configs").join("config.json"), config.to_string()).unwrap();
        config["VerboseLevel"] = "Normal".into();
        fs::write(root.join("config.json"), config.to_string()).unwrap();
        install_runtime_config(&root, &server, 2335, 27017).unwrap();
        let installed: Value = serde_json::from_slice(&fs::read(server.join("Configs").join("config.json")).unwrap()).unwrap();
        assert_eq!(installed["VerboseLevel"], "Normal");
        config["Database"]["Port"] = 27018.into();
        fs::write(root.join("config.json"), config.to_string()).unwrap();
        assert!(install_runtime_config(&root, &server, 2335, 27017).is_err());
        let kept: Value = serde_json::from_slice(&fs::read(server.join("Configs").join("config.json")).unwrap()).unwrap();
        assert_eq!(kept["Database"]["Port"], 27017, "a refused config is not installed");
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn occupied_port_is_refused() {
        let listener = TcpListener::bind((Ipv4Addr::LOCALHOST, 0)).unwrap();
        let port = listener.local_addr().unwrap().port();
        let error = assert_port_free(port, "fixture").unwrap_err().to_string();
        assert!(error.contains("already in use"), "{error}");
    }

    #[test]
    fn checkout_clones_fast_forwards_and_refuses_dirty_or_divergent_history() {
        let temp = env::temp_dir().join(format!("ascnet-setup-{}", uuid::Uuid::new_v4()));
        fs::create_dir_all(&temp).unwrap();
        let origin = temp.join("origin");
        let checkout = temp.join("checkout");
        let git = Path::new("git");
        let deadline = Instant::now() + Duration::from_secs(60);
        let mut progress = |_: &str| {};
        let mut log = |_: &str| Ok(());
        let run = |args: &[&str]| {
            let mut command = Command::new(git);
            command.args(["-c", "commit.gpgsign=false", "-c", "user.email=fixture@example.invalid", "-c", "user.name=Fixture"]);
            command.args(args);
            let output = command.output().unwrap();
            assert!(output.status.success(), "{}", String::from_utf8_lossy(&output.stderr));
        };
        run(&["init", "--initial-branch=master", origin.to_str().unwrap()]);
        fs::write(origin.join("value.txt"), "one").unwrap();
        run(&["-C", origin.to_str().unwrap(), "-c", "user.email=fixture@example.invalid", "-c", "user.name=Fixture", "add", "value.txt"]);
        run(&["-C", origin.to_str().unwrap(), "-c", "user.email=fixture@example.invalid", "-c", "user.name=Fixture", "commit", "-m", "one"]);
        update_checkout(git, &checkout, origin.to_str().unwrap(), "master", deadline, &mut progress, &mut log).unwrap();
        assert_eq!(fs::read_to_string(checkout.join("value.txt")).unwrap(), "one");
        fs::create_dir_all(origin.join("AscNet.Launcher")).unwrap();
        fs::write(origin.join("AscNet.Launcher").join("setup-local.ps1"), "obsolete").unwrap();
        run(&["-C", origin.to_str().unwrap(), "add", "AscNet.Launcher/setup-local.ps1"]);
        run(&["-C", origin.to_str().unwrap(), "-c", "user.email=fixture@example.invalid", "-c", "user.name=Fixture", "commit", "-m", "obsolete setup"]);
        update_checkout(git, &checkout, origin.to_str().unwrap(), "master", deadline, &mut progress, &mut log).unwrap();
        assert_eq!(fs::read_to_string(checkout.join("AscNet.Launcher").join("setup-local.ps1")).unwrap(), "obsolete");

        fs::write(origin.join("value.txt"), "two").unwrap();
        run(&["-C", origin.to_str().unwrap(), "-c", "user.email=fixture@example.invalid", "-c", "user.name=Fixture", "commit", "-am", "two"]);
        update_checkout(git, &checkout, origin.to_str().unwrap(), "master", deadline, &mut progress, &mut log).unwrap();
        assert_eq!(fs::read_to_string(checkout.join("value.txt")).unwrap(), "two");

        let synced = String::from_utf8(Command::new(git).args(["-C", checkout.to_str().unwrap(), "rev-parse", "HEAD"]).output().unwrap().stdout).unwrap();
        let hidden = temp.join("origin-offline");
        fs::rename(&origin, &hidden).unwrap();
        update_checkout(git, &checkout, origin.to_str().unwrap(), "master", deadline, &mut progress, &mut log).unwrap();
        let stayed = String::from_utf8(Command::new(git).args(["-C", checkout.to_str().unwrap(), "rev-parse", "HEAD"]).output().unwrap().stdout).unwrap();
        assert_eq!(synced, stayed);
        fs::rename(&hidden, &origin).unwrap();

        fs::write(checkout.join("value.txt"), "user changes").unwrap();
        let error = update_checkout(git, &checkout, origin.to_str().unwrap(), "master", deadline, &mut progress, &mut log).unwrap_err().to_string();
        assert!(error.contains("local changes"), "{error}");
        assert_eq!(fs::read_to_string(checkout.join("value.txt")).unwrap(), "user changes");
        fs::write(checkout.join("value.txt"), "two").unwrap();

        fs::write(checkout.join("local.txt"), "local commit").unwrap();
        run(&["-C", checkout.to_str().unwrap(), "-c", "user.email=fixture@example.invalid", "-c", "user.name=Fixture", "add", "local.txt"]);
        run(&["-C", checkout.to_str().unwrap(), "-c", "user.email=fixture@example.invalid", "-c", "user.name=Fixture", "commit", "-m", "local"]);
        fs::write(origin.join("remote.txt"), "remote commit").unwrap();
        run(&["-C", origin.to_str().unwrap(), "add", "remote.txt"]);
        run(&["-C", origin.to_str().unwrap(), "-c", "user.email=fixture@example.invalid", "-c", "user.name=Fixture", "commit", "-m", "remote"]);
        let before = String::from_utf8(Command::new(git).args(["-C", checkout.to_str().unwrap(), "rev-parse", "HEAD"]).output().unwrap().stdout).unwrap();
        let error = update_checkout(git, &checkout, origin.to_str().unwrap(), "master", deadline, &mut progress, &mut log).unwrap_err();
        let after = String::from_utf8(Command::new(git).args(["-C", checkout.to_str().unwrap(), "rev-parse", "HEAD"]).output().unwrap().stdout).unwrap();
        let message = format!("{error:#}");
        assert!(message.contains("commits that are not on"), "{message}");
        assert!(message.contains("reset --hard FETCH_HEAD"), "{message}");
        assert!(message.contains("local"), "{message}");
        assert_eq!(before, after);
        fs::remove_dir_all(temp).unwrap();
    }

    #[test]
    fn portable_compiler_keeps_the_process_environment() {
        let inherited = vec![
            ("SystemRoot".to_owned(), r"C:\Windows".to_owned()),
            ("TEMP".to_owned(), r"C:\users\steamuser\AppData\Local\Temp".to_owned()),
            ("USERPROFILE".to_owned(), r"C:\users\steamuser".to_owned()),
            ("Path".to_owned(), r"C:\Windows\system32".to_owned()),
            ("INCLUDE".to_owned(), r"C:\stale".to_owned()),
            ("RUSTUP_TOOLCHAIN".to_owned(), "stable-x86_64-pc-windows-msvc".to_owned()),
            ("RUSTC".to_owned(), r"C:\wrong\rustc.exe".to_owned()),
        ];
        let compiler = vec![
            ("Path".to_owned(), r"C:\msvc\cl".to_owned()),
            ("INCLUDE".to_owned(), r"C:\msvc\include".to_owned()),
            ("LIB".to_owned(), r"C:\msvc\lib".to_owned()),
        ];
        let portable = compiler_process_env(&inherited, &compiler, r"C:\rust\bin", r"C:\rust\bin\rustc.exe", true, false);
        let get = |key: &str| {
            portable.iter().find(|(name, _)| name.eq_ignore_ascii_case(key)).map(|(_, value)| value.as_str())
        };
        assert_eq!(get("SystemRoot"), Some(r"C:\Windows"));
        assert_eq!(get("TEMP"), Some(r"C:\users\steamuser\AppData\Local\Temp"));
        assert_eq!(get("USERPROFILE"), Some(r"C:\users\steamuser"));
        assert_eq!(get("INCLUDE"), Some(r"C:\msvc\include"));
        assert_eq!(get("LIB"), Some(r"C:\msvc\lib"));
        assert_eq!(get("Path"), Some(r"C:\rust\bin;C:\msvc\cl;C:\Windows\system32"));
        assert_eq!(get("RUSTC"), Some(r"C:\rust\bin\rustc.exe"));
        assert_eq!(get("CARGO_BUILD_JOBS"), Some("1"));
        assert_eq!(get("RUSTUP_TOOLCHAIN"), None);
        assert_eq!(portable.iter().filter(|(name, _)| name.eq_ignore_ascii_case("Path")).count(), 1);

        let dumped = compiler_process_env(
            &inherited,
            &[
                ("SystemRoot".to_owned(), r"C:\Windows".to_owned()),
                ("Path".to_owned(), r"C:\vc\bin;C:\Windows\system32".to_owned()),
                ("INCLUDE".to_owned(), r"C:\vc\include".to_owned()),
            ],
            r"C:\rust\bin",
            r"C:\rust\bin\rustc.exe",
            false,
            true,
        );
        let dumped_get = |key: &str| {
            dumped.iter().find(|(name, _)| name.eq_ignore_ascii_case(key)).map(|(_, value)| value.as_str())
        };
        assert_eq!(dumped_get("SystemRoot"), Some(r"C:\Windows"));
        assert_eq!(dumped_get("TEMP"), None);
        assert_eq!(dumped_get("USERPROFILE"), None);
        assert_eq!(dumped_get("Path"), Some(r"C:\rust\bin;C:\vc\bin;C:\Windows\system32"));
        assert_eq!(dumped_get("INCLUDE"), Some(r"C:\vc\include"));
        assert_eq!(dumped_get("CARGO_BUILD_JOBS"), None);
        assert_eq!(dumped_get("RUSTC"), Some(r"C:\rust\bin\rustc.exe"));
    }

    #[test]
    fn dropped_download_is_transient_and_a_checksum_mismatch_is_not() {
        let eof = anyhow::Error::from(std::io::Error::new(ErrorKind::UnexpectedEof, "end of file before message length reached"));
        let wrapped = eof.context("read https://static.rust-lang.org/dist/rustc.tar.gz");
        assert!(download_interrupted(&wrapped));
        let outer = std::io::Error::new(ErrorKind::Other, std::io::Error::new(ErrorKind::UnexpectedEof, "end of file before message length reached"));
        assert!(download_interrupted(&anyhow::Error::from(outer)));
        assert!(download_interrupted(&anyhow::Error::from(std::io::Error::new(ErrorKind::ConnectionReset, "connection reset"))));
        let short = anyhow::Error::from(std::io::Error::new(ErrorKind::UnexpectedEof, "download https://example.invalid/rustc.tar.gz ended after 10 bytes, expected 99"));
        assert!(download_interrupted(&short));
        let recorded = anyhow::anyhow!("read https://static.rust-lang.org/dist/rustc.tar.gz: request or response body error: error reading a body from connection: end of file before message length reached");
        assert!(download_interrupted(&recorded));
        let releases = anyhow::anyhow!("read https://builds.dotnet.microsoft.com/dotnet/release-metadata/8.0/releases.json: request or response body error: error reading a body from connection: end of file before message length reached");
        assert!(download_interrupted(&releases));
        assert!(!download_interrupted(&anyhow::anyhow!("checksum mismatch for https://static.rust-lang.org/dist/rustc.tar.gz (expected abc, received def)")));
        assert!(!download_interrupted(&anyhow::anyhow!("local setup timed out")));
        assert!(!download_interrupted(&anyhow::anyhow!("download https://example.invalid/missing: HTTP status client error (404 Not Found)")));
        assert!(!download_interrupted(&anyhow::Error::from(std::io::Error::other("disk full"))));
    }

    #[test]
    fn wine_publish_stays_in_process() {
        let mut command = Command::new("dotnet");
        apply_wine_publish_limits(&mut command);
        let args: Vec<String> = command.get_args().map(|arg| arg.to_string_lossy().into_owned()).collect();
        assert_eq!(args, vec!["-m:1".to_owned(), "-nodeReuse:false".to_owned(), "--disable-build-servers".to_owned()]);
        let env = command.get_envs().map(|(key, value)| (key.to_string_lossy().into_owned(), value.map(|item| item.to_string_lossy().into_owned()))).collect::<Vec<_>>();
        assert!(env.contains(&("MSBUILDDISABLENODEREUSE".to_owned(), Some("1".to_owned()))));
        assert!(env.contains(&("DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER".to_owned(), Some("1".to_owned()))));
    }

    struct PlainHttp;
    impl PlainHttp {
        fn enable() -> Self {
            ALLOW_PLAIN_HTTP.with(|flag| flag.set(true));
            Self
        }
    }
    impl Drop for PlainHttp {
        fn drop(&mut self) {
            ALLOW_PLAIN_HTTP.with(|flag| flag.set(false));
        }
    }

    /// Serves `payload`, closing early `drops` times, then honoring `Range`.
    fn serve_resumable(payload: &[u8], drops: usize) -> (u16, std::sync::Arc<std::sync::Mutex<Vec<Option<String>>>>) {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let port = listener.local_addr().unwrap().port();
        let ranges = std::sync::Arc::new(std::sync::Mutex::new(Vec::new()));
        let recorded = std::sync::Arc::clone(&ranges);
        let payload = payload.to_vec();
        thread::spawn(move || {
            for _ in 0..8 {
                let Ok((mut socket, _)) = listener.accept() else { break };
                socket.set_read_timeout(Some(Duration::from_secs(5))).ok();
                let mut request = Vec::new();
                let mut buffer = [0u8; 1024];
                loop {
                    match socket.read(&mut buffer) {
                        Ok(0) => break,
                        Ok(count) => {
                            request.extend_from_slice(&buffer[..count]);
                            if request.windows(4).any(|window| window == b"\r\n\r\n") {
                                break;
                            }
                        }
                        Err(_) => break,
                    }
                }
                let text = String::from_utf8_lossy(&request);
                let range = text.lines().find(|line| line.to_ascii_lowercase().starts_with("range:")).map(|line| line.to_owned());
                let start = range.as_deref().and_then(|line| line.split('=').nth(1)).and_then(|value| value.split('-').next()).and_then(|value| value.trim().parse::<usize>().ok()).unwrap_or(0);
                let seen = {
                    let mut guard = recorded.lock().unwrap();
                    guard.push(range);
                    guard.len()
                };
                let fail = seen <= drops;
                let end = if fail { (start + 10).min(payload.len().saturating_sub(1).max(start)) } else { payload.len() };
                let body = payload.get(start..end).unwrap_or_default();
                let header = if start == 0 {
                    format!("HTTP/1.1 200 OK\r\nContent-Length: {}\r\nConnection: close\r\n\r\n", payload.len())
                } else {
                    format!(
                        "HTTP/1.1 206 Partial Content\r\nContent-Range: bytes {start}-{}/{}\r\nContent-Length: {}\r\nConnection: close\r\n\r\n",
                        payload.len().saturating_sub(1),
                        payload.len(),
                        payload.len() - start
                    )
                };
                let _ = socket.write_all(header.as_bytes());
                let _ = socket.write_all(body);
            }
        });
        (port, ranges)
    }

    #[test]
    fn interrupted_download_resumes_from_the_partial() {
        let payload = b"abcdefghijklmnopqrstuvwxyz0123456789";
        let (port, ranges) = serve_resumable(payload, 2);
        let dir = env::temp_dir().join(format!("ascnet-resume-{}", uuid::Uuid::new_v4()));
        fs::create_dir(&dir).unwrap();
        let dest = dir.join("payload.bin");
        let sha = format!("{:x}", Sha256::digest(payload));
        let _http = PlainHttp::enable();
        let mut notes = Vec::new();
        let hash = download_file(
            &format!("http://127.0.0.1:{port}/payload.bin"),
            &dest,
            Some(&sha),
            None,
            &dir,
            Instant::now() + Duration::from_secs(20),
            &mut |_| {},
            &mut |line| {
                notes.push(line.to_owned());
                Ok(())
            },
        )
        .unwrap_or_else(|error| panic!("{error:#}\n{notes:?}"));
        assert_eq!(hash, sha);
        assert_eq!(fs::read(&dest).unwrap(), payload);
        assert!(!partial_path(&dest).exists());
        let seen = ranges.lock().unwrap().clone();
        assert!(seen.len() >= 3, "{seen:?}");
        assert!(seen[0].is_none(), "{seen:?}");
        assert!(seen[1].as_deref().is_some_and(|line| line.contains("bytes=10-")), "{seen:?}");
        assert!(seen[2].as_deref().is_some_and(|line| line.contains("bytes=20-")), "{seen:?}");
        assert!(notes.iter().any(|line| line.contains("retrying")), "{notes:?}");
        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn interrupted_text_download_resumes_from_received_bytes() {
        let payload = b"abcdefghijklmnopqrstuvwxyz0123456789";
        let (port, ranges) = serve_resumable(payload, 2);
        let _http = PlainHttp::enable();
        let mut notes = Vec::new();
        let text = read_download_text(
            &format!("http://127.0.0.1:{port}/channel"),
            Instant::now() + Duration::from_secs(20),
            &mut |_| {},
            &mut |line| {
                notes.push(line.to_owned());
                Ok(())
            },
        )
        .unwrap_or_else(|error| panic!("{error:#}\n{notes:?}"));
        assert_eq!(text.as_bytes(), payload);
        let seen = ranges.lock().unwrap().clone();
        assert!(seen.len() >= 3, "{seen:?}");
        assert!(seen[0].is_none(), "{seen:?}");
        assert!(seen[1].as_deref().is_some_and(|line| line.contains("bytes=10-")), "{seen:?}");
        assert!(seen[2].as_deref().is_some_and(|line| line.contains("bytes=20-")), "{seen:?}");
    }

    #[test]
    fn checksum_mismatch_discards_the_partial() {
        let (port, _) = serve_resumable(b"hello", 0);
        let dir = env::temp_dir().join(format!("ascnet-mismatch-{}", uuid::Uuid::new_v4()));
        fs::create_dir(&dir).unwrap();
        let dest = dir.join("payload.bin");
        let _http = PlainHttp::enable();
        let error = download_file(
            &format!("http://127.0.0.1:{port}/payload.bin"),
            &dest,
            Some("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
            None,
            &dir,
            Instant::now() + Duration::from_secs(20),
            &mut |_| {},
            &mut |_| Ok(()),
        )
        .unwrap_err();
        assert!(format!("{error:#}").contains("checksum mismatch"), "{error:#}");
        assert!(!partial_path(&dest).exists());
        assert!(!dest.exists());
        let _ = fs::remove_dir_all(&dir);
    }
}

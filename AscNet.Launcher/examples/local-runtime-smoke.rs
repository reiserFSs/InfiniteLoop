use anyhow::{bail, Context, Result};
use ascnet_launcher::local;
use std::{
    env,
    io::{self, Write},
    net::{TcpListener, TcpStream},
    path::PathBuf,
};

fn main() -> Result<()> {
    let appdata = PathBuf::from(
        env::args_os()
            .nth(1)
            .context("usage: local-runtime-smoke <isolated-LOCALAPPDATA>")?,
    );
    if !appdata.is_absolute() {
        bail!("isolated LOCALAPPDATA must be an absolute path");
    }
    if env::var_os("LOCALAPPDATA").is_some_and(|current| PathBuf::from(current) == appdata) {
        bail!("refusing the current user LOCALAPPDATA; pass an isolated prepared fixture");
    }
    env::set_var("LOCALAPPDATA", &appdata);

    let build = local::load_build()?.context("fixture build-state missing")?;
    let occupied = TcpListener::bind(("127.0.0.1", build.sdk_port))?;
    assert!(
        local::LocalRuntime::start(&build, &mut |_| {}).is_err(),
        "occupied port accepted"
    );
    assert!(
        TcpStream::connect(("127.0.0.1", build.sdk_port)).is_ok(),
        "foreign listener disturbed"
    );
    drop(occupied);

    let mut runtime = local::LocalRuntime::start(&build, &mut |line| println!("{line}"))?;
    println!("READY");
    io::stdout().flush()?;
    let mut command = String::new();
    io::stdin().read_line(&mut command)?;
    runtime.stop()?;

    for port in [build.sdk_port, build.game_port, build.mongo_port] {
        drop(
            TcpListener::bind(("127.0.0.1", port))
                .with_context(|| format!("owned listener remained on port {port} after stop"))?,
        );
    }
    println!("PASS");
    Ok(())
}

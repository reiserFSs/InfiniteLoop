#![cfg_attr(windows, windows_subsystem = "windows")]

#[cfg(windows)]
mod animation;
mod steam;
#[cfg(windows)]
mod ui;

use anyhow::{Context, Result};
use ascnet_launcher::{install, local, package};
use serde::Deserialize;
use serde_json::json;
use std::{
    env, fs,
    path::{Path, PathBuf},
};

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct DistributorConfig {
    repository_url: String,
    branch: String,
}

fn main() {
    if let Err(error) = run() {
        #[cfg(windows)]
        if env::args_os().len() == 1 {
            ui::show_fatal(&format!("{error:#}"));
        }
        eprintln!("{error:#}");
        std::process::exit(1);
    }
}

fn run() -> Result<()> {
    let args: Vec<String> = env::args().skip(1).collect();
    if args.is_empty() {
        #[cfg(windows)]
        {
            return ui::run();
        }
        #[cfg(not(windows))]
        anyhow::bail!("the graphical launcher is available on Windows only");
    }
    match args[0].as_str() {
        "--setup" => {
            require_len(&args, 2, "--setup <game-directory>")?;
            let game = PathBuf::from(&args[1]);
            if !steam::valid_game_directory(&game) { anyhow::bail!("game directory does not contain PGR.exe") }
            if install::game_running()? { anyhow::bail!("close PGR.exe before setup") }
            let config: DistributorConfig = serde_json::from_slice(
                &fs::read(executable_dir()?.join("launcher.json")).context("launcher.json is missing")?,
            ).context("launcher.json is invalid")?;
            let build = local::prepare(&config.repository_url, &config.branch, &mut |line| eprintln!("{line}"))?;
            let package = package::load_package(&build.patch_directory)?;
            install::install(&game, &package, &mut |line| eprintln!("{line}"))?;
            println!("{}", build.revision);
        }
        "--inspect" => {
            require_len(&args, 2, "--inspect <game-directory>")?;
            let package = built_package()?;
            let state = install::inspect(Path::new(&args[1]), &package)?;
            println!("{}", serde_json::to_string_pretty(&state_json(state))?);
        }
        "--install" => {
            require_len(&args, 2, "--install <game-directory>")?;
            let package = built_package()?;
            let backup = install::install(Path::new(&args[1]), &package, &mut |line| eprintln!("{line}"))?;
            println!("{}", backup.display());
        }
        "--restore" => {
            require_len(&args, 2, "--restore <game-directory>")?;
            install::restore(Path::new(&args[1]), &mut |line| eprintln!("{line}"))?;
        }
        "--check-server" => {
            require_len(&args, 2, "--check-server <origin>")?;
            let origin = package::validate_server_origin(&args[1])?;
            let response = reqwest::blocking::Client::builder().no_proxy().build()?
                .get(format!("{origin}/api/launcher/status")).send()?.error_for_status()?;
            let value: serde_json::Value = response.json()?;
            println!("{}", serde_json::to_string_pretty(&value)?);
        }
        _ => anyhow::bail!("usage: ascnet-launcher [--setup GAME | --inspect GAME | --install GAME | --restore GAME | --check-server ORIGIN]"),
    }
    Ok(())
}

fn built_package() -> Result<package::PatchPackage> {
    let build = local::load_build()?.context("no local build; run --setup first")?;
    package::load_package(&build.patch_directory)
}

fn executable_dir() -> Result<PathBuf> {
    Ok(env::current_exe()
        .context("locating launcher executable")?
        .parent()
        .context("launcher executable has no parent directory")?
        .to_path_buf())
}

fn require_len(args: &[String], len: usize, usage: &str) -> Result<()> {
    if args.len() != len {
        anyhow::bail!("usage: ascnet-launcher {usage}")
    }
    Ok(())
}

fn state_json(state: install::PatchState) -> serde_json::Value {
    match state {
        install::PatchState::Unpatched => json!({"state":"unpatched"}),
        install::PatchState::Current => json!({"state":"current"}),
        install::PatchState::UpdateAvailable => json!({"state":"updateAvailable"}),
        install::PatchState::AdoptionRequired => json!({"state":"adoptionRequired"}),
        install::PatchState::Unsupported(reason) => json!({"state":"unsupported","reason":reason}),
        install::PatchState::RepairRequired(reason) => {
            json!({"state":"repairRequired","reason":reason})
        }
    }
}

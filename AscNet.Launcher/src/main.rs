#![cfg_attr(windows, windows_subsystem = "windows")]

mod controller;
mod steam;
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
    #[cfg(windows)]
    {
        let args: Vec<String> = env::args().skip(1).collect();
        if args.first().is_some_and(|arg| arg == "--patch-worker") {
            std::process::exit(install::patch_worker(&args));
        }
    }
    if let Err(error) = run() {
        #[cfg(windows)]
        if env::args_os().len() == 1 {
            ui::show_fatal(&format!("{error:#}"));
        }
        eprintln!("{}", local::logged_error(&format!("{error:#}")));
        std::process::exit(1);
    }
}

fn run() -> Result<()> {
    if ascnet_launcher::updater::startup()? {
        return Ok(());
    }
    local::launcher_log("Launcher started")?;
    let args: Vec<String> = env::args().skip(1)
        .filter(|arg| arg != "--self-update-health" && arg != "--self-update-rolled-back").collect();
    if args.is_empty() {
        return ui::run();
    }
    match args[0].as_str() {
        // Packaging gate: accepts only a background video the launcher's own decoder can play (no B-frames).
        "--check-media" => {
            require_len(&args, 2, "--check-media <background.mp4>")?;
            ascnet_launcher::media::Video::start(Path::new(&args[1]), |_| {})?;
        }
        "--setup" => {
            require_len(&args, 2, "--setup <game-directory>")?;
            let game = PathBuf::from(&args[1]);
            if !steam::valid_game_directory(&game) { anyhow::bail!("game directory does not contain PGR.exe") }
            if install::game_running()? { anyhow::bail!("close PGR.exe before setup") }
            let config: DistributorConfig = serde_json::from_slice(
                &fs::read(executable_dir()?.join("launcher.json")).context("launcher.json is missing")?,
            ).context("launcher.json is invalid")?;
            let build = local::prepare(&config.repository_url, &config.branch, &game, &mut |line| eprintln!("{line}"))?;
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
            let backup = install::install_with_consent(Path::new(&args[1]), &package, &mut |line| eprintln!("{line}"))?;
            println!("{}", backup.display());
        }
        "--restore" => {
            require_len(&args, 2, "--restore <game-directory>")?;
            install::restore_with_consent(Path::new(&args[1]), &mut |line| eprintln!("{line}"))?;
        }
        "--check-server" => {
            require_len(&args, 2, "--check-server <origin>")?;
            let origin = package::validate_server_origin(&args[1])?;
            let response = ascnet_launcher::download::wine_safe(reqwest::blocking::Client::builder().no_proxy()).build()?
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

/// Where `launcher.json` and the other shipped files are.
fn executable_dir() -> Result<PathBuf> {
    local::resource_dir()
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

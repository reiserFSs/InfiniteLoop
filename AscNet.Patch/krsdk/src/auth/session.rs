use crate::types::UserSession;
use serde::{Deserialize, Serialize};
use std::path::PathBuf;

/// Last successful KRSDK login. The password is never written here.
#[derive(Serialize, Deserialize)]
struct StoredSession {
    username: String,
    token: String,
    uid: String,
}

fn path() -> Option<PathBuf> {
    let base = std::env::var_os("LOCALAPPDATA")?;
    Some(PathBuf::from(base).join("AscNet").join("krsdk-session.json"))
}

pub fn load() -> Option<UserSession> {
    let text = std::fs::read_to_string(path()?).ok()?;
    let stored: StoredSession = serde_json::from_str(&text).ok()?;
    if stored.username.is_empty() || stored.token.is_empty() || stored.uid.is_empty() {
        return None;
    }
    Some(UserSession {
        username: stored.username,
        token: stored.token,
        uid: stored.uid,
    })
}

pub fn save(session: &UserSession) {
    let Some(path) = path() else {
        return;
    };
    if let Some(parent) = path.parent() {
        let _ = std::fs::create_dir_all(parent);
    }
    let stored = StoredSession {
        username: session.username.clone(),
        token: session.token.clone(),
        uid: session.uid.clone(),
    };
    if let Ok(text) = serde_json::to_string(&stored) {
        let _ = std::fs::write(path, text);
    }
}

pub fn clear() {
    if let Some(path) = path() {
        let _ = std::fs::remove_file(path);
    }
}

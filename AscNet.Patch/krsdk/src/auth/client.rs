use crate::types::*;
use serde::Serialize;

#[derive(Serialize)]
struct VerifyRequest {
    token: String,
}

pub enum VerifyFailure {
    Rejected(String),
    Unavailable(String),
}

fn account_from(status: reqwest::StatusCode, data: LoginResponse) -> Result<UserSession, String> {
    if !status.is_success() || data.code != 0 {
        return Err(data.msg);
    }
    let account = data
        .account
        .ok_or_else(|| "No account data returned".to_string())?;
    Ok(UserSession {
        username: account.username,
        token: account.token,
        uid: account.uid.to_string(),
    })
}

fn origin() -> String {
    std::env::var("ASCNET_PATCH_ORIGIN").unwrap_or_else(|_| "http://127.0.0.1:8080".to_string())
}

/// Confirms a saved account token with the existing `/api/AscNet/verify` route.
pub fn verify(token: &str) -> Result<UserSession, VerifyFailure> {
    if token.is_empty() {
        return Err(VerifyFailure::Rejected("Missing token".to_string()));
    }
    let client = reqwest::blocking::Client::builder()
        .no_proxy()
        .build()
        .map_err(|error| VerifyFailure::Unavailable(format!("Network client error: {error}")))?;
    let response = client
        .post(format!(
            "{}/api/AscNet/verify",
            origin().trim_end_matches('/')
        ))
        .json(&VerifyRequest {
            token: token.to_string(),
        })
        .send()
        .map_err(|error| VerifyFailure::Unavailable(format!("Network error: {error}")))?;
    let status = response.status();
    let data: LoginResponse = response.json().map_err(|error| {
        VerifyFailure::Unavailable(format!("Invalid server response ({status}): {error}"))
    })?;
    account_from(status, data).map_err(VerifyFailure::Rejected)
}

fn authenticate(path: &str, username: &str, password: &str) -> Result<UserSession, String> {
    let client = reqwest::blocking::Client::builder()
        .no_proxy()
        .build()
        .map_err(|e| format!("Network client error: {e}"))?;
    let response = client
        .post(format!(
            "{}/api/AscNet/{}",
            origin().trim_end_matches('/'),
            path
        ))
        .json(&LoginRequest {
            username: username.to_string(),
            password: password.to_string(),
        })
        .send()
        .map_err(|e| format!("Network error: {e}"))?;
    let status = response.status();
    let data: LoginResponse = response
        .json()
        .map_err(|e| format!("Invalid server response ({status}): {e}"))?;
    account_from(status, data)
}

pub fn login(username: &str, password: &str) -> Result<UserSession, String> {
    authenticate("login", username, password)
}

pub fn register(username: &str, password: &str) -> Result<UserSession, String> {
    authenticate("register", username, password)
}

use crate::types::*;

fn authenticate(path: &str, username: &str, password: &str) -> Result<UserSession, String> {
    let origin = std::env::var("ASCNET_PATCH_ORIGIN")
        .unwrap_or_else(|_| "http://127.0.0.1:8080".to_string());
    let client = reqwest::blocking::Client::builder()
        .no_proxy()
        .build()
        .map_err(|e| format!("Network client error: {e}"))?;
    let response = client
        .post(format!("{}/api/AscNet/{}", origin.trim_end_matches('/'), path))
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

pub fn login(username: &str, password: &str) -> Result<UserSession, String> {
    authenticate("login", username, password)
}

pub fn register(username: &str, password: &str) -> Result<UserSession, String> {
    authenticate("register", username, password)
}

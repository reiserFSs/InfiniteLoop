pub mod main_window;
pub mod login_dialog;
pub mod register_dialog;

use crate::globals::send_callback;
use crate::types::UserSession;
use crate::exports::config::packaged_sdk_config;
use once_cell::sync::Lazy;
use std::sync::Mutex;
use windows::Win32::Foundation::HWND;

pub static PARENT_HWND: Lazy<Mutex<Option<HWND>>> = Lazy::new(|| Mutex::new(None));
pub static SESSION: Lazy<Mutex<Option<UserSession>>> = Lazy::new(|| Mutex::new(None));

pub fn finish_login(session: UserSession) {
    let account_channel_id = match packaged_sdk_config()
        .and_then(|config| {
            config
                .get("KR_ChannelID")
                .cloned()
                .ok_or_else(|| "packaged SDK config is missing KR_ChannelID".to_string())
        }) {
        Ok(channel_id) => channel_id,
        Err(error) => {
            eprintln!("[KRSDK] Failed to read login channel: {error}");
            return;
        }
    };
    let response = serde_json::json!({
        "data": {
            "accessToken": session.token,
            "accountChannelId": account_channel_id,
            "cuid": session.uid,
            "loginType": "account",
            "userName": session.username
        },
        "isSuccessful": true,
        "msg": "",
        "statusCode": 0
    });
    *SESSION.lock().unwrap() = Some(session);
    send_callback("LOGIN", &response.to_string());
}

pub fn logout() {
    *SESSION.lock().unwrap() = None;
    send_callback("LOGOUT", "");
}

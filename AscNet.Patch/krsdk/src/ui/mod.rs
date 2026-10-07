pub mod main_window;
pub mod login_dialog;
pub mod register_dialog;

use crate::globals::send_callback;
use crate::types::UserSession;
use once_cell::sync::Lazy;
use std::sync::Mutex;
use windows::Win32::Foundation::HWND;

pub static PARENT_HWND: Lazy<Mutex<Option<HWND>>> = Lazy::new(|| Mutex::new(None));
pub static SESSION: Lazy<Mutex<Option<UserSession>>> = Lazy::new(|| Mutex::new(None));

pub fn current_session() -> Option<UserSession> {
    SESSION.lock().unwrap().clone()
}

pub fn finish_login(session: UserSession) -> bool {
    let account_channel_id = match crate::exports::sdk_identity::read_packaged()
        .and_then(|config| {
            config
                .get("KR_ChannelID")
                .cloned()
                .ok_or_else(|| "packaged SDK config is missing KR_ChannelID".to_string())
        }) {
        Ok(channel_id) => channel_id,
        Err(error) => {
            crate::diag::log(&crate::diag::failed_line("KRSDK", "login channel from KRSDK.bin", &error));
            return false;
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
    crate::auth::session::save(&session);
    *SESSION.lock().unwrap() = Some(session);
    send_callback("LOGIN", &response.to_string());
    true
}

pub fn logout() {
    *SESSION.lock().unwrap() = None;
    crate::auth::session::clear();
    send_callback("LOGOUT", "");
}

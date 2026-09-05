use serde::{Deserialize, Serialize};

#[derive(Clone, Debug)]
#[allow(dead_code)]
pub struct UserSession {
    pub username: String,
    pub token: String,
    pub uid: String,
}

#[derive(Serialize)]
pub struct LoginRequest {
    pub username: String,
    pub password: String,
}

#[derive(Deserialize)]
pub struct LoginResponse {
    pub code: i32,
    pub msg: String,
    pub account: Option<AccountData>,
}

#[derive(Deserialize, Debug)]
pub struct AccountData {
    #[serde(rename = "Username")]
    pub username: String,

    #[serde(rename = "Token")]
    pub token: String,

    #[serde(rename = "Uid")]
    pub uid: i64,
}

pub const ID_BTN_LOGIN: i32 = 10001;
pub const ID_BTN_REGISTER: i32 = 10002;
pub const ID_EDIT_USERNAME: i32 = 1001;
pub const ID_EDIT_PASSWORD: i32 = 1002;
pub const ID_BTN_SUBMIT: i32 = 1; // IDOK, used by IsDialogMessage for Enter.
pub const ID_BTN_CANCEL: i32 = 2; // IDCANCEL, used by IsDialogMessage for Escape.

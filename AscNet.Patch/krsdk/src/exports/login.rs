use crate::globals::{GAME_HWND, SDK_INIT_FLAG_1, SDK_INIT_FLAG_2};
use crate::ui;

use std::sync::atomic::Ordering;

#[no_mangle]
pub extern "C" fn kurosdk_login() {
    println!("[KRSDK] *** kurosdk_login called ***");

    if !SDK_INIT_FLAG_1.load(Ordering::SeqCst) || !SDK_INIT_FLAG_2.load(Ordering::SeqCst) {
        println!("[KRSDK] ERROR: fail to open login dialog, has not init success");
        return;
    }

    let parent_hwnd = *GAME_HWND.lock().unwrap();
    unsafe {
        if let Some(hwnd) = parent_hwnd {
            ui::main_window::show_with_parent(hwnd);
        } else {
            ui::main_window::show();
        }
    }
}

#[no_mangle]
pub extern "C" fn kr_sdk_login() -> i64 {
    kurosdk_login();
    0
}

#[no_mangle]
pub extern "C" fn kr_sdk_logout() -> u8 {
    println!("[KRSDK] *** Logout called ***");
    ui::logout();
    0
}

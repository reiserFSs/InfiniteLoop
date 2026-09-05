use crate::util::dll_dir;
use libloading::Library;
use once_cell::sync::Lazy;
use std::ffi::{c_void, CString};
use std::os::raw::c_char;
use std::sync::atomic::AtomicBool;
use std::sync::Mutex;
use windows::Win32::Foundation::HWND;

pub static CALLBACK: Lazy<Mutex<Option<extern "C" fn(*const c_char, *const c_char)>>> =
    Lazy::new(|| Mutex::new(None));

pub fn send_callback(event: &str, data: &str) -> bool {
    let callback = CALLBACK.lock().ok().and_then(|guard| *guard);
    let (Ok(event), Ok(data)) = (CString::new(event), CString::new(data)) else {
        return false;
    };
    if let Some(callback) = callback {
        callback(event.as_ptr(), data.as_ptr());
        true
    } else {
        false
    }
}

pub struct SendPtr(pub *mut c_char);
unsafe impl Send for SendPtr {}

pub static ALLOCATED_STRINGS: Lazy<Mutex<Vec<SendPtr>>> = Lazy::new(|| Mutex::new(Vec::new()));

pub static LANGUAGE: Lazy<Mutex<String>> = Lazy::new(|| Mutex::new(String::from("en")));

pub static GAME_HWND: Lazy<Mutex<Option<HWND>>> = Lazy::new(|| Mutex::new(None));

pub static AGREEMENT_DATA: Lazy<Mutex<Option<String>>> = Lazy::new(|| Mutex::new(None));
pub static SDK_CONFIG: Lazy<Mutex<Option<String>>> = Lazy::new(|| Mutex::new(None));

pub static AGREEMENT_CSTRING: Lazy<Mutex<Option<CString>>> = Lazy::new(|| Mutex::new(None));
pub static CONFIG_CSTRING: Lazy<Mutex<Option<CString>>> = Lazy::new(|| Mutex::new(None));

pub const DLL_PROCESS_ATTACH: u32 = 1;
pub const DLL_PROCESS_DETACH: u32 = 0;

pub static SDK_INITIALIZED: AtomicBool = AtomicBool::new(false);

pub static SDK_INIT_FLAG_1: AtomicBool = AtomicBool::new(false);
pub static SDK_INIT_FLAG_2: AtomicBool = AtomicBool::new(false);

// The game's curl library didnt work for us so we use libcurl instead
pub static CURL_LIB: Lazy<Library> = Lazy::new(|| {
    let mut path = dll_dir();
    path.push("PGR_Data");
    path.push("Plugins");
    path.push("libcurl.dll");

    println!("[KRSDK] Loading curl from {:?}", path);

    unsafe { Library::new(path).expect("Unable to load curl DLL") }
});

#[no_mangle]
pub extern "C" fn kurosdk_setGameHwnd(hwnd: *mut c_void) {
    println!(
        "[KRSDK] *** kurosdk_setGameHwnd called with HWND: {:?} ***",
        hwnd
    );

    if !hwnd.is_null() {
        *GAME_HWND.lock().unwrap() = Some(HWND(hwnd as isize));
        println!("[KRSDK] Game HWND stored");
    }
}

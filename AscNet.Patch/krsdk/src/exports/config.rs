use crate::globals::{SendPtr, ALLOCATED_STRINGS, LANGUAGE};
use std::ffi::CString;
use std::os::{raw::c_char, windows::ffi::OsStrExt};
use windows::{
    core::{s, PCSTR, PCWSTR},
    Win32::{
        Foundation::HMODULE,
        System::LibraryLoader::{
            GetModuleHandleA, GetProcAddress, LoadLibraryExW,
            LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR, LOAD_LIBRARY_SEARCH_SYSTEM32,
        },
    },
};

pub(crate) fn packaged_sdk_config() -> Result<std::collections::HashMap<String, String>, String> {
    let exe = std::env::current_exe().map_err(|error| error.to_string())?;
    let path = exe
        .parent()
        .ok_or_else(|| "game executable has no parent directory".to_string())?
        .join("PGR_Data/Plugins/KRSDKRes/KRSDK.bin");
    let text = std::fs::read_to_string(&path)
        .map_err(|error| format!("{}: {error}", path.display()))?;

    Ok(text
        .lines()
        .filter_map(|line| line.trim_end_matches('\r').split_once('='))
        .map(|(key, value)| (key.to_string(), value.to_string()))
        .collect())
}

fn packaged_config() -> Result<serde_json::Value, String> {
    let config = packaged_sdk_config()?;
    let get = |key: &str| {
        config
            .get(key)
            .cloned()
            .ok_or_else(|| format!("packaged SDK config is missing {key}"))
    };

    Ok(serde_json::json!({
        "channelId": get("KR_ChannelID")?,
        "channelName": get("KR_ChannelName")?,
        "channelOp": get("KR_ChannelOp")?,
        "gameId": get("KR_ProjectId")?,
        "pkgId": get("KR_ProductId")?
    }))
}

fn initialize_routing() -> Result<(), &'static str> {
    unsafe {
        let module = match GetModuleHandleA(s!("lucia.dll")) {
            Ok(module) => module,
            Err(_) => {
                let exe = std::env::current_exe()
                    .map_err(|_| "could not determine the game executable path")?;
                let path = exe
                    .parent()
                    .ok_or("game executable has no parent directory")?
                    .join("lucia.dll");
                let path: Vec<u16> = path
                    .as_os_str()
                    .encode_wide()
                    .chain(std::iter::once(0))
                    .collect();
                LoadLibraryExW(
                    PCWSTR(path.as_ptr()),
                    None,
                    LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32,
                )
                .map_err(|_| "could not load client-root lucia.dll")?
            }
        };
        let initialize =
            GetProcAddress::<HMODULE, PCSTR>(module, s!("ascnet_patch_initialize"))
                .ok_or("lucia.dll is missing ascnet_patch_initialize")?;
        let initialize: unsafe extern "system" fn() -> i32 = std::mem::transmute(initialize);
        (initialize() == 1)
            .then_some(())
            .ok_or("lucia.dll routing initialization failed")
    }
}

#[no_mangle]
pub extern "C" fn kurosdk_getConfigInfo() -> *mut c_char {
    println!("[KRSDK] *** kurosdk_getConfigInfo called ***");
    if let Err(error) = initialize_routing() {
        eprintln!("[KRSDK] {error}");
        return std::ptr::null_mut();
    }
    let config = packaged_config().unwrap_or_else(|error| {
        eprintln!("[KRSDK] Failed to read packaged config: {error}");
        serde_json::json!({})
    });

    let json_str = config.to_string();
    println!("[KRSDK] Returning config: {}", json_str);

    let c_str = CString::new(json_str).unwrap();
    let ptr = c_str.into_raw();

    ALLOCATED_STRINGS.lock().unwrap().push(SendPtr(ptr));
    ptr
}

#[no_mangle]
pub extern "C" fn kurosdk_getDeviceInfo() -> *mut c_char {
    println!("[KRSDK] *** kurosdk_getDeviceInfo called ***");

    let device_info = serde_json::json!({
        "did": uuid::Uuid::new_v4().to_string(),
        "idfv": "",
        "jyDid": "",
        "oaId": ""
    });

    let json_str = device_info.to_string();
    println!("[KRSDK] Returning device info: {}", json_str);

    let c_str = CString::new(json_str).unwrap();
    let ptr = c_str.into_raw();

    // Keep track of allocated strings
    ALLOCATED_STRINGS.lock().unwrap().push(SendPtr(ptr));

    ptr
}

#[no_mangle]
pub extern "C" fn kurosdk_getProtocolInfo() -> *mut c_char {
    println!("[KRSDK] *** Get Protocol Info called ***");

    let protocol_info = serde_json::json!({"data": []});

    let json_str = protocol_info.to_string();
    println!("[KRSDK] Returning protocol info: {}", json_str);

    let c_ctr = CString::new(json_str).unwrap();
    let ptr = c_ctr.into_raw();

    ALLOCATED_STRINGS.lock().unwrap().push(SendPtr(ptr));
    ptr
}

#[no_mangle]
pub extern "C" fn kurosdk_setLanguage(data: *const c_char) {
    println!("[KRSDK] *** kurosdk_setLanguage called ***");

    if data.is_null() {
        println!("[KRSDK] setLanguage: null data");
        return;
    }

    unsafe {
        let c_str = std::ffi::CStr::from_ptr(data);
        if let Ok(json_str) = c_str.to_str() {
            println!("[KRSDK] setLanguage data: {}", json_str);

            // Parse JSON to get language
            if let Ok(json) = serde_json::from_str::<serde_json::Value>(json_str) {
                if let Some(lang) = json.get("language").and_then(|v| v.as_str()) {
                    *LANGUAGE.lock().unwrap() = lang.to_string();
                    println!("[KRSDK] Language set to: {}", lang);
                }
            }
        }
    }
}

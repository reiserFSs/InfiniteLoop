use crate::globals::{
    send_callback, AGREEMENT_CSTRING, AGREEMENT_DATA, CALLBACK, CONFIG_CSTRING,
    DLL_PROCESS_ATTACH, DLL_PROCESS_DETACH, SDK_CONFIG, SDK_INITIALIZED, SDK_INIT_FLAG_1,
    SDK_INIT_FLAG_2,
};
use std::ffi::{c_char, CString};
use std::os::raw::c_void;
use std::sync::atomic::Ordering;
#[allow(unused_imports)]
use windows::Win32::System::Console::{AllocConsole, FreeConsole};

#[no_mangle]
#[allow(non_snake_case)]
pub extern "system" fn DllMain(
    _hinst_dll: *mut c_void,
    fdw_reason: u32,
    _lpv_reserved: *mut c_void,
) -> i32 {
    match fdw_reason {
        DLL_PROCESS_ATTACH => {
            #[cfg(windows)]
            #[allow(unused_unsafe)]
            unsafe {
                //let _ = AllocConsole();
                println!("[KRSDK] DLL Loaded - Console initialized");
            }
        }
        DLL_PROCESS_DETACH => {
            #[cfg(windows)]
            #[allow(unused_unsafe)]
            unsafe {
                println!("[KRSDK] DLL Unloading");
                //let _ = FreeConsole();
            }
        }
        _ => {}
    }
    1
}

fn local_asset(contents: &str) -> String {
    let origin = std::env::var("ASCNET_PATCH_ORIGIN")
        .unwrap_or_else(|_| "http://127.0.0.1:8080".to_string());
    contents.replace("http://127.0.0.1:8080", origin.trim_end_matches('/'))
}

#[no_mangle]
pub extern "C" fn kurosdk_initSdk() {
    println!("[KRSDK] *** kurosdk_initSdk called ***");

    std::thread::spawn(|| {
        *AGREEMENT_DATA.lock().unwrap() =
            Some(local_asset(include_str!("../../assets/agreement.json")));
        *SDK_CONFIG.lock().unwrap() = Some(local_asset(include_str!("../../assets/conf.json")));

        SDK_INITIALIZED.store(true, Ordering::SeqCst);
        SDK_INIT_FLAG_1.store(true, Ordering::SeqCst);
        SDK_INIT_FLAG_2.store(true, Ordering::SeqCst);

        send_callback("INIT_SDK_FINISHED", "");
        println!("[KRSDK] Init complete - login should be available");
    });
}

#[no_mangle]
pub extern "C" fn kurosdk_registerSdkGlobalCallback(
    callback: extern "C" fn(*const c_char, *const c_char),
) {
    println!(
        "[KRSDK] *** kurosdk_registerSdkGlobalCallback called - callback at: {:?} ***",
        callback as *const ()
    );
    *CALLBACK.lock().unwrap() = Some(callback);
}

#[no_mangle]
pub extern "C" fn kurosdk_unregisterSdkGlobalCallback() {
    println!("[KRSDK] *** kurosdk_unregisterSdkGlobalCallback called",);
    *CALLBACK.lock().unwrap() = None;
}

#[no_mangle]
pub extern "C" fn kurosdk_initEnv() -> u8 {
    println!("[KRSDK] *** kurosdk_initEnv called ***");
    1
}

#[no_mangle]
pub extern "C" fn kr_sdk_initialize(
    a1: *const c_char,
    a2: *const c_char,
    a3: *const c_char,
    a4: *const c_char,
    a5: *const c_char,
) -> u32 {
    println!("[KRSDK] *** kr_sdk_initialize called ***");

    unsafe {
        if !a1.is_null() {
            if let Ok(s) = std::ffi::CStr::from_ptr(a1).to_str() {
                println!("[KRSDK]   param1: {}", s);
            }
        }
        if !a2.is_null() {
            if let Ok(s) = std::ffi::CStr::from_ptr(a2).to_str() {
                println!("[KRSDK]   param2: {}", s);
            }
        }
        if !a3.is_null() {
            if let Ok(s) = std::ffi::CStr::from_ptr(a3).to_str() {
                println!("[KRSDK]   param3: {}", s);
            }
        }
        if !a4.is_null() {
            if let Ok(s) = std::ffi::CStr::from_ptr(a4).to_str() {
                println!("[KRSDK]   param4: {}", s);
            }
        }
        if !a5.is_null() {
            if let Ok(s) = std::ffi::CStr::from_ptr(a5).to_str() {
                println!("[KRSDK]   param5: {}", s);
            }
        }
    }

    0
}

#[no_mangle]
pub extern "C" fn kr_sdk_null_initialize() -> u32 {
    println!("[KRSDK] *** kr_sdk_null_initialize called ***");

    0
}

#[no_mangle]
pub extern "C" fn kurosdk_getAgreementData() -> *const c_char {
    println!("[KRSDK] *** kurosdk_getAgreementData called ***");

    if let Some(data) = AGREEMENT_DATA.lock().unwrap().as_ref() {
        let cstring = CString::new(data.as_str()).unwrap();
        let ptr = cstring.as_ptr();
        *AGREEMENT_CSTRING.lock().unwrap() = Some(cstring);
        ptr
    } else {
        std::ptr::null()
    }
}

#[no_mangle]
pub extern "C" fn kurosdk_getSdkConfig() -> *const c_char {
    println!("[KRSDK] *** kurosdk_getSdkConfig called ***");

    if let Some(data) = SDK_CONFIG.lock().unwrap().as_ref() {
        let cstring = CString::new(data.as_str()).unwrap();
        let ptr = cstring.as_ptr();
        *CONFIG_CSTRING.lock().unwrap() = Some(cstring);
        ptr
    } else {
        std::ptr::null()
    }
}

use crate::globals::{ALLOCATED_STRINGS};
use std::os::raw::{c_void, c_char};
use std::ffi::CString;

#[no_mangle]
pub extern "C" fn kurosdk_freeKRPluginMemory(ptr: *mut c_void) {

    println!("[KRSDK] *** kurosdk_freeKRPluginMemory called with ptr: {:?} ***", ptr);

    if ptr.is_null() {
        return;
    }

    unsafe {
        let mut allocated = ALLOCATED_STRINGS.lock().unwrap();
        if let Some(pos) = allocated.iter().position(|p| p.0 == ptr as *mut c_char) {
            allocated.remove(pos);
            let _ = CString::from_raw(ptr as *mut c_char);
            println!("[KRSDK] Freed CString");
        }
    }
}

#[no_mangle]
pub extern "C" fn kurosdk_free(ptr: *mut c_void) -> u8 {
    println!("[KRSDK] *** kurosdk_free called with ptr: {:?} ***", ptr);
    if ptr.is_null() {
        return 0;
    }

    unsafe {
        let mut allocated = ALLOCATED_STRINGS.lock().unwrap();
        if let Some(pos) = allocated.iter().position(|p| p.0 == ptr as *mut c_char) {
            allocated.remove(pos);
            let _ = CString::from_raw(ptr as *mut c_char);
            return 1;
        }
    }
    0
}

#[no_mangle]
pub extern "C" fn KuroSdkPc_getAndFreeString(ptr: *mut c_char) -> *mut c_char {
    if kurosdk_free(ptr.cast()) != 0 {
        std::ptr::null_mut()
    } else {
        ptr
    }
}

use crate::globals::CURL_LIB;
use libloading::Symbol;
use std::os::raw::{c_char, c_int, c_long, c_void};

// Map KRSDK’s function names to standard libcurl functions
type FnEasyInit    = unsafe extern "C" fn() -> *mut c_void;
type FnEasyCleanup = unsafe extern "C" fn(*mut c_void);
type FnEasySetopt  = unsafe extern "C" fn(*mut c_void, c_int, *mut c_void) -> c_int;
type FnEasyPerform = unsafe extern "C" fn(*mut c_void) -> c_int;
type FnEasyGetinfo = unsafe extern "C" fn(*mut c_void, c_int, *mut c_void) -> c_int;

type FnSlistAppend = unsafe extern "C" fn(*mut c_void, *const c_char) -> *mut c_void;
type FnSlistFree   = unsafe extern "C" fn(*mut c_void);

type FnEscape      = unsafe extern "C" fn(*mut c_void, *const c_char, c_int) -> *mut c_char;
type FnGlobalInit  = unsafe extern "C" fn(c_long) -> c_int;


#[no_mangle]
pub extern "C" fn kr_sdk_curl_easy_init() -> *mut c_void {
    unsafe {
        let f: Symbol<FnEasyInit> =
            CURL_LIB.get(b"curl_easy_init\0").expect("missing curl_easy_init");
        f()
    }
}


#[no_mangle]
pub extern "C" fn kr_sdk_curl_easy_setopt(
    h: *mut c_void,
    opt: c_int,
    param: *mut c_void,
) -> c_int {
    unsafe {
        let f: Symbol<FnEasySetopt> =
            CURL_LIB.get(b"curl_easy_setopt\0").expect("missing curl_easy_setopt");
        f(h, opt, param)
    }
}

#[no_mangle]
pub extern "C" fn kr_sdk_curl_easy_perform(h: *mut c_void) -> c_int {
    unsafe {
        let f: Symbol<FnEasyPerform> =
            CURL_LIB.get(b"curl_easy_perform\0").expect("missing curl_easy_perform");
        f(h)
    }
}


#[no_mangle]
pub extern "C" fn kr_sdk_curl_easy_cleanup(h: *mut c_void) {
    unsafe {
        let f: Symbol<FnEasyCleanup> =
            CURL_LIB.get(b"curl_easy_cleanup\0").expect("missing curl_easy_cleanup");
        f(h)
    }
}


#[no_mangle]
pub extern "C" fn kr_sdk_curl_easy_getinfo(
    h: *mut c_void,
    info: c_int,
    out: *mut c_void,
) -> c_int {
    unsafe {
        let f: Symbol<FnEasyGetinfo> =
            CURL_LIB.get(b"curl_easy_getinfo\0").expect("missing curl_easy_getinfo");
        f(h, info, out)
    }
}

#[no_mangle]
pub extern "C" fn kr_sdk_curl_slist_append(
    list: *mut c_void,
    s: *const c_char,
) -> *mut c_void {
    unsafe {
        let f: Symbol<FnSlistAppend> =
            CURL_LIB.get(b"curl_slist_append\0").expect("missing curl_slist_append");
        f(list, s)
    }
}


#[no_mangle]
pub extern "C" fn kr_sdk_curl_slist_free_all(list: *mut c_void) {
    unsafe {
        let f: Symbol<FnSlistFree> =
            CURL_LIB.get(b"curl_slist_free_all\0").expect("missing curl_slist_free_all");
        f(list)
    }
}

#[no_mangle]
pub extern "C" fn kr_sdk_curl_easy_escape(
    h: *mut c_void,
    s: *const c_char,
    len: c_int
) -> *mut c_char {
    unsafe {
        let f: Symbol<FnEscape> =
            CURL_LIB.get(b"curl_easy_escape\0").expect("missing curl_easy_escape");
        f(h, s, len)
    }
}


#[no_mangle]
pub extern "C" fn kr_sdk_curl_global_init(flags: c_long) -> c_int {
    unsafe {
        let f: Symbol<FnGlobalInit> =
            CURL_LIB.get(b"curl_global_init\0").expect("missing curl_global_init");
        f(flags)
    }
}

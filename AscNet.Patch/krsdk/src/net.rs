use std::ffi::CString;
use std::os::raw::{c_char, c_int, c_void};

extern "C" {
    fn kr_sdk_curl_easy_init() -> *mut c_void;
    fn kr_sdk_curl_easy_setopt(h: *mut c_void, opt: c_int, param: *mut c_void) -> c_int;
    fn kr_sdk_curl_easy_perform(h: *mut c_void) -> c_int;
    fn kr_sdk_curl_easy_cleanup(h: *mut c_void);
    fn kr_sdk_curl_easy_getinfo(h: *mut c_void, info: c_int, out: *mut c_void) -> c_int;
    fn kr_sdk_curl_slist_append(lst: *mut c_void, s: *const c_char) -> *mut c_void;
    fn kr_sdk_curl_slist_free_all(lst: *mut c_void);
}

// ---- curl enums we need ----
const CURLOPT_HTTPAUTH: c_int = 99;
const CURLOPT_NOPROGRESS: c_int = 43;
const CURLOPT_FOLLOWLOCATION: c_int = 52;
const CURLOPT_MAXREDIRS: c_int = 68;
const CURLOPT_URL: c_int = 10002;
const CURLOPT_NOBODY: c_int = 44;
const CURLOPT_WRITEFUNCTION: c_int = 20011;
const CURLOPT_WRITEDATA: c_int = 10001;
const CURLOPT_HTTPHEADER: c_int = 10023;
const CURLOPT_DNS_USE_GLOBAL_CACHE: c_int = 91;
const CURLOPT_DNS_CACHE_TIMEOUT: c_int = 92;

const CURLINFO_RESPONSE_CODE: c_int = 0x200000 + 2; // 2097154

const CURLOPT_POST: c_int = 47;
const CURLOPT_POSTFIELDS: c_int = 10015;
const CURLOPT_POSTFIELDSIZE: c_int = 60;

const CURLOPT_SSL_VERIFYPEER: c_int = 64;
const CURLOPT_SSL_VERIFYHOST: c_int = 81;

// --- write callback ---
unsafe extern "C" fn write_cb(
    data: *mut c_char,
    size: usize,
    nmemb: usize,
    userdata: *mut c_void,
) -> usize {
    let total = size * nmemb;

    if userdata.is_null() {
        return total;
    }

    let slice = std::slice::from_raw_parts(data as *const u8, total);
    let vec = &mut *(userdata as *mut Vec<u8>);
    vec.extend_from_slice(slice);

    total
}

pub fn http_get(url: &str, headers: &[String]) -> Result<(u32, Vec<u8>), String> {
    unsafe {
        let handle = kr_sdk_curl_easy_init();
        if handle.is_null() {
            return Err("curl_easy_init returned null".into());
        }

        let mut body: Vec<u8> = Vec::new();
        let body_ptr = &mut body as *mut _ as *mut c_void;

        // Basic curl options used by sub_CEB80:
        kr_sdk_curl_easy_setopt(handle, CURLOPT_HTTPAUTH, 1 as *mut c_void);
        kr_sdk_curl_easy_setopt(handle, CURLOPT_NOPROGRESS, 1 as *mut c_void);
        kr_sdk_curl_easy_setopt(handle, CURLOPT_FOLLOWLOCATION, 1 as *mut c_void);
        kr_sdk_curl_easy_setopt(handle, CURLOPT_MAXREDIRS, 2 as *mut c_void);

        // Disable SSL verification for localhost
        kr_sdk_curl_easy_setopt(handle, CURLOPT_SSL_VERIFYPEER, 0 as *mut c_void);
        kr_sdk_curl_easy_setopt(handle, CURLOPT_SSL_VERIFYHOST, 0 as *mut c_void);

        // URL
        let c_url = CString::new(url).unwrap();
        kr_sdk_curl_easy_setopt(handle, CURLOPT_URL, c_url.as_ptr() as *mut c_void);

        // Download body
        kr_sdk_curl_easy_setopt(handle, CURLOPT_NOBODY, 0 as *mut c_void);

        // Write callback
        kr_sdk_curl_easy_setopt(handle, CURLOPT_WRITEFUNCTION, write_cb as *mut c_void);
        kr_sdk_curl_easy_setopt(handle, CURLOPT_WRITEDATA, body_ptr);

        // Headers
        let mut slist: *mut c_void = std::ptr::null_mut();
        for h in headers {
            let c = CString::new(h.as_str()).unwrap();
            slist = kr_sdk_curl_slist_append(slist, c.as_ptr());
        }
        if !slist.is_null() {
            kr_sdk_curl_easy_setopt(handle, CURLOPT_HTTPHEADER, slist);
        }

        let rc = kr_sdk_curl_easy_perform(handle);

        if !slist.is_null() {
            kr_sdk_curl_slist_free_all(slist);
        }

        let mut http_code: i64 = 0;
        kr_sdk_curl_easy_getinfo(
            handle,
            CURLINFO_RESPONSE_CODE,
            &mut http_code as *mut _ as *mut c_void,
        );

        kr_sdk_curl_easy_cleanup(handle);

        if rc != 0 {
            return Err(format!("curl perform error {}", rc));
        }

        Ok((http_code as u32, body))
    }
}

pub fn http_post(
    url: &str,
    headers: &[(&str, &str)],
    body: &[u8],
) -> Result<(u32, Vec<u8>), String> {
    unsafe {
        let handle = kr_sdk_curl_easy_init();
        if handle.is_null() {
            return Err("curl_easy_init returned null".into());
        }

        let mut response_body: Vec<u8> = Vec::new();
        let body_ptr = &mut response_body as *mut _ as *mut c_void;

        // Basic options
        kr_sdk_curl_easy_setopt(handle, CURLOPT_HTTPAUTH, 1 as *mut c_void);
        kr_sdk_curl_easy_setopt(handle, CURLOPT_NOPROGRESS, 1 as *mut c_void);
        kr_sdk_curl_easy_setopt(handle, CURLOPT_FOLLOWLOCATION, 1 as *mut c_void);
        kr_sdk_curl_easy_setopt(handle, CURLOPT_MAXREDIRS, 2 as *mut c_void);

        kr_sdk_curl_easy_setopt(handle, CURLOPT_SSL_VERIFYPEER, 0 as *mut c_void);
        kr_sdk_curl_easy_setopt(handle, CURLOPT_SSL_VERIFYHOST, 0 as *mut c_void);

        // URL
        let c_url = CString::new(url).unwrap();
        kr_sdk_curl_easy_setopt(handle, CURLOPT_URL, c_url.as_ptr() as *mut c_void);

        // POST mode
        kr_sdk_curl_easy_setopt(handle, CURLOPT_POST, 1 as *mut c_void);

        // POST body
        kr_sdk_curl_easy_setopt(handle, CURLOPT_POSTFIELDS, body.as_ptr() as *mut c_void);
        kr_sdk_curl_easy_setopt(handle, CURLOPT_POSTFIELDSIZE, body.len() as *mut c_void);

        // Write callback
        kr_sdk_curl_easy_setopt(handle, CURLOPT_WRITEFUNCTION, write_cb as *mut c_void);
        kr_sdk_curl_easy_setopt(handle, CURLOPT_WRITEDATA, body_ptr);

        // Headers
        let mut slist: *mut c_void = std::ptr::null_mut();
        for (name, value) in headers {
            let header = format!("{}: {}", name, value);
            let c_header = CString::new(header).unwrap();
            slist = kr_sdk_curl_slist_append(slist, c_header.as_ptr());
        }
        if !slist.is_null() {
            kr_sdk_curl_easy_setopt(handle, CURLOPT_HTTPHEADER, slist);
        }

        // DNS options
        kr_sdk_curl_easy_setopt(handle, CURLOPT_DNS_USE_GLOBAL_CACHE, 0 as *mut c_void);
        kr_sdk_curl_easy_setopt(handle, CURLOPT_DNS_CACHE_TIMEOUT, 30 as *mut c_void);

        // Perform request
        let rc = kr_sdk_curl_easy_perform(handle);

        if !slist.is_null() {
            kr_sdk_curl_slist_free_all(slist);
        }

        let mut http_code: i64 = 0;
        kr_sdk_curl_easy_getinfo(
            handle,
            CURLINFO_RESPONSE_CODE,
            &mut http_code as *mut _ as *mut c_void,
        );

        kr_sdk_curl_easy_cleanup(handle);

        if rc != 0 {
            return Err(format!("curl perform error {}", rc));
        }

        Ok((http_code as u32, response_body))
    }
}

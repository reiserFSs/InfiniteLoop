use std::ffi::CStr;
use std::os::raw::c_char;

#[no_mangle]
pub extern "C" fn kurosdk_uploadMarketingEvent(data: *const c_char) -> i64 {
    if data.is_null() {
        println!("[KRSDK] uploadMarketingEvent(NULL)");
        return 0;
    }

    let s = unsafe { CStr::from_ptr(data) };
    let text = s.to_string_lossy();

    handle_marketing_event(&text);

    0
}

fn handle_marketing_event(json: &str) {
    if let Ok(v) = serde_json::from_str::<serde_json::Value>(json) {
        if let Some(name) = v.get("eventName").and_then(|x| x.as_str()) {
            println!("[KRSDK] eventName = {}", name);
            match name {
                "Version_Checking_Start" => {
                    println!("[KRSDK] → version checking starting");
                }
                "Version_Checking_End" => {
                    println!("[KRSDK] → version checking done");
                }
                "SDK_Initialize" => {
                    println!("[KRSDK] → SDK initialized event");
                    // Don't need to do anything here, just log it
                }
                "SDK_Login" => {
                    println!("[KRSDK] → SDK_Login event (already handled by kurosdk_login)");
                    // Don't call kurosdk_login here - game already calls it directly!
                }
                other => {
                    println!("[KRSDK] → other event: {}", other);
                }
            }
        }
    }
}

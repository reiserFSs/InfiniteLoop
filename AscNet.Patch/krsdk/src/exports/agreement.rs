#[no_mangle]
pub extern "C" fn kurosdk_isProtocolAgreed() -> u8 {
    println!("[KRSDK] isProtocolAgreed -> 1");
    1   // YES, protocol agreed
}

#[no_mangle]
pub extern "C" fn kurosdk_isPrivacySettingEnabled() -> u8 {
    println!("[KRSDK] isPrivacySettingEnabled -> 0");
    0
}

use std::{ffi::{c_void, CStr, CString}, mem::size_of, sync::LazyLock};

use anyhow::{anyhow, Result};
use windows::{
    core::{s, PCSTR},
    Win32::System::{
        LibraryLoader::{GetModuleHandleA, GetProcAddress},
        Memory::{VirtualQuery, MEMORY_BASIC_INFORMATION, MEM_COMMIT, PAGE_EXECUTE, PAGE_EXECUTE_READ, PAGE_EXECUTE_READWRITE, PAGE_EXECUTE_WRITECOPY, PAGE_GUARD, PAGE_NOACCESS},
    },
};

pub static GAME_ASSEMBLY_BASE: LazyLock<usize> =
    LazyLock::new(|| unsafe { GetModuleHandleA(s!("GameAssembly.dll")).unwrap().0 as usize });

pub fn get_export(name: &str) -> Result<usize> {
    let name = CString::new(name)?;
    unsafe {
        GetProcAddress(
            GetModuleHandleA(s!("GameAssembly.dll"))?,
            PCSTR(name.as_ptr().cast()),
        )
        .map(|proc| proc as usize)
        .ok_or_else(|| anyhow!("GameAssembly export `{}` not found", name.to_string_lossy()))
    }
}

pub fn readable(address: usize, size: usize) -> bool {
    if address == 0 || size == 0 {
        return false;
    }
    unsafe {
        let mut info = MEMORY_BASIC_INFORMATION::default();
        if VirtualQuery(Some(address as *const c_void), &mut info, size_of::<MEMORY_BASIC_INFORMATION>()) == 0 {
            return false;
        }
        let protect = info.Protect;
        info.State == MEM_COMMIT
            && !protect.contains(PAGE_GUARD)
            && !protect.contains(PAGE_NOACCESS)
            && address.checked_add(size).is_some_and(|end| end <= info.BaseAddress as usize + info.RegionSize)
    }
}

pub fn executable(address: usize) -> bool {
    if !readable(address, 1) {
        return false;
    }
    unsafe {
        let mut info = MEMORY_BASIC_INFORMATION::default();
        VirtualQuery(Some(address as *const c_void), &mut info, size_of::<MEMORY_BASIC_INFORMATION>());
        let protect = info.Protect;
        protect.contains(PAGE_EXECUTE)
            || protect.contains(PAGE_EXECUTE_READ)
            || protect.contains(PAGE_EXECUTE_READWRITE)
            || protect.contains(PAGE_EXECUTE_WRITECOPY)
    }
}

pub fn game_assembly_code(address: usize) -> bool {
    if !executable(address) {
        return false;
    }
    unsafe {
        let mut info = MEMORY_BASIC_INFORMATION::default();
        VirtualQuery(Some(address as *const c_void), &mut info, size_of::<MEMORY_BASIC_INFORMATION>());
        info.AllocationBase as usize == *GAME_ASSEMBLY_BASE
    }
}

pub unsafe fn read_csharp_string(address: usize) -> Option<String> {
    if !readable(address, 20) {
        return None;
    }
    let len = *(address.checked_add(16)? as *const u32) as usize;
    let bytes = len.checked_mul(2)?;
    let chars = address.checked_add(20)?;
    if len > 1_048_576 || (bytes != 0 && !readable(chars, bytes)) {
        return None;
    }
    Some(String::from_utf16_lossy(std::slice::from_raw_parts(chars as *const u16, len)))
}

pub unsafe fn c_string(pointer: *const i8) -> Option<String> {
    (!pointer.is_null()).then(|| CStr::from_ptr(pointer).to_str().ok().map(str::to_owned)).flatten()
}

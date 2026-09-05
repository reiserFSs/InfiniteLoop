
use std::{sync::{LazyLock, RwLock}, time::Duration};

use lazy_static::lazy_static;
use windows::core::PCSTR;
use windows::Win32::System::SystemServices::DLL_PROCESS_ATTACH;
use windows::Win32::{Foundation::HINSTANCE, System::LibraryLoader::GetModuleHandleA};

mod interceptor;
mod modules;
mod util;

use crate::modules::{Http, MhyContext, ModuleManager};

unsafe fn initialize() -> bool {
    let Ok(game_assembly) = GetModuleHandleA(PCSTR(b"GameAssembly.dll\0".as_ptr())) else {
        eprintln!("[lucia] initialization failed closed: GameAssembly.dll is unavailable");
        return false;
    };
    println!("[lucia] GameAssembly base: 0x{:X}", game_assembly.0 as usize);
    let Ok(mut module_manager) = MODULE_MANAGER.write() else {
        eprintln!("[lucia] initialization failed closed: module manager lock is poisoned");
        return false;
    };
    match module_manager.enable(MhyContext::<Http>::new(game_assembly.0 as usize)) {
        Ok(()) => true,
        Err(error) => {
            eprintln!("[lucia] initialization failed closed: {error:#}");
            false
        }
    }
}

unsafe fn probe_thread() {
    while GetModuleHandleA(PCSTR(b"GameAssembly.dll\0".as_ptr())).is_err() {
        std::thread::sleep(Duration::from_millis(200));
    }
    ascnet_patch_initialize();
}

lazy_static! {
    static ref MODULE_MANAGER: RwLock<ModuleManager> = RwLock::new(ModuleManager::default());
}
static INITIALIZED: LazyLock<bool> = LazyLock::new(|| unsafe { initialize() });

#[no_mangle]
pub unsafe extern "system" fn ascnet_patch_initialize() -> i32 {
    i32::from(*INITIALIZED)
}

#[no_mangle]
#[allow(non_snake_case)]
unsafe extern "system" fn DllMain(_: HINSTANCE, call_reason: u32, _: *mut ()) -> bool {
    if call_reason == DLL_PROCESS_ATTACH
        && std::env::var("ASCNET_PATCH_PROBE").as_deref() == Ok("1")
    {
        std::thread::spawn(|| probe_thread());
    }

    true
}

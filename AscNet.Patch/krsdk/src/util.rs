use std::path::PathBuf;
use windows::Win32::System::LibraryLoader::GetModuleFileNameW;

pub fn dll_dir() -> PathBuf {
    unsafe {
        let mut buf = [0u16; 260];
        let len = GetModuleFileNameW(None, &mut buf);
        let full = String::from_utf16_lossy(&buf[..len as usize]);

        let mut path = PathBuf::from(full);
        path.pop(); // remove KRSDK.dll filename
        path
    }
}

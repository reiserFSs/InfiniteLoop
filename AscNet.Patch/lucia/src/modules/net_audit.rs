//! Native outbound calls: DNS lookups of known telemetry / public-IP-lookup hosts are always failed in place
//! with `WSAHOST_NOT_FOUND`, like an offline host, without reaching the resolver; with `ASCNET_PATCH_NETAUDIT=1`
//! every outbound call is also logged once per destination and calling module. Arguments are otherwise untouched.
use std::{cell::Cell, collections::HashSet, ffi::c_void, net::{Ipv4Addr, Ipv6Addr}, sync::{LazyLock, Mutex, mpsc::{sync_channel, SyncSender}, atomic::{AtomicBool, Ordering}}};
use ilhook::x64::Registers;
use windows::Win32::System::Diagnostics::Debug::{CONTEXT, CONTEXT_CONTROL_AMD64, CONTEXT_INTEGER_AMD64, RtlLookupFunctionEntry, RtlVirtualUnwind, UNW_FLAG_NHANDLER};
use crate::{diag, interceptor::Interceptor};

// Minimal kernel32 ABI keeps this observer independent of networking feature flags.
#[link(name = "kernel32")]
extern "system" {
    fn LoadLibraryW(name: *const u16) -> *mut c_void;
    fn GetProcAddress(module: *mut c_void, name: *const u8) -> *mut c_void;
    fn GetModuleHandleExW(flags: u32, address: *const u16, module: *mut *mut c_void) -> i32;
    fn GetModuleFileNameW(module: *mut c_void, name: *mut u16, size: u32) -> u32;
    fn ReadProcessMemory(process: *mut c_void, address: *const c_void, buffer: *mut c_void, size: usize, read: *mut usize) -> i32;
    fn GetLastError() -> u32;
    fn SetLastError(error: u32);
}

const LIMIT: usize = 512;
static AUDIT: AtomicBool = AtomicBool::new(false);
/// Seen by the 4.8 client (network audit, 2026-10-23): CrashSight's upload host (`pc.crashsight.wetest.net`)
/// and the public-IP lookup `XNoticeManager.RequestIp` runs at start-up (icanhazip, then ipify / ifconfig /
/// inet-ip as fallbacks). The client has no consumer of that IP; blocking leaves it empty.
const BLOCKED_SUFFIXES: [&str; 6] = ["wetest.net", "icanhazip.com", "ipify.org", "ifconfig.me", "ifconfig.co", "inet-ip.info"];

fn blocked_host(host: &str) -> bool {
    let host = host.trim_end_matches('.').to_ascii_lowercase();
    BLOCKED_SUFFIXES.iter().any(|suffix| host == *suffix || host.ends_with(&format!(".{suffix}")))
}
static OWNER: LazyLock<Mutex<Interceptor>> = LazyLock::new(|| unsafe { install_hooks() });
static EVENTS: LazyLock<SyncSender<Event>> = LazyLock::new(|| {
    let (sender, receiver) = sync_channel::<Event>(512);
    std::thread::spawn(move || {
        ACTIVE.with(|flag| flag.set(true));
        let mut seen = HashSet::with_capacity(LIMIT);
        let mut full = false;
        while let Ok(event) = receiver.recv() {
            // Module lookups run here, not inside the hooked call.
            let caller = unsafe { module_at(event.caller_address) }.unwrap_or_else(|| "<unknown>".into());
            let source = event.source
                .or_else(|| (!system_module(&caller) && caller != "<unknown>").then(|| caller.clone()))
                .unwrap_or_else(|| "<unknown>".into());
            let key = dedup_key(event.api, event.destination, &source);
            if OVERFLOW.swap(false, Ordering::Relaxed) {
                diag::log(&diag::failed_line("lucia", "net audit", "audit queue full; some calls were not audited"));
            }
            if seen.contains(&key) { continue; }
            if seen.len() == LIMIT {
                if !full {
                    full = true;
                    diag::log(&diag::ok_line("lucia", "net audit", "dedup table full; further new destinations are not logged"));
                }
                continue;
            }
            let line = if event.blocked {
                format!("{} ({}) from {source}; the lookup fails as if offline", key.destination, key.api)
            } else {
                format!("{} {} from {source} (caller {caller})", key.api, key.destination)
            };
            let what = if event.blocked { "telemetry blocked" } else { "net audit" };
            seen.insert(key);
            diag::log(&diag::ok_line("lucia", what, &line));
        }
    });
    sender
});
static OVERFLOW: AtomicBool = AtomicBool::new(false);
struct Event { api: &'static str, destination: String, caller_address: usize, source: Option<String>, blocked: bool }
thread_local! { static ACTIVE: Cell<bool> = const { Cell::new(false) }; }

#[derive(Hash, PartialEq, Eq)]
struct AuditKey { api: &'static str, destination: String, module: String }
fn dedup_key(api: &'static str, destination: String, module: &str) -> AuditKey {
    AuditKey { api, destination, module: module.to_ascii_lowercase() }
}

/// Always installs the DNS hooks (telemetry block); the connect / WinHTTP / WinINet observers only with the audit.
pub unsafe fn install() {
    AUDIT.store(std::env::var("ASCNET_PATCH_NETAUDIT").as_deref() == Ok("1"), Ordering::Relaxed);
    LazyLock::force(&EVENTS);
    LazyLock::force(&OWNER);
}

unsafe fn install_hooks() -> Mutex<Interceptor> {
        let mut owner = Interceptor::new();
        let dns: &[(&str, ilhook::x64::JmpToRetRoutine)] =
            &[("getaddrinfo", dns_a), ("GetAddrInfoW", dns_w), ("GetAddrInfoExW", dns_ex_w), ("gethostbyname", host_a)];
        install_library(&mut owner, "ws2_32.dll", dns, |owner, address, routine| owner.redirect(address, routine));
        if AUDIT.load(Ordering::Relaxed) {
            let attach = |owner: &mut Interceptor, address, routine| owner.attach(address, routine);
            install_library(&mut owner, "ws2_32.dll", &[("connect", connect as ilhook::x64::JmpBackRoutine)], attach);
            install_library(&mut owner, "winhttp.dll", &[("WinHttpConnect", http_connect as ilhook::x64::JmpBackRoutine)], attach);
            install_library(&mut owner, "wininet.dll", &[("InternetConnectW", internet_connect_w as ilhook::x64::JmpBackRoutine), ("InternetConnectA", internet_connect_a), ("InternetOpenUrlW", url_w), ("InternetOpenUrlA", url_a)], attach);
        }
        Mutex::new(owner)
}

unsafe fn install_library<R: Copy>(owner: &mut Interceptor, dll: &str, targets: &[(&str, R)], hook: impl Fn(&mut Interceptor, usize, R) -> anyhow::Result<()>) {
    let wide: Vec<u16> = dll.encode_utf16().chain(Some(0)).collect();
    // Retain LoadLibrary's reference for the lifetime of the installed hooks.
    let module = LoadLibraryW(wide.as_ptr());
    for &(api, routine) in targets {
        let name: Vec<u8> = api.bytes().chain(Some(0)).collect();
        let address = if module.is_null() { std::ptr::null_mut() } else { GetProcAddress(module, name.as_ptr()) };
        if address.is_null() {
            diag::log(&diag::failed_line("lucia", "net hook", &format!("{dll}!{api} unavailable; calls through this API are neither blocked nor audited")));
        } else if let Err(error) = hook(owner, address as usize, routine) {
            diag::log(&diag::failed_line("lucia", "net hook", &format!("{dll}!{api}: {error}; calls through this API are neither blocked nor audited")));
        } else if AUDIT.load(Ordering::Relaxed) {
            diag::log(&diag::ok_line("lucia", "net audit hook", &format!("{dll}!{api}")));
        }
    }
}

unsafe fn copy_memory(address: usize, bytes: &mut [u8]) -> bool {
    if address == 0 { return false; }
    let mut copied = 0;
    ReadProcessMemory(-1isize as *mut c_void, address as *const c_void, bytes.as_mut_ptr().cast(), bytes.len(), &mut copied) != 0 && copied == bytes.len()
}

unsafe fn text(address: usize, wide: bool) -> String {
    if address == 0 { return "<null>".into(); }
    let mut buffer = [0u8; 2048];
    let mut length = 0;
    let unit = if wide { 2 } else { 1 };
    while length + unit <= buffer.len() {
        let Some(pointer) = address.checked_add(length) else { return "<unreadable>".into(); };
        // Read only as far as the next page boundary, including strings ending there.
        let count = (4096 - pointer % 4096).min(buffer.len() - length);
        let count = count - count % unit;
        if count == 0 || !copy_memory(pointer, &mut buffer[length..length + count]) { return "<unreadable>".into(); }
        for offset in (length..length + count).step_by(unit) {
            if buffer[offset] == 0 && (!wide || buffer[offset + 1] == 0) {
                return decode_text(&buffer[..offset], wide);
            }
        }
        length += count;
    }
    let mut value = decode_text(&buffer, wide);
    value.push_str("<truncated>");
    value
}

fn decode_text(bytes: &[u8], wide: bool) -> String {
    let value = if wide {
        char::decode_utf16(bytes.chunks_exact(2).map(|b| u16::from_le_bytes([b[0], b[1]]))).map(|c| c.unwrap_or(char::REPLACEMENT_CHARACTER)).collect::<String>()
    } else { String::from_utf8_lossy(bytes).into_owned() };
    // Keep untrusted URLs/hostnames on a single diagnostic line.
    value.chars().map(|c| if c.is_control() { ' ' } else { c }).collect()
}

fn sockaddr(bytes: &[u8]) -> Option<String> {
    let family = u16::from_le_bytes(bytes.get(..2)?.try_into().ok()?);
    let port = u16::from_be_bytes(bytes.get(2..4)?.try_into().ok()?);
    match family {
        2 => { let ip: [u8; 4] = bytes.get(4..8)?.try_into().ok()?; Some(format!("{}:{port}", Ipv4Addr::from(ip))) }
        23 => {
            let ip: [u8; 16] = bytes.get(8..24)?.try_into().ok()?;
            let scope = u32::from_le_bytes(bytes.get(24..28)?.try_into().ok()?);
            let ip = Ipv6Addr::from(ip);
            Some(if scope == 0 { format!("[{ip}]:{port}") } else { format!("[{ip}%{scope}]:{port}") })
        }
        _ => None,
    }
}

fn system_module(name: &str) -> bool {
    let basename = name.rsplit(['/', '\\']).next().unwrap_or(name).to_ascii_lowercase();
    matches!(basename.as_str(), "ntdll.dll" | "kernel32.dll" | "kernelbase.dll" | "ws2_32.dll" | "winhttp.dll" | "wininet.dll" | "mswsock.dll" | "dnsapi.dll" | "iphlpapi.dll" | "nsi.dll" | "secur32.dll" | "crypt32.dll" | "rpcrt4.dll" | "sechost.dll" | "ucrtbase.dll" | "msvcrt.dll" | "lucia.dll")
        || basename.starts_with("wine") || basename.starts_with("api-ms-win-") || basename.starts_with("ext-ms-win-")
}

unsafe fn module_at(address: usize) -> Option<String> {
    if address == 0 { return None; }
    let mut module = std::ptr::null_mut();
    if GetModuleHandleExW(0x4 | 0x2, address as *const u16, &mut module) == 0 { return None; }
    if crate::LUCIA_MODULE.get().copied() == Some(module as usize) { return Some("lucia.dll".into()); }
    let mut path = [0u16; 1024];
    let length = GetModuleFileNameW(module, path.as_mut_ptr(), path.len() as u32) as usize;
    if length == 0 || length >= path.len() { return None; }
    let start = path[..length].iter().rposition(|&c| c == b'\\' as u16 || c == b'/' as u16).map_or(0, |i| i + 1);
    Some(String::from_utf16_lossy(&path[start..length]))
}

unsafe fn original_source(regs: &Registers, caller: usize) -> Option<String> {
    // ilhook's generated trampoline has no registered unwind metadata. Capturing
    // the callback stack cannot reliably cross it; resume from the original
    // caller's post-call context instead, without touching the live registers.
    let mut context = CONTEXT {
        ContextFlags: CONTEXT_CONTROL_AMD64 | CONTEXT_INTEGER_AMD64,
        Rip: caller as u64,
        Rsp: regs.rsp.checked_add(8)?,
        Rbx: regs.rbx,
        Rbp: regs.rbp,
        Rsi: regs.rsi,
        Rdi: regs.rdi,
        R12: regs.r12,
        R13: regs.r13,
        R14: regs.r14,
        R15: regs.r15,
        ..Default::default()
    };
    for _ in 0..16 {
        if context.Rip == 0 || context.Rsp == 0 { break; }
        let Some(name) = module_at(context.Rip as usize) else { break; };
        if !system_module(&name) { return Some(name); }
        let (rip, rsp) = (context.Rip, context.Rsp);
        let mut return_address = [0u8; 8];
        if !copy_memory(rsp as usize, &mut return_address) { break; }
        let mut image_base = 0;
        let entry = RtlLookupFunctionEntry(rip, &mut image_base, None);
        if entry.is_null() {
            // Leaf functions have no unwind entry: emulate their return safely.
            context.Rip = u64::from_le_bytes(return_address);
            let Some(next_rsp) = rsp.checked_add(8) else { break; };
            context.Rsp = next_rsp;
        } else {
            let mut handler_data = std::ptr::null_mut();
            let mut establisher_frame = 0;
            RtlVirtualUnwind(UNW_FLAG_NHANDLER, image_base, rip, entry, &mut context, &mut handler_data, &mut establisher_frame, None);
        }
        if context.Rip == 0 || context.Rsp <= rsp { break; }
    }
    None
}

struct Guard(u32);
impl Drop for Guard {
    fn drop(&mut self) { ACTIVE.with(|flag| flag.set(false)); unsafe { SetLastError(self.0); } }
}

/// Queues `destination` for logging (always for blocks, otherwise only with the audit on). Runs inside the
/// hooked call, so it only reads the caller's return address; stack unwinding stays with the developer audit.
unsafe fn observe(reg: *mut Registers, api: &'static str, blocked: bool, destination: impl FnOnce(&Registers) -> String) {
    let audit = AUDIT.load(Ordering::Relaxed);
    if !blocked && !audit { return; }
    let error = GetLastError();
    if ACTIVE.with(|flag| flag.replace(true)) { return; }
    let _guard = Guard(error);
    let regs = &*reg;
    let destination = destination(regs);
    let mut return_address = [0u8; 8];
    let caller_address = if copy_memory(regs.rsp as usize, &mut return_address) { usize::from_le_bytes(return_address) } else { 0 };
    let source = if audit && module_at(caller_address).is_none_or(|caller| system_module(&caller)) {
        original_source(regs, caller_address)
    } else { None };
    // ponytail: bounded handoff; logging and dedup never hold up a network thread.
    if EVENTS.try_send(Event { api, destination, caller_address, source, blocked }).is_err() {
        OVERFLOW.store(true, Ordering::Relaxed);
    }
}

macro_rules! host_hook {
    ($name:ident, $api:literal, $register:ident, $wide:expr, $port:expr) => {
        unsafe extern "win64" fn $name(reg: *mut Registers, _: usize) {
            observe(reg, $api, false, |r| {
                let host = text(r.$register as usize, $wide);
                if $port { format!("{host}:{}", r.r8 as u16) } else { host }
            });
        }
    };
}

// Stand-ins for a deny-listed lookup: the same result as an unresolvable name, without a resolver round-trip.
// WSASetLastError is SetLastError on Windows and Wine.
const WSAHOST_NOT_FOUND: i32 = 11001;
unsafe extern "system" fn fail_addrinfo(_: *const c_void, _: *const c_void, _: *const c_void, result: *mut *mut c_void) -> i32 {
    if !result.is_null() { *result = std::ptr::null_mut(); }
    SetLastError(WSAHOST_NOT_FOUND as u32);
    WSAHOST_NOT_FOUND
}
#[allow(clippy::too_many_arguments)]
unsafe extern "system" fn fail_addrinfo_ex(_: *const u16, _: *const u16, _: u32, _: *const c_void, _: *const c_void, result: *mut *mut c_void, _: *const c_void, _: *const c_void, _: *const c_void, _: *const c_void) -> i32 {
    if !result.is_null() { *result = std::ptr::null_mut(); }
    SetLastError(WSAHOST_NOT_FOUND as u32);
    WSAHOST_NOT_FOUND
}
unsafe extern "system" fn fail_hostent(_: *const u8) -> *mut c_void {
    SetLastError(WSAHOST_NOT_FOUND as u32);
    std::ptr::null_mut()
}

/// DNS entry points: the host is the first argument (rcx). A deny-listed lookup jumps to the matching stand-in
/// with the caller's registers and stack intact; every other lookup continues into the original function.
macro_rules! dns_hook {
    ($name:ident, $api:literal, $wide:expr, $fail:ident) => {
        unsafe extern "win64" fn $name(reg: *mut Registers, original: usize, _: usize) -> usize {
            if ACTIVE.with(|flag| flag.get()) { return original; }
            let host = text((*reg).rcx as usize, $wide);
            let blocked = blocked_host(&host);
            observe(reg, $api, blocked, |_| host);
            if blocked { $fail as *const () as usize } else { original }
        }
    };
}
dns_hook!(dns_a, "getaddrinfo", false, fail_addrinfo);
dns_hook!(dns_w, "GetAddrInfoW", true, fail_addrinfo);
dns_hook!(dns_ex_w, "GetAddrInfoExW", true, fail_addrinfo_ex);
dns_hook!(host_a, "gethostbyname", false, fail_hostent);
host_hook!(http_connect, "WinHttpConnect", rdx, true, true);
host_hook!(internet_connect_w, "InternetConnectW", rdx, true, true);
host_hook!(internet_connect_a, "InternetConnectA", rdx, false, true);
host_hook!(url_w, "InternetOpenUrlW", rdx, true, false);
host_hook!(url_a, "InternetOpenUrlA", rdx, false, false);
unsafe extern "win64" fn connect(reg: *mut Registers, _: usize) {
    observe(reg, "connect", false, |r| {
        let mut address = [0u8; 28];
        let size = (r.r8 as i32).max(0) as usize;
        let size = size.min(address.len());
        if size == 0 || !copy_memory(r.rdx as usize, &mut address[..size]) { return "<unreadable sockaddr>".into(); }
        sockaddr(&address[..size]).unwrap_or_else(|| "<unsupported sockaddr>".into())
    });
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn socket_addresses() {
        assert_eq!(sockaddr(&[2, 0, 1, 187, 127, 0, 0, 1]).as_deref(), Some("127.0.0.1:443"));
        let mut ipv6 = [0u8; 28];
        ipv6[0] = 23; ipv6[3] = 80; ipv6[23] = 1;
        assert_eq!(sockaddr(&ipv6).as_deref(), Some("[::1]:80"));
        ipv6[24] = 3;
        assert_eq!(sockaddr(&ipv6).as_deref(), Some("[::1%3]:80"));
        assert_eq!(sockaddr(&ipv6[..20]), None);
        assert_eq!(sockaddr(&[0; 8]), None);
    }
    #[test]
    fn attribution_and_dedup() {
        assert!(system_module("C:\\Windows\\System32\\WS2_32.DLL"));
        assert!(system_module("lucia.dll"));
        assert!(system_module("winepulse.drv"));
        assert!(!system_module("CrashSight64.dll"));
        assert!(!system_module("GameAssembly.dll"));
        let key = dedup_key("connect", "127.0.0.1:80".into(), "PGR.exe");
        assert!(key == dedup_key("connect", "127.0.0.1:80".into(), "pgr.EXE"));
        assert!(key != dedup_key("connect", "127.0.0.1:81".into(), "PGR.exe"));
        assert!(key != dedup_key("WinHttpConnect", "127.0.0.1:80".into(), "PGR.exe"));
        assert!(key != dedup_key("connect", "127.0.0.1:80".into(), "PerfSight.dll"));
    }
}

#[cfg(test)]
mod block_tests {
    use super::*;
    #[test]
    fn deny_list_matches_hosts_and_subdomains_only() {
        for host in ["pc.crashsight.wetest.net", "WETEST.NET.", "ipv4.icanhazip.com", "icanhazip.com", "api.ipify.org", "ifconfig.me", "inet-ip.info"] {
            assert!(blocked_host(host), "{host}");
        }
        for host in ["notwetest.net", "icanhazip.com.evil.example", "prod-encdn-ak.pgr-game.com", "127.0.0.1", ""] {
            assert!(!blocked_host(host), "{host}");
        }
    }
}

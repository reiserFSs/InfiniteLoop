#[path = "network_routing.rs"]
mod network_routing;

use std::{
    ffi::{c_void, CString},
    sync::{
        atomic::{AtomicUsize, Ordering},
        OnceLock,
    },
};

use anyhow::{anyhow, bail, Context, Result};
use ilhook::x64::Registers;

use super::{MhyContext, MhyModule, ModuleType};
use crate::util::{c_string, executable, game_assembly_code, get_export, read_csharp_string, readable, GAME_ASSEMBLY_BASE};

const UNITY_WEB_REQUEST_ASSEMBLY: &str = "UnityEngine.UnityWebRequestModule.dll";
const WEB_REQUEST_NAMESPACE: &str = "UnityEngine.Networking";
const WEB_REQUEST_CLASS: &str = "UnityWebRequest";
const WEB_REQUEST_METHOD: &str = "InternalSetUrl";

static ORIGIN: OnceLock<String> = OnceLock::new();
static STRING_NEW: OnceLock<unsafe extern "system" fn(*const i8) -> usize> = OnceLock::new();
static TRACE: OnceLock<bool> = OnceLock::new();
static TRACE_CALLS: AtomicUsize = AtomicUsize::new(0);
const TRACE_CALL_LIMIT: usize = 64;

pub struct Http;

type DomainGet = unsafe extern "system" fn() -> *mut c_void;
type DomainGetAssemblies = unsafe extern "system" fn(*mut c_void, *mut usize) -> *mut *mut c_void;
type DomainAssemblyOpen = unsafe extern "system" fn(*mut c_void, *const i8) -> *mut c_void;
type AssemblyGetImage = unsafe extern "system" fn(*mut c_void) -> *mut c_void;
type ClassFromName = unsafe extern "system" fn(*mut c_void, *const i8, *const i8) -> *mut c_void;
type ClassGetMethod = unsafe extern "system" fn(*mut c_void, *const i8, i32) -> *mut c_void;
type MethodGetParamCount = unsafe extern "system" fn(*mut c_void) -> u32;
type MethodGetParam = unsafe extern "system" fn(*mut c_void, u32) -> *mut c_void;
type MethodGetReturnType = unsafe extern "system" fn(*mut c_void) -> *mut c_void;
type TypeGetName = unsafe extern "system" fn(*mut c_void) -> *const i8;
type ThreadAttach = unsafe extern "system" fn(*mut c_void) -> *mut c_void;
type ThreadCurrent = unsafe extern "system" fn() -> *mut c_void;
type ThreadDetach = unsafe extern "system" fn(*mut c_void);
type MethodGetFlags = unsafe extern "system" fn(*mut c_void, *mut u32) -> u32;
type Il2CppFree = unsafe extern "system" fn(*mut c_void);
type ImageGetName = unsafe extern "system" fn(*mut c_void) -> *const i8;
type ClassGetFields = unsafe extern "system" fn(*mut c_void, *mut *mut c_void) -> *mut c_void;
type FieldGetName = unsafe extern "system" fn(*mut c_void) -> *const i8;
type FieldGetFlags = unsafe extern "system" fn(*mut c_void) -> u32;
type FieldGetType = unsafe extern "system" fn(*mut c_void) -> *mut c_void;
type FieldStaticGetValue = unsafe extern "system" fn(*mut c_void, *mut c_void);
type ClassGetMethods = unsafe extern "system" fn(*mut c_void, *mut *mut c_void) -> *mut c_void;
type MethodGetName = unsafe extern "system" fn(*mut c_void) -> *const i8;
type ClassGetNestedTypes = unsafe extern "system" fn(*mut c_void, *mut *mut c_void) -> *mut c_void;
type ClassGetName = unsafe extern "system" fn(*mut c_void) -> *const i8;
struct AttachedThread {
    thread: *mut c_void,
    detach: ThreadDetach,
}

impl Drop for AttachedThread {
    fn drop(&mut self) {
        unsafe { (self.detach)(self.thread) }
    }
}

unsafe fn export<T>(name: &str) -> Result<T> {
    let address = get_export(name)?;
    Ok(std::mem::transmute_copy(&address))
}

unsafe fn resolved_type_name(type_name: TypeGetName, free: Il2CppFree, type_info: *mut c_void) -> Option<String> {
    if type_info.is_null() {
        return None;
    }
    let pointer = type_name(type_info);
    let name = c_string(pointer);
    if !pointer.is_null() {
        free(pointer as *mut c_void);
    }
    name
}

unsafe fn method_rva(method: *mut c_void) -> Option<usize> {
    if !readable(method as usize, std::mem::size_of::<usize>()) {
        return None;
    }
    let address = *(method as *const usize);
    game_assembly_code(address).then_some(address.checked_sub(*GAME_ASSEMBLY_BASE)).flatten()
}

unsafe fn trace_remote_config_metadata(
    assemblies: *mut *mut c_void,
    assembly_count: usize,
    assembly_get_image: AssemblyGetImage,
    image_get_name: ImageGetName,
    class_from_name: ClassFromName,
    type_name: TypeGetName,
    free: Il2CppFree,
) -> Result<()> {
    if !TRACE.get().copied().unwrap_or(false) {
        return Ok(());
    }

    let empty = CString::new("")?;
    let class_name = CString::new("XRemoteConfig")?;
    let mut selected = None;
    for index in 0..assembly_count {
        let image = assembly_get_image(*assemblies.add(index));
        if image.is_null() {
            continue;
        }
        let image_name = c_string(image_get_name(image)).unwrap_or_else(|| "<unnamed>".into());
        let class = class_from_name(image, empty.as_ptr(), class_name.as_ptr());
        if !class.is_null() {
            println!("[lucia] remote-config metadata image={image_name} type=XRemoteConfig");
            if selected.is_none() || image_name == "Assembly-CSharp.dll" {
                selected = Some((class, image_name == "Assembly-CSharp.dll"));
            }
        }
    }
    let Some((class, preferred)) = selected else {
        println!("[lucia] remote-config metadata type=XRemoteConfig status=not-found");
        return Ok(());
    };
    println!("[lucia] remote-config metadata selected assembly_csharp={preferred}");

    let class_get_fields: ClassGetFields = export("il2cpp_class_get_fields")?;
    let field_get_name: FieldGetName = export("il2cpp_field_get_name")?;
    let field_get_flags: FieldGetFlags = export("il2cpp_field_get_flags")?;
    let field_get_type: FieldGetType = export("il2cpp_field_get_type")?;
    let field_static_get_value: FieldStaticGetValue = export("il2cpp_field_static_get_value")?;
    let mut iterator = std::ptr::null_mut();
    for _ in 0..128 {
        let field = class_get_fields(class, &mut iterator);
        if field.is_null() {
            break;
        }
        let name = c_string(field_get_name(field)).unwrap_or_else(|| "<unnamed>".into());
        let flags = field_get_flags(field);
        let field_type = resolved_type_name(type_name, free, field_get_type(field))
            .unwrap_or_else(|| "<invalid>".into());
        println!("[lucia] remote-config field name={name} flags=0x{flags:X} type={field_type}");
        if (name == "LoadConfigUrl" || name == "ServerListStr")
            && flags & 0x10 != 0
            && field_type == "System.String"
        {
            let mut value = 0usize;
            field_static_get_value(field, (&mut value as *mut usize).cast());
            let observed = read_csharp_string(value)
                .map(|text| {
                    let stripped = text.split(['?', '#']).next().unwrap_or("");
                    if stripped.is_empty() {
                        "<empty>".into()
                    } else if stripped.contains("://") {
                        safe_url(stripped)
                    } else {
                        stripped.chars().take(512).collect()
                    }
                })
                .unwrap_or_else(|| "<null-or-unreadable>".into());
            println!("[lucia] remote-config {name}={observed}");
        }
        if name == "HasGetRemoteConfig" && flags & 0x10 != 0 && field_type == "System.Boolean" {
            let mut value = 0u8;
            field_static_get_value(field, (&mut value as *mut u8).cast());
            println!("[lucia] remote-config HasGetRemoteConfig={}", value != 0);
        }
    }

    let class_get_methods: ClassGetMethods = export("il2cpp_class_get_methods")?;
    let method_get_name: MethodGetName = export("il2cpp_method_get_name")?;
    let param_count: MethodGetParamCount = export("il2cpp_method_get_param_count")?;
    let get_param: MethodGetParam = export("il2cpp_method_get_param")?;
    let get_return: MethodGetReturnType = export("il2cpp_method_get_return_type")?;
    let method_flags: MethodGetFlags = export("il2cpp_method_get_flags")?;
    let relevant = ["GetConfig", "InitLoadConfigUrl", "LoadConfigUrl", "ParseRemoteConfig"];
    iterator = std::ptr::null_mut();
    for _ in 0..256 {
        let method = class_get_methods(class, &mut iterator);
        if method.is_null() {
            break;
        }
        let name = c_string(method_get_name(method)).unwrap_or_else(|| "<unnamed>".into());
        if !relevant.iter().any(|candidate| name.contains(candidate)) {
            continue;
        }
        let parameters = (0..param_count(method))
            .map(|index| {
                resolved_type_name(type_name, free, get_param(method, index))
                    .unwrap_or_else(|| "<invalid>".into())
            })
            .collect::<Vec<_>>()
            .join(",");
        let return_type = resolved_type_name(type_name, free, get_return(method))
            .unwrap_or_else(|| "<invalid>".into());
        let flags = method_flags(method, std::ptr::null_mut());
        let rva = method_rva(method)
            .map(|value| format!("0x{value:X}"))
            .unwrap_or_else(|| "<unavailable>".into());
        println!(
            "[lucia] remote-config method name={name} flags=0x{flags:X} signature=({parameters})->{return_type} rva={rva}"
        );
    }
    let class_get_nested_types: ClassGetNestedTypes = export("il2cpp_class_get_nested_types")?;
    let class_get_name: ClassGetName = export("il2cpp_class_get_name")?;
    iterator = std::ptr::null_mut();
    for _ in 0..128 {
        let nested = class_get_nested_types(class, &mut iterator);
        if nested.is_null() {
            break;
        }
        let nested_name = c_string(class_get_name(nested)).unwrap_or_else(|| "<unnamed>".into());
        if !nested_name.contains("<GetConfig>") {
            continue;
        }
        let mut method_iterator = std::ptr::null_mut();
        for _ in 0..128 {
            let method = class_get_methods(nested, &mut method_iterator);
            if method.is_null() {
                break;
            }
            let name = c_string(method_get_name(method)).unwrap_or_else(|| "<unnamed>".into());
            if name != "MoveNext" || param_count(method) != 0 {
                continue;
            }
            let return_type = resolved_type_name(type_name, free, get_return(method))
                .unwrap_or_else(|| "<invalid>".into());
            if return_type != "System.Boolean" {
                println!("[lucia] remote-config nested={nested_name} method=MoveNext status=unexpected-return type={return_type}");
                continue;
            }
            let flags = method_flags(method, std::ptr::null_mut());
            let rva = method_rva(method)
                .map(|value| format!("0x{value:X}"))
                .unwrap_or_else(|| "<unavailable>".into());
            println!("[lucia] remote-config nested={nested_name} method={name} flags=0x{flags:X} signature=()->{return_type} rva={rva}");
        }
    }

    println!("[lucia] remote-config observation boundary=resolver-domain-ready; if LoadConfigUrl is null, next named boundary is InitLoadConfigUrl/LoadConfigUrl/ParseRemoteConfig");
    Ok(())
}

unsafe fn attach_current_thread(domain: *mut c_void) -> Result<Option<AttachedThread>> {
    let thread_current: ThreadCurrent = export("il2cpp_thread_current")?;
    if !thread_current().is_null() {
        return Ok(None);
    }
    let thread_attach: ThreadAttach = export("il2cpp_thread_attach")?;
    let thread_detach: ThreadDetach = export("il2cpp_thread_detach")?;
    let thread = thread_attach(domain);
    if thread.is_null() {
        bail!("failed to attach metadata resolver thread to IL2CPP")
    }
    Ok(Some(AttachedThread { thread, detach: thread_detach }))
}

unsafe fn resolve_url_hook() -> Result<usize> {
    let domain_get: DomainGet = export("il2cpp_domain_get")?;
    let domain_get_assemblies: DomainGetAssemblies = export("il2cpp_domain_get_assemblies")?;
    let assembly_open: DomainAssemblyOpen = export("il2cpp_domain_assembly_open")?;
    let assembly_get_image: AssemblyGetImage = export("il2cpp_assembly_get_image")?;
    let class_from_name: ClassFromName = export("il2cpp_class_from_name")?;
    let class_get_method: ClassGetMethod = export("il2cpp_class_get_method_from_name")?;
    let param_count: MethodGetParamCount = export("il2cpp_method_get_param_count")?;
    let get_param: MethodGetParam = export("il2cpp_method_get_param")?;
    let get_return: MethodGetReturnType = export("il2cpp_method_get_return_type")?;
    let type_name: TypeGetName = export("il2cpp_type_get_name")?;
    let method_flags: MethodGetFlags = export("il2cpp_method_get_flags")?;
    let free: Il2CppFree = export("il2cpp_free")?;

    let domain = loop {
        let domain = domain_get();
        if !domain.is_null() {
            let mut count = 0;
            let assemblies = domain_get_assemblies(domain, &mut count);
            if !assemblies.is_null() && count != 0 {
                break domain;
            }
        }
        std::thread::sleep(std::time::Duration::from_millis(100));
    };
    let _attached = attach_current_thread(domain)?;
    let assembly_name = CString::new(UNITY_WEB_REQUEST_ASSEMBLY)?;
    let namespace = CString::new(WEB_REQUEST_NAMESPACE)?;
    let class_name = CString::new(WEB_REQUEST_CLASS)?;
    let method_name = CString::new(WEB_REQUEST_METHOD)?;
    let assembly = assembly_open(domain, assembly_name.as_ptr());
    if assembly.is_null() { bail!("assembly `{UNITY_WEB_REQUEST_ASSEMBLY}` is unavailable") }
    let image = assembly_get_image(assembly);
    if image.is_null() { bail!("assembly image is unavailable") }
    let class = class_from_name(image, namespace.as_ptr(), class_name.as_ptr());
    if class.is_null() { bail!("type `{WEB_REQUEST_NAMESPACE}.{WEB_REQUEST_CLASS}` is unavailable") }
    let method = class_get_method(class, method_name.as_ptr(), 1);
    if method.is_null() || param_count(method) != 1 { bail!("method `{WEB_REQUEST_METHOD}(string)` is unavailable") }
    if method_flags(method, std::ptr::null_mut()) & 0x10 != 0 { bail!("method `{WEB_REQUEST_METHOD}` is static") }
    let actual = resolved_type_name(type_name, free, get_param(method, 0))
        .unwrap_or_else(|| "<invalid>".into());
    if actual != "System.String" { bail!("parameter 0 is `{actual}`, expected `System.String`") }
    let actual_return = resolved_type_name(type_name, free, get_return(method))
        .unwrap_or_else(|| "<invalid>".into());
    if actual_return != "System.Void" { bail!("return type is `{actual_return}`, expected `System.Void`") }
    if !readable(method as usize, std::mem::size_of::<usize>()) {
        bail!("MethodInfo is not readable")
    }
    let target = *(method as *const usize);
    if !game_assembly_code(target) {
        bail!("MethodInfo methodPointer 0x{target:X} is not executable GameAssembly code")
    }
    Ok(target)
}

unsafe fn trace_metadata() -> Result<()> {
    if !TRACE.get().copied().unwrap_or(false) {
        return Ok(());
    }
    let domain_get: DomainGet = export("il2cpp_domain_get")?;
    let domain_get_assemblies: DomainGetAssemblies = export("il2cpp_domain_get_assemblies")?;
    let assembly_get_image: AssemblyGetImage = export("il2cpp_assembly_get_image")?;
    let image_get_name: ImageGetName = export("il2cpp_image_get_name")?;
    let class_from_name: ClassFromName = export("il2cpp_class_from_name")?;
    let type_name: TypeGetName = export("il2cpp_type_get_name")?;
    let free: Il2CppFree = export("il2cpp_free")?;
    let domain = domain_get();
    if domain.is_null() {
        bail!("IL2CPP domain is unavailable for diagnostics")
    }
    let _attached = attach_current_thread(domain)?;
    let mut assembly_count = 0;
    let assemblies = domain_get_assemblies(domain, &mut assembly_count);
    if assemblies.is_null() {
        bail!("IL2CPP assemblies are unavailable for diagnostics")
    }
    trace_remote_config_metadata(
        assemblies,
        assembly_count,
        assembly_get_image,
        image_get_name,
        class_from_name,
        type_name,
        free,
    )
}

fn origin() -> Result<&'static str> {
    let value = std::env::var("ASCNET_PATCH_ORIGIN").unwrap_or_else(|_| "http://127.0.0.1:8080".into());
    let value = value.trim_end_matches('/');
    let authority = value.strip_prefix("http://").or_else(|| value.strip_prefix("https://"));
    if value.is_empty() || authority.is_none_or(|v| v.is_empty() || v.contains('/')) {
        bail!("ASCNET_PATCH_ORIGIN must be an http(s) origin without a path")
    }
    ORIGIN.set(value.to_owned()).map_err(|_| anyhow!("origin already initialized"))?;
    Ok(ORIGIN.get().unwrap())
}

fn redirected_url(url: &str) -> Option<String> {
    network_routing::redirected_url(ORIGIN.get().unwrap(), url)
}

fn safe_url(url: &str) -> String {
    let Some((scheme, rest)) = url.split_once("://") else {
        return "<relative-or-invalid-url>".into();
    };
    let end = rest.find(['/', '?', '#']).unwrap_or(rest.len());
    let authority = &rest[..end];
    let host = authority.rsplit_once('@').map_or(authority, |(_, host)| host);
    let path = if end < rest.len() && rest.as_bytes()[end] == b'/' {
        let path = &rest[end..];
        &path[..path.find(['?', '#']).unwrap_or(path.len())]
    } else {
        "/"
    };
    format!("{scheme}://{host}{path}")
}

fn trace_call(boundary: &str, url: Option<&str>) {
    if !TRACE.get().copied().unwrap_or(false)
        || TRACE_CALLS.fetch_add(1, Ordering::Relaxed) >= TRACE_CALL_LIMIT
    {
        return;
    }
    println!(
        "[lucia] {boundary} call url={}",
        url.map(|value| {
            if value.contains("://") { safe_url(value) } else { value.split(['?', '#']).next().unwrap_or("").chars().take(512).collect() }
        }).unwrap_or_else(|| "<unreadable>".into())
    );
}

impl MhyModule for MhyContext<Http> {
    unsafe fn init(&mut self) -> Result<()> {
        let origin = origin()?;
        TRACE.set(std::env::var("ASCNET_PATCH_TRACE").as_deref() == Ok("1"))
            .map_err(|_| anyhow!("trace setting already initialized"))?;
        let target = resolve_url_hook().context("runtime metadata lookup failed; native routing disabled")?;
        let string_new_address = get_export("il2cpp_string_new")?;
        if !executable(string_new_address) { bail!("il2cpp_string_new export is not executable") }
        STRING_NEW.set(std::mem::transmute(string_new_address)).map_err(|_| anyhow!("string allocator already initialized"))?;

        println!("[lucia] verified {WEB_REQUEST_NAMESPACE}.{WEB_REQUEST_CLASS}.{WEB_REQUEST_METHOD}(System.String) -> System.Void at 0x{target:X}");
        println!("[lucia] routing config/notice requests to {origin}");
        if std::env::var("ASCNET_PATCH_PROBE").as_deref() == Ok("1") {
            println!("[lucia] probe-only mode; no hook installed");
        } else {
            self.interceptor.attach(target, Http::on_internal_set_url)?;
            println!("[lucia] InternalSetUrl hook installed at 0x{target:X}");
        }
        if let Err(error) = trace_metadata() {
            eprintln!("[lucia] optional metadata diagnostics failed: {error:#}");
        }
        Ok(())
    }

    unsafe fn de_init(&mut self) -> Result<()> { Ok(()) }
    fn get_module_type(&self) -> ModuleType { ModuleType::Http }
}

impl Http {
    unsafe extern "win64" fn on_internal_set_url(reg: *mut Registers, _: usize) {
        let original = read_csharp_string((*reg).rdx as usize);
        trace_call("InternalSetUrl", original.as_deref());
        let Some(original) = original else { return };
        let Some(replacement) = redirected_url(&original) else { return };
        let Ok(replacement) = CString::new(replacement) else { return };
        let Some(string_new) = STRING_NEW.get() else { return };
        let new_pointer = string_new(replacement.as_ptr());
        if new_pointer != 0 {
            println!(
                "[lucia] routed {} -> {}",
                safe_url(&original),
                safe_url(&replacement.to_string_lossy())
            );
            (*reg).rdx = new_pointer as u64;
        }
    }
}

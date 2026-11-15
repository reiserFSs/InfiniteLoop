//! Opt-in graphics options from `ascnet-graphics.ini` beside PGR.exe (see `graphics_params.rs` for the keys).
//! A lucia thread attached to IL2CPP:
//! - `renderscale`: detours `RenderPIplineQualityManger.SetupExtraRenderScale/EnableExtraRenderScale`, which the
//!   game calls on every scene change (stock: 1.333 menus, 1.0 fights), so the chosen scale holds everywhere.
//! - `<Setting>.<Field>` overrides: writes fields of the `GetRuntimeData()` quality objects every 0.5 s, logging
//!   the replaced value and every later game reset (`graphics_tweaks.rs`).
//! - `dump`: walks the render-pipeline and quality objects by reflection every 2 s and logs every value once, then
//!   only the values that changed, tagged with the active Unity scene. Read-only.
//! - `fps` / `nofade`: frame-rate unlock and camera-fade removal, applied each tick on this same thread
//!   (`graphics_extras.rs`); switching either off restores the game's own values.
//! - `ultrawide`: fullscreen at the monitor's own resolution past the game's 2560x2048 cap. Needs Lua run on the main
//!   thread, so a detour on `XLuaEngine.Update` drains queued chunks (`graphics_lua.rs`).
//! - `ui16x9`, `uifov`, `uiscale` and the overlay console share that main-thread Lua bridge.
//! - `fightfov`: scales the game's per-frame fight-camera FOV input without changing the source lens
//!   (`graphics_fight_fov.rs`), including StatusSyncFight/Babylonia cameras.
//!
//! Dump roots: `UnityEngine.Rendering.Universal.RenderPIplineQualityManger` (statics, `GetRuntimeData()`, `Is*()`
//! flags), `XQualityManager.Instance`, `XResolutionManager` statics and the managed `UnityEngine.QualitySettings`
//! getters that survived stripping. Objects are followed only into quality/pipeline classes (see `followed`).
#[path = "graphics_extras.rs"]
mod graphics_extras;
#[path = "graphics_fight_fov.rs"]
mod graphics_fight_fov;
#[path = "graphics_dps.rs"]
mod graphics_dps;
#[path = "graphics_dps_hits.rs"]
mod graphics_dps_hits;
#[path = "graphics_lua.rs"]
mod graphics_lua;
#[path = "graphics_live.rs"]
mod graphics_live;
#[path = "graphics_notice.rs"]
mod graphics_notice;
#[path = "graphics_overlay.rs"]
mod graphics_overlay;
#[path = "graphics_nofade_params.rs"]
mod graphics_nofade_params;
#[path = "graphics_params.rs"]
mod graphics_params;
#[path = "graphics_tweaks.rs"]
mod graphics_tweaks;
#[path = "graphics_settings.rs"]
mod graphics_settings;

use std::{collections::BTreeMap, ffi::{c_void, CString}, ptr::null_mut, sync::{atomic::Ordering, MutexGuard, PoisonError}, time::{Duration, Instant}};

use anyhow::{Context, Result};

use super::il2cpp::{self, export, Il2cpp, FLAG_STATIC};
use crate::util::{c_string, read_csharp_string};
use graphics_live::{Live, Status, LIVE};
use graphics_params::{Config, CONFIG_FILE};
use graphics_extras::{Fps, NoFade};
use graphics_fight_fov::FightFov;
use graphics_dps::DpsMeter;
use graphics_lua::{LuaBridge, UiFit, UiFov, UiScale, Ultrawide};
use graphics_tweaks::{Overrides, RenderScale, QUITTING};

const URP_NAMESPACE: &str = "UnityEngine.Rendering.Universal";
const URP_MANAGER: &str = "RenderPIplineQualityManger";
const URP_FLAGS: [&str; 5] = ["IsAntiAliasingEnable", "IsPvsEnable", "IsSocEnable", "IsMotionVectorOnGBuffer", "IsCharacterGBufferWithLighting"];
const UNITY_QUALITY_GETTERS: [&str; 7] = [
    "get_vSyncCount", "get_antiAliasing", "get_streamingMipmapsActive", "get_streamingMipmapsMemoryBudget",
    "get_streamingMipmapsMaxLevelReduction", "get_streamingMipmapsRenderersPerFrame", "get_streamingMipmapsAddAllCameras",
];
const UNAFFECTED: &str = "the game keeps its own value";
/// Overrides and render-scale checks run every tick; the dump every `DUMP_EVERY` ticks (2 s).
const TICK: Duration = Duration::from_millis(500);
const DUMP_EVERY: u64 = 4;
const FIELD_LITERAL: u32 = 0x40;
/// Arrays longer than this are summarised; the quality arrays (cascade splits, update rates) are tiny.
const ARRAY_LIMIT: usize = 16;

// IL2CPP_TYPE_* values from il2cpp-blob.h.
const T_BOOLEAN: i32 = 0x02;
const T_CHAR: i32 = 0x03;
const T_I1: i32 = 0x04;
const T_U1: i32 = 0x05;
const T_I2: i32 = 0x06;
const T_U2: i32 = 0x07;
const T_I4: i32 = 0x08;
const T_U4: i32 = 0x09;
const T_I8: i32 = 0x0a;
const T_U8: i32 = 0x0b;
const T_R4: i32 = 0x0c;
const T_R8: i32 = 0x0d;
const T_STRING: i32 = 0x0e;
const T_VALUETYPE: i32 = 0x11;
const T_CLASS: i32 = 0x12;
const T_GENERICINST: i32 = 0x15;
const T_OBJECT: i32 = 0x1c;
const T_SZARRAY: i32 = 0x1d;

fn ok(what: &str, detail: &str) {
    il2cpp::ok(what, detail);
}

fn failed(what: &str, reason: &str) {
    il2cpp::failed(what, reason, UNAFFECTED);
}

/// Starts the graphics worker and Settings tab even without an ini or enabled overrides.
/// Missing configuration uses the game's defaults; other read problems are reported.
pub fn spawn_if_enabled() {
    let Some(path) = std::env::current_exe().ok().and_then(|exe| Some(exe.parent()?.join(CONFIG_FILE))) else { return };
    let text = match std::fs::read_to_string(&path) {
        Ok(text) => text,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => String::new(),
        Err(error) => {
            failed(&format!("read {CONFIG_FILE}"), &format!("{}: {error}", path.display()));
            String::new()
        }
    };
    let config = Config::parse(&text);
    for problem in &config.problems {
        failed(CONFIG_FILE, &format!("{problem}; that line is ignored"));
    }
    let mut requested = Vec::new();
    if let Some(scale) = config.render_scale {
        requested.push(format!("renderscale = {scale}"));
    }
    if !config.overrides.is_empty() {
        requested.push(format!("{} setting override(s)", config.overrides.len()));
    }
    if config.dump {
        requested.push("dump = 1".into());
    }
    if let Some(fps) = config.fps {
        requested.push(format!("fps = {fps}"));
    }
    if config.nofade {
        requested.push("nofade = on".into());
    }
    if config.ultrawide {
        requested.push("ultrawide = on".into());
    }
    if config.ui16x9 {
        requested.push("ui16x9 = on".into());
    }
    if let Some(scale) = config.ui_fov {
        requested.push(format!("uifov = {scale}"));
    }
    if let Some(scale) = config.fight_fov {
        requested.push(format!("fightfov = {scale}"));
    }
    if let Some(scale) = config.ui_scale {
        requested.push(format!("uiscale = {scale}"));
    }
    if config.dps_meter {
        requested.push("dpsmeter = on".into());
    }
    if config.overlay {
        requested.push("overlay = on".into());
    }
    ok("graphics options requested", &format!("{} in {CONFIG_FILE}; applying once IL2CPP is ready", requested.join(", ")));
    live().desired = config.into();
    std::thread::spawn(|| {
        if let Err(error) = unsafe { run() } {
            failed("graphics options", &format!("{error:#}"));
        }
    });
}

fn live() -> MutexGuard<'static, Live> {
    LIVE.lock().unwrap_or_else(PoisonError::into_inner)
}

type ClassGetFields = unsafe extern "system" fn(*mut c_void, *mut *mut c_void) -> *mut c_void;
type FieldGetName = unsafe extern "system" fn(*mut c_void) -> *const i8;
type FieldGetOffset = unsafe extern "system" fn(*mut c_void) -> usize;
type ObjectGetClass = unsafe extern "system" fn(*mut c_void) -> *mut c_void;
type ClassFromType = unsafe extern "system" fn(*mut c_void) -> *mut c_void;
type ClassPredicate = unsafe extern "system" fn(*mut c_void) -> bool;
type ClassEnumBasetype = unsafe extern "system" fn(*mut c_void) -> *mut c_void;
type ClassGetName = unsafe extern "system" fn(*mut c_void) -> *const i8;
type TypeGetType = unsafe extern "system" fn(*mut c_void) -> i32;
type ArrayLength = unsafe extern "system" fn(*mut c_void) -> u32;
type MethodReturnType = unsafe extern "system" fn(*mut c_void) -> *mut c_void;

/// Reflection exports only this read-only walker needs (the shared table stays minimal for the tweaks).
struct Reflect {
    class_fields: ClassGetFields,
    field_name: FieldGetName,
    field_offset: FieldGetOffset,
    object_class: ObjectGetClass,
    class_from_type: ClassFromType,
    class_is_enum: ClassPredicate,
    class_is_valuetype: ClassPredicate,
    enum_basetype: ClassEnumBasetype,
    class_name: ClassGetName,
    type_type: TypeGetType,
    array_length: ArrayLength,
    return_type: MethodReturnType,
}

enum Value {
    Text(String),
    Object(*mut c_void),
}

struct Walker<'a> {
    il: &'a Il2cpp,
    rf: Reflect,
    values: BTreeMap<String, String>,
}

impl Walker<'_> {
    unsafe fn class_name(&self, class: *mut c_void) -> String {
        c_string((self.rf.class_name)(class)).unwrap_or_else(|| "<unnamed>".into())
    }

    /// Only quality/pipeline data is followed; delegates, dictionaries and Unity objects are named, not walked.
    fn followed(name: &str) -> bool {
        ["Quality_", "XQuality", "XGraphics", "RenderPipe", "UniversalRenderPipelineAsset"].iter().any(|p| name.starts_with(p))
    }

    unsafe fn read(&self, ty: *mut c_void, at: *const u8) -> Value {
        let text = match (self.rf.type_type)(ty) {
            T_BOOLEAN => (at.read() != 0).to_string(),
            T_CHAR => char::from_u32(u32::from(at.cast::<u16>().read_unaligned())).map_or("?".into(), |c| format!("{c:?}")),
            T_I1 => at.cast::<i8>().read().to_string(),
            T_U1 => at.read().to_string(),
            T_I2 => at.cast::<i16>().read_unaligned().to_string(),
            T_U2 => at.cast::<u16>().read_unaligned().to_string(),
            T_I4 => at.cast::<i32>().read_unaligned().to_string(),
            T_U4 => at.cast::<u32>().read_unaligned().to_string(),
            T_I8 => at.cast::<i64>().read_unaligned().to_string(),
            T_U8 => at.cast::<u64>().read_unaligned().to_string(),
            T_R4 => at.cast::<f32>().read_unaligned().to_string(),
            T_R8 => at.cast::<f64>().read_unaligned().to_string(),
            T_STRING => {
                let pointer = at.cast::<usize>().read_unaligned();
                if pointer == 0 { "null".into() } else { read_csharp_string(pointer).map_or("<unreadable string>".into(), |s| format!("{s:?}")) }
            }
            T_VALUETYPE => {
                let class = (self.rf.class_from_type)(ty);
                if class.is_null() || !(self.rf.class_is_enum)(class) {
                    format!("<{}>", self.il.type_name_of(ty))
                } else {
                    let base = (self.rf.type_type)((self.rf.enum_basetype)(class));
                    let raw = match base {
                        T_I1 => i64::from(at.cast::<i8>().read()),
                        T_U1 => i64::from(at.read()),
                        T_I2 => i64::from(at.cast::<i16>().read_unaligned()),
                        T_U2 => i64::from(at.cast::<u16>().read_unaligned()),
                        T_U4 => i64::from(at.cast::<u32>().read_unaligned()),
                        T_I8 | T_U8 => at.cast::<i64>().read_unaligned(),
                        _ => i64::from(at.cast::<i32>().read_unaligned()),
                    };
                    format!("{}:{raw}", self.class_name(class))
                }
            }
            T_CLASS | T_OBJECT => return Value::Object(at.cast::<*mut c_void>().read_unaligned()),
            // A generic instance is a reference only when its class is not a value type (Nullable<T>, KeyValuePair...).
            T_GENERICINST => {
                let class = (self.rf.class_from_type)(ty);
                if !class.is_null() && !(self.rf.class_is_valuetype)(class) {
                    return Value::Object(at.cast::<*mut c_void>().read_unaligned());
                }
                format!("<{}>", self.il.type_name_of(ty))
            }
            T_SZARRAY => self.array(ty, at.cast::<*mut c_void>().read_unaligned()),
            _ => format!("<{}>", self.il.type_name_of(ty)),
        };
        Value::Text(text)
    }

    unsafe fn array(&self, ty: *mut c_void, array: *mut c_void) -> String {
        if array.is_null() {
            return "null".into();
        }
        let name = self.il.type_name_of(ty);
        let length = (self.rf.array_length)(array) as usize;
        // Il2CppArray: object header (16) + bounds (8) + max_length (8), elements follow.
        let data = array.cast::<u8>().add(0x20);
        let shown = length.min(ARRAY_LIMIT);
        let items: Option<Vec<String>> = match name.as_str() {
            "System.Single[]" => Some((0..shown).map(|i| data.cast::<f32>().add(i).read_unaligned().to_string()).collect()),
            "System.Int32[]" => Some((0..shown).map(|i| data.cast::<i32>().add(i).read_unaligned().to_string()).collect()),
            "System.Boolean[]" => Some((0..shown).map(|i| (data.add(i).read() != 0).to_string()).collect()),
            _ => None,
        };
        match items {
            Some(items) => format!("[{}{}]", items.join(", "), if length > shown { format!(", … {length} total") } else { String::new() }),
            None => format!("<{name} len {length}>"),
        }
    }

    unsafe fn put(&mut self, key: String, value: Value, depth: u32) {
        match value {
            Value::Text(text) => {
                self.values.insert(key, text);
            }
            Value::Object(object) => self.object(key, object, depth),
        }
    }

    unsafe fn object(&mut self, key: String, object: *mut c_void, depth: u32) {
        if object.is_null() {
            self.values.insert(key, "null".into());
            return;
        }
        let class = (self.rf.object_class)(object);
        let name = self.class_name(class);
        if depth == 0 || !Self::followed(&name) {
            self.values.insert(key, format!("<{name}>"));
            return;
        }
        self.fields(&key, class, object.cast(), depth - 1);
    }

    /// Instance fields of `object` (`base` non-null) or static fields of `class` (`base` null).
    unsafe fn fields(&mut self, prefix: &str, class: *mut c_void, base: *const u8, depth: u32) {
        let statics = base.is_null();
        let mut iterator = null_mut();
        loop {
            let field = (self.rf.class_fields)(class, &mut iterator);
            if field.is_null() {
                break;
            }
            let flags = (self.il.field_flags)(field);
            if flags & FIELD_LITERAL != 0 || (flags & FLAG_STATIC != 0) != statics {
                continue;
            }
            let name = c_string((self.rf.field_name)(field)).unwrap_or_else(|| "<unnamed>".into());
            let ty = (self.il.field_type)(field);
            // Large enough for any primitive, pointer or small struct (Rect is 16 bytes).
            let mut buffer = [0u8; 64];
            let at = if statics {
                (self.il.static_get)(field, buffer.as_mut_ptr().cast());
                buffer.as_ptr()
            } else {
                base.add((self.rf.field_offset)(field))
            };
            let value = self.read(ty, at);
            self.put(format!("{prefix}.{name}"), value, depth);
        }
    }

    /// Static zero-argument method; its boxed result is read by return type.
    unsafe fn call(&mut self, key: String, method: *mut c_void, depth: u32) {
        let mut exception = null_mut();
        let boxed = (self.il.invoke)(method, null_mut(), null_mut(), &mut exception);
        if !exception.is_null() {
            self.values.insert(key, "<threw>".into());
            return;
        }
        let ty = (self.rf.return_type)(method);
        let value = match (self.rf.type_type)(ty) {
            T_CLASS | T_OBJECT | T_GENERICINST | T_STRING | T_SZARRAY if boxed.is_null() => Value::Text("null".into()),
            T_CLASS | T_OBJECT | T_GENERICINST => Value::Object(boxed),
            // Reference results come back as the object itself; read() expects a slot holding the pointer.
            T_STRING | T_SZARRAY => self.read(ty, (&boxed as *const *mut c_void).cast()),
            _ if boxed.is_null() => Value::Text("null".into()),
            _ => self.read(ty, (self.il.unbox)(boxed).cast()),
        };
        self.put(key, value, depth);
    }
}

struct Roots {
    urp: Option<*mut c_void>,
    /// Static field `RenderPIplineQualityManger._DefaultPipelineAssets` (read as memory, see `runtime_data`).
    urp_assets: Option<*mut c_void>,
    urp_flags: Vec<(&'static str, *mut c_void)>,
    quality_manager: Option<*mut c_void>,
    resolution_manager: Option<*mut c_void>,
    unity_quality: Vec<(&'static str, *mut c_void)>,
    active_scene: Option<(*mut c_void, *mut c_void)>,
}

unsafe fn method(il: &Il2cpp, class: *mut c_void, name: &str, params: i32) -> Option<*mut c_void> {
    let c_name = CString::new(name).ok()?;
    let method = (il.class_method)(class, c_name.as_ptr(), params);
    (!method.is_null() && (il.method_flags)(method, null_mut()) & FLAG_STATIC != 0).then_some(method)
}

unsafe fn class(il: &Il2cpp, assemblies: &[*mut c_void], namespace: &str, name: &str) -> Option<*mut c_void> {
    let full = if namespace.is_empty() { name.to_string() } else { format!("{namespace}.{name}") };
    match il.find_class(assemblies, namespace, name) {
        Some((class, image)) => {
            ok(&format!("lookup {full}"), &format!("assembly={image}"));
            (il.class_init)(class);
            Some(class)
        }
        None => {
            failed(&format!("lookup {full}"), &format!("class not found in any of {} loaded assemblies; it is skipped", assemblies.len()));
            None
        }
    }
}

unsafe fn roots(il: &Il2cpp, assemblies: &[*mut c_void]) -> Roots {
    let urp = class(il, assemblies, URP_NAMESPACE, URP_MANAGER);
    let urp_assets = urp.and_then(|c| {
        let name = CString::new("_DefaultPipelineAssets").ok()?;
        let field = (il.class_field)(c, name.as_ptr());
        (!field.is_null() && (il.field_flags)(field) & FLAG_STATIC != 0).then_some(field)
    });
    let urp_flags = urp.map_or_else(Vec::new, |c| URP_FLAGS.iter().filter_map(|&n| Some((n, method(il, c, n, 0)?))).collect());
    let unity_quality = class(il, assemblies, "UnityEngine", "QualitySettings")
        .map_or_else(Vec::new, |c| UNITY_QUALITY_GETTERS.iter().filter_map(|&n| Some((n, method(il, c, n, 0)?))).collect());
    let active_scene = class(il, assemblies, "UnityEngine.SceneManagement", "SceneManager")
        .and_then(|manager| method(il, manager, "GetActiveScene", 0))
        .zip(class(il, assemblies, "UnityEngine.SceneManagement", "Scene").and_then(|scene| method(il, scene, "GetNameInternal", 1)));
    let roots = Roots {
        urp,
        urp_assets,
        urp_flags,
        quality_manager: class(il, assemblies, "", "XQualityManager"),
        resolution_manager: class(il, assemblies, "", "XResolutionManager"),
        unity_quality,
        active_scene,
    };
    let missing: Vec<&str> = [
        (roots.urp.is_some() && roots.urp_assets.is_none(), "RenderPIplineQualityManger._DefaultPipelineAssets (the runtime quality data is skipped)"),
        (roots.urp.is_some() && roots.urp_flags.len() != URP_FLAGS.len(), "some RenderPIplineQualityManger.Is*() flags"),
        (roots.unity_quality.len() != UNITY_QUALITY_GETTERS.len(), "some UnityEngine.QualitySettings getters"),
        (roots.active_scene.is_none(), "SceneManager.GetActiveScene()/Scene.GetNameInternal(Int32)"),
    ].into_iter().filter_map(|(gone, name)| gone.then_some(name)).collect();
    if !missing.is_empty() {
        failed("graphics dump roots", &format!("not found in this build: {}; the rest is dumped", missing.join(", ")));
    }
    roots
}

unsafe fn scene_name(il: &Il2cpp, active_scene: Option<(*mut c_void, *mut c_void)>) -> String {
    let Some((get_active, get_name)) = active_scene else { return "?".into() };
    let mut exception = null_mut();
    let boxed = (il.invoke)(get_active, null_mut(), null_mut(), &mut exception);
    if !exception.is_null() || boxed.is_null() {
        return "?".into();
    }
    // Scene is a struct holding one Int32 handle.
    let mut handle = *((il.unbox)(boxed) as *const i32);
    let mut args = [(&mut handle as *mut i32).cast::<c_void>()];
    let name = (il.invoke)(get_name, null_mut(), args.as_mut_ptr(), &mut exception);
    if !exception.is_null() || name.is_null() {
        return "?".into();
    }
    read_csharp_string(name as usize).unwrap_or_else(|| "?".into())
}

/// The `RenderPIplineQualityManger.GetRuntimeData()` object, null while the pipeline asset is not loaded yet.
/// Never invokes `GetRuntimeData()`: its getter chain may call `GraphicsSettings.defaultRenderPipeline`, which
/// crashes inside UnityPlayer while the engine tears down. The same object is read straight from managed memory:
/// the static `_DefaultPipelineAssets`, then that asset's `RenderPipelineDefaultQualityData` field. `offset` caches
/// the field lookup (`Some(None)` = lookup failed, already logged).
unsafe fn runtime_data(walker: &Walker, assets: Option<*mut c_void>, offset: &mut Option<Option<usize>>) -> *mut c_void {
    let Some(assets) = assets else { return null_mut() };
    let mut asset: *mut c_void = null_mut();
    (walker.il.static_get)(assets, (&mut asset as *mut *mut c_void).cast());
    if asset.is_null() {
        return null_mut();
    }
    let offset = *offset.get_or_insert_with(|| {
        const NAME: &str = "RenderPipelineDefaultQualityData";
        let class = (walker.rf.object_class)(asset);
        let field = CString::new(NAME).ok().map(|n| (walker.il.class_field)(class, n.as_ptr())).filter(|f| !f.is_null());
        let found = field.filter(|&f| {
            (walker.il.field_flags)(f) & FLAG_STATIC == 0 && walker.il.type_name_of((walker.il.field_type)(f)).ends_with("RenderPipelineQualityRuntimeData")
        });
        match found {
            Some(f) => Some((walker.rf.field_offset)(f)),
            None => {
                failed("lookup RenderPipelineDefaultQualityData", &format!("instance field of RenderPipelineQualityRuntimeData type not found on `{}`; overrides, the quality catalogue and the runtime-data dump are skipped", walker.class_name(class)));
                None
            }
        }
    });
    offset.map_or(null_mut(), |offset| asset.cast::<u8>().add(offset).cast::<*mut c_void>().read_unaligned())
}

/// Detaches from IL2CPP, logs once, then sleeps forever. Never returns, so the detours stay installed: the game may
/// still call the detoured functions while it tears down. Without the detach, `il2cpp_shutdown` waits for this
/// attached thread forever and the game hangs on exit.
unsafe fn park(il: &Il2cpp) -> ! {
    il.detach_current();
    ok("graphics options", "the game is quitting; detached from IL2CPP and stopped touching the game");
    loop {
        std::thread::sleep(Duration::from_secs(3600));
    }
}

unsafe fn run() -> Result<()> {
    let il = Il2cpp::load().context("il2cpp exports for graphics options")?;
    let rf = Reflect {
        class_fields: export("il2cpp_class_get_fields")?,
        field_name: export("il2cpp_field_get_name")?,
        field_offset: export("il2cpp_field_get_offset")?,
        object_class: export("il2cpp_object_get_class")?,
        class_from_type: export("il2cpp_class_from_type")?,
        class_is_enum: export("il2cpp_class_is_enum")?,
        class_is_valuetype: export("il2cpp_class_is_valuetype")?,
        enum_basetype: export("il2cpp_class_enum_basetype")?,
        class_name: export("il2cpp_class_get_name")?,
        type_type: export("il2cpp_type_get_type")?,
        array_length: export("il2cpp_array_length")?,
        return_type: export("il2cpp_method_get_return_type")?,
    };
    ok("il2cpp exports (graphics options)", "class/field/type reflection, runtime_invoke and helpers resolved");
    // Stays attached for the whole process; the loop below runs until the game exits.
    let assemblies = il.wait_attach()?;
    let mut fps = Fps::new(&il, &assemblies);
    let mut nofade = NoFade::new(&il, &assemblies);
    let mut fight_fov = FightFov::default();
    let bridge = LuaBridge::install(&il, &assemblies).map_err(|error| format!("{error:#}"));
    if let Err(message) = &bridge {
        failed("Lua bridge", &format!("{message}; AscNet Settings, ultrawide, ui16x9, uifov, uiscale, dpsmeter and the Lua console are unavailable"));
    }
    let mut ultrawide = Ultrawide::default();
    let mut ui_fit = UiFit::default();
    let mut ui_fov = UiFov::default();
    let mut ui_scale = UiScale::default();
    let mut dps_meter = DpsMeter::default();
    let mut settings = graphics_settings::Settings::default();
    let mut damage_hook = graphics_dps_hits::DamageHook::default();
    let mut overlay_started = false;
    let mut notice = graphics_notice::Notice::default();
    let _quit_hook = match graphics_tweaks::watch_quit(&il, &assemblies) {
        Ok(hook) => Some(hook),
        Err(error) => {
            failed("exit detection", &format!("{error:#}; exit detection unavailable, the worker may touch the game while it quits"));
            None
        }
    };
    let roots = roots(&il, &assemblies);
    let started = Instant::now();
    let mut walker = Walker { il: &il, rf, values: BTreeMap::new() };
    let mut render_scale = RenderScale::default();
    let mut data_offset = None;
    let mut overrides = Overrides::default();
    let mut previous: Option<BTreeMap<String, String>> = None;
    let mut tick = 0u64;
    let quitting = || QUITTING.load(Ordering::Relaxed);
    loop {
        if quitting() {
            park(&il);
        }
        settings.receive(&bridge);
        notice.update(&bridge);
        let (desired, dps_reset) = {
            let live = live();
            (live.desired.clone(), live.dps_reset)
        };
        if !overlay_started && (desired.overlay || desired.dps_meter) {
            // Installing through the bridge avoids Wine's dummy-window destroy stall on this worker.
            match &bridge {
                Ok(_) => graphics_lua::start_overlay_on_main_thread(),
                Err(_) => graphics_overlay::start(),
            }
            overlay_started = true;
        }
        let scene = || scene_name(&il, roots.active_scene);
        let runtime_data = runtime_data(&walker, roots.urp_assets, &mut data_offset);
        let mut errors = Vec::new();
        if !runtime_data.is_null() {
            overrides.apply(&walker, runtime_data, &desired.overrides, &scene, &mut errors);
        }
        if quitting() {
            park(&il);
        }
        render_scale.update(&il, roots.urp, desired.render_scale, &scene, &mut errors);
        if quitting() {
            park(&il);
        }
        let (target_frame_rate, vsync_count) = fps.update(&il, desired.fps, &mut errors);
        nofade.update(&il, desired.nofade, &mut errors);
        fight_fov.update(&il, &assemblies, desired.fight_fov, &mut errors);
        ultrawide.update(&bridge, desired.ultrawide, &mut errors);
        ui_scale.update(&bridge, desired.ui_scale, &mut errors);
        ui_fit.update(&bridge, desired.ui16x9, desired.ui_scale, &mut errors);
        ui_fov.update(&bridge, desired.ui_fov, &mut errors);
        damage_hook.update(&il, &assemblies, desired.dps_meter);
        let dps = dps_meter.update(&bridge, desired.dps_meter, dps_reset, &mut errors);
        let catalogue = if runtime_data.is_null() { Vec::new() } else { graphics_tweaks::catalogue(&walker, runtime_data, &overrides) };
        if quitting() {
            park(&il);
        }
        {
            let mut live = live();
            live.catalogue = catalogue;
            live.dps = dps;
            live.status = Status {
                scene: scene(),
                game_render_scale: RenderScale::game_scale(),
                effective_render_scale: render_scale.effective,
                errors,
                target_frame_rate,
                vsync_count,
                screen: LuaBridge::screen(),
                fullscreen: LuaBridge::fullscreen(),
            };
        }
        settings.publish(&bridge);
        if desired.dump && tick % DUMP_EVERY == 0 {
            if quitting() {
                park(&il);
            }
            dump_pass(&mut walker, &roots, runtime_data, &mut previous, tick / DUMP_EVERY + 1, started, &scene());
        }
        tick += 1;
        std::thread::sleep(TICK);
    }
}

unsafe fn dump_pass(walker: &mut Walker, roots: &Roots, runtime_data: *mut c_void, previous: &mut Option<BTreeMap<String, String>>, pass: u64, started: Instant, scene: &str) {
    walker.values.clear();
    if let Some(urp) = roots.urp {
        walker.fields("URP", urp, std::ptr::null(), 1);
        walker.object("URP.GetRuntimeData()".into(), runtime_data, 2);
        for &(name, get) in &roots.urp_flags {
            walker.call(format!("URP.{name}()"), get, 0);
        }
    }
    if let Some(manager) = roots.quality_manager {
        walker.fields("XQualityManager", manager, std::ptr::null(), 3);
    }
    if let Some(manager) = roots.resolution_manager {
        walker.fields("XResolutionManager", manager, std::ptr::null(), 0);
    }
    for &(name, get) in &roots.unity_quality {
        walker.call(format!("QualitySettings.{}", name.trim_start_matches("get_")), get, 0);
    }

    let changed: Vec<(&String, &String)> = walker.values.iter()
        .filter(|(key, value)| previous.as_ref().is_none_or(|p| p.get(*key) != Some(*value)))
        .collect();
    let removed = previous.as_ref().map_or(0, |p| p.keys().filter(|k| !walker.values.contains_key(*k)).count());
    if !changed.is_empty() || removed != 0 {
        let kind = if previous.is_none() { "initial" } else { "changed" };
        ok("graphics dump", &format!("pass {pass}, t={}s, scene={scene:?}, {kind} {} of {} value(s){}",
            started.elapsed().as_secs(), changed.len(), walker.values.len(),
            if removed != 0 { format!(", {removed} gone") } else { String::new() }));
        for (key, value) in changed {
            il2cpp::ok(&format!("graphics {key}"), value);
        }
    }
    *previous = Some(walker.values.clone());
}

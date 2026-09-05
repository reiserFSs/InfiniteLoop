use std::collections::HashMap;

use anyhow::Result;

use crate::interceptor::Interceptor;

use windows::{
    core::PCSTR,
    Win32::{
        Foundation::HMODULE,
        System::LibraryLoader::{GetModuleHandleA, GetProcAddress},
    },
};

mod network;

pub use network::Http;

#[derive(Default)]
pub struct ModuleManager {
    modules: HashMap<ModuleType, Box<dyn MhyModule>>,
}
unsafe impl Sync for ModuleManager {}
unsafe impl Send for ModuleManager {}

impl ModuleManager {
    pub unsafe fn enable(&mut self, module: impl MhyModule + 'static) -> Result<()> {
        let mut boxed_module = Box::new(module);
        boxed_module.init()?;
        self.modules
            .insert(boxed_module.get_module_type(), boxed_module);
        Ok(())
    }

    #[allow(dead_code)]
    pub unsafe fn disable(&mut self, module_type: ModuleType) {
        let module = self.modules.remove(&module_type);
        if let Some(mut module) = module {
            module.as_mut().de_init().unwrap();
        }
    }
}

#[derive(Copy, Clone, Hash, PartialEq, Eq)]
pub enum ModuleType {
    Http,
}

pub trait MhyModule {
    unsafe fn init(&mut self) -> Result<()>;
    unsafe fn de_init(&mut self) -> Result<()>;
    fn get_module_type(&self) -> ModuleType;
}

pub struct MhyContext<T> {
    pub assembly_base: usize,
    pub interceptor: Interceptor,
    _phantom: std::marker::PhantomData<T>,
}

#[allow(dead_code)]
impl<T> MhyContext<T> {
    pub const fn new(assembly_base: usize) -> Self {
        Self {
            assembly_base,
            interceptor: Interceptor::new(),
            _phantom: std::marker::PhantomData,
        }
    }

    pub fn get_export(&self, module: &str, name: &str) -> anyhow::Result<usize> {
        let module_cstr = std::ffi::CString::new(module)?;
        let name_cstr = std::ffi::CString::new(name)?;

        unsafe {
            let handle = GetModuleHandleA(PCSTR(module_cstr.as_ptr() as _))
                .ok()
                .ok_or_else(|| anyhow::anyhow!("Module `{}` not found", module))?;

            let addr = GetProcAddress::<HMODULE, PCSTR>(handle, PCSTR(name_cstr.as_ptr() as _))
                .ok_or_else(|| anyhow::anyhow!("Symbol `{}` not found", name))?;

            Ok(addr as usize)
        }
    }
}

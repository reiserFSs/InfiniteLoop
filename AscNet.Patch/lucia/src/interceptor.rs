use anyhow::{bail, Result};
use ilhook::x64::{
    CallbackOption, HookFlags, HookPoint, HookType, Hooker, JmpBackRoutine, JmpToRetRoutine, RetnRoutine,
};

unsafe impl Send for Interceptor {}
unsafe impl Sync for Interceptor {}

pub struct Interceptor {
    pub hooks: Vec<HookPoint>,
}

impl Interceptor {
    pub const fn new() -> Self {
        Self { hooks: Vec::new() }
    }

    pub unsafe fn attach(&mut self, addr: usize, routine: JmpBackRoutine) -> Result<()> {
        let hooker = Hooker::new(
            addr,
            HookType::JmpBack(routine),
            CallbackOption::None,
            0,
            HookFlags::empty(),
        );

        let Ok(hook_point) = hooker.hook() else {
            bail!("Failed to attach 0x{addr:X}")
        };

        self.hooks.push(hook_point);
        Ok(())
    }

    /// The routine returns where execution continues, with the caller's registers and stack restored: the
    /// original function pointer it receives, or a stand-in with the same signature.
    pub unsafe fn redirect(&mut self, addr: usize, routine: JmpToRetRoutine) -> Result<()> {
        let hooker = Hooker::new(addr, HookType::JmpToRet(routine), CallbackOption::None, 0, HookFlags::empty());
        let Ok(hook_point) = hooker.hook() else {
            bail!("Failed to redirect 0x{addr:X}")
        };
        self.hooks.push(hook_point);
        Ok(())
    }

    #[allow(dead_code)]
    pub unsafe fn replace(&mut self, addr: usize, routine: RetnRoutine) -> Result<()> {
        let hooker = Hooker::new(
            addr,
            HookType::Retn(routine),
            CallbackOption::None,
            0,
            HookFlags::empty(),
        );

        let Ok(hook_point) = hooker.hook() else {
            bail!("Failed to replace 0x{addr:X}")
        };

        self.hooks.push(hook_point);
        Ok(())
    }
}

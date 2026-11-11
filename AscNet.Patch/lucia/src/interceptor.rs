use anyhow::{bail, Result};
use iced_x86::{Decoder, DecoderOptions, FlowControl};
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
        check_prologue(addr)?;
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
        check_prologue(addr)?;
        let hooker = Hooker::new(addr, HookType::JmpToRet(routine), CallbackOption::None, 0, HookFlags::empty());
        let Ok(hook_point) = hooker.hook() else {
            bail!("Failed to redirect 0x{addr:X}")
        };
        self.hooks.push(hook_point);
        Ok(())
    }

    #[allow(dead_code)]
    pub unsafe fn replace(&mut self, addr: usize, routine: RetnRoutine) -> Result<()> {
        check_prologue(addr)?;
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

/// ilhook overwrites 14 bytes and copies the displaced instructions into a trampoline without remapping branch
/// targets, so a branch inside them to another displaced byte lands in the patch (seen as an access violation at
/// `WS2_32!GetAddrInfoW+0xB`), and a terminator before byte 14 means the patch runs into the next function.
unsafe fn check_prologue(addr: usize) -> Result<()> {
    if let Some(reason) = prologue_hazard(addr as u64, std::slice::from_raw_parts(addr as *const u8, 32)) {
        bail!("0x{addr:X} prologue unsafe to patch: {reason}");
    }
    Ok(())
}

fn prologue_hazard(ip: u64, code: &[u8]) -> Option<&'static str> {
    const PATCH: u64 = 14;
    let mut decoder = Decoder::with_ip(64, code, ip, DecoderOptions::NONE);
    let mut insts = Vec::new();
    let mut end = ip;
    while end < ip + PATCH {
        let inst = decoder.decode();
        if inst.is_invalid() { return Some("undecodable"); }
        end = inst.next_ip();
        insts.push(inst);
    }
    for (index, inst) in insts.iter().enumerate() {
        match inst.flow_control() {
            FlowControl::ConditionalBranch | FlowControl::UnconditionalBranch | FlowControl::Call
                if (ip..end).contains(&inst.near_branch_target()) && inst.near_branch_target() != 0 => return Some("branch into displaced bytes"),
            FlowControl::Return | FlowControl::UnconditionalBranch | FlowControl::IndirectBranch | FlowControl::Exception
                if index + 1 < insts.len() => return Some("function ends inside displaced bytes"),
            _ => {}
        }
    }
    None
}

#[cfg(test)]
mod tests {
    use super::prologue_hazard;
    #[test]
    fn rejects_branches_into_the_patch() {
        // test rcx,rcx; je +6 (to +0xB); mov rax,rsp-ish filler; ...
        let inward = [0x48, 0x85, 0xC9, 0x74, 0x06, 0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x74, 0x24, 0x10, 0x90, 0x90, 0x90];
        assert_eq!(prologue_hazard(0x1000, &inward), Some("branch into displaced bytes"));
        let outward = [0x48, 0x85, 0xC9, 0x0F, 0x84, 0x00, 0x01, 0x00, 0x00, 0x48, 0x89, 0x5C, 0x24, 0x08, 0x90, 0x90];
        assert_eq!(prologue_hazard(0x1000, &outward), None);
        let short = [0x33, 0xC0, 0xC3, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC];
        assert_eq!(prologue_hazard(0x1000, &short), Some("function ends inside displaced bytes"));
        assert_eq!(prologue_hazard(0x1000, &[0x48, 0x83, 0xEC, 0x28, 0x48, 0x8B, 0xC4, 0x48, 0x89, 0x5C, 0x24, 0x08, 0x90, 0x90, 0x90]), None);
    }
}

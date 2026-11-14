fn main() {
    // Messages are the English literals in the .slint files; `lang/<code>/LC_MESSAGES/ascnet-launcher.po` translates them.
    // The Slint compiler recurses deeply over the long message tables in ui/state.slint. Windows gives the
    // build script's main thread only 1 MiB of stack, which overflows (STATUS_STACK_OVERFLOW on CI), so
    // compile on a thread with an explicit 64 MiB stack.
    std::thread::Builder::new()
        .stack_size(64 * 1024 * 1024)
        .spawn(|| {
            let config = slint_build::CompilerConfiguration::new()
                .with_bundled_translations("lang")
                .with_default_translation_context(slint_build::DefaultTranslationContext::None);
            slint_build::compile_with_config("ui/app.slint", config).unwrap();
        })
        .unwrap()
        .join()
        .unwrap();

    // Windows exe icon. Cross builds from macOS/Linux need `llvm-rc` on PATH (brew `llvm`); winresource finds it there.
    if std::env::var("CARGO_CFG_TARGET_OS").unwrap() == "windows" {
        println!("cargo:rerun-if-changed=packaging/icon/ascnet.ico");
        winresource::WindowsResource::new().set_icon("packaging/icon/ascnet.ico").compile().unwrap();
    }
}

fn main() {
    // Messages are the English literals in the .slint files; `lang/<code>/LC_MESSAGES/ascnet-launcher.po` translates them.
    let config = slint_build::CompilerConfiguration::new()
        .with_bundled_translations("lang")
        .with_default_translation_context(slint_build::DefaultTranslationContext::None);
    slint_build::compile_with_config("ui/app.slint", config).unwrap();

    // Windows exe icon. Cross builds from macOS/Linux need `llvm-rc` on PATH (brew `llvm`); winresource finds it there.
    if std::env::var("CARGO_CFG_TARGET_OS").unwrap() == "windows" {
        println!("cargo:rerun-if-changed=packaging/icon/ascnet.ico");
        winresource::WindowsResource::new().set_icon("packaging/icon/ascnet.ico").compile().unwrap();
    }
}

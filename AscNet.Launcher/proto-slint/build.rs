fn main() {
    let cfg = slint_build::CompilerConfiguration::new().with_bundled_translations("lang").with_default_translation_context(slint_build::DefaultTranslationContext::None);
    slint_build::compile_with_config("ui/app.slint", cfg).unwrap();
}

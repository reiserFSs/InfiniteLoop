use anyhow::{Context, Result};
use std::{
    collections::BTreeMap,
    env, fs,
    path::{Path, PathBuf},
};

pub fn discover_game() -> Result<Option<PathBuf>> {
    for root in steam_roots() {
        for library in libraries(&root) {
            let apps = library.join("steamapps");
            let Ok(entries) = fs::read_dir(&apps) else {
                continue;
            };
            for entry in entries.flatten() {
                let name = entry.file_name();
                let name = name.to_string_lossy();
                if !name.starts_with("appmanifest_") || !name.ends_with(".acf") {
                    continue;
                }
                let Ok(text) = fs::read_to_string(entry.path()) else {
                    continue;
                };
                let Ok(fields) = object_fields(&text, "AppState") else {
                    continue;
                };
                let Some(dir) = fields.get("installdir") else {
                    continue;
                };
                let candidate = apps.join("common").join(dir);
                if valid_game_directory(&candidate) {
                    return Ok(Some(candidate));
                }
            }
        }
    }
    Ok(None)
}

pub fn valid_game_directory(path: &Path) -> bool {
    path.is_dir() && path.join("PGR.exe").is_file()
}

fn steam_roots() -> Vec<PathBuf> {
    let mut roots = Vec::new();
    if let Some(path) = env::var_os("STEAM_PATH") {
        roots.push(path.into());
    }
    if let Some(path) = env::var_os("PROGRAMFILES(X86)") {
        roots.push(PathBuf::from(path).join("Steam"));
    }
    if let Some(path) = env::var_os("PROGRAMFILES") {
        roots.push(PathBuf::from(path).join("Steam"));
    }
    roots.sort();
    roots.dedup();
    roots
}

fn libraries(root: &Path) -> Vec<PathBuf> {
    let mut result = vec![root.to_path_buf()];
    let file = root.join("steamapps/libraryfolders.vdf");
    let Ok(text) = fs::read_to_string(file) else {
        return result;
    };
    if let Ok(fields) = object_fields(&text, "libraryfolders") {
        for value in fields.values() {
            let path = PathBuf::from(value);
            if path.join("steamapps").is_dir() {
                result.push(path);
            }
        }
    }
    // Modern VDF nests each library's path in a numbered object.
    let tokens = tokens(&text).unwrap_or_default();
    for pair in tokens.windows(2) {
        if pair[0].eq_ignore_ascii_case("path") {
            let path = PathBuf::from(&pair[1]);
            if path.join("steamapps").is_dir() {
                result.push(path);
            }
        }
    }
    result.sort();
    result.dedup();
    result
}

fn object_fields(text: &str, object: &str) -> Result<BTreeMap<String, String>> {
    let t = tokens(text)?;
    let start = t
        .iter()
        .position(|v| v.eq_ignore_ascii_case(object))
        .with_context(|| format!("missing VDF object {object}"))?;
    let mut fields = BTreeMap::new();
    let mut i = start + 1;
    if t.get(i).map(String::as_str) != Some("{") {
        anyhow::bail!("invalid VDF object {object}")
    }
    i += 1;
    let mut depth = 1;
    while i < t.len() && depth > 0 {
        match t[i].as_str() {
            "{" => {
                depth += 1;
                i += 1;
            }
            "}" => {
                depth -= 1;
                i += 1;
            }
            key if depth == 1 && t.get(i + 1).is_some_and(|v| v != "{" && v != "}") => {
                fields.insert(key.to_ascii_lowercase(), t[i + 1].clone());
                i += 2;
            }
            _ => i += 1,
        }
    }
    if depth != 0 {
        anyhow::bail!("unterminated VDF object {object}")
    }
    Ok(fields)
}

fn tokens(text: &str) -> Result<Vec<String>> {
    let bytes = text.as_bytes();
    let mut out = Vec::new();
    let mut i = 0;
    while i < bytes.len() {
        while i < bytes.len() && bytes[i].is_ascii_whitespace() {
            i += 1;
        }
        if i >= bytes.len() {
            break;
        }
        if bytes[i] == b'/' && bytes.get(i + 1) == Some(&b'/') {
            while i < bytes.len() && bytes[i] != b'\n' {
                i += 1;
            }
            continue;
        }
        if matches!(bytes[i], b'{' | b'}') {
            out.push((bytes[i] as char).to_string());
            i += 1;
            continue;
        }
        if bytes[i] != b'"' {
            anyhow::bail!("invalid VDF token at byte {i}")
        }
        i += 1;
        let mut value = String::new();
        while i < bytes.len() && bytes[i] != b'"' {
            if bytes[i] == b'\\' && i + 1 < bytes.len() && matches!(bytes[i + 1], b'\\' | b'"') {
                i += 1;
            }
            value.push(bytes[i] as char);
            i += 1;
        }
        if i == bytes.len() {
            anyhow::bail!("unterminated VDF string")
        }
        i += 1;
        out.push(value);
    }
    Ok(out)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn parses_nested_modern_vdf_and_escapes() {
        let text =
            r#""libraryfolders" { "0" { "path" "C:\\Program Files\\Steam" "apps" { "1" "2" } } }"#;
        let t = tokens(text).unwrap();
        assert!(t
            .windows(2)
            .any(|p| p == ["path", "C:\\Program Files\\Steam"]));
    }
}

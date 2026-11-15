//! Warns the player when the patch routes to a server that is not on their own PC. InfiniteLoop is free and meant
//! to run locally; people who sell access to hosted servers rely on victims not knowing that.
//!
//! Display path: the game's own `XUiManager.DialogTip` (OnlySure) through the Lua bridge (`graphics_lua.rs`).
//! The bridge is installed by the graphics worker unconditionally, whereas the D3D11 overlay's Present hook is only
//! installed when `ascnet-graphics.ini` enables `overlay`/`dpsmeter`, and a seller controls that file. A game dialog
//! needs no key press, stays until clicked, and the text is compiled in. Origin, region and text are all read from
//! the DLL/client, never from a server response or a file.
use std::net::IpAddr;

use super::graphics_lua::LuaBridge;
use crate::modules::{is_cn_client, network};

const CHUNK: &str = "remote server notice";

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Lang {
    En,
    ZhTw,
    Ko,
    Ja,
    ZhCn,
}

/// Region from the KRSDK.bin project id the patch already reads (`.../sdk_identity.rs`); unknown means English.
fn lang_for_project(project_id: &str) -> Lang {
    match project_id {
        "G279" => Lang::ZhTw,
        "G286" => Lang::Ko,
        "G282" => Lang::Ja,
        "G148" => Lang::ZhCn,
        _ => Lang::En,
    }
}

fn client_lang() -> Lang {
    if is_cn_client() {
        return Lang::ZhCn;
    }
    crate::sdk_identity::read_packaged()
        .ok()
        .and_then(|config| crate::sdk_identity::identity(&config).ok())
        .map_or(Lang::En, |id| lang_for_project(&id.project_id))
}

/// `(title, body)`; every body ends with the official repository.
fn text(lang: Lang) -> (&'static str, &'static str) {
    match lang {
        Lang::En => ("Warning", "You are connected to a server run by someone else. InfiniteLoop is free and meant to run on your own PC; if you paid for access, you were scammed. Official: github.com/reiserFSs/InfiniteLoop"),
        Lang::ZhTw => ("警告", "你正在連線到由他人架設的伺服器。InfiniteLoop 是免費的，應該在你自己的電腦上執行；如果你為此付費，你就被騙了。官方：github.com/reiserFSs/InfiniteLoop"),
        Lang::Ko => ("경고", "다른 사람이 운영하는 서버에 접속 중입니다. InfiniteLoop는 무료이며 본인의 PC에서 직접 실행하도록 만들어졌습니다. 접속 대가로 비용을 지불했다면 사기를 당한 것입니다. 공식: github.com/reiserFSs/InfiniteLoop"),
        Lang::Ja => ("警告", "他の人が運営しているサーバーに接続しています。InfiniteLoop は無料で、ご自身の PC で動かすためのものです。アクセスのために支払ったのなら、詐欺に遭っています。公式: github.com/reiserFSs/InfiniteLoop"),
        Lang::ZhCn => ("警告", "你正在连接由他人运营的服务器。InfiniteLoop 是免费的，应该在你自己的电脑上运行；如果你为此付费，你就被骗了。官方：github.com/reiserFSs/InfiniteLoop"),
    }
}

/// Host of an `http(s)://[user@]host[:port]` origin; IPv6 literals lose their brackets.
fn host_of(origin: &str) -> &str {
    let authority = origin.split_once("://").map_or(origin, |(_, rest)| rest);
    let authority = authority.split(['/', '?', '#']).next().unwrap_or("");
    let authority = authority.rsplit_once('@').map_or(authority, |(_, host)| host);
    match authority.strip_prefix('[') {
        Some(rest) => rest.split(']').next().unwrap_or(""),
        None => authority.split(':').next().unwrap_or(""),
    }
}

/// Loopback = 127.0.0.0/8, `::1` (also its IPv4-mapped form) and the name `localhost`. LAN addresses and every
/// other name are remote: the seller of a hosted server can be one hop away or behind any name.
fn is_loopback_host(host: &str) -> bool {
    if host.trim_end_matches('.').eq_ignore_ascii_case("localhost") {
        return true;
    }
    match host.parse::<IpAddr>() {
        Ok(IpAddr::V4(ip)) => ip.is_loopback(),
        Ok(IpAddr::V6(ip)) => ip.is_loopback() || ip.to_ipv4_mapped().is_some_and(|v4| v4.is_loopback()),
        Err(_) => false,
    }
}

fn lua_chunk(lang: Lang) -> String {
    let (title, body) = text(lang);
    format!(
        r#"
local ui, mgr = rawget(_G, "XUiManager"), rawget(_G, "XLuaUiManager")
if not (ui and mgr and ui.DialogTip) then return "pending" end
if not (mgr.IsUiShow("UiLogin") or mgr.IsUiShow("UiMain")) then return "pending" end
ui.DialogTip([==[{title}]==], [==[{body}]==], ui.DialogType.OnlySure)
return "shown"
"#
    )
}

#[derive(Default)]
pub(super) struct Notice {
    queued: bool,
    done: bool,
}

impl Notice {
    /// Called every worker tick; acts once, as soon as native routing has an origin and it is not loopback.
    pub(super) fn update(&mut self, bridge: &Result<LuaBridge, String>) {
        if self.done {
            return;
        }
        if !self.queued {
            let Some(origin) = network::routing_origin() else { return };
            let host = host_of(origin);
            self.queued = true;
            if is_loopback_host(host) {
                self.done = true;
                return;
            }
            crate::diag::log(&format!("[lucia] NOTICE remote server origin {host}"));
            match bridge {
                Ok(bridge) => bridge.queue(CHUNK, lua_chunk(client_lang())),
                Err(message) => {
                    super::failed(CHUNK, &format!("Lua bridge unavailable ({message}); the player was not shown the warning"));
                    self.done = true;
                }
            }
            return;
        }
        let Ok(bridge) = bridge else { return };
        match bridge.outcome(CHUNK) {
            Some((_, Ok(message))) => super::ok(CHUNK, &message),
            Some((_, Err(_))) => {} // already logged as FAILED by the bridge
            None => return,
        }
        self.done = true;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn remote(origin: &str) -> bool {
        !is_loopback_host(host_of(origin))
    }

    #[test]
    fn loopback_origins_stay_silent() {
        for origin in [
            "http://127.0.0.1:8080",
            "http://127.0.0.1",
            "http://127.1.2.3:8080/",
            "https://127.255.255.254:443",
            "http://[::1]:8080",
            "http://[0:0:0:0:0:0:0:1]:8080",
            "http://[::ffff:127.0.0.1]:8080",
            "http://localhost:8080",
            "http://LOCALHOST:8080",
            "http://localhost.:8080",
        ] {
            assert!(!remote(origin), "{origin}");
        }
    }

    #[test]
    fn everything_else_is_remote() {
        for origin in [
            "http://192.168.1.20:8080",
            "http://10.0.0.5:8080",
            "http://172.16.0.1",
            "http://169.254.1.1",
            "http://0.0.0.0:8080",
            "http://8.8.8.8",
            "http://[::]:8080",
            "http://[fe80::1]:8080",
            "http://[2001:db8::1]:8080",
            "http://example.com:8080",
            "https://play.example.net",
            "http://127.0.0.1.example.com:8080",
            "http://localhost.example.com:8080",
            "http://mylocalhost:8080",
            "http://127.0.0.1@evil.example:8080",
            "http://localhost@203.0.113.9:8080",
            "http://127.0.0.1:80@evil.example",
        ] {
            assert!(remote(origin), "{origin}");
        }
    }

    #[test]
    fn host_is_only_the_host() {
        assert_eq!(host_of("http://user:pw@play.example.com:8080/x"), "play.example.com");
        assert_eq!(host_of("https://[2001:db8::1]:8443"), "2001:db8::1");
        assert_eq!(host_of("http://192.168.1.20:8080"), "192.168.1.20");
    }

    #[test]
    fn language_follows_the_client_region() {
        for (project, lang) in [("G143", Lang::En), ("G279", Lang::ZhTw), ("G286", Lang::Ko), ("G282", Lang::Ja), ("G148", Lang::ZhCn), ("G999", Lang::En), ("", Lang::En)] {
            assert_eq!(lang_for_project(project), lang, "{project}");
        }
    }

    #[test]
    fn every_language_has_text_with_the_repo_and_embeds_in_lua() {
        let mut seen = Vec::new();
        for lang in [Lang::En, Lang::ZhTw, Lang::Ko, Lang::Ja, Lang::ZhCn] {
            let (title, body) = text(lang);
            assert!(body.ends_with("github.com/reiserFSs/InfiniteLoop") && !title.is_empty(), "{lang:?}");
            assert!(!title.contains("]==]") && !body.contains("]==]"));
            assert!(lua_chunk(lang).contains(body));
            assert!(!seen.contains(&body), "{lang:?} duplicates another language");
            seen.push(body);
        }
        assert_eq!(text(Lang::En).1, "You are connected to a server run by someone else. InfiniteLoop is free and meant to run on your own PC; if you paid for access, you were scammed. Official: github.com/reiserFSs/InfiniteLoop");
    }
}

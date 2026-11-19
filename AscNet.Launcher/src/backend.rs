//! Finds a MongoDB or AscNet server that already runs on this computer, so Play uses it instead of starting its own.
//!
//! Detection talks to the port: MongoDB must answer an OP_MSG `hello` (the wire protocol), AscNet must answer
//! `GET /api/launcher/status` (schemaVersion 1) with its game port listening. Anything else on a port the launcher needs is
//! an error, never a victim: the launcher starts, stops and shuts down only what it started itself.
use anyhow::{bail, Result};
use std::{
    io::{Read, Write},
    net::{Ipv4Addr, SocketAddr, TcpListener, TcpStream},
    time::Duration,
};

/// Where a hand-run server and `mongod` listen without any configuration (`Config.cs`, `launch-pgr-ascnet.sh`).
pub const DEFAULT: Ports = Ports { mongo: 27017, sdk: 8080, game: 2335 };

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Ports {
    pub mongo: u16,
    pub sdk: u16,
    pub game: u16,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ServerPlan {
    /// Start the build's own server on the build's ports.
    Own,
    /// An AscNet server already answers here; it is left alone.
    Reuse { sdk: u16, game: u16 },
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum MongoPlan {
    Own,
    /// A MongoDB already answers on this port; it is left alone (and never shut down).
    Reuse(u16),
    /// The server in use brings its own database.
    NotNeeded,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Plan {
    pub server: ServerPlan,
    pub mongo: MongoPlan,
}

impl Plan {
    pub const OWN: Plan = Plan { server: ServerPlan::Own, mongo: MongoPlan::Own };

    /// The SDK port the game's patch must talk to.
    pub fn sdk_port(&self, build: u16) -> u16 {
        match self.server {
            ServerPlan::Reuse { sdk, .. } => sdk,
            ServerPlan::Own => build,
        }
    }
}

/// What the launcher should start, given the build's ports. `use_running_mongo`: a server the launcher starts may point at
/// the MongoDB already running on [`DEFAULT`]'s port (the player's data) instead of a database of its own.
pub fn plan(build: Ports, use_running_mongo: bool) -> Result<Plan> {
    plan_against(build, DEFAULT, use_running_mongo)
}

/// [`plan`] with the well-known ports given (tests pass spare ones).
pub fn plan_against(build: Ports, known: Ports, use_running_mongo: bool) -> Result<Plan> {
    let server = server_plan(build, known)?;
    let mongo = if matches!(server, ServerPlan::Reuse { .. }) { MongoPlan::NotNeeded } else { mongo_plan(build.mongo, known.mongo, use_running_mongo)? };
    Ok(Plan { server, mongo })
}

fn server_plan(build: Ports, known: Ports) -> Result<ServerPlan> {
    match probe_server(build.sdk, build.game) {
        Probe::Running => Ok(ServerPlan::Reuse { sdk: build.sdk, game: build.game }),
        Probe::Taken(why) => bail!("{why}"),
        Probe::Free if (known.sdk, known.game) != (build.sdk, build.game) && probe_server(known.sdk, known.game) == Probe::Running => {
            Ok(ServerPlan::Reuse { sdk: known.sdk, game: known.game })
        }
        Probe::Free => Ok(ServerPlan::Own),
    }
}

fn mongo_plan(build: u16, known: u16, use_running: bool) -> Result<MongoPlan> {
    match probe_mongo(build) {
        Port::Mongo => Ok(MongoPlan::Reuse(build)),
        Port::Other => bail!("local port {build} is already occupied by something that is not MongoDB; stop it or run Set up again to pick new ports"),
        Port::Free if use_running && known != build && probe_mongo(known) == Port::Mongo => Ok(MongoPlan::Reuse(known)),
        Port::Free => Ok(MongoPlan::Own),
    }
}

#[derive(Debug, PartialEq, Eq)]
pub enum Port {
    Free,
    Mongo,
    Other,
}

fn can_bind(port: u16) -> bool {
    TcpListener::bind((Ipv4Addr::LOCALHOST, port)).is_ok()
}

fn listening(port: u16) -> bool {
    TcpStream::connect_timeout(&SocketAddr::new(Ipv4Addr::LOCALHOST.into(), port), Duration::from_millis(250)).is_ok()
}

const OP_MSG: i32 = 2013;
const REQUEST_ID: i32 = 0x4153_434e; // "ASCN"
const MAX_REPLY: usize = 65_536;

/// `{hello: 1, $db: "admin"}` as an OP_MSG: the handshake every MongoDB since 3.6 answers without authentication.
fn hello() -> Vec<u8> {
    let mut doc = vec![0, 0, 0, 0, 0x10];
    doc.extend(b"hello\0");
    doc.extend(1i32.to_le_bytes());
    doc.push(0x02);
    doc.extend(b"$db\0");
    doc.extend(6i32.to_le_bytes());
    doc.extend(b"admin\0");
    doc.push(0);
    let len = doc.len() as i32;
    doc[..4].copy_from_slice(&len.to_le_bytes());
    let mut message = Vec::with_capacity(21 + doc.len());
    message.extend((21 + len).to_le_bytes()); // header (16) + flagBits (4) + section kind (1) + document
    message.extend(REQUEST_ID.to_le_bytes());
    message.extend(0i32.to_le_bytes());
    message.extend(OP_MSG.to_le_bytes());
    message.extend(0u32.to_le_bytes());
    message.push(0);
    message.extend(doc);
    message
}

/// Whether the reply is an OP_MSG answering [`hello`]. Anything else (HTTP, silence, a closed socket) is not MongoDB.
fn is_hello_reply(stream: &mut TcpStream) -> bool {
    let mut header = [0u8; 16];
    if stream.read_exact(&mut header).is_err() {
        return false;
    }
    let field = |at: usize| i32::from_le_bytes(header[at..at + 4].try_into().unwrap());
    let length = field(0);
    if field(8) != REQUEST_ID || field(12) != OP_MSG || length < 21 || length as usize > MAX_REPLY {
        return false;
    }
    // The whole reply must arrive: a real server's reply is complete, a lookalike's is not.
    stream.read_exact(&mut vec![0u8; length as usize - 16]).is_ok()
}

/// What listens on `port`: nothing, a MongoDB, or something else.
pub fn probe_mongo(port: u16) -> Port {
    let Ok(mut stream) = TcpStream::connect_timeout(&SocketAddr::new(Ipv4Addr::LOCALHOST.into(), port), Duration::from_millis(500)) else {
        return if can_bind(port) { Port::Free } else { Port::Other };
    };
    let _ = stream.set_read_timeout(Some(Duration::from_secs(2)));
    let _ = stream.set_write_timeout(Some(Duration::from_secs(2)));
    if stream.write_all(&hello()).is_ok() && is_hello_reply(&mut stream) {
        Port::Mongo
    } else {
        Port::Other
    }
}

#[derive(Debug, PartialEq, Eq)]
pub enum Probe {
    Free,
    Running,
    /// Player-facing reason the ports cannot be used.
    Taken(String),
}

/// Whether `GET {origin}/api/launcher/status` is an AscNet server's answer.
pub fn ascnet_answers(client: &reqwest::blocking::Client, origin: &str) -> bool {
    client
        .get(format!("{origin}/api/launcher/status"))
        .send()
        .and_then(reqwest::blocking::Response::error_for_status)
        .ok()
        .and_then(|mut response| {
            let length = response.content_length();
            if length.is_some_and(|n| n > 65_536) {
                return None;
            }
            let bytes = crate::download::read_body(&mut response, length, 65_536).ok()?;
            if bytes.len() > 65_536 || length.is_some_and(|n| bytes.len() as u64 != n) {
                return None;
            }
            serde_json::from_slice::<serde_json::Value>(&bytes).ok()
        })
        .and_then(|value| value.get("schemaVersion").and_then(serde_json::Value::as_u64))
        == Some(1)
}

pub fn status_client() -> Result<reqwest::blocking::Client> {
    Ok(crate::download::wine_safe(reqwest::blocking::Client::builder().no_proxy().timeout(Duration::from_secs(2))).build()?)
}

pub fn probe_server(sdk: u16, game: u16) -> Probe {
    let answers = status_client().is_ok_and(|client| ascnet_answers(&client, &format!("http://127.0.0.1:{sdk}")));
    if answers {
        return if listening(game) {
            Probe::Running
        } else {
            Probe::Taken(format!("an AscNet server answers on port {sdk}, but nothing listens on its game port {game}; stop it or run Set up again to pick new ports"))
        };
    }
    for (port, what) in [(sdk, "SDK"), (game, "game")] {
        if !can_bind(port) {
            return Probe::Taken(format!("local {what} port {port} is already occupied by something that is not an AscNet server; stop it or run Set up again to pick new ports"));
        }
    }
    Probe::Free
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::{
        io::{BufRead, BufReader},
        process::{Child, Command, Stdio},
        thread,
    };

    fn spare() -> u16 {
        let listener = TcpListener::bind((Ipv4Addr::LOCALHOST, 0)).unwrap();
        listener.local_addr().unwrap().port()
    }

    fn spare_ports() -> Ports {
        Ports { mongo: spare(), sdk: spare(), game: spare() }
    }

    /// Accepts connections and never answers (a plain TCP service).
    fn silent() -> (TcpListener, u16) {
        let listener = TcpListener::bind((Ipv4Addr::LOCALHOST, 0)).unwrap();
        let port = listener.local_addr().unwrap().port();
        (listener, port)
    }

    /// A fake status endpoint: answers `GET /api/launcher/status` with `body`.
    fn status_endpoint(body: &'static str) -> u16 {
        let listener = TcpListener::bind((Ipv4Addr::LOCALHOST, 0)).unwrap();
        let port = listener.local_addr().unwrap().port();
        thread::spawn(move || {
            for stream in listener.incoming().flatten() {
                let mut stream = stream;
                let mut reader = BufReader::new(stream.try_clone().unwrap());
                let mut line = String::new();
                let _ = reader.read_line(&mut line);
                let ok = line.starts_with("GET /api/launcher/status ");
                let reply = if ok {
                    format!("HTTP/1.1 200 OK\r\ncontent-type: application/json\r\ncontent-length: {}\r\nconnection: close\r\n\r\n{body}", body.len())
                } else {
                    "HTTP/1.1 404 Not Found\r\ncontent-length: 0\r\nconnection: close\r\n\r\n".to_owned()
                };
                let _ = stream.write_all(reply.as_bytes());
            }
        });
        port
    }

    fn mongod() -> Option<std::path::PathBuf> {
        let from_build = crate::local::load_build().ok().flatten().map(|b| b.mongod);
        let from_env = std::env::var_os("ASCNET_TEST_MONGOD").map(std::path::PathBuf::from);
        [from_env, from_build, Some("mongod".into())].into_iter().flatten().find(|p| Command::new(p).arg("--version").stdout(Stdio::null()).stderr(Stdio::null()).status().is_ok_and(|s| s.success()))
    }

    struct Mongo(Child, std::path::PathBuf);
    impl Drop for Mongo {
        fn drop(&mut self) {
            let _ = self.0.kill();
            let _ = self.0.wait();
            let _ = std::fs::remove_dir_all(&self.1);
        }
    }

    fn start_mongod(binary: &std::path::Path, port: u16) -> Mongo {
        let dir = std::env::temp_dir().join(format!("ascnet-backend-mongo-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir_all(&dir).unwrap();
        let child = Command::new(binary)
            .args(["--bind_ip", "127.0.0.1", "--port", &port.to_string(), "--dbpath"])
            .arg(&dir)
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .spawn()
            .unwrap();
        let mongo = Mongo(child, dir);
        for _ in 0..100 {
            if listening(port) {
                return mongo;
            }
            thread::sleep(Duration::from_millis(100));
        }
        panic!("throwaway mongod did not start");
    }

    #[test]
    fn mongo_probe_tells_mongo_from_a_plain_listener_and_a_free_port() {
        assert_eq!(probe_mongo(spare()), Port::Free);
        let (_listener, port) = silent();
        assert_eq!(probe_mongo(port), Port::Other, "a TCP listener that never answers");
        let http = status_endpoint("{}");
        assert_eq!(probe_mongo(http), Port::Other, "an HTTP server");
        let Some(binary) = mongod() else {
            eprintln!("no mongod found (set ASCNET_TEST_MONGOD); skipping the real-mongod half");
            return;
        };
        let port = spare();
        let _mongo = start_mongod(&binary, port);
        assert_eq!(probe_mongo(port), Port::Mongo);
    }

    #[test]
    fn mongo_plan_reuses_starts_own_or_refuses() {
        let ports = spare_ports();
        // Free: start our own.
        assert_eq!(plan_against(ports, spare_ports(), true).unwrap(), Plan::OWN);
        // Something that is not MongoDB on the configured port: a clear error.
        let (_listener, taken) = silent();
        let error = plan_against(Ports { mongo: taken, ..ports }, spare_ports(), true).unwrap_err().to_string();
        assert!(error.contains(&taken.to_string()) && error.contains("not MongoDB"), "{error}");
        let Some(binary) = mongod() else { return };
        // A MongoDB on the configured port is used, whatever the setting says.
        let port = spare();
        let _mongo = start_mongod(&binary, port);
        for opt in [true, false] {
            assert_eq!(plan_against(Ports { mongo: port, ..ports }, spare_ports(), opt).unwrap().mongo, MongoPlan::Reuse(port));
        }
        // The player's MongoDB on the well-known port: used only when they opted in.
        let known = Ports { mongo: port, ..spare_ports() };
        assert_eq!(plan_against(ports, known, true).unwrap().mongo, MongoPlan::Reuse(port));
        assert_eq!(plan_against(ports, known, false).unwrap().mongo, MongoPlan::Own);
    }

    #[test]
    fn server_plan_reuses_an_ascnet_status_endpoint_only() {
        let build = spare_ports();
        let none = spare_ports();
        assert_eq!(plan_against(build, none, false).unwrap(), Plan::OWN);

        let sdk = status_endpoint(r#"{"schemaVersion":1,"online":true}"#);
        let (_game, game_port) = silent();
        // At the build's ports.
        let plan = plan_against(Ports { sdk, game: game_port, ..build }, none, false).unwrap();
        assert_eq!(plan.server, ServerPlan::Reuse { sdk, game: game_port });
        assert_eq!(plan.mongo, MongoPlan::NotNeeded, "a running server brings its own database");
        assert_eq!(plan.sdk_port(build.sdk), sdk);
        // At the well-known ports while the build's are free.
        let plan = plan_against(build, Ports { sdk, game: game_port, ..none }, false).unwrap();
        assert_eq!(plan.server, ServerPlan::Reuse { sdk, game: game_port });
        // Status answers but the game port is dead: refuse.
        let error = plan_against(Ports { sdk, ..build }, none, false).unwrap_err().to_string();
        assert!(error.contains("game port"), "{error}");
        // Something else on the SDK port (wrong schema, plain listener): refuse.
        let other = status_endpoint(r#"{"schemaVersion":2}"#);
        assert!(plan_against(Ports { sdk: other, ..build }, none, false).unwrap_err().to_string().contains("not an AscNet server"));
        let (_listener, plain) = silent();
        assert!(plan_against(Ports { sdk: plain, ..build }, none, false).is_err());
        // A stranger on the game port while the SDK port is free.
        assert!(plan_against(Ports { game: plain, ..build }, none, false).is_err());
    }

    /// Read-only report on this machine (a `hello` and a `GET`): `cargo test --offline real_machine -- --ignored --nocapture`
    #[test]
    #[ignore]
    fn real_machine_report() {
        let build = spare_ports();
        for opt in [true, false] {
            eprintln!("use_running_mongo={opt}: {:?}", plan(build, opt));
        }
        eprintln!("mongo {}: {:?}, server {}/{}: {:?}", DEFAULT.mongo, probe_mongo(DEFAULT.mongo), DEFAULT.sdk, DEFAULT.game, probe_server(DEFAULT.sdk, DEFAULT.game));
    }
}

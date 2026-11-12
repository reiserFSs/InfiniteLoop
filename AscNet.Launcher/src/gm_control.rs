use anyhow::{bail, Context, Result};
use serde::{Deserialize, Serialize};
use std::{
    collections::HashMap,
    io::{BufRead, BufReader, Read, Write},
    process::ChildStdin,
    sync::{mpsc, Arc, Mutex},
    thread,
    time::Duration,
};
use uuid::Uuid;

const PREFIX: &[u8] = b"ASCNET-GM/1 ";
const REPLY_TIMEOUT: Duration = Duration::from_secs(15);

#[derive(Clone, Copy, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum GuideRewardPolicy {
    Preserve,
    Grant,
    Skip,
}

#[derive(Serialize)]
#[serde(tag = "operation", rename_all = "camelCase", rename_all_fields = "camelCase")]
pub enum GmRequest {
    Hello,
    ReadLevel { uid: i64 },
    SetLevel { uid: i64, level: i32 },
    Guides {
        uid: i64,
        guide_id: Option<i32>,
        group_id: Option<i32>,
        all: bool,
        reward_policy: GuideRewardPolicy,
        dry_run: bool,
        ignore_client_triggers: bool,
    },
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PlayerLevel {
    pub uid: i64,
    pub level: i64,
    pub online: bool,
    pub requires_relog: bool,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GmReply {
    id: String,
    ok: bool,
    pub code: Option<String>,
    pub message: Option<String>,
    pub player: Option<PlayerLevel>,
    pub server_version: Option<String>,
}

#[derive(Default)]
struct ReplyWaiters {
    closed: bool,
    calls: HashMap<String, mpsc::Sender<GmReply>>,
}

impl ReplyWaiters {
    fn deliver(&mut self, line: &[u8]) -> bool {
        let Some(json) = line.strip_prefix(PREFIX) else { return false };
        let Ok(reply) = serde_json::from_slice::<GmReply>(json) else { return false };
        if let Some(waiter) = self.calls.remove(&reply.id) {
            let _ = waiter.send(reply);
        }
        true
    }
}

#[derive(Clone)]
pub struct GmControl {
    input: Arc<Mutex<ChildStdin>>,
    replies: Arc<Mutex<ReplyWaiters>>,
}

impl GmControl {
    pub(crate) fn attach(input: ChildStdin, output: impl Read + Send + 'static,
        mut log: impl FnMut(&[u8]) + Send + 'static) -> Self {
        let replies = Arc::new(Mutex::new(ReplyWaiters::default()));
        let dispatch = replies.clone();
        thread::spawn(move || {
            let mut stdout = BufReader::new(output);
            let mut line = Vec::new();
            while let Ok(1..) = stdout.read_until(b'\n', &mut line) {
                if !dispatch.lock().unwrap().deliver(&line) {
                    log(&line);
                }
                line.clear();
            }
            let mut dispatch = dispatch.lock().unwrap();
            dispatch.closed = true;
            dispatch.calls.clear();
        });
        Self { input: Arc::new(Mutex::new(input)), replies }
    }

    pub fn request(&self, request: GmRequest) -> Result<GmReply> {
        self.request_with_timeout(request, REPLY_TIMEOUT)
    }

    pub(crate) fn request_with_timeout(&self, request: GmRequest, timeout: Duration) -> Result<GmReply> {
        #[derive(Serialize)]
        struct Frame<'a> {
            id: &'a str,
            #[serde(flatten)]
            request: GmRequest,
        }

        let id = Uuid::new_v4().simple().to_string();
        let mut line = PREFIX.to_vec();
        serde_json::to_writer(&mut line, &Frame { id: &id, request })?;
        line.push(b'\n');

        let (send, receive) = mpsc::channel();
        {
            let mut replies = self.replies.lock().unwrap();
            if replies.closed {
                bail!("The launcher-owned server has stopped.");
            }
            replies.calls.insert(id.clone(), send);
        }
        let result = self.write(&line).and_then(|()| match receive.recv_timeout(timeout) {
            Ok(reply) => Ok(reply),
            Err(mpsc::RecvTimeoutError::Timeout) => bail!("The server did not confirm the operation in time. Read the current state before retrying."),
            Err(mpsc::RecvTimeoutError::Disconnected) => bail!("The server stopped before confirming the operation. Read the state after starting it again."),
        });
        self.replies.lock().unwrap().calls.remove(&id);
        let reply = result?;
        if !reply.ok {
            bail!("{}", reply.message.as_deref().unwrap_or("The server declined the GM operation."));
        }
        Ok(reply)
    }

    pub(crate) fn shutdown(&self) -> Result<()> {
        self.write(b"shutdown\n").context("request AscNet server shutdown")
    }

    fn write(&self, bytes: &[u8]) -> Result<()> {
        let mut input = self.input.lock().unwrap();
        input.write_all(bytes).context("write to the launcher-owned AscNet server")?;
        input.flush().context("flush the AscNet control pipe")
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn guide_frame_uses_explicit_scope_and_policy() {
        let frame = serde_json::to_value(GmRequest::Guides {
            uid: 2, guide_id: None, group_id: Some(8), all: false,
            reward_policy: GuideRewardPolicy::Skip, dry_run: true, ignore_client_triggers: false,
        }).unwrap();
        assert_eq!(frame, serde_json::json!({
            "operation": "guides", "uid": 2, "guideId": null, "groupId": 8, "all": false,
            "rewardPolicy": "skip", "dryRun": true, "ignoreClientTriggers": false
        }));
    }

    #[test]
    fn responses_route_by_id_and_ignore_log_lines_and_late_replies() {
        let mut replies = ReplyWaiters::default();
        let (a, first) = mpsc::channel();
        let (b, second) = mpsc::channel();
        replies.calls.insert("a".into(), a);
        replies.calls.insert("b".into(), b);

        assert!(!replies.deliver(b"[INFO] Starting server\r\n"));
        assert!(replies.deliver(b"ASCNET-GM/1 {\"id\":\"expired\",\"ok\":true}\r\n"));
        assert!(replies.deliver(b"ASCNET-GM/1 {\"id\":\"b\",\"ok\":true}\r\n"));
        assert!(first.try_recv().is_err());
        assert!(second.try_recv().unwrap().ok);
        assert!(replies.deliver(b"ASCNET-GM/1 {\"id\":\"a\",\"ok\":false,\"code\":\"invalid_uid\"}\r\n"));
        assert_eq!(first.try_recv().unwrap().code.as_deref(), Some("invalid_uid"));
    }
}

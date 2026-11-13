use super::GmRequest;
use anyhow::{Context, Result};
use serde::{Deserialize, Serialize};
use std::{fs, path::{Path, PathBuf}};
use uuid::Uuid;

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Catalog {
    pub revision: String,
    pub client_version: String,
    pub stale: bool,
    pub total: usize,
    pub categories: Vec<Category>,
    pub languages: Vec<String>,
}

#[derive(Debug, Deserialize)]
pub struct Category {
    pub key: String,
    pub name: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Item {
    pub id: i32,
    pub name: Option<String>,
    pub category_name: String,
    pub item_type: i32,
    pub inventory_max: Option<i64>,
    pub bulk_max: Option<i64>,
    pub grant_kind: String,
}

impl Item {
    pub fn display_name(&self) -> &str {
        self.name.as_deref().unwrap_or("English name unavailable")
    }
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Balance {
    pub owned: i64,
    pub can_add: i64,
    pub combined_owned: Option<i64>,
}

#[derive(Debug, Deserialize)]
pub struct SearchRow {
    pub item: Item,
    pub balance: Option<Balance>,
}

#[derive(Debug, Deserialize)]
pub struct Search {
    pub uid: i64,
    pub online: bool,
    pub total: usize,
    pub offset: usize,
    pub rows: Vec<SearchRow>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Inspection {
    pub uid: i64,
    pub online: bool,
    pub connected_client_version: Option<String>,
    pub details: Details,
    pub balance: Option<Balance>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Details {
    pub item: Item,
    pub description: Option<String>,
    pub icon: Option<String>,
    pub source: String,
    pub client_version: String,
    pub subtype_parameters: Vec<i32>,
    pub weapon_fashion_id: Option<i32>,
    pub combined_ids: Vec<i32>,
    pub gift_reward_id: Option<i32>,
    pub behavior: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Grant {
    pub uid: i64,
    pub item: Item,
    pub balance: Balance,
    pub added: i32,
    pub balance_after_grant: i64,
    pub replay: bool,
    pub online: bool,
    pub update_queued: bool,
}

/// Persists an ambiguous operation across panel, game and launcher restarts.
/// This contains no credentials. The local server still checks its own receipt and UID.
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct PendingGrant {
    pub uid: i64,
    pub item_id: i32,
    pub quantity: i32,
    pub grant_id: String,
}

impl PendingGrant {
    pub fn new(uid: i64, item_id: i32, quantity: i32) -> Self {
        Self { uid, item_id, quantity, grant_id: Uuid::new_v4().simple().to_string() }
    }

    pub fn request(&self, resolve_only: bool) -> GmRequest {
        let Self { uid, item_id, quantity, grant_id } = self.clone();
        if resolve_only {
            GmRequest::ItemResolve { uid, item_id, quantity, grant_id }
        } else {
            GmRequest::ItemAdd { uid, item_id, quantity, grant_id }
        }
    }

    pub fn load(path: &Path) -> Result<Option<Self>> {
        match fs::read(path) {
            Ok(bytes) => Ok(Some(serde_json::from_slice(&bytes).context("read pending item grant")?)),
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(None),
            Err(error) => Err(error).context("open pending item grant"),
        }
    }

    pub fn save(&self, path: &Path) -> Result<()> {
        let temporary = path.with_extension("pending");
        fs::write(&temporary, serde_json::to_vec(self)?).context("record pending item grant")?;
        crate::local::atomic_replace(&temporary, path)
    }

    pub fn path() -> Result<PathBuf> {
        Ok(crate::local::root()?.join("gm-item-pending.json"))
    }
}

pub fn copy_command(item_id: i32, quantity: i32) -> String {
    format!("/item add {item_id} {quantity}")
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn retries_reuse_the_grant_id_and_keep_correlation_independent() {
        let pending = PendingGrant::new(2, 47, 200);
        let add = serde_json::to_value(pending.request(false)).unwrap();
        let resolve = serde_json::to_value(pending.request(true)).unwrap();
        assert_eq!(add["grantId"], resolve["grantId"]);
        assert_eq!(add["itemId"], 47);
        assert_eq!(add["uid"], 2);
        assert_eq!(add["operation"], "itemAdd");
        assert_eq!(resolve["operation"], "itemResolve");
        assert_eq!(copy_command(47, 200), "/item add 47 200");
    }
}

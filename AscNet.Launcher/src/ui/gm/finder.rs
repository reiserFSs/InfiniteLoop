use super::*;
use ascnet_launcher::gm_control::{
    items::{self, Balance, Catalog, Grant, Inspection, Item, PendingGrant, Search},
    GmRejected,
};
use std::{fs, path::Path};
use windows::Win32::{
    System::{
        DataExchange::{CloseClipboard, EmptyClipboard, OpenClipboard, SetClipboardData},
        Memory::{GlobalAlloc, GlobalLock, GlobalUnlock, GMEM_MOVEABLE},
        Ole::CF_UNICODETEXT,
        SystemServices::SS_BITMAP,
    },
    UI::Shell::{IShellItemImageFactory, SHCreateItemFromParsingName, SIIGBF_THUMBNAILONLY},
};

pub(super) const SEARCH: i32 = 461;
pub(super) const CATEGORY: i32 = 462;
pub(super) const COUNT: i32 = 463;
pub(super) const LIST: i32 = 464;
pub(super) const QUANTITY: i32 = 465;
pub(super) const ADD: i32 = 466;
pub(super) const STATUS: i32 = 467;
const PREVIOUS: i32 = 468;
const NEXT: i32 = 469;
const ITEM_NAME: i32 = 470;
const DESCRIPTION: i32 = 471;
const DETAILS: i32 = 472;
const BALANCE: i32 = 473;
const MAX: i32 = 474;
const COPY_ID: i32 = 475;
const COPY_COMMAND: i32 = 476;
const REFRESH: i32 = 477;
const RESOLVE: i32 = 478;
const ICON: i32 = 479;

pub(super) const CONTROLS: &[i32] = &[SEARCH, CATEGORY, COUNT, LIST, QUANTITY, ADD, STATUS, PREVIOUS, NEXT, ITEM_NAME,
    DESCRIPTION, DETAILS, BALANCE, MAX, COPY_ID, COPY_COMMAND, REFRESH, RESOLVE, ICON, 481, 482];

pub(super) fn in_card(id: i32) -> bool {
    matches!(id, ITEM_NAME | DESCRIPTION | DETAILS | BALANCE | 482)
}

#[derive(Debug, Clone, PartialEq)]
pub(super) struct Query {
    uid: i64,
    text: String,
    category: Option<String>,
    offset: usize,
}

pub(super) struct Finder {
    game: Option<PathBuf>,
    catalog: Option<Catalog>,
    results: Option<Search>,
    rendered_query: Option<Query>,
    inspection: Option<Inspection>,
    pending: Option<PendingGrant>,
    pending_path: Result<PathBuf, String>,
    pending_error: Option<String>,
    icon: HBITMAP,
    pub search_waiting: bool,
    pub catalog_checked: bool,
}

impl Finder {
    pub(super) fn new(game: Option<PathBuf>) -> Self {
        let pending_path = PendingGrant::path().map_err(|error| format!("{error:#}"));
        let (pending, pending_error) = match &pending_path {
            Ok(path) => match PendingGrant::load(path) {
                Ok(pending) => (pending, None),
                Err(error) => (None, Some(format!("{error:#}"))),
            },
            Err(error) => (None, Some(error.clone())),
        };
        Self {
            game, catalog: None, results: None, rendered_query: None, inspection: None,
            pending, pending_path, pending_error, icon: HBITMAP(0), search_waiting: false, catalog_checked: false,
        }
    }

    pub(super) fn has_catalog(&self) -> bool { self.catalog.is_some() }

    unsafe fn query(&self, hwnd: HWND, offset: usize) -> Result<Query> {
        let index = selected(hwnd, CATEGORY);
        let category = self.catalog.as_ref().and_then(|catalog| catalog.categories.get((index - 1) as usize))
            .map(|category| category.key.clone());
        Ok(Query { uid: uid(hwnd)?, text: super::super::get_text(hwnd, SEARCH)?, category, offset })
    }

    pub(super) unsafe fn search(&mut self, hwnd: HWND, offset: usize) -> Option<(Job, GmRequest)> {
        self.search_waiting = false;
        self.catalog.as_ref()?;
        match self.query(hwnd, offset) {
            Ok(query) => {
                self.status(hwnd, "Searching the item catalog…");
                let request = GmRequest::ItemSearch {
                    uid: query.uid, query: query.text.clone(), category: query.category.clone(), offset: query.offset,
                };
                Some((Job::Search(query), request))
            }
            Err(error) => {
                self.status(hwnd, &error.to_string());
                self.update_actions(hwnd, false);
                None
            }
        }
    }

    pub(super) unsafe fn selection(&mut self, hwnd: HWND) -> Option<(Job, GmRequest)> {
        let result = self.results.as_ref()?;
        if self.rendered_query.as_ref() != self.query(hwnd, result.offset).ok().as_ref() { return None; }
        let Some(row) = selected_row(hwnd).and_then(|index| result.rows.get(index)) else {
            if self.inspection.take().is_some() { self.show_details(hwnd); }
            return None;
        };
        let item_id = row.item.id;
        let uid = result.uid;
        if self.inspection.as_ref().is_some_and(|item| item.uid == uid && item.details.item.id == item_id) {
            return None;
        }
        self.inspection = None;
        self.show_details(hwnd);
        set_text(hwnd, QUANTITY, "1");
        Some((Job::Inspect(uid, item_id), GmRequest::ItemInspect { uid, item_id }))
    }

    pub(super) unsafe fn command(&mut self, hwnd: HWND, id: i32) -> Result<Option<(Job, GmRequest)>> {
        match id {
            PREVIOUS | NEXT => {
                let Some(result) = &self.results else { return Ok(None) };
                let offset = if id == PREVIOUS { result.offset.saturating_sub(50) } else { result.offset + result.rows.len() };
                return Ok(self.search(hwnd, offset));
            }
            RESOLVE => {
                if let Some(pending) = &self.pending {
                    self.status(hwnd, "Checking the original grant receipt…");
                    return Ok(Some((Job::Grant { resolve: true }, pending.request(true))));
                }
            }
            ADD if self.pending.is_some() => {
                let pending = self.pending.as_ref().unwrap();
                self.status(hwnd, "Retrying the original grant ID…");
                return Ok(Some((Job::Grant { resolve: false }, pending.request(false))));
            }
            _ => {}
        }

        let Some(inspection) = self.selected_inspection(hwnd) else { return Ok(None) };
        let uid = inspection.uid;
        let item_id = inspection.details.item.id;
        match id {
            REFRESH => {
                self.status(hwnd, "Reading current inventory…");
                return Ok(Some((Job::Inspect(uid, item_id), GmRequest::ItemInspect { uid, item_id })));
            }
            COPY_ID => {
                copy(hwnd, &item_id.to_string())?;
                self.status(hwnd, &format!("Copied item ID {item_id}."));
            }
            COPY_COMMAND => {
                let quantity = quantity(hwnd)?;
                copy(hwnd, &items::copy_command(item_id, quantity))?;
                self.status(hwnd, "Copied the item command.");
            }
            MAX => {
                if let Some(balance) = &inspection.balance {
                    set_text(hwnd, QUANTITY, &balance.can_add.to_string());
                }
            }
            ADD => {
                let quantity = quantity(hwnd)?;
                anyhow::ensure!(self.can_grant(inspection, i64::from(quantity)), "Select an online player and enter an amount within the available capacity.");
                let pending = PendingGrant::new(uid, item_id, quantity);
                pending.save(self.pending_path.as_ref().map_err(|error| anyhow::anyhow!("{error}"))?)?;
                let request = pending.request(false);
                self.pending = Some(pending);
                self.status(hwnd, "Saving the item grant…");
                return Ok(Some((Job::Grant { resolve: false }, request)));
            }
            _ => {}
        }
        self.update_actions(hwnd, false);
        Ok(None)
    }

    pub(super) unsafe fn finish(&mut self, hwnd: HWND, job: Job, result: Result<GmReply>) -> Option<(Job, GmRequest)> {
        if matches!(job, Job::Catalog) { self.catalog_checked = true; }
        let reply = match result {
            Ok(reply) => reply,
            Err(error) => {
                if let Job::Grant { resolve } = job {
                    if !resolve && error.downcast_ref::<GmRejected>().is_some_and(|error| matches!(error.code.as_str(),
                        "invalid_uid" | "invalid_item" | "invalid_quantity" | "invalid_grant_id" |
                        "grant_id_conflict" | "unsupported_item" | "inventory_limit" | "ambiguous_inventory" | "catalog_stale"))
                    {
                        self.clear_pending();
                    }
                }
                self.status(hwnd, &format!("{error:#}"));
                return None;
            }
        };
        match job {
            Job::Catalog => {
                let Some(catalog) = reply.catalog else {
                    self.status(hwnd, "Start the matching Item Finder server build.");
                    return None;
                };
                let combo = GetDlgItem(hwnd, CATEGORY);
                let _ = SendMessageW(combo, CB_RESETCONTENT, WPARAM(0), LPARAM(0));
                let mut labels = vec!["All categories"];
                labels.extend(catalog.categories.iter().map(|category| category.name.as_str()));
                fill_combo(combo, &labels);
                self.catalog = Some(catalog);
                if let Some(pending) = &self.pending {
                    set_text(hwnd, UID, &pending.uid.to_string());
                    set_text(hwnd, SEARCH, &pending.item_id.to_string());
                    set_text(hwnd, QUANTITY, &pending.quantity.to_string());
                    self.status(hwnd, "Checking the grant saved by the previous launcher session…");
                    self.search_waiting = true;
                    return Some((Job::Grant { resolve: true }, pending.request(true)));
                }
                self.search(hwnd, 0)
            }
            Job::Search(query) => {
                let Some(results) = reply.search else { return None };
                if self.query(hwnd, query.offset).ok().as_ref() != Some(&query) {
                    return None;
                }
                let selected = self.inspection.as_ref().filter(|item| item.uid == results.uid).map(|item| item.details.item.id);
                self.rendered_query = Some(query);
                self.results = Some(results);
                self.inspection = None;
                self.show_details(hwnd);
                self.render_rows(hwnd, selected);
                let results = self.results.as_ref().unwrap();
                let message = if self.pending.is_some() {
                    "A grant is awaiting confirmation. Check its receipt or retry the same grant."
                } else if self.catalog.as_ref().is_some_and(|catalog| catalog.stale) {
                    "The catalog changed on disk. Restart the launcher-managed server."
                } else if !results.online {
                    "Player offline — log into PGR to add items."
                } else if results.rows.is_empty() {
                    "No items match the current search."
                } else {
                    "Select a result to inspect its live inventory."
                };
                self.status(hwnd, message);
                None
            }
            Job::Inspect(uid, item_id) => {
                if self.results.as_ref().is_some_and(|results| results.uid == uid
                    && selected_row(hwnd).and_then(|index| results.rows.get(index)).is_some_and(|row| row.item.id == item_id)) {
                    self.inspection = reply.item;
                    self.show_details(hwnd);
                    self.refresh_row(hwnd);
                    if let Some(item) = &self.inspection {
                        if !item.online { self.status(hwnd, "Player offline — log into PGR to add items."); }
                        else if item.details.item.grant_kind == "unavailable" { self.status(hwnd, &item.details.behavior); }
                        else if item.balance.as_ref().is_some_and(|balance| balance.can_add == 0) {
                            self.status(hwnd, "Inventory limit reached.");
                        } else { self.status(hwnd, "Live inventory refreshed."); }
                    }
                }
                None
            }
            Job::Grant { .. } => {
                if let Some(grant) = reply.grant {
                    self.clear_pending();
                    self.apply_grant(hwnd, grant);
                } else {
                    self.status(hwnd, "The server did not return an item grant result. Check the original receipt.");
                }
                None
            }
            Job::Level | Job::Guides => None,
        }
    }

    fn clear_pending(&mut self) {
        if let Ok(path) = &self.pending_path {
            if let Err(error) = fs::remove_file(path) {
                if error.kind() != std::io::ErrorKind::NotFound {
                    self.pending_error = Some(format!("Could not clear the original grant record. {error}"));
                    return;
                }
            }
        }
        self.pending = None;
    }

    unsafe fn apply_grant(&mut self, hwnd: HWND, grant: Grant) {
        let delivery = if grant.update_queued { " Update sent to the live game session." }
            else if !grant.online { " Player offline." } else { "" };
        let summary = if grant.item.grant_kind == "weaponCoating" {
            format!("{} unlocked for UID {}.{}", grant.item.display_name(), grant.uid, delivery)
        } else if grant.replay {
            format!("Confirmed original grant of {} × {} to UID {}. Balance after grant {}. Current balance {}.{}",
                number(i64::from(grant.added)), grant.item.display_name(), grant.uid, number(grant.balance_after_grant),
                number(grant.balance.owned), delivery)
        } else {
            format!("Added {} × {} to UID {}. New balance {}.{}",
                number(i64::from(grant.added)), grant.item.display_name(), grant.uid, number(grant.balance.owned), delivery)
        };
        if let Some(inspection) = self.inspection.as_mut().filter(|item| item.uid == grant.uid && item.details.item.id == grant.item.id) {
            inspection.online = grant.online;
            inspection.balance = Some(grant.balance);
            self.show_details(hwnd);
            self.refresh_row(hwnd);
        }
        self.status(hwnd, &summary);
    }

    unsafe fn selected_inspection(&self, hwnd: HWND) -> Option<&Inspection> {
        let results = self.results.as_ref()?;
        if self.rendered_query.as_ref() != self.query(hwnd, results.offset).ok().as_ref() { return None; }
        let item_id = results.rows.get(selected_row(hwnd)?)?.item.id;
        self.inspection.as_ref().filter(|inspection| inspection.uid == results.uid && inspection.details.item.id == item_id)
    }

    fn can_grant(&self, inspection: &Inspection, quantity: i64) -> bool {
        self.pending.is_none() && self.pending_error.is_none() && self.catalog.as_ref().is_some_and(|catalog| !catalog.stale)
            && inspection.online && inspection.balance.as_ref().is_some_and(|balance| quantity > 0 && quantity <= balance.can_add)
    }

    pub(super) unsafe fn update_actions(&self, hwnd: HWND, busy: bool) {
        let selected = self.selected_inspection(hwnd);
        let amount = quantity(hwnd).ok();
        let pending = self.pending.is_some();
        let ready = !busy && !pending;
        set_text(hwnd, ADD, if pending { "RETRY SAME GRANT" } else { "+  ADD ITEM" });
        enabled(hwnd, ADD, !busy && (pending || selected.zip(amount).is_some_and(|(item, count)| self.can_grant(item, count.into()))));
        enabled(hwnd, MAX, ready && selected.is_some_and(|item| self.can_grant(item, 1)));
        enabled(hwnd, QUANTITY, ready && selected.is_some());
        enabled(hwnd, COPY_ID, !busy && selected.is_some());
        enabled(hwnd, COPY_COMMAND, ready && selected.zip(amount).is_some_and(|(item, amount)|
            item.details.source.ends_with("/item/Item.tsv") && item.details.item.grant_kind != "unavailable"
                && item.details.item.inventory_max.is_some_and(|max| i64::from(amount) <= max)));
        enabled(hwnd, REFRESH, !busy && selected.is_some());
        enabled(hwnd, RESOLVE, !busy && pending);
        enabled(hwnd, PREVIOUS, !busy && self.results.as_ref().is_some_and(|result| result.offset > 0));
        enabled(hwnd, NEXT, !busy && self.results.as_ref().is_some_and(|result| result.offset + result.rows.len() < result.total));
    }

    pub(super) unsafe fn status(&self, hwnd: HWND, text: &str) {
        set_text(hwnd, STATUS, self.pending_error.as_deref().unwrap_or(text));
    }

    unsafe fn render_rows(&self, hwnd: HWND, restore: Option<i32>) {
        let results = self.results.as_ref().unwrap();
        let catalog = self.catalog.as_ref().unwrap();
        let first = if results.rows.is_empty() { 0 } else { results.offset + 1 };
        set_text(hwnd, COUNT, &format!("{} results  ·  {}–{}  ·  {} catalog items  ·  {} {}",
            results.total, first, results.offset + results.rows.len(), catalog.total,
            catalog.languages.join("/").to_uppercase(), catalog.client_version));
        let list = GetDlgItem(hwnd, LIST);
        let _ = SendMessageW(list, WM_SETREDRAW, WPARAM(0), LPARAM(0));
        let _ = SendMessageW(list, LVM_DELETEALLITEMS, WPARAM(0), LPARAM(0));
        for (index, row) in results.rows.iter().enumerate() {
            let mut text = super::super::wide(row.item.display_name());
            let item = LVITEMW { mask: LVIF_TEXT, iItem: index as i32, pszText: PWSTR(text.as_mut_ptr()), ..Default::default() };
            let _ = SendMessageW(list, LVM_INSERTITEMW, WPARAM(0), LPARAM((&item as *const LVITEMW) as isize));
            set_row(list, index, &row.item, row.balance.as_ref());
        }
        if !results.rows.is_empty() {
            let index = results.rows.iter().position(|row| Some(row.item.id) == restore).unwrap_or(0);
            let selection = LVITEMW { state: LIST_VIEW_ITEM_STATE_FLAGS(LVIS_FOCUSED.0 | LVIS_SELECTED.0),
                stateMask: LIST_VIEW_ITEM_STATE_FLAGS(LVIS_FOCUSED.0 | LVIS_SELECTED.0), ..Default::default() };
            let _ = SendMessageW(list, LVM_SETITEMSTATE, WPARAM(index), LPARAM((&selection as *const LVITEMW) as isize));
        }
        let _ = SendMessageW(list, WM_SETREDRAW, WPARAM(1), LPARAM(0));
        let _ = InvalidateRect(list, None, true);
    }

    unsafe fn refresh_row(&self, hwnd: HWND) {
        if let Some((index, item)) = selected_row(hwnd).zip(self.inspection.as_ref()) {
            set_row(GetDlgItem(hwnd, LIST), index, &item.details.item, item.balance.as_ref());
        }
    }

    unsafe fn show_details(&mut self, hwnd: HWND) {
        let inspection = self.inspection.as_ref();
        let details = inspection.map(|item| &item.details);
        set_text(hwnd, ITEM_NAME, details.map_or("Select an item", |detail| detail.item.display_name()));
        set_text(hwnd, DESCRIPTION, details.and_then(|detail| detail.description.as_deref()).unwrap_or(""));
        set_text(hwnd, BALANCE, &inspection.map_or_else(String::new, |inspection| {
            let item = &inspection.details.item;
            let owned = inspection.balance.as_ref().map(|balance| balance.owned);
            let capacity = inspection.balance.as_ref().map(|balance| balance.can_add);
            format!("Max  {}      Owned  {}      Can add  {}      Bulk cap  {}", optional(item.inventory_max),
                optional(owned), optional(capacity), optional(item.bulk_max))
        }));
        let info = details.map_or_else(String::new, |detail| {
            let connected = inspection.and_then(|item| item.connected_client_version.as_deref()).unwrap_or("—");
            let revision = self.catalog.as_ref().map_or("—", |catalog| catalog.revision.get(..12).unwrap_or(&catalog.revision));
            let mut info = format!("{}  ·  ID {}  ·  Type {}  ·  Subtype {:?}\r\n{}\r\n{}  ·  EN {}  ·  Catalog {}  ·  Client {}",
                detail.item.category_name, detail.item.id, detail.item.item_type, detail.subtype_parameters,
                detail.behavior, detail.source, detail.client_version, revision, connected);
            if let Some(fashion) = detail.weapon_fashion_id { info.push_str(&format!("\r\nWeapon fashion ID {fashion}")); }
            if let Some(reward) = detail.gift_reward_id { info.push_str(&format!("\r\nGift reward configuration {reward}")); }
            if detail.combined_ids.len() > 1 {
                let balance = inspection.and_then(|item| item.balance.as_ref()).and_then(|balance| balance.combined_owned);
                info.push_str(&format!("\r\nCombined balance {}  ·  Physical IDs {:?}", optional(balance), detail.combined_ids));
            }
            if let Some(icon) = &detail.icon { info.push_str(&format!("\r\nIcon {icon}")); }
            info
        });
        set_text(hwnd, DETAILS, &info);
        let bitmap = details.and_then(|detail| self.local_icon(detail.icon.as_deref()?)).unwrap_or(HBITMAP(0));
        let _ = SendMessageW(GetDlgItem(hwnd, ICON), STM_SETIMAGE, WPARAM(IMAGE_BITMAP.0 as usize), LPARAM(bitmap.0));
        if self.icon.0 != 0 { let _ = DeleteObject(self.icon); }
        self.icon = bitmap;
    }

    unsafe fn local_icon(&self, asset: &str) -> Option<HBITMAP> {
        let game = self.game.as_ref()?;
        let asset = Path::new(asset);
        if !asset.components().all(|part| matches!(part, std::path::Component::Normal(_))) { return None; }
        let file = [game.join(asset), game.join("PGR_Data/StreamingAssets").join(asset)].into_iter().find(|file| file.is_file())?;
        use std::os::windows::ffi::OsStrExt;
        let wide: Vec<u16> = file.as_os_str().encode_wide().chain([0]).collect();
        let shell: IShellItemImageFactory = SHCreateItemFromParsingName(PCWSTR(wide.as_ptr()), None).ok()?;
        shell.GetImage(SIZE { cx: 40, cy: 40 }, SIIGBF_THUMBNAILONLY).ok()
    }
}

impl Drop for Finder {
    fn drop(&mut self) {
        if self.icon.0 != 0 { unsafe { let _ = DeleteObject(self.icon); } }
    }
}

pub(super) unsafe fn schedule_search(hwnd: HWND) {
    let _ = SetTimer(hwnd, TIMER_SEARCH, 240, None);
}

pub(super) unsafe fn create_controls(hwnd: HWND) {
    label(hwnd, 481, "Search items", theme::Font::Label, 25, 124, 360, 24);
    let search = edit(hwnd, SEARCH, "", false, 24, 150, 540, 33);
    let _ = SendMessageW(search, EM_LIMITTEXT, WPARAM(120), LPARAM(0));
    let _ = SendMessageW(search, EM_SETCUEBANNER, WPARAM(0), LPARAM(w!("Search item name or ID…").0 as isize));
    let filter = combo(hwnd, CATEGORY, 581, 150, 239, 255);
    fill_combo(filter, &["All categories"]);
    label(hwnd, COUNT, "Loading the item catalog…", theme::Font::Body, 25, 190, 618, 23);
    button(hwnd, PREVIOUS, "PREV", 670, 188, 67, 27);
    button(hwnd, NEXT, "NEXT", 743, 188, 77, 27);
    let list = child(hwnd, WC_LISTVIEWW, "", WS_TABSTOP | WS_BORDER | WINDOW_STYLE(LVS_REPORT | LVS_SINGLESEL | LVS_SHOWSELALWAYS),
        LIST, theme::Font::Body, 24, 220, 796, 180);
    let _ = SendMessageW(list, LVM_SETEXTENDEDLISTVIEWSTYLE, WPARAM(0), LPARAM((LVS_EX_FULLROWSELECT | LVS_EX_DOUBLEBUFFER) as isize));
    for (index, (name, width)) in [("Item name", 252), ("ID", 75), ("Category", 148), ("Max", 99), ("Owned", 94), ("Can add", 103)].into_iter().enumerate() {
        let mut name = super::super::wide(name);
        let column = LVCOLUMNW { mask: LVCF_TEXT | LVCF_WIDTH | LVCF_FMT, pszText: PWSTR(name.as_mut_ptr()), cx: width,
            fmt: if index >= 3 { LVCFMT_RIGHT } else { LVCFMT_LEFT }, ..Default::default() };
        let _ = SendMessageW(list, LVM_INSERTCOLUMNW, WPARAM(index), LPARAM((&column as *const LVCOLUMNW) as isize));
    }
    child(hwnd, w!("STATIC"), "", WINDOW_STYLE(SS_BITMAP.0), ICON, theme::Font::Body, 42, 427, 40, 40);
    label(hwnd, ITEM_NAME, "Select an item", theme::Font::Heading, 94, 428, 699, 30);
    label(hwnd, DESCRIPTION, "", theme::Font::Body, 94, 461, 697, 42);
    label(hwnd, BALANCE, "", theme::Font::Label, 44, 500, 746, 24);
    multiline(hwnd, DETAILS, "", 44, 528, 749, 55);
    label(hwnd, 482, "Quantity", theme::Font::Body, 44, 601, 74, 25);
    edit(hwnd, QUANTITY, "1", true, 120, 595, 92, 34);
    button(hwnd, MAX, "MAX", 218, 595, 57, 34);
    button(hwnd, ADD, "+  ADD ITEM", 283, 595, 161, 34);
    button(hwnd, COPY_ID, "COPY ID", 453, 595, 88, 34);
    button(hwnd, COPY_COMMAND, "COPY COMMAND", 549, 595, 140, 34);
    button(hwnd, REFRESH, "REFRESH", 697, 595, 97, 34);
    label(hwnd, STATUS, "", theme::Font::Body, 25, 651, 655, 26);
    button(hwnd, RESOLVE, "CHECK GRANT", 689, 648, 131, 27);
}

unsafe fn set_row(list: HWND, index: usize, item: &Item, balance: Option<&Balance>) {
    for (offset, value) in [item.id.to_string(), item.category_name.clone(), optional(item.inventory_max),
        optional(balance.map(|value| value.owned)), optional(balance.map(|value| value.can_add))].into_iter().enumerate()
    {
        let mut text = super::super::wide(&value);
        let item = LVITEMW { iSubItem: offset as i32 + 1, pszText: PWSTR(text.as_mut_ptr()), ..Default::default() };
        let _ = SendMessageW(list, LVM_SETITEMTEXTW, WPARAM(index), LPARAM((&item as *const LVITEMW) as isize));
    }
}

unsafe fn selected_row(hwnd: HWND) -> Option<usize> {
    let index = SendMessageW(GetDlgItem(hwnd, LIST), LVM_GETNEXTITEM, WPARAM(usize::MAX), LPARAM(LVNI_SELECTED as isize)).0;
    usize::try_from(index).ok()
}

unsafe fn quantity(hwnd: HWND) -> Result<i32> {
    let amount: i32 = super::super::get_text(hwnd, QUANTITY)?.trim().parse().context("Enter a positive whole quantity.")?;
    anyhow::ensure!(amount > 0, "Enter a positive whole quantity.");
    Ok(amount)
}

fn optional(value: Option<i64>) -> String { value.map_or_else(|| "—".into(), number) }

fn number(value: i64) -> String {
    let value = value.to_string();
    let mut grouped = String::new();
    for (index, digit) in value.chars().enumerate() {
        if index > 0 && (value.len() - index) % 3 == 0 && grouped != "-" { grouped.push(','); }
        grouped.push(digit);
    }
    grouped
}

unsafe fn copy(hwnd: HWND, text: &str) -> Result<()> {
    let wide = super::super::wide(text);
    OpenClipboard(hwnd).context("Open Windows clipboard")?;
    let result = (|| -> Result<()> {
        let memory = GlobalAlloc(GMEM_MOVEABLE, std::mem::size_of_val(wide.as_slice()))?;
        let pointer = GlobalLock(memory).cast::<u16>();
        if pointer.is_null() {
            let _ = GlobalFree(memory);
            return Err(windows::core::Error::from_win32().into());
        }
        std::ptr::copy_nonoverlapping(wide.as_ptr(), pointer, wide.len());
        let _ = GlobalUnlock(memory);
        if let Err(error) = EmptyClipboard().and_then(|()| SetClipboardData(CF_UNICODETEXT.0 as u32, HANDLE(memory.0 as isize))) {
            let _ = GlobalFree(memory);
            return Err(error.into());
        }
        Ok(())
    })();
    let _ = CloseClipboard();
    result
}

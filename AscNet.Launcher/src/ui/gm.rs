use anyhow::{bail, Context, Result};
use ascnet_launcher::gm_control::{GmControl, GmReply, GmRequest, GuideRewardPolicy};
use std::{path::PathBuf, sync::mpsc, thread};
use windows::{
    core::{w, PCWSTR, PWSTR},
    Win32::{
        Foundation::*,
        Graphics::Gdi::*,
        System::LibraryLoader::GetModuleHandleW,
        UI::{Controls::*, Input::KeyboardAndMouse::SetFocus, WindowsAndMessaging::*},
    },
};

mod finder;
mod theme;

const CLASS: PCWSTR = w!("AscNetLauncherGmTools");
const WM_RESULT: u32 = WM_APP + 1;
const WM_ITEM_SELECTION: u32 = WM_APP + 2;
const TIMER_SEARCH: usize = 1;
const WIDTH: i32 = 844;
const HEIGHT: i32 = 680;

const UID: i32 = 401;
const TAB_PLAYER: i32 = 402;
const TAB_GUIDES: i32 = 403;
const TAB_ITEMS: i32 = 404;
const LEVEL: i32 = 411;
const READ: i32 = 412;
const SET: i32 = 413;
const PLAYER_STATUS: i32 = 414;
const SCOPE: i32 = 421;
const GUIDE_ID: i32 = 422;
const POLICY: i32 = 423;
const IGNORE_CLIENT: i32 = 424;
const PREVIEW: i32 = 425;
const COMPLETE: i32 = 426;
const GUIDE_STATUS: i32 = 427;

const PLAYER: &[i32] = &[LEVEL, READ, SET, PLAYER_STATUS, 451, 452];
const GUIDES: &[i32] = &[SCOPE, GUIDE_ID, POLICY, IGNORE_CLIENT, PREVIEW, COMPLETE, GUIDE_STATUS, 453, 454, 455, 456];
const PROGRESSION_ACTIONS: &[i32] = &[READ, SET, PREVIEW, COMPLETE];

#[derive(Clone, Copy, PartialEq, Eq)]
enum Tab { Player, Guides, Items }

struct Panel {
    control: GmControl,
    send: mpsc::Sender<(Job, Result<GmReply>)>,
    receive: mpsc::Receiver<(Job, Result<GmReply>)>,
    busy: bool,
    tab: Tab,
    finder: finder::Finder,
    uid: i64,
}

enum Job {
    Level,
    Guides,
    Catalog,
    Search(finder::Query),
    Inspect(i64, i32),
    Grant { resolve: bool },
}

pub(super) unsafe fn open(owner: HWND, control: GmControl, uid: i64, game: Option<PathBuf>) -> Result<HWND> {
    let instance = HINSTANCE(GetModuleHandleW(None)?.0);
    let class = WNDCLASSW {
        lpfnWndProc: Some(window_proc),
        hInstance: instance,
        hCursor: LoadCursorW(None, IDC_ARROW)?,
        hbrBackground: theme::palette().canvas,
        lpszClassName: CLASS,
        ..Default::default()
    };
    if RegisterClassW(&class) == 0 && GetLastError() != ERROR_CLASS_ALREADY_EXISTS {
        return Err(windows::core::Error::from_win32().into());
    }
    let _ = InitCommonControlsEx(&INITCOMMONCONTROLSEX {
        dwSize: std::mem::size_of::<INITCOMMONCONTROLSEX>() as u32,
        dwICC: ICC_LISTVIEW_CLASSES,
    });
    let (send, receive) = mpsc::channel();
    let mut panel = Some(Panel { control, send, receive, busy: false, tab: Tab::Player, finder: finder::Finder::new(game), uid });
    let mut owner_rect = RECT::default();
    GetWindowRect(owner, &mut owner_rect)?;
    let style = WS_POPUP | WS_CAPTION | WS_SYSMENU;
    let extended = WS_EX_CONTROLPARENT;
    let mut rect = RECT { left: 0, top: 0, right: WIDTH, bottom: HEIGHT };
    AdjustWindowRectEx(&mut rect, style, false, extended)?;
    let width = rect.right - rect.left;
    let height = rect.bottom - rect.top;
    let mut monitor = MONITORINFO { cbSize: std::mem::size_of::<MONITORINFO>() as u32, ..Default::default() };
    let bounds = if GetMonitorInfoW(MonitorFromWindow(owner, MONITOR_DEFAULTTONEAREST), &mut monitor).as_bool() {
        monitor.rcWork
    } else {
        owner_rect
    };
    let x = (owner_rect.left + (owner_rect.right - owner_rect.left - width) / 2)
        .clamp(bounds.left, (bounds.right - width).max(bounds.left));
    let y = (owner_rect.top + (owner_rect.bottom - owner_rect.top - height) / 2)
        .clamp(bounds.top, (bounds.bottom - height).max(bounds.top));
    let hwnd = CreateWindowExW(extended, CLASS, w!("AscNet GM Tools"), style, x, y, width, height, owner, None,
        instance, Some((&mut panel as *mut Option<Panel>).cast()));
    if hwnd.0 == 0 {
        return Err(windows::core::Error::from_win32().into());
    }
    let _ = ShowWindow(hwnd, SW_SHOW);
    let _ = SetForegroundWindow(hwnd);
    let _ = SetFocus(GetDlgItem(hwnd, LEVEL));
    Ok(hwnd)
}

unsafe extern "system" fn window_proc(hwnd: HWND, message: u32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
    if message == WM_NCCREATE {
        let create = &*(lparam.0 as *const CREATESTRUCTW);
        let panel = &mut *create.lpCreateParams.cast::<Option<Panel>>();
        if let Some(panel) = panel.take() {
            SetWindowLongPtrW(hwnd, GWLP_USERDATA, Box::into_raw(Box::new(panel)) as isize);
            return LRESULT(1);
        }
        return LRESULT(0);
    }
    let ptr = GetWindowLongPtrW(hwnd, GWLP_USERDATA) as *mut Panel;
    if ptr.is_null() {
        return DefWindowProcW(hwnd, message, wparam, lparam);
    }
    match message {
        WM_CREATE => {
            create_controls(hwnd, (*ptr).uid);
            (*ptr).show_tab(hwnd, Tab::Player);
        }
        WM_PAINT => {
            theme::paint(hwnd);
        }
        WM_CTLCOLORSTATIC | WM_CTLCOLOREDIT => {
            return theme::control_color(hwnd, HDC(wparam.0 as isize), HWND(lparam.0), message);
        }
        WM_DRAWITEM => {
            theme::draw_button(&*(lparam.0 as *const DRAWITEMSTRUCT));
            return LRESULT(1);
        }
        WM_COMMAND => {
            let id = (wparam.0 & 0xffff) as i32;
            let notification = (wparam.0 >> 16) as u32;
            if notification == EN_CHANGE && matches!(id, UID | finder::SEARCH) || notification == CBN_SELCHANGE && id == finder::CATEGORY {
                finder::schedule_search(hwnd);
            } else if notification == EN_CHANGE && id == finder::QUANTITY {
                let _ = PostMessageW(hwnd, WM_ITEM_SELECTION, WPARAM(1), LPARAM(0));
            } else if id == SCOPE && notification == CBN_SELCHANGE {
                enabled(hwnd, GUIDE_ID, selected(hwnd, SCOPE) != 2);
            } else if notification == BN_CLICKED as u32 {
                (*ptr).command(hwnd, id);
            }
        }
        WM_NOTIFY => {
            let header = &*(lparam.0 as *const NMHDR);
            if header.idFrom == finder::LIST as usize && header.code == LVN_ITEMCHANGED {
                let event = &*(lparam.0 as *const NMLISTVIEW);
                if (event.uNewState ^ event.uOldState) & LVIS_SELECTED.0 != 0 {
                    let _ = PostMessageW(hwnd, WM_ITEM_SELECTION, WPARAM(0), LPARAM(0));
                }
            }
        }
        WM_TIMER if wparam.0 == TIMER_SEARCH => {
            let _ = KillTimer(hwnd, TIMER_SEARCH);
            let _ = remember_uid(hwnd);
            (*ptr).item_search(hwnd);
        }
        WM_ITEM_SELECTION => {
            if wparam.0 == 0 {
                (*ptr).item_selection(hwnd);
            } else {
                (*ptr).finder.update_actions(hwnd, (*ptr).busy);
            }
        }
        WM_RESULT => {
            if let Ok((job, result)) = (*ptr).receive.try_recv() {
                (*ptr).finish(hwnd, job, result);
            }
        }
        WM_CLOSE => { let _ = DestroyWindow(hwnd); }
        WM_NCDESTROY => {
            let _ = KillTimer(hwnd, TIMER_SEARCH);
            SetWindowLongPtrW(hwnd, GWLP_USERDATA, 0);
            let _ = remember_uid(hwnd);
            drop(Box::from_raw(ptr));
            return DefWindowProcW(hwnd, message, wparam, lparam);
        }
        _ => return DefWindowProcW(hwnd, message, wparam, lparam),
    }
    LRESULT(0)
}

impl Panel {
    unsafe fn command(&mut self, hwnd: HWND, id: i32) {
        match id {
            TAB_PLAYER => self.show_tab(hwnd, Tab::Player),
            TAB_GUIDES => self.show_tab(hwnd, Tab::Guides),
            TAB_ITEMS => self.show_tab(hwnd, Tab::Items),
            READ | SET | PREVIEW | COMPLETE if !self.busy => {
                let status = if matches!(id, READ | SET) { PLAYER_STATUS } else { GUIDE_STATUS };
                match progression_request(hwnd, id) {
                    Ok(request) => {
                        let _ = remember_uid(hwnd);
                        set_text(hwnd, status, "Waiting for the local server…");
                        self.start(hwnd, if status == PLAYER_STATUS { Job::Level } else { Job::Guides }, request);
                    }
                    Err(error) => set_text(hwnd, status, &error.to_string()),
                }
            }
            _ if self.tab == Tab::Items && !self.busy => {
                match self.finder.command(hwnd, id) {
                    Ok(Some((job, request))) => self.start(hwnd, job, request),
                    Ok(None) => {}
                    Err(error) => self.finder.status(hwnd, &format!("{error:#}")),
                }
            }
            _ => {}
        }
    }

    unsafe fn show_tab(&mut self, hwnd: HWND, tab: Tab) {
        self.tab = tab;
        for (controls, show) in [(PLAYER, tab == Tab::Player), (GUIDES, tab == Tab::Guides), (finder::CONTROLS, tab == Tab::Items)] {
            for &id in controls {
                let _ = ShowWindow(GetDlgItem(hwnd, id), if show { SW_SHOWNA } else { SW_HIDE });
            }
        }
        for id in [TAB_PLAYER, TAB_GUIDES, TAB_ITEMS] {
            let _ = InvalidateRect(GetDlgItem(hwnd, id), None, false);
        }
        let _ = InvalidateRect(hwnd, None, true);
        if tab == Tab::Items {
            self.finder.update_actions(hwnd, self.busy);
            if self.finder.has_catalog() {
                self.item_search(hwnd);
            } else if !self.busy {
                self.finder.status(hwnd, "Loading the item catalog…");
                self.start(hwnd, Job::Catalog, GmRequest::ItemCatalog);
            }
        }
    }

    unsafe fn start(&mut self, hwnd: HWND, job: Job, request: GmRequest) {
        self.busy = true;
        for &id in PROGRESSION_ACTIONS {
            enabled(hwnd, id, false);
        }
        self.finder.update_actions(hwnd, true);
        let control = self.control.clone();
        let send = self.send.clone();
        thread::spawn(move || {
            let result = control.request(request);
            if send.send((job, result)).is_ok() {
                let _ = PostMessageW(hwnd, WM_RESULT, WPARAM(0), LPARAM(0));
            }
        });
    }

    unsafe fn finish(&mut self, hwnd: HWND, job: Job, result: Result<GmReply>) {
        self.busy = false;
        for &id in PROGRESSION_ACTIONS {
            enabled(hwnd, id, true);
        }
        let next = match job {
            Job::Level | Job::Guides => {
                let id = if matches!(job, Job::Level) { PLAYER_STATUS } else { GUIDE_STATUS };
                let status = match result {
                    Ok(reply) => describe(reply),
                    Err(error) => format!("GM operation failed. {error:#}"),
                };
                set_text(hwnd, id, &status.replace('\n', "\r\n"));
                None
            }
            job => self.finder.finish(hwnd, job, result),
        };
        self.finder.update_actions(hwnd, false);
        if self.tab == Tab::Items {
            if let Some((job, request)) = next {
                self.start(hwnd, job, request);
            } else if self.finder.search_waiting {
                self.item_search(hwnd);
            } else if !self.finder.has_catalog() && !self.finder.catalog_checked {
                self.start(hwnd, Job::Catalog, GmRequest::ItemCatalog);
            } else {
                self.item_selection(hwnd);
            }
        }
    }

    unsafe fn item_search(&mut self, hwnd: HWND) {
        if self.tab != Tab::Items { return; }
        if self.busy {
            self.finder.search_waiting = true;
        } else if let Some((job, request)) = self.finder.search(hwnd, 0) {
            self.start(hwnd, job, request);
        }
    }

    unsafe fn item_selection(&mut self, hwnd: HWND) {
        if self.tab == Tab::Items && !self.busy {
            if let Some((job, request)) = self.finder.selection(hwnd) {
                self.start(hwnd, job, request);
            }
        }
    }
}

unsafe fn create_controls(hwnd: HWND, uid: i64) {
    label(hwnd, 450, "GM Tools", theme::Font::Title, 26, 16, 480, 33);
    label(hwnd, 457, "Player UID", theme::Font::Body, 600, 22, 75, 24);
    edit(hwnd, UID, &uid.to_string(), true, 684, 19, 134, 31);
    for (id, name, x) in [(TAB_PLAYER, "Player", 28), (TAB_GUIDES, "Guides", 291), (TAB_ITEMS, "Item Finder", 554)] {
        button(hwnd, id, name, x, 70, 262, 35);
    }

    label(hwnd, 451, "Commandant level", theme::Font::Heading, 47, 151, 520, 32);
    label(hwnd, 452, "Target level", theme::Font::Body, 48, 211, 120, 25);
    edit(hwnd, LEVEL, "120", true, 175, 206, 164, 34);
    button(hwnd, READ, "READ CURRENT LEVEL", 363, 205, 214, 36);
    button(hwnd, SET, "SET LEVEL", 593, 205, 199, 36);
    multiline(hwnd, PLAYER_STATUS, "Read the current Commandant level or set a new level for the selected UID.",
        47, 265, 747, 106);

    label(hwnd, 453, "Guide progression", theme::Font::Heading, 47, 151, 520, 32);
    label(hwnd, 454, "Guide scope", theme::Font::Body, 47, 206, 112, 25);
    let scope = combo(hwnd, SCOPE, 170, 200, 268, 170);
    label(hwnd, 455, "ID", theme::Font::Body, 470, 206, 55, 25);
    edit(hwnd, GUIDE_ID, "", true, 541, 201, 251, 32);
    label(hwnd, 456, "Rewards", theme::Font::Body, 47, 253, 112, 25);
    let policy = combo(hwnd, POLICY, 170, 247, 622, 170);
    child(hwnd, w!("BUTTON"), "Waive client-only display checks", WS_TABSTOP | WINDOW_STYLE(BS_AUTOCHECKBOX as u32),
        IGNORE_CLIENT, theme::Font::Body, 170, 286, 622, 29);
    button(hwnd, PREVIEW, "DRY-RUN PREVIEW", 363, 332, 214, 36);
    button(hwnd, COMPLETE, "COMPLETE GUIDES", 593, 332, 199, 36);
    multiline(hwnd, GUIDE_STATUS, "Sign in to PGR as the selected player to preview or complete guides.", 47, 396, 747, 202);
    fill_combo(scope, &["Single guide", "Guide group", "All eligible guides"]);
    fill_combo(policy, &["Select a reward policy", "Preserve rewards", "Grant rewards", "Skip without reward"]);
    finder::create_controls(hwnd);
}

unsafe fn child(hwnd: HWND, class: PCWSTR, value: &str, style: WINDOW_STYLE, id: i32, font: theme::Font,
    x: i32, y: i32, width: i32, height: i32) -> HWND {
    let wide = super::wide(value);
    let child = super::control(hwnd, class, PCWSTR(wide.as_ptr()), style, id, x, y, width, height);
    let _ = SendMessageW(child, WM_SETFONT, WPARAM(theme::font(font).0 as usize), LPARAM(1));
    child
}

unsafe fn label(hwnd: HWND, id: i32, value: &str, font: theme::Font, x: i32, y: i32, width: i32, height: i32) -> HWND {
    child(hwnd, w!("STATIC"), value, if id == 450 || id == 457 { WS_VISIBLE } else { WINDOW_STYLE(0) }, id, font, x, y, width, height)
}

unsafe fn edit(hwnd: HWND, id: i32, value: &str, numeric: bool, x: i32, y: i32, width: i32, height: i32) -> HWND {
    let mode = ES_AUTOHSCROLL | if numeric { ES_NUMBER } else { 0 };
    let visibility = if id == UID { WS_VISIBLE } else { WINDOW_STYLE(0) };
    let edit = child(hwnd, w!("EDIT"), value, visibility | WS_TABSTOP | WS_BORDER | WINDOW_STYLE(mode as u32), id,
        theme::Font::Body, x, y, width, height);
    let _ = SendMessageW(edit, EM_SETMARGINS, WPARAM((EC_LEFTMARGIN | EC_RIGHTMARGIN) as usize), LPARAM(8 | (8 << 16)));
    edit
}

unsafe fn multiline(hwnd: HWND, id: i32, value: &str, x: i32, y: i32, width: i32, height: i32) -> HWND {
    child(hwnd, w!("EDIT"), value, WS_TABSTOP | WS_BORDER | WS_VSCROLL
        | WINDOW_STYLE((ES_MULTILINE | ES_AUTOVSCROLL | ES_READONLY) as u32), id, theme::Font::Body, x, y, width, height)
}

unsafe fn combo(hwnd: HWND, id: i32, x: i32, y: i32, width: i32, height: i32) -> HWND {
    child(hwnd, w!("COMBOBOX"), "", WS_TABSTOP | WS_VSCROLL | WINDOW_STYLE(CBS_DROPDOWNLIST as u32), id,
        theme::Font::Body, x, y, width, height)
}

unsafe fn button(hwnd: HWND, id: i32, value: &str, x: i32, y: i32, width: i32, height: i32) -> HWND {
    let visibility = if matches!(id, TAB_PLAYER | TAB_GUIDES | TAB_ITEMS) { WS_VISIBLE } else { WINDOW_STYLE(0) };
    child(hwnd, w!("BUTTON"), value, visibility | WS_TABSTOP | WINDOW_STYLE(BS_OWNERDRAW as u32), id,
        theme::Font::Label, x, y, width, height)
}

unsafe fn fill_combo(hwnd: HWND, labels: &[&str]) {
    for label in labels {
        let label = super::wide(label);
        let _ = SendMessageW(hwnd, CB_ADDSTRING, WPARAM(0), LPARAM(label.as_ptr() as isize));
    }
    let _ = SendMessageW(hwnd, CB_SETCURSEL, WPARAM(0), LPARAM(0));
}

unsafe fn selected(hwnd: HWND, id: i32) -> isize {
    SendMessageW(GetDlgItem(hwnd, id), CB_GETCURSEL, WPARAM(0), LPARAM(0)).0
}

unsafe fn set_text(hwnd: HWND, id: i32, value: &str) { super::set_text(hwnd, id, value); }
unsafe fn enabled(hwnd: HWND, id: i32, value: bool) { super::set_enabled(hwnd, id, value); }

unsafe fn uid(hwnd: HWND) -> Result<i64> {
    let uid: i64 = super::get_text(hwnd, UID)?.trim().parse().context("Enter a positive player UID.")?;
    anyhow::ensure!(uid > 0, "Enter a positive player UID.");
    Ok(uid)
}

unsafe fn remember_uid(hwnd: HWND) -> Result<i64> {
    let uid = uid(hwnd)?;
    let owner = GetWindow(hwnd, GW_OWNER);
    let _ = PostMessageW(owner, super::WM_GM_UID, WPARAM(uid as u32 as usize), LPARAM(((uid as u64) >> 32) as isize));
    Ok(uid)
}

unsafe fn progression_request(hwnd: HWND, action: i32) -> Result<GmRequest> {
    let uid = uid(hwnd)?;
    match action {
        READ => Ok(GmRequest::ReadLevel { uid }),
        SET => {
            let level: i32 = super::get_text(hwnd, LEVEL)?.trim().parse().context("Enter a positive Commandant level.")?;
            anyhow::ensure!(level > 0, "Enter a positive Commandant level.");
            Ok(GmRequest::SetLevel { uid, level })
        }
        _ => {
            let scope = selected(hwnd, SCOPE);
            let id = if scope == 2 { None } else {
                let id: i32 = super::get_text(hwnd, GUIDE_ID)?.trim().parse().context("Enter a positive guide or group ID.")?;
                anyhow::ensure!(id > 0, "Enter a positive guide or group ID.");
                Some(id)
            };
            let reward_policy = match selected(hwnd, POLICY) {
                1 => GuideRewardPolicy::Preserve,
                2 => GuideRewardPolicy::Grant,
                3 => GuideRewardPolicy::Skip,
                _ => bail!("Select a reward policy."),
            };
            Ok(GmRequest::Guides {
                uid, guide_id: id.filter(|_| scope == 0), group_id: id.filter(|_| scope == 1),
                all: scope == 2, reward_policy, dry_run: action == PREVIEW,
                ignore_client_triggers: SendMessageW(GetDlgItem(hwnd, IGNORE_CLIENT), BM_GETCHECK, WPARAM(0), LPARAM(0)).0 != 0,
            })
        }
    }
}

fn describe(reply: GmReply) -> String {
    if let Some(player) = reply.player {
        let state = if player.online { "online" } else { "offline" };
        let relog = if player.requires_relog { "\nSign in again to refresh the client after a level decrease." } else { "" };
        format!("Server confirmed UID {} at Commandant Level {} ({}).{relog}", player.uid, player.level, state)
    } else {
        reply.message.unwrap_or_else(|| "The server completed the operation.".into())
    }
}

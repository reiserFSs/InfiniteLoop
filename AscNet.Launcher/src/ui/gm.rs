use anyhow::{bail, Context, Result};
use ascnet_launcher::gm_control::{GmControl, GmReply, GmRequest, GuideRewardPolicy};
use std::{sync::mpsc, thread};
use windows::{
    core::{w, PCWSTR},
    Win32::{
        Foundation::*,
        Graphics::Gdi::{GetStockObject, COLOR_BTNFACE, DEFAULT_GUI_FONT, HBRUSH},
        System::LibraryLoader::GetModuleHandleW,
        UI::Input::KeyboardAndMouse::SetFocus,
        UI::WindowsAndMessaging::*,
    },
};

const CLASS: PCWSTR = w!("AscNetLauncherGmTools");
const WM_GM_RESULT: u32 = WM_APP + 1;
const UID: i32 = 401;
const LEVEL: i32 = 402;
const READ: i32 = 403;
const SET: i32 = 404;
const SCOPE: i32 = 405;
const GUIDE_ID: i32 = 406;
const POLICY: i32 = 407;
const IGNORE_CLIENT: i32 = 408;
const PREVIEW: i32 = 409;
const COMPLETE: i32 = 410;
const STATUS: i32 = 411;
const ACTIONS: [i32; 4] = [READ, SET, PREVIEW, COMPLETE];

struct Panel {
    control: GmControl,
    send: mpsc::Sender<Result<GmReply>>,
    receive: mpsc::Receiver<Result<GmReply>>,
    pending: bool,
}

pub(super) unsafe fn open(owner: HWND, control: GmControl) -> Result<HWND> {
    let instance = HINSTANCE(GetModuleHandleW(None)?.0);
    let window_class = WNDCLASSW {
        lpfnWndProc: Some(window_proc),
        hInstance: instance,
        hCursor: LoadCursorW(None, IDC_ARROW)?,
        hbrBackground: HBRUSH((COLOR_BTNFACE.0 + 1) as isize),
        lpszClassName: CLASS,
        ..Default::default()
    };
    if RegisterClassW(&window_class) == 0 && GetLastError() != ERROR_CLASS_ALREADY_EXISTS {
        return Err(windows::core::Error::from_win32().into());
    }

    let (send, receive) = mpsc::channel();
    let mut panel = Some(Panel { control, send, receive, pending: false });
    let mut owner_rect = RECT::default();
    GetWindowRect(owner, &mut owner_rect)?;
    let style = WS_POPUP | WS_CAPTION | WS_SYSMENU;
    let extended = WS_EX_CONTROLPARENT | WS_EX_DLGMODALFRAME;
    let mut rect = RECT { left: 0, top: 0, right: 542, bottom: 442 };
    AdjustWindowRectEx(&mut rect, style, false, extended)?;
    let width = rect.right - rect.left;
    let height = rect.bottom - rect.top;
    let hwnd = CreateWindowExW(extended, CLASS, w!("AscNet GM Tools"), style,
        owner_rect.left + (owner_rect.right - owner_rect.left - width) / 2,
        owner_rect.top + (owner_rect.bottom - owner_rect.top - height) / 2,
        width, height, owner, None, instance, Some((&mut panel as *mut Option<Panel>).cast()));
    if hwnd.0 == 0 {
        return Err(windows::core::Error::from_win32().into());
    }
    let _ = ShowWindow(hwnd, SW_SHOW);
    let _ = SetForegroundWindow(hwnd);
    let _ = SetFocus(GetDlgItem(hwnd, UID));
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
    let panel = GetWindowLongPtrW(hwnd, GWLP_USERDATA) as *mut Panel;
    if panel.is_null() {
        return DefWindowProcW(hwnd, message, wparam, lparam);
    }

    match message {
        WM_CREATE => create_controls(hwnd),
        WM_COMMAND => {
            let id = (wparam.0 & 0xffff) as i32;
            let notification = (wparam.0 >> 16) as u32;
            if id == SCOPE && notification == CBN_SELCHANGE {
                super::set_enabled(hwnd, GUIDE_ID, selected(hwnd, SCOPE) != 2);
            } else if ACTIONS.contains(&id) && notification == BN_CLICKED as u32 && !(*panel).pending {
                match request(hwnd, id) {
                    Ok(request) => {
                        (*panel).pending = true;
                        for id in ACTIONS {
                            super::set_enabled(hwnd, id, false);
                        }
                        super::set_text(hwnd, STATUS, "Waiting for the local server…");
                        let control = (*panel).control.clone();
                        let send = (*panel).send.clone();
                        thread::spawn(move || {
                            let result = control.request(request);
                            if send.send(result).is_ok() {
                                let _ = PostMessageW(hwnd, WM_GM_RESULT, WPARAM(0), LPARAM(0));
                            }
                        });
                    }
                    Err(error) => super::set_text(hwnd, STATUS, &error.to_string()),
                }
            }
        }
        WM_GM_RESULT => {
            if let Ok(result) = (*panel).receive.try_recv() {
                (*panel).pending = false;
                for id in ACTIONS {
                    super::set_enabled(hwnd, id, true);
                }
                let status = match result {
                    Ok(reply) => describe(reply),
                    Err(error) => format!("GM operation failed. {error:#}"),
                };
                super::set_text(hwnd, STATUS, &status.replace('\n', "\r\n"));
            }
        }
        WM_CLOSE => { let _ = DestroyWindow(hwnd); }
        WM_NCDESTROY => {
            SetWindowLongPtrW(hwnd, GWLP_USERDATA, 0);
            drop(Box::from_raw(panel));
            return DefWindowProcW(hwnd, message, wparam, lparam);
        }
        _ => return DefWindowProcW(hwnd, message, wparam, lparam),
    }
    LRESULT(0)
}

unsafe fn create_controls(hwnd: HWND) {
    let font = GetStockObject(DEFAULT_GUI_FONT);
    let add = |class, text, style, id, x, y, width, height| {
        let child = super::control(hwnd, class, text, WS_VISIBLE | style, id, x, y, width, height);
        let _ = SendMessageW(child, WM_SETFONT, WPARAM(font.0 as usize), LPARAM(1));
        child
    };
    let edit = WS_TABSTOP | WS_BORDER | WINDOW_STYLE((ES_AUTOHSCROLL | ES_NUMBER) as u32);
    let combo = WS_TABSTOP | WS_VSCROLL | WINDOW_STYLE(CBS_DROPDOWNLIST as u32);
    let button = WS_TABSTOP | WINDOW_STYLE(BS_PUSHBUTTON as u32);

    add(w!("STATIC"), w!("Player UID"), WINDOW_STYLE(0), -1, 20, 22, 105, 22);
    add(w!("EDIT"), w!("2"), edit, UID, 133, 18, 188, 26);
    add(w!("BUTTON"), w!("READ CURRENT LEVEL"), button, READ, 334, 18, 186, 28);
    add(w!("STATIC"), w!("Target level"), WINDOW_STYLE(0), -1, 20, 59, 105, 22);
    add(w!("EDIT"), w!("120"), edit, LEVEL, 133, 55, 188, 26);
    add(w!("BUTTON"), w!("SET LEVEL"), button, SET, 334, 55, 186, 28);

    add(w!("STATIC"), w!("Guide scope"), WINDOW_STYLE(0), -1, 20, 111, 105, 22);
    let scope = add(w!("COMBOBOX"), w!(""), combo, SCOPE, 133, 107, 188, 140);
    add(w!("STATIC"), w!("ID"), WINDOW_STYLE(0), -1, 334, 111, 30, 22);
    add(w!("EDIT"), w!(""), edit, GUIDE_ID, 367, 107, 153, 26);
    add(w!("STATIC"), w!("Rewards"), WINDOW_STYLE(0), -1, 20, 148, 105, 22);
    let policy = add(w!("COMBOBOX"), w!(""), combo, POLICY, 133, 144, 387, 150);
    add(w!("BUTTON"), w!("Waive client-only display checks"), WS_TABSTOP | WINDOW_STYLE(BS_AUTOCHECKBOX as u32),
        IGNORE_CLIENT, 133, 178, 387, 25);
    add(w!("BUTTON"), w!("DRY-RUN PREVIEW"), button, PREVIEW, 133, 213, 188, 28);
    add(w!("BUTTON"), w!("COMPLETE GUIDES"), button, COMPLETE, 334, 213, 186, 28);

    add(w!("EDIT"), w!("Guide completion requires a signed-in player."),
        WS_TABSTOP | WS_BORDER | WS_VSCROLL | WINDOW_STYLE((ES_MULTILINE | ES_AUTOVSCROLL | ES_READONLY) as u32),
        STATUS, 20, 264, 500, 155);
    fill_combo(scope, &[w!("Single guide"), w!("Guide group"), w!("All eligible guides")]);
    fill_combo(policy, &[w!("Select a reward policy"), w!("Preserve rewards"), w!("Grant rewards"), w!("Skip without reward")]);
}

unsafe fn fill_combo(hwnd: HWND, labels: &[PCWSTR]) {
    for label in labels {
        let _ = SendMessageW(hwnd, CB_ADDSTRING, WPARAM(0), LPARAM(label.0 as isize));
    }
    let _ = SendMessageW(hwnd, CB_SETCURSEL, WPARAM(0), LPARAM(0));
}

unsafe fn selected(hwnd: HWND, id: i32) -> isize {
    SendMessageW(GetDlgItem(hwnd, id), CB_GETCURSEL, WPARAM(0), LPARAM(0)).0
}

unsafe fn request(hwnd: HWND, action: i32) -> Result<GmRequest> {
    let uid: i64 = super::get_text(hwnd, UID)?.trim().parse().context("Enter a positive player UID.")?;
    anyhow::ensure!(uid > 0, "Enter a positive player UID.");
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

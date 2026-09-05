use crate::auth::client;
use crate::types::*;
use crate::ui::{finish_login, PARENT_HWND};

use windows::Win32::UI::Input::KeyboardAndMouse::*;
use windows::{
    core::*, Win32::Foundation::*, Win32::Graphics::Gdi::*, Win32::System::LibraryLoader::*,
    Win32::UI::WindowsAndMessaging::*,
};

static mut USERNAME_HWND: Option<HWND> = None;
static mut PASSWORD_HWND: Option<HWND> = None;

const ES_AUTOHSCROLL: u32 = 0x0080;
const ES_PASSWORD: u32 = 0x0020;
const BS_DEFPUSHBUTTON: u32 = 0x0001;
static mut DIALOG_HWND: Option<HWND> = None;

pub unsafe fn handles_dialog_message(message: &MSG) -> bool {
    DIALOG_HWND
        .map(|hwnd| IsDialogMessageW(hwnd, message).as_bool())
        .unwrap_or(false)
}
const BS_PUSHBUTTON: u32 = 0x0000;

pub unsafe fn show(parent: HWND) {
    let class_name = w!("KRSDK_Login");

    let wc = WNDCLASSW {
        lpfnWndProc: Some(window_proc),
        hInstance: GetModuleHandleW(None).unwrap().into(),
        lpszClassName: class_name,
        hbrBackground: HBRUSH((COLOR_WINDOW.0 + 1) as isize),
        ..Default::default()
    };

    RegisterClassW(&wc);

    // Create as TOP-LEVEL window (NO parent)
    let hwnd = CreateWindowExW(
        WS_EX_CONTROLPARENT,
        class_name,
        w!("Login"),
        WS_OVERLAPPEDWINDOW,
        0,
        0,
        0,
        0,
        HWND(0),
        None,
        GetModuleHandleW(None).unwrap(),
        None,
    );

    if hwnd.0 == 0 {
        return;
    }
    DIALOG_HWND = Some(hwnd);

    // Set owner AFTER creation
    if parent.0 != 0 {
        SetWindowLongPtrW(hwnd, GWLP_HWNDPARENT, parent.0);
    }

    // Dialog size
    let w = 400;
    let h = 270;

    // Center on parent (or screen if parent is none)
    let (x, y) = if parent.0 != 0 {
        let mut rc = RECT::default();
        let _ = GetWindowRect(parent, &mut rc);
        let pw = rc.right - rc.left;
        let ph = rc.bottom - rc.top;

        (rc.left + (pw - w) / 2, rc.top + (ph - h) / 2)
    } else {
        (
            (GetSystemMetrics(SM_CXSCREEN) - w) / 2,
            (GetSystemMetrics(SM_CYSCREEN) - h) / 2,
        )
    };

    // Show window centered & foreground
    let _ = SetWindowPos(hwnd, HWND_TOP, x, y, w, h, SWP_SHOWWINDOW);

    let _ = SetForegroundWindow(hwnd);
    SetActiveWindow(hwnd);
    if let Some(username) = USERNAME_HWND {
        SetFocus(username);
    }
}

unsafe extern "system" fn window_proc(
    hwnd: HWND,
    msg: u32,
    wparam: WPARAM,
    _lparam: LPARAM,
) -> LRESULT {
    match msg {
        WM_CREATE => {
            // Username label
            CreateWindowExW(
                WINDOW_EX_STYLE(0),
                w!("Static"),
                w!("Username:"),
                WS_CHILD | WS_VISIBLE,
                20,
                20,
                100,
                20,
                hwnd,
                None,
                GetModuleHandleW(None).unwrap(),
                None,
            );

            // Username edit
            USERNAME_HWND = Some(CreateWindowExW(
                WS_EX_CLIENTEDGE,
                w!("Edit"),
                w!(""),
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | WINDOW_STYLE(ES_AUTOHSCROLL),
                120,
                20,
                240,
                25,
                hwnd,
                HMENU(ID_EDIT_USERNAME as isize),
                GetModuleHandleW(None).unwrap(),
                None,
            ));

            // Password label
            CreateWindowExW(
                WINDOW_EX_STYLE(0),
                w!("Static"),
                w!("Password:"),
                WS_CHILD | WS_VISIBLE,
                20,
                60,
                100,
                20,
                hwnd,
                None,
                GetModuleHandleW(None).unwrap(),
                None,
            );

            // Password edit
            PASSWORD_HWND = Some(CreateWindowExW(
                WS_EX_CLIENTEDGE,
                w!("Edit"),
                w!(""),
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | WINDOW_STYLE(ES_PASSWORD | ES_AUTOHSCROLL),
                120,
                60,
                240,
                25,
                hwnd,
                HMENU(ID_EDIT_PASSWORD as isize),
                GetModuleHandleW(None).unwrap(),
                None,
            ));

            // Submit button
            CreateWindowExW(
                WINDOW_EX_STYLE(0),
                w!("Button"),
                w!("Login"),
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | WINDOW_STYLE(BS_DEFPUSHBUTTON),
                120,
                110,
                100,
                30,
                hwnd,
                HMENU(ID_BTN_SUBMIT as isize),
                GetModuleHandleW(None).unwrap(),
                None,
            );

            // Cancel button
            CreateWindowExW(
                WINDOW_EX_STYLE(0),
                w!("Button"),
                w!("Cancel"),
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | WINDOW_STYLE(BS_PUSHBUTTON),
                230,
                110,
                100,
                30,
                hwnd,
                HMENU(ID_BTN_CANCEL as isize),
                GetModuleHandleW(None).unwrap(),
                None,
            );

            LRESULT(0)
        }

        WM_COMMAND => {
            match wparam.0 as i32 {
                ID_BTN_SUBMIT => handle_login(hwnd),
                ID_BTN_CANCEL => {
                    let _ = DestroyWindow(hwnd);
                }
                _ => {}
            }
            LRESULT(0)
        }

        WM_DESTROY => {
            USERNAME_HWND = None;
            PASSWORD_HWND = None;
            DIALOG_HWND = None;
            LRESULT(0)
        }

        _ => DefWindowProcW(hwnd, msg, wparam, _lparam),
    }
}

unsafe fn handle_login(hwnd: HWND) {
    let username = get_window_text(USERNAME_HWND.unwrap());
    let password = get_window_text(PASSWORD_HWND.unwrap());

    if username.is_empty() || password.is_empty() {
        MessageBoxW(
            hwnd,
            w!("Please enter both username and password"),
            w!("Error"),
            MB_OK | MB_ICONERROR,
        );
        return;
    }

    match client::login(&username, &password) {
        Ok(session) => {
            finish_login(session);

            MessageBoxW(
                hwnd,
                w!("Login successful!"),
                w!("Success"),
                MB_OK | MB_ICONINFORMATION,
            );

            // Close login window
            let _ = DestroyWindow(hwnd);

            // Close the parent main window
            if let Some(parent) = *PARENT_HWND.lock().unwrap() {
                let _ = DestroyWindow(parent);
            }
        }

        Err(e) => {
            let msg = format!("Login failed: {}\0", e);
            let wide: Vec<u16> = msg.encode_utf16().collect();

            MessageBoxW(
                hwnd,
                PCWSTR(wide.as_ptr()),
                w!("Error"),
                MB_OK | MB_ICONERROR,
            );
        }
    }
}

unsafe fn get_window_text(hwnd: HWND) -> String {
    let len = GetWindowTextLengthW(hwnd);
    if len == 0 {
        return String::new();
    }

    let mut buffer = vec![0u16; (len + 1) as usize];
    GetWindowTextW(hwnd, &mut buffer);
    String::from_utf16_lossy(&buffer[..len as usize])
}

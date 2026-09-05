use crate::types::*;
use crate::ui::{login_dialog, register_dialog, PARENT_HWND};
use windows::{
    core::*, Win32::Foundation::*, Win32::Graphics::Gdi::*, Win32::System::LibraryLoader::*,
    Win32::UI::Input::KeyboardAndMouse::*, Win32::UI::WindowsAndMessaging::*,
};

pub unsafe fn show_with_parent(parent: HWND) {
    let class_name = w!("KRSDK_MainWindow");

    let wc = WNDCLASSW {
        lpfnWndProc: Some(window_proc),
        hInstance: GetModuleHandleW(None).unwrap().into(),
        lpszClassName: class_name,
        hbrBackground: HBRUSH((COLOR_WINDOW.0 + 1) as isize),
        ..Default::default()
    };

    RegisterClassW(&wc);

    // Create as top-level window
    let hwnd = CreateWindowExW(
        WINDOW_EX_STYLE(0),
        class_name,
        w!("KRSDK"),
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

    // Set the owner AFTER creation (correct fix)
    if parent.0 != 0 {
        SetWindowLongPtrW(hwnd, GWLP_HWNDPARENT, parent.0 as isize);
    }

    // 3️⃣ Size + center logic
    let w = 400;
    let h = 300;

    let (x, y) = if parent.0 != 0 {
        // center on parent
        let mut rc = RECT::default();
        let _ = GetWindowRect(parent, &mut rc);

        let pw = rc.right - rc.left;
        let ph = rc.bottom - rc.top;

        (rc.left + (pw - w) / 2, rc.top + (ph - h) / 2)
    } else {
        // center on screen
        (
            (GetSystemMetrics(SM_CXSCREEN) - w) / 2,
            (GetSystemMetrics(SM_CYSCREEN) - h) / 2,
        )
    };

    // Show & activate window
    let _ = SetWindowPos(hwnd, HWND_TOP, x, y, w, h, SWP_SHOWWINDOW);

    let _ = SetForegroundWindow(hwnd);
    SetFocus(hwnd);

    // Save the handle so login dialog can close it later
    *PARENT_HWND.lock().unwrap() = Some(hwnd);

    // Standard message loop
    let mut msg = MSG::default();
    while GetMessageW(&mut msg, None, 0, 0).into() {
        if !login_dialog::handles_dialog_message(&msg)
            && !register_dialog::handles_dialog_message(&msg)
        {
            let _ = TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }
}

pub unsafe fn show() {
    show_with_parent(HWND(0));
}

unsafe extern "system" fn window_proc(
    hwnd: HWND,
    msg: u32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
    match msg {
        WM_CREATE => {
            // Login button
            CreateWindowExW(
                WINDOW_EX_STYLE(0),
                w!("Button"),
                w!("Login"),
                WS_CHILD | WS_VISIBLE,
                0,
                0,
                150,
                75,
                hwnd,
                HMENU(ID_BTN_LOGIN as isize),
                GetModuleHandleW(None).unwrap(),
                None,
            );

            // Register button
            CreateWindowExW(
                WINDOW_EX_STYLE(0),
                w!("Button"),
                w!("Register"),
                WS_CHILD | WS_VISIBLE,
                150,
                0,
                150,
                75,
                hwnd,
                HMENU(ID_BTN_REGISTER as isize),
                GetModuleHandleW(None).unwrap(),
                None,
            );

            LRESULT(0)
        }

        WM_COMMAND => {
            match wparam.0 as i32 {
                ID_BTN_LOGIN => {
                    println!("[KRSDK] Login clicked");
                    login_dialog::show(hwnd);
                }
                ID_BTN_REGISTER => {
                    println!("[KRSDK] Register clicked");
                    register_dialog::show(hwnd);
                }
                _ => {}
            }
            LRESULT(0)
        }

        WM_PAINT => {
            let mut ps = PAINTSTRUCT::default();
            let hdc = BeginPaint(hwnd, &mut ps);

            let mut rect = RECT::default();
            let _ = GetClientRect(hwnd, &mut rect);

            SetTextColor(hdc, COLORREF(0));
            SetBkMode(hdc, TRANSPARENT);

            let text = w!("KRSDK 2.0.0 - an AscNet utility");
            let mut text_vec: Vec<u16> = text.as_wide().iter().copied().collect();

            DrawTextW(
                hdc,
                &mut text_vec,
                &mut rect,
                DT_CENTER | DT_VCENTER | DT_SINGLELINE,
            );

            let _ = EndPaint(hwnd, &ps);
            LRESULT(0)
        }

        WM_DESTROY => {
            PostQuitMessage(0);
            LRESULT(0)
        }

        _ => DefWindowProcW(hwnd, msg, wparam, lparam),
    }
}

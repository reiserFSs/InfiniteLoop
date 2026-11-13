use super::*;
use std::sync::OnceLock;

const INK: COLORREF = COLORREF(0x00282120);
const MUTED: COLORREF = COLORREF(0x00706760);
const WHITE: COLORREF = COLORREF(0x00ffffff);
const ACCENT: COLORREF = COLORREF(0x0062532c);
const BORDER: COLORREF = COLORREF(0x00e8e4de);
pub(super) const CANVAS: COLORREF = COLORREF(0x00f8f6f3);

pub(super) enum Font { Body, Label, Heading, Title }

pub(super) struct Palette {
    pub canvas: HBRUSH,
    white: HBRUSH,
    tabs: HBRUSH,
    body: HFONT,
    label: HFONT,
    heading: HFONT,
    title: HFONT,
}

pub(super) fn palette() -> &'static Palette {
    static PALETTE: OnceLock<Palette> = OnceLock::new();
    PALETTE.get_or_init(|| unsafe {
        Palette {
            canvas: CreateSolidBrush(CANVAS),
            white: CreateSolidBrush(WHITE),
            tabs: CreateSolidBrush(COLORREF(0x00efebe6)),
            body: make_font(17, FW_NORMAL),
            label: make_font(16, FW_SEMIBOLD),
            heading: make_font(22, FW_SEMIBOLD),
            title: make_font(28, FW_SEMIBOLD),
        }
    })
}

unsafe fn make_font(size: i32, weight: FONT_WEIGHT) -> HFONT {
    CreateFontW(-size, 0, 0, 0, weight.0 as i32, 0, 0, 0, DEFAULT_CHARSET.0 as u32, 0, 0,
        CLEARTYPE_QUALITY.0 as u32, 0, w!("Segoe UI"))
}

pub(super) fn font(style: Font) -> HFONT {
    let p = palette();
    match style {
        Font::Body => p.body,
        Font::Label => p.label,
        Font::Heading => p.heading,
        Font::Title => p.title,
    }
}

unsafe fn tab(hwnd: HWND) -> Tab {
    let state = GetWindowLongPtrW(hwnd, GWLP_USERDATA) as *const Panel;
    if state.is_null() { Tab::Player } else { (*state).tab }
}

pub(super) unsafe fn control_color(_hwnd: HWND, dc: HDC, child: HWND, message: u32) -> LRESULT {
    let id = GetDlgCtrlID(child);
    let surface = if message == WM_CTLCOLOREDIT || matches!(id, PLAYER_STATUS | GUIDE_STATUS) || finder::in_card(id) {
        (WHITE, palette().white)
    } else { (CANVAS, palette().canvas) };
    let _ = SetBkColor(dc, surface.0);
    let _ = SetTextColor(dc, if id == finder::STATUS || id == finder::COUNT { MUTED } else { INK });
    LRESULT(surface.1.0)
}

pub(super) unsafe fn paint(hwnd: HWND) {
    let mut paint = PAINTSTRUCT::default();
    let dc = BeginPaint(hwnd, &mut paint);
    let p = palette();
    rounded(dc, &RECT { left: 24, top: 66, right: 820, bottom: 109 }, p.tabs, None, 13);
    let bounds = match tab(hwnd) {
        Tab::Player => Some(RECT { left: 24, top: 126, right: 820, bottom: 402 }),
        Tab::Guides => Some(RECT { left: 24, top: 126, right: 820, bottom: 626 }),
        Tab::Items => Some(RECT { left: 24, top: 413, right: 820, bottom: 643 }),
    };
    if let Some(rect) = bounds {
        rounded(dc, &rect, p.white, Some(BORDER), 15);
    }
    let _ = EndPaint(hwnd, &paint);
}

pub(super) unsafe fn draw_button(button: &DRAWITEMSTRUCT) {
    let parent = GetParent(button.hwndItem);
    let id = button.CtlID as i32;
    let active_tab = matches!((id, tab(parent)), (TAB_PLAYER, Tab::Player) | (TAB_GUIDES, Tab::Guides) | (TAB_ITEMS, Tab::Items));
    let tab = matches!(id, TAB_PLAYER | TAB_GUIDES | TAB_ITEMS);
    let primary = matches!(id, SET | COMPLETE | finder::ADD);
    let disabled = button.itemState.0 & ODS_DISABLED.0 != 0;
    let pressed = button.itemState.0 & ODS_SELECTED.0 != 0;
    let (background, border, color) = if disabled {
        (COLORREF(0x00efedea), None, COLORREF(0x008b8580))
    } else if primary || active_tab {
        (if pressed { COLORREF(0x00504823) } else { ACCENT }, None, WHITE)
    } else if tab {
        (COLORREF(0x00efebe6), None, MUTED)
    } else {
        (if pressed { COLORREF(0x00efebe6) } else { WHITE }, Some(BORDER), INK)
    };
    let brush = CreateSolidBrush(background);
    rounded(button.hDC, &button.rcItem, brush, border, if tab { 11 } else { 9 });
    let _ = DeleteObject(brush);
    let mut text = [0u16; 96];
    let count = GetWindowTextW(button.hwndItem, &mut text) as usize;
    let mut rect = button.rcItem;
    let dc = button.hDC;
    let _ = SetBkMode(dc, TRANSPARENT);
    let _ = SetTextColor(dc, color);
    let old = SelectObject(dc, palette().label);
    let _ = DrawTextW(dc, &mut text[..count], &mut rect, DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
    let _ = SelectObject(dc, old);
    if button.itemState.0 & ODS_FOCUS.0 != 0 {
        let _ = InflateRect(&mut rect, -4, -4);
        let _ = DrawFocusRect(dc, &rect);
    }
}

unsafe fn rounded(dc: HDC, rect: &RECT, brush: HBRUSH, border: Option<COLORREF>, radius: i32) {
    let pen = border.map(|color| CreatePen(PS_SOLID, 1, color));
    let previous_brush = SelectObject(dc, brush);
    let previous_pen = SelectObject(dc, pen.map_or_else(|| GetStockObject(NULL_PEN), |pen| HGDIOBJ(pen.0)));
    let _ = RoundRect(dc, rect.left, rect.top, rect.right, rect.bottom, radius, radius);
    let _ = SelectObject(dc, previous_brush);
    let _ = SelectObject(dc, previous_pen);
    if let Some(pen) = pen {
        let _ = DeleteObject(pen);
    }
}

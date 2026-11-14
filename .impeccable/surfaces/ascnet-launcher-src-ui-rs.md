---
version: 1
slug: "ascnet-launcher-src-ui-rs"
primary_target: "AscNet.Launcher/src/ui.rs"
related_targets: ["AscNet.Launcher/proto-slint/ui/app.slint"]
---

# AscNet Launcher (all screens)

Scope: every launcher screen on Windows and macOS (home, Settings, Game files, progress, errors, confirmations, first-run CrossOver/bottle setup on macOS). Mode: Operate.

## Direction contract
THESIS: A game launcher in the style of Kuro's official PGR / Wuthering Waves launchers. The version's art is full-screen and the controls are compact and small. It refuses concept themes, oversized type and panels that cover the art.
OWN-WORLD: The background is the version's animated login art, cover-scaled. Controls sit on small dark plates, rgba(11,12,15,.84), radius 2px. Text is off-white #e9ebef; secondary text is #9aa0aa and tertiary #7d838e. There is one neutral off-white primary button, #eef0f3 with #0b0c0f text, with no version-specific accent. Type is Noto Sans plus the bundled Noto Sans SC/JP/TC/KR, 13px body and 11–12px labels; the largest text is the 16px primary label. Text never sits directly on the art.
STORY: The player always sees which state they are in (game / AscNet patch / local server) and the single next step, explained in plain words. Technical detail lives in the logs.
FIRST VIEWPORT: a brand plate at the top left (AscNet · game name · version chip), and separate minimise/close plates at the top right. At the bottom left, an Activity plate (340px, three timed lines). At the bottom right, a status plate (three rows plus one sentence), the 52px primary button, and a links plate (Game files | Settings).
FORM: canon (the category standard, played straight). seed:c2d90968895c4f4d, resolved by the user taking the canon after two declined hands.
SIGNATURE: none beyond craft. Secondary screens open as a centred dark plate over the dimmed art, never as a full-screen page.
Reference: .impeccable/mocks/decision/official.html / official.png; Slint prototype AscNet.Launcher/proto-slint/.

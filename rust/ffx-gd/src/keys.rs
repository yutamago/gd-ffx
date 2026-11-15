//! Godot's physical key codes as `DirectInput` scan codes (`DIK_*`), the
//! codes the game's keyboard state and its key bindings use (ffx-rust's
//! docs/harness.md, "Input facts": B is Start, the arrows move, Enter and
//! C confirm, X and Backspace cancel, V opens the menu, Esc the PC menu).

use godot::global::{Key, KeyLocation};
use godot::obj::EngineEnum as _;

/// The scan code of Godot's physical key `key` (`location` tells the left
/// and right Shift, Ctrl and Alt apart); `None` for a key the game has no
/// code for.
#[must_use]
#[allow(clippy::too_many_lines)] // one arm per key
pub fn scan_code(key: Key, location: KeyLocation) -> Option<u8> {
    let right = location == KeyLocation::RIGHT;
    let ord = key.ord();
    // letters by their row on a US keyboard
    if (Key::A.ord()..=Key::Z.ord()).contains(&ord) {
        const LETTERS: [u8; 26] = [
            0x1e, 0x30, 0x2e, 0x20, 0x12, 0x21, 0x22, 0x23, 0x17, 0x24, 0x25, 0x26, 0x32, 0x31,
            0x18, 0x19, 0x10, 0x13, 0x1f, 0x14, 0x16, 0x2f, 0x11, 0x2d, 0x15, 0x2c,
        ];
        return usize::try_from(ord - Key::A.ord()).ok().map(|i| LETTERS[i]);
    }
    // 1..9 are 0x02..0x0a, 0 is 0x0b
    if (Key::KEY_1.ord()..=Key::KEY_9.ord()).contains(&ord) {
        return u8::try_from(ord - Key::KEY_1.ord() + 2).ok();
    }
    if (Key::F1.ord()..=Key::F10.ord()).contains(&ord) {
        return u8::try_from(ord - Key::F1.ord() + 0x3b).ok();
    }
    let code = match key {
        Key::KEY_0 => 0x0b,
        Key::ESCAPE => 0x01,
        Key::MINUS => 0x0c,
        Key::EQUAL => 0x0d,
        Key::BACKSPACE => 0x0e,
        Key::TAB => 0x0f,
        Key::BRACKETLEFT => 0x1a,
        Key::BRACKETRIGHT => 0x1b,
        Key::ENTER => 0x1c,
        Key::CTRL if right => 0x9d,
        Key::CTRL => 0x1d,
        Key::SEMICOLON => 0x27,
        Key::APOSTROPHE => 0x28,
        Key::QUOTELEFT => 0x29,
        Key::SHIFT if right => 0x36,
        Key::SHIFT => 0x2a,
        Key::BACKSLASH => 0x2b,
        Key::COMMA => 0x33,
        Key::PERIOD => 0x34,
        Key::SLASH => 0x35,
        Key::KP_MULTIPLY => 0x37,
        Key::ALT if right => 0xb8,
        Key::ALT => 0x38,
        Key::SPACE => 0x39,
        Key::CAPSLOCK => 0x3a,
        Key::NUMLOCK => 0x45,
        Key::SCROLLLOCK => 0x46,
        Key::KP_7 => 0x47,
        Key::KP_8 => 0x48,
        Key::KP_9 => 0x49,
        Key::KP_SUBTRACT => 0x4a,
        Key::KP_4 => 0x4b,
        Key::KP_5 => 0x4c,
        Key::KP_6 => 0x4d,
        Key::KP_ADD => 0x4e,
        Key::KP_1 => 0x4f,
        Key::KP_2 => 0x50,
        Key::KP_3 => 0x51,
        Key::KP_0 => 0x52,
        Key::KP_PERIOD => 0x53,
        Key::F11 => 0x57,
        Key::F12 => 0x58,
        Key::KP_ENTER => 0x9c,
        Key::KP_DIVIDE => 0xb5,
        Key::PRINT => 0xb7,
        Key::PAUSE => 0xc5,
        Key::HOME => 0xc7,
        Key::UP => 0xc8,
        Key::PAGEUP => 0xc9,
        Key::LEFT => 0xcb,
        Key::RIGHT => 0xcd,
        Key::END => 0xcf,
        Key::DOWN => 0xd0,
        Key::PAGEDOWN => 0xd1,
        Key::INSERT => 0xd2,
        Key::DELETE => 0xd3,
        Key::META if right => 0xdc,
        Key::META => 0xdb,
        Key::MENU => 0xdd,
        _ => return None,
    };
    Some(code)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_games_default_keys() {
        let at = |k| scan_code(k, KeyLocation::UNSPECIFIED);
        // Start, confirm, cancel, menu (docs/harness.md "Input facts")
        assert_eq!(at(Key::B), Some(0x30));
        assert_eq!(at(Key::C), Some(0x2e));
        assert_eq!(at(Key::X), Some(0x2d));
        assert_eq!(at(Key::V), Some(0x2f));
        assert_eq!(at(Key::ENTER), Some(0x1c));
        assert_eq!(at(Key::ESCAPE), Some(0x01));
        assert_eq!(at(Key::UP), Some(0xc8));
        assert_eq!(at(Key::DOWN), Some(0xd0));
        assert_eq!(at(Key::LEFT), Some(0xcb));
        assert_eq!(at(Key::RIGHT), Some(0xcd));
        assert_eq!(at(Key::KEY_1), Some(0x02));
        assert_eq!(at(Key::KEY_0), Some(0x0b));
        assert_eq!(at(Key::F10), Some(0x44));
        assert_eq!(scan_code(Key::SHIFT, KeyLocation::RIGHT), Some(0x36));
        assert_eq!(scan_code(Key::SHIFT, KeyLocation::LEFT), Some(0x2a));
    }
}

//! Port of the pure parsers in SteamRip.Core.Launch.HostCapabilities: KDE's kscreen-doctor
//! output, EDID HDR detection, the PCI vendor names and the Proton environment names a script
//! mentions. No probing here, only text and bytes.

use regex::Regex;
use serde::{Deserialize, Serialize};
use std::collections::BTreeSet;
use std::sync::OnceLock;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum HdrState {
    Unknown,
    Incapable,
    Capable,
    Disabled,
    Enabled,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct DisplayInfo {
    pub name: String,
    pub hdr: HdrState,
    pub width: i32,
    pub height: i32,
    pub sdr_nits: i32,
    pub peak_nits: i32,
}

fn re(cell: &'static OnceLock<Regex>, pattern: &str) -> &'static Regex {
    cell.get_or_init(|| Regex::new(pattern).expect("valid display regex"))
}

fn ansi() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    re(&RE, r"\x1b\[[0-9;]*[A-Za-z]")
}

fn kscreen_output() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    re(&RE, r"^Output:\s+\d+\s+(\S+)")
}

fn screen_mode() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    re(&RE, r"(\d{3,5})x(\d{3,5})")
}

fn digits() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    re(&RE, r"\d+")
}

fn proton_var() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    re(&RE, r"\b(PROTON_[A-Z0-9_]+|DXVK_HDR|WINE_FULLSCREEN_FSR)\b")
}

fn strip_ansi(text: &str) -> String {
    ansi().replace_all(text, "").into_owned()
}

fn first_int(line: &str) -> i32 {
    digits()
        .find(line)
        .and_then(|m| m.as_str().parse().ok())
        .unwrap_or(0)
}

fn last_int(line: &str) -> i32 {
    digits()
        .find_iter(line)
        .last()
        .and_then(|m| m.as_str().parse().ok())
        .unwrap_or(0)
}

fn hdr_state(word: &str) -> HdrState {
    match word {
        "enabled" => HdrState::Enabled,
        "disabled" => HdrState::Disabled,
        "incapable" => HdrState::Incapable,
        _ => HdrState::Unknown,
    }
}

/// KDE: kscreen-doctor -o, verified on Plasma 6.
pub fn parse_kscreen(text: &str) -> Vec<DisplayInfo> {
    let mut list: Vec<DisplayInfo> = Vec::new();
    let cleaned = strip_ansi(text);
    for raw in cleaned.split('\n') {
        let line = raw.trim();
        if let Some(caps) = kscreen_output().captures(line) {
            if let Some(last) = list.last().cloned() {
                let _ = last;
            }
            list.push(DisplayInfo {
                name: caps[1].to_string(),
                hdr: HdrState::Unknown,
                width: 0,
                height: 0,
                sdr_nits: 0,
                peak_nits: 0,
            });
            continue;
        }
        let Some(current) = list.last_mut() else {
            continue;
        };
        if let Some(rest) = line.strip_prefix("HDR:") {
            current.hdr = hdr_state(rest.trim());
        } else if let Some(rest) = line.strip_prefix("SDR brightness:") {
            current.sdr_nits = first_int(rest);
        } else if let Some(rest) = line.strip_prefix("Peak brightness:") {
            current.peak_nits = last_int(rest);
        } else if line.starts_with("Geometry:") {
            if let Some(caps) = screen_mode().captures(line) {
                current.width = caps[1].parse().unwrap_or(0);
                current.height = caps[2].parse().unwrap_or(0);
            }
        }
    }
    list
}

/// True when any CTA-861 extension block carries the HDR Static Metadata data block (extended tag 6).
pub fn edid_has_hdr(edid: &[u8]) -> bool {
    let mut block = 128;
    while block + 128 <= edid.len() {
        let b = &edid[block..block + 128];
        if b[0] == 0x02 {
            let dtd_start = b[2] as usize;
            let mut i = 4;
            while i < dtd_start && i < 127 {
                let tag = b[i] >> 5;
                let len = (b[i] & 0x1f) as usize;
                if tag == 7 && len >= 1 && b[i + 1] == 6 {
                    return true;
                }
                i += len + 1;
            }
        }
        block += 128;
    }
    false
}

/// PCI vendor id to a name worth showing.
pub fn vendor_name(pci_vendor: &str) -> String {
    match pci_vendor.trim().to_lowercase().as_str() {
        "0x1002" => "AMD".to_string(),
        "0x10de" => "NVIDIA".to_string(),
        "0x8086" => "Intel".to_string(),
        other => other.to_string(),
    }
}

/// Every Proton-related environment name a script mentions.
pub fn proton_env_vars(script: &str) -> BTreeSet<String> {
    proton_var()
        .find_iter(script)
        .map(|m| m.as_str().to_string())
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reads_a_single_kscreen_output() {
        let text = "Output: 1 HDMI-A-2\nHDR: incapable\nGeometry: 0,0 1920x1080";
        let displays = parse_kscreen(text);
        assert_eq!(displays.len(), 1);
        assert_eq!(displays[0].name, "HDMI-A-2");
        assert_eq!(displays[0].hdr, HdrState::Incapable);
        assert_eq!(displays[0].width, 1920);
    }

    #[test]
    fn vendor_names_are_lowercased_first() {
        assert_eq!(vendor_name("0x10DE"), "NVIDIA");
        assert_eq!(vendor_name(" 0x1002 "), "AMD");
        assert_eq!(vendor_name("amd"), "amd");
    }
}

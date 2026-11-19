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

fn mutter_monitor() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    re(&RE, r"\(\('([^']+)', '[^']*', '[^']*', '[^']*'\)")
}

fn mutter_supported() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    re(&RE, r"'supported-color-modes':\s*<\[([^\]]*)\]>")
}

fn mutter_color_mode() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    re(&RE, r"'color-mode':\s*<uint32 (\d+)>")
}

fn mutter_current_mode() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    re(
        &RE,
        r"\('[^']*',\s*(\d+),\s*(\d+),\s*[\d.]+,\s*[\d.]+,\s*\[[^\]]*\],\s*\{[^}]*'is-current':\s*<true>",
    )
}

/// GNOME/mutter: the GVariant text gdbus prints for DisplayConfig.GetCurrentState.
pub fn parse_mutter(text: &str) -> Vec<DisplayInfo> {
    let heads: Vec<(usize, String)> = mutter_monitor()
        .captures_iter(text)
        .filter_map(|c| Some((c.get(0)?.start(), c[1].to_string())))
        .collect();
    let mut list = Vec::new();
    for (i, (start, name)) in heads.iter().enumerate() {
        let end = heads.get(i + 1).map(|(s, _)| *s).unwrap_or(text.len());
        let seg = &text[*start..end];
        let supported = mutter_supported().captures(seg);
        let mode = mutter_color_mode().captures(seg);
        let hdr = match &supported {
            None => HdrState::Unknown,
            Some(sup) => {
                let hdr_listed = sup[1]
                    .split(',')
                    .map(|v| v.replace("uint32", "").trim().to_string())
                    .any(|v| v == "1");
                if !hdr_listed {
                    HdrState::Incapable
                } else {
                    match &mode {
                        Some(m) if &m[1] == "1" => HdrState::Enabled,
                        Some(_) => HdrState::Disabled,
                        None => HdrState::Capable,
                    }
                }
            }
        };
        let (width, height) = match mutter_current_mode().captures(seg) {
            Some(m) => (
                m[1].parse::<i32>().unwrap_or(0),
                m[2].parse::<i32>().unwrap_or(0),
            ),
            None => (0, 0),
        };
        list.push(DisplayInfo {
            name: name.clone(),
            hdr,
            width,
            height,
            sdr_nits: 0,
            peak_nits: 0,
        });
    }
    list
}

/// Hyprland `hyprctl monitors -j` and sway `swaymsg -t get_outputs`.
pub fn parse_wlr_outputs(json: &str) -> Vec<DisplayInfo> {
    if json.trim().is_empty() {
        return Vec::new();
    }
    let Ok(value) = serde_json::from_str::<serde_json::Value>(json) else {
        return Vec::new();
    };
    let Some(outputs) = value.as_array() else {
        return Vec::new();
    };
    outputs
        .iter()
        .filter_map(|o| {
            let name = o.get("name")?.as_str()?.to_string();
            let hdr = match o.get("hdr") {
                Some(serde_json::Value::Bool(true)) => HdrState::Enabled,
                Some(serde_json::Value::Bool(false)) => HdrState::Disabled,
                _ => HdrState::Unknown,
            };
            let dimension = |value: &serde_json::Value, key: &str| {
                value
                    .get(key)
                    .and_then(|v| v.as_i64())
                    .map(|v| v as i32)
                    .unwrap_or(0)
            };
            let (width, height) = if o.get("width").map(|v| v.is_number()).unwrap_or(false) {
                (dimension(o, "width"), dimension(o, "height"))
            } else if let Some(mode) = o.get("current_mode") {
                (dimension(mode, "width"), dimension(mode, "height"))
            } else {
                (0, 0)
            };
            Some(DisplayInfo {
                name,
                hdr,
                width,
                height,
                sdr_nits: 0,
                peak_nits: 0,
            })
        })
        .collect()
}

/// One GPU as vulkaninfo describes it.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct GpuInfo {
    pub vendor: String,
    pub driver: Option<String>,
    pub driver_info: Option<String>,
    pub driver_version: Option<String>,
}

fn vulkan_header() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    re(&RE, r"(?m)^GPU\d+:")
}

fn vulkan_field() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    re(&RE, r"(?m)^\s*(\w+)\s*=\s*(.+)$")
}

/// vulkaninfo --summary: the discrete GPU when there is one, else the first device.
pub fn parse_vulkan_info(text: &str) -> Option<GpuInfo> {
    if text.trim().is_empty() {
        return None;
    }
    let blocks: Vec<&str> = vulkan_header().split(text).skip(1).collect();
    let block = blocks
        .iter()
        .find(|b| b.contains("DISCRETE_GPU"))
        .copied()
        .or_else(|| blocks.first().copied())?;
    let mut fields: std::collections::BTreeMap<String, String> = std::collections::BTreeMap::new();
    for caps in vulkan_field().captures_iter(block) {
        let key = caps[1].to_string();
        let value = caps[2].trim().to_string();
        fields.entry(key).or_insert(value);
    }
    Some(GpuInfo {
        vendor: fields
            .get("vendorID")
            .map(|v| vendor_name(v))
            .unwrap_or_else(|| "unknown".to_string()),
        driver: fields.get("driverName").cloned(),
        driver_info: fields.get("driverInfo").cloned(),
        driver_version: fields.get("driverVersion").cloned(),
    })
}

/// The biggest mode across a set of DRM modes texts, or None when none has one.
pub fn largest_screen_of(modes_texts: &[String]) -> Option<(i32, i32)> {
    let mut best: Option<(i32, i32)> = None;
    for modes in modes_texts {
        let Some(caps) = screen_mode().captures(modes) else {
            continue;
        };
        let width: i32 = caps[1].parse().ok()?;
        let height: i32 = caps[2].parse().ok()?;
        best = match best {
            Some((bw, bh)) if bw * bh >= width * height => Some((bw, bh)),
            _ => Some((width, height)),
        };
    }
    best
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

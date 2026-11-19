//! Port of `SteamRip.Core.Catalog.TitleParser`.
//!
//! The C# original decodes HTML entities, then splits "Game Name Free Download (v1.2.3)"
//! into a name and an optional version. When the title does not match, the whole string
//! (still entity-decoded and trimmed) is the name and the version is `None`.

use regex::Regex;
use std::sync::OnceLock;

fn title_regex() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    RE.get_or_init(|| {
        Regex::new(
            r"(?i)^(?P<name>.+?)\s+Free\s+Downl?oad(?:\s+[^()]*?)?\s*(?:\((?P<ver>[^)]*)\))?\s*$",
        )
        .expect("title regex is valid")
    })
}

/// Splits a catalog post title into the game name and the version text, if any.
pub fn parse(raw_title: &str) -> (String, Option<String>) {
    let title = html_escape::decode_html_entities(raw_title)
        .trim()
        .to_string();
    let Some(caps) = title_regex().captures(&title) else {
        // Unparseable title (e.g. bracketed dual versions): keep it whole, no version.
        return (title, None);
    };
    let name = caps
        .name("name")
        .map(|m| m.as_str().trim())
        .unwrap_or_default();
    let version = caps
        .name("ver")
        .map(|m| m.as_str().trim().to_string())
        .filter(|v| !v.is_empty());
    (name.to_string(), version)
}

#[cfg(test)]
mod tests {
    use super::parse;

    #[test]
    fn splits_name_and_version() {
        assert_eq!(
            parse("Mortal Shell II Free Download (v98314+1943)"),
            (
                "Mortal Shell II".to_string(),
                Some("v98314+1943".to_string())
            )
        );
        assert_eq!(
            parse("Hull Rupture Free Download"),
            ("Hull Rupture".to_string(), None)
        );
        assert_eq!(
            parse("Some Game Free Download (v1.2 + ALL DLCs)"),
            ("Some Game".to_string(), Some("v1.2 + ALL DLCs".to_string()))
        );
    }

    #[test]
    fn decodes_entities_before_splitting() {
        assert_eq!(
            parse("R&#8217;s Quest Free Download (v2)"),
            ("R\u{2019}s Quest".to_string(), Some("v2".to_string()))
        );
    }

    #[test]
    fn keeps_unparseable_titles_whole() {
        let (name, version) = parse("Dual Pack [v1.0 & v1.1]");
        assert_eq!(name, "Dual Pack [v1.0 & v1.1]");
        assert_eq!(version, None);
    }
}

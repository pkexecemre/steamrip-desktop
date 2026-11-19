pub mod parse_one;
pub mod size;
pub mod title_parser;
pub mod title_prefix;

/// Post ids in rank order from one page of the site's /top-games/ listing.
pub fn parse_top_games(html: &str) -> Vec<i64> {
    use regex::Regex;
    use std::sync::OnceLock;
    static RE: OnceLock<Regex> = OnceLock::new();
    let regex = RE.get_or_init(|| {
        Regex::new(r#"class="post-item post-(\d+)\b"#).expect("valid top-games regex")
    });
    let mut ids: Vec<i64> = Vec::new();
    for caps in regex.captures_iter(html) {
        if let Ok(id) = caps[1].parse::<i64>() {
            if !ids.contains(&id) {
                ids.push(id);
            }
        }
    }
    ids
}

/// One mirror link from a post body.
#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
pub struct Mirror {
    pub host: crate::hoster::Hoster,
    pub url: String,
}

fn mirror_anchor() -> &'static regex::Regex {
    use regex::Regex;
    use std::sync::OnceLock;
    static RE: OnceLock<Regex> = OnceLock::new();
    RE.get_or_init(|| {
        Regex::new(r"(?i)<a\b[^>]*\bshortc-button\b[^>]*>").expect("valid anchor regex")
    })
}

fn href_in(anchor: &str) -> Option<String> {
    use regex::Regex;
    use std::sync::OnceLock;
    static RE: OnceLock<Regex> = OnceLock::new();
    let regex = RE.get_or_init(|| Regex::new(r#"(?i)href="([^"]+)""#).expect("valid href regex"));
    regex.captures(anchor).map(|c| c[1].to_string())
}

fn game_size_regex() -> &'static regex::Regex {
    use regex::Regex;
    use std::sync::OnceLock;
    static RE: OnceLock<Regex> = OnceLock::new();
    RE.get_or_init(|| {
        Regex::new(r"(?i)Game Size:\s*</strong>\s*([0-9.,]+\s*[KMGT]B)")
            .expect("valid game size regex")
    })
}

/// Mirror links in a post body: normalized, de-duplicated by url, without FileCrypt wrappers.
pub fn mirrors_from_html(content: &str) -> Vec<Mirror> {
    let mut mirrors: Vec<Mirror> = Vec::new();
    for anchor in mirror_anchor().find_iter(content) {
        let Some(raw) = href_in(anchor.as_str()) else {
            continue;
        };
        let url = crate::hoster::normalize(&html_escape::decode_html_entities(&raw));
        if mirrors.iter().any(|m| m.url == url) {
            continue;
        }
        let host = crate::hoster::detect(&url);
        if host == crate::hoster::Hoster::FileCrypt {
            continue;
        }
        mirrors.push(Mirror { host, url });
    }
    mirrors
}

/// The "Game Size" line inside content.rendered, as the site prints it.
pub fn game_size_text(content: &str) -> Option<String> {
    game_size_regex()
        .captures(content)
        .map(|c| c[1].trim().to_string())
}

/// One WordPress category: the genre list the site publishes.
#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
pub struct Category {
    pub id: i64,
    pub name: String,
    pub count: i32,
}

/// /wp/v2/categories JSON array -> categories, names HTML-decoded, missing counts as zero.
pub fn parse_categories(json: &str) -> Vec<Category> {
    let Ok(value) = serde_json::from_str::<serde_json::Value>(json) else {
        return Vec::new();
    };
    let Some(items) = value.as_array() else {
        return Vec::new();
    };
    items
        .iter()
        .filter_map(|item| {
            let id = item.get("id")?.as_i64()?;
            let raw = item
                .get("name")
                .and_then(|n| n.as_str())
                .unwrap_or_default();
            let name = html_escape::decode_html_entities(raw).into_owned();
            let count = item.get("count").and_then(|c| c.as_i64()).unwrap_or(0) as i32;
            Some(Category { id, name, count })
        })
        .collect()
}

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

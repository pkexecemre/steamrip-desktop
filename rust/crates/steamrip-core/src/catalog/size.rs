//! Port of `CatalogParser.ParseSize`: "12.3 GB" -> bytes, decimal units like the site.

use regex::Regex;
use std::sync::OnceLock;

fn size_regex() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    RE.get_or_init(|| {
        Regex::new(r"(?i)^\s*([0-9]+(?:[.,][0-9]+)?)\s*([KMGT])B\s*$").expect("size regex is valid")
    })
}

/// Bytes for a size line, or `None` when the text does not look like one.
///
/// `.NET`'s `Math.Round` rounds half to even, so this uses `round_ties_even` to stay
/// byte-identical on the recorded cases.
pub fn parse_size(size_text: Option<&str>) -> Option<i64> {
    let text = size_text?;
    let caps = size_regex().captures(text)?;
    let number: f64 = caps.get(1)?.as_str().replace(',', ".").parse().ok()?;
    let scale = match caps.get(2)?.as_str().chars().next()?.to_ascii_uppercase() {
        'K' => 1e3,
        'M' => 1e6,
        'G' => 1e9,
        _ => 1e12,
    };
    Some((number * scale).round_ties_even() as i64)
}

#[cfg(test)]
mod tests {
    use super::parse_size;

    #[test]
    fn parses_decimal_units() {
        assert_eq!(parse_size(Some("12.3 GB")), Some(12_300_000_000));
        assert_eq!(parse_size(Some("750 MB")), Some(750_000_000));
        assert_eq!(parse_size(Some("1,5 TB")), Some(1_500_000_000_000));
        assert_eq!(parse_size(Some("900 KB")), Some(900_000));
    }

    #[test]
    fn rejects_anything_else() {
        assert_eq!(parse_size(Some("unknown")), None);
        assert_eq!(parse_size(None), None);
    }
}

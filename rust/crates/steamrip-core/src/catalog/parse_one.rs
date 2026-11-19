//! Port of `CatalogParser.ParseOne`: one WordPress REST post -> every `CatalogPost` field.
//!
//! The leaves are ported next door (`title_parser`, `size`, `game_size_text`,
//! `mirrors_from_html`, `dates::parse_utc_text`); this is the composition. Two original
//! behaviours are kept rather than smoothed over: a post without `link` throws, and an
//! unreadable date becomes `DateTime.MinValue` (unix -62135596800), not `None`. So does a
//! `categories` array with any non-number entry: C#'s `TryGetInt64` throws for a string or
//! null element instead of skipping it.

use serde::{Deserialize, Serialize};
use serde_json::Value;

use super::{game_size_text, mirrors_from_html, size::parse_size, title_parser, Mirror};
use crate::dates::parse_utc_text;

/// C# `DateTime.MinValue` (0001-01-01T00:00:00Z) as unix seconds.
pub const MIN_DATE_UNIX: i64 = -62_135_596_800;

/// One steamrip.com post (one game), composed from its REST JSON.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct CatalogPost {
    pub id: i64,
    pub slug: String,
    pub url: String,
    pub title: String,
    pub name: String,
    pub title_version: Option<String>,
    pub build: Option<String>,
    pub steam_app_id: Option<i64>,
    pub modified_utc: i64,
    pub published_utc: i64,
    pub size_text: Option<String>,
    pub mirrors: Vec<Mirror>,
    pub size_bytes: Option<i64>,
    pub category_ids: Vec<i64>,
}

/// Why one post could not be composed. The C# original surfaces these as
/// `KeyNotFoundException` (missing `title`/`rendered`/`link`) or `InvalidOperationException`
/// (a wrongly typed field, or a non-number category entry); the variants name the field.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ParseOneError {
    InvalidId,
    MissingTitle,
    InvalidTitle,
    InvalidContent,
    MissingLink,
    InvalidLink,
    InvalidSlug,
    InvalidCategory,
}

/// One post, or `None` when it has no `id` (the original returns null there).
pub fn parse_one(post: &Value) -> Result<Option<CatalogPost>, ParseOneError> {
    let Some(id_element) = post.get("id") else {
        return Ok(None);
    };

    let Some(title_object) = post.get("title") else {
        return Err(ParseOneError::MissingTitle);
    };
    let Some(rendered_title) = title_object.get("rendered") else {
        return Err(ParseOneError::MissingTitle);
    };
    let raw_title = match rendered_title {
        Value::Null => "",
        Value::String(text) => text.as_str(),
        _ => return Err(ParseOneError::InvalidTitle),
    };

    let content = match post
        .get("content")
        .and_then(|content| content.get("rendered"))
    {
        None | Some(Value::Null) => "",
        Some(Value::String(text)) => text.as_str(),
        Some(_) => return Err(ParseOneError::InvalidContent),
    };

    let acf = match post.get("acf") {
        Some(Value::Object(object)) => Some(object),
        _ => None,
    };
    let build = acf
        .and_then(|object| object.get("tagmetafield"))
        .and_then(Value::as_str)
        .map(str::trim)
        .filter(|text| !text.is_empty())
        .map(str::to_string);
    let steam_app_id = acf
        .and_then(|object| object.get("steam_app_id"))
        .and_then(parse_steam_app_id);

    let mirrors = mirrors_from_html(content);
    let size_text = game_size_text(content);
    let size_bytes = parse_size(size_text.as_deref());

    let category_ids = match post.get("categories") {
        Some(Value::Array(entries)) => {
            let mut ids = Vec::with_capacity(entries.len());
            for entry in entries {
                match entry {
                    Value::Number(number) => {
                        if let Some(id) = number.as_i64() {
                            ids.push(id);
                        }
                    }
                    _ => return Err(ParseOneError::InvalidCategory),
                }
            }
            ids
        }
        _ => Vec::new(),
    };

    let id = id_element.as_i64().ok_or(ParseOneError::InvalidId)?;
    let slug = match post.get("slug") {
        None | Some(Value::Null) => "",
        Some(Value::String(text)) => text.as_str(),
        Some(_) => return Err(ParseOneError::InvalidSlug),
    }
    .to_string();
    let url = match post.get("link") {
        None => return Err(ParseOneError::MissingLink),
        Some(Value::Null) => "",
        Some(Value::String(text)) => text.as_str(),
        Some(_) => return Err(ParseOneError::InvalidLink),
    }
    .to_string();

    let (name, title_version) = title_parser::parse(raw_title);

    Ok(Some(CatalogPost {
        id,
        slug,
        url,
        title: html_escape::decode_html_entities(raw_title).into_owned(),
        name,
        title_version,
        build,
        steam_app_id,
        modified_utc: parse_utc(post, &["modified_gmt", "modified"]),
        published_utc: parse_utc(post, &["date_gmt", "date"]),
        size_text,
        mirrors,
        size_bytes,
        category_ids,
    }))
}

/// `long.TryParse` under `NumberStyles.Integer`: surrounding whitespace and an optional sign.
fn parse_steam_app_id(value: &Value) -> Option<i64> {
    let parsed = match value {
        Value::Number(number) => number.as_i64(),
        Value::String(text) => text.trim().parse::<i64>().ok(),
        _ => None,
    };
    parsed.filter(|id| *id > 0)
}

/// The first parseable timestamp in `names`, as unix seconds; `DateTime.MinValue` when none is.
fn parse_utc(post: &Value, names: &[&str]) -> i64 {
    for name in names {
        let Some(Value::String(text)) = post.get(*name) else {
            continue;
        };
        if let Some(unix) = parse_utc_text(text) {
            return unix;
        }
    }
    MIN_DATE_UNIX
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::hoster::Hoster;
    use serde_json::json;

    fn post_with(acf: Value, categories: Value) -> Value {
        json!({
            "id": 7,
            "slug": "some-game",
            "link": "https://steamrip.com/some-game/",
            "title": { "rendered": "Tom &amp; Jerry Free Download (v1.2)" },
            "content": {
                "rendered": "<p><strong>Game Size:</strong> 12.3 GB</p>\
        <a class=\"shortc-button\" href=\"https://fileditch.com/f/x.rar\">FD</a>"
            },
            "acf": acf,
            "categories": categories,
            "modified_gmt": "2026-11-06T15:29:26",
            "date_gmt": "2026-08-18T17:02:33"
        })
    }

    #[test]
    fn composes_every_field_from_one_post() {
        let post = post_with(
            json!({ "tagmetafield": "  Build 26315250  ", "steam_app_id": 2584270 }),
            json!([26, 33, 1538]),
        );
        let parsed = parse_one(&post).expect("valid post").expect("has an id");

        assert_eq!(parsed.id, 7);
        assert_eq!(parsed.slug, "some-game");
        assert_eq!(parsed.url, "https://steamrip.com/some-game/");
        assert_eq!(parsed.title, "Tom & Jerry Free Download (v1.2)");
        assert_eq!(parsed.name, "Tom & Jerry");
        assert_eq!(parsed.title_version.as_deref(), Some("v1.2"));
        assert_eq!(parsed.build.as_deref(), Some("Build 26315250"));
        assert_eq!(parsed.steam_app_id, Some(2584270));
        assert_eq!(parsed.modified_utc, 1_793_978_966);
        assert_eq!(parsed.published_utc, 1_787_072_553);
        assert_eq!(parsed.size_text.as_deref(), Some("12.3 GB"));
        assert_eq!(parsed.size_bytes, Some(12_300_000_000));
        assert_eq!(parsed.category_ids, vec![26, 33, 1538]);
        assert_eq!(parsed.mirrors.len(), 1);
        assert_eq!(parsed.mirrors[0].host, Hoster::FileDitch);
        assert_eq!(parsed.mirrors[0].url, "https://fileditch.com/f/x.rar");
    }

    #[test]
    fn steam_app_id_takes_only_positive_numbers_and_numeric_strings() {
        let cases = [
            (json!(730), Some(730)),
            (json!("730"), Some(730)),
            (json!("  730  "), Some(730)),
            (json!(0), None),
            (json!(-1), None),
            (json!("-42"), None),
            (json!("not-a-number"), None),
            (json!(1.5), None),
            (json!(true), None),
            (json!(null), None),
            (json!("99999999999999999999"), None),
        ];
        for (value, expected) in cases {
            let post = post_with(json!({ "steam_app_id": value }), json!([]));
            let parsed = parse_one(&post).expect("valid post").expect("has an id");
            assert_eq!(parsed.steam_app_id, expected, "app id for {value}");
        }
    }

    #[test]
    fn tagmetafield_is_trimmed_and_blank_becomes_none() {
        for (value, expected) in [
            (json!("Build 1"), Some("Build 1")),
            (json!("  Build 1  "), Some("Build 1")),
            (json!(""), None),
            (json!("   "), None),
            (json!(null), None),
            (json!(42), None),
        ] {
            let post = post_with(json!({ "tagmetafield": value }), json!([]));
            let parsed = parse_one(&post).expect("valid post").expect("has an id");
            assert_eq!(parsed.build.as_deref(), expected, "build for {value}");
        }
    }

    #[test]
    fn categories_skip_non_integral_numbers_but_throw_on_other_kinds() {
        let post = post_with(Value::Null, json!([26, 33.0, 4.5, 63]));
        let parsed = parse_one(&post).expect("valid post").expect("has an id");
        assert_eq!(parsed.category_ids, vec![26, 63]);

        let string_entry = post_with(Value::Null, json!([26, "27", 63]));
        assert_eq!(
            parse_one(&string_entry),
            Err(ParseOneError::InvalidCategory)
        );

        let null_entry = post_with(Value::Null, json!([26, null, 63]));
        assert_eq!(parse_one(&null_entry), Err(ParseOneError::InvalidCategory));

        let no_array = post_with(Value::Null, Value::Null);
        assert_eq!(
            parse_one(&no_array)
                .expect("valid")
                .expect("id")
                .category_ids,
            Vec::<i64>::new()
        );
    }

    #[test]
    fn missing_id_is_none_and_missing_link_throws() {
        let mut no_id = post_with(Value::Null, json!([]));
        no_id.as_object_mut().expect("object").remove("id");
        assert_eq!(parse_one(&no_id), Ok(None));

        let mut no_link = post_with(Value::Null, json!([]));
        no_link.as_object_mut().expect("object").remove("link");
        assert_eq!(parse_one(&no_link), Err(ParseOneError::MissingLink));
    }

    #[test]
    fn unreadable_dates_become_the_minimum_date_and_gmt_fields_fall_back() {
        let mut post = post_with(Value::Null, json!([]));
        {
            let object = post.as_object_mut().expect("object");
            object.insert("modified_gmt".into(), json!("not a date"));
            object.insert("date_gmt".into(), json!(""));
        }
        let parsed = parse_one(&post).expect("valid post").expect("has an id");
        assert_eq!(parsed.modified_utc, MIN_DATE_UNIX);
        assert_eq!(parsed.published_utc, MIN_DATE_UNIX);

        let mut fallback = post_with(Value::Null, json!([]));
        {
            let object = fallback.as_object_mut().expect("object");
            object.insert("modified_gmt".into(), json!("not a date"));
            object.insert("modified".into(), json!("2026-02-03T04:05:06"));
            object.insert("date_gmt".into(), json!(123));
            object.insert("date".into(), json!("2026-01-01T00:00:00Z"));
        }
        let parsed = parse_one(&fallback)
            .expect("valid post")
            .expect("has an id");
        assert_eq!(parsed.modified_utc, 1_770_091_506);
        assert_eq!(parsed.published_utc, 1_767_225_600);
    }

    #[test]
    fn absent_content_yields_no_mirrors_and_no_size() {
        let mut post = post_with(Value::Null, json!([]));
        post.as_object_mut().expect("object").remove("content");
        let parsed = parse_one(&post).expect("valid post").expect("has an id");
        assert!(parsed.mirrors.is_empty());
        assert_eq!(parsed.size_text, None);
        assert_eq!(parsed.size_bytes, None);
    }
}

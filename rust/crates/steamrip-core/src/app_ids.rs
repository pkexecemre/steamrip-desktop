//! Port of the app-id trust and store-search rules in
//! `SteamRip.Core.Catalog.SteamAppIdResolver`: which words a title contributes, whether the
//! site's claimed app id has a store name close enough to trust, and which `storesearch` hit
//! becomes the id.
//!
//! HTTP, caching and logging stay on the C# side; this module takes names and search hits as data.
//! The C# token regex is `[a-z0-9]+` with IgnoreCase and `ToLowerInvariant`; `str::to_lowercase`
//! is used here, which differs only for characters outside the recorded ASCII titles.

use regex::Regex;
use std::collections::HashSet;
use std::sync::OnceLock;

/// Trademark and edition noise that must not decide whether two names are the same game.
const STOPWORDS: [&str; 23] = [
    "the",
    "and",
    "for",
    "of",
    "a",
    "an",
    "to",
    "in",
    "on",
    "pc",
    "free",
    "download",
    "edition",
    "deluxe",
    "ultimate",
    "complete",
    "goty",
    "game",
    "remastered",
    "remake",
    "definitive",
    "enhanced",
    "year",
];

fn word() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    RE.get_or_init(|| Regex::new(r"(?i)[a-z0-9]+").expect("valid token regex"))
}

/// Significant tokens: words longer than two characters, lowercased, without stopwords.
pub fn tokens(value: &str) -> HashSet<String> {
    word()
        .find_iter(value)
        .map(|m| m.as_str().to_lowercase())
        .filter(|token| token.chars().count() > 2 && !STOPWORDS.contains(&token.as_str()))
        .collect()
}

/// Overlap needed: most of the shorter token set, rounded up, at least one token.
fn needed(count: usize) -> usize {
    (((count as f64) * 0.6).ceil() as usize).max(1)
}

/// True when the names share enough significant tokens for the claimed id to be trusted.
pub fn names_match(game_name: &str, steam_name: &str) -> bool {
    let a = tokens(game_name);
    let b = tokens(steam_name);
    if a.is_empty() || b.is_empty() {
        return false;
    }
    let overlap = a.intersection(&b).count();
    overlap >= needed(a.len().min(b.len()))
}

/// Best storesearch hit for `game_name`, or none. Hits are scored by overlap first and by shorter
/// store names second, so the base game wins over a DLC; ties keep the first hit.
pub fn pick_search_match(game_name: &str, hits: &[(i64, &str)]) -> Option<i64> {
    let want = tokens(game_name);
    if want.is_empty() {
        return None;
    }
    let need = needed(want.len());
    let mut best: Option<(i64, i32)> = None;
    for &(id, name) in hits {
        let got = tokens(name);
        let overlap = want.intersection(&got).count();
        if overlap == 0 || overlap < need {
            continue;
        }
        let score = overlap as i32 * 100 - got.len() as i32;
        match best {
            Some((_, best_score)) if score <= best_score => {}
            _ => best = Some((id, score)),
        }
    }
    best.map(|(id, _)| id)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn set(items: &[&str]) -> HashSet<String> {
        items.iter().map(|item| (*item).to_string()).collect()
    }

    #[test]
    fn tokens_lowercase_and_drop_stopwords_and_short_words() {
        assert_eq!(tokens("The Last of Us Part II"), set(&["last", "part"]));
        assert_eq!(
            tokens("Cyberpunk 2077: Phantom Liberty"),
            set(&["2077", "cyberpunk", "liberty", "phantom"])
        );
        assert!(tokens("Deluxe GOTY Remastered Edition").is_empty());
    }

    #[test]
    fn typographic_and_straight_apostrophes_tokenize_the_same() {
        assert_eq!(tokens("Lara’s Quest"), tokens("Lara's Quest"));
        assert_eq!(tokens("Lara’s Quest"), set(&["lara", "quest"]));
        assert_eq!(tokens("Tom & Jerry"), set(&["jerry", "tom"]));
    }

    #[test]
    fn non_ascii_letters_separate_tokens() {
        assert_eq!(tokens("Türkçe Oyun Adı"), set(&["oyun"]));
    }

    #[test]
    fn names_match_ignores_edition_and_trademark_noise() {
        assert!(names_match(
            "The Last of Us Part II",
            "The Last of Us Part II Remastered"
        ));
        assert!(names_match(
            "The Witcher 3: Wild Hunt",
            "The Witcher 3: Wild Hunt - Complete Edition"
        ));
        assert!(names_match("Hades", "Hades Deluxe Edition"));
        assert!(names_match("Fallout 4 GOTY", "Fallout 4"));
        assert!(!names_match("Cyberpunk 2077", "The Witcher 3: Wild Hunt"));
    }

    #[test]
    fn names_match_needs_sixty_percent_of_the_shorter_name() {
        // Three tokens need ceil(1.8) = 2 shared tokens.
        assert!(names_match("Alpha Beta Gamma", "Alpha Beta Delta"));
        assert!(!names_match("Alpha Beta Gamma", "Alpha Delta Epsilon"));
        // Four tokens need ceil(2.4) = 3 shared tokens.
        assert!(!names_match(
            "Alpha Beta Gamma Delta",
            "Alpha Beta Zeta Epsilon"
        ));
        assert!(names_match(
            "Alpha Beta Gamma Delta",
            "Alpha Beta Gamma Epsilon"
        ));
    }

    #[test]
    fn a_name_without_tokens_never_matches() {
        assert!(!names_match("", "Prey"));
        assert!(!names_match("The Game", "Prey"));
        assert!(!names_match("!!!", "Prey"));
    }

    #[test]
    fn base_game_beats_a_dlc_and_a_loose_sibling() {
        let hits = [
            (101, "Elden Ring: Shadow of the Erdtree"),
            (102, "Elden Ring"),
            (103, "Elden Ring Nightreign"),
        ];
        assert_eq!(pick_search_match("Elden Ring", &hits), Some(102));
    }

    #[test]
    fn ties_keep_the_first_hit() {
        assert_eq!(
            pick_search_match("Doom", &[(201, "Doom 2"), (202, "Doom")]),
            Some(201)
        );
        assert_eq!(
            pick_search_match("Doom", &[(202, "Doom"), (201, "Doom 2")]),
            Some(202)
        );
    }

    #[test]
    fn a_hit_needs_sixty_percent_of_the_query() {
        assert_eq!(
            pick_search_match("Alpha Beta Gamma Delta", &[(501, "Alpha Beta")]),
            None
        );
        assert_eq!(pick_search_match("Prey", &[(601, "Pray")]), None);
    }

    #[test]
    fn a_stopword_query_picks_nothing() {
        assert_eq!(
            pick_search_match("The GOTY Edition", &[(601, "Prey")]),
            None
        );
        assert_eq!(pick_search_match("", &[(601, "Prey")]), None);
    }

    #[test]
    fn a_qualifying_dlc_is_taken_when_the_base_game_is_absent() {
        assert_eq!(
            pick_search_match("Hollow Knight", &[(401, "Hollow Knight: Silksong")]),
            Some(401)
        );
        assert_eq!(
            pick_search_match("Control", &[(301, "Control Deluxe Edition")]),
            Some(301)
        );
    }

    #[test]
    fn score_prefers_overlap_then_the_shorter_name() {
        let hits = [
            (701, "Alpha Beta Gamma Delta"),
            (702, "Alpha Beta Gamma"),
            (703, "Alpha Beta Gamma Epsilon Zeta"),
        ];
        assert_eq!(pick_search_match("Alpha Beta Gamma", &hits), Some(702));
    }
}

//! Port of `SteamAppIdResolver.TitlePrefix`: the shorter search query before a colon or
//! " - ", when it still says something.

/// Text before the first `:` or ` - `, when it leaves a useful shorter query.
pub fn title_prefix(game_name: &str) -> Option<String> {
    let s = game_name.trim();
    let colon = s.find(':');
    let dash = s.find(" - ");
    let at = match (colon, dash) {
        (None, None) => return None,
        (Some(c), None) => c,
        (None, Some(d)) => d,
        (Some(c), Some(d)) => c.min(d),
    };
    // The C# original compares a UTF-16 index against 3; count characters instead so
    // non-ASCII names take the same branch.
    if s[..at].chars().count() < 3 {
        return None;
    }
    let prefix = s[..at].trim();
    let useful = prefix.chars().count() >= 3 && prefix.to_lowercase() != s.to_lowercase();
    useful.then(|| prefix.to_string())
}

#[cfg(test)]
mod tests {
    use super::title_prefix;

    #[test]
    fn cuts_at_subtitle_markers() {
        assert_eq!(
            title_prefix("LEGO Batman: Legacy of the Dark Knight").as_deref(),
            Some("LEGO Batman")
        );
        assert_eq!(
            title_prefix("Cool Game - Ultimate Edition").as_deref(),
            Some("Cool Game")
        );
    }

    #[test]
    fn refuses_when_there_is_nothing_to_gain() {
        assert_eq!(title_prefix("NoSubtitle"), None);
        assert_eq!(title_prefix("Ab: cd"), None);
    }
}

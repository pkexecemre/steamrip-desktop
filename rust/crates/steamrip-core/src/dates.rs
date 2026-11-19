//! Port of CatalogParser.ParseUtcText: one timestamp from the REST payload as unix seconds.
//!
//! The C# side leans on DateTime.TryParse with InvariantCulture and AssumeUniversal, so a value
//! without an offset is UTC. Only the shapes the site and the tests use are accepted here;
//! anything else is None, which is what the recordings expect.

fn days_from_civil(year: i64, month: i64, day: i64) -> i64 {
    let y = if month <= 2 { year - 1 } else { year };
    let era = if y >= 0 { y } else { y - 399 } / 400;
    let yoe = y - era * 400;
    let mp = (month + 9) % 12;
    let doy = (153 * mp + 2) / 5 + day - 1;
    let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    era * 146097 + doe - 719468
}

fn days_in_month(year: i64, month: i64) -> i64 {
    match month {
        1 | 3 | 5 | 7 | 8 | 10 | 12 => 31,
        4 | 6 | 9 | 11 => 30,
        2 => {
            let leap = (year % 4 == 0 && year % 100 != 0) || year % 400 == 0;
            if leap {
                29
            } else {
                28
            }
        }
        _ => 0,
    }
}

fn parse_clock(text: &str) -> Option<(i64, i64, i64, i64)> {
    // HH:MM[:SS][.frac][Z|+HH:MM|-HH:MM]
    let (clock, offset) = split_zone(text)?;
    let (hms, _frac) = match clock.split_once('.') {
        Some((hms, frac)) => (hms, Some(frac)),
        None => (clock, None),
    };
    let mut parts = hms.split(':');
    let hour: i64 = parts.next()?.parse().ok()?;
    let minute: i64 = parts.next()?.parse().ok()?;
    let second: i64 = match parts.next() {
        Some(s) => s.parse().ok()?,
        None => 0,
    };
    if parts.next().is_some() || hour > 23 || minute > 59 || second > 59 {
        return None;
    }
    Some((hour, minute, second, offset))
}

fn split_zone(text: &str) -> Option<(&str, i64)> {
    if let Some(clock) = text.strip_suffix('Z') {
        return Some((clock, 0));
    }
    // a +/- after the clock belongs to the offset, not to the date
    if let Some(idx) = text.rfind(['+', '-']) {
        if idx > 0 {
            let (clock, zone) = text.split_at(idx);
            let sign = if zone.starts_with('-') { -1 } else { 1 };
            let body = &zone[1..];
            let (hours, minutes) = match body.split_once(':') {
                Some((h, m)) => (h, m),
                None if body.len() == 4 => (&body[..2], &body[2..]),
                None => return None,
            };
            let hours: i64 = hours.parse().ok()?;
            let minutes: i64 = minutes.parse().ok()?;
            if hours > 23 || minutes > 59 {
                return None;
            }
            return Some((clock, sign * (hours * 3600 + minutes * 60)));
        }
    }
    Some((text, 0))
}

fn parse_date(text: &str) -> Option<(i64, i64, i64)> {
    let (year, month, day) = if text.contains('/') {
        // InvariantCulture's US order: MM/DD/YYYY
        let mut parts = text.split('/');
        let month: i64 = parts.next()?.parse().ok()?;
        let day: i64 = parts.next()?.parse().ok()?;
        let year: i64 = parts.next()?.parse().ok()?;
        (year, month, day)
    } else {
        let mut parts = text.split('-');
        let year: i64 = parts.next()?.parse().ok()?;
        let month: i64 = parts.next()?.parse().ok()?;
        let day: i64 = parts.next()?.parse().ok()?;
        (year, month, day)
    };
    if !(1..=12).contains(&month) || !(1..=days_in_month(year, month)).contains(&day) {
        return None;
    }
    Some((year, month, day))
}

/// One timestamp as unix seconds, or None when it cannot be read.
pub fn parse_utc_text(text: &str) -> Option<i64> {
    let raw = text.trim();
    if raw.is_empty() {
        return None;
    }
    let (date_part, rest) = match raw.find(['T', ' ']) {
        Some(idx) => (&raw[..idx], Some(&raw[idx + 1..])),
        None => (raw, None),
    };
    let (year, month, day) = parse_date(date_part)?;
    let (hour, minute, second, offset) = match rest {
        Some(clock) if !clock.is_empty() => parse_clock(clock)?,
        _ => (0, 0, 0, 0),
    };
    let days = days_from_civil(year, month, day);
    Some(days * 86400 + hour * 3600 + minute * 60 + second - offset)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reads_iso_with_and_without_zone() {
        assert_eq!(parse_utc_text("1970-01-01T00:00:00Z"), Some(0));
        assert_eq!(parse_utc_text("2026-11-19T12:34:56"), Some(1795091696));
        assert_eq!(
            parse_utc_text("2026-11-19T12:34:56+03:00"),
            Some(1795080896)
        );
    }

    #[test]
    fn rejects_garbage() {
        assert_eq!(parse_utc_text("not a date"), None);
        assert_eq!(parse_utc_text("2026-13-45T99:99:99"), None);
        assert_eq!(parse_utc_text(""), None);
    }
}

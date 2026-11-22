//! The database as the Rust app writes it: the same rows, the same ISO timestamps, the same
//! events table the C# app maintains, so the two can take turns on one profile.

use std::path::Path;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use rusqlite::{params, Connection};
use steamrip_core::catalog::parse_one::CatalogPost;

/// One install, as the library needs it.
#[derive(Debug, Clone)]
pub struct InstallRow {
    pub post_id: i64,
    pub name: String,
    pub version: String,
    pub state: i64,
    pub exe: Option<String>,
    pub playtime: i64,
    pub app_id: Option<i64>,
    pub install_path: String,
    pub slug: String,
    pub proton: Option<String>,
    pub arguments: Option<String>,
}

pub struct Db {
    conn: Connection,
}

impl Db {
    /// Read-only, for the window that must never take a write lock the C# app needs.
    pub fn open_readonly(path: &Path) -> Result<Self, String> {
        let conn = Connection::open_with_flags(
            path,
            rusqlite::OpenFlags::SQLITE_OPEN_READ_ONLY | rusqlite::OpenFlags::SQLITE_OPEN_NO_MUTEX,
        )
        .map_err(|e| format!("open {}: {e}", path.display()))?;
        Ok(Self { conn })
    }

    pub fn open(path: &Path) -> Result<Self, String> {
        let conn = Connection::open(path).map_err(|e| format!("open {}: {e}", path.display()))?;
        conn.busy_timeout(Duration::from_secs(5))
            .map_err(|e| e.to_string())?;
        Ok(Self { conn })
    }

    pub fn installs(&self) -> Result<Vec<InstallRow>, String> {
        let mut statement = self
            .conn
            .prepare(
                "select i.post_id, i.name, coalesce(i.installed_version, ''), i.state, i.exe_rel, \
                        coalesce(i.playtime_sec, 0), coalesce(p.steam_app_id, 0), coalesce(i.install_path, ''), \
                        i.slug, i.proton, i.launch_args \
                 from installs i left join posts p on p.id = i.post_id \
                 order by i.installed_utc desc",
            )
            .map_err(|e| e.to_string())?;
        let rows = statement
            .query_map([], |row| {
                let app_id: i64 = row.get(6)?;
                Ok(InstallRow {
                    post_id: row.get(0)?,
                    name: row.get(1)?,
                    version: row.get(2)?,
                    state: row.get(3)?,
                    exe: row.get(4)?,
                    playtime: row.get(5)?,
                    app_id: (app_id > 0).then_some(app_id),
                    install_path: row.get(7)?,
                    slug: row.get(8)?,
                    proton: row.get(9)?,
                    arguments: row.get(10)?,
                })
            })
            .map_err(|e| e.to_string())?;
        Ok(rows.flatten().collect())
    }

    /// Stamps the launch time, like the C# app does right after a successful spawn.
    pub fn mark_played(&self, post_id: i64) -> Result<(), String> {
        self.conn
            .execute(
                "update installs set last_played_utc = ?1 where post_id = ?2",
                params![now_iso(), post_id],
            )
            .map(|_| ())
            .map_err(|e| e.to_string())
    }

    /// Adds the seconds a session lasted to the running total.
    pub fn add_playtime(&self, post_id: i64, seconds: i64) -> Result<(), String> {
        self.conn
            .execute(
                "update installs set playtime_sec = coalesce(playtime_sec, 0) + ?1 where post_id = ?2",
                params![seconds, post_id],
            )
            .map(|_| ())
            .map_err(|e| e.to_string())
    }

    /// One column of one install, for the diagnostics that prove a write landed.
    pub fn install_field(&self, post_id: i64, column: &str) -> Option<String> {
        let sql = format!("select {column} from installs where post_id = ?1");
        self.conn
            .query_row(&sql, params![post_id], |row| {
                row.get::<_, Option<String>>(0)
            })
            .ok()
            .flatten()
    }

    pub fn scalar_i64(&self, sql: &str) -> Option<i64> {
        self.conn.query_row(sql, [], |row| row.get(0)).ok()
    }

    /// One catalogue post, rebuilt from the columns the sync writes.
    pub fn post(&self, id: i64) -> Result<Option<CatalogPost>, String> {
        let row = self.conn.query_row(
            "select id, slug, url, title, name, title_version, build, steam_app_id, modified_utc,
                    published_utc, size_text, mirrors_json, coalesce(size_bytes, 0)
             from posts where id = ?1",
            params![id],
            |row| {
                Ok((
                    row.get::<_, i64>(0)?,
                    row.get::<_, String>(1)?,
                    row.get::<_, String>(2)?,
                    row.get::<_, String>(3)?,
                    row.get::<_, String>(4)?,
                    row.get::<_, Option<String>>(5)?,
                    row.get::<_, Option<String>>(6)?,
                    row.get::<_, Option<i64>>(7)?,
                    row.get::<_, String>(8)?,
                    row.get::<_, String>(9)?,
                    row.get::<_, Option<String>>(10)?,
                    row.get::<_, String>(11)?,
                    row.get::<_, i64>(12)?,
                ))
            },
        );
        match row {
            Ok((
                id,
                slug,
                url,
                title,
                name,
                title_version,
                build,
                steam_app_id,
                modified,
                published,
                size_text,
                mirrors_json,
                size_bytes,
            )) => Ok(Some(CatalogPost {
                id,
                slug,
                url,
                title,
                name,
                title_version,
                build,
                steam_app_id,
                modified_utc: parse_iso(&modified),
                published_utc: parse_iso(&published),
                size_text,
                mirrors: mirrors_from_json(&mirrors_json),
                size_bytes: (size_bytes > 0).then_some(size_bytes),
                category_ids: Vec::new(),
            })),
            Err(rusqlite::Error::QueryReturnedNoRows) => Ok(None),
            Err(error) => Err(error.to_string()),
        }
    }

    pub fn kv(&self, key: &str) -> Result<Option<String>, String> {
        self.conn
            .query_row("select value from kv where key = ?1", params![key], |row| {
                row.get::<_, String>(0)
            })
            .map(Some)
            .or_else(|e| match e {
                rusqlite::Error::QueryReturnedNoRows => Ok(None),
                other => Err(other.to_string()),
            })
    }

    pub fn set_kv(&self, key: &str, value: &str) -> Result<(), String> {
        self.conn
            .execute(
                "insert into kv(key,value) values(?1,?2) on conflict(key) do update set value=excluded.value",
                params![key, value],
            )
            .map(|_| ())
            .map_err(|e| e.to_string())
    }

    pub fn upsert_categories(&self, categories: &[(i64, String, i64)]) -> Result<(), String> {
        let mut statement = self
            .conn
            .prepare(
                "insert into categories(id,name,count) values(?1,?2,?3)                  on conflict(id) do update set name=excluded.name, count=excluded.count",
            )
            .map_err(|e| e.to_string())?;
        for (id, name, count) in categories {
            statement
                .execute(params![id, name, count])
                .map_err(|e| e.to_string())?;
        }
        Ok(())
    }

    /// The same upsert the C# app runs, including the mirror JSON it expects back.
    pub fn upsert_posts(&self, posts: &[CatalogPost]) -> Result<(), String> {
        let mut statement = self
            .conn
            .prepare(
                "insert into posts(id,slug,url,title,name,title_version,build,steam_app_id,modified_utc,published_utc,size_text,mirrors_json,seen_utc,size_bytes)                  values(?1,?2,?3,?4,?5,?6,?7,?8,?9,?10,?11,?12,?13,?14)                  on conflict(id) do update set slug=excluded.slug, url=excluded.url, title=excluded.title,                    name=excluded.name, title_version=excluded.title_version, build=excluded.build,                    steam_app_id=coalesce(excluded.steam_app_id, posts.steam_app_id), modified_utc=excluded.modified_utc,                    published_utc=excluded.published_utc, size_text=excluded.size_text,                    mirrors_json=excluded.mirrors_json, size_bytes=excluded.size_bytes",
            )
            .map_err(|e| e.to_string())?;
        let seen = now_iso();
        for post in posts {
            statement
                .execute(params![
                    post.id,
                    post.slug,
                    post.url,
                    post.title,
                    post.name,
                    post.title_version,
                    post.build,
                    post.steam_app_id,
                    crate::sync::date_iso(post.modified_utc),
                    crate::sync::date_iso(post.published_utc),
                    post.size_text,
                    crate::sync::mirrors_json(&post.mirrors),
                    seen,
                    post.size_bytes,
                ])
                .map_err(|e| e.to_string())?;
        }
        Ok(())
    }

    pub fn log_event(&self, kind: &str, message: &str, post_id: Option<i64>) -> Result<(), String> {
        self.conn
            .execute(
                "insert into events(at_utc, post_id, kind, message) values(?1, ?2, ?3, ?4)",
                params![now_iso(), post_id, kind, message],
            )
            .map(|_| ())
            .map_err(|e| e.to_string())
    }
}

/// The C# app writes .NET's round-trip format; the database is shared, so match it.
pub fn now_iso() -> String {
    let now = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default();
    iso_from_unix(now.as_secs() as i64, now.subsec_nanos())
}

pub fn iso_from_unix(secs: i64, nanos: u32) -> String {
    let (year, month, day, hour, minute, second) = civil_from_unix(secs);
    // .NET's round-trip format keeps seven fractional digits; the C# app writes this column.
    let nanos7 = format!("{nanos:09}")[..7].to_string();
    format!("{year:04}-{month:02}-{day:02}T{hour:02}:{minute:02}:{second:02}.{nanos7}Z")
}

/// Days since the Unix epoch to a civil date (Howard Hinnant's algorithm, no dependencies).
fn civil_from_unix(seconds: i64) -> (i64, u32, u32, u32, u32, u32) {
    let days = seconds.div_euclid(86_400);
    let rem = seconds.rem_euclid(86_400);
    let z = days + 719_468;
    let era = if z >= 0 { z } else { z - 146_096 } / 146_097;
    let doe = z - era * 146_097;
    let yoe = (doe - doe / 1_460 + doe / 36_524 - doe / 146_096) / 365;
    let mut year = yoe + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let day = (doy - (153 * mp + 2) / 5 + 1) as u32;
    let month = if mp < 10 { mp + 3 } else { mp - 9 } as u32;
    if month <= 2 {
        year += 1;
    }
    (
        year,
        month,
        day,
        (rem / 3_600) as u32,
        ((rem % 3_600) / 60) as u32,
        (rem % 60) as u32,
    )
}

/// ISO text (the C# app's round-trip format) back to unix seconds.
fn parse_iso(text: &str) -> i64 {
    steamrip_core::dates::parse_utc_text(text).unwrap_or(0)
}

/// The C# mirror column: [{"Host":2,"Url":"…"}] with the enum's numbers.
fn mirrors_from_json(text: &str) -> Vec<steamrip_core::catalog::Mirror> {
    use steamrip_core::catalog::Mirror;
    use steamrip_core::hoster::Hoster;
    let Ok(value) = serde_json::from_str::<serde_json::Value>(text) else {
        return Vec::new();
    };
    let Some(items) = value.as_array() else {
        return Vec::new();
    };
    items
        .iter()
        .filter_map(|item| {
            let host = item.get("Host").and_then(|v| v.as_i64()).unwrap_or(0);
            let url = item.get("Url").and_then(|v| v.as_str())?;
            Some(Mirror {
                host: match host {
                    1 => Hoster::BuzzHeavier,
                    2 => Hoster::FileDitch,
                    3 => Hoster::GoFile,
                    4 => Hoster::MegaDb,
                    5 => Hoster::Fichier,
                    6 => Hoster::FileCrypt,
                    _ => Hoster::Unknown,
                },
                url: url.to_string(),
            })
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn epoch_is_the_epoch() {
        assert_eq!(iso_from_unix(0, 0), "1970-01-01T00:00:00.0000000Z");
    }

    #[test]
    fn known_timestamp() {
        // 2026-08-27T04:05:52Z, checked with date -u
        assert_eq!(
            iso_from_unix(1_787_803_552, 0),
            "2026-08-27T04:05:52.0000000Z"
        );
    }
}

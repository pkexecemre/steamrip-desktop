//! The catalogue sync, in Rust: the same WordPress REST calls, the same politeness, the same
//! rows the C# app writes.

use std::path::Path;
use std::time::Duration;

use serde_json::{json, Value};
use steamrip_core::catalog::parse_one::parse_one;
use steamrip_core::catalog::parse_one::CatalogPost;
use steamrip_core::catalog::{parse_categories, Mirror};

use crate::db::{iso_from_unix, now_iso, Db};

const BASE: &str = "https://steamrip.com/wp-json/wp/v2";
const PER_PAGE: usize = 100;
const BETWEEN_PAGES: Duration = Duration::from_millis(1500);

pub struct Report {
    pub pages: usize,
    pub posts: usize,
    pub total: i64,
    pub categories: usize,
}

pub fn client() -> Result<reqwest::Client, String> {
    reqwest::Client::builder()
        .user_agent(concat!("steamrip-desktop-rust/", env!("CARGO_PKG_VERSION")))
        .timeout(Duration::from_secs(30))
        .build()
        .map_err(|e| e.to_string())
}

/// Incremental sync: only posts modified after the last successful run, unless `full`.
pub async fn sync(profile: &Path, full: bool) -> Result<Report, String> {
    let db = Db::open(&profile.join("steamrip.db"))?;
    let http = client()?;

    let since = if full { None } else { db.kv("last_sync_utc")? };

    let categories = fetch_categories(&http).await?;
    db.upsert_categories(&categories)?;

    let mut posts = Vec::new();
    let mut pages = 0usize;
    let mut page = 1usize;
    loop {
        let mut url = format!(
            "{BASE}/posts?per_page={PER_PAGE}&page={page}&orderby=modified&order=desc&_fields=id,slug,link,title,content,modified,published,categories,acf"
        );
        if let Some(since) = since.as_deref() {
            url.push_str("&modified_after=");
            url.push_str(since);
        }
        let response = http
            .get(&url)
            .send()
            .await
            .map_err(|e| format!("page {page}: {e}"))?;
        if response.status() == reqwest::StatusCode::BAD_REQUEST
            || response.status() == reqwest::StatusCode::NOT_FOUND
        {
            break; // past the last page
        }
        if response.status() == reqwest::StatusCode::TOO_MANY_REQUESTS {
            let wait = response
                .headers()
                .get(reqwest::header::RETRY_AFTER)
                .and_then(|v| v.to_str().ok())
                .and_then(|v| v.parse::<u64>().ok())
                .unwrap_or(5);
            tokio::time::sleep(Duration::from_secs(wait)).await;
            continue;
        }
        if !response.status().is_success() {
            return Err(format!("page {page}: HTTP {}", response.status()));
        }
        let items: Vec<Value> = response.json().await.map_err(|e| e.to_string())?;
        if items.is_empty() {
            break;
        }
        pages += 1;
        for item in &items {
            match parse_one(item) {
                Ok(Some(post)) => posts.push(post),
                Ok(None) => {}
                Err(error) => eprintln!("skipped a post: {error:?}"),
            }
        }
        page += 1;
        tokio::time::sleep(BETWEEN_PAGES).await;
    }

    let written = posts.len();
    db.upsert_posts(&posts)?;
    db.set_kv("last_sync_utc", &now_iso())?;
    let total = db.scalar_i64("select count(*) from posts").unwrap_or(0);
    db.log_event(
        "sync",
        &format!("{written} changed post(s), {total} total"),
        None,
    )?;
    Ok(Report {
        pages,
        posts: written,
        total,
        categories: categories.len(),
    })
}

async fn fetch_categories<C: Fetcher>(http: &C) -> Result<Vec<(i64, String, i64)>, String> {
    let url = format!("{BASE}/categories?per_page=100&_fields=id,name,count");
    let response = http.get_json(&url).await?;
    let parsed = parse_categories(&response.to_string());
    Ok(parsed
        .into_iter()
        .map(|c| (c.id, c.name, i64::from(c.count)))
        .collect())
}

/// The one HTTP call the sync needs, so the parser side stays testable without a network.
pub trait Fetcher {
    async fn get_json(&self, url: &str) -> Result<Value, String>;
}

impl Fetcher for reqwest::Client {
    async fn get_json(&self, url: &str) -> Result<Value, String> {
        let response = self.get(url).send().await.map_err(|e| e.to_string())?;
        if !response.status().is_success() {
            return Err(format!("HTTP {}", response.status()));
        }
        response.json().await.map_err(|e| e.to_string())
    }
}

/// The C# app stores mirrors as `[{"Host":2,"Url":"…"}]` with the enum's numbers; the Rust
/// enum serialises as names, so the shared column is built by hand.
pub fn mirrors_json(mirrors: &[Mirror]) -> String {
    let items: Vec<Value> = mirrors
        .iter()
        .map(|m| json!({ "Host": hoster_number(m.host), "Url": m.url }))
        .collect();
    Value::Array(items).to_string()
}

fn hoster_number(host: steamrip_core::hoster::Hoster) -> i64 {
    use steamrip_core::hoster::Hoster as H;
    match host {
        H::Unknown => 0,
        H::BuzzHeavier => 1,
        H::FileDitch => 2,
        H::GoFile => 3,
        H::MegaDb => 4,
        H::Fichier => 5,
        H::FileCrypt => 6,
    }
}

/// The date column, in the .NET round-trip shape the C# app parses back.
pub fn date_iso(unix_seconds: i64) -> String {
    if unix_seconds <= steamrip_core::catalog::parse_one::MIN_DATE_UNIX {
        return "0001-01-01T00:00:00.0000000Z".into();
    }
    iso_from_unix(unix_seconds, 0)
}

#[allow(dead_code)]
fn unused_post_shape(_post: &CatalogPost) {}

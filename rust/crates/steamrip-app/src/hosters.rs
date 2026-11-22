//! Hoster resolution, in Rust: the same steps the C# resolvers take, for the hosters that do not
//! need a browser. FileDitch and BuzzHeavier answer through a Cloudflare challenge and a
//! header-only redirect, so they still go through the system Chromium (not ported yet).

use std::time::Duration;

use reqwest::header::{HeaderMap, HeaderValue, REFERER};
use serde_json::Value;
use steamrip_core::catalog::{parse_one::CatalogPost, Mirror};
use steamrip_core::chain::effective_order;
use steamrip_core::hoster::Hoster;
use steamrip_core::hosters::{gofile_content_id, parse_megadb};

pub const USER_AGENT: &str = "Mozilla/5.0";

#[derive(Debug, Clone)]
pub struct Resolved {
    pub host: Hoster,
    pub url: String,
    pub headers: Vec<(String, String)>,
    pub file_name: Option<String>,
    pub size: Option<i64>,
}

pub fn http() -> Result<reqwest::Client, String> {
    reqwest::Client::builder()
        .user_agent(USER_AGENT)
        .cookie_store(true)
        .timeout(Duration::from_secs(60))
        .redirect(reqwest::redirect::Policy::none())
        .build()
        .map_err(|e| e.to_string())
}

/// Walks a post's mirrors in the settings order and returns the first direct link the ported
/// resolvers can produce.
pub async fn resolve_post(
    http: &reqwest::Client,
    post: &CatalogPost,
    order: &[Hoster],
) -> Result<Resolved, String> {
    let mirrors: Vec<(Hoster, String)> = post
        .mirrors
        .iter()
        .map(|m: &Mirror| (m.host, m.url.clone()))
        .collect();
    let mut errors: Vec<String> = Vec::new();

    for host in effective_order(order) {
        for (mirror_host, url) in mirrors.iter().filter(|(h, _)| *h == host) {
            match resolve_one(http, *mirror_host, url, post).await {
                Ok(resolved) => return Ok(resolved),
                Err(error) => errors.push(format!("{mirror_host:?}: {error}")),
            }
        }
    }
    Err(if errors.is_empty() {
        "no mirror this build can resolve yet".to_string()
    } else {
        errors.join("; ")
    })
}

async fn resolve_one(
    http: &reqwest::Client,
    host: Hoster,
    url: &str,
    _post: &CatalogPost,
) -> Result<Resolved, String> {
    match host {
        Hoster::GoFile => gofile(http, url).await,
        Hoster::MegaDb => megadb(http, url).await,
        Hoster::FileDitch | Hoster::BuzzHeavier => {
            Err("needs the browser solver, not ported yet".to_string())
        }
        Hoster::Fichier | Hoster::FileCrypt => Err("unsupported hoster".to_string()),
        Hoster::Unknown => Err("unknown hoster".to_string()),
    }
}

/// GoFile: a guest account token, then the share's contents; the largest file is the archive.
async fn gofile(http: &reqwest::Client, share_url: &str) -> Result<Resolved, String> {
    let content_id = gofile_content_id(share_url)
        .ok_or_else(|| format!("not a gofile.io/d/ link: {share_url}"))?;

    // GoFile wants the per-four-hour website token on every call, guest account or not.
    let website_token = |account: &str| {
        let now = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap_or_default()
            .as_secs() as i64;
        steamrip_core::hosters::gofile_website_token(account, now)
    };

    let account: Value = http
        .post("https://api.gofile.io/accounts")
        .header("Origin", "https://gofile.io")
        .header(REFERER, "https://gofile.io/")
        .header("X-Website-Token", website_token(""))
        .header("X-BL", "en-US")
        .header("Content-Type", "application/json")
        .body("{}")
        .send()
        .await
        .map_err(|e| e.to_string())?
        .json()
        .await
        .map_err(|e| e.to_string())?;
    let token = account
        .get("data")
        .and_then(|d| d.get("token"))
        .and_then(Value::as_str)
        .ok_or("no guest token")?
        .to_string();

    let contents: Value = http
        .get(format!(
            "https://api.gofile.io/contents/{content_id}?cache=true&sortField=createTime&sortDirection=1"
        ))
        .header("Authorization", format!("Bearer {token}"))
        .header("X-Website-Token", website_token(&token))
        .header("X-BL", "en-US")
        .header("Cookie", format!("accountToken={token}"))
        .header("Origin", "https://gofile.io")
        .header(REFERER, "https://gofile.io/")
        .send()
        .await
        .map_err(|e| e.to_string())?
        .json()
        .await
        .map_err(|e| e.to_string())?;

    let children = contents
        .get("data")
        .and_then(|d| d.get("children"))
        .and_then(Value::as_object)
        .ok_or_else(|| {
            format!(
                "folder has no children (status {:?}): {}",
                contents
                    .get("status")
                    .and_then(Value::as_str)
                    .unwrap_or("?"),
                contents.to_string().chars().take(160).collect::<String>()
            )
        })?;
    let best = children
        .values()
        .filter(|c| c.get("type").and_then(Value::as_str) == Some("file"))
        .max_by_key(|c| c.get("size").and_then(Value::as_i64).unwrap_or(0))
        .ok_or("no files in folder")?;

    Ok(Resolved {
        host: Hoster::GoFile,
        url: best
            .get("link")
            .and_then(Value::as_str)
            .ok_or("file has no link")?
            .to_string(),
        headers: vec![
            ("Cookie".into(), format!("accountToken={token}")),
            ("User-Agent".into(), USER_AGENT.into()),
        ],
        file_name: best.get("name").and_then(Value::as_str).map(str::to_string),
        size: best.get("size").and_then(Value::as_i64),
    })
}

/// MegaDB: landing page (Referer steamrip.com), the server's countdown, then the token URL's 302.
async fn megadb(http: &reqwest::Client, share_url: &str) -> Result<Resolved, String> {
    let html = http
        .get(share_url)
        .header(REFERER, "https://steamrip.com/")
        .send()
        .await
        .map_err(|e| e.to_string())?
        .text()
        .await
        .map_err(|e| e.to_string())?;
    let page = parse_megadb(&html)?;

    tokio::time::sleep(Duration::from_secs(u64::from(page.wait_seconds) + 1)).await;

    let mut location: Option<String> = None;
    for attempt in 1..=3 {
        if attempt > 1 {
            tokio::time::sleep(Duration::from_secs(1)).await;
        }
        let response = http
            .get(&page.pt_url)
            .header(REFERER, share_url)
            .send()
            .await
            .map_err(|e| e.to_string())?;
        location = response
            .headers()
            .get(reqwest::header::LOCATION)
            .and_then(|v| v.to_str().ok())
            .map(str::to_string);
        if location.is_some() {
            break;
        }
    }
    let location = location.ok_or("the token URL never redirected to a storage node")?;
    let file_url = if location.starts_with("http") {
        location
    } else {
        format!("https://megadb.net{}", location.trim_start_matches('/'))
    };

    let mut headers = HeaderMap::new();
    headers.insert(REFERER, HeaderValue::from_static("https://megadb.net/"));
    Ok(Resolved {
        host: Hoster::MegaDb,
        file_name: page.file_name.clone(),
        size: None,
        url: file_url,
        headers: headers
            .iter()
            .map(|(k, v)| {
                (
                    k.as_str().to_string(),
                    v.to_str().unwrap_or_default().to_string(),
                )
            })
            .collect(),
    })
}

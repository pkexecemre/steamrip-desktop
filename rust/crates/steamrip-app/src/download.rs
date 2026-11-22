//! The download engine, in Rust: the aria2 client (aria2c stays an external tool) plus the
//! built-in ranged HTTP fallback the C# app keeps for machines without it.

use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::time::Duration;

use serde_json::{json, Value};

pub const USER_AGENT: &str = "Mozilla/5.0";

pub struct Download {
    pub path: PathBuf,
    pub bytes: u64,
}

/// aria2c over JSON-RPC: start it on a free port, add one URI, watch it, shut it down.
pub struct Aria2 {
    child: Child,
    port: u16,
    secret: String,
    http: reqwest::Client,
}

impl Aria2 {
    pub fn available() -> Option<PathBuf> {
        crate::find_tool("aria2c").map(PathBuf::from)
    }

    pub async fn start() -> Result<Self, String> {
        let exe = Self::available().ok_or("aria2c is not installed")?;
        let port = free_port()?;
        let secret = format!("sr{}", std::process::id());
        let child = Command::new(&exe)
            .arg("--enable-rpc")
            .arg("--rpc-listen-all=false")
            .arg(format!("--rpc-listen-port={port}"))
            .arg(format!("--rpc-secret={secret}"))
            .arg("--rpc-max-request-size=32M")
            .arg("--auto-file-renaming=false")
            .arg("--allow-overwrite=true")
            .arg("--file-allocation=none")
            .arg("--max-connection-per-server=8")
            .arg("--split=8")
            .arg("--min-split-size=4M")
            .arg("--max-tries=5")
            .arg("--retry-wait=5")
            .arg("--timeout=30")
            .arg("--connect-timeout=15")
            .arg("--summary-interval=0")
            .arg("--console-log-level=warn")
            .arg("--quiet=true")
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .spawn()
            .map_err(|e| format!("start aria2c: {e}"))?;

        let http = reqwest::Client::new();
        let client = Self {
            child,
            port,
            secret,
            http,
        };
        // the RPC socket needs a moment
        for _ in 0..40 {
            if client.call("aria2.getVersion", json!([])).await.is_ok() {
                return Ok(client);
            }
            tokio::time::sleep(Duration::from_millis(100)).await;
        }
        Err("aria2c did not answer on its RPC port".into())
    }

    async fn call(&self, method: &str, params: Value) -> Result<Value, String> {
        let body = json!({
            "jsonrpc": "2.0",
            "id": "steamrip",
            "method": method,
            "params": {
                "token": self.secret
            }
        });
        // aria2 wants the params as a positional array with the token first
        let mut body = body;
        let mut list = vec![json!(format!("token:{}", self.secret))];
        if let Some(array) = params.as_array() {
            list.extend(array.iter().cloned());
        }
        body["params"] = Value::Array(list);

        let response = self
            .http
            .post(format!("http://127.0.0.1:{}/jsonrpc", self.port))
            .json(&body)
            .timeout(Duration::from_secs(20))
            .send()
            .await
            .map_err(|e| e.to_string())?
            .json::<Value>()
            .await
            .map_err(|e| e.to_string())?;
        if let Some(error) = response.get("error") {
            return Err(format!(
                "{}: {}",
                method,
                error
                    .get("message")
                    .and_then(Value::as_str)
                    .unwrap_or("unknown error")
            ));
        }
        Ok(response.get("result").cloned().unwrap_or(Value::Null))
    }

    pub async fn add(
        &self,
        uri: &str,
        dir: &Path,
        out: &str,
        headers: &[(String, String)],
    ) -> Result<String, String> {
        let mut options = json!({
            "dir": dir.to_string_lossy(),
            "out": out,
            "continue": "true",
            "auto-file-renaming": "false",
            "allow-overwrite": "true",
            "max-connection-per-server": "8",
            "split": "8",
            "min-split-size": "4M",
            "max-tries": "5",
            "retry-wait": "5",
            "timeout": "30",
            "connect-timeout": "15",
        });
        if !headers.is_empty() {
            let list: Vec<String> = headers.iter().map(|(k, v)| format!("{k}: {v}")).collect();
            options["header"] = json!(list);
        }
        if uri.starts_with("magnet:") || uri.ends_with(".torrent") {
            options["seed-time"] = json!("0");
        }
        let gid = self.call("aria2.addUri", json!([[uri], options])).await?;
        Ok(gid.as_str().unwrap_or_default().to_string())
    }

    /// Polls until the transfer ends, printing one line per change of state.
    pub async fn wait(&self, gid: &str, label: &str) -> Result<u64, String> {
        let mut last_printed = 0u64;
        loop {
            let status = self
                .call(
                    "aria2.tellStatus",
                    json!([
                        gid,
                        [
                            "status",
                            "completedLength",
                            "totalLength",
                            "downloadSpeed",
                            "errorCode",
                            "errorMessage"
                        ]
                    ]),
                )
                .await?;
            let state = status.get("status").and_then(Value::as_str).unwrap_or("");
            let done = status
                .get("completedLength")
                .and_then(Value::as_str)
                .and_then(|v| v.parse::<u64>().ok())
                .unwrap_or(0);
            let total = status
                .get("totalLength")
                .and_then(Value::as_str)
                .and_then(|v| v.parse::<u64>().ok())
                .unwrap_or(0);
            let speed = status
                .get("downloadSpeed")
                .and_then(Value::as_str)
                .and_then(|v| v.parse::<u64>().ok())
                .unwrap_or(0);
            if done / (16 * 1024 * 1024) != last_printed {
                last_printed = done / (16 * 1024 * 1024);
                let percent = if total > 0 { done * 100 / total } else { 0 };
                eprintln!(
                    "[{label}] {percent}% · {} MiB / {} MiB · {} MiB/s",
                    done / 1_048_576,
                    total / 1_048_576,
                    speed / 1_048_576
                );
            }
            match state {
                "complete" => return Ok(done),
                "error" | "removed" => {
                    return Err(format!(
                        "aria2 {}: {}",
                        state,
                        status
                            .get("errorMessage")
                            .and_then(Value::as_str)
                            .unwrap_or("no message")
                    ))
                }
                _ => tokio::time::sleep(Duration::from_millis(700)).await,
            }
        }
    }

    pub async fn shutdown(mut self) {
        let _ = self.call("aria2.shutdown", json!([])).await;
        let _ = self.child.kill();
        let _ = self.child.wait();
    }
}

fn free_port() -> Result<u16, String> {
    let listener = std::net::TcpListener::bind("127.0.0.1:0").map_err(|e| e.to_string())?;
    let port = listener.local_addr().map_err(|e| e.to_string())?.port();
    drop(listener);
    Ok(port)
}

/// Downloads one resolved link: aria2 when it is installed, the ranged loop otherwise.
pub async fn fetch(
    resolve: &crate::hosters::Resolved,
    downloads_dir: &Path,
    out_name: Option<&str>,
) -> Result<Download, String> {
    std::fs::create_dir_all(downloads_dir).map_err(|e| e.to_string())?;
    let name = out_name
        .map(str::to_string)
        .or_else(|| resolve.file_name.clone())
        .or_else(|| {
            resolve
                .url
                .split('?')
                .next()
                .and_then(|u| u.rsplit('/').next())
                .map(str::to_string)
        })
        .filter(|n| !n.is_empty())
        .unwrap_or_else(|| "download.bin".to_string());
    let target = downloads_dir.join(&name);

    if Aria2::available().is_some() && std::env::var("SR_NO_ARIA2").is_err() {
        let aria = Aria2::start().await?;
        let gid = aria
            .add(&resolve.url, downloads_dir, &name, &resolve.headers)
            .await;
        let outcome = match gid {
            Ok(gid) => {
                let bytes = aria.wait(&gid, &name).await;
                aria.shutdown().await;
                bytes
            }
            Err(error) => {
                aria.shutdown().await;
                return Err(error);
            }
        };
        return Ok(Download {
            path: target,
            bytes: outcome?,
        });
    }

    let bytes = ranged_download(&resolve.url, &resolve.headers, &target).await?;
    Ok(Download {
        path: target,
        bytes,
    })
}

/// The fallback: one ranged GET that appends to a .part file, so a killed run resumes.
pub async fn ranged_download(
    url: &str,
    headers: &[(String, String)],
    target: &Path,
) -> Result<u64, String> {
    use tokio::io::AsyncWriteExt;

    let part = target.with_extension("part");
    let mut have = std::fs::metadata(&part).map(|m| m.len()).unwrap_or(0);
    let client = reqwest::Client::builder()
        .user_agent(USER_AGENT)
        .timeout(Duration::from_secs(60))
        .build()
        .map_err(|e| e.to_string())?;

    loop {
        let mut request = client.get(url);
        for (key, value) in headers {
            request = request.header(key, value);
        }
        if have > 0 {
            request = request.header(reqwest::header::RANGE, format!("bytes={have}-"));
        }
        let response = request.send().await.map_err(|e| e.to_string())?;
        let status = response.status();
        if !(status.is_success() || status == reqwest::StatusCode::PARTIAL_CONTENT) {
            return Err(format!("HTTP {status}"));
        }
        let mut file = tokio::fs::OpenOptions::new()
            .create(true)
            .append(true)
            .open(&part)
            .await
            .map_err(|e| e.to_string())?;
        let mut stream = response.bytes_stream();
        use futures::StreamExt;
        while let Some(chunk) = stream.next().await {
            let chunk = chunk.map_err(|e| e.to_string())?;
            file.write_all(&chunk).await.map_err(|e| e.to_string())?;
            have += chunk.len() as u64;
        }
        file.flush().await.map_err(|e| e.to_string())?;
        drop(file);
        // a second pass only happens when the server ignored the Range and we started over
        let expected = std::fs::metadata(&part)
            .ok()
            .map(|m| m.len())
            .unwrap_or(have);
        if expected == have {
            break;
        }
        have = expected;
    }

    std::fs::rename(&part, target).map_err(|e| e.to_string())?;
    Ok(have)
}

//! Installing an archive, in Rust: the same steps the C# installer takes - test, extract to
//! staging, back the old install up, swap it in, record the row.

use std::path::{Path, PathBuf};
use std::process::Stdio;
use std::time::{SystemTime, UNIX_EPOCH};

use steamrip_core::catalog::parse_one::CatalogPost;
use steamrip_core::exe_finder::{best, Entry};
use steamrip_core::installer::{collapse_target, safe_name};

use crate::db::{now_iso, Db};

pub struct Installed {
    pub target: PathBuf,
    pub exe: Option<String>,
    pub backup: Option<PathBuf>,
}

/// Where the archive tools are; both are external, like aria2.
fn tools() -> (Option<String>, Option<String>) {
    (
        crate::find_tool("unrar"),
        crate::find_tool("7z").or_else(|| crate::find_tool("7za")),
    )
}

fn is_rar(path: &Path) -> bool {
    path.extension()
        .and_then(|e| e.to_str())
        .is_some_and(|e| e.eq_ignore_ascii_case("rar"))
}

/// One process, waited for; the archive tools print their progress, which is passed through.
fn run(exe: &str, args: &[String], cwd: Option<&Path>) -> Result<(), String> {
    let mut command = std::process::Command::new(exe);
    command
        .args(args)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::piped());
    if let Some(cwd) = cwd {
        command.current_dir(cwd);
    }
    let output = command.output().map_err(|e| format!("{exe}: {e}"))?;
    if output.status.success() {
        return Ok(());
    }
    let stderr = String::from_utf8_lossy(&output.stderr);
    Err(format!(
        "{exe} exited {}: {}",
        output.status.code().unwrap_or(-1),
        stderr.lines().rev().take(3).collect::<Vec<_>>().join(" | ")
    ))
}

/// True when the archive is intact: unrar when it can read the format, 7z otherwise.
pub fn test_archive(archive: &Path) -> Result<(), String> {
    let (unrar, seven) = tools();
    if is_rar(archive) {
        if let Some(unrar) = unrar {
            return run(
                &unrar,
                &["t".into(), "-y".into(), archive.display().to_string()],
                None,
            );
        }
    }
    let seven = seven.ok_or("neither unrar nor 7z is installed")?;
    run(
        &seven,
        &[
            "t".into(),
            archive.display().to_string(),
            "-y".into(),
            "-bsp0".into(),
            "-bso0".into(),
        ],
        None,
    )
}

fn extract(archive: &Path, staging: &Path) -> Result<(), String> {
    let (unrar, seven) = tools();
    if is_rar(archive) {
        if let Some(unrar) = unrar {
            return run(
                &unrar,
                &[
                    "x".into(),
                    "-y".into(),
                    "-o+".into(),
                    archive.display().to_string(),
                    format!("{}/", staging.display()),
                ],
                None,
            );
        }
    }
    let seven = seven.ok_or("neither unrar nor 7z is installed")?;
    run(
        &seven,
        &[
            "x".into(),
            archive.display().to_string(),
            format!("-o{}", staging.display()),
            "-y".into(),
            "-bsp0".into(),
            "-bso0".into(),
        ],
        None,
    )
}

/// The top-level entries of a directory, as paths relative to it.
fn top_level(dir: &Path) -> (Vec<String>, Vec<String>) {
    let mut dirs = Vec::new();
    let mut files = Vec::new();
    if let Ok(entries) = std::fs::read_dir(dir) {
        for entry in entries.flatten() {
            let name = entry.file_name().to_string_lossy().to_string();
            if entry.file_type().map(|t| t.is_dir()).unwrap_or(false) {
                dirs.push(name);
            } else {
                files.push(name);
            }
        }
    }
    (dirs, files)
}

/// The executables under the install, scored by the ported ranker.
fn find_exe(root: &Path, game_name: &str) -> Option<String> {
    let mut entries = Vec::new();
    let mut stack = vec![(root.to_path_buf(), 0)];
    while let Some((dir, depth)) = stack.pop() {
        if depth > 4 {
            continue;
        }
        let Ok(children) = std::fs::read_dir(&dir) else {
            continue;
        };
        for child in children.flatten() {
            let path = child.path();
            let Ok(kind) = child.file_type() else {
                continue;
            };
            let relative = path
                .strip_prefix(root)
                .unwrap_or(&path)
                .display()
                .to_string();
            if kind.is_dir() {
                let name = child.file_name().to_string_lossy().to_lowercase();
                entries.push(Entry::dir(relative, depth as i32));
                if matches!(
                    name.as_str(),
                    "_commonredist"
                        | "redist"
                        | "redistributables"
                        | "directx"
                        | "dotnet"
                        | "vcredist"
                        | "drive_c"
                        | "__installer"
                        | "support"
                ) {
                    continue;
                }
                stack.push((path, depth + 1));
            } else if path
                .extension()
                .and_then(|e| e.to_str())
                .is_some_and(|e| e.eq_ignore_ascii_case("exe"))
            {
                let size = child.metadata().map(|m| m.len() as i64).unwrap_or(0);
                entries.push(Entry::file(relative, size, depth as i32));
            }
        }
    }
    best(&entries, Some(game_name))
}

/// Installs one archive into the library and records it. The old install is only moved aside once
/// the new one has extracted and passed nothing worse than a swap.
pub fn install(
    db: &Db,
    library: &Path,
    slug: &str,
    post: &CatalogPost,
    archive: &Path,
) -> Result<Installed, String> {
    let target = library.join(slug);
    let staging = library.join(".staging").join(slug);
    let backups = library.join(".backups").join(slug);

    if staging.exists() {
        std::fs::remove_dir_all(&staging).map_err(|e| e.to_string())?;
    }
    std::fs::create_dir_all(&staging).map_err(|e| e.to_string())?;

    db.log_event(
        "install",
        &format!("{}: testing the archive", post.name),
        Some(post.id),
    )?;
    if let Err(error) = test_archive(archive) {
        let _ = std::fs::remove_dir_all(&staging);
        db.log_event(
            "error",
            &format!("{}: archive failed its test ({error})", post.name),
            Some(post.id),
        )?;
        return Err(error);
    }

    db.log_event(
        "install",
        &format!("{}: extracting", post.name),
        Some(post.id),
    )?;
    if let Err(error) = extract(archive, &staging) {
        let _ = std::fs::remove_dir_all(&staging);
        return Err(error);
    }

    let (dirs, files) = top_level(&staging);
    let root = match collapse_target(&dirs, &files) {
        Some(name) => staging.join(name),
        None => staging.clone(),
    };

    let mut backup = None;
    if target.exists() {
        let stamp = post.title_version.clone().unwrap_or_else(|| {
            SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .map(|d| d.as_secs().to_string())
                .unwrap_or_else(|_| "old".into())
        });
        let path = backups.join(safe_name(&stamp));
        if path.exists() {
            // the same version twice: keep the newest
            let _ = std::fs::remove_dir_all(&path);
        }
        if let Some(parent) = path.parent() {
            std::fs::create_dir_all(parent).map_err(|e| e.to_string())?;
        }
        std::fs::rename(&target, &path).map_err(|e| e.to_string())?;
        backup = Some(path);
        db.log_event(
            "install",
            &format!("{}: old version moved to {}", post.name, backups.display()),
            Some(post.id),
        )?;
    }

    if let Some(parent) = target.parent() {
        std::fs::create_dir_all(parent).map_err(|e| e.to_string())?;
    }
    std::fs::rename(&root, &target).map_err(|e| e.to_string())?;
    if staging.exists() {
        let _ = std::fs::remove_dir_all(&staging);
    }

    let exe = find_exe(&target, &post.name);
    db.upsert_install(post, slug, &target, exe.as_deref())?;
    db.log_event(
        "install",
        &format!(
            "{}: installed {} ({})",
            post.name,
            post.title_version.clone().unwrap_or_default(),
            target.display()
        ),
        Some(post.id),
    )?;
    let _ = now_iso();

    Ok(Installed {
        target,
        exe,
        backup,
    })
}

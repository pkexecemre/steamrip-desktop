//! Port of the pure parts of SteamRip.Core.Install.RedistInstaller: which VC++ redistributables
//! to run, and in what order.

pub const MARKER_NAME: &str = ".steamrip-vc-redist";
const AIO_ARGS: [&str; 1] = ["/yAI"];
const MS_ARGS: [&str; 3] = ["/install", "/quiet", "/norestart"];

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct VcInstaller {
    pub file: String,
    pub args: Vec<String>,
}

fn file_name(path: &str) -> &str {
    path.rsplit_once('/').map(|(_, name)| name).unwrap_or(path)
}

fn is_executable(path: &str) -> bool {
    path.to_ascii_lowercase().ends_with(".exe")
}

/// vcredist*/vc_redist* executables, the Microsoft-style installers.
pub fn is_vc_redist_name(path: &str) -> bool {
    let name = file_name(path).to_ascii_lowercase();
    name.starts_with("vcredist") || name.starts_with("vc_redist")
}

/// x64 before x86, and named-year installers before a bare vcredist_x64.
pub fn vc_sort_key(path: &str) -> i32 {
    let name = file_name(path).to_ascii_lowercase();
    let arch = if name.contains("x86") && !name.contains("x64") {
        1
    } else {
        0
    };
    let modern = name.contains("2015")
        || name.contains("2017")
        || name.contains("2019")
        || name.contains("2022");
    let age = if modern { 0 } else { 1 };
    arch * 10 + age
}

/// The installers to run out of a folder's file list, in order: the AIO when it is there,
/// else every vcredist/vc_redist executable.
pub fn select_installers(files: &[String]) -> Vec<VcInstaller> {
    let executables: Vec<&String> = files.iter().filter(|f| is_executable(f)).collect();

    let mut aio: Vec<&String> = executables
        .iter()
        .copied()
        .filter(|f| {
            file_name(f)
                .to_ascii_lowercase()
                .contains("visualcppredist_aio")
        })
        .collect();
    if !aio.is_empty() {
        aio.sort_by_key(|f| f.to_ascii_lowercase());
        return aio
            .into_iter()
            .map(|f| VcInstaller {
                file: f.clone(),
                args: AIO_ARGS.iter().map(|a| a.to_string()).collect(),
            })
            .collect();
    }

    let mut chosen: Vec<&String> = executables
        .into_iter()
        .filter(|f| is_vc_redist_name(f))
        .collect();
    chosen.sort_by_key(|f| (vc_sort_key(f), f.to_ascii_lowercase()));
    chosen
        .into_iter()
        .map(|f| VcInstaller {
            file: f.clone(),
            args: MS_ARGS.iter().map(|a| a.to_string()).collect(),
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn aio_wins_when_present() {
        let files = vec![
            "/r/vcredist_x64.exe".to_string(),
            "/r/VisualCppRedist_AIO_x86_x64.exe".to_string(),
        ];
        let picked = select_installers(&files);
        assert_eq!(picked.len(), 1);
        assert!(picked[0].file.contains("VisualCppRedist_AIO"));
        assert_eq!(picked[0].args, vec!["/yAI".to_string()]);
    }

    #[test]
    fn x64_comes_before_x86() {
        let files = vec![
            "/r/vcredist_x86.exe".to_string(),
            "/r/vcredist_x64.exe".to_string(),
        ];
        let picked = select_installers(&files);
        assert!(picked[0].file.ends_with("x64.exe"));
    }
}

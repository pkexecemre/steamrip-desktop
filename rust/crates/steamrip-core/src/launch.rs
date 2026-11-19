//! Port of SteamRip.Core.Launch.GameLauncher.Compose: the wrappers, runner, exe, arguments and
//! environment a launch turns into. Pure data in, pure data out, so both implementations can be
//! replayed against the same recordings.

use crate::launch_options::{parse_environment, LaunchOptions};
use crate::prefix::game_id;
use std::collections::BTreeMap;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LaunchSpec {
    pub steam_app_id: Option<i64>,
    pub slug: Option<String>,
    pub proton_path: Option<String>,
    pub arguments: Option<String>,
    pub options: Option<LaunchOptions>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LaunchCommand {
    pub command: Vec<String>,
    pub environment: BTreeMap<String, String>,
    pub warnings: Vec<String>,
}

/// Splits an argument string the way the C# original does: quotes group, they are dropped, and
/// runs of whitespace separate.
pub fn split_args(args: Option<&str>) -> Vec<String> {
    let Some(args) = args else {
        return Vec::new();
    };
    if args.trim().is_empty() {
        return Vec::new();
    }
    let mut out = Vec::new();
    let mut current = String::new();
    let mut quoted = false;
    for ch in args.chars() {
        if ch == '"' {
            quoted = !quoted;
            continue;
        }
        if ch.is_whitespace() && !quoted {
            if !current.is_empty() {
                out.push(std::mem::take(&mut current));
            }
            continue;
        }
        current.push(ch);
    }
    if !current.is_empty() {
        out.push(current);
    }
    out
}

/// The fully composed process: wrappers, runner, exe, arguments and the environment to add.
pub fn compose(
    spec: &LaunchSpec,
    runner: Option<&str>,
    exe: &str,
    prefix: Option<&str>,
    find_tool: impl Fn(&str) -> Option<String>,
    screen: Option<(u32, u32)>,
) -> LaunchCommand {
    let options = spec
        .options
        .clone()
        .unwrap_or_else(LaunchOptions::app_defaults);
    let mut env: BTreeMap<String, String> = BTreeMap::new();
    let mut warnings: Vec<String> = Vec::new();
    let mut command: Vec<String> = Vec::new();
    let user_env = parse_environment(options.environment.as_deref());

    if let Some(wrapper) = options.wrapper.as_deref() {
        if !wrapper.trim().is_empty() {
            command.extend(split_args(Some(wrapper)));
        }
    }

    if let Some(runner) = runner {
        let prefix = prefix.expect("a runner launch needs a prefix");
        env.insert("WINEPREFIX".to_string(), prefix.to_string());
        env.insert(
            "GAMEID".to_string(),
            game_id(spec.steam_app_id, spec.slug.as_deref()),
        );
        env.insert("STORE".to_string(), "none".to_string());
        if let Some(proton) = spec.proton_path.as_deref() {
            if !proton.is_empty() {
                env.insert("PROTONPATH".to_string(), proton.to_string());
            }
        }

        let gamescope = if options.gamescope == Some(true) {
            find_tool("gamescope")
        } else {
            None
        };
        if options.gamescope == Some(true) && gamescope.is_none() {
            warnings
                .push("gamescope is enabled but not installed; launching without it.".to_string());
        }

        if options.game_mode == Some(true) {
            match find_tool("gamemoderun") {
                Some(path) => command.push(path),
                None => warnings.push(
                    "gamemode is enabled but gamemoderun is not installed; skipping it."
                        .to_string(),
                ),
            }
        }

        if options.mango_hud == Some(true) && gamescope.is_none() {
            env.insert("MANGOHUD".to_string(), "1".to_string());
        }
        if options.mango_hud == Some(true) {
            if let Some(config) = options.mango_hud_config.as_deref() {
                if !config.trim().is_empty() {
                    env.insert("MANGOHUD_CONFIG".to_string(), config.to_string());
                }
            }
        }

        if options.hdr == Some(true) {
            if gamescope.is_none() {
                env.insert("PROTON_ENABLE_WAYLAND".to_string(), "1".to_string());
                if options.wayland != Some(true) {
                    warnings.push(
                        "HDR without gamescope needs Wine's Wayland driver; enabling PROTON_ENABLE_WAYLAND for this launch."
                            .to_string(),
                    );
                }
            } else if user_env.iter().any(|(k, _)| k == "ENABLE_HDR_WSI") {
                warnings.push(
                    "ENABLE_HDR_WSI is set in the environment; gamescope HDR works without it and it can break the output.".to_string(),
                );
            }
        }
        for (key, value) in &user_env {
            if matches!(
                key.as_str(),
                "DXVK_HDR" | "PROTON_ENABLE_HDR" | "PROTON_USE_HDR"
            ) && value == "1"
            {
                warnings.push(format!(
                    "{key}=1 is set in the environment; current Proton builds detect HDR themselves and this forces PQ output onto what Wine sees as an SDR display (grey blacks). Use PROTON_FORCE_HDR=1 if you really need to force it."
                ));
            }
        }

        if options.wayland == Some(true) {
            env.insert("PROTON_ENABLE_WAYLAND".to_string(), "1".to_string());
        }
        if options.fsr == Some(true) {
            env.insert("WINE_FULLSCREEN_FSR".to_string(), "1".to_string());
        }
        if options.fsync == Some(false) {
            env.insert("PROTON_NO_FSYNC".to_string(), "1".to_string());
        }
        if options.proton_log == Some(true) {
            env.insert("PROTON_LOG".to_string(), "1".to_string());
        }
        if let Some(overrides) = options.dll_overrides.as_deref() {
            if !overrides.trim().is_empty() {
                env.insert("WINEDLLOVERRIDES".to_string(), overrides.trim().to_string());
            }
        }

        if let Some(gamescope) = gamescope {
            command.push(gamescope);
            let args = split_args(options.gamescope_args.as_deref());
            command.extend(args.clone());
            if let Some((width, height)) = screen {
                let sized = args.iter().any(|a| {
                    matches!(
                        a.as_str(),
                        "-W" | "-H" | "--output-width" | "--output-height"
                    ) || a.starts_with("--output-width=")
                        || a.starts_with("--output-height=")
                });
                if !sized {
                    command.push("-W".to_string());
                    command.push(width.to_string());
                    command.push("-H".to_string());
                    command.push(height.to_string());
                }
            }
            if options.hdr == Some(true) && !args.iter().any(|a| a == "--hdr-enabled") {
                command.push("--hdr-enabled".to_string());
            }
            if options.mango_hud == Some(true) && !args.iter().any(|a| a == "--mangoapp") {
                command.push("--mangoapp".to_string());
            }
            command.push("--".to_string());
        }
        command.push(runner.to_string());
    }

    command.push(exe.to_string());
    command.extend(split_args(spec.arguments.as_deref()));
    for (key, value) in user_env {
        env.insert(key, value);
    }

    LaunchCommand {
        command,
        environment: env,
        warnings,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn splits_quoted_arguments() {
        assert_eq!(
            split_args(Some("\"hello world\" --flag")),
            vec!["hello world".to_string(), "--flag".to_string()]
        );
        assert!(split_args(None).is_empty());
        assert!(split_args(Some("   ")).is_empty());
    }

    #[test]
    fn gamemode_missing_is_a_warning_not_a_failure() {
        let spec = LaunchSpec {
            steam_app_id: Some(7),
            slug: None,
            proton_path: None,
            arguments: None,
            options: Some(LaunchOptions::app_defaults()),
        };
        let composed = compose(
            &spec,
            Some("/usr/bin/umu-run"),
            "/g/g.exe",
            Some("/p"),
            |_| None,
            None,
        );
        assert_eq!(composed.command, vec!["/usr/bin/umu-run", "/g/g.exe"]);
        assert_eq!(composed.warnings.len(), 1);
    }
}

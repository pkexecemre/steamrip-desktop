//! Port of the XDG autostart entry SteamRip.Core.Platform.Autostart writes.

/// The desktop entry for one executable path, exactly as it is written.
pub fn desktop_entry(executable_path: &str) -> String {
    format!(
        "[Desktop Entry]\nType=Application\nName=SteamRIP Desktop\nComment=Keeps your SteamRIP library up to date\nExec=\"{executable_path}\" --hidden\nIcon=steamrip-desktop\nTerminal=false\nX-GNOME-Autostart-enabled=true\nX-KDE-autostart-after=panel\n"
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn quotes_the_executable() {
        let entry = desktop_entry("/usr/bin/x");
        assert!(entry.contains("Exec=\"/usr/bin/x\" --hidden"));
        assert!(entry.ends_with("X-KDE-autostart-after=panel\n"));
    }
}

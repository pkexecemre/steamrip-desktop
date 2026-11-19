//! Port of ResolverChain's order decision: which hosters a job walks, and in what order.

use crate::hoster::Hoster;

pub const DEFAULT_ORDER: [Hoster; 4] = [
    Hoster::FileDitch,
    Hoster::GoFile,
    Hoster::MegaDb,
    Hoster::BuzzHeavier,
];

/// The order a job walks: the configured list when it says something, else the default.
pub fn effective_order(configured: &[Hoster]) -> Vec<Hoster> {
    if configured.is_empty() {
        DEFAULT_ORDER.to_vec()
    } else {
        configured.to_vec()
    }
}

/// What one mirror did when the chain tried it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Outcome {
    /// A direct link to the archive.
    Direct,
    /// A .torrent instead of the file; usable only as a last resort.
    Torrent,
    /// The message the resolver failed with, without the host prefix.
    Failed(String),
}

/// The mirror walk's result: what was chosen and everything that went wrong on the way.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Decision {
    pub chosen: Option<(Hoster, String)>,
    pub chosen_is_torrent: bool,
    pub errors: Vec<String>,
}

/// Walks the mirrors in preference order exactly like the C# chain: the first direct link wins, a
/// .torrent is only kept as a fallback (and only when torrents are allowed), and a failure is
/// reported as "Host: message".
pub fn decide(
    order: &[Hoster],
    mirrors: &[(Hoster, String)],
    outcomes: &std::collections::HashMap<String, Outcome>,
    allow_torrents: bool,
) -> Decision {
    let mut errors: Vec<String> = Vec::new();
    let mut torrent: Option<(Hoster, String)> = None;

    for host in order {
        for (mirror_host, url) in mirrors.iter().filter(|(h, _)| h == host) {
            match outcomes.get(url).cloned() {
                Some(Outcome::Direct) => {
                    return Decision {
                        chosen: Some((*mirror_host, url.clone())),
                        chosen_is_torrent: false,
                        errors,
                    };
                }
                Some(Outcome::Torrent) => {
                    if !allow_torrents {
                        errors.push(format!(
                            "{mirror_host:?}: only offers a .torrent (peer-to-peer), not the file itself"
                        ));
                        continue;
                    }
                    if torrent.is_none() {
                        torrent = Some((*mirror_host, url.clone()));
                    }
                }
                Some(Outcome::Failed(message)) => {
                    errors.push(format!("{mirror_host:?}: {message}"));
                }
                None => errors.push(format!("{mirror_host:?}: no outcome recorded")),
            }
        }
    }

    if let Some((host, url)) = torrent {
        return Decision {
            chosen: Some((host, url)),
            chosen_is_torrent: true,
            errors,
        };
    }

    if errors.is_empty() {
        let mut hosts: Vec<Hoster> = Vec::new();
        for (host, _) in mirrors {
            if !hosts.contains(host) {
                hosts.push(*host);
            }
        }
        let names: Vec<String> = hosts.iter().map(|h| format!("{h:?}")).collect();
        errors.push(format!(
            "No supported mirror on this post ({})",
            names.join(", ")
        ));
    }

    Decision {
        chosen: None,
        chosen_is_torrent: false,
        errors,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn empty_configuration_falls_back_to_the_default() {
        assert_eq!(effective_order(&[]), DEFAULT_ORDER.to_vec());
    }

    #[test]
    fn a_configured_order_is_kept_verbatim() {
        let custom = vec![Hoster::BuzzHeavier, Hoster::FileDitch];
        assert_eq!(effective_order(&custom), custom);
    }
}

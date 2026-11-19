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

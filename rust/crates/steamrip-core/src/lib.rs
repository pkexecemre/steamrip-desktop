//! Ported pieces of SteamRip Desktop's C# core, with parity checked against recordings
//! of the original (`rust/fixtures`). Every unit here answers exactly what the C#
//! implementation answers for the recorded cases; new units follow the same shape.

pub mod archive;
pub mod catalog;
pub mod host_caps;
pub mod hosters;
pub mod launch;
pub mod launch_options;
pub mod prefix;
pub mod proton;
pub mod save_locator;
pub mod updates;

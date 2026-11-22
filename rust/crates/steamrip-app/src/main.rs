//! The desktop app in Rust: one native window, no web view anywhere in the UI.
//!
//! This first slice reads the same profile the C# app writes (config dir, SQLite, cached covers)
//! and draws the library, so the two can be compared side by side while the port grows.

use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::time::Instant;

mod db;
mod download;
mod hosters;
mod sync;
use db::Db;

use iced::widget::{button, column, container, image, row, scrollable, text, Space};
use iced::{
    Alignment, Background, Border, Color, Element, Length, Padding, Radians, Shadow, Task, Theme,
};
use steamrip_core::launch::{compose, LaunchSpec};
use steamrip_core::prefix;

const INK: Color = Color::from_rgb(0.071, 0.055, 0.106); // #120E1B
const PANEL: Color = Color::from_rgb(0.094, 0.071, 0.149); // #181226
const RAISED: Color = Color::from_rgb(0.133, 0.098, 0.212); // #221936
const LINE: Color = Color::from_rgb(0.180, 0.137, 0.267); // #2E2344
const TEXT: Color = Color::from_rgb(0.949, 0.933, 0.980); // #F2EEFA
const MUTED: Color = Color::from_rgb(0.612, 0.561, 0.710); // #9C8FB5
const VIOLET: Color = Color::from_rgb(0.518, 0.220, 1.0); // #8438FF
const PINK: Color = Color::from_rgb(1.0, 0.0, 0.478); // #FF007A
const GREEN: Color = Color::from_rgb(0.373, 0.753, 0.549); // #5FC08C
const AMBER: Color = Color::from_rgb(1.0, 0.772, 0.506); // #FFC581

fn main() -> iced::Result {
    // A diagnostic that prints the composed launch without starting Proton: the launch rules live
    // in steamrip-core and this is how they are checked against the C# app's own log lines.
    let args: Vec<String> = std::env::args().collect();
    if let Some(at) = args.iter().position(|a| a == "--download") {
        let Some(post_id) = args.get(at + 1).and_then(|v| v.parse::<i64>().ok()) else {
            println!("usage: --download <post_id> [dir]");
            return Ok(());
        };
        let dir = args
            .get(at + 2)
            .map(PathBuf::from)
            .unwrap_or_else(|| PathBuf::from("/tmp/sr-fetch"));
        let profile = config_dir();
        let Ok(runtime) = tokio::runtime::Runtime::new() else {
            return Ok(());
        };
        let outcome = runtime.block_on(async move {
            let db = Db::open_readonly(&profile.join("steamrip.db"))?;
            let post = db
                .post(post_id)?
                .ok_or_else(|| format!("no post {post_id}"))?;
            let http = hosters::http()?;
            let resolved = hosters::resolve_post(&http, &post, &[]).await?;
            eprintln!(
                "resolved on {:?}: {:?}",
                resolved.host,
                resolved.file_name.as_deref().unwrap_or("?")
            );
            download::fetch(&resolved, &dir, None).await
        });
        match outcome {
            Ok(done) => println!("downloaded {} bytes to {}", done.bytes, done.path.display()),
            Err(error) => println!("download failed: {error}"),
        }
        return Ok(());
    }

    if let Some(at) = args.iter().position(|a| a == "--resolve") {
        let Some(post_id) = args.get(at + 1).and_then(|v| v.parse::<i64>().ok()) else {
            println!("usage: --resolve <post_id>");
            return Ok(());
        };
        let dir = config_dir();
        let Ok(runtime) = tokio::runtime::Runtime::new() else {
            return Ok(());
        };
        let outcome = runtime.block_on(async move {
            let db = Db::open_readonly(&dir.join("steamrip.db"))?;
            let Some(post) = db.post(post_id)? else {
                return Err(format!("no post {post_id}"));
            };
            let http = hosters::http()?;
            hosters::resolve_post(&http, &post, &[]).await
        });
        match outcome {
            Ok(resolved) => {
                let head: String = resolved.url.chars().take(96).collect();
                println!(
                    "{:?}: {} ({} bytes, headers {:?})",
                    resolved.host,
                    head,
                    resolved
                        .size
                        .map(|s| s.to_string())
                        .unwrap_or_else(|| "?".into()),
                    resolved
                        .headers
                        .iter()
                        .map(|(k, _)| k.as_str())
                        .collect::<Vec<_>>()
                );
                println!("file: {:?}", resolved.file_name);
            }
            Err(error) => println!("resolve failed: {error}"),
        }
        return Ok(());
    }

    if let Some(at) = args.iter().position(|a| a == "--sync") {
        let full = args.get(at + 1).map(|v| v == "full").unwrap_or(false);
        let dir = config_dir();
        let runtime = tokio::runtime::Runtime::new().map_err(|e| {
            eprintln!("runtime: {e}");
            e
        });
        let Ok(runtime) = runtime else {
            return Ok(());
        };
        match runtime.block_on(sync::sync(&dir, full)) {
            Ok(report) => println!(
                "synced {} post(s) over {} page(s); {} total; {} categories",
                report.posts, report.pages, report.total, report.categories
            ),
            Err(error) => println!("sync failed: {error}"),
        }
        return Ok(());
    }

    if let Some(at) = args.iter().position(|a| a == "--simulate-play") {
        let index = args
            .get(at + 1)
            .and_then(|v| v.parse::<usize>().ok())
            .unwrap_or(0);
        let seconds: i64 = args.get(at + 2).and_then(|v| v.parse().ok()).unwrap_or(60);
        let dir = config_dir();
        let (games, _) = load_games(&dir.join("steamrip.db"), &dir.join("covers"));
        let Some(game) = games.get(index) else {
            println!("no install at {index}");
            return Ok(());
        };
        match Db::open(&dir.join("steamrip.db")) {
            Ok(db) => {
                let _ = db.mark_played(game.post_id);
                let _ = db.add_playtime(game.post_id, seconds);
                let _ = db.log_event(
                    "play",
                    &format!("{} simulated {seconds}s", game.name),
                    Some(game.post_id),
                );
                let row = db
                    .installs()
                    .ok()
                    .and_then(|rows| rows.into_iter().find(|r| r.post_id == game.post_id));
                match row {
                    Some(row) => println!(
                        "{}: playtime {}s, last {} events-after {}",
                        row.name,
                        row.playtime,
                        last_played(&db, game.post_id).unwrap_or_default(),
                        event_count(&db).unwrap_or_default()
                    ),
                    None => println!("row disappeared"),
                }
            }
            Err(error) => println!("db: {error}"),
        }
        return Ok(());
    }

    if let Some(at) = args.iter().position(|a| a == "--dry-run-play") {
        let index = args
            .get(at + 1)
            .and_then(|v| v.parse::<usize>().ok())
            .unwrap_or(0);
        let dir = config_dir();
        let (games, status) = load_games(&dir.join("steamrip.db"), &dir.join("covers"));
        let prefix_root = prefix::root(
            &library_root(&dir, &games),
            read_prefix_root(&dir).as_deref(),
        );
        let opened = Db::open(&dir.join("steamrip.db")).ok();
        let mut state = App {
            games,
            selected: None,
            status,
            prefix_root,
            running: HashMap::new(),
            db: opened,
        };
        std::env::set_var("SR_DRY_RUN", "1");
        play(&mut state, index);
        println!("status: {}", state.status);
        return Ok(());
    }

    iced::application("SteamRIP Desktop (Rust)", update, view)
        .theme(|_| steamrip_theme())
        .window_size((1180.0, 760.0))
        .subscription(|_| {
            iced::time::every(std::time::Duration::from_millis(700)).map(|_| Message::Tick)
        })
        .run_with(App::new)
}

#[derive(Debug, Clone)]
struct Game {
    name: String,
    version: String,
    state: i64,
    exe: Option<String>,
    playtime: i64,
    initial: char,
    cover: Option<PathBuf>,
    post_id: i64,
    install_path: PathBuf,
    slug: String,
    steam_app_id: Option<i64>,
    proton: Option<String>,
    arguments: Option<String>,
}

#[derive(Debug, Clone)]
enum Message {
    Play(usize),
    Stop(usize),
    Tick,
    Sync,
    Synced(Result<String, String>),
}

struct Session {
    child: Child,
    started: Instant,
    post_id: i64,
    name: String,
}

struct App {
    games: Vec<Game>,
    selected: Option<usize>,
    status: String,
    prefix_root: PathBuf,
    running: HashMap<usize, Session>,
    db: Option<Db>,
}

impl App {
    fn new() -> (Self, Task<Message>) {
        let dir = config_dir();
        let (games, status) = load_games(&dir.join("steamrip.db"), &dir.join("covers"));
        let prefix_root = prefix::root(
            &library_root(&dir, &games),
            read_prefix_root(&dir).as_deref(),
        );
        let opened = Db::open(&dir.join("steamrip.db")).ok();
        (
            Self {
                games,
                selected: None,
                status,
                prefix_root,
                running: HashMap::new(),
                db: opened,
            },
            Task::none(),
        )
    }
}

fn update(state: &mut App, message: Message) -> Task<Message> {
    match message {
        Message::Play(index) => {
            play(state, index);
            Task::none()
        }
        Message::Stop(index) => {
            if let Some(mut session) = state.running.remove(&index) {
                let _ = session.child.kill();
                let _ = session.child.wait();
                settle(state, &session);
                state.status = format!("{}: stopped", session.name);
            }
            Task::none()
        }
        Message::Sync => {
            state.status = "syncing the catalogue…".into();
            let dir = config_dir();
            Task::perform(async move { sync::sync(&dir, false).await }, |result| {
                Message::Synced(
                    result
                        .map(|report| format!("{} changed · {} games", report.posts, report.total)),
                )
            })
        }
        Message::Synced(result) => {
            state.status = match result {
                Ok(line) => line,
                Err(error) => format!("sync failed: {error}"),
            };
            Task::none()
        }
        Message::Tick => {
            let mut finished = Vec::new();
            for (index, session) in state.running.iter_mut() {
                match session.child.try_wait() {
                    Ok(Some(status)) => finished.push((*index, status.code())),
                    Ok(None) => {}
                    Err(_) => finished.push((*index, None)),
                }
            }
            for (index, code) in finished {
                if let Some(session) = state.running.remove(&index) {
                    settle(state, &session);
                    state.status = format!("{} exited ({})", session.name, code.unwrap_or(-1));
                }
            }
            Task::none()
        }
    }
}

/// Composes the launch exactly like the C# app does (the rules live in steamrip-core) and starts
/// it. With SR_DRY_RUN set it only reports the command, which is how this is tested without
/// starting Proton.
fn play(state: &mut App, index: usize) {
    let Some(game) = state.games.get(index) else {
        return;
    };
    if state.running.contains_key(&index) {
        return;
    }
    let Some(exe) = game.exe.clone() else {
        state.status = format!("{}: no executable chosen yet", game.name);
        return;
    };
    let prefix_dir = prefix::dir(&state.prefix_root, &game.slug);
    let install_path = if game.install_path.as_os_str().is_empty() {
        let library = read_setting(&config_dir(), "LibraryRoot")
            .map(PathBuf::from)
            .unwrap_or_else(|| PathBuf::from("/tmp"));
        library.join(&game.slug)
    } else {
        game.install_path.clone()
    };
    let spec = LaunchSpec {
        steam_app_id: game.steam_app_id,
        slug: Some(game.slug.clone()),
        proton_path: game.proton.clone().or_else(|| {
            read_setting(&config_dir(), "DefaultProtonPath").or_else(|| Some("GE-Proton".into()))
        }),
        arguments: game.arguments.clone(),
        options: None,
    };
    let runner = find_tool("umu-run");
    let command = compose(
        &spec,
        runner.as_deref(),
        &exe,
        Some(&prefix_dir.to_string_lossy()),
        find_tool,
        None,
    );

    if command.command.is_empty() {
        state.status = format!("{}: nothing to run", game.name);
        return;
    }
    let rendered = format!("{} {}", command.command[0], command.command[1..].join(" "));
    if std::env::var("SR_DRY_RUN").is_ok() {
        eprintln!("[dry-run] env {:?} exec {}", command.environment, rendered);
        state.status = format!("{}: would run {}", game.name, command.command[0]);
        return;
    }

    let mut process = Command::new(&command.command[0]);
    process.args(&command.command[1..]);
    process.envs(&command.environment);
    process.current_dir(&install_path);
    process.stdin(Stdio::null());
    match process.spawn() {
        Ok(child) => {
            if let Some(db) = state.db.as_ref() {
                let _ = db.mark_played(game.post_id);
                let _ = db.log_event(
                    "play",
                    &format!("{} launched", game.name),
                    Some(game.post_id),
                );
            }
            state.running.insert(
                index,
                Session {
                    child,
                    started: Instant::now(),
                    post_id: game.post_id,
                    name: game.name.clone(),
                },
            );
            state.status = format!("{}: running", game.name);
        }
        Err(error) => state.status = format!("{}: could not start ({error})", game.name),
    }
}

/// Records how long the game ran, in the same columns and events the C# app uses.
fn settle(state: &App, session: &Session) {
    let seconds = session.started.elapsed().as_secs() as i64;
    if let Some(db) = state.db.as_ref() {
        let _ = db.add_playtime(session.post_id, seconds);
        let _ = db.log_event(
            "play",
            &format!("{} played {}m", session.name, seconds / 60),
            Some(session.post_id),
        );
    }
}

fn find_tool(name: &str) -> Option<String> {
    let path = std::env::var("PATH").unwrap_or_default();
    for dir in path.split(':') {
        let candidate = Path::new(dir).join(name);
        if candidate.is_file() {
            return Some(candidate.to_string_lossy().into_owned());
        }
    }
    None
}

fn read_setting(dir: &Path, key: &str) -> Option<String> {
    let text = std::fs::read_to_string(dir.join("settings.json")).ok()?;
    let value: serde_json::Value = serde_json::from_str(&text).ok()?;
    value
        .get(key)
        .and_then(|v| v.as_str())
        .filter(|s| !s.is_empty())
        .map(str::to_string)
}

fn read_prefix_root(dir: &Path) -> Option<String> {
    read_setting(dir, "PrefixRoot")
}

/// Where the library lives: the setting, else the first install's parent, else nowhere useful.
fn library_root(dir: &Path, games: &[Game]) -> PathBuf {
    read_setting(dir, "LibraryRoot")
        .map(PathBuf::from)
        .or_else(|| {
            games.iter().find_map(|g| {
                (!g.install_path.as_os_str().is_empty())
                    .then(|| g.install_path.parent().map(Path::to_path_buf))
                    .flatten()
            })
        })
        // the C# app's own default: ~/Games/SteamRIP
        .unwrap_or_else(|| {
            PathBuf::from(std::env::var("HOME").unwrap_or_else(|_| "/tmp".into()))
                .join("Games/SteamRIP")
        })
}

fn view(state: &App) -> Element<'_, Message> {
    let mark = container(text("SR").size(13).color(Color::WHITE))
        .width(Length::Fixed(26.0))
        .height(Length::Fixed(26.0))
        .align_x(Alignment::Center)
        .align_y(Alignment::Center)
        .style(|_| plate());

    let brand = row![
        mark,
        column![
            text("SteamRIP").size(15).color(TEXT),
            text("rust · native").size(10).color(MUTED)
        ]
        .spacing(1),
        Space::with_width(Length::Fill),
        text(format!("{} games · {}", state.games.len(), state.status))
            .size(11)
            .color(MUTED),
        button(text("Sync").size(11).color(TEXT))
            .padding(Padding::from([5, 12]))
            .on_press(Message::Sync)
            .style(|_, _| quiet_button()),
    ]
    .spacing(10)
    .align_y(Alignment::Center);

    let header = container(brand)
        .width(Length::Fill)
        .padding(Padding::from([14, 20]))
        .style(|_| container::Style {
            background: Some(Background::Color(PANEL)),
            border: Border {
                width: 1.0,
                color: LINE,
                radius: 0.0.into(),
            },
            ..container::Style::default()
        });

    let running: std::collections::HashSet<usize> = state.running.keys().copied().collect();
    let list = state
        .games
        .iter()
        .enumerate()
        .fold(column![].spacing(2), |col, (index, game)| {
            col.push(game_row(
                game,
                index,
                state.selected == Some(index),
                &running,
            ))
        });

    let body = scrollable(container(list).padding(Padding::from([10, 20]))).height(Length::Fill);

    container(column![header, body])
        .width(Length::Fill)
        .height(Length::Fill)
        .style(|_| container::Style {
            background: Some(Background::Color(INK)),
            ..container::Style::default()
        })
        .into()
}

fn game_row<'a>(
    game: &'a Game,
    index: usize,
    selected: bool,
    running_index: &std::collections::HashSet<usize>,
) -> Element<'a, Message> {
    let art: Element<'a, Message> = match &game.cover {
        Some(path) => image(image::Handle::from_path(path))
            .width(Length::Fixed(56.0))
            .height(Length::Fixed(76.0))
            .content_fit(iced::ContentFit::Cover)
            .into(),
        None => container(text(game.initial.to_string()).size(22).color(MUTED))
            .width(Length::Fixed(56.0))
            .height(Length::Fixed(76.0))
            .align_x(Alignment::Center)
            .align_y(Alignment::Center)
            .style(|_| container::Style {
                background: Some(Background::Color(RAISED)),
                border: Border {
                    radius: 3.0.into(),
                    width: 1.0,
                    color: LINE,
                },
                ..container::Style::default()
            })
            .into(),
    };

    let (state_word, state_color) = match game.state {
        4 => ("installed", GREEN),
        5 => ("stopped", AMBER),
        6 => ("needs you", PINK),
        _ => ("queued", MUTED),
    };

    let meta = row![
        text(game.version.clone()).size(11.5).color(MUTED),
        text("·").size(11.5).color(LINE),
        text(state_word).size(11.5).color(state_color),
        text("·").size(11.5).color(LINE),
        text(format!("{} h played", game.playtime / 3600))
            .size(11.5)
            .color(MUTED),
    ]
    .spacing(6);

    let title = column![
        text(game.name.clone()).size(15).color(TEXT),
        meta,
        text(
            game.exe
                .clone()
                .unwrap_or_else(|| "no executable yet".into())
        )
        .size(10.5)
        .color(MUTED),
    ]
    .spacing(3)
    .width(Length::Fill);

    let running = running_index.contains(&index);
    let play = container(
        text(if running { "Stop" } else { "Play" })
            .size(12)
            .color(Color::WHITE),
    )
    .padding(Padding::from([7, 20]))
    .style(|_| play_plate());

    let content = row![art, title, play]
        .spacing(14)
        .align_y(Alignment::Center)
        .padding(Padding::from([10, 8]));

    button(content)
        .padding(Padding::from([0, 0]))
        .width(Length::Fill)
        .on_press(if running {
            Message::Stop(index)
        } else {
            Message::Play(index)
        })
        .style(move |_, status| row_style(selected, status))
        .into()
}

fn steamrip_theme() -> Theme {
    Theme::custom(
        "SteamRIP".to_string(),
        iced::theme::Palette {
            background: INK,
            text: TEXT,
            primary: VIOLET,
            success: GREEN,
            danger: PINK,
        },
    )
}

fn plate() -> container::Style {
    container::Style {
        background: Some(Background::Gradient(
            iced::gradient::Linear::new(Radians(0.7))
                .add_stop(0.0, VIOLET)
                .add_stop(1.0, PINK)
                .into(),
        )),
        border: Border {
            radius: 8.0.into(),
            ..Border::default()
        },
        ..container::Style::default()
    }
}

fn play_plate() -> container::Style {
    container::Style {
        background: Some(Background::Gradient(
            iced::gradient::Linear::new(Radians(0.0))
                .add_stop(0.0, VIOLET)
                .add_stop(1.0, PINK)
                .into(),
        )),
        border: Border {
            radius: 4.0.into(),
            ..Border::default()
        },
        shadow: Shadow {
            color: Color::from_rgba(0.0, 0.0, 0.0, 0.45),
            offset: iced::Vector::new(0.0, 3.0),
            blur_radius: 10.0,
        },
        ..container::Style::default()
    }
}

fn quiet_button() -> button::Style {
    button::Style {
        background: Some(Background::Color(RAISED)),
        text_color: TEXT,
        border: Border {
            width: 1.0,
            color: LINE,
            radius: 4.0.into(),
        },
        ..button::Style::default()
    }
}

fn row_style(selected: bool, status: button::Status) -> button::Style {
    let hovered = matches!(status, button::Status::Hovered | button::Status::Pressed);
    let background = if selected {
        RAISED
    } else if hovered {
        Color::from_rgb(0.122, 0.090, 0.188)
    } else {
        Color::TRANSPARENT
    };
    button::Style {
        background: Some(Background::Color(background)),
        text_color: TEXT,
        border: Border {
            width: 1.0,
            color: if selected { VIOLET } else { LINE },
            radius: 5.0.into(),
        },
        ..button::Style::default()
    }
}

fn config_dir() -> PathBuf {
    if let Ok(dir) = std::env::var("XDG_CONFIG_HOME") {
        return PathBuf::from(dir).join("steamrip-desktop");
    }
    let home = std::env::var("HOME").unwrap_or_else(|_| "/tmp".into());
    PathBuf::from(home).join(".config/steamrip-desktop")
}

/// Reads the rows the C# app writes; the Rust UI does not own the database yet.
fn load_games(db_path: &Path, covers: &Path) -> (Vec<Game>, String) {
    let db = match Db::open_readonly(db_path) {
        Ok(db) => db,
        Err(error) => return (Vec::new(), format!("no database ({error})")),
    };
    let rows = match db.installs() {
        Ok(rows) => rows,
        Err(error) => return (Vec::new(), format!("query failed ({error})")),
    };
    let games: Vec<Game> = rows
        .into_iter()
        .map(|row| {
            let cover = row
                .app_id
                .map(|id| covers.join(format!("{id}.jpg")))
                .filter(|path| path.exists());
            Game {
                initial: row.name.chars().next().unwrap_or('?'),
                name: row.name,
                version: row.version,
                state: row.state,
                exe: row.exe,
                playtime: row.playtime,
                cover,
                post_id: row.post_id,
                install_path: if row.install_path.is_empty() {
                    PathBuf::new()
                } else {
                    PathBuf::from(row.install_path)
                },
                slug: row.slug,
                steam_app_id: row.app_id,
                proton: row.proton,
                arguments: row.arguments,
            }
        })
        .collect();
    let status = if games.is_empty() {
        "nothing installed".to_string()
    } else {
        "same database as the C# app".to_string()
    };
    (games, status)
}

fn last_played(db: &Db, post_id: i64) -> Option<String> {
    db.install_field(post_id, "last_played_utc")
}

fn event_count(db: &Db) -> Option<i64> {
    db.scalar_i64("select count(*) from events")
}

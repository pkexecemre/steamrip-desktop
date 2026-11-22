//! The desktop app in Rust: one native window, no web view anywhere in the UI.
//!
//! This first slice reads the same profile the C# app writes (config dir, SQLite, cached covers)
//! and draws the library, so the two can be compared side by side while the port grows.

use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};

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
        let mut state = App {
            games,
            selected: None,
            status,
            prefix_root,
            running: HashMap::new(),
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
}

struct App {
    games: Vec<Game>,
    selected: Option<usize>,
    status: String,
    prefix_root: PathBuf,
    running: HashMap<usize, Child>,
}

impl App {
    fn new() -> (Self, Task<Message>) {
        let dir = config_dir();
        let (games, status) = load_games(&dir.join("steamrip.db"), &dir.join("covers"));
        let prefix_root = prefix::root(
            &library_root(&dir, &games),
            read_prefix_root(&dir).as_deref(),
        );
        (
            Self {
                games,
                selected: None,
                status,
                prefix_root,
                running: HashMap::new(),
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
            if let Some(mut child) = state.running.remove(&index) {
                let _ = child.kill();
                let _ = child.wait();
                state.status = "stopped".into();
            }
            Task::none()
        }
        Message::Tick => {
            let mut finished = Vec::new();
            for (index, child) in state.running.iter_mut() {
                match child.try_wait() {
                    Ok(Some(status)) => finished.push((*index, status.code())),
                    Ok(None) => {}
                    Err(_) => finished.push((*index, None)),
                }
            }
            for (index, code) in finished {
                state.running.remove(&index);
                let name = state
                    .games
                    .get(index)
                    .map(|g| g.name.clone())
                    .unwrap_or_default();
                state.status = format!("{name} exited ({})", code.unwrap_or(-1));
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
            state.running.insert(index, child);
            state.status = format!("{}: running", game.name);
        }
        Err(error) => state.status = format!("{}: could not start ({error})", game.name),
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
    let conn = match rusqlite::Connection::open_with_flags(
        db_path,
        rusqlite::OpenFlags::SQLITE_OPEN_READ_ONLY,
    ) {
        Ok(conn) => conn,
        Err(error) => return (Vec::new(), format!("no database ({error})")),
    };
    // Covers are cached per Steam app id, so the row has to come along with the install.
    let mut statement = match conn.prepare(
        "select i.name, coalesce(i.installed_version, ''), i.state, i.exe_rel, \
                coalesce(i.playtime_sec, 0), coalesce(p.steam_app_id, 0), \
                i.install_path, i.slug, i.proton, i.launch_args \
         from installs i left join posts p on p.id = i.post_id \
         order by i.installed_utc desc",
    ) {
        Ok(statement) => statement,
        Err(error) => return (Vec::new(), format!("query failed ({error})")),
    };
    let rows = statement.query_map([], |row| {
        Ok((
            row.get::<_, String>(0)?,
            row.get::<_, String>(1)?,
            row.get::<_, i64>(2)?,
            row.get::<_, Option<String>>(3)?,
            row.get::<_, i64>(4)?,
            row.get::<_, i64>(5)?,
            row.get::<_, Option<String>>(6)?,
            row.get::<_, String>(7)?,
            row.get::<_, Option<String>>(8)?,
            row.get::<_, Option<String>>(9)?,
        ))
    });
    let mut games = Vec::new();
    if let Ok(rows) = rows {
        for (name, version, state, exe, playtime, app_id, install_path, slug, proton, arguments) in
            rows.flatten()
        {
            let cover = covers.join(format!("{app_id}.jpg"));
            games.push(Game {
                initial: name.chars().next().unwrap_or('?'),
                name,
                version,
                state,
                exe,
                playtime,
                cover: cover.exists().then_some(cover),
                install_path: install_path.map(PathBuf::from).unwrap_or_default(),
                slug,
                steam_app_id: (app_id > 0).then_some(app_id),
                proton,
                arguments,
            });
        }
    }
    let status = if games.is_empty() {
        "nothing installed".to_string()
    } else {
        "same database as the C# app".to_string()
    };
    (games, status)
}

//! The desktop app in Rust: one native window, no web view anywhere in the UI.
//!
//! This first slice reads the same profile the C# app writes (config dir, SQLite, cached covers)
//! and draws the library, so the two can be compared side by side while the port grows.

use std::path::{Path, PathBuf};

use iced::widget::{button, column, container, image, row, scrollable, text, Space};
use iced::{
    Alignment, Background, Border, Color, Element, Length, Padding, Radians, Shadow, Subscription,
    Task, Theme,
};

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
    iced::application("SteamRIP Desktop (Rust)", update, view)
        .theme(|_| steamrip_theme())
        .window_size((1180.0, 760.0))
        .subscription(|_| Subscription::none())
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
}

#[derive(Debug, Clone)]
enum Message {
    Select(usize),
}

struct App {
    games: Vec<Game>,
    selected: Option<usize>,
    status: String,
}

impl App {
    fn new() -> (Self, Task<Message>) {
        let dir = config_dir();
        let (games, status) = load_games(&dir.join("steamrip.db"), &dir.join("covers"));
        (
            Self {
                games,
                selected: None,
                status,
            },
            Task::none(),
        )
    }
}

fn update(state: &mut App, message: Message) -> Task<Message> {
    match message {
        Message::Select(index) => {
            state.selected = Some(index);
            Task::none()
        }
    }
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

    let header = container(brand).width(Length::Fill).padding(Padding::from([14, 20]));

    let list = state.games.iter().enumerate().fold(
        column![].spacing(2),
        |col, (index, game)| col.push(game_row(game, index, state.selected == Some(index))),
    );

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

fn game_row<'a>(game: &'a Game, index: usize, selected: bool) -> Element<'a, Message> {
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
        text(game.exe.clone().unwrap_or_else(|| "no executable yet".into()))
            .size(10.5)
            .color(MUTED),
    ]
    .spacing(3)
    .width(Length::Fill);

    let play = container(text("Play").size(12).color(Color::WHITE))
        .padding(Padding::from([7, 20]))
        .style(|_| play_plate());

    let content = row![art, title, play]
        .spacing(14)
        .align_y(Alignment::Center)
        .padding(Padding::from([10, 8]));

    button(content)
        .padding(Padding::from([0, 0]))
        .width(Length::Fill)
        .on_press(Message::Select(index))
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
                coalesce(i.playtime_sec, 0), coalesce(p.steam_app_id, 0) \
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
        ))
    });
    let mut games = Vec::new();
    if let Ok(rows) = rows {
        for (name, version, state, exe, playtime, app_id) in rows.flatten() {
            let cover = covers.join(format!("{app_id}.jpg"));
            games.push(Game {
                initial: name.chars().next().unwrap_or('?'),
                name,
                version,
                state,
                exe,
                playtime,
                cover: cover.exists().then_some(cover),
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

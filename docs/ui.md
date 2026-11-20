# Interface

The app is a machine that fetches, unpacks and launches things. The interface should read like a
control surface built by someone who uses it: dense, legible, quiet until something needs you.
No decoration, no greeting, no cards.

## Direction

Cold ink background, one signal colour, mono for anything a machine produced. The layout is a
command bar across the top, a rail of sections down the left, and a single content column that
behaves like a table more than a page. Sections are separated by hairlines, never by boxes.

Deliberately avoided: purple or blue-violet accents, gradients, glowing edges, rounded cards with
soft shadows, uppercase micro-labels over every heading, middle-dot meta strings, emoji icons.

## Tokens

| Token | Value | Use |
|---|---|---|
| Ink | `#0B0E12` | window background |
| Panel | `#10141A` | rail and command bar |
| Raised | `#171C24` | hover, inputs, pressed |
| Line | `#232A34` | every separator, 1px |
| Text | `#E9EBEF` | body |
| Muted | `#8B94A3` | secondary, labels |
| Amber | `#F5B62E` | current section, primary action, progress |
| Green | `#5FC08C` | running, done, healthy |
| Red | `#E05D52` | failed, needs attention |
| Steel | `#6F8BA8` | neutral information only |

Type: IBM Plex Sans for the interface, IBM Plex Mono for numbers, versions, paths, ids, timings.
Scale: 11 micro, 12.5 body, 14 row title, 16 section, 22 page title, 32 empty-state statement.
Display sizes use -0.4 tracking; body line height 1.45.

Space: 4, 8, 12, 16, 24, 32. Corner radius 2. Shadow only on the launch sheet.

## Rules

- A row is a row: content on one line, actions right, hairline underneath, hover tints the row.
- Numbers are mono and right-aligned when they sit in a column.
- Amber appears at most twice on screen: the current section and the primary action.
- Empty states are a statement, one sentence, and one action.
- Every control keeps a visible keyboard focus (1px amber outline).
- Nothing animates unless it is answering an action.

## Layout

```
┌──────────────────────────────────────────────────────────────┐
│ steamrip / Library          ● synced · 1 update   [ sync ]   │  command bar, 48px
├───────────┬──────────────────────────────────────────────────┤
│ Library   │  Library                        [Browse] [Sync]  │  page header
│ Browse    │  3 games, 3.4 h played                           │
│ Queue  2  │  ──────────────────────────────────────────────  │
│ Activity  │  ▣  Blue Prince            Build 18392011  3.4GB │  rows, hairline
│ Settings  │  ▣  Castle Crashers         24095717     130 MB  │
└───────────┴──────────────────────────────────────────────────┘
```

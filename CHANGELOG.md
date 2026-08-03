# Changelog

All notable TerminalStateGuard changes are documented here.

## [2.1.7] - 2026-08-04

### Fixed

- Fall back from `copilot --resume=<id>` to `copilot --session-id=<id>` only
  when Copilot reports that no session, task, or name matches, without treating
  an interrupted valid session as a missing one
- Handle mouse-wheel events while TSG Recover mouse tracking is enabled
- Fix clicked-row result handling so mouse clicks reliably open the selected session

## [2.1.6] - 2026-08-04

### Fixed

- Resume sessions in a new tab of the currently active Windows Terminal window
- Preserve the calling shell application (`pwsh`, Windows PowerShell, or `cmd`)
- Carry the original shell through `TSG Recover` click actions
- Replace external `file://` links in `TSG Recover` with native in-terminal mouse input
- Remove the Windows Terminal unsafe-location warning from recovery clicks

## [2.1.5] - 2026-08-04

### Added

- Clickable session IDs throughout `tsg recover`
- Local `file://` launchers for tracked-window tabs, open tabs, and stored sessions

## [2.1.4] - 2026-08-04

### Fixed

- Replace custom `tsg://` OSC 8 targets with supported local `file://` launchers
  so Ctrl+click works on Windows Terminal versions that reject custom schemes
- Validate launcher session IDs as GUIDs and store launchers under
  `~/.tsg/session-links/`

## [2.1.3] - 2026-08-04

### Documentation and release integrity

- Document clickable Copilot session IDs, `tsg://` registration, and `tsg resume`
- Document `--limit`, `-n`, `--all`, and interactive `[+]/[-]` list controls
- Explain title/path/timestamp correlation for legacy closed-window captures
- Correct security reports that previously claimed zero external dependencies
- Display the installed TSG package version in Session Recovery instead of the internal script version
- Show the Windows Terminal `Ctrl+click` gesture beside clickable session IDs
- Add `tsg recover -n N`, `--limit N`, and `--all` for lists larger than 100

## [2.1.2] - 2026-08-04

### Added

- Clickable OSC 8 Copilot session IDs in `tsg windows`
- `tsg resume <sessionId>` to open a session in a new Windows Terminal tab
- Per-user `tsg://resume/<sessionId>` protocol registration through `tsg install`
- Optional large lists with `tsg windows -n N`, `--limit N`, and `--all`
- Interactive `[+]/[-]` controls for the closed-window display limit

### Security

- Pin `SQLitePCLRaw.lib.e_sqlite3` 2.1.12 to replace vulnerable transitive 2.1.11
- Pass dependency vulnerability scanning and strict `-warnaserror` static analysis

### Fixed

- Use `System.Uri` for terminal hyperlink APIs
- Rename the resume command type to avoid cross-language reserved-word conflicts

## [2.0.4] - 2026-04-18

- Restore legacy sessions using working-directory, title, and nearest-timestamp scoring
- Preserve tab ordering, Copilot session IDs, and working directories during restore

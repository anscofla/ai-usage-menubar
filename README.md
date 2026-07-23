# AI Usage Menubar

A tiny native macOS menu bar app that shows your Claude usage limits at a glance — `✳ 64 79 80%` (session / weekly / model-scoped utilization), always visible, refreshed every 60 seconds.

한국어 문서: [README.ko.md](README.ko.md)

## How it works

The app reuses the OAuth token that [Claude Code](https://claude.com/claude-code) stores in the macOS Keychain (`Claude Code-credentials`) and polls Anthropic's usage endpoint (`/api/oauth/usage`). No credentials of its own, no data leaves your machine except the API call to Anthropic.

The dropdown panel shows each limit with its reset countdown, plus manual refresh and quit.

## Requirements

- macOS 13+
- Swift toolchain (Xcode Command Line Tools are enough — `xcode-select --install`)
- Claude Code installed and logged in at least once (that's where the token comes from)

## Build & install

```bash
bash make_app.sh          # builds and bundles → dist/AI Usage.app
open "dist/AI Usage.app"
```

- First launch prompts once for Keychain access — choose "Always Allow".
- Auto-start: add `dist/AI Usage.app` to System Settings → General → Login Items (manual).
- The bundle is ad-hoc signed. If you download a prebuilt release instead of building yourself, macOS will warn about an unidentified developer — right-click → Open to bypass, or just build from source.

## Tests

```bash
swift run AIUsageTests    # assert-based harness (works without XCTest), exits 1 on failure
```

## Known limitations (v1)

- Relies on an **unofficial** Anthropic endpoint — if the schema changes, the app degrades to a `⚠︎` indicator in the menu bar (no crash).
- Assumes exactly one `weekly_scoped` limit; a second scoped model is reported as a schema error (`⚠︎`).
- Token is whatever Claude Code last refreshed — if it expires from long inactivity, run Claude Code once.
- Claude only for now. The `UsageProvider` protocol is the extension point for other providers (e.g. Codex) in v2.

## Design notes

See [docs/DESIGN.md](docs/DESIGN.md) and [docs/PLAN.md](docs/PLAN.md) (Korean) for the spec and the TDD build log.

## Windows (system tray)

A .NET port lives in `windows/` — tray icon shows the highest utilization with traffic-light coloring; right-click for details.

- Requirements: Windows 10+, Claude Code installed and logged in with a subscription (OAuth) account **natively on Windows**. WSL installs and API-key/Bedrock/Vertex auth are not supported (the token file isn't where the app can see it).
- Token source: `%USERPROFILE%\.claude\.credentials.json` (or `%CLAUDE_CONFIG_DIR%`).
- Build from source (recommended): install the .NET 10 SDK, then
  `dotnet publish windows/AIUsage.Tray -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true`
- Prebuilt exe: see Releases (~45 MB zip, self-contained — no .NET install needed; SHA-256 checksums attached). It is unsigned, so SmartScreen will warn — building from source avoids this. Self-contained builds don't receive .NET runtime patches automatically; republished on .NET patch releases as needed.

## License

[MIT](LICENSE)

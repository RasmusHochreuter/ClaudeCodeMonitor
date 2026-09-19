# ClaudeCodeMonitor

## Conventions
- .NET 10 worker service, C#, `Nullable` + `ImplicitUsings` enabled
- Options pattern with static `SectionName` constants, bound from `appsettings.json` (style reference: `C:\src\KitchenDashboard\Program.cs`)
- Typed HttpClients registered via `AddHttpClient`
- `~/.claude` is read-only territory: never write, lock, or log anything from `.credentials.json`
- No secrets in `appsettings.json` or the repo

## Specs
- .claude/specs/awtrix-claude-usage-monitor.md

# Changelog

## [1.0.2] - 2026-02-11

### Fixed

- **CRITICAL**: Fixed configuration binding bug in `Load()` method
  - Changed `.BindConfiguration(ConfigFileName)` to `.BindConfiguration(ConfigSection)`
  - This bug caused all config values to use hardcoded defaults instead of reading from `k4-gotv.jsonc`
  - **Impact**: Plugin configuration was completely non-functional - all upload settings, auto-record, retention policies were ignored

## [1.0.1] - 2025-12-12

### Changed

- **Config System**: Replaced `IOptions<PluginConfig>.Value` with `IOptionsMonitor<PluginConfig>` static property pattern
  - Config is now accessed via `Config.CurrentValue` instead of private `_config` field
  - Enables hot-reload configuration changes without plugin restart
  - Config file renamed from `config.json` to `k4-gotv.jsonc`

- **GameRules Handling**: Replaced custom `GetGameRules()` helper with `Core.EntitySystem.GetGameRules()`
  - Removed `_gameRules` cached field - now fetched fresh when needed
  - Removed `CCSGameRulesProxy` manual entity lookup
  - Uses SwiftlyS2's built-in helper method for consistency

### Fixed

- DemoRequest logic now properly enforces `AutoRecord.Enabled = true` and `AutoRecord.CropRounds = true` when `DemoRequest.Enabled` is set (moved to config property getter)

### Technical Details

- All `_config.X` references updated to `Config.CurrentValue.X` across:
  - Plugin.cs
  - Plugin.Events.cs
  - Plugin.Recording.cs
  - Plugin.FileOps.cs
  - Plugin.Upload.cs

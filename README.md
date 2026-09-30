# Automatic Local Achievements for Playnite

[简体中文](README.zh-CN.md) · English

A Playnite extension that watches **local achievement events or state files** for non-Steam games, shows an achievement toast, and can synchronize the unlock time to an existing [SuccessStory](https://github.com/Lacro59/playnite-successstory-plugin) manual achievement list. Release: **v1.1**.

It does not unlock achievements on Steam, bypass DRM, or invent completed achievements. A definition file alone is not an unlock event: the game and its local achievement provider must actually record an unlock. One optional RPG Maker MV compatibility path backs up and edits a game script **only after an explicit confirmation**; see below.

## Requirements and dependencies

- **Required:** Windows and [Playnite](https://github.com/JosefNemec/Playnite) Desktop. Built and tested against the locally installed Playnite 10.62 SDK. The installer is a `.pext` extension package.
- **For achievement lists, icons, and recorded unlock times:** [SuccessStory](https://github.com/Lacro59/playnite-successstory-plugin). Live synchronization was tested with SuccessStory **3.7.1**. Add/import the game's Steam achievement list **in SuccessStory first**; this extension does not import or replace that list. Without SuccessStory, local monitoring and toasts can still work, but there is no SuccessStory list to update.
- **For automatic GSE or RUNE/CODEX detection:** the game must already have a compatible local provider/configuration. This project does **not** bundle GSE, CODEX, RUNE, game files, or any emulator DLL. Neither [GSE Fork](https://github.com/Detanup01/gbe_fork) nor Achievement Watcher Next is installed or required.

## Install and use

1. Download `LocalAchievements_1.1.pext` from [Releases](https://github.com/Canary-235/PlayniteLocalAchievements/releases/tag/v1.1), open it with Playnite, and restart Playnite when prompted.
2. Add the game to Playnite. Set its **Installation directory** and a working **Play action**. Launching through Playnite is required for live monitoring.
3. In SuccessStory, manually add/import that game's Steam achievement list. Its `ApiName` IDs must match the IDs written by the game's local achievement provider.
4. Right-click the game → **Automatic Local Achievements** → **Detect emulator and configure rules**. Read the result and compatibility check. If a safely identifiable GSE definition or RUNE/CODEX interface is missing, detection attempts the relevant completion automatically and reports what happened.
5. Launch the game **from Playnite** and earn a *new* achievement in-game. The extension watches the local file and shows a toast. If SuccessStory has a matching `ApiName`, it records the unlock time and refreshes its live counters.

The interface defaults to Chinese. To switch languages, open **Add-ons → Generic → Automatic Local Achievements**, choose English or Chinese, save, then accept or decline the restart prompt. The choice takes effect after a restart. Game-provided achievement names and descriptions are not translated.

The game menu also offers **File completion → GSE achievement definitions / RUNE/CODEX achievement interface** for a manual recheck, **View achievement source health** for diagnostics, **Test achievement pop-up** for display testing, and **Rescan and synchronize local achievements now** to reconcile already-written local state. The test pop-up does **not** unlock an achievement.

### What file completion can and cannot do

- **GSE / Goldberg:** when the active Steam API DLL and App ID identify one unambiguous target, the extension can create a *missing* `steam_settings/achievements.json` from Steam's public achievement-definition endpoint. It does not overwrite an existing file or mark anything earned.
- **RUNE / CODEX:** when the App ID matches and an adjacent original Steam API file contains exactly one interface version, the extension can back up `steam_emu.ini` and add a missing `SteamUserStats` entry. It does not create GSE's `achievements.json`, nor fabricate `achievements.ini` or `Achieved=1` entries.
- If there are multiple candidate directories, a mismatched App ID, an unknown interface version, or no local unlock reporting, the extension refuses to guess. It cannot replay achievements that a previously played game never wrote to local state.

For a recognized RPG Maker MV / Greenworks achievement script, detection asks permission before backing up and adding a small event logger to that script. The original achievement call remains in place. The logger records IDs and timestamps in the game's save directory. Existing saves are not converted into unlocks; removing the rule restores the backup only if the script has not changed since installation.

## If an achievement does not appear

Check **View achievement source health** for the monitored path, recent writes, detected unlock IDs, and SuccessStory `ApiName` matches. A configuration/definition file proves only that metadata is present; it does not prove that the game calls the achievement API. Play through a clearly new achievement condition and see whether the provider writes `earned=true` (GSE) or `Achieved=1` (RUNE/CODEX). If that state is missing, the extension has no trustworthy event to show. A topmost desktop toast may also be hidden by an exclusive-fullscreen game; borderless-window mode is more reliable.

## Custom rules

For unsupported providers, right-click the game → **Import automatic achievement rule file...** and choose a JSON rule. A source can watch appended text (`log`) or a full text file (`snapshot`). The captured `achievementId` must match SuccessStory's `ApiName` for synchronization.

```json
{
  "schemaVersion": 1,
  "name": "Example game",
  "sources": [
    {
      "type": "log",
      "path": "%USERPROFILE%\\AppData\\LocalLow\\Example\\Player.log",
      "encoding": "utf-8",
      "match": "Achievement unlocked: (?<achievementId>[A-Za-z0-9_]+)",
      "achievementGroup": "achievementId"
    }
  ]
}
```

An optional `unlockedAt` capture group with an ISO timestamp preserves an event's original time. The [rules](rules) directory contains game-specific examples; they are **not** universal achievement detectors.

## Build from source

Use Windows PowerShell and the .NET Framework compiler included with Windows. Pass the directory containing `Playnite.SDK.dll`:

```powershell
.\build.ps1 -PlaynitePath 'C:\Path\To\Playnite'
.\test-plugin.ps1 -PlaynitePath 'C:\Path\To\Playnite'
```

The build writes `LocalAchievements_1.1.pext` beside this source directory. Tests require network access for Steam's public achievement definitions; installed SuccessStory is checked when present. Optional game-folder discovery roots can be supplied as a semicolon-separated `PLAYNITE_LOCAL_ACHIEVEMENTS_GAME_ROOTS` environment variable; otherwise configure the Playnite installation directory or play action.

## Acknowledgements and attribution

- [Playnite](https://github.com/JosefNemec/Playnite) by Josef Nemec provides the host application and SDK.
- [SuccessStory](https://github.com/Lacro59/playnite-successstory-plugin) by Lacro59, with contributions from eFMann and others, provides the achievement list and UI that this extension optionally updates. It is an independent project, not bundled here.
- [Playnite Achievements — Santodan fork](https://github.com/Santodan/PlayniteAchievements) (based on [Justin Delano's project](https://github.com/justin-delano/PlayniteAchievements)), [Local Achievements](https://github.com/uWaazy/Local-Achievements) by uWaazy, and [Achievement Watcher Next](https://github.com/Shirowwww/Achievement-Watcher-Next) informed the investigation of local achievement sources and UI workflows.
- [GSE Fork](https://github.com/Detanup01/gbe_fork) and its configuration-generator documentation informed the `achievements.json` format and compatibility checks. This extension implements its own limited definition completion; it does not embed or run GSE's generator.

These acknowledgements describe **ideas and interoperability references**, not copied source files or assets. No binaries, art, sounds, or game files from the named projects are included. Their names and trademarks remain their owners' property. This project is not affiliated with Playnite, SuccessStory, Valve, or the referenced projects.

## License

[MIT](LICENSE). Copyright © 2026 Canary-235.

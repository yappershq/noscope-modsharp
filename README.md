<div align="center">
  <h1><strong>NoScope</strong></h1>
  <p>Announces no-scope sniper kills in CS2 chat with the kill distance, and keeps a MySQL distance leaderboard.</p>
</div>

<p align="center">
  <a href="https://github.com/Kxnrl/modsharp-public"><img src="https://img.shields.io/badge/framework-ModSharp-5865F2?logo=github" alt="ModSharp"></a>
  <img src="https://img.shields.io/badge/game-CS2-orange" alt="CS2">
  <img src="https://img.shields.io/github/stars/yappershq/noscope-modsharp?style=flat&logo=github" alt="Stars">
</p>

---

A [ModSharp](https://github.com/Kxnrl/modsharp-public) plugin for CS2. When a player lands a sniper kill without scoping in, NoScope announces it in chat with the distance between attacker and victim, and (optionally) records every qualifying kill in MySQL for a distance leaderboard. Ported from the CounterStrikeSharp NoScope plugin.

## 🚀 Install

Copy the build output into your ModSharp install (`<sharp>` = your `sharp` directory):

| From | To |
|------|----|
| `.build/modules/NoScope/` | `<sharp>/modules/NoScope/` |
| `.build/locales/noscope.json` | `<sharp>/locales/noscope.json` |

Restart the server (or change map) to load. On first run the plugin writes `<sharp>/configs/noscope.jsonc`.

Localized chat requires the **LocalizerManager** module (ships with ModSharp); without it, messages fall back to raw keys. The MySQL database is optional — leave the DB credentials blank and kills are still announced, just not recorded.

## ⌨️ Commands

Chat commands also work in console with the `ms_` prefix. Both share a per-player 5-second cooldown.

| Command | Aliases | Description |
|---------|---------|-------------|
| `!noscopetop` | `!nstop` | Show the top recorded no-scope kills, ranked by distance. |

## ⚙️ Configuration

`<sharp>/configs/noscope.jsonc` (auto-generated on first run):

| Setting | Default | Meaning |
|---------|---------|---------|
| `MinDistanceToRecord` | `500.0` | Minimum distance (game units) for a kill to be saved to the DB. Shorter kills are still announced, just not recorded. |
| `TopRecordsLimit` | `10` | Number of rows shown by `!noscopetop` / `!nstop` (clamped 1–100). |
| `NotificationTarget` | `"All"` | Who sees the announcement: `All`, `AttackerOnly`, `VictimOnly`, `AttackerAndVictim`, `None`. |
| `DatabaseHost` | `"localhost"` | MySQL host. |
| `DatabasePort` | `3306` | MySQL port. |
| `DatabaseName` | `"noscope"` | MySQL database name. |
| `DatabaseUser` | `""` | MySQL user. Leave blank to disable persistence. |
| `DatabasePassword` | `""` | MySQL password. |

### Database setup

Persistence and the leaderboard use MySQL via SqlSugar. Create a database and user, then fill in the credentials above:

```sql
CREATE DATABASE noscope CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
CREATE USER 'noscope'@'%' IDENTIFIED BY 'your-password';
GRANT ALL PRIVILEGES ON noscope.* TO 'noscope'@'%';
FLUSH PRIVILEGES;
```

The `noscope_kills` table is created automatically on load; the connection pool is capped at 4. If `DatabaseUser` is blank, the plugin still loads and announces kills — it just won't persist anything or serve the leaderboard.

## 🔧 How it works

NoScope hooks the `player_death` game event. A kill counts as a no-scope when the engine's `noscope` flag is set **and** the weapon is a sniper rifle (`awp`, `ssg08`, `scar20`, `g3sg1`) — no netvar reads needed. The distance is the 3D distance between the attacker and victim pawns. Qualifying kills (attacker/victim names + SteamIDs, distance, weapon, headshot, map, UTC timestamp) are recorded when the distance meets `MinDistanceToRecord`, and the attacker is privately told their running no-scope count. Database work runs off the main thread, with chat follow-ups marshalled back and the player re-validated by SteamID inside the callback.

## 📦 Build

Requires the .NET 10 SDK.

```bash
dotnet build -c Release
```

Outputs the module to `.build/modules/NoScope/` (DLL + dependencies) and the locale to `.build/locales/noscope.json`.

## 🙏 Credits

Port of the CounterStrikeSharp **NoScope** plugin to ModSharp.

---

<div align="center">
  <p>Made with ❤️ by <a href="https://github.com/yappershq">yappershq</a></p>
  <p>⭐ Star this repo if you find it useful!</p>
</div>

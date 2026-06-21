# NoScope (ModSharp)

A [ModSharp](https://github.com/Kxnrl/modsharp-public) plugin for CS2 that announces
**no-scope sniper kills** in chat with the distance between attacker and victim, and records
every qualifying kill in a MySQL database for a distance leaderboard.

This is a port of the CounterStrikeSharp `NoScope` plugin to ModSharp.

## What it does

- Hooks the `player_death` game event.
- A kill counts as a no-scope when the engine's `noscope` flag is set **and** the weapon is
  a sniper rifle (`awp`, `ssg08`, `scar20`, `g3sg1`) — i.e. the attacker fired without being scoped.
- Announces the kill in chat: `<attacker> no-scoped <victim> from <distance> units away! [HEADSHOT!]`
- Records the kill (attacker/victim names + SteamIDs, distance, weapon, headshot flag, map,
  UTC timestamp) in MySQL, if the distance is at least `MinDistanceToRecord`.
- After a recorded kill, privately tells the attacker their running no-scope count.

## Commands

Chat (also works in console with the `ms_` prefix):

| Command | Description |
|---|---|
| `!noscopetop` | Show the top recorded no-scope kills (by distance). |
| `!nstop` | Alias of `!noscopetop`. |

Both commands have a per-player 5-second cooldown.

## Config

On first load the plugin writes `<sharp>/configs/noscope.jsonc`:

```jsonc
{
  // Minimum distance (game units) for a no-scope to be saved to the database.
  // Kills under this distance are still announced in chat, just not recorded.
  "MinDistanceToRecord": 500.0,

  // Number of rows shown by !noscopetop / !nstop.
  "TopRecordsLimit": 10,

  // Who sees the no-scope announcement:
  //   "All", "AttackerOnly", "VictimOnly", "AttackerAndVictim", "None"
  "NotificationTarget": "All",

  // ── MySQL database ──
  // REQUIRED for persistence + the leaderboard. Leave User/Password empty to
  // disable the database entirely (kills are still announced in chat).
  // Fill in real credentials before starting the server.
  "DatabaseHost": "localhost",
  "DatabasePort": 3306,
  "DatabaseName": "noscope",
  "DatabaseUser": "",
  "DatabasePassword": ""
}
```

## Database setup

NoScope uses MySQL via SqlSugar. Create a database (or reuse an existing one) and a user:

```sql
CREATE DATABASE noscope CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
CREATE USER 'noscope'@'%' IDENTIFIED BY 'your-password';
GRANT ALL PRIVILEGES ON noscope.* TO 'noscope'@'%';
FLUSH PRIVILEGES;
```

Fill in `DatabaseHost` / `DatabasePort` / `DatabaseName` / `DatabaseUser` / `DatabasePassword`
in `noscope.jsonc`, then start the server. The `noscope_kills` table is created automatically
on load. The connection pool is capped at 4 connections.

If credentials are left empty, the plugin still loads and announces kills in chat — it just
won't persist anything or serve the leaderboard.

## Build

Requires the .NET 10 SDK.

```bash
dotnet build -c Release
```

Output lands in `.build/modules/NoScope/`. Copy that `NoScope/` folder (DLL + dependencies)
into your server's `/game/sharp/modules/` directory.

## License

MIT.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Definition;
using Sharp.Shared.Enums;
using Sharp.Shared.Listeners;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using NoScope.Database;
using NoScope.Models;

namespace NoScope;

/// <summary>
/// NoScope — announces no-scope sniper kills in chat with the distance between attacker
/// and victim, and records each one in MySQL. Provides <c>!noscopetop</c> / <c>!nstop</c>
/// (distance leaderboard).
///
/// Detection: the CS2 <c>player_death</c> event carries a <c>noscope</c> bool. We combine
/// that with a sniper-weapon whitelist (awp / ssg08 / scar20 / g3sg1) to confirm a genuine
/// no-scope sniper kill — no netvar reads required.
///
/// Threading: DB work runs off the main thread via Task.Run; any chat/UI follow-up is
/// marshalled back with IModSharp.InvokeFrameAction and the player slot is re-validated
/// inside the callback (a player may disconnect while a query is in flight).
///
/// Config: {SharpPath}/configs/noscope.jsonc (created with empty DB creds on first run).
/// </summary>
public sealed class NoScopePlugin : IModSharpModule, IEventListener
{
    public string DisplayName   => "NoScope";
    public string DisplayAuthor => "YappersHQ";

    private static readonly HashSet<string> SniperWeapons = new(StringComparer.OrdinalIgnoreCase)
    {
        "awp", "ssg08", "scar20", "g3sg1",
        "weapon_awp", "weapon_ssg08", "weapon_scar20", "weapon_g3sg1",
    };

    private const int CommandCooldownSeconds = 5;

    private readonly ILogger<NoScopePlugin> _logger;
    private readonly IModSharp              _modSharp;
    private readonly IClientManager         _clientManager;
    private readonly IEventManager          _eventManager;
    private readonly string                 _sharpPath;

    private NoScopeConfig   _config = new();
    private NoScopeDatabase? _db;

    /// <summary>Per-slot last command time (engine seconds). Indexed by player slot 0..63.</summary>
    private readonly double[] _lastCommandTime = new double[64];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        AllowTrailingCommas         = true,
        WriteIndented               = true,
        Converters                  = { new JsonStringEnumConverter() },
    };

    public NoScopePlugin(
        ISharedSystem   sharedSystem,
        string?         dllPath,
        string?         sharpPath,
        Version?        version,
        IConfiguration? coreConfiguration,
        bool            hotReload
    )
    {
        _logger        = sharedSystem.GetLoggerFactory().CreateLogger<NoScopePlugin>();
        _modSharp      = sharedSystem.GetModSharp();
        _clientManager = sharedSystem.GetClientManager();
        _eventManager  = sharedSystem.GetEventManager();
        _sharpPath     = sharpPath ?? string.Empty;
    }

    #region Lifecycle

    public bool Init()
        => true;

    public void PostInit()
    {
        LoadConfig();

        _eventManager.HookEvent("player_death");
        _eventManager.InstallEventListener(this);

        _clientManager.InstallCommandCallback("noscopetop", OnTopCommand);
        _clientManager.InstallCommandCallback("nstop",      OnTopCommand);
    }

    public void OnAllModulesLoaded()
    {
        if (!_config.HasDatabaseCredentials)
        {
            _logger.LogWarning(
                "[NoScope] No database credentials configured in noscope.jsonc — "
                + "kills will be announced but NOT recorded. Fill in DatabaseUser/DatabasePassword to enable persistence.");
            return;
        }

        try
        {
            _db = new NoScopeDatabase(_config);
            _db.InitTables();
            _logger.LogInformation(
                "[NoScope] Connected to MySQL '{Db}' on {Host}:{Port} — table ready",
                _config.DatabaseName, _config.DatabaseHost, _config.DatabasePort);
        }
        catch (Exception ex)
        {
            _db = null;
            _logger.LogError(ex, "[NoScope] Failed to connect to MySQL — persistence disabled");
        }
    }

    public void OnLibraryConnected(string name)  { }
    public void OnLibraryDisconnect(string name) { }

    public void Shutdown()
    {
        _eventManager.RemoveEventListener(this);
        _clientManager.RemoveCommandCallback("noscopetop", OnTopCommand);
        _clientManager.RemoveCommandCallback("nstop",      OnTopCommand);

        _db?.Dispose();
        _db = null;
    }

    #endregion

    #region IEventListener

    int IEventListener.ListenerVersion  => IEventListener.ApiVersion;
    int IEventListener.ListenerPriority => 0;

    void IEventListener.FireGameEvent(IGameEvent @event)
    {
        if (@event.Name == "player_death")
            OnPlayerDeath(@event);
    }

    #endregion

    #region No-Scope Detection

    private void OnPlayerDeath(IGameEvent e)
    {
        var weapon = e.GetString("weapon");

        if (!SniperWeapons.Contains(weapon))
            return;

        if (!e.GetBool("noscope"))
            return;

        if (e.GetPlayerController("attacker") is not { IsValidEntity: true } attacker)
            return;

        if (e.GetPlayerController("userid") is not { IsValidEntity: true } victim)
            return;

        // Ignore suicide / self-kills
        if (attacker.SteamId.Equals(victim.SteamId))
            return;

        if (e.GetPlayerPawn("attacker") is not { IsValidEntity: true } attackerPawn)
            return;

        if (e.GetPlayerPawn("userid") is not { IsValidEntity: true } victimPawn)
            return;

        var attackerPos = attackerPawn.GetAbsOrigin();
        var victimPos   = victimPawn.GetAbsOrigin();
        var distance    = attackerPos.DistTo(victimPos);

        var headshot       = e.GetBool("headshot");
        var attackerName   = attacker.PlayerName;
        var victimName     = victim.PlayerName;
        var attackerSteam  = attacker.SteamId.AsPrimitive();   // ulong
        var victimSteam    = victim.SteamId.AsPrimitive();

        // Announce synchronously — `attacker`/`victim` are only used on this frame, never captured.
        var headshotText = headshot
            ? $" {ChatColor.Red}HEADSHOT!{ChatColor.White}"
            : string.Empty;

        var message =
            $" {ChatColor.Green}{attackerName}{ChatColor.White} no-scoped "
            + $"{ChatColor.Red}{victimName}{ChatColor.White} from "
            + $"{ChatColor.Blue}{distance:0.0}{ChatColor.White} units away!{headshotText}";

        switch (_config.NotificationTarget)
        {
            case NotificationTarget.All:
                _modSharp.PrintToChatAll(message);
                break;
            case NotificationTarget.AttackerOnly:
                attacker.Print(HudPrintChannel.Chat, message);
                break;
            case NotificationTarget.VictimOnly:
                victim.Print(HudPrintChannel.Chat, message);
                break;
            case NotificationTarget.AttackerAndVictim:
                attacker.Print(HudPrintChannel.Chat, message);
                victim.Print(HudPrintChannel.Chat, message);
                break;
            case NotificationTarget.None:
                break;
        }

        if (_db is null || distance < _config.MinDistanceToRecord)
            return;

        RecordKill(new NoScopeRecord
        {
            AttackerName    = attackerName,
            AttackerSteamId = (long)attackerSteam,
            VictimName      = victimName,
            VictimSteamId   = (long)victimSteam,
            Distance        = (decimal)distance,
            Weapon          = NormalizeWeapon(weapon),
            Headshot        = headshot,
            MapName         = _modSharp.GetMapName() ?? "unknown",
            Timestamp       = DateTime.UtcNow,
        });
    }

    private void RecordKill(NoScopeRecord record)
    {
        var db = _db;
        if (db is null)
            return;

        var attackerSteam = record.AttackerSteamId;

        Task.Run(async () =>
        {
            try
            {
                await db.InsertAsync(record);

                var count = await db.GetPlayerCountAsync(attackerSteam);

                if (count <= 0)
                    return;

                // Marshal chat back to main thread; re-locate the player by SteamID since the
                // slot may have been reused while the DB round-trip was in flight.
                _modSharp.InvokeFrameAction(() =>
                {
                    var client = _clientManager.GetGameClient(new SteamID((ulong)attackerSteam));

                    if (client is not { IsInGame: true })
                        return;

                    client.Print(
                        HudPrintChannel.Chat,
                        $" {ChatColor.Green}You now have {ChatColor.Gold}{count}{ChatColor.Green} recorded no-scopes!");
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[NoScope] Failed to record no-scope kill");
            }
        });
    }

    #endregion

    #region Command — !noscopetop / !nstop

    private ECommandAction OnTopCommand(IGameClient client, StringCommand command)
    {
        if (client is not { IsInGame: true })
            return ECommandAction.Handled;

        var slot = (int)client.Slot.AsPrimitive();

        if (slot is not (>= 0 and < 64))
            return ECommandAction.Handled;

        // Per-player cooldown
        var now      = _modSharp.EngineTime();
        var elapsed  = now - _lastCommandTime[slot];

        if (elapsed < CommandCooldownSeconds)
        {
            var remaining = (int)Math.Ceiling(CommandCooldownSeconds - elapsed);
            client.Print(
                HudPrintChannel.Chat,
                $" {ChatColor.Red}Command on cooldown! Wait {remaining} more seconds.");
            return ECommandAction.Handled;
        }

        _lastCommandTime[slot] = now;

        var db = _db;
        if (db is null)
        {
            client.Print(HudPrintChannel.Chat, $" {ChatColor.Red}No-scope records are not available (no database).");
            return ECommandAction.Handled;
        }

        var steamId = client.SteamId;
        var limit   = _config.TopRecordsLimit;

        Task.Run(async () =>
        {
            NoScopeRecord[] records;

            try
            {
                records = await db.GetTopAsync(limit);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[NoScope] Failed to query top no-scopes");
                _modSharp.InvokeFrameAction(() =>
                {
                    var c = _clientManager.GetGameClient(steamId);

                    if (c is not { IsInGame: true })
                        return;

                    c.Print(HudPrintChannel.Chat, $" {ChatColor.Red}Error retrieving no-scope records.");
                });
                return;
            }

            _modSharp.InvokeFrameAction(() =>
            {
                var c = _clientManager.GetGameClient(steamId);

                if (c is not { IsInGame: true })
                    return;

                if (records.Length == 0)
                {
                    c.Print(HudPrintChannel.Chat, $" {ChatColor.Green}No no-scope records found yet!");
                    return;
                }

                c.Print(HudPrintChannel.Chat, $" {ChatColor.Green}=== Top No-Scope Kills ===");

                for (var i = 0; i < records.Length; i++)
                {
                    var r            = records[i];
                    var headshotText = r.Headshot ? $" {ChatColor.Red}(HS){ChatColor.White}" : string.Empty;
                    var relativeTime = GetRelativeTime(r.Timestamp);

                    c.Print(
                        HudPrintChannel.Chat,
                        $" {ChatColor.Green}{i + 1}.{ChatColor.White} {r.AttackerName} "
                        + $"{ChatColor.White}> {ChatColor.Red}{r.VictimName} {ChatColor.White}- "
                        + $"{ChatColor.Blue}{r.Distance:0.0}{ChatColor.White}u - "
                        + $"{ChatColor.Purple}{FormatWeapon(r.Weapon)}{ChatColor.White}{headshotText} - "
                        + $"{r.MapName} - {ChatColor.Grey}{relativeTime}");
                }

                c.Print(HudPrintChannel.Chat, $" {ChatColor.Green}======================");
            });
        });

        return ECommandAction.Handled;
    }

    #endregion

    #region Formatting helpers

    private static string NormalizeWeapon(string weapon)
        => weapon.StartsWith("weapon_", StringComparison.OrdinalIgnoreCase)
            ? weapon["weapon_".Length..]
            : weapon;

    private static string FormatWeapon(string weapon)
        => weapon.ToLowerInvariant() switch
        {
            "awp"    => "AWP",
            "ssg08"  => "SSG 08",
            "scar20" => "SCAR-20",
            "g3sg1"  => "G3SG1",
            _        => weapon,
        };

    private static string GetRelativeTime(DateTime timestampUtc)
    {
        var span = DateTime.UtcNow - timestampUtc;

        if (span.TotalSeconds < 60)
            return $"{(int)span.TotalSeconds}s ago";
        if (span.TotalMinutes < 60)
            return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24)
            return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 30)
            return $"{(int)span.TotalDays}d ago";
        if (span.TotalDays < 365)
            return $"{(int)(span.TotalDays / 30)}mo ago";

        return $"{(int)(span.TotalDays / 365)}y ago";
    }

    #endregion

    #region Config

    private string GetConfigPath()
        => Path.Combine(Path.GetFullPath(Path.Combine(_sharpPath, "configs")), "noscope.jsonc");

    private void LoadConfig()
    {
        var path = GetConfigPath();

        try
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, DefaultConfig);
                _logger.LogInformation("[NoScope] Wrote default config to {Path}", path);
            }

            var json   = File.ReadAllText(path);
            var parsed = JsonSerializer.Deserialize<NoScopeConfig>(json, JsonOptions);

            if (parsed is not null)
                _config = parsed;

            if (_config.MinDistanceToRecord < 0)
                _config.MinDistanceToRecord = 500.0f;

            if (_config.TopRecordsLimit is <= 0 or > 100)
                _config.TopRecordsLimit = 10;

            _logger.LogInformation(
                "[NoScope] Config loaded — min_distance={Min}, top_limit={Limit}, target={Target}",
                _config.MinDistanceToRecord, _config.TopRecordsLimit, _config.NotificationTarget);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[NoScope] Failed to load config from {Path} — using defaults", path);
            _config = new NoScopeConfig();
        }
    }

    private const string DefaultConfig =
        """
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
        """;

    #endregion
}

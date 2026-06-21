using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Modules.LocalizerManager.Shared;
using Sharp.Shared;
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

    /// <summary>Locale file name (without extension) under <c>{sharp}/locales/</c>.</summary>
    private const string LocaleName = "noscope";

    // Locale keys (declared in .assets/locales/noscope.json).
    private const string KeyAnnounce          = "NoScope_Announce";
    private const string KeyAnnounceHsSuffix  = "NoScope_Announce_HeadshotSuffix";
    private const string KeyRecordedCount     = "NoScope_RecordedCount";
    private const string KeyCooldown          = "NoScope_Cooldown";
    private const string KeyNoDatabase        = "NoScope_NoDatabase";
    private const string KeyQueryError        = "NoScope_QueryError";
    private const string KeyNoRecords         = "NoScope_NoRecords";
    private const string KeyTopHeader         = "NoScope_Top_Header";
    private const string KeyTopRow            = "NoScope_Top_Row";
    private const string KeyTopRowHsSuffix    = "NoScope_Top_RowHeadshotSuffix";
    private const string KeyTopFooter         = "NoScope_Top_Footer";

    private readonly ILogger<NoScopePlugin> _logger;
    private readonly IModSharp              _modSharp;
    private readonly IClientManager         _clientManager;
    private readonly IEventManager          _eventManager;
    private readonly ISharpModuleManager    _sharpModuleManager;
    private readonly string                 _sharpPath;

    private NoScopeConfig    _config = new();
    private NoScopeDatabase? _db;
    private ILocalizerManager? _localizer;

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
        _logger             = sharedSystem.GetLoggerFactory().CreateLogger<NoScopePlugin>();
        _modSharp           = sharedSystem.GetModSharp();
        _clientManager      = sharedSystem.GetClientManager();
        _eventManager       = sharedSystem.GetEventManager();
        _sharpModuleManager = sharedSystem.GetSharpModuleManager();
        _sharpPath          = sharpPath ?? string.Empty;
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
        // Resolve LocalizerManager here — publishers finish PostInit before any OAM fires.
        _localizer = _sharpModuleManager
            .GetOptionalSharpModuleInterface<ILocalizerManager>(ILocalizerManager.Identity)?.Instance;

        if (_localizer is null)
        {
            _logger.LogWarning(
                "[NoScope] LocalizerManager not found — messages will fall back to plain text. Is the LocalizerManager module loaded?");
        }
        else
        {
            _localizer.LoadLocaleFile(LocaleName, suppressDuplicationWarnings: true);
        }

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

    public void OnLibraryConnected(string name)
    {
        if (name != ILocalizerManager.Identity && name != "LocalizerManager")
            return;

        _localizer = _sharpModuleManager
            .GetOptionalSharpModuleInterface<ILocalizerManager>(ILocalizerManager.Identity)?.Instance;

        _localizer?.LoadLocaleFile(LocaleName, suppressDuplicationWarnings: true);
    }

    public void OnLibraryDisconnect(string name)
    {
        if (name == ILocalizerManager.Identity || name == "LocalizerManager")
            _localizer = null;
    }

    /// <summary>
    ///     Localizes a key in <paramref name="client" />'s Steam culture and returns the
    ///     color-processed string. Falls back to the raw key when LocalizerManager is absent.
    /// </summary>
    private string Localize(IGameClient client, string key, params object?[] args)
    {
        if (_localizer is not { } mgr)
            return key;

        return ChatFormat.ProcessColorCodes(mgr.For(client).Localized(key, args).Build());
    }

    /// <summary>
    ///     Localizes a key for <paramref name="client" /> WITHOUT color processing — used
    ///     to build fragments (e.g. the headshot suffix) that are embedded as a format
    ///     argument in a larger key, so the outer Transform processes their tokens once.
    /// </summary>
    private string LocalizeFragment(IGameClient client, string key)
    {
        if (_localizer is not { } mgr)
            return string.Empty;

        return mgr.For(client).Localized(key).Build();
    }

    /// <summary>
    ///     Prints a localized, color-processed line to a single client's chat.
    ///     Falls back to the raw key when LocalizerManager is unavailable.
    /// </summary>
    private void PrintLocalized(IGameClient client, string key, params object?[] args)
    {
        if (_localizer is not { } mgr)
        {
            client.Print(HudPrintChannel.Chat, key);
            return;
        }

        mgr.For(client)
            .Localized(key, args)
            .Prefix(null)
            .Transform(ChatFormat.ProcessColorCodes)
            .Print();
    }

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

        // Announce synchronously — `attacker`/`victim` controllers/clients are only used on
        // this frame, never captured. Rendered per-recipient so each sees their own culture.
        var distanceText = distance.ToString("0.0", CultureInfo.InvariantCulture);

        switch (_config.NotificationTarget)
        {
            case NotificationTarget.All:
                foreach (var c in _clientManager.GetGameClients(true))
                {
                    if (c.IsInGame)
                        AnnounceNoScope(c, attackerName, victimName, distanceText, headshot);
                }

                break;
            case NotificationTarget.AttackerOnly:
                if (attacker.GetGameClient() is { IsInGame: true } atkOnly)
                    AnnounceNoScope(atkOnly, attackerName, victimName, distanceText, headshot);

                break;
            case NotificationTarget.VictimOnly:
                if (victim.GetGameClient() is { IsInGame: true } vicOnly)
                    AnnounceNoScope(vicOnly, attackerName, victimName, distanceText, headshot);

                break;
            case NotificationTarget.AttackerAndVictim:
                if (attacker.GetGameClient() is { IsInGame: true } atk)
                    AnnounceNoScope(atk, attackerName, victimName, distanceText, headshot);

                if (victim.GetGameClient() is { IsInGame: true } vic)
                    AnnounceNoScope(vic, attackerName, victimName, distanceText, headshot);

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

    /// <summary>
    ///     Renders + prints the no-scope announcement to one recipient in their culture.
    ///     The headshot suffix is a localized fragment embedded as a format arg so the
    ///     outer Transform processes its color tokens once.
    /// </summary>
    private void AnnounceNoScope(
        IGameClient recipient,
        string      attackerName,
        string      victimName,
        string      distanceText,
        bool        headshot)
    {
        var hsSuffix = headshot ? LocalizeFragment(recipient, KeyAnnounceHsSuffix) : string.Empty;
        PrintLocalized(recipient, KeyAnnounce, attackerName, victimName, distanceText, hsSuffix);
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

                    PrintLocalized(client, KeyRecordedCount, count);
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
            PrintLocalized(client, KeyCooldown, remaining);
            return ECommandAction.Handled;
        }

        _lastCommandTime[slot] = now;

        var db = _db;
        if (db is null)
        {
            PrintLocalized(client, KeyNoDatabase);
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

                    PrintLocalized(c, KeyQueryError);
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
                    PrintLocalized(c, KeyNoRecords);
                    return;
                }

                PrintLocalized(c, KeyTopHeader);

                for (var i = 0; i < records.Length; i++)
                {
                    var r            = records[i];
                    var headshotText = r.Headshot ? LocalizeFragment(c, KeyTopRowHsSuffix) : string.Empty;
                    var relativeTime = GetRelativeTime(r.Timestamp);
                    var distanceText = r.Distance.ToString("0.0", CultureInfo.InvariantCulture);

                    PrintLocalized(
                        c,
                        KeyTopRow,
                        i + 1,
                        r.AttackerName,
                        r.VictimName,
                        distanceText,
                        FormatWeapon(r.Weapon),
                        headshotText,
                        r.MapName,
                        relativeTime);
                }

                PrintLocalized(c, KeyTopFooter);
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

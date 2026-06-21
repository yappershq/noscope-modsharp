using System.Text.Json.Serialization;

namespace NoScope.Models;

/// <summary>Who receives the in-chat no-scope announcement.</summary>
public enum NotificationTarget
{
    All               = 0,
    AttackerOnly      = 1,
    VictimOnly        = 2,
    AttackerAndVictim = 3,
    None              = 4,
}

/// <summary>
/// NoScope plugin config — deserialized from
/// <c>{SharpPath}/configs/noscope.jsonc</c>.
/// </summary>
public sealed class NoScopeConfig
{
    // ── Behaviour ──

    /// <summary>Minimum distance (game units) for a no-scope to be written to the DB.</summary>
    [JsonPropertyName("MinDistanceToRecord")]
    public float MinDistanceToRecord { get; set; } = 500.0f;

    /// <summary>How many rows the <c>!noscopetop</c> leaderboard shows.</summary>
    [JsonPropertyName("TopRecordsLimit")]
    public int TopRecordsLimit { get; set; } = 10;

    /// <summary>Who sees the announcement on a no-scope kill.</summary>
    [JsonPropertyName("NotificationTarget")]
    public NotificationTarget NotificationTarget { get; set; } = NotificationTarget.All;

    // ── Database (MySQL) ──
    // Leave User/Password empty in the shipped default. The operator must fill in
    // real credentials before starting the server — empty creds = no DB persistence.

    [JsonPropertyName("DatabaseHost")]
    public string DatabaseHost { get; set; } = "localhost";

    [JsonPropertyName("DatabasePort")]
    public int DatabasePort { get; set; } = 3306;

    [JsonPropertyName("DatabaseName")]
    public string DatabaseName { get; set; } = "noscope";

    [JsonPropertyName("DatabaseUser")]
    public string DatabaseUser { get; set; } = "";

    [JsonPropertyName("DatabasePassword")]
    public string DatabasePassword { get; set; } = "";

    /// <summary>True once the operator has filled in usable MySQL credentials.</summary>
    [JsonIgnore]
    public bool HasDatabaseCredentials
        => !string.IsNullOrWhiteSpace(DatabaseUser);
}

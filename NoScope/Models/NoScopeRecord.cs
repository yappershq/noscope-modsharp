using System;
using SqlSugar;

namespace NoScope.Models;

/// <summary>
/// Database entity for a recorded no-scope kill.
/// SteamIDs are stored as <c>long</c> (not <c>ulong</c>) — SqlSugar/MySQL has no native
/// unsigned-64 BSON-style mapping, so we keep them as signed and cast at the CLR boundary.
/// </summary>
[SugarTable("noscope_kills")]
public sealed class NoScopeRecord
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }

    [SugarColumn(Length = 64, IsNullable = false)]
    public string AttackerName { get; set; } = string.Empty;

    /// <summary>Attacker SteamID64 stored as signed long (cast from ulong at the boundary).</summary>
    [SugarColumn(IsNullable = false)]
    public long AttackerSteamId { get; set; }

    [SugarColumn(Length = 64, IsNullable = false)]
    public string VictimName { get; set; } = string.Empty;

    /// <summary>Victim SteamID64 stored as signed long.</summary>
    [SugarColumn(IsNullable = false)]
    public long VictimSteamId { get; set; }

    /// <summary>Distance between attacker and victim in game units.</summary>
    [SugarColumn(DecimalDigits = 2, IsNullable = false)]
    public decimal Distance { get; set; }

    [SugarColumn(Length = 32, IsNullable = false)]
    public string Weapon { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false)]
    public bool Headshot { get; set; }

    [SugarColumn(Length = 128, IsNullable = false)]
    public string MapName { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false)]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

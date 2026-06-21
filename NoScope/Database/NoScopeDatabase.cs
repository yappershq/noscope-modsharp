using System;
using System.Threading.Tasks;
using SqlSugar;
using NoScope.Models;

namespace NoScope.Database;

/// <summary>
/// SqlSugar-backed MySQL store for no-scope kill records.
///
/// Pool is capped at 4 connections (Maximum Pool Size=4) — this server shares a MySQL
/// instance that hits "Too many connections" if every plugin opens an unbounded pool.
///
/// All public methods are async and run off the main thread. Callers must marshal any
/// resulting entity/UI work back to the main thread (IModSharp.InvokeFrameAction) — this
/// class never touches game entities.
/// </summary>
public sealed class NoScopeDatabase : IDisposable
{
    private readonly SqlSugarScope _db;

    public NoScopeDatabase(NoScopeConfig config)
    {
        var connectionString =
            $"Server={config.DatabaseHost};Port={config.DatabasePort};Database={config.DatabaseName};"
            + $"User={config.DatabaseUser};Password={config.DatabasePassword};"
            + "Maximum Pool Size=4;Minimum Pool Size=0;";

        _db = new SqlSugarScope(new ConnectionConfig
        {
            DbType                = DbType.MySql,
            ConnectionString      = connectionString,
            IsAutoCloseConnection = true,
            InitKeyType           = InitKeyType.Attribute,
            MoreSettings          = new ConnMoreSettings { DisableNvarchar = true },
            LanguageType          = LanguageType.English,
        });
    }

    /// <summary>Create the kills table if it does not exist. Throws on connection failure.</summary>
    public void InitTables()
        => _db.CodeFirst.InitTables(typeof(NoScopeRecord));

    /// <summary>Insert a no-scope record. Fire-and-forget safe; logs are the caller's job.</summary>
    public async Task<int> InsertAsync(NoScopeRecord record)
        => await _db.Insertable(record).ExecuteReturnIdentityAsync();

    /// <summary>Top N records ordered by descending distance.</summary>
    public async Task<NoScopeRecord[]> GetTopAsync(int limit)
        => (await _db.Queryable<NoScopeRecord>()
                     .OrderBy(r => r.Distance, OrderByType.Desc)
                     .Take(limit)
                     .ToListAsync())
            .ToArray();

    /// <summary>Count of recorded no-scopes for a given attacker SteamID.</summary>
    public async Task<int> GetPlayerCountAsync(long attackerSteamId)
        => await _db.Queryable<NoScopeRecord>()
                    .Where(r => r.AttackerSteamId == attackerSteamId)
                    .CountAsync();

    public void Dispose()
        => _db.Dispose();
}

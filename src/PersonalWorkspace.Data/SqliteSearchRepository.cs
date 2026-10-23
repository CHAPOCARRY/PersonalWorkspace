using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.Data;

public sealed class SqliteSearchRepository : ISearchRepository
{
    public const int ProjectionVersion = 1;
    private readonly HashSet<string> verified = new(StringComparer.OrdinalIgnoreCase);
    private static SqliteCommand Command(SqliteConnection c, SqliteTransaction tx, string sql, params (string, object?)[] args)
    {
        var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue("$" + name, value ?? DBNull.Value);
        return cmd;
    }
    private static void Execute(SqliteConnection c, SqliteTransaction tx, string sql, params (string, object?)[] args)
    { using var cmd = Command(c, tx, sql, args); cmd.ExecuteNonQuery(); }
    private static long Scalar(SqliteConnection c, SqliteTransaction tx, string sql)
    { using var cmd = Command(c, tx, sql); return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture); }
    public static string TitleKey(string value) => string.Concat(value.Normalize(NormalizationForm.FormD)
        .Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)).Normalize(NormalizationForm.FormC).ToUpperInvariant().Trim();
    private static string Match(string query)
    {
        if (query.Length > 256) throw new SearchException("Use up to 256 characters in a search.");
        var words = Regex.Matches(query, @"[\p{L}\p{N}\p{M}]+", RegexOptions.CultureInvariant).Select(m => m.Value).ToArray();
        if (words.Length > 16) throw new SearchException("Use up to 16 search terms.");
        return string.Join(" AND ", words.Select((w, i) => "\"" + w.Replace("\"", "\"\"") + "\"" + (i == words.Length - 1 ? "*" : "")));
    }
    private static void BeginRebuild(SqliteConnection c, SqliteTransaction tx)
    {
        // Discard index postings first, so recovery does not depend on previously correct postings.
        Execute(c, tx, "INSERT INTO SearchDocumentsFts(SearchDocumentsFts) VALUES('rebuild'); DELETE FROM SearchDocuments; DELETE FROM SearchDirtyDocuments;");
        Execute(c, tx, "INSERT INTO SearchDirtyDocuments SELECT Kind,SourceId FROM SearchSources; UPDATE SearchIndexState SET Version=$version,Rebuilding=1,Generation=Generation+1 WHERE Id=1", ("version", ProjectionVersion));
    }
    public async Task RebuildAsync(WorkspaceContext w, CancellationToken token)
    {
        using var c = await SqliteConnectionFactory.OpenDatabaseAsync(w.DatabasePath, false, token);
        using var tx = c.BeginTransaction(deferred: false); token.ThrowIfCancellationRequested(); BeginRebuild(c, tx); tx.Commit();
    }
    public async Task<long> GenerationAsync(WorkspaceContext w, CancellationToken token)
    {
        using var c = await SqliteConnectionFactory.OpenDatabaseAsync(w.DatabasePath, false, token);
        using var tx = c.BeginTransaction(); return Scalar(c, tx, "SELECT Generation FROM SearchIndexState WHERE Id=1");
    }
    public async Task<SearchResponse> SearchAsync(WorkspaceContext w, string query, bool archived, Func<SearchProjection, string> extract, CancellationToken token)
    {
        var match = Match(query);
        using var c = await SqliteConnectionFactory.OpenDatabaseAsync(w.DatabasePath, false, token);
        var checkedIndex = verified.Contains(w.DatabasePath);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            using var tx = c.BeginTransaction(deferred: false);
            if (Scalar(c, tx, "SELECT Version FROM SearchIndexState WHERE Id=1") != ProjectionVersion) BeginRebuild(c, tx);
            if (!checkedIndex)
            {
                try { Execute(c, tx, "INSERT INTO SearchDocumentsFts(SearchDocumentsFts,rank) VALUES('integrity-check',1)"); }
                catch (SqliteException e) when (e.SqliteErrorCode == 11) { BeginRebuild(c, tx); }
                checkedIndex = true;
            }
            var keys = new List<(int Kind, string Id)>();
            using (var cmd = Command(c, tx, "SELECT Kind,SourceId FROM SearchDirtyDocuments ORDER BY Kind,SourceId LIMIT 64"))
            using (var r = cmd.ExecuteReader()) while (r.Read()) keys.Add((r.GetInt32(0), r.GetString(1)));
            if (keys.Count > 0)
            {
                // One bounded, set-based canonical read, never a service call per document.
                var projections = new List<SearchProjection>();
                using (var cmd = Command(c, tx, """
                    WITH batch AS MATERIALIZED (SELECT Kind,SourceId FROM SearchDirtyDocuments ORDER BY Kind,SourceId LIMIT 64)
                    SELECT s.* FROM SearchSources s WHERE s.SourceId IN (SELECT SourceId FROM batch)
                        AND (s.Kind,s.SourceId) IN (SELECT Kind,SourceId FROM batch) ORDER BY s.Kind,s.SourceId
                    """))
                using (var r = cmd.ExecuteReader()) while (r.Read())
                    projections.Add(new((SearchKind)r.GetInt32(0), Guid.Parse(r.GetString(1)), r.IsDBNull(2) ? null : Guid.Parse(r.GetString(2)), r.GetString(3), r.GetString(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.GetInt32(8)));
                foreach (var key in keys) Execute(c, tx, "DELETE FROM SearchDocuments WHERE Kind=$kind AND SourceId=$id", ("kind", key.Kind), ("id", key.Id));
                foreach (var p in projections)
                {
                    token.ThrowIfCancellationRequested(); var body = extract(p);
                    Execute(c, tx, "INSERT INTO SearchDocuments(Kind,SourceId,OwnerId,Title,Metadata,Body,TitleKey,EntryDate) VALUES($kind,$id,$owner,$title,$meta,$body,$key,$date)",
                        ("kind", (int)p.Kind), ("id", p.SourceId.ToString("D")), ("owner", p.OwnerId?.ToString("D")), ("title", p.Title), ("meta", p.Metadata), ("body", body), ("key", TitleKey(p.Title)), ("date", p.EntryDate));
                }
                foreach (var key in keys) Execute(c, tx, "DELETE FROM SearchDirtyDocuments WHERE Kind=$kind AND SourceId=$id", ("kind", key.Kind), ("id", key.Id));
                token.ThrowIfCancellationRequested(); tx.Commit(); continue;
            }
            // This immediate transaction covers BOTH readiness and the result read. No writer can
            // commit between them, including writes from another connection/process.
            Execute(c, tx, "UPDATE SearchIndexState SET Rebuilding=0 WHERE Id=1");
            var generation = Scalar(c, tx, "SELECT Generation FROM SearchIndexState WHERE Id=1");
            var rows = new List<SearchResult>();
            if (match.Length > 0)
            {
                using var cmd = Command(c, tx, """
                    SELECT d.Kind,d.SourceId,d.OwnerId,d.EntryDate,
                        CASE d.Kind WHEN 2 THEN 'List item' WHEN 3 THEN 'Journal entry' WHEN 4 THEN 'Page content' WHEN 5 THEN 'Template' ELSE
                            CASE w.ItemType WHEN 1 THEN 'Task' WHEN 2 THEN 'Event' WHEN 3 THEN 'Tracker' WHEN 4 THEN 'Journal' WHEN 5 THEN 'Page' WHEN 6 THEN 'List' END END,
                        CASE WHEN d.Title<>'' THEN d.Title ELSE w.Title END,coalesce(w.Title,t.Name,''),
                        snippet(SearchDocumentsFts,-1,'','','…',24), w.ArchivedAtUtc IS NOT NULL
                    FROM SearchDocumentsFts JOIN SearchDocuments d ON d.Id=SearchDocumentsFts.rowid
                    LEFT JOIN WorkspaceItems w ON w.Id=d.OwnerId LEFT JOIN Templates t ON d.Kind=5 AND t.Id=d.SourceId
                    WHERE SearchDocumentsFts MATCH $match AND ((d.Kind=5 AND t.Id IS NOT NULL) OR
                        (w.Id IS NOT NULL AND w.DeletedAtUtc IS NULL AND ($archived=1 OR w.ArchivedAtUtc IS NULL)))
                    ORDER BY CASE WHEN d.TitleKey=$key THEN 0 WHEN d.Id IN(SELECT rowid FROM SearchDocumentsFts WHERE SearchDocumentsFts MATCH $titles) THEN 1
                        WHEN d.Id IN(SELECT rowid FROM SearchDocumentsFts WHERE SearchDocumentsFts MATCH $anyTitle) THEN 2 ELSE 3 END,
                        bm25(SearchDocumentsFts,8.0,2.0,1.0),d.TitleKey,d.Kind,d.SourceId LIMIT 51
                    """, ("match", match), ("titles", "Title : (" + match + ")"), ("anyTitle", "Title : (" + match.Replace(" AND ", " OR ") + ")"), ("key", TitleKey(query)), ("archived", archived ? 1 : 0));
                using var r = cmd.ExecuteReader();
                while (r.Read()) rows.Add(new(new(w.ProfileId, (SearchKind)r.GetInt32(0), Guid.Parse(r.GetString(1)), r.IsDBNull(2) ? null : Guid.Parse(r.GetString(2)), r.IsDBNull(3) ? null : DateOnly.ParseExact(r.GetString(3), "yyyy-MM-dd", CultureInfo.InvariantCulture)), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), r.GetBoolean(8)));
            }
            token.ThrowIfCancellationRequested(); tx.Commit(); verified.Add(w.DatabasePath); return new(rows.Take(50).ToArray(), rows.Count > 50, generation);
        }
    }
    public async Task<NavigationRoute> ResolveAsync(WorkspaceContext w, SearchTarget target, bool archived, CancellationToken token)
    {
        if (target.ProfileId != w.ProfileId) throw new WorkspaceChangedException();
        using var c = await SqliteConnectionFactory.OpenDatabaseAsync(w.DatabasePath, false, token);
        using var tx = c.BeginTransaction();
        using var cmd = Command(c, tx, """
            SELECT s.OwnerId,s.EntryDate,w.ItemType,w.ArchivedAtUtc IS NOT NULL FROM SearchSources s
            LEFT JOIN WorkspaceItems w ON w.Id=s.OwnerId WHERE s.Kind=$kind AND s.SourceId=$id AND
                (s.Kind=5 OR (w.Id IS NOT NULL AND w.DeletedAtUtc IS NULL AND ($archived=1 OR w.ArchivedAtUtc IS NULL)))
            """, ("kind", (int)target.Kind), ("id", target.SourceId.ToString("D")), ("archived", archived ? 1 : 0));
        using var r = cmd.ExecuteReader();
        if (!r.Read() || (r.IsDBNull(0) ? null : (Guid?)Guid.Parse(r.GetString(0))) != target.OwnerId ||
            (r.IsDBNull(1) ? null : (DateOnly?)DateOnly.ParseExact(r.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture)) != target.EntryDate)
            throw new SearchException("This result is no longer available. Search again.");
        var destination = target.Kind == SearchKind.Template ? "Templates" : ((WorkspaceItemType)r.GetInt32(2)) switch
        { WorkspaceItemType.Task => "Task", WorkspaceItemType.Event => "Event", WorkspaceItemType.Tracker => "Tracker", WorkspaceItemType.Journal => "Journal", WorkspaceItemType.List => "Lists", WorkspaceItemType.Page => "Pages", _ => throw new SearchException("Unsupported result.") };
        var id = target.OwnerId ?? target.SourceId;
        return new(destination, destination == "Journal" ? $"{id:D}|{(target.EntryDate ?? DateOnly.FromDateTime(DateTime.Now)):yyyy-MM-dd}" : id.ToString("D"),
            new(w.ProfileId, target.Kind is SearchKind.ListItem or SearchKind.PageContent ? target.SourceId : null, r.GetBoolean(3)));
    }
}

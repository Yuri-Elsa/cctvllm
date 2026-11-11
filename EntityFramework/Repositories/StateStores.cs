using System.Data;
using System.Data.Common;
using System.Text.Json;

// Where QueueStore keeps its state between runs. QueueStore still holds everything in memory and decides what changed;
// the store only has to load it at start and write it after each change.
public interface IStateStore
{
    string Description { get; }
    /// <summary>Null when nothing has been saved yet.</summary>
    StoredState? Load();
    void Save(StoredState state);
    /// <summary>Throws when the store cannot be reached or written.</summary>
    void Ping();
}

public static class StateStores
{
    /// <summary>CCTV_SQLSERVER (a connection string) selects SQL Server; otherwise state.json in the data folder.</summary>
    public static IStateStore Create(string root) =>
        Environment.GetEnvironmentVariable("CCTV_SQLSERVER") is { Length: > 0 } connection
            ? new SqlServerStateStore(connection, root)
            : new JsonFileStateStore(root);
}

public sealed class JsonFileStateStore(string root) : IStateStore
{
    private readonly string statePath = Path.Combine(root, "state.json");
    private DateTime lastSaveWarning = DateTime.MinValue;
    public string Description => statePath;

    public StoredState? Load()
    {
        if (!File.Exists(statePath)) return null;
        try
        {
            return JsonSerializer.Deserialize<StoredState>(File.ReadAllText(statePath), QueueStore.Json)
                ?? throw new JsonException("state.json is empty.");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A damaged state file must not stop the app (and with it the cameras) from starting.
            // Keep the damaged copy for inspection and start with an empty queue.
            var backup = Path.Combine(root, $"state.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            try { File.Copy(statePath, backup, true); } catch (IOException) { }
            Console.Error.WriteLine($"state.json could not be read ({ex.Message}). A copy was kept as {backup}; starting with an empty queue.");
            return null;
        }
    }

    public void Save(StoredState state)
    {
        var temp = Path.Combine(root, "state.tmp");
        var json = JsonSerializer.Serialize(state, QueueStore.Json);
        // On Windows another process (antivirus, a sync client, the search indexer) can hold state.json open for a
        // moment, and replacing it then fails with "Access to the path is denied". That is nearly always gone a
        // moment later, so try a few times before giving up.
        for (var attempt = 1; ; attempt++)
        {
            try { File.WriteAllText(temp, json); File.Move(temp, statePath, true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt < 6) { Thread.Sleep(25 * (1 << (attempt - 1))); continue; }     // 25, 50, 100, 200, 400 ms
                // Give up for now. The state is still in memory and every later change writes all of it again, so a
                // video must not fail just because this one write did.
                if (DateTime.UtcNow - lastSaveWarning > TimeSpan.FromSeconds(30))
                {
                    lastSaveWarning = DateTime.UtcNow;
                    Console.Error.WriteLine($"state.json could not be saved ({ex.Message}). It will be written again on the next change.");
                }
                return;
            }
        }
    }

    public void Ping() { var probe = Path.Combine(root, ".write-test"); File.WriteAllText(probe, "ok"); File.Delete(probe); }
}

/// <summary>Works out what to write: only the jobs whose JSON changed since the last successful save, and the ids that are gone.</summary>
public sealed class StateDiff
{
    private readonly Dictionary<string, string> saved = new();
    private string? savedSettings, savedFolder;
    public sealed record Change(Dictionary<string, string> Upserts, List<string> Deletes, string? Settings, string? Folder, bool FolderChanged);

    public Change Compare(IReadOnlyList<(string Id, string Json)> jobs, string settingsJson, string? folder)
    {
        var upserts = jobs.Where(j => !saved.TryGetValue(j.Id, out var old) || old != j.Json).ToDictionary(j => j.Id, j => j.Json);
        var ids = jobs.Select(j => j.Id).ToHashSet();
        var deletes = saved.Keys.Where(id => !ids.Contains(id)).ToList();
        return new Change(upserts, deletes, settingsJson == savedSettings ? null : settingsJson, folder, folder != savedFolder);
    }

    /// <summary>Call only after the change was written; a failed write leaves the old picture so the next save repeats it.</summary>
    public void Commit(Change change, string settingsJson)
    {
        foreach (var (id, json) in change.Upserts) saved[id] = json;
        foreach (var id in change.Deletes) saved.Remove(id);
        savedSettings = settingsJson; savedFolder = change.Folder;
    }

    public void Reset(IEnumerable<(string Id, string Json)> jobs, string settingsJson, string? folder)
    {
        saved.Clear();
        foreach (var (id, json) in jobs) saved[id] = json;
        savedSettings = settingsJson; savedFolder = folder;
    }
}

// SQL Server through the ADO.NET provider model, so nothing here refers to Microsoft.Data.SqlClient at compile time:
// the provider is looked up by name (the package is referenced in CctvDemo.csproj). Each job is one row holding its JSON,
// which is what state.json held, so adding a field to a job needs no schema change.
//
//   CCTV_SQLSERVER="Server=db01;Database=Cctv;User Id=cctv;Password=...;Encrypt=True;TrustServerCertificate=False"
//
// The tables (dbo.CctvJobs, dbo.CctvState) are created on first start. If they are empty and the data folder has a
// state.json, that file is imported once.
public sealed class SqlServerStateStore : IStateStore
{
    private static readonly JsonSerializerOptions Compact = new(QueueStore.Json) { WriteIndented = false };
    private readonly DbProviderFactory factory;
    private readonly string connection, root;
    private readonly StateDiff diff = new();
    private DateTime lastSaveWarning = DateTime.MinValue;
    public string Description { get; }

    public SqlServerStateStore(string connection, string root)
    {
        this.connection = connection; this.root = root;
        var type = Type.GetType("Microsoft.Data.SqlClient.SqlClientFactory, Microsoft.Data.SqlClient")
            ?? throw new InvalidOperationException("CCTV_SQLSERVER is set but the Microsoft.Data.SqlClient package is not part of this build. Add it to CctvDemo.csproj and rebuild.");
        factory = (DbProviderFactory)type.GetField("Instance")!.GetValue(null)!;
        var builder = factory.CreateConnectionStringBuilder() ?? throw new InvalidOperationException("The SQL Server provider has no connection string builder.");
        try { builder.ConnectionString = connection; }
        catch (ArgumentException ex) { throw new InvalidOperationException("CCTV_SQLSERVER is not a valid connection string: " + ex.Message); }
        // Never print the password.
        object? server = null, database = null;
        foreach (var key in new[] { "Data Source", "Server" }) if (server is null) builder.TryGetValue(key, out server);
        foreach (var key in new[] { "Initial Catalog", "Database" }) if (database is null) builder.TryGetValue(key, out database);
        Description = $"SQL Server {server}/{database}";
    }

    private DbConnection Open()
    {
        var conn = factory.CreateConnection()!;
        conn.ConnectionString = connection;
        conn.Open();
        return conn;
    }

    private static DbCommand Command(DbConnection conn, string sql, DbTransaction? tx = null, params (string Name, object? Value)[] args)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql; cmd.Transaction = tx; cmd.CommandTimeout = 30;
        foreach (var (name, value) in args)
        {
            var p = cmd.CreateParameter(); p.ParameterName = name; p.Value = value ?? DBNull.Value; cmd.Parameters.Add(p);
        }
        return cmd;
    }

    private void EnsureSchema(DbConnection conn)
    {
        using var cmd = Command(conn, """
            IF OBJECT_ID(N'dbo.CctvJobs', N'U') IS NULL
                CREATE TABLE dbo.CctvJobs (
                    Seq       bigint IDENTITY(1,1) NOT NULL,
                    Id        nvarchar(64)  NOT NULL CONSTRAINT PK_CctvJobs PRIMARY KEY,
                    Status    nvarchar(32)  NOT NULL,
                    Json      nvarchar(max) NOT NULL,
                    UpdatedAt datetime2(3)  NOT NULL);
            IF OBJECT_ID(N'dbo.CctvState', N'U') IS NULL
                CREATE TABLE dbo.CctvState ([Key] nvarchar(64) NOT NULL CONSTRAINT PK_CctvState PRIMARY KEY, Value nvarchar(max) NULL);
            """);
        cmd.ExecuteNonQuery();
    }

    public StoredState? Load()
    {
        using var conn = Open();     // an unreachable database stops the start: an empty queue would hide the problem and later overwrite the real one
        EnsureSchema(conn);
        var jobs = new List<VideoJob>(); var ids = new List<(string, string)>();
        using (var cmd = Command(conn, "SELECT Id, Json FROM dbo.CctvJobs ORDER BY Seq"))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
            {
                var id = reader.GetString(0); var json = reader.GetString(1);
                try { jobs.Add(JsonSerializer.Deserialize<VideoJob>(json, QueueStore.Json) ?? throw new JsonException("empty")); ids.Add((id, json)); }
                catch (JsonException ex) { Console.Error.WriteLine($"Job {id} in the database could not be read ({ex.Message}); skipped (the row is left as it is)."); }
            }
        string? settingsJson = null, folder = null;
        using (var cmd = Command(conn, "SELECT [Key], Value FROM dbo.CctvState"))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
            {
                var value = reader.IsDBNull(1) ? null : reader.GetString(1);
                if (reader.GetString(0) == "settings") settingsJson = value; else if (reader.GetString(0) == "linkedFolder") folder = value;
            }
        if (jobs.Count == 0 && settingsJson is null && File.Exists(Path.Combine(root, "state.json")))
        {
            var imported = new JsonFileStateStore(root).Load();
            if (imported is not null)
            {
                Console.Error.WriteLine($"Importing state.json ({imported.Jobs.Count} videos) into {Description}.");
                Save(imported);      // a failure here is an error: the file is the only copy
                if (lastSaveFailed) throw new InvalidOperationException("The import of state.json into SQL Server failed; see the message above.");
                return imported;
            }
        }
        var settings = settingsJson is null ? new ModelSettings() : JsonSerializer.Deserialize<ModelSettings>(settingsJson, QueueStore.Json) ?? new ModelSettings();
        var state = new StoredState(settings, jobs) { LinkedFolder = folder };
        diff.Reset(ids, Serialize(settings), folder);
        return jobs.Count == 0 && settingsJson is null ? null : state;
    }

    private bool lastSaveFailed;
    private static string Serialize(ModelSettings settings) => JsonSerializer.Serialize(settings, Compact);

    public void Save(StoredState state)
    {
        var jobs = state.Jobs.Select(j => (j.Id, Json: JsonSerializer.Serialize(j, Compact))).ToList();
        var settings = Serialize(state.Settings);
        var change = diff.Compare(jobs, settings, state.LinkedFolder);
        if (change.Upserts.Count == 0 && change.Deletes.Count == 0 && change.Settings is null && !change.FolderChanged) { lastSaveFailed = false; return; }
        try
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            foreach (var (id, json) in change.Upserts)
            {
                var status = state.Jobs.First(j => j.Id == id).Status;
                using var cmd = Command(conn, """
                    UPDATE dbo.CctvJobs SET Status = @status, Json = @json, UpdatedAt = SYSUTCDATETIME() WHERE Id = @id;
                    IF @@ROWCOUNT = 0 INSERT INTO dbo.CctvJobs (Id, Status, Json, UpdatedAt) VALUES (@id, @status, @json, SYSUTCDATETIME());
                    """, tx, ("@id", id), ("@status", status), ("@json", json));
                cmd.ExecuteNonQuery();
            }
            foreach (var id in change.Deletes)
            {
                using var cmd = Command(conn, "DELETE FROM dbo.CctvJobs WHERE Id = @id", tx, ("@id", id));
                cmd.ExecuteNonQuery();
            }
            if (change.Settings is not null) Put(conn, tx, "settings", change.Settings);
            if (change.FolderChanged) Put(conn, tx, "linkedFolder", change.Folder);
            tx.Commit();
            diff.Commit(change, settings);
            lastSaveFailed = false;
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or TimeoutException)
        {
            // The state stays in memory and the next change repeats this write (nothing was marked as saved).
            lastSaveFailed = true;
            if (DateTime.UtcNow - lastSaveWarning > TimeSpan.FromSeconds(30))
            {
                lastSaveWarning = DateTime.UtcNow;
                Console.Error.WriteLine($"The state could not be saved to {Description} ({ex.Message}). It will be written again on the next change.");
            }
        }
    }

    private static void Put(DbConnection conn, DbTransaction tx, string key, string? value)
    {
        using var cmd = Command(conn, """
            UPDATE dbo.CctvState SET Value = @value WHERE [Key] = @key;
            IF @@ROWCOUNT = 0 INSERT INTO dbo.CctvState ([Key], Value) VALUES (@key, @value);
            """, tx, ("@key", key), ("@value", value));
        cmd.ExecuteNonQuery();
    }

    public void Ping()
    {
        using var conn = Open();
        using var cmd = Command(conn, "SELECT COUNT_BIG(*) FROM dbo.CctvJobs");
        cmd.ExecuteScalar();
    }
}

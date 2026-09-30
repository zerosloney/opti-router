using System.Globalization;
using Microsoft.Data.Sqlite;

namespace OptiRouter.Routing;

/// <summary>
/// SQLite 持久化的学习状态存储，同时支持 Thompson 采样与 Contextual Bandit 状态。
/// 与成本账本共享同一 DB 文件，WAL 模式，线程安全。
/// </summary>
public sealed class SqliteLearningStateStore : IThompsonStateStore, IBanditStateStore, IDisposable
{
    private readonly object _lock = new();
    private readonly SqliteConnection _connection;
    private bool _disposed;

    /// <summary>
    /// 用指定 DB 文件路径构造。
    /// </summary>
    /// <param name="path">SQLite 文件路径（与成本账本相同）。</param>
    public SqliteLearningStateStore(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        _connection = new SqliteConnection($"Data Source={path};Default Timeout=15");
        _connection.Open();

        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA busy_timeout=5000;");

        Execute("""
            CREATE TABLE IF NOT EXISTS thompson_states (
                model_name TEXT PRIMARY KEY,
                alpha REAL NOT NULL,
                beta REAL NOT NULL
            );
            """);

        Execute("""
            CREATE TABLE IF NOT EXISTS bandit_arms (
                model_name TEXT PRIMARY KEY,
                dim INTEGER NOT NULL,
                a_json TEXT NOT NULL,
                b_json TEXT NOT NULL,
                n INTEGER NOT NULL DEFAULT 0
            );
            """);
    }

    /// <inheritdoc />
    public void Save(string modelName, double alpha, double beta)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(modelName);

        lock (_lock)
        {
            using var tx = _connection.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO thompson_states (model_name, alpha, beta)
                VALUES (@model, @alpha, @beta)
                ON CONFLICT(model_name) DO UPDATE SET
                    alpha = @alpha,
                    beta = @beta;
                """;
            cmd.Parameters.AddWithValue("@model", modelName);
            cmd.Parameters.AddWithValue("@alpha", alpha);
            cmd.Parameters.AddWithValue("@beta", beta);
            cmd.ExecuteNonQuery();
            tx.Commit();
        }
    }

    /// <inheritdoc />
    Dictionary<string, (double Alpha, double Beta)> IThompsonStateStore.LoadAll()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var result = new Dictionary<string, (double, double)>(StringComparer.OrdinalIgnoreCase);

        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT model_name, alpha, beta FROM thompson_states;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string model = reader.GetString(0);
                double alpha = reader.GetDouble(1);
                double beta = reader.GetDouble(2);
                result[model] = (alpha, beta);
            }
        }

        return result;
    }

    /// <inheritdoc />
    public void Save(string modelName, int dim, double[,] a, double[] b, int n)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(modelName);
        if (a is null) throw new ArgumentNullException(nameof(a));
        if (b is null) throw new ArgumentNullException(nameof(b));

        // Serialize A as flat JSON array (row-major), dim 已知可反序列化。
        var flatA = new double[dim * dim];
        for (int i = 0; i < dim; i++)
            for (int j = 0; j < dim; j++)
                flatA[i * dim + j] = a[i, j];

        string aJson = System.Text.Json.JsonSerializer.Serialize(flatA);
        string bJson = System.Text.Json.JsonSerializer.Serialize(b);

        lock (_lock)
        {
            using var tx = _connection.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO bandit_arms (model_name, dim, a_json, b_json, n)
                VALUES (@model, @dim, @a, @b, @n)
                ON CONFLICT(model_name) DO UPDATE SET
                    dim = @dim,
                    a_json = @a,
                    b_json = @b,
                    n = @n;
                """;
            cmd.Parameters.AddWithValue("@model", modelName);
            cmd.Parameters.AddWithValue("@dim", dim);
            cmd.Parameters.AddWithValue("@a", aJson);
            cmd.Parameters.AddWithValue("@b", bJson);
            cmd.Parameters.AddWithValue("@n", n);
            cmd.ExecuteNonQuery();
            tx.Commit();
        }
    }

    /// <inheritdoc />
    Dictionary<string, (int Dim, double[,] A, double[] B, int N)> IBanditStateStore.LoadAll()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var result = new Dictionary<string, (int, double[,], double[], int)>(StringComparer.OrdinalIgnoreCase);

        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT model_name, dim, a_json, b_json, n FROM bandit_arms;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string model = reader.GetString(0);
                int dim = reader.GetInt32(1);
                string aJson = reader.GetString(2);
                string bJson = reader.GetString(3);
                int n = reader.GetInt32(4);

                try
                {
                    double[] flatA = System.Text.Json.JsonSerializer.Deserialize<double[]>(aJson)
                        ?? throw new InvalidDataException("bandit_arms.a_json deserialized to null.");
                    if (flatA.Length != dim * dim)
                        continue; // 维度不匹配，跳过（dim 变更导致）

                    double[] b = System.Text.Json.JsonSerializer.Deserialize<double[]>(bJson)
                        ?? throw new InvalidDataException("bandit_arms.b_json deserialized to null.");
                    if (b.Length != dim)
                        continue;

                    var a = new double[dim, dim];
                    for (int i = 0; i < dim; i++)
                        for (int j = 0; j < dim; j++)
                            a[i, j] = flatA[i * dim + j];

                    result[model] = (dim, a, b, n);
                }
                catch
                {
                    // 单条记录反序列化失败不影响其余记录。
                }
            }
        }

        return result;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Clear the connection pool before disposing so the underlying file handle
        // is released immediately; otherwise SQLite keeps the file locked briefly
        // after Dispose() and tests that delete the DB file fail on Windows.
        Microsoft.Data.Sqlite.SqliteConnection.ClearPool(_connection);
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private void Execute(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}

/// <summary>
/// 无操作学习状态存储，持久化禁用时使用。
/// </summary>
internal sealed class NullLearningStateStore : IThompsonStateStore, IBanditStateStore
{
    public static NullLearningStateStore Instance { get; } = new();

    private NullLearningStateStore() { }

    Dictionary<string, (double Alpha, double Beta)> IThompsonStateStore.LoadAll()
        => new(StringComparer.OrdinalIgnoreCase);

    void IThompsonStateStore.Save(string modelName, double alpha, double beta) { }

    Dictionary<string, (int Dim, double[,] A, double[] B, int N)> IBanditStateStore.LoadAll()
        => new(StringComparer.OrdinalIgnoreCase);

    void IBanditStateStore.Save(string modelName, int dim, double[,] a, double[] b, int n) { }
}


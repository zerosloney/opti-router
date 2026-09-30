using MySqlConnector;

namespace OptiRouter.Routing;

/// <summary>
/// MariaDB/MySQL 持久化的学习状态存储，同时支持 Thompson 采样与 Contextual Bandit 状态。
/// 与 <see cref="SqliteLearningStateStore"/> 同构（表结构/语义一致），连接按操作从连接池获取。
/// </summary>
public sealed class MariaDbLearningStateStore : IThompsonStateStore, IBanditStateStore, IDisposable
{
    private readonly string _connectionString;
    private bool _disposed;

    /// <summary>
    /// 用 MariaDB 连接串构造。
    /// </summary>
    /// <param name="connectionString">MariaDB 连接串（与成本账本/审计同库不同表）。</param>
    public MariaDbLearningStateStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        _connectionString = connectionString;

        using var conn = new MySqlConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS optirouter_thompson_states (
                model_name VARCHAR(255) NOT NULL PRIMARY KEY,
                alpha DOUBLE NOT NULL,
                beta DOUBLE NOT NULL
            );
            CREATE TABLE IF NOT EXISTS optirouter_bandit_arms (
                model_name VARCHAR(255) NOT NULL PRIMARY KEY,
                dim INT NOT NULL,
                a_json LONGTEXT NOT NULL,
                b_json LONGTEXT NOT NULL,
                n INT NOT NULL DEFAULT 0
            );
            """;
        cmd.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public void Save(string modelName, double alpha, double beta)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(modelName);

        using var conn = new MySqlConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO optirouter_thompson_states (model_name, alpha, beta)
            VALUES (@model, @alpha, @beta)
            ON DUPLICATE KEY UPDATE
                alpha = VALUES(alpha),
                beta = VALUES(beta);
            """;
        cmd.Parameters.AddWithValue("@model", modelName);
        cmd.Parameters.AddWithValue("@alpha", alpha);
        cmd.Parameters.AddWithValue("@beta", beta);
        cmd.ExecuteNonQuery();
    }

    /// <inheritdoc />
    Dictionary<string, (double Alpha, double Beta)> IThompsonStateStore.LoadAll()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var result = new Dictionary<string, (double, double)>(StringComparer.OrdinalIgnoreCase);

        using var conn = new MySqlConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT model_name, alpha, beta FROM optirouter_thompson_states;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string model = reader.GetString(0);
            double alpha = reader.GetDouble(1);
            double beta = reader.GetDouble(2);
            result[model] = (alpha, beta);
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

        using var conn = new MySqlConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO optirouter_bandit_arms (model_name, dim, a_json, b_json, n)
            VALUES (@model, @dim, @a, @b, @n)
            ON DUPLICATE KEY UPDATE
                dim = VALUES(dim),
                a_json = VALUES(a_json),
                b_json = VALUES(b_json),
                n = VALUES(n);
            """;
        cmd.Parameters.AddWithValue("@model", modelName);
        cmd.Parameters.AddWithValue("@dim", dim);
        cmd.Parameters.AddWithValue("@a", aJson);
        cmd.Parameters.AddWithValue("@b", bJson);
        cmd.Parameters.AddWithValue("@n", n);
        cmd.ExecuteNonQuery();
    }

    /// <inheritdoc />
    Dictionary<string, (int Dim, double[,] A, double[] B, int N)> IBanditStateStore.LoadAll()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var result = new Dictionary<string, (int, double[,], double[], int)>(StringComparer.OrdinalIgnoreCase);

        using var conn = new MySqlConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT model_name, dim, a_json, b_json, n FROM optirouter_bandit_arms;";
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
                    ?? throw new InvalidDataException("optirouter_bandit_arms.a_json deserialized to null.");
                if (flatA.Length != dim * dim)
                    continue; // 维度不匹配，跳过（dim 变更导致）

                double[] b = System.Text.Json.JsonSerializer.Deserialize<double[]>(bJson)
                    ?? throw new InvalidDataException("optirouter_bandit_arms.b_json deserialized to null.");
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

        return result;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}

using System.Globalization;
using MySqlConnector;

namespace OptiRouter.Configuration;

/// <summary>
/// MariaDB/MySQL 版租户客户端 Key 存储（表 optirouter_client_keys，类型化列），
/// 经 <see cref="ClientKeyService"/> 双后端门面启用（<c>OptiRouter:ConfigDbConnectionString</c>）。
/// </summary>
/// <remarks>
/// 多实例共享同一库：建/改/删为按行写；花费以相对增量累加（<see cref="ApplySpendDelta"/>）；
/// QPS/预算准入经 <see cref="TryAdmit"/> 单条 UPDATE 的行锁条件自增原子判定（全局口径）。
/// 时间列以 UTC ISO 字符串存储（与 JSON 文件往返语义一致，DateTime Kind=Utc 保真）。
/// </remarks>
internal sealed class MariaDbClientKeyStore
{
    private readonly string _connectionString;

    public MariaDbClientKeyStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        _connectionString = connectionString;

        using var conn = new MySqlConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS optirouter_client_keys (
                key_id               VARCHAR(32)   NOT NULL PRIMARY KEY,
                key_hash             CHAR(64)      NOT NULL,
                key_prefix           VARCHAR(32)   NOT NULL,
                tenant_name          VARCHAR(255)  NOT NULL,
                daily_budget_usd     DECIMAL(18,6) NOT NULL DEFAULT 100.000000,
                daily_spend_usd      DECIMAL(18,6) NOT NULL DEFAULT 0.000000,
                daily_request_count  INT           NOT NULL DEFAULT 0,
                max_qps              INT           NOT NULL DEFAULT 50,
                enabled              TINYINT       NOT NULL DEFAULT 1,
                created_at           VARCHAR(40)   NOT NULL,
                daily_spend_date_utc VARCHAR(10)   NULL,
                qps_window_start     BIGINT        NULL,
                qps_count            INT           NOT NULL DEFAULT 0
            );
            """;
        cmd.ExecuteNonQuery();
        EnsureColumn(conn, "qps_window_start", "BIGINT NULL");
        EnsureColumn(conn, "qps_count", "INT NOT NULL DEFAULT 0");
    }

    /// <summary>旧库增量补列（CREATE TABLE IF NOT EXISTS 不会给已存在的表加列）。</summary>
    private static void EnsureColumn(MySqlConnection conn, string columnName, string definition)
    {
        using (var check = conn.CreateCommand())
        {
            check.CommandText = """
                SELECT COUNT(*) FROM information_schema.COLUMNS
                WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'optirouter_client_keys' AND COLUMN_NAME = @col;
                """;
            check.Parameters.AddWithValue("@col", columnName);
            if (Convert.ToInt64(check.ExecuteScalar(), CultureInfo.InvariantCulture) > 0) return;
        }

        try
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = $"ALTER TABLE optirouter_client_keys ADD COLUMN {columnName} {definition};";
            alter.ExecuteNonQuery();
        }
        catch (MySqlException ex) when (ex.Message.Contains("Duplicate column", StringComparison.OrdinalIgnoreCase))
        {
            // 另一实例并行补列。
        }
    }

    /// <summary>读取全部 Key（创建时间正序）。行结构校验由调用方（ClientKeyService）统一执行。</summary>
    public List<ClientKeyInfo> Load()
    {
        var result = new List<ClientKeyInfo>();
        using var conn = new MySqlConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT key_id, key_hash, key_prefix, tenant_name,
                   daily_budget_usd, daily_spend_usd, daily_request_count, max_qps, enabled,
                   created_at, daily_spend_date_utc
            FROM optirouter_client_keys
            ORDER BY created_at ASC, key_id ASC;
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new ClientKeyInfo
            {
                KeyId = reader.GetString(0),
                KeyHash = reader.GetString(1),
                KeyPrefix = reader.GetString(2),
                TenantName = reader.GetString(3),
                DailyBudgetUsd = reader.GetDecimal(4),
                DailySpendUsd = reader.GetDecimal(5),
                DailyRequestCount = reader.GetInt32(6),
                MaxQps = reader.GetInt32(7),
                Enabled = Convert.ToInt32(reader.GetValue(8), CultureInfo.InvariantCulture) != 0,
                CreatedAt = DateTime.Parse(reader.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal),
                DailySpendDateUtc = reader.IsDBNull(10)
                    ? null
                    : DateTime.ParseExact(reader.GetString(10), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)
            });
        }
        return result;
    }

    /// <summary>插入单个 Key 行（创建）。key_id 冲突（同 Guid 碰撞）由主键约束拒绝并抛出。</summary>
    public void InsertKey(ClientKeyInfo key)
    {
        ArgumentNullException.ThrowIfNull(key);
        using var conn = new MySqlConnection(_connectionString);
        conn.Open();
        using var cmd = CreateInsertCommand(conn, transaction: null, key);
        cmd.ExecuteNonQuery();
    }

    /// <summary>按 key_id 更新管理设置列（enabled/预算/QPS）。只写设置列，不触碰花费/计数控，多实例安全。</summary>
    public void UpdateKeySettings(ClientKeyInfo key)
    {
        ArgumentNullException.ThrowIfNull(key);
        using var conn = new MySqlConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE optirouter_client_keys
            SET daily_budget_usd = @budget, max_qps = @qps, enabled = @enabled
            WHERE key_id = @kid;
            """;
        cmd.Parameters.AddWithValue("@budget", key.DailyBudgetUsd);
        cmd.Parameters.AddWithValue("@qps", key.MaxQps);
        cmd.Parameters.AddWithValue("@enabled", key.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@kid", key.KeyId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>按 key_id 删除单行。</summary>
    public void DeleteKey(string keyId)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyId);
        using var conn = new MySqlConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM optirouter_client_keys WHERE key_id = @kid;";
        cmd.Parameters.AddWithValue("@kid", keyId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 全局准入判定（多实例共享口径）：单条 UPDATE 在 InnoDB 行锁下原子完成——
    /// QPS 固定秒窗计数自增、UTC 跨日请求计数/花费重置、以及 enabled/预算条件检查
    /// （预算/上限直接引用列值，即时反映其他实例的管理端修改）。
    /// affected=1 即已准入并完成计数；=0 时读行状态区分拒绝原因。
    /// 注意：MySQL/MariaDB 的 UPDATE SET 从左到右求值且后续赋值可见，跨日重置的 IF
    /// 必须排在 daily_spend_date_utc 赋值之前（否则永远读到新日期、不会重置）。
    /// </summary>
    public ClientKeyAdmission TryAdmit(string keyId, DateTime todayUtc, long currentWindowSecond)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyId);
        using var conn = new MySqlConnection(_connectionString);
        conn.Open();
        long affected;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE optirouter_client_keys
                SET qps_count            = IF(qps_window_start = @sec, qps_count + 1, 1),
                    qps_window_start     = @sec,
                    daily_spend_usd      = IF(daily_spend_date_utc = @today, daily_spend_usd, 0),
                    daily_request_count  = IF(daily_spend_date_utc = @today, daily_request_count + 1, 1),
                    daily_spend_date_utc = @today
                WHERE key_id = @kid
                  AND enabled = 1
                  AND (qps_window_start IS NULL OR qps_window_start <> @sec OR qps_count < max_qps)
                  AND (daily_budget_usd <= 0 OR daily_spend_date_utc IS NULL
                       OR daily_spend_date_utc <> @today OR daily_spend_usd < daily_budget_usd)
                """;
            cmd.Parameters.AddWithValue("@sec", currentWindowSecond);
            cmd.Parameters.AddWithValue("@today", todayUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("@kid", keyId);
            affected = cmd.ExecuteNonQuery();
        }

        if (affected == 1)
            return ClientKeyAdmission.Admitted;

        // 未准入：读当前行状态判别原因（预算优先于限流——RetryAfter 到次日零点更有用）。
        using var probe = conn.CreateCommand();
        probe.CommandText = """
            SELECT enabled, qps_window_start, qps_count, daily_budget_usd, daily_spend_usd, daily_spend_date_utc
            FROM optirouter_client_keys
            WHERE key_id = @kid;
            """;
        probe.Parameters.AddWithValue("@kid", keyId);
        using var reader = probe.ExecuteReader();
        if (!reader.Read())
            return ClientKeyAdmission.NotFound;

        bool enabled = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture) != 0;
        long? windowStart = reader.IsDBNull(1) ? null : reader.GetInt64(1);
        int qpsCount = reader.GetInt32(2);
        decimal budget = reader.GetDecimal(3);
        decimal spend = reader.GetDecimal(4);
        string? spendDate = reader.IsDBNull(5) ? null : reader.GetString(5);
        string today = todayUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        if (!enabled)
            return ClientKeyAdmission.Disabled;
        if (budget > 0m && spendDate == today && spend >= budget)
            return ClientKeyAdmission.BudgetExhausted;
        if (windowStart == currentWindowSecond && qpsCount > 0)
            return ClientKeyAdmission.RateLimited;

        // 状态在 UPDATE 与探测之间漂移（如窗口刚好跨秒）：按限流处理，客户端 1 秒后重试。
        return ClientKeyAdmission.RateLimited;
    }

    /// <summary>
    /// 累加一个 Key 当日（UTC）花费，单条原子语句处理跨日滚动：
    /// 当日已有记录则增量累加，否则（新一天或首次）从增量值起算。
    /// 各实例只提交自己的增量，全局值在库内收敛，不会互相覆盖。行不存在时为 no-op。
    /// </summary>
    public void ApplySpendDelta(string keyId, DateTime todayUtc, decimal spendDelta)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyId);
        using var conn = new MySqlConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE optirouter_client_keys
            SET daily_spend_usd     = IF(daily_spend_date_utc = @today, daily_spend_usd + @spendDelta, @spendDelta),
                daily_spend_date_utc = @today
            WHERE key_id = @kid;
            """;
        cmd.Parameters.AddWithValue("@today", todayUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@spendDelta", spendDelta);
        cmd.Parameters.AddWithValue("@kid", keyId);
        cmd.ExecuteNonQuery();
    }

    private static MySqlCommand CreateInsertCommand(MySqlConnection conn, MySqlTransaction? transaction, ClientKeyInfo key)
    {
        var ins = conn.CreateCommand();
        ins.Transaction = transaction;
        ins.CommandText = """
            INSERT INTO optirouter_client_keys
                (key_id, key_hash, key_prefix, tenant_name,
                 daily_budget_usd, daily_spend_usd, daily_request_count, max_qps, enabled,
                 created_at, daily_spend_date_utc)
            VALUES (@kid, @khash, @kprefix, @tenant,
                    @budget, @spend, @reqCount, @qps, @enabled,
                    @createdAt, @spendDate);
            """;
        ins.Parameters.AddWithValue("@kid", key.KeyId);
        ins.Parameters.AddWithValue("@khash", key.KeyHash);
        ins.Parameters.AddWithValue("@kprefix", key.KeyPrefix);
        ins.Parameters.AddWithValue("@tenant", key.TenantName);
        ins.Parameters.AddWithValue("@budget", key.DailyBudgetUsd);
        ins.Parameters.AddWithValue("@spend", key.DailySpendUsd);
        ins.Parameters.AddWithValue("@reqCount", key.DailyRequestCount);
        ins.Parameters.AddWithValue("@qps", key.MaxQps);
        ins.Parameters.AddWithValue("@enabled", key.Enabled ? 1 : 0);
        ins.Parameters.AddWithValue("@createdAt", key.CreatedAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
        ins.Parameters.AddWithValue("@spendDate", (object?)FormatDate(key.DailySpendDateUtc) ?? DBNull.Value);
        return ins;
    }

    private static string? FormatDate(DateTime? utcDate)
        => utcDate?.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>全局准入判定结果（<see cref="MariaDbClientKeyStore.TryAdmit"/>）。</summary>
internal enum ClientKeyAdmission
{
    /// <summary>已准入（QPS 窗口计数与当日请求数已在库内原子自增）。</summary>
    Admitted,

    /// <summary>全局 QPS 窗口已满。</summary>
    RateLimited,

    /// <summary>当日全局花费已达预算。</summary>
    BudgetExhausted,

    /// <summary>Key 已被其他实例禁用。</summary>
    Disabled,

    /// <summary>行已被其他实例删除。</summary>
    NotFound
}

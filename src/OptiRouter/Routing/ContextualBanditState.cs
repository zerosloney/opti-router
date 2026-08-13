using System.Collections.Concurrent;

namespace OptiRouter.Routing;

/// <summary>
/// 上下文老虎机（Contextual Bandit / LinUCB）状态存储。
/// 每模型维护一个线性模型 θ（d 维），用上下文特征向量 x（分类信号 one-hot + tier one-hot + bias）
/// 预测期望奖励，加 UCB 不确定性项平衡探索-利用。
/// 修非上下文 Thompson 「只优化延迟、系统性低估 Strong」的缺陷——LinUCB 用请求特征学习「模型↔任务」匹配。
/// </summary>
/// <remarks>
/// LinUCB（Li et al., WWW 2010）：
///   score = θ·x + α·sqrt(xᵀ A⁻¹ x)
///   update: A += x·xᵀ, b += reward·x, θ = A⁻¹·b
/// 线程安全：ConcurrentDictionary + 每 arm 锁。零 I/O，决策层内存读。
/// </remarks>
public sealed class ContextualBanditState
{
    /// <summary>单模型 arm 状态：协方差逆 A、累积 b、样本数 N。</summary>
    public sealed class ArmState
    {
        /// <summary>协方差矩阵 A（d×d），初始为单位阵（岭回归先验）。</summary>
        public double[,] A;

        /// <summary>累积向量 b（d），b += reward·x。</summary>
        public double[] B;

        /// <summary>样本数。</summary>
        public int N;

        /// <summary>arm 锁（A/B 更新与读取互斥）。</summary>
        public readonly object Lock = new();

        public ArmState(int dim)
        {
            A = new double[dim, dim];
            for (int i = 0; i < dim; i++) A[i, i] = 1.0;  // 单位阵先验
            B = new double[dim];
            N = 0;
        }
    }

    private readonly ConcurrentDictionary<string, ArmState> _arms;
    private readonly int _dim;
    private readonly IBanditStateStore? _persistence;

    /// <summary>
    /// 构造上下文老虎机状态。
    /// </summary>
    /// <param name="dim">上下文特征维度（默认 11 = 7 信号 + 3 tier + 1 bias）。</param>
    /// <param name="persistence">持久化接口；null（默认）时不持久化。</param>
    public ContextualBanditState(int dim = 11, IBanditStateStore? persistence = null)
    {
        _dim = dim;
        _arms = new ConcurrentDictionary<string, ArmState>(StringComparer.OrdinalIgnoreCase);
        _persistence = persistence;

        if (_persistence is not null)
        {
            foreach (var (model, armState) in _persistence.LoadAll())
            {
                if (armState.Dim != dim) continue;
                _arms[model] = new ArmState(dim)
                {
                    A = armState.A,
                    B = armState.B,
                    N = armState.N
                };
            }
        }
    }

    /// <summary>上下文特征维度。</summary>
    public int Dimension => _dim;

    /// <summary>当前跟踪的模型数。</summary>
    public int Count => _arms.Count;

    /// <summary>获取或创建某模型的 arm 状态。</summary>
    public ArmState GetOrAdd(string modelName)
        => _arms.GetOrAdd(modelName, _ => new ArmState(_dim));

    /// <summary>
    /// LinUCB 打分：θ·x + α·sqrt(xᵀ A⁻¹ x)。样本不足（N==0）时 θ=0，仅 UCB 项（探索）。
    /// </summary>
    /// <param name="modelName">模型名。</param>
    /// <param name="context">上下文特征向量（长度 = <see cref="Dimension"/>）。</param>
    /// <param name="alpha">探索系数 α。</param>
    /// <returns>LinUCB 分数（越高越优先）。</returns>
    public double Predict(string modelName, double[] context, double alpha)
    {
        if (context.Length != _dim)
            throw new ArgumentException($"context 长度 {context.Length} 必须等于维度 {_dim}", nameof(context));

        var arm = GetOrAdd(modelName);
        lock (arm.Lock)
        {
            // θ = A⁻¹·b（解线性方程组 A·θ = b）。
            var theta = SolveViaGaussJordan(arm.A, arm.B);
            double mean = Dot(theta, context);

            // UCB 项：α·sqrt(xᵀ A⁻¹ x)。
            var aInvX = SolveViaGaussJordan(arm.A, context);
            double uncertainty = Math.Sqrt(Dot(context, aInvX));

            return mean + alpha * uncertainty;
        }
    }

    /// <summary>
    /// 更新 arm：A += x·xᵀ，b += reward·x，历史按 discount 衰减。
    /// </summary>
    /// <param name="modelName">模型名。</param>
    /// <param name="context">上下文特征向量。</param>
    /// <param name="reward">奖励（快成功 1.0 / 慢成功 0.3 / 失败 0 / 竞速 0.5）。</param>
    /// <param name="discount">历史折扣因子 [0.5, 0.99]。</param>
    public void Update(string modelName, double[] context, double reward, double discount)
    {
        if (context.Length != _dim)
            throw new ArgumentException($"context 长度 {context.Length} 必须等于维度 {_dim}", nameof(context));

        var arm = GetOrAdd(modelName);
        lock (arm.Lock)
        {
            // 历史衰减：A *= discount, b *= discount（旧样本影响力随时间减弱）。
            for (int i = 0; i < _dim; i++)
            {
                arm.B[i] *= discount;
                for (int j = 0; j < _dim; j++)
                    arm.A[i, j] *= discount;
            }

            // A += x·xᵀ
            for (int i = 0; i < _dim; i++)
                for (int j = 0; j < _dim; j++)
                    arm.A[i, j] += context[i] * context[j];

            // b += reward·x
            for (int i = 0; i < _dim; i++)
                arm.B[i] += reward * context[i];

            arm.N++;
        }

        _persistence?.Save(modelName, _dim, arm.A, arm.B, arm.N);
    }

    /// <summary>
    /// 热重载清理：剔除已删除/改名的模型条目，防 _arms 无界增长。
    /// </summary>
    /// <param name="retainNames">应保留的模型名集合。</param>
    /// <returns>移除的条目数。</returns>
    public int Retain(IEnumerable<string>? retainNames)
    {
        if (retainNames is null) return 0;
        var keep = new HashSet<string>(retainNames, StringComparer.OrdinalIgnoreCase);
        int removed = 0;
        foreach (var key in _arms.Keys)
        {
            if (!keep.Contains(key) && _arms.TryRemove(key, out _))
                removed++;
        }
        return removed;
    }

    /// <summary>
    /// 解线性方程组 A·x = b（高斯-约旦消元 + 部分主元）。
    /// A 为对称正定（岭回归），数值稳定。命名揭示 O(d³) 成本，避免误以为是 O(d²) 三角求解。
    /// </summary>
    private static double[] SolveViaGaussJordan(double[,] a, double[] b)
    {
        int n = b.Length;
        // 增广矩阵 [A | b] 拷贝（不修改原 A）。
        // ridge floor：Update 每步对整个 A 做 *= discount，未触发的 one-hot 特征列对角元按 discount^k
        // 衰减（discount=0.95 约 538 步、0.5 约 40 步跌破 1e-12）。原实现一旦单列奇异就返回全零，
        // 使整组 Predict（θ 与 A⁻¹x）作废，bandit 静默退化为 no-op。加未衰减岭项保证非奇异。
        const double ridge = 1e-6;
        var m = new double[n, n + 1];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++) m[i, j] = a[i, j];
            m[i, i] += ridge;
            m[i, n] = b[i];
        }

        // 高斯消元（部分主元）。
        for (int col = 0; col < n; col++)
        {
            // 找主元行。
            int pivot = col;
            double maxAbs = Math.Abs(m[col, col]);
            for (int r = col + 1; r < n; r++)
            {
                double v = Math.Abs(m[r, col]);
                if (v > maxAbs) { maxAbs = v; pivot = r; }
            }
            // 加 ridge 后仍奇异（极端）：该列无信息，跳过消元置零，不丢弃其他列的解。
            if (maxAbs < 1e-12) continue;

            if (pivot != col)
            {
                for (int j = 0; j <= n; j++) (m[col, j], m[pivot, j]) = (m[pivot, j], m[col, j]);
            }

            // 消元。
            for (int r = 0; r < n; r++)
            {
                if (r == col) continue;
                double factor = m[r, col] / m[col, col];
                for (int j = col; j <= n; j++)
                    m[r, j] -= factor * m[col, j];
            }
        }

        // 回代（被跳过的奇异列对角元为 0，该分量置 0）。
        var x = new double[n];
        for (int i = 0; i < n; i++)
            x[i] = Math.Abs(m[i, i]) < 1e-12 ? 0.0 : m[i, n] / m[i, i];
        return x;
    }

    private static double Dot(double[] a, double[] b)
    {
        double s = 0.0;
        for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }
}

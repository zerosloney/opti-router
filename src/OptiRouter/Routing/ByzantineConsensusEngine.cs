namespace OptiRouter.Routing;

/// <summary>
/// 候选模型响应项。
/// </summary>
public sealed record ModelResponseCandidate(
    string ModelName,
    string OutputText,
    long LatencyMs = 0,
    decimal Cost = 0m);

/// <summary>
/// 拜占庭共识裁决结果。
/// </summary>
public sealed record ByzantineConsensusResult(
    bool ConsensusAchieved,
    string WinningModelName,
    string WinningOutputText,
    double ConsensusScore,
    IReadOnlyList<string> OutlierModels,
    IReadOnlyDictionary<string, double> IndividualScores,
    string Reason);

/// <summary>
/// 拜占庭容错 (BFT) 与多模型多重共识校验引擎 (Byzantine Fault-Tolerant Consensus Engine)。
/// 在高安全、零幻觉场景下（如金融核算、法规审计、医疗推理），对多个异构厂商模型（如 GPT-4o、Claude 3.5 Sonnet、DeepSeek-V3）
/// 的输出进行词法/语义向量对齐与拜占庭投票，自动识别并剔除语义偏离度高、存在幻觉或被注入攻击的异常响应，
/// 提取多数共识（Majority Consensus）的最高置信度结果。
/// </summary>
public sealed class ByzantineConsensusEngine
{
    /// <summary>
    /// 对多个候选模型的响应进行拜占庭共识评定与异常剔除。
    /// </summary>
    /// <param name="candidates">候选模型输出列表 (建议 >= 3 个不同上游)</param>
    /// <param name="outlierThreshold">判定为两两达成共识的相似度门限（默认 0.50）</param>
    public ByzantineConsensusResult EvaluateConsensus(
        IReadOnlyList<ModelResponseCandidate> candidates,
        double outlierThreshold = 0.50)
    {
        if (candidates == null || candidates.Count == 0)
        {
            return new ByzantineConsensusResult(
                ConsensusAchieved: false,
                WinningModelName: string.Empty,
                WinningOutputText: string.Empty,
                ConsensusScore: 0.0,
                OutlierModels: Array.Empty<string>(),
                IndividualScores: new Dictionary<string, double>(),
                Reason: "No candidate responses available for consensus.");
        }

        if (candidates.Count == 1)
        {
            var single = candidates[0];
            return new ByzantineConsensusResult(
                ConsensusAchieved: true,
                WinningModelName: single.ModelName,
                WinningOutputText: single.OutputText,
                ConsensusScore: 1.0,
                OutlierModels: Array.Empty<string>(),
                IndividualScores: new Dictionary<string, double> { [single.ModelName] = 1.0 },
                Reason: "Single candidate auto-adopted.");
        }

        int n = candidates.Count;
        var vocab = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var tokenized = new List<string[]>(n);

        foreach (var cand in candidates)
        {
            var tokens = (cand.OutputText ?? string.Empty)
                .Split(new[] { ' ', '\n', '\r', '\t', ',', '.', ';', ':', '!', '?' }, StringSplitOptions.RemoveEmptyEntries);
            tokenized.Add(tokens);
            foreach (var t in tokens)
            {
                if (!vocab.ContainsKey(t))
                {
                    vocab[t] = vocab.Count;
                }
            }
        }

        int dim = vocab.Count;
        var vectors = new float[n][];
        for (int i = 0; i < n; i++)
        {
            vectors[i] = new float[dim];
            foreach (var t in tokenized[i])
            {
                if (vocab.TryGetValue(t, out int idx))
                {
                    vectors[i][idx] += 1.0f;
                }
            }
            // L2 归一化
            double normSq = 0.0;
            for (int d = 0; d < dim; d++) normSq += vectors[i][d] * vectors[i][d];
            if (normSq > 0.0)
            {
                float inv = (float)(1.0 / Math.Sqrt(normSq));
                for (int d = 0; d < dim; d++) vectors[i][d] *= inv;
            }
        }

        // 计算全两两余弦相似度矩阵
        var simMatrix = new double[n, n];
        for (int i = 0; i < n; i++)
        {
            simMatrix[i, i] = 1.0;
            for (int j = i + 1; j < n; j++)
            {
                double dot = 0.0;
                for (int d = 0; d < dim; d++) dot += vectors[i][d] * vectors[j][d];
                double sim = Math.Clamp(dot, 0.0, 1.0);
                simMatrix[i, j] = sim;
                simMatrix[j, i] = sim;
            }
        }

        // 拜占庭共识投票：每个候选统计与其相似度 >= outlierThreshold 的多数法定对端数
        int quorumRequired = n / 2; // 多数派法定人数
        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var outliers = new List<string>();
        double maxScore = -1.0;
        int bestIndex = 0;

        for (int i = 0; i < n; i++)
        {
            int agreeCount = 0;
            double sumAgreeSim = 0.0;

            for (int j = 0; j < n; j++)
            {
                if (i != j && simMatrix[i, j] >= outlierThreshold)
                {
                    agreeCount++;
                    sumAgreeSim += simMatrix[i, j];
                }
            }

            double candidateScore = agreeCount > 0 ? (sumAgreeSim / agreeCount) : 0.0;
            scores[candidates[i].ModelName] = candidateScore;

            if (agreeCount < quorumRequired)
            {
                outliers.Add(candidates[i].ModelName);
            }

            if (candidateScore > maxScore)
            {
                maxScore = candidateScore;
                bestIndex = i;
            }
        }

        var winner = candidates[bestIndex];
        bool achieved = (n - outliers.Count) >= (quorumRequired + 1) && maxScore >= outlierThreshold;

        string reason = achieved
            ? $"Byzantine consensus achieved on '{winner.ModelName}' with agreement score {maxScore:F3} (outliers pruned: {outliers.Count})."
            : $"Consensus divergence detected: insufficient quorum (outliers={outliers.Count}, quorum_required={quorumRequired + 1}).";

        return new ByzantineConsensusResult(
            ConsensusAchieved: achieved,
            WinningModelName: winner.ModelName,
            WinningOutputText: winner.OutputText,
            ConsensusScore: maxScore,
            OutlierModels: outliers,
            IndividualScores: scores,
            Reason: reason);
    }
}

using System.Collections.Concurrent;
using System.Text;
using OptiRouter.Clients;

namespace OptiRouter.Routing;

/// <summary>
/// KV Cache Trie 节点与命中状态信息。
/// </summary>
public sealed record KvCacheHitResult(
    string ModelName,
    int MatchedPrefixLength,
    double SavingsRatio,
    DateTimeOffset LastHitTime);

/// <summary>
/// KV-Cache 空间局部性与 Radix Trie 前缀亲和性树 (KV-Cache Prefix Locality Trie)。
/// 维护全局系统提示词 (System Prompt) 及 RAG 长上下文前缀索引，计算输入 Prompt 与各上游模型历史 KV Cache 的重合前缀长度。
/// 优先将具备热 KV Cache 前缀的请求“钉选”至对应 Provider，以最大化利用 OpenAI / Claude / DeepSeek 上游的 Prompt Caching 优惠（降低 80% 延迟与 50%~90% 成本）。
/// </summary>
/// <remarks>
/// 内存有界：节点总数受硬上限（默认 100k）约束，超限或过期子树由
/// <see cref="PruneExpired"/> 周期性回收（复用缓存 TTL 作为子树活跃判定），
/// 防止长期运行下唯一前缀路径无限增长耗尽内存。
/// </remarks>
public sealed class KvCachePrefixTrie
{
    private sealed class TrieNode
    {
        public ConcurrentDictionary<string, TrieNode> Children { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, DateTimeOffset> ModelAccessTimes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public DateTimeOffset LastAccessUtc = DateTimeOffset.MinValue;
    }

    private readonly TrieNode _root = new();
    private readonly TimeSpan _cacheTtl;
    private readonly TimeSpan _pruneMinInterval;
    private readonly TimeProvider _timeProvider;
    private readonly int _maxNodes;

    private int _nodeCount;
    private int _pruneInProgress;
    private long _lastPruneUtcTicks;

    /// <summary>
    /// 当前 Trie 节点总数（含根节点），用于测试与诊断。
    /// </summary>
    public int NodeCount => Volatile.Read(ref _nodeCount);

    public KvCachePrefixTrie(
        TimeSpan? cacheTtl = null,
        TimeProvider? timeProvider = null,
        int maxNodes = 100_000,
        TimeSpan? pruneMinInterval = null)
    {
        _cacheTtl = cacheTtl ?? TimeSpan.FromMinutes(10);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _maxNodes = Math.Max(1, maxNodes);
        _pruneMinInterval = pruneMinInterval ?? TimeSpan.FromSeconds(30);
        Interlocked.Increment(ref _nodeCount); // 根节点
    }

    /// <summary>
    /// 从 ChatRequest 提取结构化前缀 Token 序列/词块。
    /// </summary>
    public static List<string> ExtractPrefixTokens(ChatRequest request, int maxPrefixChunks = 16)
    {
        var chunks = new List<string>();
        if (request?.Messages == null || request.Messages.Count == 0)
            return chunks;

        foreach (var msg in request.Messages)
        {
            string text = msg.GetText();
            if (string.IsNullOrEmpty(text))
                continue;

            // 按空白或标点拆分为词块/Token 组
            var words = text.Split(new[] { ' ', '\n', '\r', '\t', '，', '。', '；', '：' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var word in words)
            {
                chunks.Add(word);
                if (chunks.Count >= maxPrefixChunks)
                    return chunks;
            }
        }

        return chunks;
    }

    /// <summary>
    /// 记录请求完成后的前缀 KV Cache 命中状态。
    /// </summary>
    public void RecordCachePrefix(ChatRequest request, string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName) || request == null) return;
        var tokens = ExtractPrefixTokens(request);
        RecordCachePrefix(tokens, modelName);
    }

    /// <summary>
    /// 记录前缀 Token 序列与模型的 KV Cache 温暖状态（支持本地与分布式同步）。
    /// </summary>
    public void RecordCachePrefix(IReadOnlyList<string> tokens, string modelName, DateTimeOffset? accessTime = null)
    {
        if (string.IsNullOrWhiteSpace(modelName) || tokens == null || tokens.Count < 3) return;

        var current = _root;
        var now = accessTime ?? _timeProvider.GetUtcNow();

        foreach (var token in tokens)
        {
            bool isNew = false;
            current = current.Children.GetOrAdd(token, _ =>
            {
                isNew = true;
                return new TrieNode();
            });
            if (isNew)
            {
                Interlocked.Increment(ref _nodeCount);
            }
            current.LastAccessUtc = now;
            current.ModelAccessTimes[modelName] = now;

            // 节点数超上限时尝试回收过期子树（带节流，避免每次插入全树扫描）。
            if (_nodeCount > _maxNodes
                && now.UtcTicks - Interlocked.Read(ref _lastPruneUtcTicks) >= _pruneMinInterval.Ticks
                && Interlocked.Exchange(ref _pruneInProgress, 1) == 0)
            {
                try
                {
                    int removed = PruneExpired(now);
                    if (removed > 0)
                    {
                        Interlocked.Add(ref _nodeCount, -removed);
                    }
                    Interlocked.Exchange(ref _lastPruneUtcTicks, _timeProvider.GetUtcNow().UtcTicks);
                }
                finally
                {
                    Volatile.Write(ref _pruneInProgress, 0);
                }
            }
        }
    }

    /// <summary>
    /// 查找对当前请求前缀具有最长 KV-Cache 温暖匹配的候选模型。
    /// </summary>
    public KvCacheHitResult? FindBestMatchingModel(ChatRequest request, HashSet<string> candidateModelNames)
    {
        if (candidateModelNames == null || candidateModelNames.Count == 0)
            return null;

        var tokens = ExtractPrefixTokens(request);
        if (tokens.Count < 3) return null;

        var current = _root;
        int depth = 0;
        int bestMatchedLength = 0;
        string? bestModel = null;
        DateTimeOffset bestHitTime = DateTimeOffset.MinValue;
        var now = _timeProvider.GetUtcNow();

        foreach (var token in tokens)
        {
            if (!current.Children.TryGetValue(token, out var nextNode))
                break;

            current = nextNode;
            depth++;

            // 检查该节点下是否有存活未超时的候选模型 KV-Cache
            foreach (var candidate in candidateModelNames)
            {
                if (current.ModelAccessTimes.TryGetValue(candidate, out var lastAccess))
                {
                    if (now - lastAccess <= _cacheTtl)
                    {
                        if (depth > bestMatchedLength)
                        {
                            bestMatchedLength = depth;
                            bestModel = candidate;
                            bestHitTime = lastAccess;
                        }
                    }
                }
            }
        }

        if (bestModel == null || bestMatchedLength < 3)
            return null;

        double savingsRatio = Math.Min(0.9, 0.3 + 0.05 * bestMatchedLength);
        return new KvCacheHitResult(bestModel, bestMatchedLength, savingsRatio, bestHitTime);
    }

    /// <summary>
    /// 递归回收超过 <paramref name="cutoff"/> 未活跃的子树，返回删除的节点数。
    /// 子树删除判定：该节点自身及其所有后代在 TTL 窗口内均无任何模型访问。
    /// </summary>
    private int PruneExpired(DateTimeOffset cutoff)
    {
        DateTimeOffset expiredBefore = cutoff - _cacheTtl;
        int removed = 0;
        PruneSubtree(_root, expiredBefore, ref removed);
        return removed;
    }

    /// <summary>
    /// 剪除本节点下所有过期子树；返回 true 表示本节点自身也可从父节点移除。
    /// 判定只依据节点自身活跃性（LastAccessUtc），而非整个子树过期——被多条路径共享的
    /// 子节点（如公共后缀）由其它活跃路径保活，删除过期父节点不影响其可达性。
    /// </summary>
    private static bool PruneSubtree(TrieNode node, DateTimeOffset cutoff, ref int removed)
    {
        foreach (var child in node.Children.ToList())
        {
            if (PruneSubtree(child.Value, cutoff, ref removed))
            {
                node.Children.TryRemove(child.Key, out _);
                removed++;
            }
        }

        return node.LastAccessUtc < cutoff;
    }
}

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
public sealed class KvCachePrefixTrie
{
    private sealed class TrieNode
    {
        public ConcurrentDictionary<string, TrieNode> Children { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, DateTimeOffset> ModelAccessTimes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentDictionary<string, int> ModelHitCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly TrieNode _root = new();
    private readonly TimeSpan _cacheTtl;
    private readonly TimeProvider _timeProvider;

    public KvCachePrefixTrie(TimeSpan? cacheTtl = null, TimeProvider? timeProvider = null)
    {
        _cacheTtl = cacheTtl ?? TimeSpan.FromMinutes(10);
        _timeProvider = timeProvider ?? TimeProvider.System;
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
        if (string.IsNullOrWhiteSpace(modelName)) return;

        var tokens = ExtractPrefixTokens(request);
        if (tokens.Count < 3) return; // 过短的前缀无 KV-Cache 价值

        var current = _root;
        var now = _timeProvider.GetUtcNow();

        foreach (var token in tokens)
        {
            current = current.Children.GetOrAdd(token, _ => new TrieNode());
            current.ModelAccessTimes[modelName] = now;
            current.ModelHitCounts.AddOrUpdate(modelName, 1, (_, count) => count + 1);
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
}

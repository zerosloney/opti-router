using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>
/// KV-Cache 空间局部性与 Prefix 亲和性重排策略 (KV-Cache Locality Policy)。
/// 利用 Radix Trie 前缀匹配树，计算当前请求与各上游模型历史 KV-Cache 的重合前缀长度。
/// 优先提振具备热 KV Cache 的模型候选，提升 Prompt Caching 命中率。
/// </summary>
public sealed class KvCacheLocalityPolicy : IRouterPolicy
{
    private readonly KvCachePrefixTrie _kvCacheTrie;

    public KvCacheLocalityPolicy(KvCachePrefixTrie kvCacheTrie)
    {
        _kvCacheTrie = kvCacheTrie;
    }

    /// <inheritdoc />
    public PolicyGroup Group => PolicyGroup.Order;

    /// <inheritdoc />
    public RouterDecision Apply(RouterContext context, RouterDecision previous)
    {
        var routing = context.Options.Routing;
        if (!routing.EnableKvCacheLocality)
        {
            return previous.Append("kv-cache-locality", "disabled");
        }

        if (previous.Candidates.Count < 2 || context.Request == null)
        {
            return previous.Append("kv-cache-locality", "<2 candidates");
        }

        var candidateNames = previous.Candidates.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hit = _kvCacheTrie.FindBestMatchingModel(context.Request, candidateNames);

        if (hit == null)
        {
            return previous.Append("kv-cache-locality", "no warm kv-cache hit");
        }

        // 将具有热 KV Cache 的模型提升至最前（保留同 tier 内或跨 tier 绝对提振）
        var reordered = new List<ModelEndpointOptions>(previous.Candidates.Count);
        ModelEndpointOptions? hitModel = null;

        foreach (var m in previous.Candidates)
        {
            if (string.Equals(m.Name, hit.ModelName, StringComparison.OrdinalIgnoreCase))
            {
                hitModel = m;
            }
            else
            {
                reordered.Add(m);
            }
        }

        if (hitModel != null)
        {
            reordered.Insert(0, hitModel);
        }

        var withResult = previous with { Candidates = reordered };
        string reason = $"promoted warm kv-cache model '{hit.ModelName}' (matched={hit.MatchedPrefixLength} tokens, est_savings={hit.SavingsRatio:P0})";

        return withResult.Append("kv-cache-locality", reason);
    }
}

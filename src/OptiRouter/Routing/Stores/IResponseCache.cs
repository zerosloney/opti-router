using OptiRouter.Clients;

namespace OptiRouter.Routing;

/// <summary>
/// 响应缓存：幂等非流式请求的精确缓存，命中即短路返回（不经路由/上游）。
/// </summary>
public interface IResponseCache
{
    /// <summary>尝试获取缓存的响应；命中返回 true。</summary>
    bool TryGet(string key, out RawChatResponse? response);

    /// <summary>写入缓存（按 TTL 过期）。</summary>
    void Set(string key, RawChatResponse response, TimeSpan ttl);
}

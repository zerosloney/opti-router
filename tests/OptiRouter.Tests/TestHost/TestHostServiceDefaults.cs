using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace OptiRouter.Tests;

/// <summary>
/// 集成测试宿主的公共默认值。测试命名空间下的工厂直接调用（父命名空间隐式可见，无需 using）。
/// </summary>
public static class TestHostServiceDefaults
{
    /// <summary>
    /// 移除全部后台 HostedService。健康探针会在测试中途给模型打熔断标记、会话亲和预热
    /// 会改写粘性缓存、指标 gauge 定时刷新会与断言竞争——这些后台任务改写的是请求间共享
    /// 状态，是 flaky 的根源；测试需要相关状态时在测试内自行构造。
    /// 在 WebApplicationFactory 的 ConfigureServices（Program.cs 注册之后执行）中调用。
    /// </summary>
    public static void RemoveBackgroundServices(this IServiceCollection services)
    {
        services.RemoveAll(typeof(IHostedService));
    }
}

using Xunit;

namespace OptiRouter.Tests;

/// <summary>
/// 需要 OPTIROUTER_MARIADB_TEST 连接串（专用临时 MariaDB）的集成测试门控。
/// 未设置时在发现阶段置 <see cref="FactAttribute.Skip"/>——结果计为 Skipped，
/// 而非静默早退伪装成的 Passed（架构审查 P2-6：通过数不得掩盖未执行的用例）。
/// dotnet test 的发现与执行同在 testhost 进程，环境变量两种场景下读取一致：
/// CI 的 build-test 任务注入 MariaDB 服务容器并设置该变量，门控用例真实执行。
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class MariaDbFactAttribute : FactAttribute
{
    public MariaDbFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPTIROUTER_MARIADB_TEST")))
        {
            Skip = "需要 OPTIROUTER_MARIADB_TEST（专用临时 MariaDB 连接串），未设置故未执行。";
        }
    }
}

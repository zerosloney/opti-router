# OptiRouter K8s 多实例部署

最小可用清单：无状态 Deployment + ClusterIP Service。镜像本身多阶段构建、非 root 运行、内建 HEALTHCHECK（见根 [Dockerfile](../../Dockerfile)）。

## 多实例的存储矩阵前提（先读这个）

单副本无要求；**replicas > 1 时必须按能力矩阵选择共享后端**，进程内存状态不会跨 Pod 同步：

| 状态 | 多实例要求 | 说明 |
| --- | --- | --- |
| 配置库（路由/预算/模型/租户 Key） | **必须** MariaDB（`OptiRouter:ConfigDbConnectionString`） | 保存走跨实例原子 CAS；SQLite 文件不能多 Pod 共享 |
| 成本账本 / 断路器状态 | **必须** 服务器型后端（`Budget:StoreProvider` = `MariaDb` / `Postgres` / `Redis`） | `Auto` 跟随 ConfigDbConnectionString 自动选 MariaDb |
| 请求审计 | 建议服务器型（MariaDb/Postgres） | 异步批量落库 |
| 响应缓存 / 健康统计 / 亲和 / 部分学习状态 | 进程内存，跨 Pod **不同步** | 可选 Redis Mesh 广播，非强一致；每 Pod 独立冷启动后自愈 |

各后端能力差异**不能互相推断**（如账本支持 Redis 不代表审计支持），详见 [docs/ARCHITECTURE.md](../../docs/ARCHITECTURE.md) 存储后端矩阵。

## 使用

```bash
# 1. 创建 Secret（含生产配置库连接串；勿提交真实值）
kubectl create secret generic optirouter-config \
  --from-literal=connection-string="Server=mariadb;Port=3306;Database=optirouter;User ID=optirouter;Password=..."

# 2. 部署（镜像 tag 按你的 registry 替换；MinVer 版本经 --build-arg VERSION 注入构建）
kubectl apply -f optirouter.yaml

# 3. 验证
kubectl rollout status deployment/optirouter
curl http://<svc>/health   # 无需鉴权
```

首次启动会按 appsettings/环境变量向配置库播种一次；生产管理密钥用 `OptiRouter__AdminApiKey` 环境变量注入（仅作首启种子，入库后即被忽略），建议追加到 Secret 并以 `secretKeyRef` 注入。

## 资源与扩缩

- 就绪/存活探针均为 `GET /health`（无鉴权、不受限流影响）；readiness 首批请求前建议留出配置库迁移时间（`initialDelaySeconds: 5` 起步，按迁移耗时上调）。
- HPA 可直接对 Deployment 扩缩；扩容前确认上表存储矩阵前提，否则各 Pod 账本分裂、熔断状态不一致。
- 版本号：镜像构建时 `--build-arg VERSION=x.y.z`（容器内无 .git，MinVer 从该参数注入，见 CI 的 docker-build job）。

namespace OptiRouter.Configuration;

/// <summary>
/// 模型路由名归一化：Name 留空且配置了 <see cref="ModelEndpointOptions.Id"/> 时，
/// 自动生成为 "{供应商}/{Id}"（供应商取显式 <c>Provider</c> 配置，缺省从 BaseUrl 推断）。
/// 自动生成的名称与既有名称冲突时按配置顺序追加 " #2"、" #3" 去重，
/// 支持同供应商同模型多端点（多 Key）场景；显式配置的重复名称不在此去重，
/// 由 <c>RouterOptionsValidator</c> 启动校验拒绝。
/// 在 Options 的 PostConfigure 阶段执行，验证与所有消费方（路由/客户端/显示）看到的都是最终名称。
/// </summary>
public static class ModelNameNormalizer
{
    /// <summary>
    /// 就地归一化模型列表的 Name 字段。
    /// </summary>
    /// <param name="models">模型端点列表（按配置顺序）。</param>
    public static void Normalize(IList<ModelEndpointOptions> models)
    {
        ArgumentNullException.ThrowIfNull(models);

        var generated = new List<ModelEndpointOptions>();
        foreach (var model in models)
        {
            if (!string.IsNullOrWhiteSpace(model.Name) || string.IsNullOrWhiteSpace(model.Id))
            {
                continue;
            }

            model.Name = GenerateName(model);
            generated.Add(model);
        }

        if (generated.Count == 0)
        {
            return;
        }

        // 只对自动生成的名称去重；显式名称间的重复留给启动校验报错。
        // 逐个生成者按配置顺序寻找首个可用名：显式模型先占位，生成者之间按先后取 base、base #2…
        var reserved = models
            .Where(m => !generated.Contains(m))
            .Select(m => m.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var model in generated)
        {
            if (!reserved.Contains(model.Name))
            {
                reserved.Add(model.Name);
                continue;
            }

            string baseName = model.Name;
            int suffix = 2;
            while (reserved.Contains($"{baseName} #{suffix}"))
            {
                suffix++;
            }

            model.Name = $"{baseName} #{suffix}";
            reserved.Add(model.Name);
        }
    }

    /// <summary>
    /// 生成路由名 "{供应商}/{Id}"。供应商取显式 Provider，缺省从 BaseUrl 推断，再缺省 "model"。
    /// </summary>
    public static string GenerateName(ModelEndpointOptions model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var provider = !string.IsNullOrWhiteSpace(model.Provider)
            ? model.Provider.Trim()
            : ProviderInference.Infer(model.BaseUrl);
        if (string.IsNullOrWhiteSpace(provider))
        {
            provider = "model";
        }

        return $"{provider}/{model.Id.Trim()}";
    }
}

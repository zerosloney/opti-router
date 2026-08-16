using System.Text.Json;
using OptiRouter.Clients;
using OptiRouter.Configuration;

namespace OptiRouter.Mcp;

/// <summary>
/// MCP / Function Calling 工具参数复杂度分析器。
/// 深度遍历请求中附带的所有 Tool Schema，递归计算参数属性数、嵌套深度与约束复杂度，
/// 输出复杂度评分并给出最适模型分级（ModelTier）建议，辅助路由决策避免小模型因 Schema 过深而幻觉崩盘。
/// </summary>
public sealed class McpToolComplexityAnalyzer
{
    private static readonly McpToolComplexityReport NoneReport = new(
        ToolCount: 0,
        TotalProperties: 0,
        MaxNestingDepth: 0,
        ComplexityScore: 0.0,
        Level: McpComplexityLevel.None,
        RecommendedMinTier: ModelTier.Cheap);

    /// <summary>
    /// 分析一组已注册的 MCP 工具定义并生成复杂度评估报告。
    /// </summary>
    public McpToolComplexityReport Analyze(IEnumerable<McpToolRegistration> tools)
    {
        if (tools == null) return NoneReport;
        var toolList = tools.ToList();
        if (toolList.Count == 0) return NoneReport;

        int totalProperties = 0;
        int maxDepth = 0;
        int totalEnums = 0;
        int totalRequired = 0;

        foreach (var tool in toolList)
        {
            if (tool.InputSchema.HasValue && tool.InputSchema.Value.ValueKind == JsonValueKind.Object)
            {
                var (props, depth, enums, reqs) = InspectSchema(tool.InputSchema.Value, currentDepth: 1);
                totalProperties += props;
                maxDepth = Math.Max(maxDepth, depth);
                totalEnums += enums;
                totalRequired += reqs;
            }
        }

        double score = (toolList.Count * 1.0)
                     + (totalProperties * 0.35)
                     + (maxDepth * 1.5)
                     + (totalEnums * 0.2)
                     + (totalRequired * 0.15);

        score = Math.Round(Math.Clamp(score, 0.0, 10.0), 2);

        McpComplexityLevel level;
        ModelTier recommendedTier;

        if (score <= 4.0 && maxDepth <= 2 && toolList.Count <= 2)
        {
            level = McpComplexityLevel.Simple;
            recommendedTier = ModelTier.Cheap;
        }
        else if (score <= 7.5 && maxDepth <= 3 && toolList.Count <= 5)
        {
            level = McpComplexityLevel.Moderate;
            recommendedTier = ModelTier.Medium;
        }
        else
        {
            level = McpComplexityLevel.High;
            recommendedTier = ModelTier.Strong;
        }

        return new McpToolComplexityReport(
            ToolCount: toolList.Count,
            TotalProperties: totalProperties,
            MaxNestingDepth: maxDepth,
            ComplexityScore: score,
            Level: level,
            RecommendedMinTier: recommendedTier);
    }

    /// <summary>
    /// 分析请求中的工具定义并生成复杂度评估报告。
    /// </summary>
    public McpToolComplexityReport Analyze(ChatRequest request)
    {
        if (request.ExtensionData == null || !request.ExtensionData.TryGetValue("tools", out var toolsEl))
        {
            return NoneReport;
        }

        if (toolsEl.ValueKind != JsonValueKind.Array)
        {
            return NoneReport;
        }

        int toolCount = toolsEl.GetArrayLength();
        if (toolCount == 0)
        {
            return NoneReport;
        }

        int totalProperties = 0;
        int maxDepth = 0;
        int totalEnums = 0;
        int totalRequired = 0;

        foreach (var tool in toolsEl.EnumerateArray())
        {
            if (tool.ValueKind != JsonValueKind.Object) continue;

            JsonElement schema = default;
            // 兼容 OpenAI 格式: { function: { parameters: { ... } } }
            if (tool.TryGetProperty("function", out var funcEl) && funcEl.ValueKind == JsonValueKind.Object)
            {
                if (funcEl.TryGetProperty("parameters", out var paramsEl) && paramsEl.ValueKind == JsonValueKind.Object)
                {
                    schema = paramsEl;
                }
            }
            // 兼容原生 MCP 格式: { inputSchema: { ... } }
            else if (tool.TryGetProperty("inputSchema", out var inputSchemaEl) && inputSchemaEl.ValueKind == JsonValueKind.Object)
            {
                schema = inputSchemaEl;
            }
            // 兼容顶层 parameters
            else if (tool.TryGetProperty("parameters", out var topParamsEl) && topParamsEl.ValueKind == JsonValueKind.Object)
            {
                schema = topParamsEl;
            }

            if (schema.ValueKind == JsonValueKind.Object)
            {
                var (props, depth, enums, reqs) = InspectSchema(schema, currentDepth: 1);
                totalProperties += props;
                maxDepth = Math.Max(maxDepth, depth);
                totalEnums += enums;
                totalRequired += reqs;
            }
        }

        // 复杂度计算公式：综合考量工具数量、参数总数、嵌套层深、枚举与必填项
        double score = (toolCount * 1.0)
                     + (totalProperties * 0.35)
                     + (maxDepth * 1.5)
                     + (totalEnums * 0.2)
                     + (totalRequired * 0.15);

        score = Math.Round(Math.Clamp(score, 0.0, 10.0), 2);

        McpComplexityLevel level;
        ModelTier recommendedTier;

        if (toolCount == 0)
        {
            level = McpComplexityLevel.None;
            recommendedTier = ModelTier.Cheap;
        }
        else if (score <= 4.0 && maxDepth <= 2 && toolCount <= 2)
        {
            level = McpComplexityLevel.Simple;
            recommendedTier = ModelTier.Cheap;
        }
        else if (score <= 7.5 && maxDepth <= 3 && toolCount <= 5)
        {
            level = McpComplexityLevel.Moderate;
            recommendedTier = ModelTier.Medium;
        }
        else
        {
            level = McpComplexityLevel.High;
            recommendedTier = ModelTier.Strong;
        }

        return new McpToolComplexityReport(
            ToolCount: toolCount,
            TotalProperties: totalProperties,
            MaxNestingDepth: maxDepth,
            ComplexityScore: score,
            Level: level,
            RecommendedMinTier: recommendedTier);
    }

    private static (int properties, int maxDepth, int enums, int required) InspectSchema(JsonElement element, int currentDepth)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return (0, currentDepth, 0, 0);
        }

        int propCount = 0;
        int localMaxDepth = currentDepth;
        int enumCount = 0;
        int reqCount = 0;

        if (element.TryGetProperty("required", out var reqEl) && reqEl.ValueKind == JsonValueKind.Array)
        {
            reqCount += reqEl.GetArrayLength();
        }

        if (element.TryGetProperty("enum", out var enumEl) && enumEl.ValueKind == JsonValueKind.Array)
        {
            enumCount += enumEl.GetArrayLength();
        }

        if (element.TryGetProperty("properties", out var propsEl) && propsEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in propsEl.EnumerateObject())
            {
                propCount++;
                if (prop.Value.ValueKind == JsonValueKind.Object)
                {
                    // 检查是否为嵌套对象或数组
                    bool isNestedObject = prop.Value.TryGetProperty("properties", out _);
                    bool isNestedArray = prop.Value.TryGetProperty("items", out _);

                    if (isNestedObject || isNestedArray)
                    {
                        var childResult = InspectSchema(prop.Value, currentDepth + 1);
                        propCount += childResult.properties;
                        localMaxDepth = Math.Max(localMaxDepth, childResult.maxDepth);
                        enumCount += childResult.enums;
                        reqCount += childResult.required;
                    }
                    else
                    {
                        // 扁平基本属性（可能含 enum）
                        if (prop.Value.TryGetProperty("enum", out var pEnum) && pEnum.ValueKind == JsonValueKind.Array)
                        {
                            enumCount += pEnum.GetArrayLength();
                        }
                    }
                }
            }
        }

        // 检查 items (数组类型)
        if (element.TryGetProperty("items", out var itemsEl) && itemsEl.ValueKind == JsonValueKind.Object)
        {
            var childResult = InspectSchema(itemsEl, currentDepth + 1);
            propCount += childResult.properties;
            localMaxDepth = Math.Max(localMaxDepth, childResult.maxDepth);
            enumCount += childResult.enums;
            reqCount += childResult.required;
        }

        return (propCount, localMaxDepth, enumCount, reqCount);
    }
}

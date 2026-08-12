using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Endpoints;

namespace OptiRouter.Routing;

/// <summary>
/// 本地-云端混合投机解码编排器 (Hybrid Local-Cloud Speculative Decoding Orchestrator)：
/// 先由本地/端侧轻量小模型 (Draft Model，如本地 1B/3B Ollama 节点) 极速生成初步草稿（Draft Answer）；
/// 随后将该 Draft 答案作为上下文送入云端强模型 (Verifier Model) 进行并行校验、补全与修补，
/// 以在保持最高智力输出的同时大幅降低首字延迟与云端 Token 开支。
/// </summary>
public sealed class HybridSpeculativeOrchestrator
{
    private readonly IModelClientProvider _clientProvider;

    public HybridSpeculativeOrchestrator(IModelClientProvider clientProvider)
    {
        _clientProvider = clientProvider ?? throw new ArgumentNullException(nameof(clientProvider));
    }

    /// <summary>
    /// 执行本地-云端混合投机生成。
    /// </summary>
    public async Task<RawChatResponse> ExecuteSpeculativeAsync(
        ChatRequest originalRequest,
        ModelEndpointOptions draftModel,
        ModelEndpointOptions verifierModel,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(originalRequest);
        ArgumentNullException.ThrowIfNull(draftModel);
        ArgumentNullException.ThrowIfNull(verifierModel);

        // 1. 本地/私有 Draft 小模型快速生成初步草稿
        var draftClient = _clientProvider.GetClient(draftModel);
        string draftText = string.Empty;
        try
        {
            var draftResp = await draftClient.CompleteRawAsync(originalRequest with { Stream = false }, ct).ConfigureAwait(false);
            draftText = ResponseConfidenceChecker.ExtractAssistantText(draftResp);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // 如果 Draft 失败（非取消），直接回退为 Verifier 单独回答
            var verifierDirectClient = _clientProvider.GetClient(verifierModel);
            return await verifierDirectClient.CompleteRawAsync(originalRequest, ct).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(draftText))
        {
            var verifierDirectClient = _clientProvider.GetClient(verifierModel);
            return await verifierDirectClient.CompleteRawAsync(originalRequest, ct).ConfigureAwait(false);
        }

        // 2. 将 Draft 答案嵌入 Verifier 校验 Prompt
        var verifierMessages = new List<ChatMessage>();
        if (originalRequest.Messages is not null)
            verifierMessages.AddRange(originalRequest.Messages);

        string instruction = $"【端侧 Draft 模型提供的初步答案】：\n{draftText}\n\n请复核上面 Draft 答案的正确性，补全盲点、修补逻辑与事实错误，并写出最终准确答案。不要复述校验过程，直接作答。";
        verifierMessages.Add(ChatMessage.FromText("user", instruction));

        var verifierRequest = originalRequest with { Messages = verifierMessages };
        var verifierClient = _clientProvider.GetClient(verifierModel);

        return await verifierClient.CompleteRawAsync(verifierRequest, ct).ConfigureAwait(false);
    }
}

using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace OptiRouter.Compliance;

/// <summary>
/// OpenAI Moderation API 兼容的内容审核客户端。
/// 调用 <c>POST {endpoint}</c>（默认 OpenAI 格式 <c>/v1/moderations</c>），
/// 解析 <c>results[0].category_scores</c> 中超过阈值的最高类别。
/// 服务不可用或超时默认 fail-open（放行并记录），避免审核故障阻断业务。
/// </summary>
public sealed class OpenAIModerationClient : IContentModerator
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenAIModerationClient> _logger;
    private readonly string _endpoint;
    private readonly string? _apiKey;
    private readonly double _threshold;

    public string Name => "openai-moderation";

    public OpenAIModerationClient(
        HttpClient httpClient,
        string endpoint,
        string? apiKey = null,
        double threshold = 0.8,
        ILogger<OpenAIModerationClient>? logger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _apiKey = apiKey;
        _threshold = Math.Clamp(threshold, 0.0, 1.0);
        _logger = logger ?? NullLogger<OpenAIModerationClient>.Instance;
    }

    /// <inheritdoc />
    public async Task<ModerationResult> ModerateTextAsync(string text, ModerationDirection direction, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new ModerationResult(false, null, 0.0, null);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = JsonContent.Create(new { input = text })
            };
            if (!string.IsNullOrWhiteSpace(_apiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            }

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseResponse(body);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // fail-open：审核服务不可用时放行，记录原因供审计。
            _logger.LogWarning(ex, "Moderation service unavailable ({Endpoint}); failing open", _endpoint);
            return new ModerationResult(false, null, 0.0, $"moderation-unavailable: {ex.Message}");
        }
    }

    private ModerationResult ParseResponse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
            {
                return new ModerationResult(false, null, 0.0, "moderation-empty-result");
            }

            var result = results[0];
            if (!result.TryGetProperty("category_scores", out var scores) || scores.ValueKind != JsonValueKind.Object)
            {
                return new ModerationResult(false, null, 0.0, null);
            }

            string? worstCategory = null;
            double worstScore = 0.0;
            foreach (var prop in scores.EnumerateObject())
            {
                double score = prop.Value.GetDouble();
                if (score > worstScore)
                {
                    worstScore = score;
                    worstCategory = prop.Name;
                }
            }

            if (worstCategory is not null && worstScore >= _threshold)
            {
                return new ModerationResult(
                    true,
                    worstCategory,
                    worstScore,
                    $"Moderation category '{worstCategory}' score {worstScore:F3} exceeds threshold {_threshold:F3}");
            }

            return new ModerationResult(false, worstCategory, worstScore, null);
        }
        catch (JsonException)
        {
            return new ModerationResult(false, null, 0.0, "moderation-invalid-response");
        }
    }
}

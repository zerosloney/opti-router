using OptiRouter.Clients;
using OptiRouter.Metrics;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Routing;

public class P2StageTests
{
    [Fact]
    public void DistributedTraceContext_ParsesAndFormatsTraceParent()
    {
        string traceparent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
        var (traceId, parentSpanId) = DistributedTraceContext.ParseTraceParent(traceparent);

        Assert.Equal("4bf92f3577b34da6a3ce929d0e0e4736", traceId);
        Assert.Equal("00f067aa0ba902b7", parentSpanId);

        string rebuilt = DistributedTraceContext.BuildTraceParent(traceId, "1122334455667788", true);
        Assert.Equal("00-4bf92f3577b34da6a3ce929d0e0e4736-1122334455667788-01", rebuilt);
    }

    [Fact]
    public void DagCostAttributor_CalculatesTraceTreeCostAndTokens()
    {
        string traceId = "test-trace-1234567890123456789012";
        var records = new List<RequestAuditRecord>
        {
            new RequestAuditRecord(
                DateTime.UtcNow, "req-1", "model-a", 100, 100, 50, 0.002m, 150, "session-1", "reason", true, null, false,
                FusionRole: "panel", TraceId: traceId, SpanId: "span-1"),
            new RequestAuditRecord(
                DateTime.UtcNow, "req-2", "model-b", 100, 100, 60, 0.003m, 200, "session-1", "reason", true, null, false,
                FusionRole: "panel", TraceId: traceId, SpanId: "span-2"),
            new RequestAuditRecord(
                DateTime.UtcNow, "req-3", "analyst-model", 200, 200, 30, 0.005m, 100, "session-1", "reason", true, null, false,
                FusionRole: "analyst", TraceId: traceId, SpanId: "span-3"),
            new RequestAuditRecord(
                DateTime.UtcNow, "req-4", "outer-model", 300, 300, 150, 0.010m, 350, "session-1", "reason", true, null, false,
                FusionRole: "outer", TraceId: traceId, SpanId: "span-4")
        };

        var tree = DagCostAttributor.BuildTraceTree(traceId, records);

        Assert.Equal(traceId, tree.TraceId);
        Assert.Equal(4, tree.TotalSubSpans);
        Assert.Equal(0.020m, tree.TotalCost);
        Assert.Equal(700, tree.TotalPromptTokens);
        Assert.Equal(290, tree.TotalCompletionTokens);
        Assert.Equal(350, tree.MaxLatencyMs);

        var byRole = tree.CostByRole;
        Assert.Equal(0.005m, byRole["panel"]);
        Assert.Equal(0.005m, byRole["analyst"]);
        Assert.Equal(0.010m, byRole["outer"]);
    }

    [Fact]
    public void PersonaDriftGuard_AppliesPersonaAnchorToMessages()
    {
        var request = new ChatRequest
        {
            Messages = new List<ChatMessage>
            {
                ChatMessage.FromText("user", "What is the capital of France?")
            }
        };

        var guarded = PersonaDriftGuard.ApplyPersonaAnchor(request);

        Assert.NotNull(guarded.Messages);
        Assert.Equal(2, guarded.Messages.Count);
        Assert.Equal("system", guarded.Messages[0].Role);
        Assert.Contains("【人设与风格一致性指示】", guarded.Messages[0].GetText());
        Assert.Equal("user", guarded.Messages[1].Role);
    }
}

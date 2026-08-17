# OptiRouter

A multi-model intelligent routing HTTP proxy built with .NET 8. Features an OpenAI-compatible API, automatic model selection, token & cost optimization, data compliance barriers, distributed W3C DAG tracing, and hybrid local-cloud speculative decoding orchestration.

## Architecture

```
┌──────────┐   POST /v1/chat/completions   ┌──────────────┐
│  Client  │ ────────────────────────────▶ │ OptiRouter   │
│ (OpenAI  │                               │   (ASP.NET   │
│  SDK /   │ ◀─────────────────────────── │    Core)     │
│  curl)   │   Non-streaming JSON / SSE    └──────┬───────┘
└──────────┘                                      │
                                          RouterEngine.Decide
                                                  │
                   ┌──────────────────────────────┴──────────────┐
                   ▼                                               ▼
            ┌─────────────┐                               ┌───────────────┐
            │ RuleClassif │ → Tier(Strong/Medium/Cheap)    │ TokenEstimator│ → Long Input Filter
            └─────────────┘                               └───────────────┘
                   ▼                                               ▼
            ┌─────────────┐                               ┌───────────────┐
            │ DataSovereig│ → Sovereignty & Local Isolation│ PiiAnonymizer │ → PII Anonymize/Restore
            └─────────────┘                               └───────────────┘
                   ▼                                               ▼
            ┌─────────────┐                               ┌───────────────┐
            │ BudgetGuard │ → Degrade/Reject on Exhaust   │ FailoverPolicy│ → Exclude Unhealthy
            └─────────────┘                               └───────────────┘
                                         Candidate Chain [A, B, C]
                                                  │
                                   ProxyOrchestrator / Race / Fusion
                                                  │
             ┌────────────────────────────────────┼────────────────────────────────────┐
             ▼                                    ▼                                    ▼
      ┌────────────┐                       ┌────────────┐                       ┌────────────┐
      │  Model A   │                       │  Model B   │                       │  Model C   │  (OpenAI Compatible)
      └────────────┘                       └────────────┘                       └────────────┘
```

## Core Features

- 🚀 **Progressive Speculative Streaming**: Enables streaming for Fusion Router with design goal of significantly lower TTFT than full fusion. Anchor models stream immediately while background Panel models and Analyst compute incremental patch chunks.
- ⚡ **Prompt Cache Distillation & APC Alignment**: Panel text distillation strips boilerplate and greetings (actual savings depend on conversation structure via history folding, deduplication, and filler removal). Static top-loaded prefixes (`[SYSTEM_PREFIX_INSTRUCTION]`) maximize Automatic Prefix Caching (APC) hit rates across providers.
- 🛡️ **P1 Data Compliance & JSON AST Auto-Repair**:
  - **PII Anonymization & Deanonymization**: Automatically detects and replaces Phone numbers, Email addresses, ID cards, Credit cards, and IP addresses with named placeholders and restores them on response. (Disabled by default, requires explicit `EnablePiiAnonymization=true`)
  - **Data Sovereignty Barrier**: Enforces routing strictly to local or on-premise endpoints (`IsLocalOrPrivate`) when `EnableDataSovereignty` is enabled. (Disabled by default, requires explicit `EnableDataSovereignty=true`)
  - **JSON AST Repairer**: Strips Markdown code fences, cleans control characters, fixes trailing commas, and auto-closes missing brackets from truncated responses.
- 🔍 **P2 Observability & Multi-Turn Persona Defense**:
  - **W3C Distributed Tracing**: Full `traceparent` parsing and ActivitySource mapping with DAG cost attribution trees across Panel, Analyst, and Outer models.
  - **Persona Alignment (`PersonaDriftGuard`)**: Injects static persona anchor instructions combined with Session Affinity locking to prevent persona drift across multi-turn agent conversations.
- 🧪 **P3 Prompt Versioning & Speculative Decoding**:
  - **Prompt Template Manager (`PromptTemplateManager`)**: Version control and variable rendering for Analyst/Outer prompts. (Planned, not yet implemented)
  - **Golden Dataset Regression Suite (`OfflineEvalRunner`)**: Automated evaluation suite computing Jaccard token similarity, accuracy rates, latency, and token consumption reports.
  - **Hybrid Speculative Orchestration (`HybridSpeculativeOrchestrator`)**: Local 1B/3B draft models generate preliminary outlines rapidly before passing context to cloud verifier models.
- 🏎️ **Zero-Blocking High Performance Architecture**:
  - **ConcurrentQueue Async Batch Persister**: 1-microsecond enqueue time for audit logs, processed asynchronously in SQLite batch transactions without blocking the data plane.
  - **Non-blocking Sweeper**: `Monitor.TryEnter` prevents concurrency sweeper locks from delaying HTTP request execution.
  - **MemoryCache SizeLimit Protection**: Hard-capped cache size prevents memory bloat under malicious Session ID flood attacks.

## Quick Start

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

### Build

```bash
dotnet build OptiRouter.sln -c Release
```

### Configuration

1. Copy `appsettings.example.json` to `appsettings.json`.
2. Configure your API keys or pass them via environment variables:

```bash
# Windows PowerShell
$env:OptiRouter__ProxyApiKey = "your-proxy-api-key"
$env:OptiRouter__Models__0__ApiKey = "sk-..."

# Linux / macOS
export OptiRouter__ProxyApiKey="your-proxy-api-key"
export OptiRouter__Models__0__ApiKey="sk-..."
```

If `ProxyApiKey` is empty, all `/v1/*` requests will be rejected.

### Run

```bash
dotnet run --project src/OptiRouter
```

The application listens on `http://localhost:5000` by default.

### Health Check

```bash
curl http://localhost:5000/health
```

The `/health` endpoint requires no API Key and bypasses rate limiting.

## Configuration Reference

Key fields under `OptiRouter` in `appsettings.json`:

### Models[] (Endpoint List)

| Field | Description | Example |
|------|------|------|
| `Name` | Model identifier | `gpt-4o` |
| `BaseUrl` | Upstream API Base URL | `https://api.openai.com/v1` |
| `ApiKey` | Authorization Key | `sk-...` |
| `Tier` | Capability tier: `Strong` / `Medium` / `Cheap` | `Strong` |
| `MaxContextTokens` | Maximum context length | `128000` |
| `InputPricePerMillion` | Input token price ($/1M tokens) | `2.5` |
| `CachedInputPricePerMillion` | Cached input token price ($/1M tokens) | `1.25` |
| `OutputPricePerMillion` | Output token price ($/1M tokens) | `10.0` |
| `IsLocalOrPrivate` | Identifies endpoint as local or on-premise (for Data Sovereignty) | `false` |

### Routing Options

| Field | Description | Default |
|------|------|------|
| `EnableRuleClassifier` | Infer tier based on request characteristics | `true` |
| `EnablePiiAnonymization` | Enable PII anonymization and restoration. **Disabled by default, recommended for privacy-sensitive deployments** | `false` |
| `EnableDataSovereignty` | Restrict routing to local/private endpoints. **Disabled by default, recommended for compliance deployments** | `false` |
| `EnableFusionRouter` | Enable Mixture-of-Agents fusion routing (panel → analyst → outer). Cost: N+2 model calls (N=panel size). **Disabled by default, requires explicit enable and cost consideration** | `false` |
| `EnableJsonAstAutoRepair` | Automatically repair broken JSON responses | `true` |
| `EnableDistributedTracing` | Enable W3C distributed tracing (`traceparent`) | `true` |
| `EnablePersonaDriftProtection` | Enable multi-turn persona drift protection | `true` |
| `EnableFusionRouter` | Enable Mixture-of-Agents fusion routing | `false` |

### Recommended Configuration Presets

Presets are starting points, not final destinations. Paste these JSON snippets into the `"OptiRouter"` section of `appsettings.json`, then tune individual switches as needed. Explicitly configured keys override defaults.

```json
{
  "OptiRouter": {
    "Routing": { ... },
    "Budget": { ... }
  }
}
```

#### 1. cost-first（Cost Priority – Batch/Offline/High-Traffic）

```json
{
"Routing": {
  "EnableThompsonSampling": true,
  "EnableLatencyAware": true,
  "ExplorationEpsilon": 0.05,
  "EnableResponseCache": true,
  "DefaultTier": "Cheap"
},
"Budget": {
  "EnforceOnExhausted": "Degrade"
}
}
```

**Use Case**: Batch processing, offline tasks, high-volume simple queries.

**Behavior**: Thompson sampling + latency-aware routing auto-converges to fast, cheap models for high-frequency requests. 5% ε-exploration ensures tail models get samples. Response cache deduplicates repeated queries (zero-cost for idempotent hits). Budget exhaustion degrades to cheaper models rather than rejecting.

#### 2. balanced（Balanced – General Purpose/Agent Backend, Recommended Starting Point）

```json
{
"Routing": {
  "EnableThompsonSampling": true,
  "EnableCascadeUpgrade": true,
  "CascadeUpgradeSampleRate": 0.1,
  "EnableResponseCache": true,
  "DefaultTier": "Medium"
}
}
```

**Use Case**: General chatbots, agent backends, multi-turn conversations (production-recommended baseline).

**Behavior**: Thompson sampling adapts model selection based on historical latency and success rates. 10% sampled Cheap→Strong cascade self-verification catches low-confidence cheap answers and upgrades to strong models. Configure `"CascadeUpgradeVerifierModel"` to a strong model name for third-party verification (eliminates self-rating bias). Response cache reduces redundant calls.

#### 3. quality-first（Quality Priority – High-Risk Low-Traffic）

```json
{
"Routing": {
  "DefaultTier": "Strong",
  "EnableFusionRouter": true,
  "EnableByzantineConsensus": true,
  "EnableCascadeUpgrade": true,
  "CascadeUpgradeSampleRate": 0.3
},
"Budget": {
  "EnforceOnExhausted": "Reject"
}
}
```

**Use Case**: High-risk decisions, financial/medical diagnosis, complex reasoning (low traffic tolerates high cost).

**Behavior**: Defaults to Strong tier. Fusion routing runs parallel panel models → Analyst structured consensus/conflict analysis → Outer final answer (~N+2 calls, N=panel size). Byzantine consensus shortcuts to majority when panels agree, Analyst arbitrates disagreements. **Note**: `EnableByzantineConsensus` only works in non-streaming fusion paths when `EnableFusionRouter` is enabled. 30% cascade sampling strengthens quality guard. Budget exhaustion rejects rather than degrades (quality is non-negotiable).

## curl Examples

Non-streaming:

```bash
curl -X POST http://localhost:5000/v1/chat/completions \
  -H "Authorization: Bearer your-proxy-api-key" \
  -H "Content-Type: application/json" \
  -d '{
    "model": "auto",
    "messages": [{"role": "user", "content": "Explain polymorphism"}],
    "stream": false
  }'
```

Streaming (Supports Progressive Speculative Streaming):

```bash
curl -X POST http://localhost:5000/v1/chat/completions \
  -H "Authorization: Bearer your-proxy-api-key" \
  -H "Content-Type: application/json" \
  -d '{
    "model": "auto",
    "messages": [{"role": "user", "content": "Write a quicksort in C#"}],
    "stream": true
  }'
```

## Testing

Run the full suite of 979+ unit and integration tests (grows with iterations):

```bash
dotnet test OptiRouter.sln -c Release
```

## License

[MIT License](LICENSE)

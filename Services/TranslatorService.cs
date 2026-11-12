using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SmartUnrealLocalization.Localization;
using SmartUnrealLocalization.Models;

namespace SmartUnrealLocalization.Services
{
    public enum AiProvider
    {
        OpenAiCompatible, // any endpoint speaking the /chat/completions schema (BaseUrl is set)
        OpenAi,
        Anthropic,
        Gemini
    }

    public static class TranslatorService
    {
        /// <summary>
        /// Resolves which provider to call from config. Explicit BaseUrl always wins
        /// (we treat any custom endpoint as OpenAI-compatible). Otherwise the model
        /// name is matched against known prefixes; anything unrecognised falls back
        /// to Gemini, since Google models historically have no shared prefix.
        /// </summary>
        public static AiProvider ResolveProvider(AppConfig cfg)
        {
            if (!string.IsNullOrWhiteSpace(cfg.BaseUrl))
                return AiProvider.OpenAiCompatible;

            string m = cfg.ModelName.ToLowerInvariant();
            if (m.StartsWith("gpt-") || m.StartsWith("o1-") || m.StartsWith("o3-") || m.StartsWith("chatgpt"))
                return AiProvider.OpenAi;
            if (m.StartsWith("claude-"))
                return AiProvider.Anthropic;
            if (m.StartsWith("gemini-") || m.StartsWith("models/gemini"))
                return AiProvider.Gemini;
            return AiProvider.Gemini;
        }


        /// <summary>
        /// Output ceiling sent to every provider. It is a CAP, not a reservation —
        /// nothing is billed or spent unless the model actually generates that far.
        ///
        /// <para>16384 rather than something larger on purpose: Anthropic refuses
        /// NON-STREAMING requests whose <c>max_tokens</c> allows a run longer than
        /// ten minutes (roughly 21k), and we do not stream. Every current model
        /// (Gemini Flash 64k+, Claude 64–128k, GPT-5.x 128k) accepts this value, so
        /// it is the largest limit that is safe everywhere.</para>
        ///
        /// <para>If a batch still hits it, the answer is a smaller batch, not a
        /// bigger ceiling — the callers report that explicitly.</para>
        /// </summary>
        public const int MaxOutputTokens = 16384;

        /// <summary>
        /// Reasoning-era OpenAI models: they take <c>max_completion_tokens</c>
        /// instead of <c>max_tokens</c> and reject a custom temperature.
        /// </summary>
        internal static bool IsOpenAiReasoningModel(string model)
        {
            string m = (model ?? "").ToLowerInvariant();
            return m.StartsWith("gpt-5") || m.StartsWith("o1") || m.StartsWith("o3") || m.StartsWith("o4");
        }

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };
        private static SemaphoreSlim? _rateLimiter;
        private static int _currentMaxConcurrent = -1;
        private static readonly object _limiterLock = new();

        // Optional min-gap throttle between consecutive requests (Settings → AI Speed).
        // Its own gate (not the concurrency limiter) so parallel workers still get
        // spaced out: each sender serializes through here just long enough to wait
        // out the remaining gap and stamp the send time.
        private static readonly SemaphoreSlim _delayGate = new(1, 1);
        private static DateTime _lastSendUtc = DateTime.MinValue;

        public static async Task<string> TranslateAsync(string prompt, string systemPrompt = "", CancellationToken ct = default)
        {
            var cfg = AppConfig.Instance;

            // Capture a stable reference to the limiter under the lock — without this two
            // concurrent first-callers can both pass the null check, dispose each other's
            // semaphore, and produce a NullReferenceException on Release.
            int maxConcurrent = cfg.ParallelTranslationEnabled ? Math.Max(1, cfg.MaxConcurrentRequests) : 1;
            SemaphoreSlim limiter;
            lock (_limiterLock)
            {
                if (_rateLimiter == null || _currentMaxConcurrent != maxConcurrent)
                {
                    _rateLimiter?.Dispose();
                    _rateLimiter = new SemaphoreSlim(maxConcurrent, maxConcurrent);
                    _currentMaxConcurrent = maxConcurrent;
                }
                limiter = _rateLimiter;
            }

            await limiter.WaitAsync(ct);
            try
            {
                if (string.IsNullOrWhiteSpace(cfg.ApiKey))
                    throw new InvalidOperationException(Loc.T("qc.ai.err.noKey"));

                if (cfg.MinRequestDelayEnabled && cfg.MinRequestDelayMs > 0)
                {
                    await _delayGate.WaitAsync(ct);
                    try
                    {
                        var wait = _lastSendUtc.AddMilliseconds(cfg.MinRequestDelayMs) - DateTime.UtcNow;
                        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                        _lastSendUtc = DateTime.UtcNow;
                    }
                    finally { _delayGate.Release(); }
                }

                return ResolveProvider(cfg) switch
                {
                    AiProvider.OpenAiCompatible => await CallOpenAiCompatible(prompt, systemPrompt, cfg, ct),
                    AiProvider.OpenAi => await CallOpenAi(prompt, systemPrompt, cfg, ct),
                    AiProvider.Anthropic => await CallAnthropic(prompt, systemPrompt, cfg, ct),
                    AiProvider.Gemini => await CallGemini(prompt, systemPrompt, cfg, ct),
                    _ => await CallGemini(prompt, systemPrompt, cfg, ct),
                };
            }
            finally
            {
                // Release the limiter we acquired, not whichever one is currently live.
                // (Important if settings changed mid-call and _rateLimiter was swapped.)
                try { limiter.Release(); } catch (ObjectDisposedException) { }
            }
        }

        private static async Task<string> CallOpenAiCompatible(string prompt, string systemPrompt, AppConfig cfg, CancellationToken ct)
        {
            var url = cfg.BaseUrl.TrimEnd('/') + "/chat/completions";

            var messages = new System.Collections.Generic.List<object>();
            if (!string.IsNullOrWhiteSpace(systemPrompt))
                messages.Add(new { role = "system", content = systemPrompt });
            messages.Add(new { role = "user", content = prompt });

            // Low temperature for terminology stability; custom endpoints (Grok,
            // local servers, proxies) all speak this schema.
            var body = new { model = cfg.ModelName, messages = messages, temperature = 0.2, max_tokens = MaxOutputTokens };
            var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent(body) };
            req.Headers.Add("Authorization", $"Bearer {cfg.ApiKey}");

            var resp = await Http.SendAsync(req, ct);
            await EnsureSuccess(resp, ct);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var choice = doc.RootElement.GetProperty("choices")[0];

            if (choice.TryGetProperty("finish_reason", out var fr) && fr.GetString() == "length")
                throw new Exception(TruncatedMessage("AI"));

            string content = choice.GetProperty("message").GetProperty("content").GetString() ?? "";
            if (string.IsNullOrWhiteSpace(content))
                throw new Exception(string.Format(Loc.T("ai.err.emptyContent"), "AI", "-"));

            return content;
        }

        private static async Task<string> CallGemini(string prompt, string systemPrompt, AppConfig cfg, CancellationToken ct)
        {
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{cfg.ModelName}:generateContent?key={cfg.ApiKey}";

            object body;
            // Gemini 3.x is tuned for the default temperature — Google advises against
            // lowering it, unlike the OpenAI-style models.
            var generationConfig = new { maxOutputTokens = MaxOutputTokens, temperature = 1.0 };

            if (!string.IsNullOrWhiteSpace(systemPrompt))
            {
                body = new
                {
                    system_instruction = new { parts = new[] { new { text = systemPrompt } } },
                    contents = new[] { new { parts = new[] { new { text = prompt } } } },
                    generationConfig = generationConfig
                };
            }
            else
            {
                body = new
                {
                    contents = new[] { new { parts = new[] { new { text = prompt } } } },
                    generationConfig = generationConfig
                };
            }

            var resp = await Http.PostAsync(url, JsonContent(body), ct);
            await EnsureSuccess(resp, ct);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var candidates = doc.RootElement.GetProperty("candidates");
            if (candidates.GetArrayLength() == 0) throw new Exception(string.Format(Loc.T("ai.err.emptyContent"), "Gemini", "no candidates"));

            var candidate = candidates[0];
            string finish = candidate.TryGetProperty("finishReason", out var fr) ? fr.GetString() ?? "" : "";
            if (finish == "MAX_TOKENS") throw new Exception(TruncatedMessage("Gemini"));
            if (finish is "SAFETY" or "PROHIBITED_CONTENT" or "BLOCKLIST")
                throw new Exception(string.Format(Loc.T("ai.err.blocked"), "Gemini", finish));

            // Collect ALL text parts. Reading parts[0] blindly threw an unhelpable
            // KeyNotFound/IndexOutOfRange when a thinking model put reasoning in the
            // first part, or returned a candidate with no parts at all.
            var sb = new StringBuilder();
            if (candidate.TryGetProperty("content", out var contentEl)
                && contentEl.TryGetProperty("parts", out var parts)
                && parts.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in parts.EnumerateArray())
                {
                    if (part.TryGetProperty("thought", out var th) && th.ValueKind == JsonValueKind.True)
                        continue; // internal reasoning, not the answer
                    if (part.TryGetProperty("text", out var t) && t.GetString() is { } s) sb.Append(s);
                }
            }

            string content = sb.ToString();
            if (string.IsNullOrWhiteSpace(content))
                throw new Exception(string.Format(Loc.T("ai.err.emptyContent"), "Gemini", finish.Length == 0 ? "-" : finish));

            return content;
        }

        private static async Task<string> CallOpenAi(string prompt, string systemPrompt, AppConfig cfg, CancellationToken ct)
        {
            var url = "https://api.openai.com/v1/chat/completions";

            var messages = new System.Collections.Generic.List<object>();
            if (!string.IsNullOrWhiteSpace(systemPrompt))
                messages.Add(new { role = "system", content = systemPrompt });
            messages.Add(new { role = "user", content = prompt });

            // GPT-5.x / o-series take max_completion_tokens and REJECT a custom
            // temperature; the 4.x family still uses max_tokens and benefits from a
            // low one. Sending the wrong pair fails the whole request with a 400.
            object body = IsOpenAiReasoningModel(cfg.ModelName)
                ? new { model = cfg.ModelName, messages, max_completion_tokens = MaxOutputTokens }
                : new { model = cfg.ModelName, messages, temperature = 0.2, max_tokens = MaxOutputTokens };

            var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent(body) };
            req.Headers.Add("Authorization", $"Bearer {cfg.ApiKey}");
            var resp = await Http.SendAsync(req, ct);
            await EnsureSuccess(resp, ct);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var choice = doc.RootElement.GetProperty("choices")[0];

            if (choice.TryGetProperty("finish_reason", out var fr) && fr.GetString() == "length")
                throw new Exception(TruncatedMessage("OpenAI"));

            string content = choice.GetProperty("message").GetProperty("content").GetString() ?? "";
            if (string.IsNullOrWhiteSpace(content))
                throw new Exception(string.Format(Loc.T("ai.err.emptyContent"), "OpenAI", "-"));

            return content;
        }

        private static async Task<string> CallAnthropic(string prompt, string systemPrompt, AppConfig cfg, CancellationToken ct)
        {
            var url = "https://api.anthropic.com/v1/messages";

            var systemBlocks = new System.Collections.Generic.List<object>();
            if (!string.IsNullOrWhiteSpace(systemPrompt))
            {
                if (cfg.PromptCachingEnabled && cfg.ModelName.ToLowerInvariant().Contains("claude"))
                {
                    systemBlocks.Add(new { type = "text", text = systemPrompt, cache_control = new { type = "ephemeral" } });
                }
                else
                {
                    systemBlocks.Add(new { type = "text", text = systemPrompt });
                }
            }

            // No temperature at all: current Claude models reject a custom one with
            // a 400. max_tokens stays at the shared ceiling — see MaxOutputTokens
            // for why a bigger value would break these non-streaming requests.
            var body = new
            {
                model = cfg.ModelName,
                max_tokens = MaxOutputTokens,
                system = systemBlocks.Count > 0 ? (object)systemBlocks : null,
                messages = new[] { new { role = "user", content = prompt } }
            };

            var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent(body) };
            req.Headers.Add("x-api-key", cfg.ApiKey);
            req.Headers.Add("anthropic-version", "2023-06-01");
            var resp = await Http.SendAsync(req, ct);
            await EnsureSuccess(resp, ct);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;

            if (root.TryGetProperty("stop_reason", out var sr) && sr.GetString() == "max_tokens")
                throw new Exception(TruncatedMessage("Claude"));

            string content = root.GetProperty("content")[0].GetProperty("text").GetString() ?? "";
            if (string.IsNullOrWhiteSpace(content))
                throw new Exception(string.Format(Loc.T("ai.err.emptyContent"), "Anthropic", "-"));

            return content;
        }

        /// <summary>
        /// One truthful message for every provider's "ran out of room" case. The
        /// old wording pointed at a "MaxTokens" setting that does not exist, which
        /// left the real remedy — fewer rows per request — undiscoverable.
        /// </summary>
        private static string TruncatedMessage(string who) =>
            string.Format(Loc.T("ai.err.truncated"), who, MaxOutputTokens);

        private static StringContent JsonContent(object obj) =>
            new(JsonSerializer.Serialize(obj, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }), Encoding.UTF8, "application/json");

        internal static async Task EnsureSuccess(HttpResponseMessage resp, CancellationToken ct)
        {
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                // A proxy or gateway answers with a whole HTML page; the log line and
                // the status bar need the gist, not 40 KB of markup.
                err = err.Trim();
                if (err.Length > 400) err = err[..400] + "…";
                throw new Exception(string.Format(Loc.T("ai.err.http"), (int)resp.StatusCode + " " + resp.StatusCode, err));
            }
        }
    }
}

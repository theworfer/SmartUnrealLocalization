using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services;
using Xunit;

namespace SmartUnrealLocalization.Tests;

/// <summary>
/// Provider routing and request shaping. No network: these pin the decisions
/// that silently break a whole batch when they are wrong (the token ceiling, and
/// which parameter names each model family accepts).
/// </summary>
public class TranslatorServiceTests
{
    [Theory]
    [InlineData("gpt-4o", AiProvider.OpenAi)]
    [InlineData("gpt-5.4", AiProvider.OpenAi)]
    [InlineData("o3-mini", AiProvider.OpenAi)]
    [InlineData("claude-sonnet-5", AiProvider.Anthropic)]
    [InlineData("gemini-3.5-flash", AiProvider.Gemini)]
    [InlineData("models/gemini-3.1-flash-lite", AiProvider.Gemini)]
    [InlineData("something-unknown", AiProvider.Gemini)]     // documented fallback
    public void Provider_is_resolved_from_the_model_name(string model, AiProvider expected)
    {
        var cfg = new AppConfig { ModelName = model, BaseUrl = "" };
        Assert.Equal(expected, TranslatorService.ResolveProvider(cfg));
    }

    [Fact]
    public void An_explicit_base_url_always_wins()
    {
        // Grok, local servers and proxies all arrive this way.
        var cfg = new AppConfig { ModelName = "claude-sonnet-5", BaseUrl = "https://api.x.ai/v1" };
        Assert.Equal(AiProvider.OpenAiCompatible, TranslatorService.ResolveProvider(cfg));
    }

    [Theory]
    [InlineData("gpt-5.4")]
    [InlineData("gpt-5.6-mini")]
    [InlineData("o1-preview")]
    [InlineData("o3-mini")]
    [InlineData("o4-mini")]
    public void Reasoning_models_are_recognised(string model)
        => Assert.True(TranslatorService.IsOpenAiReasoningModel(model));

    [Theory]
    [InlineData("gpt-4o")]
    [InlineData("gpt-4.1")]
    [InlineData("gpt-3.5-turbo")]
    [InlineData("")]
    public void Classic_models_are_not_treated_as_reasoning_models(string model)
        => Assert.False(TranslatorService.IsOpenAiReasoningModel(model));

    [Fact]
    public void The_output_ceiling_stays_safe_for_non_streaming_requests()
    {
        // Anthropic refuses non-streaming requests whose max_tokens allows a run
        // longer than ten minutes (~21k). We do not stream, so the ceiling must
        // stay below that — raising it would break Claude entirely.
        Assert.Equal(16384, TranslatorService.MaxOutputTokens);
        Assert.True(TranslatorService.MaxOutputTokens < 20000);
    }
}

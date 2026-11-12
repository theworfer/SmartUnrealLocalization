using Newtonsoft.Json.Linq;
using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services.Mcp;
using Xunit;

namespace SmartUnrealLocalization.Tests;

/// <summary>
/// The MCP surface an AI client sees. The registry and the app map are the
/// model's ONLY manual, so a stale or wrong entry does not cause a crash — it
/// causes the assistant to confidently tell the user something untrue.
/// </summary>
public class McpSettingsTests
{
    [Fact]
    public void Every_entry_is_fully_described()
    {
        // Name, type, UI path and description are what the model reads before
        // touching a setting; a blank one makes it guess.
        Assert.All(McpSettings.All, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Name));
            Assert.False(string.IsNullOrWhiteSpace(e.Type));
            Assert.False(string.IsNullOrWhiteSpace(e.UiPath));
            Assert.False(string.IsNullOrWhiteSpace(e.Description));
            Assert.NotNull(e.Get);
        });
    }

    [Fact]
    public void Setting_names_are_unique_and_lower_snake_case()
    {
        var names = McpSettings.All.Select(e => e.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(names, n => Assert.Matches("^[a-z][a-z0-9_]*$", n));
    }

    [Fact]
    public void Lookup_is_case_insensitive_and_misses_cleanly()
    {
        Assert.NotNull(McpSettings.Find("model_name"));
        Assert.NotNull(McpSettings.Find("MODEL_NAME"));
        Assert.Null(McpSettings.Find("no_such_setting"));
    }

    [Fact]
    public void Every_declared_type_is_one_the_protocol_knows()
    {
        Assert.All(McpSettings.All, e =>
            Assert.Contains(e.Type, new[] { "bool", "int", "number", "string", "enum", "string[]" }));
    }

    [Fact]
    public void Every_entry_can_read_its_value_from_a_config()
    {
        // A throwing getter would break get_settings for the whole registry.
        var cfg = new AppConfig();
        Assert.All(McpSettings.All, e => Assert.NotNull(e.Get(cfg)));
    }

    [Fact]
    public void The_api_key_is_never_readable_or_writable_through_mcp()
    {
        // Safety invariant: the model may explain WHERE the field is — that is
        // why an `api_key` entry exists at all — but must never see the secret or
        // be able to set it. Stored DPAPI-encrypted for the same reason.
        var cfg = new AppConfig { ApiKey = "sk-super-secret-value" };

        Assert.All(McpSettings.All, e =>
        {
            Assert.DoesNotContain("sk-super-secret-value", e.Get(cfg).ToString());
            if (e.Name.Contains("api_key", StringComparison.OrdinalIgnoreCase))
                Assert.Null(e.Set);
        });
    }

    [Fact]
    public void The_api_key_entry_reports_only_whether_a_key_is_present()
    {
        var entry = McpSettings.Find("api_key")!;

        Assert.Equal("(not set)", entry.Get(new AppConfig { ApiKey = "" }).ToString());
        Assert.Equal("(set)", entry.Get(new AppConfig { ApiKey = "anything" }).ToString());
        Assert.Contains("Settings", entry.UiPath);   // the model can still guide the user there
    }

    [Fact]
    public void The_mcp_toggle_itself_cannot_be_changed_through_mcp()
    {
        // Otherwise a session could keep itself alive after the user disables it.
        // The entry must EXIST (the model has to be able to report the state) and
        // must be read-only — a conditional assert would pass if it vanished.
        var entry = McpSettings.Find("mcp_server_enabled");
        Assert.NotNull(entry);
        Assert.Null(entry.Set);
    }

    [Fact]
    public void A_writable_entry_rejects_a_wrong_type_instead_of_corrupting_the_config()
    {
        var cfg = new AppConfig();
        var boolEntry = McpSettings.All.First(e => e.Type == "bool" && e.Set is not null);

        string? rejection = boolEntry.Set!(cfg, JToken.FromObject("not a bool"));
        Assert.False(string.IsNullOrWhiteSpace(rejection));
    }

    [Fact]
    public void An_integer_entry_refuses_a_value_outside_its_documented_range()
    {
        var cfg = new AppConfig();
        var entry = McpSettings.All.First(e => e.Name == "qc_batch_size");

        Assert.NotNull(entry.Set!(cfg, JToken.FromObject(0)));          // below range
        Assert.NotNull(entry.Set!(cfg, JToken.FromObject(99999)));      // above range
        Assert.Null(entry.Set!(cfg, JToken.FromObject(25)));            // inside
        Assert.Equal(25, cfg.QcBatchSize);
    }

    [Fact]
    public void A_range_constrained_entry_documents_its_range()
    {
        // The model shows this to the user before proposing a value.
        Assert.All(McpSettings.All.Where(e => e.Type is "int" or "number" or "enum"),
                   e => Assert.False(string.IsNullOrWhiteSpace(e.Allowed)));
    }

    [Fact]
    public void The_qc_settings_are_registered()
    {
        // The upkeep rule: a new AppConfig option must appear here or the model
        // cannot see (or explain) it.
        foreach (var name in new[]
                 {
                     "qc_batch_size", "qc_parallel_enabled", "qc_max_concurrent_requests",
                     "qc_skip_empty", "qc_skip_auto_carried", "qc_skip_identical", "qc_skip_approved",
                     "qc_only_validator_flagged",
                     "qc_leak_exceptions", "qc_term_fixes",
                 })
            Assert.NotNull(McpSettings.Find(name));
    }

    [Fact]
    public void The_qc_content_lists_round_trip_as_string_arrays()
    {
        var cfg = new AppConfig();
        var entry = McpSettings.Find("qc_term_fixes")!;

        Assert.Null(entry.Set!(cfg, JToken.FromObject(new[] { "пролом=пробоїна", " x=y " })));
        Assert.Equal(new[] { "пролом=пробоїна", "x=y" }, cfg.QcTermFixes);
        Assert.Equal(new[] { "пролом=пробоїна", "x=y" }, entry.Get(cfg).ToObject<string[]>());
        Assert.NotNull(entry.Set!(cfg, JToken.FromObject("not an array")));
    }

    [Fact]
    public void Round_tripping_a_value_through_the_registry_preserves_it()
    {
        var cfg = new AppConfig();
        var entry = McpSettings.All.First(e => e.Name == "qc_batch_size");

        Assert.Null(entry.Set!(cfg, JToken.FromObject(42)));
        Assert.Equal(42, entry.Get(cfg).ToObject<int>());
    }
}

public class McpAppMapTests
{
    [Fact]
    public void The_map_is_valid_json_with_named_areas()
    {
        var map = McpAppMap.Build();
        Assert.NotEmpty(map.Properties());
    }

    [Fact]
    public void The_map_mentions_the_qc_review_and_how_a_model_can_be_its_reviewer()
    {
        // The map IS the documentation the model works from; a feature missing
        // here is a feature the assistant will deny exists.
        string text = McpAppMap.Build().ToString();
        Assert.Contains("QC", text);
        Assert.Contains("get_qc_rows", text);
        Assert.DoesNotContain("run_qc", text);              // the local pass is gone
        Assert.Contains("submit_qc_verdicts", text);
    }

    [Fact]
    public void The_map_states_the_translation_memory_safety_invariant()
    {
        // MCP must never write to the TM, and the model has to know that.
        string text = McpAppMap.Build().ToString();
        Assert.Contains("Translation Memory", text);
    }

    [Fact]
    public void Building_the_map_twice_gives_the_same_content()
        => Assert.Equal(McpAppMap.Build().ToString(), McpAppMap.Build().ToString());
}

public class McpPipeTests
{
    [Fact]
    public void The_pipe_is_per_user_so_two_accounts_never_share_a_session()
    {
        Assert.StartsWith("SmartUnrealLocalization.Mcp.", McpPipe.PipeName);
        Assert.Contains(Environment.UserName, McpPipe.PipeName);
    }

    [Fact]
    public void The_pipe_name_is_stable_within_a_session()
        => Assert.Equal(McpPipe.PipeName, McpPipe.PipeName);
}

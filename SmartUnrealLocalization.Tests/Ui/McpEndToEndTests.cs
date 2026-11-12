using System.Diagnostics;
using System.Text;
using Newtonsoft.Json.Linq;
using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services.Mcp;
using Xunit;
using static SmartUnrealLocalization.Tests.Ui.UiSession;

namespace SmartUnrealLocalization.Tests.Ui;

/// <summary>
/// The MCP bridge end to end, the way a chat client uses it: the real exe is started
/// as <c>SmartUnrealLocalization.exe --mcp</c>, spoken to in JSON-RPC over its stdin
/// and stdout, and every tool call travels the named pipe into a live window. Until
/// now only the two halves were tested apart; whether the spawned proxy, the protocol
/// SDK and the pipe actually meet was checked by hand.
/// </summary>
[Collection("AppState")]
public class McpEndToEndTests
{
    private sealed class Client : IDisposable
    {
        private readonly Process _process;
        private int _id;

        public Client(string exe)
        {
            _process = Process.Start(new ProcessStartInfo(exe, "--mcp")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardInputEncoding = new UTF8Encoding(false),
            })!;
        }

        private async Task SendAsync(JObject message)
        {
            await _process.StandardInput.WriteLineAsync(message.ToString(Newtonsoft.Json.Formatting.None));
            await _process.StandardInput.FlushAsync();
        }

        public Task NotifyAsync(string method) => SendAsync(new JObject { ["jsonrpc"] = "2.0", ["method"] = method });

        /// <summary>Send a request and return the response with the same id.</summary>
        public async Task<JObject> RequestAsync(string method, JObject? parameters = null)
        {
            int id = ++_id;
            var message = new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
            if (parameters != null) message["params"] = parameters;
            await SendAsync(message);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (true)
            {
                string? line = await _process.StandardOutput.ReadLineAsync(timeout.Token);
                Assert.True(line != null, "the proxy closed its output");
                // stdout carries the protocol and nothing else — every line must parse.
                var reply = JObject.Parse(line!);
                if ((int?)reply["id"] == id) return reply;
            }
        }

        /// <summary>Call a tool; returns the app's JSON answer ({"ok":…}).</summary>
        public async Task<JObject> CallAsync(string tool, JObject? arguments = null)
        {
            var reply = await RequestAsync("tools/call", new JObject { ["name"] = tool, ["arguments"] = arguments ?? new JObject() });
            Assert.True(reply["error"] == null, $"{tool}: {reply["error"]}");
            string text = (string)reply["result"]!["content"]![0]!["text"]!;
            return JObject.Parse(text);
        }

        public void Dispose()
        {
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
            _process.Dispose();
        }
    }

    [Fact]
    public Task A_chat_client_drives_the_running_window_through_the_spawned_proxy()
        => WithWindowAsync(async (window, vm) =>
        {
            string exe = Path.Combine(AppContext.BaseDirectory, "SmartUnrealLocalization.exe");
            Assert.True(File.Exists(exe), "the app exe is not next to the tests");
            // A real copy of the app with MCP on would answer on the same per-user pipe.
            if (Process.GetProcessesByName("SmartUnrealLocalization").Length > 0) return;

            await vm.OpenFileAsync(MakeLocres("mcp.locres", 4, i => $"Open the <b>door</b> {i}"));
            await Settle();
            var row = vm.AllRows[1];

            McpGuiServer.Instance.SetEnabled(true);
            using var client = new Client(exe);
            try
            {
                // Handshake.
                var init = await client.RequestAsync("initialize", new JObject
                {
                    ["protocolVersion"] = "2024-11-05",
                    ["capabilities"] = new JObject(),
                    ["clientInfo"] = new JObject { ["name"] = "tests", ["version"] = "1" },
                });
                Assert.NotNull(init["result"]?["serverInfo"]);
                await client.NotifyAsync("notifications/initialized");

                // The tool list a model sees.
                var tools = (JArray)(await client.RequestAsync("tools/list"))["result"]!["tools"]!;
                var names = tools.Select(t => (string)t["name"]!).ToHashSet();
                foreach (var expected in new[] { "get_status", "get_rows", "set_translations", "get_settings", "set_settings",
                                                 "get_app_map", "run_validation", "undo", "save_current", "get_problems" })
                    Assert.Contains(expected, names);
                Assert.All(tools, t => Assert.False(string.IsNullOrWhiteSpace((string?)t["description"])));

                // Read: the window's state and rows.
                var status = await client.CallAsync("get_status");
                Assert.True((bool)status["ok"]!, status.ToString());
                Assert.Contains("mcp.locres", status["data"]!.ToString());

                var rows = await client.CallAsync("get_rows", new JObject { ["count"] = 10 });
                Assert.True((bool)rows["ok"]!, rows.ToString());
                Assert.Contains(row.Key, rows["data"]!.ToString());

                // Write: a translation lands in the grid, tag-validated and undoable.
                var items = new JArray { new JObject { ["namespace"] = row.Namespace, ["key"] = row.Key, ["target"] = "Відчини <b>двері</b> 1" } };
                var written = await client.CallAsync("set_translations", new JObject { ["items_json"] = items.ToString() });
                Assert.True((bool)written["ok"]!, written.ToString());
                await Settle();
                Assert.Equal("Відчини <b>двері</b> 1", row.Target);

                // A translation that drops the tag is refused, not written.
                var broken = new JArray { new JObject { ["namespace"] = row.Namespace, ["key"] = row.Key, ["target"] = "Відчини двері" } };
                var refused = await client.CallAsync("set_translations", new JObject { ["items_json"] = broken.ToString() });
                await Settle();
                Assert.Equal("Відчини <b>двері</b> 1", row.Target);
                Assert.Contains("tag", refused.ToString(), StringComparison.OrdinalIgnoreCase);

                var undone = await client.CallAsync("undo");
                Assert.True((bool)undone["ok"]!, undone.ToString());
                await Settle();
                Assert.NotEqual("Відчини <b>двері</b> 1", row.Target);

                // Settings: readable, the new accent setting applies, and the two
                // protected ones stay out of reach.
                AppConfig.Instance.ApiKey = Secret;
                var settings = await client.CallAsync("get_settings");
                string settingsText = settings.ToString();
                Assert.Contains("accent_color", settingsText);
                Assert.DoesNotContain(Secret, settingsText);                 // the API key never crosses the bridge

                var keyWrite = await client.CallAsync("set_settings", new JObject { ["settings_json"] = "{\"api_key\":\"stolen\"}" });
                Assert.Equal(Secret, AppConfig.Instance.ApiKey);            // …and cannot be set through it
                Assert.DoesNotContain(Secret, keyWrite.ToString());

                var accent = await client.CallAsync("set_settings", new JObject { ["settings_json"] = "{\"accent_color\":\"#2FA36B\"}" });
                Assert.True((bool)accent["ok"]!, accent.ToString());
                Assert.Equal("#2FA36B", AppConfig.Instance.AccentColor);

                // The MCP switch is read-only over MCP: asking to turn it off changes
                // nothing, and the bridge keeps answering.
                bool before = AppConfig.Instance.McpServerEnabled;
                await client.CallAsync("set_settings", new JObject { ["settings_json"] = "{\"mcp_server_enabled\":" + (before ? "false" : "true") + "}" });
                Assert.Equal(before, AppConfig.Instance.McpServerEnabled);

                var map = await client.CallAsync("get_app_map");
                Assert.True((bool)map["ok"]!, map.ToString());
                Assert.Contains("Prompt history", map.ToString());
            }
            finally
            {
                McpGuiServer.Instance.SetEnabled(false);
                Services.ThemeManager.SetAccent(null);
                AppConfig.Instance.ApiKey = "";
            }
        });

    private const string Secret = "sk-e2e-secret-value-7f3a";
}

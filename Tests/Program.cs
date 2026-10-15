using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;
using Discord.Net.Rest;
using Discord.Rest;
using Discord.Webhook;
using DiscordStatus;

// All HTTP traffic is in-memory. No webhook credentials, sockets or CS2 host needed.
static class Program
{
    const string Secret = "synthetic-webhook-token-never-log";

    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    static async Task<int> Main(string[] args)
    {
        if (args.Contains("--timer-child")) return await TimerChild();
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("synchronous constructor-style failure", async () =>
                Check(!await DiscordOperation.RunAsync(() => throw new HttpRequestException(Secret), "test"), "Failure reported as success")),
            ("asynchronous send-style failure", async () =>
                Check(!await DiscordOperation.RunAsync(async () => { await Task.Yield(); throw new TaskCanceledException(Secret); }, "test"), "Failure reported as success")),
            ("success result", async () =>
                Check(await DiscordOperation.RunAsync(() => Task.CompletedTask, "test"), "Success reported as failure")),
            ("real webhook constructor failure and later recovery", ConstructorRecovery),
            ("all six webhook paths: constructor and send failures", AllWebhookFailures),
            ("request command contains failures without false success", RequestCommand),
            ("real timer survives asynchronous exception in child process", TimerSubprocess),
        };
        int failed = 0;
        foreach (var test in tests)
        {
            var output = Console.Out;
            using var logs = new StringWriter();
            Console.SetOut(logs);
            try
            {
                await test.Run();
                Check(!logs.ToString().Contains(Secret), "Webhook token leaked to logs");
                output.WriteLine($"PASS: {test.Name}");
            }
            catch (Exception ex)
            {
                output.WriteLine($"FAIL: {test.Name} ({ex.GetType().Name}: {ex.Message.Replace(Secret, "[redacted]")})");
                failed++;
            }
            finally { Console.SetOut(output); }
        }
        Console.WriteLine($"{tests.Length - failed}/{tests.Length} regression groups passed");
        return failed == 0 ? 0 : 1;
    }

    static Globals Config() => new()
    {
        MessageID = 42,
        ServerIP = "127.0.0.1:27015",
        ConnectURL = "",
        MapName = "de_test",
        WConfig = new()
        {
            StatusWebhookURL = "https://discord.com/api/webhooks/123/" + Secret,
            StatusMessageID = 42,
            RequestPlayersURL = "https://discord.com/api/webhooks/123/" + Secret,
            NotifyWebhookURL = "https://discord.com/api/webhooks/123/" + Secret,
            ScoreboardURL = "https://discord.com/api/webhooks/123/" + Secret,
        },
        EConfig = new() { Title = "Regression test" },
    };

    static Webhook Webhook(Globals g, FakeRest rest) => new(new Chores(g, null!), g,
        url => global::DiscordStatus.Webhook.CreateClient(url, _ => rest));

    static async Task ConstructorRecovery()
    {
        var rest = new FakeRest { FailGet = true };
        var webhook = Webhook(Config(), rest);
        Check(!await DiscordOperation.RunAsync(webhook.UpdateEmbed, "status update"), "Constructor failure escaped detection");
        Check(rest.Gets == 1 && rest.Patches == 0, "Must fail in real Discord.Net constructor GET before PATCH");
        Check(rest.Disposals == 1, "Failed constructor leaked its REST transport");
        rest.FailGet = false;
        Check(await DiscordOperation.RunAsync(webhook.UpdateEmbed, "status update"), "Recovery failed");
        Check(rest.Patches == 1, "Next update did not PATCH the message");
        Check(rest.Disposals == 2, "Recovered update leaked its client");
    }

    static async Task AllWebhookFailures()
    {
        foreach (bool constructor in new[] { true, false })
        {
            var g = Config();
            g.PlayerList[1] = new PlayerInfo { Name = "Test", TeamID = 2 };
            g.TPlayersName.Add("Test");
            var rest = new FakeRest { FailGet = constructor, FailWrite = !constructor };
            var webhook = Webhook(g, rest);
            var actions = new Func<Task>[]
            {
                webhook.InitialMessageAsync, webhook.UpdateEmbed,
                () => webhook.RequestPlayers("Admin"), () => webhook.NewMap("de_test", 1),
                () => webhook.GameEnd("Test"), webhook.ServerOffiline,
            };
            foreach (var action in actions) await DiscordOperation.RunAsync(action, "notification");
            Check(rest.Failures == 6, "Each real webhook path must reach the injected HTTP failure");
            Check(rest.Disposals == 6, "Every failed operation must dispose its transport/client");
        }
    }

    static async Task RequestCommand()
    {
        // Bypass only BasePlugin's native-listener constructor; execute the real command.
        var plugin = (DiscordStatus.DiscordStatus)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(DiscordStatus.DiscordStatus));
        var g = Config();
        var rest = new FakeRest { FailWrite = true };
        var chores = new Chores(g, null!);
        void Set(string name, object value) => typeof(DiscordStatus.DiscordStatus)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(plugin, value);
        Set("_chores", chores);
        Set("_webhook", Webhook(g, rest));
        var previous = SynchronizationContext.Current;
        using var logs = new StringWriter();
        var output = Console.Out;
        Console.SetOut(logs);
        try
        {
            var context = new CallbackContext();
            SynchronizationContext.SetSynchronizationContext(context);
            plugin.RequestPlayers(null, null!);
            SynchronizationContext.SetSynchronizationContext(previous);
            await context.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(context.Errors.Count == 0, "Request command leaked an async-void exception");
            Check(!logs.ToString().Contains("Request sent"), "Failed request was reported as sent");
            Check(!logs.ToString().Contains(Secret), "Request command logged a webhook token");
            Check(rest.Failures == 1 && rest.Disposals == 1, "Request did not exercise and clean up the failing send");
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); Console.SetOut(output); }
    }

    static async Task<int> TimerChild()
    {
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        using var timer = DiscordOperation.StartTimer(TimeSpan.FromMilliseconds(15), async () =>
        {
            var call = Interlocked.Increment(ref calls);
            await Task.Yield(); // Exception happens after the void timer handler returns.
            if (call == 1) throw new TaskCanceledException(Secret);
            recovered.TrySetResult();
        });
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        timer.Stop();
        Console.WriteLine("TIMER_RECOVERED");
        return 0;
    }

    static async Task TimerSubprocess()
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--timer-child");
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
        catch { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
        var output = await stdout;
        var errors = await stderr;
        Check(process.ExitCode == 0 && !errors.Contains("Unhandled exception"), $"Timer child failed (exit {process.ExitCode})");
        Check(output.Contains("TIMER_RECOVERED"), "Timer did not run after its first failure");
        Check(!output.Contains(Secret) && !errors.Contains(Secret), "Timer logged sensitive exception content");
    }
}

sealed class CallbackContext : SynchronizationContext
{
    public readonly TaskCompletionSource Completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly System.Collections.Concurrent.ConcurrentQueue<Exception> Errors = new();
    public override void OperationCompleted() => Completed.TrySetResult();
    public override void Post(SendOrPostCallback callback, object? state) => ThreadPool.QueueUserWorkItem(_ =>
    {
        try { callback(state); }
        catch (Exception ex) { Errors.Enqueue(ex); }
    });
}

sealed class FakeRest : IRestClient
{
    public bool FailGet, FailWrite;
    public int Gets, Patches, Failures, Disposals;
    public void Dispose() => Disposals++;
    public void SetHeader(string key, string value) { }
    public void SetCancelToken(CancellationToken token) { }
    public Task<RestResponse> SendAsync(string method, string endpoint, CancellationToken cancelToken,
        bool headerOnly = false, string? reason = null, IEnumerable<KeyValuePair<string, IEnumerable<string>>>? requestHeaders = null)
        => SendCore(method);
    public Task<RestResponse> SendAsync(string method, string endpoint, string json, CancellationToken cancelToken,
        bool headerOnly = false, string? reason = null, IEnumerable<KeyValuePair<string, IEnumerable<string>>>? requestHeaders = null)
        => SendCore(method);
    public Task<RestResponse> SendAsync(string method, string endpoint, IReadOnlyDictionary<string, object> multipartParams, CancellationToken cancelToken,
        bool headerOnly = false, string? reason = null, IEnumerable<KeyValuePair<string, IEnumerable<string>>>? requestHeaders = null)
        => SendCore(method);
    async Task<RestResponse> SendCore(string method)
    {
        if (method == "GET") Gets++;
        if ((method == "GET" && FailGet) || (method != "GET" && FailWrite))
        {
            Failures++;
            await Task.Yield();
            throw new TaskCanceledException("synthetic-webhook-token-never-log");
        }
        if (method == "PATCH") Patches++;
        var json = method == "GET"
            ? "{\"id\":\"123\",\"type\":1,\"token\":\"synthetic\",\"name\":\"test\",\"channel_id\":\"456\"}"
            : "{\"id\":\"42\",\"type\":0,\"channel_id\":\"456\",\"content\":\"\",\"author\":{\"id\":\"123\",\"username\":\"test\",\"discriminator\":\"0000\"},\"timestamp\":\"2026-10-15T00:00:00Z\"}";
        return new RestResponse(HttpStatusCode.OK, new(), new MemoryStream(Encoding.UTF8.GetBytes(json)));
    }
}

using System.Net;
using System.Text;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;
using Vigo.Integrations.Ai;

namespace Vigo.Integrations.Tests;

public sealed class AiBackendTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeVault(string? key) : ISecretVault
    {
        public string? Retrieve(string name) => key;

        public void Store(string name, string secret) { }

        public void Remove(string name) { }
    }

    private sealed class FakeHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request, request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task Api_sends_the_key_the_model_and_the_prompt_and_returns_the_text()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, """{"content":[{"type":"text","text":"Add feature"}]}""");
        var sut = new AnthropicApiBackend(new HttpClient(handler), new FakeVault("sk-test"));

        var result = await sut.CompleteAsync(new AiRequest("the prompt", null, null), Ct);

        Assert.Equal("Add feature", Assert.IsType<Success<string>>(result.Value).Value);
        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal("https://api.anthropic.com/v1/messages", request.RequestUri!.ToString());
        Assert.Equal("sk-test", Assert.Single(request.Headers.GetValues("x-api-key")));
        Assert.Equal("2023-06-01", Assert.Single(request.Headers.GetValues("anthropic-version")));
        Assert.Contains($"\"model\":\"{AiSettings.DefaultApiModel}\"", body, StringComparison.Ordinal);
        Assert.Contains("the prompt", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Api_without_a_key_asks_for_one_and_sends_nothing()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        var sut = new AnthropicApiBackend(new HttpClient(handler), new FakeVault(null));

        Assert.IsType<AuthenticationFailed>((await sut.CompleteAsync(new AiRequest("p", null, null), Ct)).Value);
        Assert.IsType<Failure>((await sut.CheckAsync(new AiRequest(string.Empty, null, null), Ct)).Value);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Api_maps_401_to_an_authentication_failure_and_other_errors_to_a_message()
    {
        var refused = new AnthropicApiBackend(new HttpClient(new FakeHandler(HttpStatusCode.Unauthorized, "{}")), new FakeVault("bad"));
        Assert.IsType<AuthenticationFailed>((await refused.CompleteAsync(new AiRequest("p", null, null), Ct)).Value);

        var overloaded = new AnthropicApiBackend(
            new HttpClient(new FakeHandler((HttpStatusCode)529, """{"error":{"message":"Overloaded"}}""")), new FakeVault("k"));
        var failure = Assert.IsType<Failure>((await overloaded.CompleteAsync(new AiRequest("p", null, null), Ct)).Value);
        Assert.Contains("Overloaded", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Command_lines_are_split_with_quotes_and_defaults()
    {
        var (program, arguments) = CliAiBackend.Split("\"C:\\Tools\\my tool.exe\" -p \"a b\" x", AiSettings.DefaultCliCommand);
        Assert.Equal("C:\\Tools\\my tool.exe", program);
        Assert.Equal(["-p", "a b", "x"], arguments);

        var (defaultProgram, defaultArguments) = CliAiBackend.Split("  ", AiSettings.DefaultCommandFor(AiCliTool.Copilot));
        Assert.Equal("copilot", defaultProgram);
        Assert.Equal(["-p", AiSettings.PromptPlaceholder, "--silent"], defaultArguments);
    }

    [Fact]
    public async Task Missing_program_is_reported()
    {
        var sut = new CliAiBackend();
        var request = new AiRequest("p", null, "definitely-not-a-real-program-vigo -p");

        Assert.IsType<Failure>((await sut.CheckAsync(request, Ct)).Value);
        Assert.IsType<Failure>((await sut.CompleteAsync(request, Ct)).Value);
    }

    [Fact]
    public async Task Cli_gets_the_prompt_on_stdin_and_returns_stdout()
    {
        // findstr "^" copies stdin to stdout: a stand-in for a tool that reads the prompt and prints the message.
        var sut = new CliAiBackend();

        var result = await sut.CompleteAsync(new AiRequest("Add feature\n\nBecause.", null, "findstr ^"), Ct);

        Assert.Contains("Add feature", Assert.IsType<Success<string>>(result.Value).Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cli_non_zero_exit_is_a_failure()
    {
        var sut = new CliAiBackend();

        var result = await sut.CompleteAsync(new AiRequest("p", null, "findstr /Z"), Ct);

        Assert.IsType<Failure>(result.Value);
    }
    [Fact]
    public async Task A_cmd_script_is_refused_when_the_prompt_is_an_argument()
    {
        var folder = Directory.CreateTempSubdirectory("vigo-ai-").FullName;
        try
        {
            var marker = Path.Combine(folder, "ran.txt");
            var script = Path.Combine(folder, "tool.cmd");
            await File.WriteAllTextAsync(script, $"@echo ran> \"{marker}\"\r\n", Ct);
            var sut = new CliAiBackend();

            var asArgument = await sut.CompleteAsync(new AiRequest("a & b %PATH%", null, $"\"{script}\" -p {{prompt}}"), Ct);
            var onStdin = await sut.CompleteAsync(new AiRequest("a & b", null, $"\"{script}\" -p"), Ct);

            Assert.Contains("script", Assert.IsType<Failure>(asArgument.Value).Message, StringComparison.Ordinal);
            Assert.IsType<Success<string>>(onStdin.Value);
            Assert.True(File.Exists(marker)); // written by the stdin run only, which is safe
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData("claude-haiku-5-5", true)]
    [InlineData("anthropic/claude-3.5:latest", true)]
    [InlineData("x\" & calc & \"", false)]
    [InlineData("a b", false)]
    public async Task The_model_name_is_checked_before_it_reaches_the_command_line(string model, bool allowed)
    {
        var result = await new CliAiBackend().CompleteAsync(new AiRequest("p", model, "findstr ^"), Ct);

        // findstr rejects the --model flag it is given, so "allowed" means: not refused by the name check.
        var refused = result.Value is Failure f && f.Message == Loc.Get("Ai.Cli.ModelRefused");
        Assert.Equal(allowed, !refused);
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Vigo.Core.Models;
using Vigo.Core.Results;
using Vigo.Core.Services;

namespace Vigo.Integrations.Ai;

/// <summary>
/// Commit message through the Anthropic Messages API (J7.9, ADR-070). The key lives in the Credential Locker
/// (<see cref="SecretNames.AnthropicApiKey"/>), never in a file or a log; only the prompt (diffstat + patch) is sent.
/// </summary>
public sealed class AnthropicApiBackend(HttpClient http, ISecretVault vault) : IAiBackend
{
    private const string Endpoint = "https://api.anthropic.com/v1/messages";
    private const int MaxTokens = 1024;

    public AiProvider Provider => AiProvider.Api;

    public Task<GitResult> CheckAsync(AiRequest settings, CancellationToken cancellationToken = default) =>
        vault.Retrieve(SecretNames.AnthropicApiKey) is { Length: > 0 }
            ? CheckKeyAsync(settings, cancellationToken)
            : Task.FromResult<GitResult>(new Failure(Loc.Get("Ai.Api.NoKey")));

    // One token of output is enough to know the key and the model are accepted.
    private async Task<GitResult> CheckKeyAsync(AiRequest settings, CancellationToken cancellationToken)
    {
        var result = await SendAsync(new AiRequest("Reply with: ok", settings.Model, null), 8, cancellationToken);
        return result switch
        {
            Success<string> => Done.Instance,
            Failure f => f,
            AuthenticationFailed a => a,
            Canceled c => c,
            _ => new Failure(Loc.Get("Ai.Error.Unexpected")),
        };
    }

    public Task<GitResult<string>> CompleteAsync(AiRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(request, MaxTokens, cancellationToken);

    private async Task<GitResult<string>> SendAsync(AiRequest request, int maxTokens, CancellationToken cancellationToken)
    {
        if (vault.Retrieve(SecretNames.AnthropicApiKey) is not { Length: > 0 } key)
            return new AuthenticationFailed("anthropic", Loc.Get("Ai.Api.NoKeyAdd"));

        var body = new MessagesRequest(
            string.IsNullOrWhiteSpace(request.Model) ? AiSettings.DefaultApiModel : request.Model, maxTokens,
            [new MessageContent("user", request.Prompt)]);
        using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = JsonContent.Create(body, AnthropicJsonContext.Default.MessagesRequest),
        };
        message.Headers.Add("x-api-key", key);
        message.Headers.Add("anthropic-version", "2023-06-01");

        try
        {
            using var response = await http.SendAsync(message, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new AuthenticationFailed("anthropic", Loc.Get("Ai.Api.KeyRefused"));
            if (!response.IsSuccessStatusCode)
            {
                var error = await ReadErrorAsync(response, cancellationToken);
                return new Failure(Loc.Format("Hosting.StatusReply", "Anthropic", (int)response.StatusCode, response.ReasonPhrase) + (error is null ? string.Empty : $" {error}"));
            }

            var reply = await response.Content.ReadFromJsonAsync(AnthropicJsonContext.Default.MessagesResponse, cancellationToken);
            var text = string.Concat(reply?.Content?.Where(c => c.Type == "text").Select(c => c.Text) ?? []);
            return text.Length > 0 ? new Success<string>(text) : new Failure(Loc.Get("Ai.Api.Empty"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Canceled.Instance;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return new Failure(Loc.Format("Hosting.Unreachable", "Anthropic", e.Message), Exception: e);
        }
    }

    private static async Task<string?> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync(AnthropicJsonContext.Default.ErrorResponse, cancellationToken);
            return error?.Error?.Message;
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or HttpRequestException)
        {
            return null;
        }
    }
}

internal sealed record MessagesRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("max_tokens")] int MaxTokens,
    [property: JsonPropertyName("messages")] MessageContent[] Messages);

internal sealed record MessageContent(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

internal sealed record MessagesResponse([property: JsonPropertyName("content")] ResponseBlock[]? Content);

internal sealed record ResponseBlock(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("text")] string? Text);

internal sealed record ErrorResponse([property: JsonPropertyName("error")] ErrorBody? Error);

internal sealed record ErrorBody([property: JsonPropertyName("message")] string? Message);

[JsonSerializable(typeof(MessagesRequest))]
[JsonSerializable(typeof(MessagesResponse))]
[JsonSerializable(typeof(ErrorResponse))]
internal sealed partial class AnthropicJsonContext : JsonSerializerContext;

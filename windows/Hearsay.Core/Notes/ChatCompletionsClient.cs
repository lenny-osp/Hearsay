using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hearsay.Core.Notes;

/// <summary>
/// Why a chat-completions call produced no usable content. Port of
/// <c>ChatCompletionsError</c> in
/// mac/HearsayCore/Sources/HearsayCore/Notes/ChatCompletionsClient.swift;
/// the Swift enum cases are records and <see cref="ChatCompletionsException"/>
/// carries one. Messages follow the Python <c>_request_api</c> /
/// <c>_report_http_error</c> diagnostics; <see cref="ChatCompletionsError.Localized"/>
/// carries their catalog keys for the app.
/// </summary>
public abstract record ChatCompletionsError
{
    /// <summary>Python truncates response bodies to 500 characters in diagnostics.</summary>
    public const int ExcerptLength = 500;

    private ChatCompletionsError()
    {
    }

    /// <summary>The auth style needs a token and none is stored.</summary>
    public sealed record MissingToken : ChatCompletionsError;

    public sealed record InvalidUrl : ChatCompletionsError;

    public sealed record Transport(string Detail) : ChatCompletionsError
    {
        /// <summary><see cref="Detail"/> with its catalog key, when Hearsay wrote it (a time-out).</summary>
        public ILocalizedMessage? LocalizedDetail { get; init; }

        /// <summary>The request ran into <see cref="ChatCompletionsClient"/>'s time limit (URLError's "The request timed out." on the Mac).</summary>
        public static Transport TimedOut() =>
            new(CommonMessages.RequestTimedOut.English) { LocalizedDetail = CommonMessages.RequestTimedOut };
    }

    /// <summary>The host could not be resolved or connected to, or there is no network.</summary>
    public sealed record Unreachable(string Host) : ChatCompletionsError;

    /// <summary>Non-2xx response, or a 2xx response carrying only an <c>error</c> object.</summary>
    public sealed record HttpStatus(int Code, string BodyExcerpt) : ChatCompletionsError;

    public sealed record InvalidJson(string BodyExcerpt) : ChatCompletionsError;

    public sealed record EmptyContent : ChatCompletionsError;

    /// <summary>The message shown to the user (Swift <c>errorDescription</c>).</summary>
    public string Description => this switch
    {
        MissingToken => "No API token is set for this provider. Add one in Settings > AI.",
        InvalidUrl => "The API URL is not a valid http or https address. Check it in Settings > AI.",
        Unreachable e => $"Cannot reach {e.Host}. Check the base URL in Settings > AI and your network connection.",
        Transport e => $"API call failed: A network connection or HTTP client error occurred. {e.Detail}",
        HttpStatus e when e.BodyExcerpt.Trim().Length == 0 =>
            $"API HTTP Error {e.Code.ToString(CultureInfo.InvariantCulture)}: The response body is empty.",
        HttpStatus e =>
            $"API HTTP Error {e.Code.ToString(CultureInfo.InvariantCulture)}: {ApiErrorMessage(e.BodyExcerpt) ?? e.BodyExcerpt}",
        InvalidJson e => $"Parsing Error: The API response is not valid JSON: {e.BodyExcerpt}",
        EmptyContent => "Parsing Error: API response did not contain usable message content.",
        _ => throw new InvalidOperationException("Unknown ChatCompletionsError."),
    };

    /// <summary><see cref="Description"/> as its catalog key and values, for the app to translate.</summary>
    public LocalizedMessage Localized => this switch
    {
        MissingToken => new LocalizedMessage("No API token is set for this provider. Add one in Settings > AI."),
        InvalidUrl => new LocalizedMessage("The API URL is not a valid http or https address. Check it in Settings > AI."),
        Unreachable e => new LocalizedMessage(
            "Cannot reach %@. Check the base URL in Settings > AI and your network connection.", e.Host),
        Transport e => new LocalizedMessage("API call failed: A network connection or HTTP client error occurred. %@",
            (object?)e.LocalizedDetail ?? e.Detail),
        HttpStatus e when e.BodyExcerpt.Trim().Length == 0 =>
            new LocalizedMessage("API HTTP Error %lld: The response body is empty.", e.Code),
        HttpStatus e => new LocalizedMessage("API HTTP Error %lld: %@", e.Code, ApiErrorMessage(e.BodyExcerpt) ?? e.BodyExcerpt),
        InvalidJson e => new LocalizedMessage("Parsing Error: The API response is not valid JSON: %@", e.BodyExcerpt),
        EmptyContent => new LocalizedMessage("Parsing Error: API response did not contain usable message content."),
        _ => throw new InvalidOperationException("Unknown ChatCompletionsError."),
    };

    /// <summary>
    /// <c>error.message</c> of an OpenAI-style error body, when present. An
    /// <c>error</c> object without a string <c>message</c> is shown as JSON
    /// (the Mac shows Foundation's dictionary description).
    /// </summary>
    internal static string? ApiErrorMessage(string body)
    {
        try
        {
            if (JsonNode.Parse(body) is not JsonObject root || root["error"] is not JsonObject error) return null;
            if (error["message"] is JsonValue message && message.TryGetValue<string>(out var text)) return text;
            return error.ToJsonString();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Thrown by <see cref="ChatCompletionsClient"/>; <see cref="Error"/> says what failed.</summary>
public sealed class ChatCompletionsException : Exception, ILocalizedError
{
    public ChatCompletionsException(ChatCompletionsError error, Exception? innerException = null)
        : base(error?.Description, innerException)
    {
        ArgumentNullException.ThrowIfNull(error);
        Error = error;
    }

    public ChatCompletionsError Error { get; }

    public ILocalizedMessage LocalizedMessage => Error.Localized;
}

/// <summary>
/// Something that answers a system + user message pair. <see cref="NotesPipeline"/>
/// depends on this so tests can inject a fake. Port of the Swift <c>ChatCompleting</c> protocol.
/// </summary>
public interface IChatCompleting
{
    Task<string> CompleteAsync(string systemMessage, string userMessage, AIProviderConfiguration configuration,
        string? token, CancellationToken cancellationToken = default);
}

/// <summary>
/// OpenAI-compatible <c>/chat/completions</c> client on <see cref="HttpClient"/>
/// (Ollama / LM Studio and Custom presets). Port of
/// mac/HearsayCore/Sources/HearsayCore/Notes/ChatCompletionsClient.swift
/// (itself the API branch of Python <c>generate_meeting_notes</c> and
/// <c>_post_chat_completions</c>).
/// <para>
/// Payload <c>{model, messages, temperature?, reasoning_effort?}</c>, keys
/// sorted as the Mac writes them. Python always sends both optional keys;
/// here <c>temperature</c> is omitted when null and <c>reasoning_effort</c>
/// when empty or unsupported by the preset.
/// </para>
/// </summary>
public sealed class ChatCompletionsClient : IChatCompleting, IDisposable
{
    /// <summary>Python <c>urlopen(request, timeout=300)</c>.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(300);

    private readonly HttpClient client;

    /// <param name="handler">The message handler (tests pass a fake); null uses a new <see cref="HttpClientHandler"/> without cookies.</param>
    public ChatCompletionsClient(HttpMessageHandler? handler = null)
    {
        client = new HttpClient(handler ?? new HttpClientHandler { UseCookies = false }, disposeHandler: true)
        {
            Timeout = Timeout,
        };
    }

    /// <summary>The time limit of one request (<see cref="Timeout"/>).</summary>
    public TimeSpan RequestTimeout => client.Timeout;

    public void Dispose() => client.Dispose();

    /// <summary>
    /// The request <see cref="CompleteAsync"/> sends. Throws
    /// <see cref="ChatCompletionsException"/> for a missing token or an
    /// unusable URL. Exposed for tests.
    /// </summary>
    public static HttpRequestMessage MakeRequest(string systemMessage, string userMessage,
        AIProviderConfiguration configuration, string? token)
    {
        ArgumentNullException.ThrowIfNull(systemMessage);
        ArgumentNullException.ThrowIfNull(userMessage);
        ArgumentNullException.ThrowIfNull(configuration);
        var trimmedToken = token?.Trim();
        var usableToken = string.IsNullOrEmpty(trimmedToken) ? null : trimmedToken;
        if (configuration.Auth.NeedsToken() && usableToken is null)
        {
            throw new ChatCompletionsException(new ChatCompletionsError.MissingToken());
        }
        var address = configuration.BaseUrl.Trim();
        if (!Uri.TryCreate(address, UriKind.Absolute, out var url)
            || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(url.Host))
        {
            throw new ChatCompletionsException(new ChatCompletionsError.InvalidUrl());
        }

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(Payload(systemMessage, userMessage, configuration)),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        switch (configuration.Auth)
        {
            case AuthHeaderStyle.Bearer when usableToken is not null:
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + usableToken);
                break;
            case AuthHeaderStyle.ApiKey when usableToken is not null:
                request.Headers.TryAddWithoutValidation("api-key", usableToken);
                break;
            default:
                break;
        }
        foreach (var (name, value) in configuration.ExtraHeaders.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var trimmedName = name.Trim();
            if (trimmedName.Length == 0) continue;
            SetHeader(request, trimmedName, value);
        }
        return request;
    }

    /// <summary>Replaces a header, as <c>URLRequest.setValue</c> does; content headers go on the content.</summary>
    private static void SetHeader(HttpRequestMessage request, string name, string value)
    {
        try
        {
            request.Headers.Remove(name);
            if (request.Headers.TryAddWithoutValidation(name, value)) return;
        }
        catch (InvalidOperationException)
        {
            // A content header name (for example Content-Type).
        }
        if (request.Content is { } content)
        {
            content.Headers.Remove(name);
            content.Headers.TryAddWithoutValidation(name, value);
        }
    }

    private static byte[] Payload(string systemMessage, string userMessage, AIProviderConfiguration configuration)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("messages");
            foreach (var (role, content) in new[] { ("system", systemMessage), ("user", userMessage) })
            {
                writer.WriteStartObject();
                writer.WriteString("content", content);
                writer.WriteString("role", role);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteString("model", configuration.Model);
            if (configuration.EffectiveReasoningEffort is { } effort)
            {
                writer.WriteString("reasoning_effort", effort);
            }
            if (configuration.Temperature is { } temperature)
            {
                writer.WriteNumber("temperature", temperature);
            }
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    public async Task<string> CompleteAsync(string systemMessage, string userMessage,
        AIProviderConfiguration configuration, string? token, CancellationToken cancellationToken = default)
    {
        using var request = MakeRequest(systemMessage, userMessage, configuration, token);
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException error)
        {
            throw new ChatCompletionsException(ChatCompletionsError.Transport.TimedOut(), error);
        }
        catch (HttpRequestException error) when (error.HttpRequestError is HttpRequestError.NameResolutionError
                                                     or HttpRequestError.ConnectionError)
        {
            throw new ChatCompletionsException(
                new ChatCompletionsError.Unreachable(request.RequestUri?.Host ?? configuration.BaseUrl), error);
        }
        catch (HttpRequestException error)
        {
            throw new ChatCompletionsException(new ChatCompletionsError.Transport(error.Message), error);
        }
        using (response)
        {
            byte[] body;
            try
            {
                body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException error)
            {
                throw new ChatCompletionsException(new ChatCompletionsError.Transport(error.Message), error);
            }
            return Content((int)response.StatusCode, body);
        }
    }

    /// <summary>Port of the status and body checks after <c>_post_chat_completions</c>.</summary>
    internal static string Content(int status, byte[] body)
    {
        var text = Encoding.UTF8.GetString(body);
        var excerpt = TextElements.Prefix(text, ChatCompletionsError.ExcerptLength);
        if (status is < 200 or >= 300)
        {
            throw new ChatCompletionsException(new ChatCompletionsError.HttpStatus(status, excerpt));
        }
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(body);
        }
        catch (JsonException error)
        {
            throw new ChatCompletionsException(new ChatCompletionsError.InvalidJson(excerpt), error);
        }
        if (parsed is not JsonObject root)
        {
            throw new ChatCompletionsException(new ChatCompletionsError.EmptyContent());
        }
        if (root["choices"] is JsonArray { Count: > 0 } choices)
        {
            if (choices[0] is JsonObject choice
                && choice["message"] is JsonObject message
                && message["content"] is JsonValue value
                && value.TryGetValue<string>(out var content)
                && content.Trim().Length > 0)
            {
                return content;
            }
            throw new ChatCompletionsException(new ChatCompletionsError.EmptyContent());
        }
        if (root["error"] is JsonObject)
        {
            throw new ChatCompletionsException(new ChatCompletionsError.HttpStatus(status, excerpt));
        }
        throw new ChatCompletionsException(new ChatCompletionsError.EmptyContent());
    }
}

/// <summary>
/// Prefixes and suffixes counted in user-perceived characters (text
/// elements), as Swift's <c>String.prefix</c> and <c>suffix</c> count them.
/// </summary>
internal static class TextElements
{
    public static string Prefix(string text, int count)
    {
        if (text.Length <= count) return text;
        var info = new StringInfo(text);
        return info.LengthInTextElements <= count ? text : info.SubstringByTextElements(0, count);
    }

    public static string Suffix(string text, int count)
    {
        if (text.Length <= count) return text;
        var info = new StringInfo(text);
        var length = info.LengthInTextElements;
        return length <= count ? text : info.SubstringByTextElements(length - count);
    }
}

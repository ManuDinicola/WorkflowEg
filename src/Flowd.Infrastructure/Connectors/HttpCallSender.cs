using System.Text;
using Flowd.Application.Connectors;
using Microsoft.Extensions.Configuration;

namespace Flowd.Infrastructure.Connectors;

/// <summary>Sends connector HTTP calls with one shared client that never follows redirects and cuts the response at the configured size (FR-CON-010).</summary>
public sealed class HttpCallSender : IHttpCallSender, IDisposable
{
    private readonly HttpClient _client = new(new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = Timeout.InfiniteTimeSpan };

    public async Task<HttpReply> SendAsync(HttpCall request, TimeSpan timeout, int maxResponseBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Url);
        if (request.Body is not null)
        {
            message.Content = new StringContent(request.Body, Encoding.UTF8, request.Headers.GetValueOrDefault("Content-Type") ?? "application/json");
        }

        foreach (var (name, value) in request.Headers.Where(h => !string.Equals(h.Key, "Content-Type", StringComparison.OrdinalIgnoreCase)))
        {
            message.Headers.TryAddWithoutValidation(name, value);
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(timeoutSource.Token).ConfigureAwait(false);
        var buffer = new byte[Math.Max(maxResponseBytes, 1)];
        var read = 0;
        int count;
        while (read < buffer.Length && (count = await stream.ReadAsync(buffer.AsMemory(read), timeoutSource.Token).ConfigureAwait(false)) > 0)
        {
            read += count;
        }

        return new HttpReply((int)response.StatusCode, response.Content.Headers.ContentType?.MediaType, Encoding.UTF8.GetString(buffer, 0, read));
    }

    public void Dispose() => _client.Dispose();
}

/// <summary>Secrets from configuration (<c>Flowd:Secrets:NAME</c>, or the environment variable <c>Flowd__Secrets__NAME</c>).</summary>
public sealed class ConfigSecretProvider(IConfiguration configuration) : ISecretProvider
{
    public string? Find(string name) => configuration.GetSection("Flowd:Secrets")[name];
}

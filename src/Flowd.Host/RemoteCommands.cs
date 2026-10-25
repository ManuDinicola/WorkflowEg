using System.Net.Http.Headers;
using System.Text;

namespace Flowd.Host;

/// <summary>The commands <c>flowd deploy</c> and <c>flowd start</c>: small clients of a running Flowd (FR-OPS-012). They use the REST API like any other client.</summary>
internal static class RemoteCommands
{
    /// <summary>Sends BPMN, DMN or form files to the matching deploy endpoint. Returns the exit code and prints the answer (or the problem) to <paramref name="output"/>.</summary>
    public static async Task<int> DeployAsync(HttpClient client, IReadOnlyList<FileInfo> files, TextWriter output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(output);
        var failed = 0;
        foreach (var file in files)
        {
            var path = Endpoint(file.Extension);
            using var form = new MultipartFormDataContent();
            using var content = new ByteArrayContent(await File.ReadAllBytesAsync(file.FullName, cancellationToken).ConfigureAwait(false));
            form.Add(content, "resources", file.Name);
            using var response = await client.PostAsync(new Uri(path, UriKind.Relative), form, cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync($"{file.Name}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)}".AsMemory(), cancellationToken).ConfigureAwait(false);
            failed += response.IsSuccessStatusCode ? 0 : 1;
        }

        return failed == 0 ? 0 : 1;
    }

    public static async Task<int> StartAsync(HttpClient client, string processId, string? variablesJson, TextWriter output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(output);
        var body = $"{{\"bpmnProcessId\":{System.Text.Json.JsonSerializer.Serialize(processId)},\"variables\":{(string.IsNullOrWhiteSpace(variablesJson) ? "null" : variablesJson)}}}";
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(new Uri("/v1/process-instances", UriKind.Relative), content, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)}".AsMemory(), cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode ? 0 : 1;
    }

    /// <summary>A client for <paramref name="baseUrl"/> that sends the API key (or the bearer token) in the right header.</summary>
    public static HttpClient CreateClient(Uri baseUrl, string? credential)
    {
        var client = new HttpClient { BaseAddress = baseUrl, Timeout = TimeSpan.FromSeconds(60) };
        if (string.IsNullOrWhiteSpace(credential))
        {
            return client;
        }

        if (credential.StartsWith("flowd_", StringComparison.Ordinal))
        {
            client.DefaultRequestHeaders.Add("X-Api-Key", credential);
        }
        else
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        }

        return client;
    }

    private static string Endpoint(string extension) => extension.ToLowerInvariant() switch
    {
        ".dmn" => "/v1/decisions",
        ".form" => "/v1/forms",
        _ => "/v1/deployments", // non-union: file extension
    };
}

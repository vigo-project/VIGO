using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Vigo.TestUtilities;

/// <summary>
/// Local HTTP "Git server" answering every request with 401 + <c>WWW-Authenticate: Basic</c>: git asks its credential
/// helpers, then fails with an authentication error. Plain sockets (no HttpListener URL reservation needed).
/// </summary>
public sealed class UnauthorizedGitServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private int _requests;

    public UnauthorizedGitServer()
    {
        _listener.Start();
        _ = AcceptLoopAsync();
    }

    /// <summary>"http://127.0.0.1:port/team/project.git".</summary>
    public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/team/project.git";

    /// <summary>Requests that carried an Authorization header (credentials sent by git).</summary>
    public int AuthorizedAttempts => _requests;

    private readonly System.Collections.Concurrent.ConcurrentQueue<string?> _authorizations = new();

    /// <summary>The Authorization header of each request received, in order; null for a request without one.</summary>
    public IReadOnlyList<string?> Authorizations => [.. _authorizations];

    /// <summary>"http://127.0.0.1:port/": the prefix an <c>http.&lt;url&gt;.extraHeader</c> setting is scoped to.</summary>
    public string Origin => Url[..(Url.IndexOf("/team/", StringComparison.Ordinal) + 1)];

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }
            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var buffer = new byte[8192];
                var request = new StringBuilder();
                while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var read = await stream.ReadAsync(buffer, _stop.Token);
                    if (read == 0) return;
                    request.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }
                if (request.ToString().Contains("\nAuthorization:", StringComparison.OrdinalIgnoreCase)) Interlocked.Increment(ref _requests);
                var authorization = request.ToString().Split("\r\n").FirstOrDefault(l => l.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase));
                _authorizations.Enqueue(authorization?["Authorization:".Length..].Trim());

                var response = "HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: Basic realm=\"vigo\"\r\n" +
                               "Content-Length: 0\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _stop.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // Connection closed by git or server stopped.
            }
        }
    }
}

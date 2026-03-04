using System.Net.Http;
using System.Text;
using MightyMiniMouse.Logging;

namespace MightyMiniMouse.Actions;

public class WebhookAction : IAction
{
    private readonly string _url;
    private readonly string _httpMethod;
    private readonly string? _body;

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    public WebhookAction(string url, string httpMethod = "POST", string? body = null)
    {
        _url = url;
        _httpMethod = httpMethod.ToUpperInvariant();
        _body = body;
    }

    public async Task ExecuteAsync(CancellationToken ct = default)
    {
        try
        {
            var request = new HttpRequestMessage(new HttpMethod(_httpMethod), _url);

            if (_body != null && _httpMethod is "POST" or "PUT" or "PATCH")
            {
                request.Content = new StringContent(_body, Encoding.UTF8, "application/json");
            }

            var response = await HttpClient.SendAsync(request, ct);
            Logger.Instance.Debug($"Webhook {_httpMethod} {_url} → {(int)response.StatusCode} {response.ReasonPhrase}");
        }
        catch (Exception ex)
        {
            Logger.Instance.Error($"Webhook {_httpMethod} {_url} failed", ex);
        }
    }
}

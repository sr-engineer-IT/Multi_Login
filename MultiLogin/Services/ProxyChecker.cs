using System.Net;
using System.Net.Http.Json;
using MultiLogin.Models;

namespace MultiLogin.Services;

public record ProxyCheckResult(bool Ok, string Message, string? Timezone = null);

public static class ProxyChecker
{
    private record IpInfo(string? Ip, string? City, string? Region, string? Country, string? Timezone, string? Org);

    /// <summary>
    /// Fetches the public IP as seen through the profile's proxy. Goes through the same
    /// local forwarder the browser uses, so a passing check means the browser will work too.
    /// </summary>
    public static async Task<ProxyCheckResult> CheckAsync(Profile profile)
    {
        try
        {
            using var forwarder = profile.HasProxy ? new ProxyForwarder(profile) : null;
            using var handler = new HttpClientHandler
            {
                Proxy = forwarder != null ? new WebProxy(forwarder.LocalProxyUrl) : null,
                UseProxy = forwarder != null
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            var info = await http.GetFromJsonAsync<IpInfo>("https://ipinfo.io/json");
            if (info?.Ip == null) return new(false, "No response.");

            var place = string.Join(", ", new[] { info.City, info.Region, info.Country }.Where(s => !string.IsNullOrEmpty(s)));
            return new(true, $"{info.Ip}  —  {place}  —  {info.Timezone}  ({info.Org})", info.Timezone);
        }
        catch (Exception ex)
        {
            return new(false, ex.GetBaseException().Message);
        }
    }
}

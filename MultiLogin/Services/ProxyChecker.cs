using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using MultiLogin.Models;

namespace MultiLogin.Services;

public record ProxyCheckResult(bool Ok, string Message, string? Timezone = null, double? Latitude = null, double? Longitude = null);

public static class ProxyChecker
{
    private record IpInfo(string? Ip, string? City, string? Region, string? Country, string? Loc, string? Timezone, string? Org);

    /// <summary>
    /// Fetches the public IP as seen through the profile's proxy. Goes through the same
    /// local forwarder the browser uses, so a passing check means the browser will work too.
    /// </summary>
    public static async Task<ProxyCheckResult> CheckAsync(Profile profile)
    {
        try
        {
            using var forwarder = profile.HasProxy ? new ProxyForwarder(profile) : null;
            return await LookupAsync(forwarder?.LocalProxyUrl);
        }
        catch (Exception ex)
        {
            return new(false, ex.GetBaseException().Message);
        }
    }

    /// <summary>Looks up the public IP, location and timezone, optionally through a proxy URL.</summary>
    public static async Task<ProxyCheckResult> LookupAsync(string? proxyUrl)
    {
        try
        {
            using var handler = new HttpClientHandler
            {
                Proxy = proxyUrl != null ? new WebProxy(proxyUrl) : null,
                UseProxy = proxyUrl != null
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            var info = await http.GetFromJsonAsync<IpInfo>("https://ipinfo.io/json");
            if (info?.Ip == null) return new(false, "No response.");

            double? lat = null, lon = null;
            var loc = info.Loc?.Split(',');
            if (loc is [var a, var b]
                && double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var la)
                && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var lo))
                (lat, lon) = (la, lo);

            var place = string.Join(", ", new[] { info.City, info.Region, info.Country }.Where(s => !string.IsNullOrEmpty(s)));
            return new(true, $"{info.Ip}  —  {place}  —  {info.Timezone}  ({info.Org})", info.Timezone, lat, lon);
        }
        catch (Exception ex)
        {
            return new(false, ex.GetBaseException().Message);
        }
    }
}

using System.Security.Cryptography;
using System.Text;

namespace SocialPoster.Core;

/// <summary>OAuth 1.0a (HMAC-SHA1) request signing for the X API in user context.</summary>
public static class OAuth1
{
    public sealed record Credentials(string ConsumerKey, string ConsumerSecret, string AccessToken, string AccessTokenSecret);

    public static string Encode(string value) => Uri.EscapeDataString(value);

    /// <param name="url">Base URL without a query string.</param>
    /// <param name="parameters">Query-string and form parameters. JSON bodies are not signed.</param>
    public static string CreateAuthorizationHeader(
        string method,
        string url,
        IEnumerable<KeyValuePair<string, string>> parameters,
        Credentials credentials,
        string? nonce = null,
        string? timestamp = null)
    {
        nonce ??= Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        timestamp ??= DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

        var oauth = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["oauth_consumer_key"] = credentials.ConsumerKey,
            ["oauth_nonce"] = nonce,
            ["oauth_signature_method"] = "HMAC-SHA1",
            ["oauth_timestamp"] = timestamp,
            ["oauth_token"] = credentials.AccessToken,
            ["oauth_version"] = "1.0",
        };

        var signature = Sign(method, url, parameters.Concat(oauth), credentials.ConsumerSecret, credentials.AccessTokenSecret);
        oauth["oauth_signature"] = signature;

        return "OAuth " + string.Join(", ", oauth.Select(kv => $"{Encode(kv.Key)}=\"{Encode(kv.Value)}\""));
    }

    public static string Sign(
        string method,
        string url,
        IEnumerable<KeyValuePair<string, string>> allParameters,
        string consumerSecret,
        string tokenSecret)
    {
        var normalized = string.Join("&", allParameters
            .Select(kv => (Key: Encode(kv.Key), Value: Encode(kv.Value)))
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .ThenBy(p => p.Value, StringComparer.Ordinal)
            .Select(p => $"{p.Key}={p.Value}"));

        var baseString = $"{method.ToUpperInvariant()}&{Encode(url)}&{Encode(normalized)}";
        var key = $"{Encode(consumerSecret)}&{Encode(tokenSecret)}";

        using var hmac = new HMACSHA1(Encoding.ASCII.GetBytes(key));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.ASCII.GetBytes(baseString)));
    }
}

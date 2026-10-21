# SocialPoster.Posting

Post text and media to **X**, **Bluesky**, **LinkedIn**, **Mastodon** and **Threads** from any .NET app. No database,
UI or ASP.NET required: it needs only `HttpClient` and a place to read your tokens from.

This is the posting engine behind [SocialPoster](https://github.com/isaacrlevin/SocialPoster), the desktop/Blazor app.

## Quick start

```csharp
using Microsoft.Extensions.DependencyInjection;
using SocialPoster.Posting;
using SocialPoster.Posting.Services;

var services = new ServiceCollection();
services.AddSocialPosting();
services.AddSingleton<ICredentialStore>(new InMemoryCredentialStore(new Dictionary<string, string?>
{
    [SettingKeys.BlueskyHandle]      = "you.bsky.social",
    [SettingKeys.BlueskyAppPassword] = "xxxx-xxxx-xxxx-xxxx",
    [SettingKeys.MastodonInstance]   = "https://mastodon.social/",
    [SettingKeys.MastodonAccessToken] = "...",
}));

await using var provider = services.BuildServiceProvider();
var publisher = provider.GetRequiredService<SocialPublisher>();

var results = await publisher.PublishTextAsync("Hello from .NET!", [Platform.Bluesky, Platform.Mastodon]);
foreach (var r in results)
    Console.WriteLine(r.Success ? $"{r.Platform}: {r.Url}" : $"{r.Platform} failed: {r.Error}");
```

Each platform is rendered, validated and published independently. A failure on one platform (bad token, post too long,
unsupported media) is returned as a `PublishResult` with `Success = false`; it never throws or stops the others.

## Credentials

Clients read tokens through `ICredentialStore`, using the keys in `SettingKeys`:

| Platform | Keys |
|----------|------|
| Bluesky  | `BlueskyHandle`, `BlueskyAppPassword`, optional `BlueskyService` |
| X        | `XConsumerKey`, `XConsumerSecret`, `XAccessToken`, `XAccessTokenSecret` (or `XPostMethod = "intent"` for a browser link, no API) |
| LinkedIn | `LinkedInAccessToken`, `LinkedInPersonUrn`, optional `LinkedInApiVersion` |
| Mastodon | `MastodonInstance`, `MastodonAccessToken` |
| Threads  | `ThreadsAccessToken`, `ThreadsUserId` (or call `ThreadsClient.ConfigureAsync(token)` to verify and fill both) |

`InMemoryCredentialStore` is the default. To use your own vault, key store or config, implement `ICredentialStore`
and register it before or after `AddSocialPosting()` (yours wins).

## Posting with media, mentions and previews

- `publisher.PublishAsync(templates, resolve, media)` renders a template per platform. `{@key}` mentions are resolved
  by your `ContactResolver` to each platform's handle format (`@user@server` on Mastodon, a real facet on Bluesky,
  a LinkedIn URN, and so on) and fall back to a plain name when a contact has no account there.
- `PostRenderer.Render(platform, template, resolve)` gives you the exact text, character count and warnings without
  posting, which is handy for live previews.
- `MediaAttachment` / `MediaRules` check per-platform limits (counts, sizes, formats). **Threads is text only**: its
  API accepts media only by public URL, not uploads.
- Counting follows each platform's rules (X weighted characters and 23-character URLs, Bluesky graphemes with
  shortened links, Mastodon/Threads graphemes).

Individual clients (`BlueskyClient`, `XClient`, `LinkedInClient`, `MastodonClient`, `ThreadsClient`) are public if you
want to call one directly.

## Notes

- Everything lives in the `SocialPoster.Posting` and `SocialPoster.Posting.Services` namespaces. The SocialPoster app
  (`SocialPoster.Core`) is built on these same types.
- Tokens are sent only to the platform's own host; Mastodon and Threads clients never follow redirects.
- `HttpClient` timeout defaults to 30 minutes for large uploads: `AddSocialPosting(o => o.PostingTimeout = ...)`.

MIT licensed.

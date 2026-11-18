# SocialPoster Privacy Policy

_Effective date: November 6, 2026_

<!-- Keep in sync with docs/privacy.html, which is the published copy linked from the Microsoft Store listing. -->

SocialPoster is an open-source app that publishes posts to X, Bluesky, LinkedIn, Mastodon and Threads. This policy
covers the SocialPoster desktop app (from the [Microsoft Store](https://apps.microsoft.com/detail/9mt2w98x8z2q) or GitHub releases) and the SocialPoster Web project when
you run it yourself.

## The short version

- SocialPoster runs on your computer. **The developer doesn't run any servers for it and receives none of your data.**
- There is **no telemetry, analytics, advertising or tracking** in the app.
- Your accounts, contacts, posts and settings are stored **only on your computer**, and credentials are encrypted.
- Data leaves your computer only when you use a feature that needs it, and only goes to the service that feature uses
  (for example, a post goes to the platforms you publish it to).

## Information stored on your computer

SocialPoster keeps its data in a local folder: `%LOCALAPPDATA%\SocialPoster` on Windows,
`~/Library/Application Support/SocialPoster` on macOS, or `~/.local/share/SocialPoster` on Linux. The Microsoft
Store version uses its own app data folder instead, which Windows deletes when you uninstall it. It contains:

- **Credentials and settings** that you enter, such as platform access tokens, app passwords, API keys for AI and web
  search providers, and a GitHub token. These are encrypted with ASP.NET Core Data Protection. On Windows the
  encryption keys are also protected with Windows DPAPI, tied to your Windows account.
- **Contacts**: the people you mention or import, with their display name, profile picture URL, bio, and their
  usernames, handles and IDs on each platform.
- **Post history and scheduled posts**: the text of posts you publish or schedule, per-platform results, and copies of
  media attached to scheduled posts.
- **Account finder results**: the searches the AI account finder ran and the accounts it suggested.

The app also writes a startup error log to `~/.socialposter` (Windows) or `~/Library/Logs/SocialPoster` (macOS) if it
fails to start, and saves exports to your Downloads folder when you ask it to. These files stay on your computer and
are never uploaded.

## Information sent to other services

SocialPoster talks directly from your computer to the services below, and only when you use the related feature.
Each service handles the data under its own privacy policy.

- **Social platforms you connect** (X, Bluesky, LinkedIn, Mastodon instances you choose, Threads): the posts and media
  you publish, the credentials needed to sign in, searches when you look someone up, and requests to import the
  accounts you follow.
- **Link previews**: when a post contains a link, SocialPoster asks Bluesky's link card service (`cardyb.bsky.app`)
  for the page's title, description and image, or reads them from the linked page itself.
- **AI features (optional, off until you set them up)**: the account finder and "Draft from a link" send the relevant
  text, such as a contact's name and known accounts, or a link and its page content, to the AI provider you choose.
  That is GitHub Copilot (using your GitHub sign-in) or a provider you add, such as OpenAI, Microsoft Foundry,
  OpenRouter, Groq, or a model running locally through Ollama or LM Studio. If automatic search is on, a search runs
  when you mention a contact who has no X or LinkedIn account yet.
- **Web search (optional)**: if you configure Brave Search, Tavily or a SearXNG instance, the AI agent sends search
  queries to it and may open the public web pages it finds.
- **Your web browser**: links you open, and X posts in browser mode, open in your default browser.

SocialPoster doesn't sell, rent or share your information with anyone else, and doesn't use it for advertising.

## Microsoft Store

If you install SocialPoster from the Microsoft Store, Microsoft handles the download, installation and updates under
the [Microsoft Privacy Statement](https://privacy.microsoft.com/privacystatement). Microsoft may give the developer
aggregated, non-identifying reports through Partner Center, such as install counts and crash reports collected by
Windows. The app itself sends nothing to the developer.

## Your choices and deleting your data

- Remove a credential with **Forget token** (or by clearing it) on the Accounts page. This deletes the local copy
  only. To revoke access fully, also revoke the token or app password on the platform itself.
- Delete contacts individually, by platform (**Purge**), or all at once on the Contacts page.
- Turn off the AI account finder, or just its automatic search, under **Accounts → Account finder agent**.
- To delete everything, close the app and delete the data folder listed above. **Uninstalling the app doesn't delete
  this folder**, so your data is kept if you reinstall or also use the Web project.

## Security

Credentials are encrypted at rest, and Mastodon and Threads tokens are sent only to their own servers. The Web project
has no login, so run it only on `localhost`. Anyone with access to your user account on your computer can read the
data folder, so protect it as you would any other private files.

## Children

SocialPoster isn't directed at children under 13, and the developer doesn't knowingly collect information from them.

## Changes to this policy

If this policy changes, the updated version will be published at the same address with a new effective date. Changes
are also visible in the project's [Git history](https://github.com/isaacrlevin/SocialPoster/commits/main/PRIVACY.md).

## Contact

Questions about this policy can be asked by opening an issue at
[github.com/isaacrlevin/SocialPoster/issues](https://github.com/isaacrlevin/SocialPoster/issues).

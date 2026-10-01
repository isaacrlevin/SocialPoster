using SocialPoster.Core;

namespace SocialPoster.Tests;

public class AccountCandidateTests
{
    private static ReportedCandidate R(string platform, string url, int confidence = 50, string? orgId = null, string? kind = null) =>
        new(platform, kind, url, "Name", orgId, confidence, "reason");

    [Theory]
    [InlineData("https://x.com/pfrazee", "pfrazee")]
    [InlineData("https://twitter.com/pfrazee/status/123", "pfrazee")]
    [InlineData("https://mobile.twitter.com/@pfrazee?lang=en", "pfrazee")]
    [InlineData("@pfrazee", "pfrazee")]
    public void X_urls_are_reduced_to_a_handle(string url, string handle)
    {
        var c = Assert.Single(AccountCandidates.Normalize([R("X", url)]));
        Assert.Equal(Platform.X, c.Platform);
        Assert.Equal(handle, c.Handle);
        Assert.Equal($"https://x.com/{handle}", c.Url);
    }

    [Theory]
    [InlineData("https://x.com/search?q=paul")]
    [InlineData("https://x.com/home")]
    [InlineData("https://example.com/pfrazee")]
    [InlineData("https://x.com/this_is_way_too_long_for_x")]
    public void Invalid_x_urls_are_dropped(string url) => Assert.Empty(AccountCandidates.Normalize([R("X", url)]));

    [Fact]
    public void LinkedIn_person_and_company_urls_are_classified()
    {
        var list = AccountCandidates.Normalize([
            R("LinkedIn", "https://uk.linkedin.com/in/jane-doe-123/?trk=x"),
            R("LinkedIn", "https://www.linkedin.com/company/microsoft/", orgId: "1035"),
        ]);

        var person = list.Single(c => c.Kind == AccountKind.Person);
        Assert.Equal("jane-doe-123", person.Handle);
        Assert.Equal("https://www.linkedin.com/in/jane-doe-123", person.Url);
        Assert.Null(person.LinkedInUrn);

        var org = list.Single(c => c.Kind == AccountKind.Organization);
        Assert.Equal("urn:li:organization:1035", org.LinkedInUrn);
    }

    [Fact]
    public void Numeric_company_slug_becomes_the_org_id_and_bad_ids_are_ignored()
    {
        var list = AccountCandidates.Normalize([
            R("LinkedIn", "https://www.linkedin.com/company/1035"),
            R("LinkedIn", "https://www.linkedin.com/company/contoso", orgId: "urn:li:organization:9"),
            R("LinkedIn", "https://www.linkedin.com/in/someone", orgId: "42"),
        ]);

        Assert.Equal("1035", list.Single(c => c.Handle == "1035").OrganizationId);
        Assert.Null(list.Single(c => c.Handle == "contoso").OrganizationId);
        Assert.Null(list.Single(c => c.Handle == "someone").OrganizationId);
    }

    [Fact]
    public void Duplicates_are_removed_and_results_sorted_by_confidence_and_capped()
    {
        var list = AccountCandidates.Normalize([
            R("X", "https://x.com/a", 10),
            R("X", "https://twitter.com/A", 90),
            R("X", "https://x.com/b", 80),
            R("X", "https://x.com/c", 70),
        ], maxPerPlatform: 2);

        Assert.Equal(["A", "b"], list.Select(c => c.Handle));
        Assert.Equal(90, list[0].Confidence);
    }

    [Fact]
    public void Contact_reports_missing_platforms_and_manual_tag_need()
    {
        var c = new Contact { DisplayName = "Jane", LinkedInProfileUrl = "https://www.linkedin.com/in/jane" };
        Assert.Equal([Platform.X], c.MissingSearchablePlatforms());
        Assert.True(c.NeedsManualLinkedInTag);

        c.LinkedInUrn = "urn:li:person:abc";
        Assert.False(c.NeedsManualLinkedInTag);
    }
}

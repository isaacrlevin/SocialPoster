using SocialPoster.Core;

namespace SocialPoster.Tests;

public class UtilityTests
{
    [Theory]
    [InlineData("satyanadella.bsky.social", "satyanadella")]
    [InlineData("@jay.bsky.team", "jay.bsky.team")]
    [InlineData("Satya Nadella", "satya-nadella")]
    [InlineData("José Pérez!", "jose-perez")]
    [InlineData("  ", "contact")]
    public void Slug_Create(string input, string expected) => Assert.Equal(expected, Slugs.Create(input));

    [Fact]
    public void Slug_MakeUnique()
    {
        var taken = new HashSet<string> { "sam", "sam-2" };
        Assert.Equal("sam-3", Slugs.MakeUnique("sam", taken.Contains));
    }

    [Theory]
    [InlineData("jay.bsky.team", "jay")]
    [InlineData("@satyanadella.bsky.social", "satyanadella")]
    [InlineData("pfrazee.com", "pfrazee")]
    [InlineData("Microsoft", "microsoft")]
    public void Slug_FromHandle(string handle, string expected) => Assert.Equal(expected, Slugs.FromHandle(handle));

    [Fact]
    public void ProfileLinks_AreExtracted()
    {
        var hints = ProfileLinkExtractor.Extract(
            "Dev at Contoso. https://x.com/jdoe_dev • linkedin.com/in/jane-doe • https://www.linkedin.com/company/contoso/");
        Assert.Equal("jdoe_dev", hints.XUsername);
        Assert.Equal("https://www.linkedin.com/in/jane-doe", hints.LinkedInProfileUrl);
        Assert.Equal("contoso", hints.LinkedInCompanyVanity);
    }

    [Fact]
    public void ProfileLinks_InlineXHandle()
    {
        Assert.Equal("jdoe", ProfileLinkExtractor.Extract("Also on Twitter: @jdoe").XUsername);
        Assert.Null(ProfileLinkExtractor.Extract("see x.com/home").XUsername);
    }

    [Theory]
    [InlineData("urn:li:person:abc-123", true)]
    [InlineData("urn:li:organization:1035", true)]
    [InlineData("urn:li:group:1", false)]
    [InlineData("https://linkedin.com/in/x", false)]
    public void LinkedInUrnValidation(string urn, bool valid) => Assert.Equal(valid, LinkedInText.IsValidMentionUrn(urn));

    [Fact]
    public void OAuth1_MatchesPublishedTwitterExample()
    {
        // Reference example from X's "Creating a signature" documentation.
        var parameters = new Dictionary<string, string>
        {
            ["status"] = "Hello Ladies + Gentlemen, a signed OAuth request!",
            ["include_entities"] = "true",
            ["oauth_consumer_key"] = "xvz1evFS4wEEPTGEFPHBog",
            ["oauth_nonce"] = "kYjzVBB8Y0ZFabxSWbWovY3uYSQ2pTgmZeNu2VS4cg",
            ["oauth_signature_method"] = "HMAC-SHA1",
            ["oauth_timestamp"] = "1318622958",
            ["oauth_token"] = "370773112-GmHxMAgYyLbNEtIKZeRNFsMKPR9EyMZeS9weJAEb",
            ["oauth_version"] = "1.0",
        };

        var signature = OAuth1.Sign(
            "POST",
            "https://api.twitter.com/1.1/statuses/update.json",
            parameters,
            "kAcSOqF21Fu85e7zjz7ZN2U4ZRhfV3WpwPAoE3Z7kBw",
            "LswwdoUaIvS8ltyTt5jkRh4J50vUPVVHtR2YPi5kE");

        Assert.Equal("hCtSmYh+iHYCEqBWrE7C7hYmtUk=", signature);
    }
}

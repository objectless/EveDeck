using Xunit;
using EveDeck.Models;
using EveDeck.Utilities;

namespace EveDeck.Tests;

public class IntelSystemTokenizerTests
{
    private static SystemJumpGraph BuildTestGraph()
    {
        var nodes = new[]
        {
            Node(30000142, "Jita"),
            Node(30000144, "Perimeter"),
            Node(30045349, "Old Man Star"), // 3-word real system name
            Node(30003524, "Sun"),          // deliberately looks like an ordinary English word
        };
        return new SystemJumpGraph(nodes);
    }

    private static SystemNode Node(int id, string name) => new() { Id = id, Name = name };

    private static ShipTypeDictionary BuildTestShipDictionary() => new(new[]
    {
        new ShipTypeEntry(11567, "Tholos"),
        new ShipTypeEntry(29988, "Loki"),
        new ShipTypeEntry(17703, "Imperial Navy Slicer"),
        new ShipTypeEntry(587, "Rifter"),
        new ShipTypeEntry(17720, "Cyclone Fleet Issue"),
        new ShipTypeEntry(17635, "Ferox Navy Issue"),
    });

    [Fact]
    public void FindsSingleWordSystemName()
    {
        var hits = IntelSystemTokenizer.FindSystemMentions("hostiles spotted in Jita right now", BuildTestGraph());
        Assert.Equal(new[] { "Jita" }, hits);
    }

    [Fact]
    public void FindsMultiWordSystemName_AsOneMatch_NotFragments()
    {
        var hits = IntelSystemTokenizer.FindSystemMentions("camp forming at Old Man Star", BuildTestGraph());
        Assert.Equal(new[] { "Old Man Star" }, hits);
    }

    [Fact]
    public void FindsMultipleDistinctSystems_InFirstSeenOrder()
    {
        var hits = IntelSystemTokenizer.FindSystemMentions("came from Perimeter heading to Jita", BuildTestGraph());
        Assert.Equal(new[] { "Perimeter", "Jita" }, hits);
    }

    [Fact]
    public void DoesNotDuplicate_SameSystemMentionedTwice()
    {
        var hits = IntelSystemTokenizer.FindSystemMentions("Jita is safe, Jita is fine", BuildTestGraph());
        Assert.Equal(new[] { "Jita" }, hits);
    }

    [Fact]
    public void IsCaseInsensitive()
    {
        var hits = IntelSystemTokenizer.FindSystemMentions("hostiles in JITA and old man star", BuildTestGraph());
        Assert.Equal(new[] { "Jita", "Old Man Star" }, hits);
    }

    [Fact]
    public void StripsSurroundingPunctuation()
    {
        var hits = IntelSystemTokenizer.FindSystemMentions("who's in Jita? camping (Perimeter).", BuildTestGraph());
        Assert.Equal(new[] { "Jita", "Perimeter" }, hits);
    }

    [Fact]
    public void OrdinaryWordThatHappensToMatchASystemName_StillMatches()
    {
        // "Sun" is deliberately in the dictionary to document the tokenizer's actual behavior: it is
        // a pure name lookup with no semantic filtering, so an ordinary word that happens to also be
        // a real system name is indistinguishable from an intentional mention. Not a bug -- RIFT's
        // own tokenizer has the same property; a region hint could disambiguate in a future pass.
        var hits = IntelSystemTokenizer.FindSystemMentions("gonna go sit in the sun today", BuildTestGraph());
        Assert.Equal(new[] { "Sun" }, hits);
    }

    [Fact]
    public void NoMatches_ReturnsEmptyList_NotNull()
    {
        var hits = IntelSystemTokenizer.FindSystemMentions("nothing relevant here at all", BuildTestGraph());
        Assert.Empty(hits);
    }

    [Fact]
    public void EmptyOrWhitespaceLine_ReturnsEmptyList()
    {
        Assert.Empty(IntelSystemTokenizer.FindSystemMentions("", BuildTestGraph()));
        Assert.Empty(IntelSystemTokenizer.FindSystemMentions("   ", BuildTestGraph()));
    }

    [Fact]
    public void EmptyGraph_ReturnsEmptyList_WithoutThrowing()
    {
        var hits = IntelSystemTokenizer.FindSystemMentions("hostiles in Jita", new SystemJumpGraph(Array.Empty<SystemNode>()));
        Assert.Empty(hits);
    }

    [Fact]
    public void TrailingText_BareSystemNameMention_IsEmpty()
    {
        var hits = IntelSystemTokenizer.FindSystemMentionsWithTrailingText("hostiles in Jita", BuildTestGraph());
        var mention = Assert.Single(hits);
        Assert.Equal("Jita", mention.SystemName);
        Assert.Equal("", mention.TrailingText);
    }

    [Fact]
    public void TrailingText_CapturesShipNameAfterSystem()
    {
        var hits = IntelSystemTokenizer.FindSystemMentionsWithTrailingText("Jita Loki", BuildTestGraph());
        var mention = Assert.Single(hits);
        Assert.Equal("Jita", mention.SystemName);
        Assert.Equal("Loki", mention.TrailingText);
    }

    [Fact]
    public void TrailingText_MultipleSystemsOnOneLine_EachBoundedToTheNext()
    {
        var hits = IntelSystemTokenizer.FindSystemMentionsWithTrailingText("Jita Loki nv Perimeter clear", BuildTestGraph());
        Assert.Equal(2, hits.Count);
        Assert.Equal("Jita", hits[0].SystemName);
        Assert.Equal("Loki nv", hits[0].TrailingText);
        Assert.Equal("Perimeter", hits[1].SystemName);
        Assert.Equal("clear", hits[1].TrailingText);
    }

    [Fact]
    public void TrailingText_MultiWordSystemName_TrailingStartsAfterFullName()
    {
        var hits = IntelSystemTokenizer.FindSystemMentionsWithTrailingText("camp at Old Man Star nv", BuildTestGraph());
        var mention = Assert.Single(hits);
        Assert.Equal("Old Man Star", mention.SystemName);
        Assert.Equal("nv", mention.TrailingText);
    }

    [Theory]
    [InlineData("nv")]
    [InlineData("NV")]
    [InlineData("no visual")]
    [InlineData("No Visual")]
    public void ClassifyTrailingText_NoVisualVariants(string trailing)
    {
        var (kind, detail) = IntelSystemTokenizer.ClassifyTrailingText(trailing);
        Assert.Equal(IntelReportKind.NoVisual, kind);
        Assert.Null(detail);
    }

    [Theory]
    [InlineData("clear")]
    [InlineData("CLEAR")]
    [InlineData("clr")]
    public void ClassifyTrailingText_ClearVariants(string trailing)
    {
        var (kind, detail) = IntelSystemTokenizer.ClassifyTrailingText(trailing);
        Assert.Equal(IntelReportKind.Clear, kind);
        Assert.Null(detail);
    }

    [Fact]
    public void ClassifyTrailingText_ArbitraryText_IsSightingWithDetail()
    {
        var (kind, detail) = IntelSystemTokenizer.ClassifyTrailingText("Loki Tengu");
        Assert.Equal(IntelReportKind.Sighting, kind);
        Assert.Equal("Loki Tengu", detail);
    }

    [Fact]
    public void ClassifyTrailingText_Empty_IsSightingWithNoDetail()
    {
        var (kind, detail) = IntelSystemTokenizer.ClassifyTrailingText("");
        Assert.Equal(IntelReportKind.Sighting, kind);
        Assert.Null(detail);
    }

    [Fact]
    public void ResolvePilotAndShip_PilotThenShip()
    {
        var (ship, remainder) = IntelSystemTokenizer.ResolvePilotAndShip("Ultrabug Tholos", BuildTestShipDictionary());
        Assert.Equal("Tholos", ship?.Name);
        Assert.Equal(11567, ship?.Id);
        Assert.Equal("Ultrabug", remainder);
    }

    [Fact]
    public void ResolvePilotAndShip_ShipThenPilot()
    {
        var (ship, remainder) = IntelSystemTokenizer.ResolvePilotAndShip("Tholos Ultrabug", BuildTestShipDictionary());
        Assert.Equal("Tholos", ship?.Name);
        Assert.Equal("Ultrabug", remainder);
    }

    [Fact]
    public void ResolvePilotAndShip_MultiWordShipName()
    {
        var (ship, remainder) = IntelSystemTokenizer.ResolvePilotAndShip("Ultrabug Imperial Navy Slicer", BuildTestShipDictionary());
        Assert.Equal("Imperial Navy Slicer", ship?.Name);
        Assert.Equal("Ultrabug", remainder);
    }

    [Fact]
    public void ResolvePilotAndShip_ShipOnly_NoRemainder()
    {
        var (ship, remainder) = IntelSystemTokenizer.ResolvePilotAndShip("Tholos", BuildTestShipDictionary());
        Assert.Equal("Tholos", ship?.Name);
        Assert.Null(remainder);
    }

    [Fact]
    public void ResolvePilotAndShip_NoKnownShip_ReturnsNullShipAndWholePhraseAsRemainder()
    {
        var (ship, remainder) = IntelSystemTokenizer.ResolvePilotAndShip("Ultrabug Somename", BuildTestShipDictionary());
        Assert.Null(ship);
        Assert.Equal("Ultrabug Somename", remainder);
    }

    [Fact]
    public void ResolvePilotAndShip_IsCaseInsensitive()
    {
        var (ship, remainder) = IntelSystemTokenizer.ResolvePilotAndShip("Ultrabug THOLOS", BuildTestShipDictionary());
        Assert.Equal("Tholos", ship?.Name); // canonical casing from the dictionary, not the input's casing
        Assert.Equal("Ultrabug", remainder);
    }

    [Fact]
    public void ResolvePilotAndShip_EmptyDictionary_ReturnsNullShipAndWholePhrase()
    {
        var (ship, remainder) = IntelSystemTokenizer.ResolvePilotAndShip("Ultrabug Tholos", new ShipTypeDictionary(Array.Empty<ShipTypeEntry>()));
        Assert.Null(ship);
        Assert.Equal("Ultrabug Tholos", remainder);
    }

    [Theory]
    [InlineData("Ultrabug CFI", "Cyclone Fleet Issue", "Ultrabug")]
    [InlineData("Ultrabug FNI", "Ferox Navy Issue", "Ultrabug")]
    [InlineData("cfi", "Cyclone Fleet Issue", null)]
    public void ResolvePilotAndShip_RecognizesFactionShipAbbreviations(string phrase, string expectedShip, string? expectedRemainder)
    {
        var (ship, remainder) = IntelSystemTokenizer.ResolvePilotAndShip(phrase, BuildTestShipDictionary());
        Assert.Equal(expectedShip, ship?.Name);
        Assert.Equal(expectedRemainder, remainder);
    }

    [Fact]
    public void ShipTypeDictionary_AmbiguousAcronym_IsNotResolved()
    {
        // Two different ships that reduce to the same initials ("Alpha Beta Charlie" and "Ares
        // Bravo Corvette" both -> ABC) must not resolve the abbreviation to either -- guessing
        // wrong is worse than not recognizing it at all.
        var ships = new ShipTypeDictionary(new[]
        {
            new ShipTypeEntry(1, "Alpha Beta Charlie"),
            new ShipTypeEntry(2, "Ares Bravo Corvette"),
        });
        Assert.Null(ships.Resolve("ABC"));
    }
}

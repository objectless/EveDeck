using EveDeck.Services;
using Xunit;

namespace EveDeck.Tests;

// Fixtures below are VERBATIM lines captured from real Pidgin 2.x HTML conversation logs (both a
// 1:1 IM and a MUC room) -- not invented approximations -- so these tests pin the parser to the
// actual on-disk format rather than an assumed one.
public class JabberPingWatcherServiceTests
{
    [Fact]
    public void ParsesSimpleMessage_GroupChat_FullDateTimeHeader()
    {
        var line = @"<span style=""color: #A82F2F""><span style=""font-size: smaller"">(4/4/2026 9:54:18 AM)</span> <b>caroline_solette:</b></span> Have fun with PDS<br>";

        var result = JabberPingWatcherService.TryParseMessageLine(line);

        Assert.NotNull(result);
        Assert.Equal("caroline_solette", result!.Value.Sender);
        Assert.Equal("Have fun with PDS", result.Value.Body);
    }

    [Fact]
    public void ParsesSimpleMessage_OneOnOne_TimeOnlyHeader()
    {
        var line = @"<span style=""color: #A82F2F""><span style=""font-size: smaller"">(4:57:11 PM)</span> <b>GoonPinger:</b></span> quick homedef<br>";

        var result = JabberPingWatcherService.TryParseMessageLine(line);

        Assert.NotNull(result);
        Assert.Equal("GoonPinger", result!.Value.Sender);
        Assert.Equal("quick homedef", result.Value.Body);
    }

    [Fact]
    public void MultiFieldPing_BreaksBecomeNewlines_ZeroWidthPaddingCollapsesToOneSpace()
    {
        // Real captured GoonPinger ping: label:value pairs padded with a long run of zero-width
        // Unicode characters (ZWJ/BOM/etc) instead of a literal space, used to fake a tab stop.
        var line = "<span style=\"color: #A82F2F\"><span style=\"font-size: smaller\">(4:57:11 PM)</span> <b>GoonPinger:</b></span> " +
                   "To cite a great Astronaut &quot;Words of Encouragement&quot;<br/>Sub Cap only<br/><br/>" +
                   "Formup:‍‍‍‍‍‍‍﻿﻿‍‍‍‍‍‍‍ C-J6MT<br/>" +
                   "FC:‍‍‍‍‍‍‍﻿﻿‍‍‍‍‍‍‍ Vivien Saken<br/>" +
                   "~~~ This was a gice broadcast from thanatos_harbinger to incursions at 2026-07-08 20:57:11.911331 EVE ~~~<br>";

        var result = JabberPingWatcherService.TryParseMessageLine(line);

        Assert.NotNull(result);
        Assert.Equal("GoonPinger", result!.Value.Sender);
        var lines = result.Value.Body.Split('\n');
        Assert.Contains("To cite a great Astronaut \"Words of Encouragement\"", lines);
        Assert.Contains("Formup: C-J6MT", lines);
        Assert.Contains("FC: Vivien Saken", lines);
        Assert.Contains("~~~ This was a gice broadcast", result.Value.Body);
    }

    [Fact]
    public void StripsXhtmlLinkWrapper_KeepsInnerText()
    {
        var line = @"<span style=""color: #A82F2F""><span style=""font-size: smaller"">(8:56:09 PM)</span> <b>nemahs_aideron:</b></span> <html xmlns='http://jabber.org/protocol/xhtml-im'><body xmlns='http://www.w3.org/1999/xhtml'><p><a href=""https://goonfleet.com/index.php/topic/379787"">https://goonfleet.com/index.php/topic/379787</a> See: Mid-grade amulet replacement</p></body></html><br>";

        var result = JabberPingWatcherService.TryParseMessageLine(line);

        Assert.NotNull(result);
        Assert.DoesNotContain("<", result!.Value.Body);
        Assert.DoesNotContain(">", result.Value.Body);
        Assert.Contains("https://goonfleet.com/index.php/topic/379787", result.Value.Body);
        Assert.Contains("See: Mid-grade amulet replacement", result.Value.Body);
    }

    [Theory]
    [InlineData(@"<span style=""font-size: smaller"">(5:44:24 PM)</span><b> tallyos [<em>tallyos@goonfleet.com/a1d5047e-d9bc-4061-ba85-0c9b14ff01a5</em>] entered the room.</b><br>")]
    [InlineData(@"<span style=""font-size: smaller"">(5:44:26 PM)</span><b> 0rigin left the room.</b><br>")]
    public void SystemLines_JoinAndLeave_AreNotParsedAsMessages(string line)
    {
        Assert.Null(JabberPingWatcherService.TryParseMessageLine(line));
    }

    [Fact]
    public void TopicChangeFragment_IsNotParsedAsMessage()
    {
        // The topic-change block itself spans several REAL newlines in the file (the topic text
        // contains literal line breaks, not <br/> tags) -- each fragment line individually must not
        // false-positive match as a real (colored-span) message.
        var line = @"<span style=""font-size: smaller"">(4/3/2026 4:09:21 PM)</span><b> xiopa_charante has set the topic to:   Beehive is  offline.";
        Assert.Null(JabberPingWatcherService.TryParseMessageLine(line));
    }

    [Fact]
    public void BlankAndWhitespaceLines_ReturnNull()
    {
        Assert.Null(JabberPingWatcherService.TryParseMessageLine(""));
        Assert.Null(JabberPingWatcherService.TryParseMessageLine("   "));
        Assert.Null(JabberPingWatcherService.TryParseMessageLine("<p>"));
    }

    [Fact]
    public void HtmlEntities_AreDecoded()
    {
        var line = @"<span style=""color: #A82F2F""><span style=""font-size: smaller"">(1:00:00 PM)</span> <b>foo:</b></span> Bring &gt;5 DPS ships, don&#39;t be shy &amp; undock<br>";

        var result = JabberPingWatcherService.TryParseMessageLine(line);

        Assert.NotNull(result);
        Assert.Equal("Bring >5 DPS ships, don't be shy & undock", result!.Value.Body);
    }

    [Fact]
    public void TryExtractCommsChannel_FindsFieldAmongOthers()
    {
        var body = "Sub Cap only\nFormup: C-J6MT\nComms: Fleet 1\nFC: Vivien Saken";
        Assert.Equal("Fleet 1", JabberPingWatcherService.TryExtractCommsChannel(body));
    }

    [Theory]
    [InlineData("comms: TypeX")]
    [InlineData("COMMS:   TypeX  ")]
    public void TryExtractCommsChannel_IsCaseInsensitive_AndTrims(string body)
    {
        Assert.Equal("TypeX", JabberPingWatcherService.TryExtractCommsChannel(body));
    }

    [Fact]
    public void TryExtractCommsChannel_NoField_ReturnsNull()
    {
        Assert.Null(JabberPingWatcherService.TryExtractCommsChannel("Sub Cap only\nFormup: C-J6MT"));
    }
}

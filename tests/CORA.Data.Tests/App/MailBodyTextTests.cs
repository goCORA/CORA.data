using CORA.App.Services.AI;

namespace CORA.Data.Tests.App;

public class MailBodyTextTests
{
    // ── FromHtml: what is kept ──────────────────────────────────────────────

    [Fact]
    public void Paragraphs_become_lines_separated_by_a_blank_line()
    {
        Assert.Equal("Hello world\n\nSecond", MailBodyText.FromHtml("<p>Hello <b>world</b></p><p>Second</p>"));
    }

    [Fact]
    public void Line_breaks_are_kept()
    {
        Assert.Equal("a\nb\nc", MailBodyText.FromHtml("a<br>b<br/>c"));
    }

    [Fact]
    public void Adjacent_and_nested_divs_do_not_pile_up_blank_lines()
    {
        Assert.Equal("a\nb", MailBodyText.FromHtml("<div><div>a</div><div>b</div></div>"));
    }

    [Fact]
    public void List_items_become_dashed_lines()
    {
        Assert.Equal("- One\n- Two", MailBodyText.FromHtml("<ul><li>One</li><li>Two</li></ul>"));
    }

    [Fact]
    public void Table_rows_become_lines_and_cells_are_space_separated()
    {
        var html = "<table><tr><td>A</td><td>B</td></tr><tr><td>C</td></tr></table>";

        Assert.Equal("A B\nC", MailBodyText.FromHtml(html));
    }

    [Fact]
    public void Entities_are_decoded_and_non_breaking_spaces_become_spaces()
    {
        Assert.Equal("Fish & Chips <3", MailBodyText.FromHtml("<p>Fish &amp; Chips&nbsp;&lt;3</p>"));
    }

    [Fact]
    public void Accented_text_survives()
    {
        Assert.Equal("Résumé — café", MailBodyText.FromHtml("<p>Résumé — café</p>"));
    }

    // ── FromHtml: what is dropped ───────────────────────────────────────────

    [Fact]
    public void Head_style_script_and_title_are_dropped()
    {
        var html = "<html><head><title>T</title><style>p{color:red}</style></head>" +
                   "<body><script>alert(1)</script><p>Hi</p></body></html>";

        Assert.Equal("Hi", MailBodyText.FromHtml(html));
    }

    [Fact]
    public void Link_text_is_kept_and_the_target_is_dropped()
    {
        var text = MailBodyText.FromHtml("<a href=\"https://example.com/track?id=1\">Click here</a>");

        Assert.Equal("Click here", text);
        Assert.DoesNotContain("example.com", text);
    }

    [Fact]
    public void Images_and_their_alt_text_are_dropped()
    {
        var text = MailBodyText.FromHtml("<p>Logo <img src=\"https://x.example/y.png\" alt=\"Company logo\"> text</p>");

        Assert.Equal("Logo text", text);
    }

    [Fact]
    public void A_link_that_wraps_only_an_image_leaves_nothing()
    {
        var text = MailBodyText.FromHtml("<p>Before <a href=\"https://x.example\"><img src=\"y.png\"></a> after</p>");

        Assert.Equal("Before after", text);
    }

    [Fact]
    public void Link_text_that_is_a_tracking_url_becomes_a_placeholder()
    {
        var text = MailBodyText.FromHtml("<a href=\"x\">https://track.example.com/c?u=abc123</a>");

        Assert.Equal("[link]", text);
    }

    [Fact]
    public void Very_long_link_text_urls_become_a_placeholder()
    {
        var url = "https://example.com/" + new string('a', 100);

        Assert.Equal("[link]", MailBodyText.FromHtml($"<a href=\"x\">{url}</a>"));
    }

    [Fact]
    public void A_short_url_as_link_text_is_kept()
    {
        Assert.Equal("https://example.com", MailBodyText.FromHtml("<a href=\"https://example.com\">https://example.com</a>"));
    }

    [Theory]
    [InlineData("<div style=\"display:none\">SECRET</div><p>Body</p>")]
    [InlineData("<div style=\"DISPLAY : NONE\">SECRET</div><p>Body</p>")]
    [InlineData("<div style=\"visibility:hidden\">SECRET</div><p>Body</p>")]
    [InlineData("<div style=\"mso-hide:all\">SECRET</div><p>Body</p>")]
    [InlineData("<div style=\"font-size:0px\">SECRET</div><p>Body</p>")]
    [InlineData("<div style=\"max-height:0;overflow:hidden\">SECRET</div><p>Body</p>")]
    [InlineData("<div style=\"opacity:0\">SECRET</div><p>Body</p>")]
    [InlineData("<div hidden>SECRET</div><p>Body</p>")]
    public void Hidden_content_is_dropped(string html)
    {
        Assert.Equal("Body", MailBodyText.FromHtml(html));
    }

    [Fact]
    public void Zero_width_padding_is_dropped()
    {
        Assert.Equal("Real", MailBodyText.FromHtml("<div>&#8203;&#8204; &nbsp;</div><p>Real</p>"));
    }

    [Fact]
    public void Comments_including_conditional_comments_are_dropped()
    {
        Assert.Equal("Visible", MailBodyText.FromHtml("<!--[if mso]>Outlook only<![endif]-->Visible"));
    }

    [Fact]
    public void No_markup_survives_messy_html()
    {
        var html = "<html><body><div class=\"a\"><table><tr><td><p>One<br>Two</p></td></tr></table>" +
                   "<span style=\"color:red\">Three</span><a href=\"u\">Four</a><img src=\"i\"></div></body></html>";

        var text = MailBodyText.FromHtml(html);

        Assert.DoesNotContain("<", text);
        Assert.DoesNotContain(">", text);
        Assert.Contains("One", text);
        Assert.Contains("Four", text);
    }

    [Fact]
    public void Very_deeply_nested_html_does_not_overflow_and_keeps_the_text()
    {
        var html = string.Concat(Enumerable.Repeat("<div>", 600)) + "deep" +
                   string.Concat(Enumerable.Repeat("</div>", 600));

        var text = MailBodyText.FromHtml(html);

        Assert.Contains("deep", text);
        Assert.DoesNotContain("<", text);
    }

    // ── FromHtml: edge cases ────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<div></div>")]
    [InlineData("<img src=\"x\">")]
    public void Nothing_visible_gives_an_empty_string(string? html)
    {
        Assert.Equal(string.Empty, MailBodyText.FromHtml(html));
    }

    // ── LooksLikeHtml / Normalize ───────────────────────────────────────────

    [Theory]
    [InlineData("<p>x</p>")]
    [InlineData("hello<br/>world")]
    [InlineData("<!DOCTYPE html><html></html>")]
    [InlineData("<A HREF='x'>link</A>")]
    [InlineData("<div class=\"a\">x</div>")]
    [InlineData("text <b>bold</b> text")]
    public void Real_markup_is_recognised(string text)
    {
        Assert.True(MailBodyText.LooksLikeHtml(text));
    }

    [Theory]
    [InlineData("just text")]
    [InlineData("1 < 2 > 0")]
    [InlineData("a < b and c > d")]
    [InlineData("John <john@example.com>")]
    [InlineData("<b@example.com>")]
    [InlineData("<notatag>")]
    public void Plain_text_is_not_mistaken_for_html(string text)
    {
        Assert.False(MailBodyText.LooksLikeHtml(text));
    }

    [Fact]
    public void Normalize_leaves_plain_text_untouched()
    {
        var plain = "Hi Sam,\r\n\r\nSee John <john@example.com> about 1 < 2.\r\n\r\nThanks";

        Assert.Equal(plain, MailBodyText.Normalize(plain));
    }

    [Fact]
    public void Normalize_converts_html()
    {
        Assert.Equal("Hello\n\nWorld", MailBodyText.Normalize("<p>Hello</p><p>World</p>"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Normalize_of_nothing_is_empty(string? text)
    {
        Assert.Equal(string.Empty, MailBodyText.Normalize(text));
    }

    // ── Camouflaged text: tiny fonts and same-colour text ──────────────────

    [Theory]
    [InlineData("<span style=\"color:#fff\">SECRET</span><p>Body</p>")]
    [InlineData("<span style=\"color:#FFFFFF\">SECRET</span><p>Body</p>")]
    [InlineData("<span style=\"color: white\">SECRET</span><p>Body</p>")]
    [InlineData("<span style=\"color:rgb(255, 255, 255)\">SECRET</span><p>Body</p>")]
    [InlineData("<span style=\"color:#fefefe\">SECRET</span><p>Body</p>")]
    [InlineData("<font color=\"white\">SECRET</font><p>Body</p>")]
    [InlineData("<div style=\"background-color:#000\"><span style=\"color:#010101\">SECRET</span></div><p>Body</p>")]
    [InlineData("<table bgcolor=\"#336699\"><tr><td><font color=\"#336699\">SECRET</font></td></tr></table><p>Body</p>")]
    [InlineData("<div style=\"font-size:1px\">SECRET</div><p>Body</p>")]
    [InlineData("<div style=\"font-size:2pt\">SECRET</div><p>Body</p>")]
    [InlineData("<div style=\"font-size:0.1em\">SECRET</div><p>Body</p>")]
    public void Camouflaged_text_is_dropped(string html)
    {
        Assert.Equal("Body", MailBodyText.FromHtml(html));
    }

    [Theory]
    [InlineData("<div style=\"background:#000\"><span style=\"color:#fff\">Visible</span></div>")]
    [InlineData("<td style=\"background-color:#222;color:#fff\">Visible</td>")]
    [InlineData("<table bgcolor=\"black\"><tr><td><font color=\"white\">Visible</font></td></tr></table>")]
    [InlineData("<span style=\"color:#333\">Visible</span>")]
    [InlineData("<span style=\"background-color:#fff\">Visible</span>")]
    [InlineData("<span style=\"color:transparent-ish\">Visible</span>")]
    [InlineData("<span style=\"color:rgba(255,255,255,0.5)\">Visible</span>")]
    [InlineData("<div style=\"font-size:9px\">Visible</div>")]
    [InlineData("<div style=\"font-size:0.75em\">Visible</div>")]
    [InlineData("<div style=\"line-height:1px;font-size:12px\">Visible</div>")]
    public void Ordinary_coloured_or_small_text_is_kept(string html)
    {
        Assert.Equal("Visible", MailBodyText.FromHtml(html));
    }
}

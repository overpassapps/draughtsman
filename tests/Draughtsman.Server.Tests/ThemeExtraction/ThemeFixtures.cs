namespace Draughtsman.Server.Tests.ThemeColourExtraction;

/// <summary>
/// Stylesheets written for these tests to look like what real sites ship. They are invented: no real site's CSS (or brand) is
/// copied here, so nothing in them can be someone's property. Each is one kind of input the extractor has to cope with.
/// </summary>
internal static class ThemeFixtures
{
    /// <summary>A dark site with a bright orange brand, hard-coded colours, a card, a danger style and a gradient hero.</summary>
    public const string DarkBrandSite = """
        /* Northwind-ish dark marketing site (invented) */
        html, body { margin: 0; background: #0b1020; color: #e6e9f2; font-family: "Helvetica Neue", Arial, sans-serif; }
        p, li { color: #e6e9f2; }
        a { color: #ff6a3d; text-decoration: none; }
        a:hover { color: #ff8a65; }
        header.site-header { background: #0b1020; border-bottom: 1px solid #263150; }
        .btn, .btn-primary { background: #ff6a3d; color: #ffffff; border: 1px solid #ff6a3d; border-radius: 6px; }
        .btn:hover { background: #ff825c; }
        .card { background: #121a30; border: 1px solid #263150; }
        .card .meta, .muted { color: #98a2bd; }
        .error, .btn-danger { color: #ff5c5c; border-color: #ff5c5c; }
        .hero { background: linear-gradient(135deg, #0b1020, #1d2a52 50%, #0b1020); }
        @font-face { font-family: Brand; src: url(https://fonts.example.invalid/brand.woff2); }
        @keyframes pulse { from { color: #00ff00; } to { color: #ff00ff; } }
        """;

    /// <summary>A light, minimal site: white page, near-black text, grey secondary text, one blue link colour and nothing else.</summary>
    public const string LightMinimalSite = """
        body { background: #ffffff; color: #222222; font: 16px/1.5 Georgia, serif; }
        p { color: #222222; }
        a { color: #0a58ca; }
        a:visited { color: #6f42c1; }
        .caption, small, .muted { color: #999999; }
        hr, table td { border-color: #e5e5e5; }
        pre { background: #f6f6f6; }
        .card { background: #f6f6f6; }
        """;

    /// <summary>A site that styles only through custom properties, with a dark scheme under prefers-color-scheme.</summary>
    public const string CustomPropertiesOnly = """
        :root {
          --brand: #7c3aed;
          --on-brand: #ffffff;
          --bg: #fafafa;
          --bg-elevated: #f1f0f7;
          --text: #1f2937;
          --text-muted: #6b7280;
          --border: #e5e7eb;
          --danger: #dc2626;
          --radius-md: 8px;
          --shadow-card: 0 1px 2px rgba(0,0,0,.2);
          --tw-ring-color: #00ff00;
          --link: var(--brand);
        }
        @media (prefers-color-scheme: dark) {
          :root {
            --bg: #0f0a1e;
            --bg-elevated: #1a1330;
            --text: #f3f4f6;
            --text-muted: #9ca3af;
            --border: #2a2540;
          }
        }
        """;

    /// <summary>A shadcn-style sheet: bare "H S% L%" custom properties used as hsl(var(--x)), light under :root and dark under .dark.</summary>
    public const string BareHslProperties = """
        :root {
          --background: 0 0% 100%;
          --foreground: 222.2 84% 4.9%;
          --primary: 221.2 83.2% 53.3%;
          --primary-foreground: 210 40% 98%;
          --muted: 210 40% 96.1%;
          --muted-foreground: 215.4 16.3% 46.9%;
          --destructive: 0 84.2% 60.2%;
          --border: 214.3 31.8% 91.4%;
        }
        .dark {
          --background: 222.2 84% 4.9%;
          --foreground: 210 40% 98%;
          --primary: 217.2 91.2% 59.8%;
          --muted: 217.2 32.6% 17.5%;
          --muted-foreground: 215 20.2% 65.1%;
          --border: 217.2 32.6% 17.5%;
        }
        body { background-color: hsl(var(--background)); color: hsl(var(--foreground)); }
        .btn { background: hsl(var(--primary)); color: hsl(var(--primary-foreground)); }
        """;

    /// <summary>No custom properties at all, and every colour syntax the extractor reads: rgb(), hsl(), a named colour, short and long hex.</summary>
    public const string HardCodedOnly = """
        body { background: rgb(250, 245, 240); color: hsl(20, 10%, 15%); }
        main { background-color: #FAF5F0; }
        a { color: teal; }
        .btn-primary { background: teal; color: white; border-color: #006666; }
        .btn-primary:hover { background: #007a7a; }
        nav { background: rgba(0, 128, 128, 0.9); }
        .tag { background: rgba(0, 0, 0, 0.04); }
        .note { color: rgb(110 100 95); }
        .danger { color: #b3261e; }
        footer { border-top: 1px solid hsl(30 20% 85%); }
        """;

    /// <summary>
    /// Garbled and hostile: unbalanced braces and quotes, a string holding a brace, an unterminated comment, var() loops and
    /// dangling references, impossible numbers, deep nesting, and a handful of real colours the extractor should still find.
    /// </summary>
    public static readonly string Hostile = string.Concat(
        "@charset \"utf-8\";\n",
        "body { background: #101820; color: #f2f2f2; content: \"} body { background: red }\"; }\n",
        "a { color: var(--a); --a: var(--b); --b: var(--a); }\n",
        ".x { color: rgb(300, -4, 9999); background: hsl(abc, 5%, 5%); border-color: var(--nowhere); }\n",
        ".y { background: transparent; color: inherit; border: 1px solid currentColor; fill: none; stroke: initial; }\n",
        ".btn { background: #ff3366 ; color: #fff;;;; : ; color }\n",
        string.Concat(Enumerable.Repeat("@media screen { ", 60)), ".deep { color: #123456 }", string.Concat(Enumerable.Repeat(" }", 60)), "\n",
        string.Concat(Enumerable.Repeat("{ ", 500)), "\n",
        ".z { background: url(\"data:image/png;base64,AAAA}\") no-repeat #202830 }\n",
        ".expr { color: expression(alert(1)); background: url(javascript:alert(1)); }\n",
        "/* never closed: .w { color: #00ff00 }");

    /// <summary>A page: its inline style and a theme-color meta, with script text and a comment that hold things that look like tags.</summary>
    public const string HtmlPage = """
        <!doctype html>
        <html><head>
        <meta charset="utf-8">
        <meta name="theme-color" content="#0f766e">
        <title>Invented &lt;style&gt; page</title>
        <!-- <style> body { background: #ff0000 } </style> -->
        <script>var s = "<style> body { background: #00ff00 } </style>";</script>
        <style>
          body { background: #ffffff; color: #1c1917; }
          a { color: #0f766e; }
          .btn { background: #0f766e; color: #ffffff; }
        </style>
        </head><body><p>Hello</p></body></html>
        """;
}

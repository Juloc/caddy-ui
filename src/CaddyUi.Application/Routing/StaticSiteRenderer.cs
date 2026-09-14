using System.Net;
using System.Text;
using CaddyUi.Domain.Routing;

namespace CaddyUi.Application.Routing;

public static class StaticSiteRenderer
{
    public static string Render(RouteConfigurationDocument configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var title = Encode(configuration.StaticSiteTitle);
        var contact = Encode(configuration.StaticSiteContact);
        var home = RenderText(configuration.StaticSiteHomeText);
        var privacy = RenderText(configuration.StaticSitePrivacyText);
        var terms = RenderText(configuration.StaticSiteTermsText);
        var contactBlock = contact.Length == 0
            ? string.Empty
            : $"<p class=\"contact\"><strong>Kontakt:</strong> {contact}</p>";

        var builder = new StringBuilder();
        builder.Append(
            """
            <!doctype html>
            <html lang="de">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <meta name="color-scheme" content="light dark">
            <title>
            """);
        builder.Append(title);
        builder.Append(
            """
            </title>
            <style>
            :root{font-family:Inter,ui-sans-serif,system-ui,-apple-system,BlinkMacSystemFont,"Segoe UI",sans-serif;color-scheme:light dark}*{box-sizing:border-box}body{margin:0;background:#f5f5f5;color:#1f1f1f}main{width:min(760px,calc(100% - 32px));margin:48px auto}.card{background:#fff;border:1px solid #e5e5e5;border-radius:16px;padding:32px;box-shadow:0 8px 30px rgba(0,0,0,.04)}h1{font-size:clamp(1.8rem,5vw,2.5rem);margin:0 0 12px}h2{font-size:1.25rem;margin:0 0 16px}p{line-height:1.65;margin:0 0 14px}.muted{color:#666}nav{display:flex;gap:8px;flex-wrap:wrap;margin:24px 0 32px}nav a{color:inherit;text-decoration:none;border:1px solid #ddd;border-radius:9px;padding:8px 12px}section+section{border-top:1px solid #e8e8e8;margin-top:28px;padding-top:28px}.contact{margin-top:24px}footer{margin-top:28px;font-size:.88rem;color:#666}@media(max-width:600px){main{margin:20px auto}.card{padding:22px;border-radius:12px}}@media(prefers-color-scheme:dark){body{background:#111;color:#eee}.card{background:#191919;border-color:#333;box-shadow:none}.muted,footer{color:#aaa}nav a{border-color:#444}section+section{border-color:#333}}
            </style>
            </head>
            <body>
            <main>
            <article class="card">
            <header><h1>
            """);
        builder.Append(title);
        builder.Append(
            """
            </h1><p class="muted">Informationen zur Anwendung</p></header>
            <nav aria-label="Seitennavigation"><a href="/">Start</a><a href="/privacy">Datenschutz</a><a href="/terms">Nutzungsbedingungen</a></nav>
            <section id="home" data-page="home"><h2>Start</h2>
            """);
        builder.Append(home);
        builder.Append(
            """
            </section>
            <section id="privacy" data-page="privacy"><h2>Datenschutzerklärung</h2>
            """);
        builder.Append(privacy);
        builder.Append(
            """
            </section>
            <section id="terms" data-page="terms"><h2>Nutzungsbedingungen</h2>
            """);
        builder.Append(terms);
        builder.Append("</section>");
        builder.Append(contactBlock);
        builder.Append(
            """
            <footer>Bereitgestellt über Caddy.</footer>
            </article>
            </main>
            <script>(()=>{const p=location.pathname.replace(/\/+$/,'')||'/';const id=p==='/privacy'?'privacy':p==='/terms'?'terms':'home';document.querySelectorAll('[data-page]').forEach(x=>x.hidden=x.id!==id)})();</script>
            </body>
            </html>
            """);
        return builder.ToString();
    }

    private static string RenderText(string? value)
    {
        var normalized = (value ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var paragraph in normalized.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var encoded = Encode(paragraph).Replace("\n", "<br>", StringComparison.Ordinal);
            builder.Append("<p>").Append(encoded).Append("</p>");
        }

        return builder.ToString();
    }

    private static string Encode(string? value)
    {
        return WebUtility.HtmlEncode(value?.Trim() ?? string.Empty);
    }
}

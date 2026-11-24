namespace CommandTray.Relay;

internal static class RelayWeb
{
    public static void Map(WebApplication app, string version)
    {
        MapAsset("/", "index.html", "text/html; charset=utf-8");
        MapAsset("/commandtray.css", "commandtray.css", "text/css; charset=utf-8");
        MapAsset("/commandtray.js", "commandtray.js", "text/javascript; charset=utf-8");
        MapAsset("/board.js", "board.js", "text/javascript; charset=utf-8");

        void MapAsset(string route, string name, string contentType) =>
            app.MapGet(route, (HttpContext context) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                context.Response.Headers["Content-Security-Policy"] =
                    "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; " +
                    "form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
                var asset = typeof(RelayWeb).Assembly.GetManifestResourceStream("CommandTray.Web." + name)
                    ?? throw new InvalidOperationException($"The relay Web asset '{name}' is missing.");
                if (name == "index.html")
                {
                    using var reader = new StreamReader(asset);
                    return Results.Content(reader.ReadToEnd().Replace("__SUITE_VERSION__", version, StringComparison.Ordinal), contentType);
                }
                return Results.File(asset, contentType);
            }).ExcludeFromDescription();
    }
}

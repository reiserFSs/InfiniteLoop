using AscNet.Logging;

namespace AscNet.SDKServer
{
    public class SDKServer
    {
        public static readonly Logger log = new(typeof(SDKServer), Logging.LogLevel.DEBUG, Logging.LogLevel.DEBUG);

        public static void Main(string[] args)
        {
            log.LogLevelColor[Logging.LogLevel.INFO] = ConsoleColor.Blue;
            var builder = WebApplication.CreateBuilder(args);

            // Disables default logger
            builder.Logging.ClearProviders();

            var app = builder.Build();
            app.UseMiddleware<RequestLoggingMiddleware>();

            foreach (string url in GetUrls(args))
                if (!app.Urls.Contains(url))
                    app.Urls.Add(url);

            if (!app.Urls.Any())
            {
                app.Urls.Add("http://*:80");
                app.Urls.Add("https://*:443");
            }

            IEnumerable<Type> controllers = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(s => s.GetTypes())
                .Where(p => typeof(IRegisterable).IsAssignableFrom(p) && !p.IsInterface)
                .Select(x => x);

            foreach (Type controller in controllers)
            {
                controller.GetMethod(nameof(IRegisterable.Register))!.Invoke(null, new object[] { app });
#if DEBUG
                log.Info($"Registered HTTP controller '{controller.Name}'");
#endif
            }

            new Thread(() => app.Run()).Start();
            log.Info($"{nameof(SDKServer)} started in port {string.Join(", ", app.Urls.Select(x => x.Split(':').Last()))}!");
        }

        private static IEnumerable<string> GetUrls(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--urls" && i + 1 < args.Length)
                    return args[i + 1].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (args[i].StartsWith("--urls=", StringComparison.Ordinal))
                    return args[i]["--urls=".Length..].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }

            return [];
        }

        public class RequestLoggingMiddleware
        {
            private static readonly System.Text.Json.JsonSerializerOptions LogJsonOptions = new()
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            private readonly RequestDelegate _next;
            private static readonly PathString[] suppressedRoutes = ["/feedback"];

            public RequestLoggingMiddleware(RequestDelegate next)
            {
                _next = next;
            }

            public async Task Invoke(HttpContext context)
            {
                bool failed = false;
                bool debug = Common.Common.config.VerboseLevel >= Common.VerboseLevel.Debug;
                bool suppressed = suppressedRoutes.Any(route => context.Request.Path == route);
                string url = $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}{context.Request.Path}{context.Request.QueryString}";
                try
                {
                    if (debug && !suppressed)
                    {
                        context.Request.EnableBuffering();
                        string body;
                        using (var reader = new StreamReader(context.Request.Body, System.Text.Encoding.UTF8,
                                   detectEncodingFromByteOrderMarks: true, leaveOpen: true))
                            body = await reader.ReadToEndAsync(context.RequestAborted);
                        context.Request.Body.Position = 0;
                        log.Debug("REQ " + System.Text.Json.JsonSerializer.Serialize(new
                        {
                            method = context.Request.Method, url, status = "-",
                            requestHeaders = context.Request.Headers.ToDictionary(x => x.Key, x => x.Value.ToString()),
                            requestBody = body
                        }, LogJsonOptions));
                    }
                    await _next(context);
                }
                catch (Exception ex)
                {
                    failed = true;
                    if (!context.Response.HasStarted)
                        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    log.Error($"Request failed: {context.Request.Method} {context.Request.Path}, error={ex.GetType().Name}");
                    if (debug)
                        log.Debug($"Request failure details: method={context.Request.Method}, url={url}, stage={context.Items["AscNet.LoginStage"] ?? "http-pipeline"}, account={context.Items["AscNet.LoginAccount"] ?? "unknown"}, responseStarted={context.Response.HasStarted}\n{ex}");
                    throw;
                }
                finally
                {
                    if (!suppressed || failed)
                    {
                        if (debug)
                            log.Debug("RSP " + System.Text.Json.JsonSerializer.Serialize(new
                        {
                            method = context.Request.Method, url, status = context.Response.StatusCode,
                            failed, stage = context.Items["AscNet.LoginStage"],
                            responseHeaders = context.Response.Headers.ToDictionary(x => x.Key, x => x.Value.ToString())
                        }, LogJsonOptions));
                        else if (Common.Common.config.VerboseLevel > Common.VerboseLevel.Silent)
                            log.Info($"{context.Response.StatusCode} {context.Request.Method} {context.Request.Path}{(failed ? " failed" : "")}");
                    }
                }
            }
        }
    }

    public interface IRegisterable
    {
        public abstract static void Register(WebApplication app);
    }
}

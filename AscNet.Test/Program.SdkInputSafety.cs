using System.IO.Pipelines;
using System.Reflection;
using System.Text;
using AscNet.Common.Database;
using AscNet.Logging;
using AscNet.SDKServer.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Newtonsoft.Json.Linq;

namespace AscNet.Test
{
    internal partial class Program
    {
        private static async Task ValidateSdkInputSafety()
        {
            await using var app = CreateGateLoginTestApp();
            var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>().ToArray();
            const int cap = 16 * 1024;
            foreach (string route in new[] { "register", "login", "verify" })
            {
                RequestDelegate endpoint = endpoints.Single(value => value.RoutePattern.RawText == $"/api/AscNet/{route}").RequestDelegate!;
                async Task<DefaultHttpContext> Invoke(Stream body, long? contentLength = null, CancellationToken cancellation = default)
                {
                    var context = new DefaultHttpContext { RequestServices = app.Services, RequestAborted = cancellation };
                    context.Request.Method = "POST";
                    context.Request.Body = body;
                    context.Request.ContentLength = contentLength;
                    context.Response.Body = new MemoryStream();
                    await endpoint(context);
                    return context;
                }
                void AssertRejected(DefaultHttpContext context, int status)
                {
                    AssertEqual(status, context.Response.StatusCode, $"{route} input status");
                    string body = Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
                    AssertEqual(-1, JObject.Parse(body).Value<int>("code"), $"{route} rejection envelope");
                    AssertEqual(false, body.Contains("synthetic-secret", StringComparison.Ordinal), "input error secrecy");
                }
                foreach (string invalid in new[] { "null", "", "{synthetic-secret", "{}", "{\"username\":\"synthetic-secret\"}", "{\"password\":\"synthetic-secret\"}", "{\"username\":\"\",\"password\":\"\",\"token\":null}" })
                    AssertRejected(await Invoke(new MemoryStream(Encoding.UTF8.GetBytes(invalid))), 400);

                // A complete JSON null is deliberately invalid credentials, avoiding any DB lookup.
                // The first fragment alone must not be parsed as a complete body.
                var pipe = new Pipe();
                await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes("nu"));
                Task<DefaultHttpContext> fragmented = Invoke(pipe.Reader.AsStream());
                AssertEqual(false, fragmented.IsCompleted, $"{route} waits for body completion");
                await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes("ll"));
                await pipe.Writer.CompleteAsync();
                AssertRejected(await fragmented, 400);
                await pipe.Reader.CompleteAsync();

                AssertRejected(await Invoke(new MemoryStream(), cap + 1), 413);
                AssertRejected(await Invoke(new MemoryStream(Encoding.UTF8.GetBytes(new string(' ', cap + 1)))), 413);
                AssertRejected(await Invoke(new MemoryStream(Encoding.UTF8.GetBytes("null" + new string(' ', cap - 4)))), 400);

                using var cancellation = new CancellationTokenSource();
                var pendingPipe = new Pipe();
                Task<DefaultHttpContext> pending = Invoke(pendingPipe.Reader.AsStream(), cancellation: cancellation.Token);
                cancellation.Cancel();
                try
                {
                    await pending;
                    throw new InvalidDataException($"{route} ignored request cancellation");
                }
                catch (OperationCanceledException) { }
                finally
                {
                    await pendingPipe.Writer.CompleteAsync();
                    await pendingPipe.Reader.CompleteAsync();
                }
            }

            var account = new Account
            {
                Id = MongoDB.Bson.ObjectId.GenerateNewId(), Uid = 123, Username = "synthetic-user",
                Password = "synthetic-secret-password", Token = "synthetic-token"
            };
            var mapper = typeof(AccountController).GetMethod("AccountResponse", BindingFlags.NonPublic | BindingFlags.Static)!;
            string projected = (string)mapper.Invoke(null, [account])!;
            JObject response = JObject.Parse(projected);
            AssertEqual(0, response.Value<int>("code"), "account success envelope");
            var safeAccount = (JObject)response["account"]!;
            var originalAccount = JObject.FromObject(account);
            foreach (string field in new[] { "Id", "Uid", "Username", "Token" })
                AssertEqual(true, JToken.DeepEquals(originalAccount[field], safeAccount[field]), $"account preserves {field}");
            AssertEqual(false, safeAccount.Properties().Any(property => property.Name.Equals("Password", StringComparison.OrdinalIgnoreCase)), "account excludes password field");
            AssertEqual(false, projected.Contains(account.Password, StringComparison.Ordinal), "account excludes password value");

            Type middlewareType = typeof(AscNet.SDKServer.SDKServer).GetNestedType("RequestLoggingMiddleware", BindingFlags.NonPublic)!;
            MethodInfo invoke = middlewareType.GetMethod("Invoke")!;
            TextWriter previousOutput = Console.Out;
            bool previousFileLogging = Logger.EnableFileLogging;
            using var output = new StringWriter();
            try
            {
                Logger.EnableFileLogging = false;
                Console.SetOut(output);
                foreach (bool started in new[] { false, true })
                {
                    var context = new DefaultHttpContext();
                    context.Features.Set<IHttpResponseFeature>(new SdkStartedResponseFeature(started));
                    context.Request.Method = "POST";
                    context.Request.Path = "/api/AscNet/login";
                    context.Request.QueryString = new QueryString("?ToKeN=synthetic-secret-query&oauthCode=synthetic-secret-query");
                    context.Response.StatusCode = 202;
                    var failure = new InvalidOperationException("synthetic-secret-exception");
                    RequestDelegate next = _ => throw failure;
                    object middleware = Activator.CreateInstance(middlewareType, [next])!;
                    try
                    {
                        await (Task)invoke.Invoke(middleware, [context])!;
                        throw new InvalidDataException("SDK middleware swallowed downstream failure");
                    }
                    catch (InvalidOperationException ex) when (ReferenceEquals(ex, failure)) { }
                    AssertEqual(202, context.Response.StatusCode, "middleware does not rewrite downstream response");
                }
                string logged = output.ToString();
                AssertEqual(false, logged.Contains("synthetic-secret", StringComparison.Ordinal), "SDK diagnostics exclude query and exception secrets");
                AssertEqual(true, logged.Contains("202 POST /api/AscNet/login", StringComparison.Ordinal), "SDK diagnostics retain method path status");
                AssertEqual(true, logged.Contains(nameof(InvalidOperationException), StringComparison.Ordinal), "SDK diagnostics retain failure type");
            }
            finally
            {
                Console.SetOut(previousOutput);
                Logger.EnableFileLogging = previousFileLogging;
            }
        }

        private sealed class SdkStartedResponseFeature(bool started) : IHttpResponseFeature
        {
            public int StatusCode { get; set; } = 200;
            public string? ReasonPhrase { get; set; }
            public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
            public Stream Body { get; set; } = Stream.Null;
            public bool HasStarted => started;
            public void OnStarting(Func<object, Task> callback, object state) { }
            public void OnCompleted(Func<object, Task> callback, object state) { }
        }
    }
}

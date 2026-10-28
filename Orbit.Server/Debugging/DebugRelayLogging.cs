using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orbit.Debugging;
using Spectre.Console;
using SPTarkov.Common.Models.Logging;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Services.Locales;

namespace Orbit.Server.Debugging;

// SPT logs requests before dispatching routes. Decorate only that middleware's logger,
// without changing the shared logger configuration or any SPT files.
public sealed class DebugRelayLogging : IOnDIConstruct
{
    private static readonly object OriginalLoggerKey = new();

    public static Task OnDIConstructAsync(IServiceCollection services, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var middleware = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("SPTarkov.Server.Middleware.SptLoggerMiddleware"))
            .FirstOrDefault(t => t != null);
        if (middleware == null) return Task.CompletedTask;

        var loggerType = typeof(ISptLogger<>).MakeGenericType(middleware);
        if (services.Any(d => d.IsKeyedService && d.ServiceType == loggerType
            && ReferenceEquals(d.ServiceKey, OriginalLoggerKey))) return Task.CompletedTask;
        var original = services.LastOrDefault(d => !d.IsKeyedService && d.ServiceType == loggerType)
            ?? services.LastOrDefault(d => !d.IsKeyedService && d.ServiceType == typeof(ISptLogger<>));
        if (original == null) return Task.CompletedTask;

        // A private keyed registration retains the existing logger and its ownership/lifetime,
        // including an earlier mod's factory or closed registration.
        if (original.ImplementationInstance is { } instance)
            services.Add(ServiceDescriptor.KeyedSingleton(loggerType, OriginalLoggerKey, instance));
        else if (original.ImplementationFactory is { } factory)
            services.Add(ServiceDescriptor.DescribeKeyed(loggerType, OriginalLoggerKey,
                (provider, _) => factory(provider), original.Lifetime));
        else if (original.ImplementationType is { } implementation)
            services.Add(ServiceDescriptor.DescribeKeyed(loggerType, OriginalLoggerKey,
                implementation.IsGenericTypeDefinition ? implementation.MakeGenericType(middleware) : implementation,
                original.Lifetime));
        else return Task.CompletedTask;

        services.AddHttpContextAccessor();
        var wrapper = typeof(DebugRelayRequestLogger<>).MakeGenericType(middleware);
        services.Add(ServiceDescriptor.Describe(loggerType, provider =>
            ActivatorUtilities.CreateInstance(provider, wrapper,
                provider.GetRequiredKeyedService(loggerType, OriginalLoggerKey)), original.Lifetime));
        return Task.CompletedTask;
    }
}

internal sealed class DebugRelayRequestLogger<T>(ISptLogger<T> inner, IHttpContextAccessor context,
    ServerLocalisationService locales) : ISptLogger<T>
{
    private bool IsRelayRequest(string data, Exception? error)
    {
        var http = context.HttpContext;
        if (error != null || http == null || !HttpMethods.IsPost(http.Request.Method)
            || !http.Request.Path.Equals(new PathString(DebugRelayProtocol.Route), StringComparison.Ordinal)) return false;

        // Match the actual translated request announcement, not arbitrary messages containing the URL.
        return data == locales.GetText("client_request", http.Request.Path.Value)
            || data == locales.GetText("client_request_ip", new {
                ip = http.Connection.RemoteIpAddress ?? IPAddress.Loopback, url = http.Request.Path.Value });
    }

    public void Info(string data, Exception? ex = null)
    {
        if (IsRelayRequest(data, ex)) inner.Debug(data);
        else inner.Info(data, ex);
    }

    public void Log(LogLevel level, string data, Color? textColor = null, Color? backgroundColor = null, Exception? ex = null)
        => inner.Log(level == LogLevel.Information && IsRelayRequest(data, ex) ? LogLevel.Debug : level,
            data, textColor, backgroundColor, ex);

    public void LogWithColor(string data, Color? textColor = null, Color? backgroundColor = null, Exception? ex = null)
        => inner.LogWithColor(data, textColor, backgroundColor, ex);
    public void Success(string data, Exception? ex = null) => inner.Success(data, ex);
    public void Error(string data, Exception? ex = null) => inner.Error(data, ex);
    public void Warning(string data, Exception? ex = null) => inner.Warning(data, ex);
    public void Debug(string data, Exception? ex = null) => inner.Debug(data, ex);
    public void Critical(string data, Exception? ex = null) => inner.Critical(data, ex);
    public bool IsLogEnabled(LogLevel level) => inner.IsLogEnabled(level);
}

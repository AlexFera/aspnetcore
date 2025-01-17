// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Microsoft.AspNetCore.HttpOverrides;

/// <summary>
/// A middleware for forwarding proxied headers onto the current request.
/// </summary>
public class ForwardedHeadersMiddleware
{
    private readonly ForwardedHeadersOptions _options;
    private readonly RequestDelegate _next;
    private readonly ILogger _logger;
    private bool _allowAllHosts;
    private IList<StringSegment>? _allowedHosts;

    // RFC 3986 scheme = ALPHA * (ALPHA / DIGIT / "+" / "-" / ".")
    private static readonly IndexOfAnyValues<char> SchemeChars =
        IndexOfAnyValues.Create("+-.0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz");

    // Host Matches Http.Sys and Kestrel
    // Host Matches RFC 3986 except "*" / "+" / "," / ";" / "=" and "%" HEXDIG HEXDIG which are not allowed by Http.Sys
    private static readonly IndexOfAnyValues<char> HostChars =
        IndexOfAnyValues.Create("!$&'()-.0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ_abcdefghijklmnopqrstuvwxyz~");

    // 0-9 / A-F / a-f / ":" / "."
    private static readonly IndexOfAnyValues<char> Ipv6HostChars =
        IndexOfAnyValues.Create(".0123456789:ABCDEFabcdef");

    /// <summary>
    /// Create a new <see cref="ForwardedHeadersMiddleware"/>.
    /// </summary>
    /// <param name="next">The <see cref="RequestDelegate"/> representing the next middleware in the pipeline.</param>
    /// <param name="loggerFactory">The <see cref="ILoggerFactory"/> used for logging.</param>
    /// <param name="options">The <see cref="ForwardedHeadersOptions"/> for configuring the middleware.</param>
    public ForwardedHeadersMiddleware(RequestDelegate next, ILoggerFactory loggerFactory, IOptions<ForwardedHeadersOptions> options)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(options);

        // Make sure required options is not null or whitespace
        EnsureOptionNotNullorWhitespace(options.Value.ForwardedForHeaderName, nameof(options.Value.ForwardedForHeaderName));
        EnsureOptionNotNullorWhitespace(options.Value.ForwardedHostHeaderName, nameof(options.Value.ForwardedHostHeaderName));
        EnsureOptionNotNullorWhitespace(options.Value.ForwardedProtoHeaderName, nameof(options.Value.ForwardedProtoHeaderName));
        EnsureOptionNotNullorWhitespace(options.Value.OriginalForHeaderName, nameof(options.Value.OriginalForHeaderName));
        EnsureOptionNotNullorWhitespace(options.Value.OriginalHostHeaderName, nameof(options.Value.OriginalHostHeaderName));
        EnsureOptionNotNullorWhitespace(options.Value.OriginalProtoHeaderName, nameof(options.Value.OriginalProtoHeaderName));

        _options = options.Value;
        _logger = loggerFactory.CreateLogger<ForwardedHeadersMiddleware>();
        _next = next;

        PreProcessHosts();

        static void EnsureOptionNotNullorWhitespace(string value, string propertyName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException($"options.{propertyName} is required", nameof(options));
            }
        }
    }

    private void PreProcessHosts()
    {
        if (_options.AllowedHosts == null || _options.AllowedHosts.Count == 0)
        {
            _allowAllHosts = true;
            return;
        }

        var allowedHosts = new List<StringSegment>();
        foreach (var entry in _options.AllowedHosts)
        {
            // Punycode. Http.Sys requires you to register Unicode hosts, but the headers contain punycode.
            var host = new HostString(entry).ToUriComponent();

            if (IsTopLevelWildcard(host))
            {
                // Disable filtering
                _allowAllHosts = true;
                return;
            }

            if (!allowedHosts.Contains(host, StringSegmentComparer.OrdinalIgnoreCase))
            {
                allowedHosts.Add(host);
            }
        }

        _allowedHosts = allowedHosts;
    }

    private static bool IsTopLevelWildcard(string host)
    {
        return (string.Equals("*", host, StringComparison.Ordinal) // HttpSys wildcard
                       || string.Equals("[::]", host, StringComparison.Ordinal) // Kestrel wildcard, IPv6 Any
                       || string.Equals("0.0.0.0", host, StringComparison.Ordinal)); // IPv4 Any
    }

    /// <summary>
    /// Executes the middleware.
    /// </summary>
    /// <param name="context">The <see cref="HttpContext"/> for the current request.</param>
    public Task Invoke(HttpContext context)
    {
        ApplyForwarders(context);
        return _next(context);
    }

    /// <summary>
    /// Forward the proxied headers to the given <see cref="HttpContext"/>.
    /// </summary>
    /// <param name="context">The <see cref="HttpContext"/>.</param>
    public void ApplyForwarders(HttpContext context)
    {
        // Gather expected headers.
        string[]? forwardedFor = null, forwardedProto = null, forwardedHost = null;
        bool checkFor = false, checkProto = false, checkHost = false;
        int entryCount = 0;

        var request = context.Request;
        var requestHeaders = context.Request.Headers;
        if (_options.ForwardedHeaders.HasFlag(ForwardedHeaders.XForwardedFor))
        {
            checkFor = true;
            forwardedFor = requestHeaders.GetCommaSeparatedValues(_options.ForwardedForHeaderName);
            entryCount = Math.Max(forwardedFor.Length, entryCount);
        }

        if (_options.ForwardedHeaders.HasFlag(ForwardedHeaders.XForwardedProto))
        {
            checkProto = true;
            forwardedProto = requestHeaders.GetCommaSeparatedValues(_options.ForwardedProtoHeaderName);
            if (_options.RequireHeaderSymmetry && checkFor && forwardedFor!.Length != forwardedProto.Length)
            {
                _logger.LogWarning(1, "Parameter count mismatch between X-Forwarded-For and X-Forwarded-Proto.");
                return;
            }
            entryCount = Math.Max(forwardedProto.Length, entryCount);
        }

        if (_options.ForwardedHeaders.HasFlag(ForwardedHeaders.XForwardedHost))
        {
            checkHost = true;
            forwardedHost = requestHeaders.GetCommaSeparatedValues(_options.ForwardedHostHeaderName);
            if (_options.RequireHeaderSymmetry
                && ((checkFor && forwardedFor!.Length != forwardedHost.Length)
                    || (checkProto && forwardedProto!.Length != forwardedHost.Length)))
            {
                _logger.LogWarning(1, "Parameter count mismatch between X-Forwarded-Host and X-Forwarded-For or X-Forwarded-Proto.");
                return;
            }
            entryCount = Math.Max(forwardedHost.Length, entryCount);
        }

        // Apply ForwardLimit, if any
        if (_options.ForwardLimit.HasValue && entryCount > _options.ForwardLimit)
        {
            entryCount = _options.ForwardLimit.Value;
        }

        // Group the data together.
        var sets = new SetOfForwarders[entryCount];
        for (int i = 0; i < sets.Length; i++)
        {
            // They get processed in reverse order, right to left.
            var set = new SetOfForwarders();
            if (checkFor && i < forwardedFor!.Length)
            {
                set.IpAndPortText = forwardedFor[forwardedFor.Length - i - 1];
            }
            if (checkProto && i < forwardedProto!.Length)
            {
                set.Scheme = forwardedProto[forwardedProto.Length - i - 1];
            }
            if (checkHost && i < forwardedHost!.Length)
            {
                set.Host = forwardedHost[forwardedHost.Length - i - 1];
            }
            sets[i] = set;
        }

        // Gather initial values
        var connection = context.Connection;
        var currentValues = new SetOfForwarders()
        {
            RemoteIpAndPort = connection.RemoteIpAddress != null ? new IPEndPoint(connection.RemoteIpAddress, connection.RemotePort) : null,
            // Host and Scheme initial values are never inspected, no need to set them here.
        };

        var checkKnownIps = _options.KnownNetworks.Count > 0 || _options.KnownProxies.Count > 0;
        bool applyChanges = false;
        int entriesConsumed = 0;

        for (; entriesConsumed < sets.Length; entriesConsumed++)
        {
            var set = sets[entriesConsumed];
            if (checkFor)
            {
                // For the first instance, allow remoteIp to be null for servers that don't support it natively.
                if (currentValues.RemoteIpAndPort != null && checkKnownIps && !CheckKnownAddress(currentValues.RemoteIpAndPort.Address))
                {
                    // Stop at the first unknown remote IP, but still apply changes processed so far.
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug(1, "Unknown proxy: {RemoteIpAndPort}", currentValues.RemoteIpAndPort);
                    }
                    break;
                }

                if (IPEndPoint.TryParse(set.IpAndPortText, out var parsedEndPoint))
                {
                    applyChanges = true;
                    set.RemoteIpAndPort = parsedEndPoint;
                    currentValues.IpAndPortText = set.IpAndPortText;
                    currentValues.RemoteIpAndPort = set.RemoteIpAndPort;
                }
                else if (!string.IsNullOrEmpty(set.IpAndPortText))
                {
                    // Stop at the first unparsable IP, but still apply changes processed so far.
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug(1, "Unparsable IP: {IpAndPortText}", set.IpAndPortText);
                    }
                    break;
                }
                else if (_options.RequireHeaderSymmetry)
                {
                    _logger.LogWarning(2, "Missing forwarded IPAddress.");
                    return;
                }
            }

            if (checkProto)
            {
                if (!string.IsNullOrEmpty(set.Scheme) && set.Scheme.AsSpan().IndexOfAnyExcept(SchemeChars) < 0)
                {
                    applyChanges = true;
                    currentValues.Scheme = set.Scheme;
                }
                else if (_options.RequireHeaderSymmetry)
                {
                    _logger.LogWarning(3, $"Forwarded scheme is not present, this is required by {nameof(_options.RequireHeaderSymmetry)}");
                    return;
                }
            }

            if (checkHost)
            {
                if (!string.IsNullOrEmpty(set.Host) && TryValidateHost(set.Host)
                    && (_allowAllHosts || HostString.MatchesAny(set.Host, _allowedHosts!)))
                {
                    applyChanges = true;
                    currentValues.Host = set.Host;
                }
                else if (_options.RequireHeaderSymmetry)
                {
                    _logger.LogWarning(4, $"Incorrect number of x-forwarded-host header values, see {nameof(_options.RequireHeaderSymmetry)}.");
                    return;
                }
            }
        }

        if (applyChanges)
        {
            if (checkFor && currentValues.RemoteIpAndPort != null)
            {
                if (connection.RemoteIpAddress != null)
                {
                    // Save the original
                    requestHeaders[_options.OriginalForHeaderName] = new IPEndPoint(connection.RemoteIpAddress, connection.RemotePort).ToString();
                }
                if (forwardedFor!.Length > entriesConsumed)
                {
                    // Truncate the consumed header values
                    requestHeaders[_options.ForwardedForHeaderName] = forwardedFor.Take(forwardedFor.Length - entriesConsumed).ToArray();
                }
                else
                {
                    // All values were consumed
                    requestHeaders.Remove(_options.ForwardedForHeaderName);
                }
                connection.RemoteIpAddress = currentValues.RemoteIpAndPort.Address;
                connection.RemotePort = currentValues.RemoteIpAndPort.Port;
            }

            if (checkProto && currentValues.Scheme != null)
            {
                // Save the original
                requestHeaders[_options.OriginalProtoHeaderName] = request.Scheme;
                if (forwardedProto!.Length > entriesConsumed)
                {
                    // Truncate the consumed header values
                    requestHeaders[_options.ForwardedProtoHeaderName] = forwardedProto.Take(forwardedProto.Length - entriesConsumed).ToArray();
                }
                else
                {
                    // All values were consumed
                    requestHeaders.Remove(_options.ForwardedProtoHeaderName);
                }
                request.Scheme = currentValues.Scheme;
            }

            if (checkHost && currentValues.Host != null)
            {
                // Save the original
                requestHeaders[_options.OriginalHostHeaderName] = request.Host.ToString();
                if (forwardedHost!.Length > entriesConsumed)
                {
                    // Truncate the consumed header values
                    requestHeaders[_options.ForwardedHostHeaderName] = forwardedHost.Take(forwardedHost.Length - entriesConsumed).ToArray();
                }
                else
                {
                    // All values were consumed
                    requestHeaders.Remove(_options.ForwardedHostHeaderName);
                }
                request.Host = HostString.FromUriComponent(currentValues.Host);
            }
        }
    }

    private bool CheckKnownAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            var ipv4Address = address.MapToIPv4();
            if (CheckKnownAddress(ipv4Address))
            {
                return true;
            }
        }
        if (_options.KnownProxies.Contains(address))
        {
            return true;
        }
        foreach (var network in _options.KnownNetworks)
        {
            if (network.Contains(address))
            {
                return true;
            }
        }
        return false;
    }

    private struct SetOfForwarders
    {
        public string IpAndPortText;
        public IPEndPoint? RemoteIpAndPort;
        public string Host;
        public string Scheme;
    }

    // Empty was checked for by the caller
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryValidateHost(string host)
    {
        if (host[0] == '[')
        {
            return TryValidateIPv6Host(host);
        }

        if (host[0] == ':')
        {
            // Only a port
            return false;
        }

        var firstNonHostCharIdx = host.AsSpan().IndexOfAnyExcept(HostChars);
        if (firstNonHostCharIdx == -1)
        {
            // no port
            return true;
        }
        else
        {
            return TryValidateHostPort(host, firstNonHostCharIdx);
        }
    }

    // The lead '[' was already checked
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryValidateIPv6Host(string hostText)
    {
        var host = hostText.AsSpan(1);

        var hostEndIdx = host.IndexOfAnyExcept(Ipv6HostChars);
        if ((uint)hostEndIdx >= (uint)host.Length || // No ']'. The uint cast is there to eliminate the
                                                     // bounds check on the 'host[hostEndIdx]' access below.
            host[hostEndIdx] != ']' || // We found an invalid host character
            hostEndIdx < 3) // [::1] is the shortest valid IPv6 host
        {
            return false;
        }

        // If there's nothing left, we're good. If there's more, validate it as a port.
        // +2 to skip the '[' and ']' (the '[' wasn't included in hostEndIdx because we
        // cut it off in the AsSpan above).
        return (hostEndIdx + 2 == hostText.Length) || TryValidateHostPort(hostText, hostEndIdx + 2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryValidateHostPort(string hostText, int offset)
    {
        if (hostText[offset] != ':' || hostText.Length == offset + 1)
        {
            // Must have at least one number after the colon if present.
            return false;
        }

        return hostText.AsSpan(offset + 1).IndexOfAnyExceptInRange('0', '9') < 0;
    }
}

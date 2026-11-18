using System.Net;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.HttpOverrides;

namespace LearnMore.Hosting;

/// <summary>
/// 反向代理／容器部署設定。IIS 透過虛擬應用程式提供 /LearnMore；
/// 在 Kestrel（Docker）前面掛反向代理時，改由 Hosting:PathBase 與 Hosting:UseForwardedHeaders 設定。
/// 兩個設定都沒填時行為與原本 IIS 部署完全相同。
/// </summary>
public static class ReverseProxyHosting
{
    public static void UseLearnMoreReverseProxy(this WebApplication app)
    {
        var section = app.Configuration.GetSection("Hosting");

        if (section.GetValue<bool>("UseForwardedHeaders"))
        {
            var options = new ForwardedHeadersOptions
            {
                ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost,
                ForwardLimit = section.GetValue<int?>("ForwardLimit") ?? 2
            };

            var knownProxies = section.GetSection("KnownProxies").Get<string[]>() ?? Array.Empty<string>();
            var knownNetworks = section.GetSection("KnownNetworks").Get<string[]>() ?? Array.Empty<string>();
            if (knownProxies.Length > 0 || knownNetworks.Length > 0)
            {
                options.KnownProxies.Clear();
                options.KnownNetworks.Clear();
                foreach (var proxy in knownProxies)
                {
                    if (IPAddress.TryParse(proxy, out var address))
                    {
                        options.KnownProxies.Add(address);
                    }
                }

                foreach (var network in knownNetworks)
                {
                    var parts = network.Split('/', 2);
                    if (parts.Length == 2 && IPAddress.TryParse(parts[0], out var prefix) && int.TryParse(parts[1], out var length))
                    {
                        options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(prefix, length));
                    }
                }
            }

            app.UseForwardedHeaders(options);
        }

        var pathBase = section["PathBase"];
        if (!string.IsNullOrWhiteSpace(pathBase))
        {
            app.UsePathBase("/" + pathBase.Trim().Trim('/'));
        }
    }

    /// <summary>
    /// 診斷追蹤檔位置：LEARNMORE_TRACE_DIR 優先；Windows 維持原本的 D:\Data；其他平台用暫存目錄。
    /// </summary>
    public static string TraceFilePath(string fileName)
    {
        var configured = Environment.GetEnvironmentVariable("LEARNMORE_TRACE_DIR");
        string directory;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            directory = configured;
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            directory = @"D:\Data";
        }
        else
        {
            directory = Path.GetTempPath();
        }

        return Path.Combine(directory, fileName);
    }
}

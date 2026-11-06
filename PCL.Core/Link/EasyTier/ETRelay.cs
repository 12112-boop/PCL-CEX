using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PCL.Core.IO.Net.Http;
using PCL.Core.Logging;

namespace PCL.Core.Link.EasyTier;
// ReSharper disable InconsistentNaming

public class ETRelay
{
    public static List<ETRelay> RelayList { get; set; } = [];

    /// <summary>
    /// 内置的 EasyTier 公共节点，在无法获取在线节点列表时使用。
    /// </summary>
    public static readonly IReadOnlyList<string> FallbackNodes =
    [
        "tcp://public.easytier.top:11010",
        "tcp://public2.easytier.cn:54321",
        "https://etnode.zkitefly.eu.org/node1",
        "https://etnode.zkitefly.eu.org/node2",
        "https://etnode.zkitefly.eu.org/-node1",
        "https://etnode.zkitefly.eu.org/-node2"
    ];

    /// <summary>
    /// 从 EasyTier 公共节点列表获取可用中继节点。
    /// </summary>
    public static async Task RefreshAsync()
    {
        try
        {
            using var rep = await HttpRequest
                .Create("https://uptime.easytier.cn/api/nodes?page=1&per_page=50&is_active=true")
                .SendAsync()
                .ConfigureAwait(false);
            rep.EnsureSuccessStatusCode();

            var dto = await rep.AsJsonAsync<PublicNodeDto>().ConfigureAwait(false);
            if (dto?.Data.Items is null) return;

            var nodes = dto.Data.Items
                .Where(it => it is { IsActive: true, IsAllowRelay: true })
                .Select(it => it.Host)
                .Take(6)
                .ToList();

            if (nodes.Count == 0) return;

            RelayList = nodes
                .Select(host => new ETRelay { Url = host, Name = "Community", Type = ETRelayType.Community })
                .ToList();

            LogWrapper.Info("Link", $"已获取 {RelayList.Count} 个 EasyTier 公共中继节点");
        }
        catch (Exception ex)
        {
            LogWrapper.Warn(ex, "Link", "获取 EasyTier 公共中继节点失败，将使用内置节点");
        }
    }

    public required string Url { get; init; }
    public required string Name { get; init; }
    public ETRelayType Type { get; init; }
}

public enum ETRelayType
{
    Community,
    Selfhosted,
    Custom
}

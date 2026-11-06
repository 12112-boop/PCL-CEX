using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using PCL.Core.App;
using PCL.Core.App.IoC;
using PCL.Core.App.Localization;
using PCL.Core.IO.Net;
using PCL.Core.IO.Net.SocketForward;
using PCL.Core.Link.EasyTier;
using PCL.Core.Link.McPing;
using PCL.Core.Logging;
using PCL.Core.UI;
using PCL.Core.Utils;

namespace PCL.Core.Link.Lobby;

/// <summary>
/// 陶瓦联机大厅服务：直接驱动 EasyTier 完成大厅的创建与加入。
/// </summary>
[LifecycleService(LifecycleState.Loaded)]
public class LobbyService() : GeneralService("lobby", "LobbyService")
{
    private static CancellationTokenSource _lobbyCts = new();
    private static CancellationTokenSource _discoveringCts = new();
    private static Task? _discoveringTask;

    private static readonly Timer _ServerGameWatcher =
        new(_CheckGameState, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15));

    private static bool _isGameWatcherRunnable;
    private static int _isLeaving;
    private static int _hostPort;
    private static bool _isHost;

    /// <summary>
    /// Current lobby state.
    /// </summary>
    public static LobbyState CurrentState { get; private set; } = LobbyState.Idle;

    /// <summary>
    /// Founded local Minecraft worlds.
    /// </summary>
    public static ObservableCollection<FoundWorld> DiscoveredWorlds { get; } = [];

    /// <summary>
    /// Demonstrate whether the current user is the host of the lobby.
    /// </summary>
    public static bool IsHost => _isHost;

    /// <summary>
    /// Current lobby full code.
    /// </summary>
    public static string? CurrentLobbyCode { get; private set; }

    /// <summary>
    /// Current lobby username.
    /// </summary>
    public static string? CurrentUserName { get; private set; }

    #region UI Events

    /// <summary>
    /// Invoked when lobby state changed. (first arg is the old state; second arg is the new state.)
    /// </summary>
    public static event Action<LobbyState, LobbyState>? StateChanged;

    /// <summary>
    /// Invoked when need to download EasyTier core files.
    /// </summary>
    public static event Action? OnNeedDownloadEasyTier;

    /// <summary>
    /// Invoked when user stop the game in server mode.
    /// </summary>
    public static event Action? OnUserStopGame;

    /// <summary>
    /// Invoked when the lobby has been created or joined successfully.
    /// </summary>
    public static event Action? OnServerStarted;

    public static event Action<Exception>? OnServerException;

    #endregion

    /// <inheritdoc />
    public override void Stop()
    {
        _lobbyCts.Cancel();
        _discoveringCts.Cancel();
        ETController.Exit();
        _ServerGameWatcher.Dispose();
        _lobbyCts.Dispose();
        _discoveringCts.Dispose();
    }

    private static bool _IsEasyTierCoreFileNotExist() =>
        !File.Exists(Path.Combine(EasyTierMetadata.EasyTierFilePath, "easytier-core.exe")) &&
        !File.Exists(Path.Combine(EasyTierMetadata.EasyTierFilePath, "Packet.dll")) &&
        !File.Exists(Path.Combine(EasyTierMetadata.EasyTierFilePath, "easytier-cli.exe"));

    public static async Task InitializeAsync()
    {
        if (CurrentState is not LobbyState.Idle && CurrentState is not LobbyState.Error)
        {
            return;
        }

        _SetState(LobbyState.Initializing);
        try
        {
            if (_IsEasyTierCoreFileNotExist())
            {
                LogWrapper.Info("LobbyService", "EasyTier not found, starting download.");
                OnNeedDownloadEasyTier?.Invoke();
            }
            else
            {
                LogWrapper.Info("LobbyService", "EasyTier files check completed.");
            }

            await ETRelay.RefreshAsync().ConfigureAwait(false);

            _SetState(LobbyState.Initialized);
            LogWrapper.Info("LobbyService", "Lobby service initialized successfully.");

            _ = DiscoverWorldAsync();
        }
        catch (Exception ex)
        {
            LogWrapper.Error(ex, "LobbyService", "Lobby service initialization failed.");
            HintWrapper.Show(Lang.Text("Link.Lobby.InitFailed"), HintTheme.Error);
            _SetState(LobbyState.Error);
        }
    }

    /// <summary>
    /// Discover minecraft shared world.
    /// </summary>
    public static async Task DiscoverWorldAsync()
    {
        if (_discoveringCts.IsCancellationRequested)
        {
            return;
        }

        if (CurrentState is not LobbyState.Initialized and not LobbyState.Idle)
        {
            return;
        }

        _SetState(LobbyState.Discovering);
        await _RunInUiAsync(() => DiscoveredWorlds.Clear()).ConfigureAwait(false);

        _discoveringTask = Task.Run(async () =>
        {
            var recordedPorts = new ConcurrentSet<int>();
            using var listener = new BroadcastListener();

            var handler = new Action<BroadcastRecord, IPEndPoint>((info, _) => Task.Run(async () =>
            {
                if (!recordedPorts.TryAdd(info.Address.Port)) return;

                using var pinger = McPingServiceFactory.CreateService(new IPEndPoint(IPAddress.Loopback, info.Address.Port));
                using var cts = new CancellationTokenSource(2000);

                try
                {
                    var pingRes = await pinger.PingAsync(cts.Token).ConfigureAwait(false);

                    if (pingRes is null)
                    {
                        throw new ArgumentNullException(nameof(pingRes), "Failed to ping minecraft entity.");
                    }

                    var worldName = Lang.Text("Link.Lobby.WorldNameFormat",
                        pingRes.Description,
                        pingRes.Version.Name,
                        info.Address.Port);
                    await _RunInUiAsync(() => DiscoveredWorlds.Add(new FoundWorld(worldName, info.Address.Port)))
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogWrapper.Error(ex, "LobbyService", $"Pinging port {info.Address.Port} failed.");
                }
            }));

            listener.OnReceive += handler;
            listener.Start();
            await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            listener.OnReceive -= handler;
        }, _discoveringCts.Token);

        _SetState(LobbyState.Initialized);
    }

    /// <summary>
    /// Create a new 陶瓦 lobby.
    /// </summary>
    /// <param name="port">Minecraft share port.</param>
    /// <param name="username">Player name.</param>
    public static async Task<bool> CreateLobbyAsync(int port, string username)
    {
        await _discoveringCts.CancelAsync().ConfigureAwait(false);

        _SetState(LobbyState.Creating);
        try
        {
            CurrentUserName = username;

            var lobby = LobbyInfoProvider.Generate(port);
            LobbyInfoProvider.TargetLobby = lobby;
            CurrentLobbyCode = lobby.OriginalCode;
            _hostPort = port;
            _isHost = true;

            if (ETController.Launch(true) != 0)
            {
                HintWrapper.Show(Lang.Text("Link.Lobby.CreateLobbyFailed"), HintTheme.Error);
                await LeaveLobbyAsync().ConfigureAwait(false);
                return false;
            }

            LogWrapper.Info("LobbyService", $"已创建陶瓦大厅 {lobby.OriginalCode}，端口 {port}");

            _SetState(LobbyState.Connected);
            _isGameWatcherRunnable = true;
            OnServerStarted?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            LogWrapper.Error(ex, "LobbyService", "Failed to create lobby.");
            OnServerException?.Invoke(ex);
            HintWrapper.Show(Lang.Text("Link.Lobby.CreateLobbyFailed"), HintTheme.Error);
            await LeaveLobbyAsync().ConfigureAwait(false);
            return false;
        }
    }

    /// <summary>
    /// Join an exist 陶瓦 lobby.
    /// </summary>
    /// <param name="lobbyCode">Lobby share code.</param>
    /// <param name="username">Current use name.</param>
    public static async Task<bool> JoinLobbyAsync(string lobbyCode, string username)
    {
        await _discoveringCts.CancelAsync().ConfigureAwait(false);

        _SetState(LobbyState.Joining);

        LogWrapper.Info("LobbyService", $"Try to join lobby {lobbyCode}");

        try
        {
            CurrentUserName = username;

            var lobby = LobbyInfoProvider.ParseCode(lobbyCode);
            if (lobby is null)
            {
                HintWrapper.Show(Lang.Text("Link.Lobby.InvalidCodeFormat"), HintTheme.Error);
                await LeaveLobbyAsync().ConfigureAwait(false);
                return false;
            }

            LobbyInfoProvider.TargetLobby = lobby;
            CurrentLobbyCode = lobby.OriginalCode;
            _hostPort = 0;
            _isHost = false;

            if (ETController.Launch(false) != 0)
            {
                HintWrapper.Show(Lang.Text("Link.Lobby.JoinFailed"), HintTheme.Error);
                await LeaveLobbyAsync().ConfigureAwait(false);
                return false;
            }

            // 把 EasyTier 转发出来的端口再映射成本地局域网世界，便于 MC 直接看到
            var tcpPortForForward = NetworkHelper.NewTcpPort();
            LobbyInfoProvider.McForward = new TcpForwardBuilder()
                .BindLocal((ushort)tcpPortForForward)
                .SetRemote(IPAddress.Loopback, (ushort)LobbyInfoProvider.JoinerLocalPort)
                .Build();
            LobbyInfoProvider.McForward.Start();
            LobbyInfoProvider.McBroadcast = new BroadcastLocal(
                Lang.Text("Link.Lobby.MotdFormat", lobby.OriginalCode), tcpPortForForward);
            LobbyInfoProvider.McBroadcast.Start();

            LogWrapper.Info("LobbyService", $"已加入陶瓦大厅 {lobby.OriginalCode}，本地端口 {tcpPortForForward}");

            _SetState(LobbyState.Connected);
            OnServerStarted?.Invoke();
            return true;
        }
        catch (ArgumentException)
        {
            LogWrapper.Error("LobbyService", $"Failed to join lobby {lobbyCode}.");
            HintWrapper.Show(Lang.Text("Link.Lobby.InvalidCodeFormat"), HintTheme.Error);
            await LeaveLobbyAsync().ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            LogWrapper.Error(ex, "LobbyService", $"Failed to join lobby {lobbyCode}.");
            HintWrapper.Show(ex.Message, HintTheme.Error);
            await LeaveLobbyAsync().ConfigureAwait(false);
            return false;
        }
    }

    /// <summary>
    /// Leave from lobby.
    /// </summary>
    public static async Task LeaveLobbyAsync()
    {
        if (Interlocked.Exchange(ref _isLeaving, 1) == 1)
            return;

        try
        {
            _SetState(LobbyState.Leaving);

            await _lobbyCts.CancelAsync().ConfigureAwait(false);

            LobbyInfoProvider.McForward?.Stop();
            LobbyInfoProvider.McForward = null;
            LobbyInfoProvider.McBroadcast?.Stop();
            LobbyInfoProvider.McBroadcast = null;
            ETController.Exit();

            LobbyInfoProvider.TargetLobby = null;
            CurrentLobbyCode = null;
            CurrentUserName = null;
            _hostPort = 0;
            _isHost = false;

            _lobbyCts = new CancellationTokenSource();
            _SetState(LobbyState.Initialized);

            LogWrapper.Info("LobbyService", "Left lobby and cleaned up resources.");

            _isGameWatcherRunnable = false;

            _discoveringCts = new CancellationTokenSource();
            _ = DiscoverWorldAsync();
        }
        catch (Exception ex)
        {
            LogWrapper.Error(ex, "LobbyService", "Failed when leave lobby.");
        }
        finally
        {
            _isLeaving = 0;
        }
    }

    private static void _SetState(LobbyState newState)
    {
        var oldState = CurrentState;
        if (oldState == newState)
        {
            return;
        }

        CurrentState = newState;
        LogWrapper.Info("LobbyService", $"Lobby state changed from {oldState} to {newState}");
        StateChanged?.Invoke(oldState, newState);
    }

    private static void _CheckGameState(object? state)
    {
        if (!_isGameWatcherRunnable || _hostPort == 0)
        {
            return;
        }

        var port = _hostPort;
        _ = Task.Run(async () =>
        {
            try
            {
                using var ping = McPingServiceFactory.CreateService("127.0.0.1", port, 3000);
                var info = await ping.PingAsync().ConfigureAwait(false);
                if (info is not null) return;

                LogWrapper.Warn("Link", $"本地 MC 局域网实例 ({port}) 疑似已关闭");
            }
            catch (Exception ex)
            {
                LogWrapper.Warn(ex, "Link", $"检查本地 MC 局域网实例 ({port}) 失败");
            }

            _isGameWatcherRunnable = false;
            OnUserStopGame?.Invoke();
        });
    }

    private static async Task _RunInUiAsync(Action action)
    {
        await Application.Current.Dispatcher.InvokeAsync(action);
    }
}

/// <summary>
/// Founded minecraft world information.
/// </summary>
/// <param name="Name">World name.</param>
/// <param name="Port">World share port.</param>
public record FoundWorld(string Name, int Port);

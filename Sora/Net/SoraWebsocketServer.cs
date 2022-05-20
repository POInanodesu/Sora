using System;
using System.Data;
using System.Threading.Tasks;
using Fleck;
using Newtonsoft.Json.Linq;
using Sora.Entities.Base;
using Sora.Entities.Socket;
using Sora.Interfaces;
using Sora.Net.Config;
using Sora.OnebotAdapter;
using Sora.Util;
using YukariToolBox.LightLog;
using LogLevel = Fleck.LogLevel;

namespace Sora.Net;

/// <summary>
/// Sora服务器实例
/// </summary>
public sealed class SoraWebsocketServer : ISoraService
{
    #region 属性

    /// <summary>
    /// 服务器配置类
    /// </summary>
    private ServerConfig Config { get; }

    /// <summary>
    /// WS服务器
    /// </summary>
    private WebSocketServer Server { get; set; }

    /// <summary>
    /// 事件接口
    /// </summary>
    public EventAdapter Event { get; }

    /// <summary>
    /// 服务器连接管理器
    /// </summary>
    public ConnectionManager ConnManager { get; }

    /// <summary>
    /// 服务ID
    /// </summary>
    public Guid ServiceId => _serverId;

    #endregion

    #region 私有字段

    /// <summary>
    /// 服务器已准备启动标识
    /// </summary>
    private readonly bool _serverReady;

    /// <summary>
    /// 服务器ID
    /// </summary>
    private readonly Guid _serverId = Guid.NewGuid();

    /// <summary>
    /// dispose flag
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// 运行标志位
    /// </summary>
    private bool _serverRunning;

    #endregion

    #region 构造函数

    /// <summary>
    /// 创建一个反向WS服务端
    /// </summary>
    /// <param name="config">服务器配置</param>
    /// <param name="crashAction">发生未处理异常时的回调</param>
    /// <exception cref="ArgumentNullException">读取到了空配置文件</exception>
    /// <exception cref="ArgumentOutOfRangeException">服务器启动参数错误</exception>
    internal SoraWebsocketServer(ServerConfig config, Action<Exception> crashAction = null)
    {
        _serverReady = false;
        Log.Info("Sora", $"Sora WebSocket服务器初始化... [{_serverId}]");
        Config = config ?? throw new ArgumentNullException(nameof(config));
        //检查端口占用
        if (Helper.IsPortInUse(Config.Port))
        {
            var e = new InvalidOperationException($"端口{Config.Port}已被占用，请更换其他端口");
            Log.Fatal(e, "Sora", $"端口{Config.Port}已被占用，请更换其他端口", Config);
            throw e;
        }

        //写入初始化信息
        if (!StaticVariable.ServiceConfigs.TryAdd(_serverId, new ServiceConfig(Config)))
            throw new DataException("try add service config failed");
        //检查参数
        if (Config.Port == 0)
            throw new ArgumentOutOfRangeException(nameof(Config.Port), "Port 0 is not allowed");
        //初始化连接管理器
        ConnManager = new ConnectionManager(Config, _serverId);
        //实例化事件接口
        Event = new EventAdapter(_serverId, Config.ThrowCommandException, Config.SendCommandErrMsg);
        //禁用原log
        FleckLog.Level = (LogLevel) 4;
        //全局异常事件
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (crashAction == null)
                Helper.FriendlyException(args);
            else
                crashAction(args.ExceptionObject as Exception);
        };
        _serverReady = true;
    }

    #endregion

    #region 服务端启动

    /// <summary>
    /// 启动 Sora 服务
    /// </summary>
    public ValueTask StartService()
    {
        _serverRunning = true;
        if (!_serverReady) return ValueTask.CompletedTask;
        //启动服务器
        Server = new WebSocketServer($"ws://{Config.Host}:{Config.Port}")
        {
            //出错后进行重启
            RestartAfterListenError = true
        };
        Server.Start(SocketConfig);
        Log.Info("Sora", $"Sora WebSocket服务器正在运行[{Config.Host}:{Config.Port}]");
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// <para>停止 Sora 服务</para>
    /// <para>停止ws服务器</para>
    /// </summary>
    public ValueTask StopService()
    {
        if (_disposed && !_serverRunning) return ValueTask.CompletedTask;
        Log.Warning("Sora", $"SoraWebsocket服务器[{ServiceId}]正在停止...");
        ConnManager.CloseAllConnection(ServiceId);
        //停止服务器
        Server.Dispose();
        _serverRunning = false;
        Log.Warning("Sora", $"Sora WebSocket服务器[{ServiceId}]已停止运行");
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// socket事件处理
    /// </summary>
    private void SocketConfig(IWebSocketConnection socket)
    {
        //接收事件处理
        //获取请求头数据
        if (!socket.ConnectionInfo.Headers.TryGetValue("X-Self-ID", out string selfId) || //bot UID
            !socket.ConnectionInfo.Headers.TryGetValue("X-Client-Role", out string role)) //Client Type
            return;

        //请求路径检查
        bool isLost = role switch
        {
            "Universal" => !socket.ConnectionInfo.Path.Trim('/').Equals(Config.UniversalPath.Trim('/')),
            _           => true
        };
        if (isLost)
        {
            socket.Close();
            Log.Warning("Sora",
                $"关闭与未知客户端的连接[{socket.ConnectionInfo.ClientIpAddress}:{socket.ConnectionInfo.ClientPort}]，请检查是否设置正确的监听地址");
            return;
        }

        //打开连接
        socket.OnOpen = () =>
        {
            if (_disposed || !_serverRunning) return;
            //获取Token
            if (socket.ConnectionInfo.Headers.TryGetValue("Authorization",
                    out string headerValue))
            {
                string token = headerValue.Split(' ')[1];
                Log.Debug("Server", $"get token = {token}");
                //验证Token
                if (!token.Equals(Config.AccessToken)) return;
            }

            //向客户端发送Ping
            socket.SendPing(new byte[] {1, 2, 5});
            //事件回调
            ConnManager.OpenConnection(role, selfId, new ServerSocket(socket), _serverId,
                socket.ConnectionInfo.Id, Config.ApiTimeOut);
            Log.Info("Sora",
                $"已连接客户端[{socket.ConnectionInfo.ClientIpAddress}:{socket.ConnectionInfo.ClientPort}]");
        };
        //关闭连接
        socket.OnClose = () =>
        {
            if (_disposed || !_serverRunning) return;
            //移除原连接信息
            if (ConnectionManager.ConnectionExists(socket.ConnectionInfo.Id))
                ConnManager.CloseConnection(role, Convert.ToInt64(selfId),
                    socket.ConnectionInfo.Id);

            Log.Info("Sora",
                $"客户端连接被关闭[{socket.ConnectionInfo.ClientIpAddress}:{socket.ConnectionInfo.ClientPort}]");
        };
        //上报接收
        socket.OnMessage = message =>
            Task.Run(() =>
            {
                if (_disposed || !_serverRunning) return;
                Event.Adapter(JObject.Parse(message), socket.ConnectionInfo.Id);
            });
    }

    /// <summary>
    /// GC析构函数
    /// </summary>
    ~SoraWebsocketServer()
    {
        Dispose();
    }

    /// <summary>
    /// 释放资源
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        StopService().AsTask().Wait();
        ConnManager?.Dispose();
        StaticVariable.DisposeService(_serverId);
        GC.SuppressFinalize(this);
    }

    #endregion

    #region util

    /// <summary>
    /// 获取API实例
    /// </summary>
    /// <param name="connectionId">链接ID</param>
    public SoraApi GetApi(Guid connectionId)
    {
        return StaticVariable.ConnectionInfos[connectionId].ApiInstance;
    }

    #endregion
}
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace TenE0.Core.Messaging.RabbitMq;

/// <summary>
/// RabbitMQ 连接管理器契约（issue #165）。
///
/// <para>
/// 抽成接口让 <see cref="RabbitMqPublisher"/> 可在单测中 mock（避免依赖真实 broker）。
/// 生产实现 <see cref="RabbitMqConnectionManager"/> 由 DI 注入。
/// </para>
/// </summary>
public interface IRabbitMqConnectionManager
{
    /// <summary>当前是否至少有一条可用连接（非精确，供健康检查与排障）。</summary>
    bool IsConnected { get; }

    /// <summary>
    /// 从池中借一个 channel（创建一个新 channel，附在借出的 connection 上）。
    /// 调用方用完必须 <c>await using</c> 归还 lease。
    /// </summary>
    Task<RabbitMqChannelLease> GetChannelAsync(CancellationToken cancellationToken);
}

/// <summary>
/// RabbitMQ 连接管理器（issue #165）。
///
/// <para>
/// 设计要点：
/// <list type="bullet">
/// <item><b>池化 Connection，不池化 Channel</b> —— <c>RabbitMQ.Client</c> v7 的 <c>IChannel</c>
///   <b>禁止跨线程并发 publish</b>，所以 channel 每次 create → publish → dispose（轻量），
///   而 Connection（TCP 长连接、自动重连）才值得池化复用。</item>
/// <item><b>自动重连</b> —— <c>ConnectionFactory.AutomaticRecoveryEnabled = true</c>（v7 默认开），
///   broker 断线后客户端自动重连；重连期间 <see cref="IsConnected"/> 返回 false，
///   Publisher 抛异常让 <c>OutboxRelayService</c> 重试（<c>MaxAttempts</c> 兜底，不丢消息）。</item>
/// <item><b>懒初始化</b> —— 首次 <see cref="GetChannelAsync"/> 时才建连，避免启动期 broker 不可达阻塞宿主启动。</item>
/// </list>
/// </para>
///
/// <para>
/// 注册为 <c>Singleton</c>：连接池跨请求共享，整个进程生命周期复用。
/// </para>
/// </summary>
public sealed class RabbitMqConnectionManager : IRabbitMqConnectionManager, IAsyncDisposable
{
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqConnectionManager> _logger;
    private readonly ConnectionFactory _factory;

    // 连接池：Channel<IConnection> 作为有界对象池，借还 O(1)，池空时 await 阻塞（背压）。
    // 不用 ConcurrentBag —— 它的 borrow 不支持异步等待，连接不够时直接抛异常。
    private readonly Channel<IConnection> _pool;
    private int _created; // 已创建连接总数（含已借出的），用于控制不超过 MaxConnections
    private volatile bool _disposed;

    /// <summary>构造。DI Singleton —— options 与 logger 由 DI 注入。</summary>
    public RabbitMqConnectionManager(
        IOptions<RabbitMqOptions> options,
        ILogger<RabbitMqConnectionManager> logger)
    {
        _options = options.Value;
        _logger = logger;
        _factory = new ConnectionFactory
        {
            HostName = _options.Connection.HostName,
            Port = _options.Connection.Port,
            UserName = _options.Connection.UserName,
            Password = _options.Connection.Password,
            VirtualHost = _options.Connection.VirtualHost,
            // v7 默认即开，显式写出表意：broker 断线后客户端自动重连 + 拓扑恢复。
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
        };
        _pool = Channel.CreateBounded<IConnection>(
            new BoundedChannelOptions(_options.Connection.MaxConnections)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false,
            });
    }

    /// <summary>
    /// 当前是否至少有一条可用连接（非精确，供健康检查与排障）。
    /// </summary>
    public bool IsConnected
    {
        get
        {
            // 借一条出来看 IsOpen，再还回去；池空或连接未建即视为未连。
            if (_pool.Reader.TryRead(out var conn))
            {
                var open = conn.IsOpen;
                if (!ReturnConnection(conn))
                    _ = Interlocked.Decrement(ref _created); // 死连接已丢弃
                return open;
            }
            return false;
        }
    }

    /// <summary>
    /// 从池中借一个 channel（创建一个新 channel，附在借出的 connection 上）。
    /// 调用方用完必须 <b>await</b> <see cref="RabbitMqChannelLease.DisposeAsync"/> 归还（封装在 lease 里）。
    /// </summary>
    /// <remarks>
    /// 为什么返回 lease 而非裸 IChannel：IChannel 不支持跨线程并发 publish，必须保证
    /// "create → publish → dispose" 在同一调用上下文，lease 用 <c>using</c> 模式强制归还语义。
    /// </remarks>
    public async Task<RabbitMqChannelLease> GetChannelAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var connection = await AcquireConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // exchange declare 幂等（Durable=true + 同名声明是 no-op），首次建连时确保目标交换机存在。
            var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            // 首次使用前确保交换机存在（幂等）。失败则释放 channel 重抛，让 Relay 重试。
            await channel.ExchangeDeclareAsync(
                exchange: _options.Exchange.Name,
                type: _options.Exchange.Type,
                durable: _options.Exchange.Durable,
                autoDelete: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            // 归还语义封装进 lease：channel dispose 后归还 connection，死连接则回退计数。
            return new RabbitMqChannelLease(channel, () =>
            {
                if (!ReturnConnection(connection))
                    _ = Interlocked.Decrement(ref _created);
                return ValueTask.CompletedTask;
            });
        }
        catch
        {
            // channel 创建失败也要归还 connection，否则连接泄露。
            if (!ReturnConnection(connection))
                _ = Interlocked.Decrement(ref _created);
            throw;
        }
    }

    /// <summary>
    /// 归还连接（channel 由 lease 自己 dispose，连接回池复用）。
    /// 返回 <c>true</c> = 已回池复用；<c>false</c> = 连接已死被丢弃（调用方应 Decrement 计数）。
    /// </summary>
    internal bool ReturnConnection(IConnection connection)
    {
        // 连接已死则不回池（让下次 Acquire 新建一个替代）。异步 dispose fire-and-forget —— broker 侧会兜底回收。
        if (!connection.IsOpen)
        {
            _ = DisposeConnectionAsync(connection);
            return false;
        }

        if (_pool.Writer.TryWrite(connection))
            return true;

        // 池满（理论上不应发生，借出数 = created ≤ MaxConnections），降级 dispose。
        _ = DisposeConnectionAsync(connection);
        return false;
    }

    private static async ValueTask DisposeConnectionAsync(IConnection connection)
    {
        try { await connection.DisposeAsync(); }
        catch { /* 关闭异常忽略，broker 侧会超时回收 */ }
    }

    private async Task<IConnection> AcquireConnectionAsync(CancellationToken cancellationToken)
    {
        // 池里有现成的优先复用。
        if (_pool.Reader.TryRead(out var existing))
        {
            if (existing.IsOpen)
                return existing;
            // 死连接，丢弃并继续新建。
            _ = Interlocked.Decrement(ref _created);
            try { await existing.DisposeAsync(); }
            catch { /* 忽略 */ }
        }

        // 池空：在不超过 MaxConnections 的前提下新建。
        var current = Interlocked.Increment(ref _created);
        if (current > _options.Connection.MaxConnections)
        {
            // 超上限，回退计数并等待归还。
            _ = Interlocked.Decrement(ref _created);
            var waited = await _pool.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            return waited;
        }

        try
        {
            var conn = await _factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "RabbitMQ 连接已建立 {Host}:{Port} VHost={VHost}",
                _options.Connection.HostName, _options.Connection.Port, _options.Connection.VirtualHost);
            return conn;
        }
        catch
        {
            // 建连失败：回退计数，让调用方（Publisher）抛异常 → Relay 重试。
            _ = Interlocked.Decrement(ref _created);
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        // 排空池中连接并关闭。
        while (_pool.Reader.TryRead(out var conn))
        {
            try { await conn.DisposeAsync(); }
            catch { /* 忽略关闭异常 */ }
        }
    }
}

/// <summary>
/// channel + 连接的归还语义封装。
/// 用法：<code>await using var lease = await mgr.GetChannelAsync(ct); var ch = lease.Channel; ...</code>
///
/// <para>
/// 持有归还回调（而非具体 <see cref="RabbitMqConnectionManager"/> 引用）让本类可独立于实现构造 ——
/// 单测可注入 fake 回调 + fake channel，无需启动真实 broker。
/// </para>
/// </summary>
public sealed class RabbitMqChannelLease : IAsyncDisposable
{
    /// <summary>借出的 channel（单线程使用，禁止并发 publish）。</summary>
    public IChannel Channel { get; }

    private readonly Func<ValueTask> _onDispose;

    /// <summary>
    /// 构造。<paramref name="onDispose"/> 在 DisposeAsync 时调用（归还连接等清理）。
    /// </summary>
    public RabbitMqChannelLease(IChannel channel, Func<ValueTask> onDispose)
    {
        Channel = channel;
        _onDispose = onDispose;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try { await Channel.DisposeAsync(); }
        catch { /* channel 关闭异常忽略 */ }
        await _onDispose();
    }
}

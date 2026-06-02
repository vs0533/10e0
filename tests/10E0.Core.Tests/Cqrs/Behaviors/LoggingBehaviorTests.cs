using Microsoft.Extensions.Logging;
using TenE0.Core.Abstractions;
using TenE0.Core.Cqrs.Behaviors;

namespace TenE0.Core.Tests.Cqrs.Behaviors;

public sealed class LoggingBehaviorTests
{
    /* ------------------------------------------------------------------ */
    /* Inline command + handler per test class to avoid parallel conflicts  */
    /* ------------------------------------------------------------------ */

    private sealed record Cmd1(string Value) : ICommand<string>;
    private sealed class Handler1 : ICommandHandler<Cmd1, string>
    {
        public Task<string> HandleAsync(Cmd1 command, CancellationToken ct) => Task.FromResult(command.Value);
    }

    /* Helper to capture ILogger calls as formatted strings */
    sealed class LoggerCapture<T> : ILogger<T>
    {
        public List<string> Messages { get; } = new();
        public List<(string Message, Exception? Ex)> ErrorMessages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel level) => true;

        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var formatted = formatter(state, exception);
            if (level == LogLevel.Error)
                ErrorMessages.Add((formatted, exception));
            else
                Messages.Add(formatted);
        }
    }

    #region 1. HandleAsync_Success_ShouldCallNext

    [Fact]
    public async Task HandleAsync_Success_ShouldCallNext()
    {
        // Arrange
        var called = false;
        CommandHandlerDelegate<string> next = _ =>
        {
            called = true;
            return Task.FromResult("ok");
        };

        var logger = new LoggerCapture<LoggingBehavior<Cmd1, string>>();
        var sut = new LoggingBehavior<Cmd1, string>(logger);
        var cmd = new Cmd1("test");

        // Act
        await sut.HandleAsync(cmd, next, CancellationToken.None);

        // Assert
        called.Should().BeTrue();
        logger.Messages.Should().HaveCount(2); // start + complete
    }

    #endregion

    #region 2. HandleAsync_Success_ShouldLogCompletion

    [Fact]
    public async Task HandleAsync_Success_ShouldLogCompletion()
    {
        // Arrange
        CommandHandlerDelegate<string> next = _ => Task.FromResult("ok");
        var logger = new LoggerCapture<LoggingBehavior<Cmd1, string>>();
        var sut = new LoggingBehavior<Cmd1, string>(logger);
        var cmd = new Cmd1("test");

        // Act
        await sut.HandleAsync(cmd, next, CancellationToken.None);

        // Assert
        var completionLog = logger.Messages.Last();
        completionLog.Should().Contain("completed");
        completionLog.Should().Contain("Cmd1");
        completionLog.Should().Contain("ms");
    }

    #endregion

    #region 3. HandleAsync_Failure_ShouldRethrow

    [Fact]
    public async Task HandleAsync_Failure_ShouldRethrow()
    {
        // Arrange
        var expected = new InvalidOperationException("boom");
        CommandHandlerDelegate<string> next = _ => throw expected;

        var logger = new LoggerCapture<LoggingBehavior<Cmd1, string>>();
        var sut = new LoggingBehavior<Cmd1, string>(logger);
        var cmd = new Cmd1("fail");

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.HandleAsync(cmd, next, CancellationToken.None));
        exception.Should().BeSameAs(expected);
    }

    #endregion

    #region 4. HandleAsync_Failure_ShouldLogError

    [Fact]
    public async Task HandleAsync_Failure_ShouldLogError()
    {
        // Arrange
        CommandHandlerDelegate<string> next = _ => throw new InvalidOperationException("boom");
        var logger = new LoggerCapture<LoggingBehavior<Cmd1, string>>();
        var sut = new LoggingBehavior<Cmd1, string>(logger);
        var cmd = new Cmd1("fail");

        // Act
        try { await sut.HandleAsync(cmd, next, CancellationToken.None); }
        catch { /* expected */ }

        // Assert
        logger.ErrorMessages.Should().HaveCount(1);
        logger.ErrorMessages[0].Message.Should().Contain("failed");
        logger.ErrorMessages[0].Ex.Should().BeAssignableTo<InvalidOperationException>();
    }

    #endregion

    #region 5. HandleAsync_ShouldIncludeCommandName

    [Fact]
    public async Task HandleAsync_ShouldIncludeCommandName()
    {
        // Arrange
        CommandHandlerDelegate<string> next = _ => Task.FromResult("ok");
        var logger = new LoggerCapture<LoggingBehavior<Cmd1, string>>();
        var sut = new LoggingBehavior<Cmd1, string>(logger);
        var cmd = new Cmd1("name-check");

        // Act
        await sut.HandleAsync(cmd, next, CancellationToken.None);

        // Assert
        var startLog = logger.Messages.First();
        startLog.Should().Contain("Cmd1");
        startLog.Should().Contain("starting");
    }

    #endregion
}

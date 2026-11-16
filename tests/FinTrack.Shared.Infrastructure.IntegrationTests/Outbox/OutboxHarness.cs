using System.Collections.Concurrent;
using FinTrack.Shared.Infrastructure.IntegrationTests.Ledger;
using FinTrack.Shared.Infrastructure.Ledger;
using FinTrack.Shared.Infrastructure.Mongo;
using FinTrack.Shared.Infrastructure.Outbox;
using FinTrack.Shared.Infrastructure.Owners;
using FinTrack.Shared.Kernel.Events;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Ledger;
using FinTrack.Shared.Kernel.Persistence;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Shared.Kernel.Time;
using FinTrack.Tests.Common;
using FluentResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace FinTrack.Shared.Infrastructure.IntegrationTests.Outbox;

/// <summary>
/// A service provider wired like the Function App, with its own ledger name, so the sweep of one
/// test never sees another test's messages.
/// </summary>
internal sealed class OutboxHarness : IAsyncDisposable
{
    private readonly ServiceProvider provider;

    public OutboxHarness(MongoDatabaseFixture fixture)
    {
        Ledger = LedgerName.From($"ob{Guid.NewGuid():N}"[..20]);
        Database = fixture.Database;

        provider = new ServiceCollection()
            .AddSingleton(fixture.Client)
            .AddSingleton(fixture.Database)
            .AddSingleton<IClock>(Clock)
            .AddSingleton<ILoggerFactory>(Logs)
            .AddSingleton(typeof(ILogger<>), typeof(Logger<>))
            .AddSingleton(Behaviour)
            .AddSingleton(Handled)
            .AddMongoLedger()
            .AddLedgerDocuments(new LedgerDocuments(Ledger).AppendOnly<TestJournalEntry>(JournalCollection))
            .AddIntegrationEventHandler<TestAccountOpened, AccountOpenedHandler>()
            .AddIntegrationEvent<TestNothingHappened>()
            .AddIntegrationEventHandler<TestDepositRecorded, DepositRecordedHandler>()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    public LedgerName Ledger { get; }

    public IMongoDatabase Database { get; }

    public FakeClock Clock { get; } = new();

    public CapturingLoggerFactory Logs { get; } = new();

    public HandlerBehaviour Behaviour { get; } = new();

    public HandledEvents Handled { get; } = new();

    public string JournalCollection => $"{Ledger.Value}_journal";

    public string OutboxCollection => LedgerCollections.OutboxCollection(Ledger);

    public OutboxDispatcher Dispatcher => provider.GetRequiredService<OutboxDispatcher>();

    public Task CreateIndexesAsync(CancellationToken cancellationToken) =>
        provider.GetServices<IMongoIndexInitializer>().OfType<LedgerIndexInitializer>().Single().CreateIndexesAsync(Database, cancellationToken);

    /// <summary>Commits <paramref name="events"/> as <paramref name="owner"/>, through the ledger the app uses (dispatching).</summary>
    public async Task<Result<LedgerCommitted>> CommitAsync(OwnerId owner, IReadOnlyList<IIntegrationEvent> events, CancellationToken cancellationToken)
    {
        await using var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ScopedOwnerContext>().Set(owner);
        var ledger = scope.ServiceProvider.GetRequiredService<ILedger>();

        var version = await ledger.GetVersionAsync(Ledger, cancellationToken);
        return await ledger.CommitAsync(new LedgerCommit(Ledger, version, events: events), cancellationToken);
    }

    /// <summary>Commits without dispatching, as if the process stopped right after the commit.</summary>
    public async Task CommitWithoutDispatchAsync(OwnerId owner, IReadOnlyList<IIntegrationEvent> events, CancellationToken cancellationToken)
    {
        await using var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ScopedOwnerContext>().Set(owner);
        var ledger = scope.ServiceProvider.GetRequiredService<MongoLedger>();

        var version = await ledger.GetVersionAsync(Ledger, cancellationToken);
        (await ledger.CommitAsync(new LedgerCommit(Ledger, version, events: events), cancellationToken)).IsSuccess.ShouldBeTrue();
    }

    /// <summary>The journal entries the handlers wrote for <paramref name="owner"/>.</summary>
    public async Task<IReadOnlyList<TestJournalEntry>> JournalOfAsync(OwnerId owner, CancellationToken cancellationToken) =>
        await Database.GetCollection<TestJournalEntry>(JournalCollection)
            .Find(Builders<TestJournalEntry>.Filter.Eq(entry => entry.OwnerId, owner))
            .ToListAsync(cancellationToken);

    public Task<OutboxMessage> MessageAsync(Guid eventId, CancellationToken cancellationToken) =>
        Database.GetCollection<OutboxMessage>(OutboxCollection)
            .Find(Builders<OutboxMessage>.Filter.Eq(message => message.Id, eventId))
            .SingleAsync(cancellationToken);

    public ValueTask DisposeAsync() => provider.DisposeAsync();
}

/// <summary>How the test handlers behave; tests change it between deliveries.</summary>
internal sealed class HandlerBehaviour
{
    public string? FailWithCode { get; set; }

    public bool Throw { get; set; }
}

/// <summary>Every delivery a handler saw: event and the owner of the scope it ran in.</summary>
internal sealed class HandledEvents
{
    private readonly ConcurrentQueue<(Guid EventId, OwnerId Owner)> deliveries = new();

    public IReadOnlyList<(Guid EventId, OwnerId Owner)> Deliveries => [.. deliveries];

    public void Add(Guid eventId, OwnerId owner) => deliveries.Enqueue((eventId, owner));
}

/// <summary>
/// An idempotent handler, as every real handler must be: it writes one journal entry per event
/// and records the handled event in the same commit.
/// </summary>
internal sealed class AccountOpenedHandler(ILedger ledger, IOwnerContext owner, IClock clock, HandledEvents handled, LedgerCollections collections)
    : IIntegrationEventHandler<TestAccountOpened>
{
    public async Task<Result> HandleAsync(TestAccountOpened integrationEvent, CancellationToken cancellationToken)
    {
        handled.Add(integrationEvent.EventId, owner.OwnerId);

        var ledgerName = collections.Ledgers.Single();
        var version = await ledger.GetVersionAsync(ledgerName, cancellationToken);
        var entry = new TestJournalEntry
        {
            Id = Guid.CreateVersion7(),
            OwnerId = owner.OwnerId,
            RecordedAt = clock.UtcNow,
            Note = $"handled {integrationEvent.EventId}",
        };

        var result = await ledger.CommitAsync(
            new LedgerCommit(ledgerName, version, appends: [entry], handledEvent: new ProcessedEvent("testledger.on_account_opened", integrationEvent.EventId)),
            cancellationToken);

        return result.ToResult();
    }
}

/// <summary>A handler whose outcome the test controls.</summary>
internal sealed class DepositRecordedHandler(IOwnerContext owner, HandledEvents handled, HandlerBehaviour behaviour)
    : IIntegrationEventHandler<TestDepositRecorded>
{
    public Task<Result> HandleAsync(TestDepositRecorded integrationEvent, CancellationToken cancellationToken)
    {
        handled.Add(integrationEvent.EventId, owner.OwnerId);

        if (behaviour.Throw)
        {
            throw new InvalidOperationException("Simulated handler bug.");
        }

        return Task.FromResult(behaviour.FailWithCode is { } code
            ? Result.Fail(AppError.BusinessRule(code))
            : Result.Ok());
    }
}

[EventName("testledger.deposit_recorded")]
internal sealed record TestDepositRecorded : IIntegrationEvent
{
    public required Guid EventId { get; init; }

    public required OwnerId OwnerId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required decimal Amount { get; init; }
}

[EventName("testledger.nothing_happened")]
internal sealed record TestNothingHappened : IIntegrationEvent
{
    public required Guid EventId { get; init; }

    public required OwnerId OwnerId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }
}

/// <summary>Captures every log line, formatted, so tests can check what is logged.</summary>
internal sealed class CapturingLoggerFactory : ILoggerFactory
{
    private readonly ConcurrentQueue<string> lines = new();

    public IReadOnlyList<string> Lines => [.. lines];

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(lines);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Enqueue($"{logLevel}: {formatter(state, exception)} {state}");
    }
}

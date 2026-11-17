using FinTrack.Budget.Application;
using FinTrack.Budget.Application.Buckets;
using FinTrack.Budget.Application.Journal;
using FinTrack.Budget.Application.Settings;
using FinTrack.Budget.Domain.Buckets;
using FinTrack.Budget.Domain.Journal;
using FinTrack.Budget.Domain.Settings;
using FinTrack.Shared.Infrastructure.Ledger;
using FinTrack.Shared.Infrastructure.Mongo;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FinTrack.Budget.Infrastructure;

/// <summary>Registers the Budget context.</summary>
public static class BudgetServiceCollectionExtensions
{
    /// <summary>
    /// Registers Budget: the storage mapping, its ledger and collections, its indexes, the store,
    /// and the Application services (state loader, validators). Handlers are registered by the
    /// mediator's source generator in the host. Needs the shared MongoDB setup and ledger
    /// (<c>AddFinTrackMongo</c>, <c>AddMongoLedger</c>) and an <c>IClock</c>.
    /// </summary>
    /// <remarks>
    /// The Application services are registered here because Application may not reference the
    /// dependency-injection package (architecture guard); Infrastructure references Application anyway.
    /// </remarks>
    public static IServiceCollection AddBudget(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        BudgetMongoMapping.Register();

        services.AddLedgerDocuments(new LedgerDocuments(BudgetLedger.Name)
            .Mutable<OwnerSettings>(BudgetCollections.Settings)
            .Mutable<Bucket>(BudgetCollections.Buckets)
            .AppendOnly<JournalEntry>(BudgetCollections.Journal));
        services.AddMongoIndexInitializer<BudgetIndexInitializer>();

        services.TryAddScoped<IBudgetStore, MongoBudgetStore>();
        services.TryAddScoped<BudgetStateLoader>();
        services.TryAddScoped<BucketEntryWriter>();

        services.TryAddEnumerable(ServiceDescriptor.Scoped<IValidator<SaveOwnerSettings>, SaveOwnerSettingsValidator>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IValidator<CreateBucket>, CreateBucketValidator>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IValidator<RenameBucket>, RenameBucketValidator>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IValidator<AddOpeningBalance>, AddOpeningBalanceValidator>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IValidator<AddDeposit>, AddDepositValidator>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IValidator<AddSpend>, AddSpendValidator>());

        return services;
    }
}

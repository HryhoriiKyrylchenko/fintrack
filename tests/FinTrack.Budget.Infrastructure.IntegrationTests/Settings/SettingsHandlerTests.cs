using FinTrack.Budget.Application;
using FinTrack.Budget.Application.Settings;
using FinTrack.Budget.Contracts.V1.Settings;
using FinTrack.Budget.Domain;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Ledger;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Tests.Common;
using FluentResults;
using Microsoft.Extensions.DependencyInjection;

namespace FinTrack.Budget.Infrastructure.IntegrationTests.Settings;

/// <summary>Saving and reading settings through the real ledger and MongoDB, owner by owner.</summary>
public sealed class SettingsHandlerTests(BudgetFixture fixture)
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task BeforeTheFirstSave_SettingsAreNotFound()
    {
        var result = await GetAsync(BudgetFixture.NewOwner());

        result.ShouldFailWith(BudgetErrorCodes.SettingsNotFound, ErrorKind.NotFound);
    }

    [Fact]
    public async Task SavedSettings_AreReturned()
    {
        var owner = BudgetFixture.NewOwner();

        (await SaveAsync(owner, new SaveOwnerSettings(Currency.Eur, "Europe/Berlin", "de-DE"))).IsSuccess.ShouldBeTrue();

        (await GetAsync(owner)).Value.ShouldBe(new OwnerSettingsDto(Currency.Eur, "Europe/Berlin", "de-DE"));
    }

    [Fact]
    public async Task Change_ReplacesTheDocument_UnderTheNextVersion()
    {
        var owner = BudgetFixture.NewOwner();
        await SaveAsync(owner, new SaveOwnerSettings(Currency.Pln, "Europe/Warsaw", "pl-PL"));
        var first = await ReadAsync(owner);

        await SaveAsync(owner, new SaveOwnerSettings(Currency.Usd, "Europe/Warsaw", "en-US"));

        var second = await ReadAsync(owner);
        second!.Id.ShouldBe(first!.Id);
        second.Version.ShouldBe(2);
        second.ReportingCurrency.ShouldBe(Currency.Usd);
        second.Locale.ShouldBe("en-US");
        (await LedgerVersionAsync(owner)).ShouldBe(2);
    }

    [Fact]
    public async Task RepeatedSave_WritesNothing()
    {
        var owner = BudgetFixture.NewOwner();
        var command = new SaveOwnerSettings(Currency.Pln, "Europe/Warsaw", "pl-PL");
        await SaveAsync(owner, command);

        (await SaveAsync(owner, command)).IsSuccess.ShouldBeTrue();

        (await ReadAsync(owner))!.Version.ShouldBe(1);
        (await LedgerVersionAsync(owner)).ShouldBe(1);
    }

    [Fact]
    public async Task InvalidSave_WritesNothing()
    {
        var owner = BudgetFixture.NewOwner();

        (await SaveAsync(owner, new SaveOwnerSettings(Currency.Pln, "Mars/Olympus", "pl-PL")))
            .ShouldFailWith(BudgetErrorCodes.SettingsTimeZoneUnknown, ErrorKind.Validation);

        (await ReadAsync(owner)).ShouldBeNull();
        (await LedgerVersionAsync(owner)).ShouldBe(0);
    }

    [Fact]
    public async Task Owners_NeverSeeEachOthersSettings()
    {
        var first = BudgetFixture.NewOwner();
        var second = BudgetFixture.NewOwner();
        await SaveAsync(first, new SaveOwnerSettings(Currency.Pln, "Europe/Warsaw", "pl-PL"));

        (await GetAsync(second)).ShouldFailWith(BudgetErrorCodes.SettingsNotFound, ErrorKind.NotFound);

        await SaveAsync(second, new SaveOwnerSettings(Currency.Usd, "America/New_York", "en-US"));

        (await GetAsync(first)).Value.ShouldBe(new OwnerSettingsDto(Currency.Pln, "Europe/Warsaw", "pl-PL"));
        (await GetAsync(second)).Value.ShouldBe(new OwnerSettingsDto(Currency.Usd, "America/New_York", "en-US"));
    }

    private async Task<Result> SaveAsync(OwnerId owner, SaveOwnerSettings command)
    {
        await using var scope = fixture.ScopeFor(owner);
        return await ActivatorUtilities.CreateInstance<SaveOwnerSettingsHandler>(scope.ServiceProvider).Handle(command, Cancellation);
    }

    private async Task<Result<OwnerSettingsDto>> GetAsync(OwnerId owner)
    {
        await using var scope = fixture.ScopeFor(owner);
        return await ActivatorUtilities.CreateInstance<GetOwnerSettingsHandler>(scope.ServiceProvider).Handle(new GetOwnerSettings(), Cancellation);
    }

    private async Task<Domain.Settings.OwnerSettings?> ReadAsync(OwnerId owner)
    {
        await using var scope = fixture.ScopeFor(owner);
        return await scope.ServiceProvider.GetRequiredService<IBudgetStore>().GetSettingsAsync(Cancellation);
    }

    private async Task<long> LedgerVersionAsync(OwnerId owner)
    {
        await using var scope = fixture.ScopeFor(owner);
        return await scope.ServiceProvider.GetRequiredService<ILedger>().GetVersionAsync(BudgetLedger.Name, Cancellation);
    }
}

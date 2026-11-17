using FinTrack.Budget.Application.Settings;
using FinTrack.Budget.Contracts.V1.Settings;
using FinTrack.Budget.Domain;
using FinTrack.Budget.Domain.Settings;
using FinTrack.Shared.Kernel.Ids;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Tests.Common;

namespace FinTrack.Budget.Application.Tests.Settings;

/// <summary>
/// Saving and reading the owner's settings (plan 1a D7) against in-memory fakes: version read first,
/// one commit per real change, nothing written for a repeat.
/// </summary>
public class OwnerSettingsHandlerTests
{
    private readonly CallLog log = new();
    private readonly FakeLedger ledger;
    private readonly FakeBudgetStore store;
    private readonly FakeClock clock = new();

    public OwnerSettingsHandlerTests()
    {
        ledger = new FakeLedger(log);
        store = new FakeBudgetStore(log);
    }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FirstSave_CreatesTheSettings_WithAServerGeneratedVersion7Id()
    {
        var result = await Save(new SaveOwnerSettings(Currency.Pln, "Europe/Warsaw", "pl-PL"));

        result.IsSuccess.ShouldBeTrue();
        var commit = ledger.Commits.ShouldHaveSingleItem();
        commit.Ledger.ShouldBe(BudgetLedger.Name);
        commit.ExpectedVersion.ShouldBe(0);
        commit.Appends.ShouldBeEmpty();
        var saved = commit.Saves.ShouldHaveSingleItem().ShouldBeOfType<OwnerSettings>();
        saved.Version.ShouldBe(0);
        saved.OwnerId.ShouldBe(FakeOwnerContext.Owner);
        UuidV7.IsValid(saved.Id).ShouldBeTrue();
        saved.ReportingCurrency.ShouldBe(Currency.Pln);
        saved.TimeZone.ShouldBe("Europe/Warsaw");
        saved.Locale.ShouldBe("pl-PL");
    }

    [Fact]
    public async Task Save_ReadsTheVersionBeforeTheStore_AndCommitsAgainstIt()
    {
        ledger.Version = 7;

        await Save(new SaveOwnerSettings(Currency.Pln, "Europe/Warsaw", "pl-PL"));

        log.Calls.ShouldBe(["version", "settings", "buckets", "journal", "version", "commit"]);
        ledger.Commits.ShouldHaveSingleItem().ExpectedVersion.ShouldBe(7);
    }

    [Fact]
    public async Task SaveWithOtherValues_ReplacesTheStoredDocument()
    {
        ledger.Version = 4;
        store.Settings = StoredSettings.Of(Currency.Pln, "Europe/Warsaw", "pl-PL", version: 2);

        var result = await Save(new SaveOwnerSettings(Currency.Eur, "Europe/Berlin", "de-DE"));

        result.IsSuccess.ShouldBeTrue();
        var saved = ledger.Commits.ShouldHaveSingleItem().Saves.ShouldHaveSingleItem().ShouldBeOfType<OwnerSettings>();
        saved.Id.ShouldBe(StoredSettings.Id);
        saved.Version.ShouldBe(2);
        saved.ReportingCurrency.ShouldBe(Currency.Eur);
        saved.TimeZone.ShouldBe("Europe/Berlin");
        saved.Locale.ShouldBe("de-DE");
    }

    [Theory]
    [InlineData("EUR", "Europe/Warsaw", "pl-PL")]
    [InlineData("PLN", "Europe/Berlin", "pl-PL")]
    [InlineData("PLN", "Europe/Warsaw", "pl")]
    public async Task SaveChangingAnyOneValue_IsCommitted(string currency, string timeZone, string locale)
    {
        store.Settings = StoredSettings.Of(Currency.Pln, "Europe/Warsaw", "pl-PL");

        await Save(new SaveOwnerSettings(Currency.FromCode(currency).Value, timeZone, locale));

        ledger.Commits.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task SaveWithTheStoredValues_SucceedsAndWritesNothing()
    {
        store.Settings = StoredSettings.Of(Currency.Pln, "Europe/Warsaw", "pl-PL");

        var result = await Save(new SaveOwnerSettings(Currency.Pln, "Europe/Warsaw", "pl-PL"));

        result.IsSuccess.ShouldBeTrue();
        ledger.Commits.ShouldBeEmpty();
        log.Calls.ShouldNotContain("commit");
    }

    [Theory]
    [InlineData("Mars/Olympus", "pl-PL", BudgetErrorCodes.SettingsTimeZoneUnknown)]
    [InlineData("Europe/Warsaw", "xx-XX", BudgetErrorCodes.SettingsLocaleUnknown)]
    public async Task SaveWithUnknownValues_FailsAndWritesNothing(string timeZone, string locale, string code)
    {
        var result = await Save(new SaveOwnerSettings(Currency.Pln, timeZone, locale));

        result.ShouldFailWith(code, ErrorKind.Validation);
        log.Calls.ShouldNotContain("commit");
    }

    [Fact]
    public async Task SaveWithUnknownValues_OverStoredSettings_FailsAndWritesNothing()
    {
        store.Settings = StoredSettings.Of(Currency.Pln, "Europe/Warsaw", "pl-PL");

        var result = await Save(new SaveOwnerSettings(Currency.Pln, "Mars/Olympus", "pl-PL"));

        result.ShouldFailWith(BudgetErrorCodes.SettingsTimeZoneUnknown, ErrorKind.Validation);
        ledger.Commits.ShouldBeEmpty();
    }

    [Fact]
    public async Task Save_ReturnsTheLedgerConflict_SoThePipelineRetries()
    {
        ledger.Conflict = true;

        var result = await Save(new SaveOwnerSettings(Currency.Pln, "Europe/Warsaw", "pl-PL"));

        result.ShouldFailWith(ErrorCodes.ConcurrencyConflict, ErrorKind.Conflict);
    }

    [Fact]
    public async Task Get_BeforeTheFirstSave_IsSettingsNotFound()
    {
        var result = await Get();

        result.ShouldFailWith(BudgetErrorCodes.SettingsNotFound, ErrorKind.NotFound);
    }

    [Fact]
    public async Task Get_ReturnsTheStoredValues()
    {
        store.Settings = StoredSettings.Of(Currency.Usd, "America/New_York", "en-US");

        var result = await Get();

        result.Value.ShouldBe(new OwnerSettingsDto(Currency.Usd, "America/New_York", "en-US"));
    }

    [Fact]
    public async Task Get_ReadsTheVersionBeforeTheStore()
    {
        await Get();

        log.Calls.ShouldBe(["version", "settings", "buckets", "journal", "version"]);
    }

    [Fact]
    public void Validator_AcceptsAnOrdinaryCommand() =>
        new SaveOwnerSettingsValidator().Validate(new SaveOwnerSettings(Currency.Pln, "Europe/Warsaw", "pl-PL")).IsValid.ShouldBeTrue();

    [Theory]
    [InlineData(true, "Europe/Warsaw", "pl-PL", "ReportingCurrency")]
    [InlineData(false, "", "pl-PL", "TimeZone")]
    [InlineData(false, "Europe/Warsaw", "", "Locale")]
    [InlineData(false, "Europe/Warsaw", "pl-PL-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", "Locale")]
    public void Validator_RejectsMissingOrOversizedFields(bool nullCurrency, string timeZone, string locale, string field)
    {
        var command = new SaveOwnerSettings(nullCurrency ? null! : Currency.Pln, timeZone, locale);

        new SaveOwnerSettingsValidator().Validate(command).Errors.ShouldContain(error => error.PropertyName == field);
    }

    [Fact]
    public void Validator_RejectsAnOversizedTimeZone() =>
        new SaveOwnerSettingsValidator().Validate(new SaveOwnerSettings(Currency.Pln, new string('a', 65), "pl-PL"))
            .Errors.ShouldContain(error => error.PropertyName == "TimeZone");

    private Task<FluentResults.Result> Save(SaveOwnerSettings command) =>
        new SaveOwnerSettingsHandler(new BudgetStateLoader(ledger, store), ledger, new FakeOwnerContext(), clock)
            .Handle(command, Cancellation).AsTask();

    private Task<FluentResults.Result<OwnerSettingsDto>> Get() =>
        new GetOwnerSettingsHandler(new BudgetStateLoader(ledger, store))
            .Handle(new GetOwnerSettings(), Cancellation).AsTask();
}

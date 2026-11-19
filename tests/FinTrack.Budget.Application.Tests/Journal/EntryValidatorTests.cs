using FinTrack.Budget.Application.Journal;
using FinTrack.Shared.Kernel.Monetary;
using FluentValidation.Results;

namespace FinTrack.Budget.Application.Tests.Journal;

/// <summary>
/// The shape checks of every entry command share one set of note and tag rules (<see cref="EntryRules"/>). Each
/// validator must apply them: an invalid note or tag that slipped through would reach the domain, which treats it
/// as a bug, and the owner would see a server error instead of a field message.
/// </summary>
public class EntryValidatorTests
{
    private static readonly DateOnly Today = new(2026, 1, 15);

    public static TheoryData<string> Commands => ["deposit", "transfer", "reversal"];

    [Theory]
    [MemberData(nameof(Commands))]
    public void Note_WithSurroundingSpace_IsInvalid(string command) =>
        Failures(command, note: " Salary").ShouldContain(("Note", "NoSurroundingWhiteSpace"));

    [Theory]
    [MemberData(nameof(Commands))]
    public void Note_WithATab_IsInvalid(string command) =>
        Failures(command, note: "Sal" + '\t' + "ary").ShouldContain(("Note", "NoControlCharacters"));

    [Theory]
    [MemberData(nameof(Commands))]
    public void Tags_ThatRepeat_AreInvalid(string command) =>
        Failures(command, tags: ["work", "WORK"]).ShouldContain(("Tags", EntryRules.DistinctTags));

    [Theory]
    [MemberData(nameof(Commands))]
    public void Tag_WithSurroundingSpace_IsInvalid(string command) =>
        Failures(command, tags: [" work"]).ShouldContain(("Tags[0]", "NoSurroundingWhiteSpace"));

    [Theory]
    [MemberData(nameof(Commands))]
    public void TenTags_AreValid_ElevenAreTooMany(string command)
    {
        Failures(command, tags: [.. Enumerable.Range(1, 10).Select(number => $"t{number}")]).ShouldBeEmpty();
        Failures(command, tags: [.. Enumerable.Range(1, 11).Select(number => $"t{number}")]).ShouldContain(("Tags", EntryRules.TooManyTags));
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public void OrdinaryCommand_IsValid(string command) =>
        Failures(command, note: "First line\nsecond line", tags: ["work", "family"]).ShouldBeEmpty();

    [Fact]
    public void Transfer_WithoutItsAmounts_IsInvalid()
    {
        var failures = Pairs(new AddTransferValidator().Validate(
            new AddTransfer(Guid.CreateVersion7(), null!, null!, null, Today, Guid.CreateVersion7(), null, Guid.CreateVersion7(), null, null, [])));

        failures.ShouldContain(("FromAmount", "NotNullValidator"));
        failures.ShouldContain(("ToAmount", "NotNullValidator"));
    }

    [Fact]
    public void Deposit_WithoutAnAmount_IsInvalid() =>
        Pairs(new AddDepositValidator().Validate(new AddDeposit(Guid.CreateVersion7(), null!, Today, Guid.CreateVersion7(), null, null, [])))
            .ShouldContain(("Amount", "NotNullValidator"));

    private static List<(string Field, string Rule)> Failures(string command, string? note = null, IReadOnlyList<string>? tags = null)
    {
        var money = Money.From(10m, Currency.Pln);
        var result = command switch
        {
            "deposit" => new AddDepositValidator().Validate(new AddDeposit(Guid.CreateVersion7(), money, Today, Guid.CreateVersion7(), null, note, tags ?? [])),
            "transfer" => new AddTransferValidator().Validate(
                new AddTransfer(Guid.CreateVersion7(), money, money, null, Today, Guid.CreateVersion7(), null, Guid.CreateVersion7(), null, note, tags ?? [])),
            _ => new ReverseEntryValidator().Validate(new ReverseEntry(Guid.CreateVersion7(), Guid.CreateVersion7(), Today, note, tags ?? [])),
        };
        return Pairs(result);
    }

    private static List<(string Field, string Rule)> Pairs(ValidationResult result) =>
        [.. result.Errors.Select(error => (error.PropertyName, error.ErrorCode))];
}

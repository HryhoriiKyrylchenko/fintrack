using System.Reflection;
using FinTrack.Shared.Kernel.Results;

namespace FinTrack.Shared.Tests.Results;

/// <summary>The shared error codes are a public contract: the UI maps each one to text.</summary>
public class ErrorCodesTests
{
    private static readonly IReadOnlyList<string> Codes =
    [
        .. typeof(ErrorCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field is { IsLiteral: true, FieldType.Name: nameof(String) })
            .Select(field => (string)field.GetRawConstantValue()!),
    ];

    [Fact]
    public void SharedCodes_AreTheExpectedOnes() =>
        Codes.Order(StringComparer.Ordinal).ShouldBe(
        [
            "concurrency.conflict",
            "currency.unknown",
            "id.invalid",
            "money.amount_not_positive",
            "money.too_many_decimals",
            "validation.failed",
        ]);

    [Fact]
    public void SharedCodes_AreAcceptedByAppError() =>
        Codes.ShouldAllBe(code => AppError.Validation(code).Code == code);
}

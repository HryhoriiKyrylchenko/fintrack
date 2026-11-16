using FinTrack.Budget.Contracts.V1.Settings;
using FinTrack.Budget.Domain;
using FinTrack.Shared.Kernel.Results;
using FluentResults;
using Mediator;

namespace FinTrack.Budget.Application.Settings;

/// <summary>Answers <see cref="GetOwnerSettings"/>: the stored settings, or <c>settings.not_found</c> (404) before the first save.</summary>
public sealed class GetOwnerSettingsHandler(BudgetStateLoader loader) : IQueryHandler<GetOwnerSettings, Result<OwnerSettingsDto>>
{
    /// <inheritdoc />
    public async ValueTask<Result<OwnerSettingsDto>> Handle(GetOwnerSettings query, CancellationToken cancellationToken)
    {
        var state = await loader.LoadAsync(cancellationToken).ConfigureAwait(false);

        return state.Settings is { } settings
            ? Result.Ok(new OwnerSettingsDto(settings.ReportingCurrency, settings.TimeZone, settings.Locale))
            : Result.Fail<OwnerSettingsDto>(AppError.NotFound(BudgetErrorCodes.SettingsNotFound));
    }
}

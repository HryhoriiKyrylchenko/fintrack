using FinTrack.Budget.Domain.Settings;
using FinTrack.Shared.Kernel.Ledger;
using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Persistence;
using FinTrack.Shared.Kernel.Time;
using FluentResults;
using FluentValidation;
using Mediator;

namespace FinTrack.Budget.Application.Settings;

/// <summary>
/// Saves all of the owner's settings at once (<c>PUT /api/v1/settings</c>): creates them on first
/// run, replaces them afterwards. Saving the values that are already stored writes nothing, so a
/// repeated request is harmless (invariant 13).
/// </summary>
/// <param name="ReportingCurrency">The currency totals are reported in.</param>
/// <param name="TimeZone">IANA time zone ID, exact form.</param>
/// <param name="Locale">Name of a predefined culture, exact form.</param>
public sealed record SaveOwnerSettings(Currency ReportingCurrency, string TimeZone, string Locale) : ICommand<Result>, ILedgerCommand;

/// <summary>Shape checks; whether the time zone and locale exist is a domain rule (<see cref="OwnerSettings"/>).</summary>
public sealed class SaveOwnerSettingsValidator : AbstractValidator<SaveOwnerSettings>
{
    /// <summary>Longest time zone ID accepted; the longest IANA IDs have about 30 characters.</summary>
    public const int MaxTimeZoneLength = 64;

    /// <summary>Longest locale name accepted; predefined culture names are much shorter.</summary>
    public const int MaxLocaleLength = 35;

    /// <summary>Creates the rules.</summary>
    public SaveOwnerSettingsValidator()
    {
        RuleFor(command => command.ReportingCurrency).NotNull();
        RuleFor(command => command.TimeZone).NotEmpty().MaximumLength(MaxTimeZoneLength);
        RuleFor(command => command.Locale).NotEmpty().MaximumLength(MaxLocaleLength);
    }
}

/// <summary>
/// Reads the state (version first), creates or changes the settings and commits them under the
/// version it read. The settings' ID is generated here: one document per owner, created by the
/// system on the first save, so the request carries no ID (ARCHITECTURE 2.2).
/// </summary>
public sealed class SaveOwnerSettingsHandler(BudgetStateLoader loader, ILedger ledger, IOwnerContext owner, IClock clock)
    : ICommandHandler<SaveOwnerSettings, Result>
{
    /// <inheritdoc />
    public async ValueTask<Result> Handle(SaveOwnerSettings command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var state = await loader.LoadAsync(cancellationToken).ConfigureAwait(false);

        if (state.Settings is { } stored && stored.HasValues(command.ReportingCurrency, command.TimeZone, command.Locale))
        {
            return Result.Ok();
        }

        var settings = state.Settings is null
            ? OwnerSettings.Create(Guid.CreateVersion7(clock.UtcNow), owner.OwnerId, command.ReportingCurrency, command.TimeZone, command.Locale)
            : state.Settings.Change(command.ReportingCurrency, command.TimeZone, command.Locale);

        if (settings.IsFailed)
        {
            return settings.ToResult();
        }

        var committed = await ledger.CommitAsync(new LedgerCommit(BudgetLedger.Name, state.Version, saves: [settings.Value]), cancellationToken)
            .ConfigureAwait(false);

        return committed.ToResult();
    }
}

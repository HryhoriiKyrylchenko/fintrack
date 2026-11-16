using FluentResults;
using Mediator;

namespace FinTrack.Budget.Contracts.V1.Settings;

/// <summary>
/// The current owner's settings. Fails with <c>NotFound</c> <c>settings.not_found</c> before they
/// are saved for the first time (first run). <c>GET /api/v1/settings</c>.
/// </summary>
public sealed record GetOwnerSettings : IQuery<Result<OwnerSettingsDto>>;

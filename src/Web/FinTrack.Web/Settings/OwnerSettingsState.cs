using FinTrack.Budget.Contracts.V1.Settings;
using FinTrack.Web.Api;

namespace FinTrack.Web.Settings;

/// <summary>Where the app stands with the owner's settings.</summary>
public enum SettingsStatus
{
    /// <summary>Not asked yet.</summary>
    Loading,

    /// <summary>The settings are saved; the app runs with them.</summary>
    Saved,

    /// <summary>First run: nothing saved yet, so only the setup screen is shown (plan 1a D7).</summary>
    NotSaved,

    /// <summary>The Api could not answer; <see cref="OwnerSettingsState.ErrorCode"/> says why.</summary>
    Failed,
}

/// <summary>
/// The owner's settings for the whole app. Loaded once before the first render (<c>Program.cs</c>),
/// so the locale is applied before anything is formatted; one instance for the app's lifetime.
/// </summary>
public sealed class OwnerSettingsState(ApiClient api)
{
    /// <summary>The Api code of "nothing saved yet".</summary>
    public const string NotFoundCode = "settings.not_found";

    private const string Path = "v1/settings";

    /// <summary>Raised after every change of <see cref="Status"/> or <see cref="Settings"/>.</summary>
    public event Action? Changed;

    /// <summary>Where the app stands.</summary>
    public SettingsStatus Status { get; private set; } = SettingsStatus.Loading;

    /// <summary>The saved settings; set when <see cref="Status"/> is <see cref="SettingsStatus.Saved"/>.</summary>
    public OwnerSettingsDto? Settings { get; private set; }

    /// <summary>Why loading failed; set when <see cref="Status"/> is <see cref="SettingsStatus.Failed"/>.</summary>
    public string? ErrorCode { get; private set; }

    /// <summary>Asks the Api for the settings.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var result = await api.GetAsync<OwnerSettingsDto>(Path, cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            Set(SettingsStatus.Saved, result.Value, errorCode: null);
        }
        else if (result.ErrorCode == NotFoundCode)
        {
            Set(SettingsStatus.NotSaved, settings: null, errorCode: null);
        }
        else
        {
            Set(SettingsStatus.Failed, settings: null, result.ErrorCode);
        }
    }

    /// <summary>Saves all settings; on success they become the current ones.</summary>
    public async Task<ApiResult> SaveAsync(SaveOwnerSettingsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await api.PutAsync(Path, request, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            Set(SettingsStatus.Saved, new OwnerSettingsDto(request.ReportingCurrency, request.TimeZone, request.Locale), errorCode: null);
        }

        return result;
    }

    private void Set(SettingsStatus status, OwnerSettingsDto? settings, string? errorCode)
    {
        Status = status;
        Settings = settings;
        ErrorCode = errorCode;
        Changed?.Invoke();
    }
}

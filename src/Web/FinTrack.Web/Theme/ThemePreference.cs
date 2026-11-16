using Microsoft.JSInterop;

namespace FinTrack.Web.Theme;

/// <summary>
/// Remembers the user's choice of light or dark in the browser (DESIGN 1.3). Without a choice the
/// theme follows the system setting.
/// </summary>
public sealed class ThemePreference(IJSRuntime js)
{
    private const string StorageKey = "fintrack.theme";
    private const string Dark = "dark";
    private const string Light = "light";

    /// <summary>The stored choice: <c>true</c> dark, <c>false</c> light, <c>null</c> follow the system.</summary>
    public async Task<bool?> GetAsync()
    {
        var stored = await js.InvokeAsync<string?>("localStorage.getItem", StorageKey).ConfigureAwait(false);
        return stored switch
        {
            Dark => true,
            Light => false,
            _ => null,
        };
    }

    /// <summary>Stores the user's choice.</summary>
    public async Task SetAsync(bool isDarkMode) =>
        await js.InvokeVoidAsync("localStorage.setItem", StorageKey, isDarkMode ? Dark : Light).ConfigureAwait(false);
}

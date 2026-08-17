using System.Net.Http;
using System.Text.Json;

namespace NetAudit.Core;

/// <param name="Sha256">
/// Контрольная сумма архива. Без неё автоустановка предупредит и попросит подтверждения:
/// ссылка приходит по сети, и запускать скачанное без сверки нельзя.
/// </param>
public record UpdateInfo(string Version, string Notes, string DownloadUrl, string? Sha256 = null);

public static class UpdateChecker
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public static async Task<UpdateInfo?> CheckAsync(string checkUrl, Version currentVersion)
    {
        try
        {
            var json = await Http.GetStringAsync(checkUrl).ConfigureAwait(false);
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var info = JsonSerializer.Deserialize<UpdateInfo>(json, opts);
            if (info is null) return null;
            if (Version.TryParse(info.Version, out var remote) && remote > currentVersion)
                return info;
        }
        catch { }
        return null;
    }
}

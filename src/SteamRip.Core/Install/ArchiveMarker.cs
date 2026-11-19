using System.Globalization;

namespace SteamRip.Core.Install;

/// <summary>
/// "This file arrived completely and the archive tool opened it" — a sidecar next to the archive.
/// Reuse across retries is only safe with it: without the marker any leftover file counted as
/// "already verified", so a truncated download or a mirror that served a <c>.torrent</c> instead of the archive was
/// retried forever against the same bytes.
/// </summary>
public static class ArchiveMarker
{
    public const string Suffix = ".ok";

    public static string PathFor(string archive) => archive + Suffix;

    /// <summary>True when the archive is there, was marked, and still has the size it was marked at.</summary>
    public static bool IsVerified(string archive, string? versionKey = null)
    {
        try
        {
            var marker = PathFor(archive);
            if (!File.Exists(archive) || !File.Exists(marker))
                return false;
            return IsVerified(new FileInfo(archive).Length, File.ReadAllText(marker), versionKey);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The marker rule without disk access: the recorded first line must equal the archive size.
    /// A null marker text means there is no marker, which is never verified.
    /// </summary>
    public static bool IsVerified(long archiveLength, string? markerText) =>
        IsVerified(archiveLength, markerText, null);

    /// <summary>The size line and the version line, both checked against what the caller expects.</summary>
    public static bool IsVerified(long archiveLength, string? markerText, string? versionKey) =>
        markerText is not null
        && markerText.Split('\n')[0].Trim() == archiveLength.ToString(CultureInfo.InvariantCulture)
        && VersionMatches(markerText, versionKey);

    /// <summary>True when the marker lines up with the release being installed; null accepts any (older markers).</summary>
    public static bool VersionMatches(string? markerText, string? versionKey)
    {
        if (versionKey is null)
            return true;
        var lines = (markerText ?? "").Split('\n');
        var recorded = lines.Length >= 3 ? lines[2].Trim() : "";
        return recorded.Length > 0
            && string.Equals(recorded, versionKey, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Records that this archive arrived and was opened. The version key ties the marker to the release it
    /// was downloaded for, so an archive left over from an older version is never reused as a newer one.
    /// </summary>
    public static void Mark(string archive, string? versionKey = null)
    {
        var info = new FileInfo(archive);
        File.WriteAllText(
            PathFor(archive),
            info.Length.ToString(CultureInfo.InvariantCulture)
                + "\n"
                + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
                + "\n"
                + (versionKey ?? "")
                + "\n"
        );
    }

    /// <summary>Forgets the marker so the next attempt re-downloads instead of reusing bad bytes.</summary>
    public static void Clear(string archive)
    {
        try
        {
            File.Delete(PathFor(archive));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // nothing to clear
        }
    }
}

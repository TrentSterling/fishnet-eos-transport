namespace FishNet.Transport.EOSNative.Migration
{
    /// <summary>
    /// Shared PUID normalization utility. Prevents format mismatches between
    /// conn.GetAddress() and ProductUserId.ToString().
    /// </summary>
    internal static class PuidUtils
    {
        /// <summary>
        /// Normalizes a PUID string: trims whitespace and lowercases.
        /// Returns null/empty unchanged.
        /// </summary>
        internal static string NormalizePuid(string puid)
        {
            if (string.IsNullOrEmpty(puid)) return puid;
            return puid.Trim().ToLowerInvariant();
        }
    }
}

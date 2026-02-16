using System;

namespace FishNet.Transport.EOSNative.Migration
{
    /// <summary>
    /// Outcome of a host migration attempt.
    /// </summary>
    public enum MigrationResult
    {
        /// <summary>Migration completed successfully.</summary>
        Success,

        /// <summary>Client failed to reconnect to the new host after all retry attempts.</summary>
        ReconnectFailed,

        /// <summary>New host's server failed to start within the expected time.</summary>
        ServerStartFailed,

        /// <summary>Migration exceeded the watchdog timeout and was forcibly ended.</summary>
        WatchdogTimeout,

        /// <summary>Lost lobby membership during migration (kicked or lobby destroyed).</summary>
        LobbyMembershipLost,

        /// <summary>Migration was cancelled via CancelMigration().</summary>
        Cancelled
    }

    /// <summary>
    /// Details about a completed (or failed) migration attempt.
    /// Passed via <see cref="HostMigrationManager.OnMigrationFinished"/>.
    /// </summary>
    public struct MigrationFinishedArgs
    {
        /// <summary>Whether the migration succeeded.</summary>
        public bool Success;

        /// <summary>The specific outcome.</summary>
        public MigrationResult Result;

        /// <summary>Whether we became the new host (true) or reconnected as client (false).</summary>
        public bool WasNewHost;

        /// <summary>How long the migration took in seconds.</summary>
        public float ElapsedSeconds;

        /// <summary>Number of reconnect attempts made (client only, 0 for host).</summary>
        public int ReconnectAttempts;
    }
}

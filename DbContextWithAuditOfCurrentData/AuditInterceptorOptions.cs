namespace DbContextWithAuditOfCurrentData;

/// <summary>
/// Options to configure the AuditSaveChangesInterceptor behavior.
/// </summary>
public class AuditInterceptorOptions
{
    /// <summary>
    /// Maximum number of rows to include in a single batched INSERT statement. Default: 500.
    /// </summary>
    public int MaxRowsPerBatch { get; set; } = 500;

    /// <summary>
    /// Maximum number of parameters allowed per command. If &lt;= 0 the interceptor will auto-detect per-provider limits.
    /// Default: -1 (auto-detect).
    /// </summary>
    public int MaxParametersPerCommand { get; set; } = -1;
}
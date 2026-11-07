using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Data.Common;

namespace DbContextWithAudit;

internal sealed class AuditDataPersister
{
    private const int SqlServerParameterLimit = 2100;

    private readonly int _maxRowsPerBatch;
    private readonly int _maxParametersPerCommand;
    private readonly ILogger _logger;

    public AuditDataPersister(
        int maxRowsPerBatch,
        int maxParametersPerCommand,
        ILogger? logger = null)
    {
        _maxRowsPerBatch = Math.Max(1, maxRowsPerBatch);
        _maxParametersPerCommand = maxParametersPerCommand < 64 ? -1 : maxParametersPerCommand;
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task PersistAsync(
        DbContext context,
        IReadOnlyCollection<AuditEntry> audits,
        DbTransaction? dbTransaction)
    {
        if (audits.Count == 0)
            return;

        var connection = context.Database.GetDbConnection();
        var closeAfter = false;
        try
        {
            closeAfter = await OpenConnectionIfNeededAsync(connection).ConfigureAwait(false);

            foreach (var group in audits.GroupBy(audit => audit.AuditTableName))
                await PersistAuditGroupAsync(context, connection, dbTransaction, group).ConfigureAwait(false);
        }
        finally
        {
            if (closeAfter)
                await connection.CloseAsync().ConfigureAwait(false);
        }
    }

    private static async Task<bool> OpenConnectionIfNeededAsync(DbConnection connection)
    {
        if (connection.State == System.Data.ConnectionState.Open)
            return false;

        await connection.OpenAsync().ConfigureAwait(false);
        return true;
    }

    private async Task PersistAuditGroupAsync(
        DbContext context,
        DbConnection connection,
        DbTransaction? dbTransaction,
        IGrouping<string, AuditEntry> group)
    {
        var entries = group.ToList();
        var columnOrder = GetColumnOrder(entries);
        if (columnOrder.Count == 0)
        {
            _logger.LogDebug("No columns determined for audit table {AuditTable}, skipping.", group.Key);
            return;
        }

        var batchSize = GetBatchSize(context, columnOrder.Count);
        var sqlGenerationHelper = context.GetService<ISqlGenerationHelper>();
        _logger.LogDebug("EffectiveMaxParams={EffectiveMaxParams}, batchSize={BatchSize} for audit table {AuditTable}",
            GetEffectiveMaxParameters(context), batchSize, group.Key);

        for (var offset = 0; offset < entries.Count; offset += batchSize)
        {
            var batch = entries.Skip(offset).Take(batchSize).ToList();
            await ExecuteAuditBatchAsync(connection, dbTransaction, sqlGenerationHelper, group.Key, columnOrder, batch).ConfigureAwait(false);
        }
    }

    private static List<string> GetColumnOrder(IReadOnlyCollection<AuditEntry> entries)
    {
        var columns = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            columns.AddRange(from column in entry.ColumnValues.Keys
                             where seen.Add(column)
                             select column);
        }

        return columns;
    }

    private int GetBatchSize(DbContext context, int columnCount)
    {
        var maxRowsByParameters = Math.Max(1, GetEffectiveMaxParameters(context) / columnCount);
        return Math.Max(1, Math.Min(_maxRowsPerBatch, maxRowsByParameters));
    }

    private int GetEffectiveMaxParameters(DbContext context)
    {
        if (_maxParametersPerCommand > 0)
            return _maxParametersPerCommand;

        return context.Database.ProviderName?.Contains("SqlServer", StringComparison.OrdinalIgnoreCase) == true
            ? SqlServerParameterLimit
            : 2000;
    }

    private static async Task ExecuteAuditBatchAsync(
        DbConnection connection,
        DbTransaction? dbTransaction,
        ISqlGenerationHelper sqlGenerationHelper,
        string auditTableName,
        IReadOnlyList<string> columnOrder,
        IReadOnlyList<AuditEntry> batch)
    {
        using var command = connection.CreateCommand();
        command.Transaction = dbTransaction;

        var valueRows = new List<string>(batch.Count);
        for (var rowIndex = 0; rowIndex < batch.Count; rowIndex++)
            valueRows.Add(CreateParameterRow(command, batch[rowIndex], columnOrder, rowIndex));

        var tableIdentifier = sqlGenerationHelper.DelimitIdentifier(auditTableName);
        var columns = string.Join(", ", columnOrder.Select(sqlGenerationHelper.DelimitIdentifier));
        command.CommandText = $"INSERT INTO {tableIdentifier} ({columns}) VALUES {string.Join(", ", valueRows)};";
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static string CreateParameterRow(
        DbCommand command,
        AuditEntry entry,
        IReadOnlyList<string> columnOrder,
        int rowIndex)
    {
        var parameterNames = new List<string>(columnOrder.Count);
        for (var columnIndex = 0; columnIndex < columnOrder.Count; columnIndex++)
        {
            var parameterName = $"@p{rowIndex}_{columnIndex}";
            var parameter = command.CreateParameter();
            parameter.ParameterName = parameterName;
            parameter.Value = entry.ColumnValues.TryGetValue(columnOrder[columnIndex], out var value)
                ? value ?? DBNull.Value
                : DBNull.Value;
            command.Parameters.Add(parameter);
            parameterNames.Add(parameterName);
        }

        return $"({string.Join(", ", parameterNames)})";
    }
}

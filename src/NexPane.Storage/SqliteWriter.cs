using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace NexPane.Storage;

/// <summary>
/// Single-writer discipline for one database (§5 "Persistence"): one writer connection owned by a dedicated
/// thread executes queued work items in order. Readers open their own pooled connections (WAL keeps them unblocked).
/// </summary>
public sealed class SqliteWriter : IAsyncDisposable, IDisposable
{
    private readonly Channel<WorkItem> _queue = Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Thread _thread;
    private readonly SqliteConnectionFactory _factory;
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    public SqliteWriter(SqliteConnectionFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "NexPane SQLite writer (" + factory.Kind + ")",
            Priority = ThreadPriority.BelowNormal,
        };
        _thread.Start();
    }

    /// <summary>Runs <paramref name="work"/> on the writer thread with the writer connection.</summary>
    public Task<T> ExecuteAsync<T>(Func<SqliteConnection, T> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        var item = new WorkItem<T>(work, ct);
        ObjectDisposedException.ThrowIf(!_queue.Writer.TryWrite(item), this);

        return item.Task;
    }

    /// <summary>Runs <paramref name="work"/> inside one transaction on the writer thread.</summary>
    public Task<T> ExecuteInTransactionAsync<T>(Func<SqliteConnection, SqliteTransaction, T> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        return ExecuteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var result = work(connection, transaction);
            transaction.Commit();
            return result;
        }, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.Writer.TryComplete();
        await _completed.Task.ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.Writer.TryComplete();
        _thread.Join();
    }

    private void Run()
    {
        SqliteConnection? connection = null;
        try
        {
            var reader = _queue.Reader;
            while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                while (reader.TryRead(out var item))
                {
                    if (item.Cancellation.IsCancellationRequested)
                    {
                        item.Cancel();
                        continue;
                    }

                    try
                    {
                        connection ??= _factory.Open();
                        item.Execute(connection);
                    }
                    catch (Exception ex)
                    {
                        item.Fail(ex);
                        if (ex is SqliteException)
                        {
                            connection?.Dispose();
                            connection = null; // reopen on the next item
                        }
                    }
                }
            }
        }
        finally
        {
            connection?.Dispose();
            _completed.TrySetResult();
        }
    }

    private abstract class WorkItem(CancellationToken cancellation)
    {
        public CancellationToken Cancellation { get; } = cancellation;

        public abstract void Execute(SqliteConnection connection);

        public abstract void Fail(Exception exception);

        public abstract void Cancel();
    }

    private sealed class WorkItem<T>(Func<SqliteConnection, T> work, CancellationToken cancellation) : WorkItem(cancellation)
    {
        private readonly TaskCompletionSource<T> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<T> Task => _tcs.Task;

        public override void Execute(SqliteConnection connection) => _tcs.TrySetResult(work(connection));

        public override void Fail(Exception exception) => _tcs.TrySetException(exception);

        public override void Cancel() => _tcs.TrySetCanceled(Cancellation);
    }
}

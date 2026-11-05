using MongoDB.Driver;

namespace WasteRegulatorCaseManagement.Utils.Mongo;

/// <summary>
/// Runs a callback inside a MongoDB transaction with a bounded time budget,
/// retrying transient transaction errors and write conflicts with
/// exponential backoff and jitter. The callback must be safe to run again.
/// </summary>
public sealed class MongoTransactions(IMongoClient client, ILogger logger)
{
    private const int TransactionTimeoutSeconds = 5;
    private const int TransactionWriteConflictRetryCount = 5;
    private const int WriteConflictErrorCode = 112;
    private const int InitialWriteConflictRetryDelayMilliseconds = 25;
    private const int WriteConflictRetryJitterMilliseconds = 25;

    public async Task<TResult> Execute<TResult>(
        Func<IClientSessionHandle, CancellationToken, Task<TResult>> callback,
        string transactionName,
        CancellationToken cancellationToken
    )
    {
        var transactionTimeout = TimeSpan.FromSeconds(TransactionTimeoutSeconds);
        using var timeoutCancellationTokenSource = new CancellationTokenSource(transactionTimeout);
        using var session = await client.StartSessionAsync(
            new ClientSessionOptions
            {
                DefaultTransactionOptions = new TransactionOptions(readPreference: ReadPreference.Primary),
            },
            cancellationToken
        );

        var retryCount = 0;
        while (true)
        {
            try
            {
                // Keep the driver token tied to the caller so WithTransactionAsync can abort with a live token when
                // the time budget expires. Passing the budget token to the driver would also cancel its abort command.
                return await session.WithTransactionAsync(
                    async (transactionSession, transactionCancellationToken) =>
                    {
                        using var operationCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
                            transactionCancellationToken,
                            timeoutCancellationTokenSource.Token
                        );

                        try
                        {
                            return await callback(transactionSession, operationCancellationTokenSource.Token);
                        }
                        catch (OperationCanceledException exception)
                            when (timeoutCancellationTokenSource.IsCancellationRequested
                                && !cancellationToken.IsCancellationRequested
                            )
                        {
                            throw TransactionTimedOut(exception, transactionName, transactionTimeout);
                        }
                    },
                    new TransactionOptions(maxCommitTime: transactionTimeout),
                    cancellationToken
                );
            }
            catch (MongoException exception)
                when (IsRetryableTransactionWriteError(exception) && retryCount < TransactionWriteConflictRetryCount)
            {
                var retryDelay = RetryDelay(retryCount);
                retryCount++;
                logger.LogWarning(
                    exception,
                    "Retrying MongoDB transaction '{MongoTransactionName}' after a retryable transaction write error. Retry {TransactionRetryAttempt} of {TransactionWriteConflictRetryCount} in {TransactionRetryDelayMilliseconds}ms",
                    transactionName,
                    retryCount,
                    TransactionWriteConflictRetryCount,
                    retryDelay.TotalMilliseconds
                );

                using var retryDelayCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    timeoutCancellationTokenSource.Token
                );

                try
                {
                    await Task.Delay(retryDelay, retryDelayCancellationTokenSource.Token);
                }
                catch (OperationCanceledException cancellationException)
                    when (timeoutCancellationTokenSource.IsCancellationRequested
                        && !cancellationToken.IsCancellationRequested
                    )
                {
                    throw TransactionTimedOut(cancellationException, transactionName, transactionTimeout);
                }
            }
        }
    }

    private TimeoutException TransactionTimedOut(
        OperationCanceledException exception,
        string transactionName,
        TimeSpan transactionTimeout
    )
    {
        logger.LogWarning(
            exception,
            "MongoDB transaction '{MongoTransactionName}' timed out after {MongoTransactionTimeoutSeconds} seconds",
            transactionName,
            transactionTimeout.TotalSeconds
        );

        return new TimeoutException(
            $"MongoDB transaction '{transactionName}' timed out after {transactionTimeout.TotalSeconds} seconds",
            exception
        );
    }

    private static bool IsRetryableTransactionWriteError(MongoException exception) =>
        exception.HasErrorLabel("TransientTransactionError")
        || exception is MongoCommandException { Code: WriteConflictErrorCode }
        || exception is MongoWriteException { WriteError.Code: WriteConflictErrorCode };

    private static TimeSpan RetryDelay(int retryCount)
    {
        var exponentialDelayMilliseconds = InitialWriteConflictRetryDelayMilliseconds * (1 << retryCount);
        var jitterMilliseconds = Random.Shared.Next(WriteConflictRetryJitterMilliseconds + 1);

        return TimeSpan.FromMilliseconds(exponentialDelayMilliseconds + jitterMilliseconds);
    }
}

using System.Text;
using Flowd.Application.Connectors;
using RabbitMQ.Client;

namespace Flowd.Infrastructure.Connectors;

/// <summary>
/// Publishes connector messages to RabbitMQ (AMQP 0-9-1) with publisher confirms: <see cref="PublishAsync"/> returns only when the broker has taken
/// responsibility for the message (FR-CON-013). One connection is shared and recreated after a failure; each publish uses its own short-lived channel.
/// </summary>
public sealed class RabbitMqPublisher(ConnectorOptions options) : IQueuePublisher, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;

    public async Task PublishAsync(QueueMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var connection = await ConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var channel = await connection.CreateChannelAsync(
                new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
                cancellationToken).ConfigureAwait(false);
            var properties = new BasicProperties { ContentType = message.ContentType, DeliveryMode = DeliveryModes.Persistent };
            await channel.BasicPublishAsync(message.Exchange, message.RoutingKey, mandatory: false, properties, Encoding.UTF8.GetBytes(message.Body), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await DropConnectionAsync(connection).ConfigureAwait(false); // the next publish connects again
            throw new InvalidOperationException("The broker did not accept the message.", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }

        _gate.Dispose();
    }

    private async Task<IConnection> ConnectionAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connection is { IsOpen: true } open)
            {
                return open;
            }

            var factory = new ConnectionFactory { Uri = new Uri(options.Queue.Address), AutomaticRecoveryEnabled = true };
            _connection = await factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
            return _connection;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException("The broker could not be reached.", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DropConnectionAsync(IConnection failed)
    {
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(_connection, failed))
            {
                _connection = null;
            }
        }
        finally
        {
            _gate.Release();
        }

        await failed.DisposeAsync().ConfigureAwait(false);
    }
}

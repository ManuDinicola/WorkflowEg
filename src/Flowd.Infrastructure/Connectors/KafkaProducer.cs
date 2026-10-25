using System.Text;
using Confluent.Kafka;
using Flowd.Application.Connectors;

namespace Flowd.Infrastructure.Connectors;

/// <summary>Produces connector records to Kafka with <c>acks=all</c> and idempotence, so a record is acknowledged by all in-sync replicas and a retry inside the client does not duplicate it (FR-CON-018).</summary>
public sealed class KafkaProducer(ConnectorOptions options) : IKafkaProducer, IDisposable
{
    private readonly Lock _gate = new();
    private IProducer<string, string>? _producer;

    public async Task<(int Partition, long Offset)> ProduceAsync(KafkaRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        var message = new Message<string, string> { Key = record.Key!, Value = record.Value, Headers = [] };
        foreach (var (name, value) in record.Headers)
        {
            message.Headers.Add(name, Encoding.UTF8.GetBytes(value));
        }

        try
        {
            var report = await Producer().ProduceAsync(record.Topic, message, cancellationToken).ConfigureAwait(false);
            return (report.Partition.Value, report.Offset.Value);
        }
        catch (ProduceException<string, string> ex)
        {
            throw new InvalidOperationException($"Kafka did not accept the record: {ex.Error.Code}.", ex);
        }
    }

    public void Dispose()
    {
        _producer?.Flush(TimeSpan.FromSeconds(5));
        _producer?.Dispose();
    }

    private IProducer<string, string> Producer()
    {
        lock (_gate)
        {
            return _producer ??= new ProducerBuilder<string, string>(Configuration()).Build();
        }
    }

    private ProducerConfig Configuration()
    {
        var kafka = options.Kafka;
        var config = new ProducerConfig
        {
            BootstrapServers = kafka.BootstrapServers,
            Acks = Acks.All,
            EnableIdempotence = true,
            MessageTimeoutMs = Math.Max(options.TimeoutSeconds, 1) * 1000,
            SecurityProtocol = Enum.TryParse<SecurityProtocol>(kafka.SecurityProtocol, ignoreCase: true, out var protocol) ? protocol : SecurityProtocol.Plaintext,
        };
        if (kafka.SaslUsername.Length > 0)
        {
            config.SaslMechanism = Enum.TryParse<SaslMechanism>(kafka.SaslMechanism, ignoreCase: true, out var mechanism) ? mechanism : SaslMechanism.Plain;
            config.SaslUsername = kafka.SaslUsername;
            config.SaslPassword = kafka.SaslPassword;
        }

        return config;
    }
}

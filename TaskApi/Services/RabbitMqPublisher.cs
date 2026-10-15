using System.Text;
using System.Text.Json;
using RabbitMQ.Client;
using TaskApi.Models;
using RabbitMqChannel = RabbitMQ.Client.IModel;

namespace TaskApi.Services;

public class RabbitMqPublisher : IRabbitMqPublisher, IHostedService, IDisposable
{
    private readonly ILogger<RabbitMqPublisher> _logger;
    private readonly IConfiguration _config;
    private IConnection? _connection;
    private RabbitMqChannel? _channel;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;
    private const string Exchange = "task.events";
    private const string RoutingKey = "task.completed";

    public RabbitMqPublisher(ILogger<RabbitMqPublisher> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;

            var factory = new ConnectionFactory
            {
                HostName = _config["RabbitMQ:HostName"] ?? "localhost",
                Port = int.Parse(_config["RabbitMQ:Port"] ?? "5672"),
                UserName = _config["RabbitMQ:UserName"] ?? "guest",
                Password = _config["RabbitMQ:Password"] ?? "guest",
                DispatchConsumersAsync = true
            };

            _connection = factory.CreateConnection();
            _channel = _connection.CreateModel();
            _channel.ExchangeDeclare(Exchange, ExchangeType.Topic, durable: true, autoDelete: false);

            _initialized = true;
            _logger.LogInformation("RabbitMQ connection established.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RabbitMQ connection failed. Will retry on publish.");
        }
        finally
        {
            _initLock.Release();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _channel?.Close();
            _connection?.Close();
            _logger.LogInformation("RabbitMQ connection closed gracefully.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error closing RabbitMQ connection.");
        }
        return Task.CompletedTask;
    }

    public async Task PublishTaskCompletedAsync(TaskItem task, CancellationToken ct = default)
    {
        try
        {
            if (!_initialized)
                await StartAsync(ct);

            if (_channel == null)
            {
                _logger.LogWarning("RabbitMQ channel is not available. Message for task {TaskId} was not sent.", task.Id);
                return;
            }

            var message = new
            {
                taskId = task.Id,
                title = task.Title,
                completedAt = task.CompletedAt,
                priority = task.Priority.ToString()
            };

            var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
            var props = _channel.CreateBasicProperties();
            props.Persistent = true;
            props.ContentType = "application/json";

            _channel.BasicPublish(Exchange, RoutingKey, props, body);
            _logger.LogInformation("Published task.completed for {TaskId}", task.Id);
        }
        catch (Exception ex)
        {
            // fail silently, но с логом
            _logger.LogError(ex, "Failed to publish task.completed for {TaskId}", task.Id);
        }
    }

    public void Dispose()
    {
        _channel?.Dispose();
        _connection?.Dispose();
        _initLock.Dispose();
    }
}
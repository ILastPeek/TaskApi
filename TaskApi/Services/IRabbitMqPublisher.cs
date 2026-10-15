using TaskApi.Models;

namespace TaskApi.Services;

public interface IRabbitMqPublisher
{
    Task PublishTaskCompletedAsync(TaskItem task, CancellationToken ct = default);
}
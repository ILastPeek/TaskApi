using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using TaskApi.Data;
using TaskApi.Models;
using TaskApi.Services;
using Xunit;

public class CompleteTaskIntegrationTest : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly Mock<IRabbitMqPublisher> _publisherMock = new();

    public CompleteTaskIntegrationTest(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Убираем реальный DbContext
                var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<TaskDbContext>));
                if (descriptor != null) services.Remove(descriptor);

                services.AddDbContext<TaskDbContext>(options =>
                    options.UseInMemoryDatabase("TestDb"));

                // Подменяем RabbitMQ на мок
                services.RemoveAll<IRabbitMqPublisher>();
                services.AddSingleton(_publisherMock.Object);
            });
        });
    }

    [Fact]
    public async Task CompleteTask_ShouldPublishMessage()
    {
        var client = _factory.CreateClient();

        // 1. Создать задачу
        var createResponse = await client.PostAsJsonAsync("/tasks", new
        {
            Title = "Test task",
            Priority = "High"
        });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<TaskItem>();
        Assert.NotNull(created);

        // 2. Завершить её
        var completeResponse = await client.PutAsync($"/tasks/{created.Id}/complete", null);
        completeResponse.EnsureSuccessStatusCode();

        // 3. Проверить, что RabbitMQ получил сообщение
        _publisherMock.Verify(p => p.PublishTaskCompletedAsync(
            It.Is<TaskItem>(t =>
                t.Id == created.Id &&
                t.IsCompleted &&
                t.CompletedAt != null),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
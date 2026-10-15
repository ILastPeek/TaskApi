using Microsoft.EntityFrameworkCore;
using TaskApi.Data;
using TaskApi.Models;
using TaskApi.Services;

var builder = WebApplication.CreateBuilder(args);

// DbContext
builder.Services.AddDbContext<TaskDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

// RabbitMQ
builder.Services.AddSingleton<RabbitMqPublisher>();
builder.Services.AddSingleton<IRabbitMqPublisher>(sp => sp.GetRequiredService<RabbitMqPublisher>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<RabbitMqPublisher>());

// Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Swagger middleware
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
// Создаём БД при старте
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<TaskDbContext>();
    db.Database.EnsureCreated(); // или Migrate()
}

// POST /tasks
app.MapPost("/tasks", async (CreateTaskRequest request, TaskDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(request.Title))
        return Results.BadRequest(new { error = "Title is required." });

    if (request.Title.Length > 200)
        return Results.BadRequest(new { error = "Title must be 200 characters or less." });

    var task = new TaskItem
    {
        Id = Guid.NewGuid(),
        Title = request.Title.Trim(),
        IsCompleted = false,
        CreatedAt = DateTimeOffset.UtcNow,
        CompletedAt = null,
        Priority = request.Priority ?? Priority.Medium
    };

    db.Tasks.Add(task);
    await db.SaveChangesAsync();

    return Results.Created($"/tasks/{task.Id}", task);
});

// GET /tasks
app.MapGet("/tasks", async (TaskDbContext db) =>
    Results.Ok(await db.Tasks.AsNoTracking().ToListAsync()));

// PUT /tasks/{id}/complete
app.MapPut("/tasks/{id:guid}/complete", async (
    Guid id,
    TaskDbContext db,
    IRabbitMqPublisher publisher,
    ILogger<Program> logger) =>
{
    var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id);
    if (task == null) return Results.NotFound();

    if (task.IsCompleted)
        return Results.Conflict(new { error = "Task already completed." });

    task.IsCompleted = true;
    task.CompletedAt = DateTimeOffset.UtcNow;

    try
    {
        await db.SaveChangesAsync();
    }
    catch (DbUpdateConcurrencyException)
    {
        return Results.Conflict(new { error = "Task was modified concurrently." });
    }

    // Публикуем ПОСЛЕ успешного сохранения в БД
    await publisher.PublishTaskCompletedAsync(task);

    return Results.Ok(task);
});

// DELETE /tasks/{id}
app.MapDelete("/tasks/{id:guid}", async (Guid id, TaskDbContext db) =>
{
    var task = await db.Tasks.FindAsync(id);
    if (task == null) return Results.NotFound();

    db.Tasks.Remove(task);
    await db.SaveChangesAsync();
    return Results.NoContent();
});

app.Run();
public partial class Program { }
public record CreateTaskRequest(string Title, Priority? Priority);
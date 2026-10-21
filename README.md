# TaskApi — тестовое задание (C# / .NET Backend Developer, Middle)

Minimal API для управления задачами: создание, получение списка, завершение и удаление.  
При завершении задачи отправляется событие в RabbitMQ. Конкурентные завершения обрабатываются через оптимистичную блокировку PostgreSQL (`xmin`).  
Для ручной проверки API подключён Swagger UI.

## Стек

- .NET 9, ASP.NET Core Minimal API
- PostgreSQL + EF Core
- RabbitMQ (только publish)
- Swagger / OpenAPI (Swashbuckle)
- xUnit + Moq + `WebApplicationFactory` (интеграционный тест)

## Требования для запуска

- .NET 9 SDK
- PostgreSQL (локально или в Docker)
- RabbitMQ (локально или в Docker)

## Конфигурация

Основные настройки находятся в `TaskApi/appsettings.json`:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Database=taskdb;Username=postgres;Password=postgres"
  },
  "RabbitMQ": {
    "HostName": "localhost",
    "Port": 5672,
    "UserName": "guest",
    "Password": "guest"
  }
}
```

При необходимости измени строку подключения и параметры RabbitMQ.

## Запуск

1. Убедись, что PostgreSQL и RabbitMQ запущены и доступны по адресам из конфигурации.
2. Примени миграции EF Core (если они есть) или создай базу вручную:
   ```bash
   dotnet ef database update --project TaskApi
   ```
   Если миграций нет, база создастся при первом запуске (зависит от настроек `DbContext`).
3. Запусти приложение:
   ```bash
   dotnet run --project TaskApi
   ```
4. API будет доступно по адресу `http://localhost:5155` (порт может отличаться — смотри вывод в консоли).
5. Swagger UI доступен в браузере по адресу:
   ```
   http://localhost:5155/swagger
   ```
   JSON-спецификация OpenAPI — по адресу `http://localhost:5155/swagger/v1/swagger.json`.

## API

| Метод  | Эндпоинт                | Описание |
|--------|-------------------------|----------|
| POST   | `/tasks`                | Создать задачу. `IsCompleted = false`, `Priority = Medium` по умолчанию. |
| GET    | `/tasks`                | Получить все задачи (без пагинации). |
| PUT    | `/tasks/{id}/complete`  | Завершить задачу: выставить `IsCompleted = true` и `CompletedAt`, отправить событие в RabbitMQ. |
| DELETE | `/tasks/{id}`           | Жёстко удалить задачу. |

Все эндпоинты задокументированы в Swagger UI, включая модели запросов/ответов и коды ошибок (`400`, `404`, `409`).

### Примеры запросов

**Создание задачи**
```http
POST /tasks
Content-Type: application/json

{
  "title": "Buy milk",
  "priority": "High"
}
```

**Завершение задачи**
```http
PUT /tasks/3fa85f64-5717-4562-b3fc-2c963f66afa6/complete
```

## Валидация

- `Title` обязателен, не пустой, максимум 200 символов.
- При создании задачи с некорректным `Title` возвращается `400 Bad Request`.
- Остальная валидация (Guid, enum) выполняется стандартными средствами модели.

## Конкурентность

Для защиты от одновременного завершения одной и той же задачи используется оптимистичная блокировка на основе системного столбца PostgreSQL `xmin`.

В модели `TaskItem` добавлено свойство:

```csharp
public uint Version { get; set; }
```

В `TaskDbContext` оно сконфигурировано так:

```csharp
e.Property(x => x.Version)
    .HasColumnName("xmin")
    .HasColumnType("xid")
    .ValueGeneratedOnAddOrUpdate()
    .IsConcurrencyToken();
```

Если два запроса одновременно пытаются завершить одну задачу, второй получит `409 Conflict` (EF Core выбросит `DbUpdateConcurrencyException`, который обрабатывается в эндпоинте).

## RabbitMQ

- Обменник: `task.events` (topic, durable).
- Routing key: `task.completed`.
- Публикация выполняется после успешного сохранения в БД.
- Используется интерфейс `IRabbitMqPublisher`, реализация — `RabbitMqPublisher`.
- Если RabbitMQ недоступен, ошибка логируется, но завершение задачи не откатывается (fail silently with logging).

**Формат сообщения:**
```json
{
  "taskId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "title": "Buy milk",
  "completedAt": "2026-05-07T12:00:00Z",
  "priority": "High"
}
```

### Graceful shutdown

`RabbitMqPublisher` реализует `IHostedService`. При остановке приложения (Ctrl+C / SIGTERM) вызывается `StopAsync`, который корректно закрывает канал и соединение RabbitMQ. Публикации, уже начавшиеся до остановки, завершаются в рамках стандартного таймаута хоста (обычно 5 секунд).

## Тесты

В проекте `TaskApi.Tests` есть один интеграционный тест:

`CompleteTaskIntegrationTest`

Сценарий:
1. Создать задачу через `POST /tasks`.
2. Завершить её через `PUT /tasks/{id}/complete`.
3. Проверить, что `IRabbitMqPublisher.PublishTaskCompletedAsync` был вызван ровно один раз с корректными данными.

RabbitMQ подменяется моком (`Moq`), база данных — `InMemoryDatabase`, поэтому для теста не нужны реальные PostgreSQL и RabbitMQ.

Запуск тестов:

```bash
dotnet test
```

## Структура проекта

```
TaskApi/
├── Data/
│   └── TaskDbContext.cs
├── Models/
│   ├── Priority.cs
│   └── TaskItem.cs
├── Services/
│   ├── IRabbitMqPublisher.cs
│   └── RabbitMqPublisher.cs
├── Program.cs
├── appsettings.json
└── TaskApi.http
TaskApi.Tests/
└── CompleteTaskIntegrationTest.cs
```

## Заметки по реализации

- `CompletedAt` выставляется **только** в эндпоинте завершения, при создании остаётся `null`.
- Для тестируемости публикация в RabbitMQ вынесена за интерфейс `IRabbitMqPublisher`.
- Добавлен Swagger UI (`/swagger`) — в задании он был опционален, но для ручной проверки API удобно.
- Если миграции не создавались, при первом запуске EF Core может попытаться создать схему (в зависимости от настроек). Для продакшн-подобного сценария лучше добавить миграции.

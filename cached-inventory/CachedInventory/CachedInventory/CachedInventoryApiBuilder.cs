namespace CachedInventory;

using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

public static class CachedInventoryApiBuilder
{
  public static WebApplication Build(string[] args)
  {
    var builder = WebApplication.CreateBuilder(args);
    var cache = new ConcurrentDictionary<int, int>();
    var timer = new ConcurrentDictionary<int, Timer>();

    // Add services to the container.
    // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();
    builder.Services.AddScoped<IWarehouseStockSystemClient, WarehouseStockSystemClient>();

    builder.Services.AddSingleton(cache);
    builder.Services.AddSingleton(timer);

    var app = builder.Build();

    // Configure the HTTP request pipeline.
    if (app.Environment.IsDevelopment())
    {
      app.UseSwagger();
      app.UseSwaggerUI();
    }

    app.UseHttpsRedirection();

    app.MapGet(
        "/stock/{productId:int}",
        async ([FromServices] IWarehouseStockSystemClient client,
        [FromServices] ConcurrentDictionary<int, int> cache, int productId) =>
        {
          if (!cache.TryGetValue(productId, out var stock))
          {
            stock = await client.GetStock(productId);
            cache[productId] = stock;
          }
          return Results.Ok(stock);
        })
      .WithName("GetStock")
      .WithOpenApi();

    app.MapPost(
        "/stock/retrieve",
        async ([FromServices] IWarehouseStockSystemClient client,
               [FromServices] ConcurrentDictionary<int, int> cache,
               [FromServices] ConcurrentDictionary<int, Timer> timer,
               [FromBody] RetrieveStockRequest req) =>
        {
          try
          {
            if (cache.TryGetValue(req.ProductId, out var stock))
            {
              if (stock < req.Amount)
              {
                return Results.BadRequest($"Not enough stock for ProductId {req.ProductId}.");
              }
              cache[req.ProductId] = stock - req.Amount;
              ProcessWithTimer(req.ProductId, client, cache, timer);
              return Results.Ok(stock);
            }

            stock = await client.GetStock(req.ProductId);
            if (stock < req.Amount)
            {
              return Results.BadRequest($"Not enough stock for ProductId {req.ProductId}.");
            }
            cache[req.ProductId] = stock - req.Amount;
            ProcessWithTimer(req.ProductId, client, cache, timer);
            return Results.Ok(stock);
          }
          catch (Exception ex)
          {
            return Results.BadRequest(ex);
          }
        })
      .WithName("RetrieveStock")
      .WithOpenApi();


    app.MapPost(
        "/stock/restock",
        async ([FromServices] IWarehouseStockSystemClient client,
               [FromServices] ConcurrentDictionary<int, int> cache,
               [FromServices] ConcurrentDictionary<int, Timer> timer,
               [FromBody] RestockRequest req) =>
        {
          try
          {
            var stock = await client.GetStock(req.ProductId);
            cache[req.ProductId] = req.Amount + stock;
            ProcessWithTimer(req.ProductId, client, cache, timer);
            return Results.Ok(stock);
          }
          catch (Exception ex)
          {
            return Results.BadRequest(ex);
          }
        })
      .WithName("Restock")
      .WithOpenApi();

    return app;
  }

  public static void ProcessWithTimer(
    int productId,
    IWarehouseStockSystemClient client,
    ConcurrentDictionary<int, int> cache,
    ConcurrentDictionary<int, Timer> timer)
  {
    var dueTime = 2500;

    if (!timer.TryGetValue(productId, out var timerStock))
    {
      var newTimer = new Timer(async state =>
      {
        var currentProductId = (int)state!;
        if (cache.TryGetValue(currentProductId, out var stock))
        {
          await client.UpdateStock(currentProductId, stock);
        }
      }, productId, dueTime, Timeout.Infinite);

      timer[productId] = newTimer;
    }
    else
    {
      _ = timerStock.Change(dueTime, Timeout.Infinite);
    }
  }
}

public record RetrieveStockRequest(int ProductId, int Amount);

public record RestockRequest(int ProductId, int Amount);

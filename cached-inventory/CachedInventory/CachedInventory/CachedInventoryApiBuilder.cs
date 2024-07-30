namespace CachedInventory;

using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

public static class CachedInventoryApiBuilder
{
  public static WebApplication Build(string[] args)
  {
    var builder = WebApplication.CreateBuilder(args);

    // Add services to the container.
    // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();
    builder.Services.AddScoped<IWarehouseStockSystemClient, WarehouseStockSystemClient>();

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
        async ([FromServices] IWarehouseStockSystemClient client, [FromServices] IMemoryCache cache, int productId) =>
        {
          if (!cache.TryGetValue(productId, out var stock))
          {
            stock = await client.GetStock(productId);
            _ = cache.Set(productId, stock, TimeSpan.FromMinutes(5));
          }
          return Results.Ok(stock);
        })
      .WithName("GetStock")
      .WithOpenApi();

    app.MapPost(
        "/stock/retrieve",
        async ([FromServices] IWarehouseStockSystemClient client, [FromBody] BulkRetrieveStockRequest req) =>
        {
          var stockCache = new ConcurrentDictionary<int, int>();

          var retrievalTasks = req.Items.Select(async item =>
          {
            if (!stockCache.TryGetValue(item.ProductId, out var stock))
            {
              stock = await client.GetStock(item.ProductId);
              stockCache[item.ProductId] = stock;
            }
            if (stock < item.Amount)
            {
              return (item.ProductId, Success: false);
            }
            stockCache[item.ProductId] -= item.Amount;
            return (item.ProductId, Success: true);
          });

          var results = await Task.WhenAll(retrievalTasks);
          var failedItems = results.Where(result => !result.Success).Select(result => result.ProductId).ToList();

          if (failedItems.Count != 0)
          {
            return Results.BadRequest($"Not enough stock for product(s): {string.Join(", ", failedItems)}");
          }

          var updateTasks = stockCache.Select(item => client.UpdateStock(item.Key, item.Value));
          await Task.WhenAll(updateTasks);

          return Results.Ok();
        })
      .WithName("RetrieveStock")
      .WithOpenApi();


    app.MapPost(
        "/stock/restock",
        async ([FromServices] IWarehouseStockSystemClient client, [FromBody] BulkRestockRequest req) =>
        {
          var stockCache = new ConcurrentDictionary<int, int>();

          var restockTaks = req.Items.Select(async item =>
          {
            if (!stockCache.TryGetValue(item.ProductId, out var stock))
            {
              stock = await client.GetStock(item.ProductId);
              stockCache[item.ProductId] = stock;
            }

            stockCache[item.ProductId] += item.Amount;
            return item.ProductId;
          });

          await Task.WhenAll(restockTaks);

          var updateTasks = stockCache.Select(item => client.UpdateStock(item.Key, item.Value));
          await Task.WhenAll(updateTasks);

          return Results.Ok();
        })
      .WithName("Restock")
      .WithOpenApi();

    return app;
  }
}

public record RetrieveStockRequest(int ProductId, int Amount);

public record RestockRequest(int ProductId, int Amount);

public record BulkRetrieveStockRequest(IEnumerable<RetrieveStockRequest> Items);

public record BulkRestockRequest(IEnumerable<RestockRequest> Items);

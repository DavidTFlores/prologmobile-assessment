using InventoryAggregation.Api.Endpoints;
using InventoryAggregation.Api.Inventory;
using InventoryAggregation.Api.Serialization;
using InventoryAggregation.Api.Upstream;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInventoryApi();
builder.Services.AddSingleton<InventoryAggregator>();

// Timestamps leave in the same UTC "Z" form they arrive in.
builder.Services.ConfigureHttpJsonOptions(json =>
    json.SerializerOptions.Converters.Add(new UtcDateTimeOffsetConverter()));

// The safety net for bugs: anything unhandled becomes a problem document with no stack trace.
// Expected upstream failures never get this far, because the endpoint maps those itself.
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapInventorySummary();

app.Run();

// Makes the entry point visible to the test project's WebApplicationFactory.
public partial class Program { }

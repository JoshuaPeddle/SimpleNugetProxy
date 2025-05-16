var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Configuration.
    AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables();

var app = builder.Build();

app.MapControllers();

app.MapGet("/health", () => "OK");
app.Run();

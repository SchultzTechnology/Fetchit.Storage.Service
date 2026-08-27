using Fetchit.Storage.Service.Authorizations;
using Fetchit.Storage.Service.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("config/appsettings.json", optional: true, reloadOnChange: true);
builder.Configuration.AddJsonFile($"config/appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true);

builder.Services.AddControllers();
builder.Services.AddHostedService<FileCleanupService>();

var app = builder.Build();

var storagePath = app.Configuration.GetValue<string>("Storage:Path") ?? "/data/files";
Directory.CreateDirectory(storagePath);

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(storagePath),
    RequestPath = "/files",
    ServeUnknownFileTypes = true,
    DefaultContentType = "application/octet-stream"
});

app.MapControllers();
app.Run();

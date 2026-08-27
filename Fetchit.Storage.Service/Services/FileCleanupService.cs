namespace Fetchit.Storage.Service.Services;

public class FileCleanupService : BackgroundService
{
    private readonly IConfiguration _config;
    private readonly ILogger<FileCleanupService> _logger;

    public FileCleanupService(IConfiguration config, ILogger<FileCleanupService> logger)
    {
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var intervalMinutes = _config.GetValue<int>("Storage:CleanupIntervalMinutes", 5);
            await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), stoppingToken);

            try
            {
                Cleanup();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cleanup failed");
            }
        }
    }

    private void Cleanup()
    {
        var storagePath = _config.GetValue<string>("Storage:Path") ?? "/data/files";
        var ttlMinutes = _config.GetValue<int>("Storage:TtlMinutes", 60);

        if (!Directory.Exists(storagePath)) return;

        var cutoff = DateTime.UtcNow.AddMinutes(-ttlMinutes);
        var removed = 0;

        foreach (var dir in Directory.GetDirectories(storagePath))
        {
            if (Directory.GetCreationTimeUtc(dir) < cutoff)
            {
                Directory.Delete(dir, true);
                removed++;
            }
        }

        if (removed > 0)
            _logger.LogInformation("Cleaned up {Count} expired file(s)", removed);
    }
}

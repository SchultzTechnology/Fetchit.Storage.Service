using Fetchit.Storage.Service.Authorizations;
using Microsoft.AspNetCore.Mvc;

namespace Fetchit.Storage.Service.Controllers;

[ApiController]
[Route("api/files")]
public class StorageController : ControllerBase
{
    private readonly IConfiguration _config;
    private readonly ILogger<StorageController> _logger;

    public StorageController(IConfiguration config, ILogger<StorageController> logger)
    {
        _config = config;
        _logger = logger;
    }

    [HttpPost]
    [ApiKeyAuthorize]
    [RequestSizeLimit(100_000_000)] // 100 MB
    public async Task<IActionResult> Upload(IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "No file provided." });

        var storagePath = _config.GetValue<string>("Storage:Path") ?? "/data/files";
        var baseUrl = _config.GetValue<string>("Storage:BaseUrl") ?? $"{Request.Scheme}://{Request.Host}";
        var ttlMinutes = _config.GetValue<int>("Storage:TtlMinutes", 60);

        var id = Guid.NewGuid().ToString("N");
        var dir = Path.Combine(storagePath, id);
        Directory.CreateDirectory(dir);

        var sanitizedName = Path.GetFileName(file.FileName);
        var filePath = Path.Combine(dir, sanitizedName);

        await using var stream = new FileStream(filePath, FileMode.Create);
        await file.CopyToAsync(stream);

        _logger.LogInformation("Stored {FileName} ({Size} bytes) as {Id}", sanitizedName, file.Length, id);

        var url = $"{baseUrl.TrimEnd('/')}/files/{id}/{sanitizedName}";
        var expiresAt = DateTime.UtcNow.AddMinutes(ttlMinutes);

        return Ok(new { url, expiresAt, id, filename = sanitizedName, size = file.Length });
    }

    [HttpDelete("{id}")]
    [ApiKeyAuthorize]
    public IActionResult Delete(string id)
    {
        var storagePath = _config.GetValue<string>("Storage:Path") ?? "/data/files";
        var dir = Path.Combine(storagePath, id);

        if (!Directory.Exists(dir))
            return NotFound(new { error = "File not found." });

        Directory.Delete(dir, true);
        _logger.LogInformation("Deleted {Id}", id);

        return Ok(new { deleted = id });
    }
}

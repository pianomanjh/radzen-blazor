using System;
using System.Linq;
using Microsoft.AspNetCore.Mvc;

namespace RadzenBlazorDemos.Server.Controllers
{
    // Bounded rather than unbounded: Stream below buffers the whole request body into memory, so
    // DisableRequestSizeLimit here made one large PUT an out-of-memory. 50 MB is well beyond anything
    // the upload demos send.
    [RequestSizeLimit(50 * 1024 * 1024)]
    public class UploadController : Controller
    {
        private readonly IWebHostEnvironment environment;

        public UploadController(IWebHostEnvironment environment)
        {
            this.environment = environment;
        }

        // The extension comes straight from the client's Content-Disposition header, which nothing
        // sanitises, and wwwroot is served by UseStaticFiles with no authentication - so an .html or
        // .svg upload came back as a working same-origin URL for whatever markup it carried. Only
        // raster image extensions are written; SVG is excluded because it can carry script.
        static readonly string[] AllowedImageExtensions =
        {
            ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp",
        };

        [HttpPut("api/upload/stream")]
        public async Task<IActionResult> Stream()
        {
            try
            {
                using var ms = new MemoryStream();
                await Request.Body.CopyToAsync(ms);

                return Ok(new { Completed = true, fileSize = ms.Length });
            }
            catch (Exception ex)
            {
                return StatusCode(500, ex.Message);
            }
        }

        [HttpPost("api/upload/single")]
        public IActionResult Single(IFormFile file)
        {
            try
            {
                return Ok(new { Completed = true });
            }
            catch (Exception ex)
            {
                return StatusCode(500, ex.Message);
            }
        }

        [HttpPost("api/upload/image")]
        public IActionResult Image(IFormFile file)
        {
            try
            {
                DeleteOldFiles();

                var extension = Path.GetExtension(file.FileName);

                if (!AllowedImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                {
                    return BadRequest("Only image files can be uploaded.");
                }

                var fileName = $"upload-{DateTime.Today:yyyy-MM-dd}-{Guid.NewGuid()}{extension}";

                using (var stream = new FileStream(Path.Combine(environment.WebRootPath, fileName), FileMode.Create))
                {
                    file.CopyTo(stream);
                }

                return Ok(new { Url = Url.Content($"~/{fileName}") });
            }
            catch (Exception ex)
            {
                return StatusCode(500, ex.Message);
            }
        }

        void DeleteOldFiles()
        {
            foreach (var file in Directory.GetFiles(environment.WebRootPath))
            {
                var fileName = Path.GetFileName(file);

                if (fileName.StartsWith("upload-") && !fileName.StartsWith($"upload-{DateTime.Today:yyyy-MM-dd}"))
                {
                    try
                    {
                        System.IO.File.Delete(file);
                    }
                    catch
                    {
                    }
                }
            }
        }

        [HttpPost("api/upload/multiple")]
        public IActionResult Multiple(IFormFile[] files)
        {
            try
            {
                return StatusCode(200);
            }
            catch (Exception ex)
            {
                return StatusCode(500, ex.Message);
            }
        }

        [HttpPost("api/upload/custom-header")]
        public IActionResult CustomHeader(IFormFile file)
        {
            try
            {
                var uploadedBy = Request.Headers["X-Uploaded-By"].ToString();
                var authorization = Request.Headers["Authorization"].ToString();

                return Ok(new { uploadedBy, authorization });
            }
            catch (Exception ex)
            {
                return StatusCode(500, ex.Message);
            }
        }

        [HttpPost("api/upload/{id}")]
        public IActionResult Post(IFormFile[] files, int id, [FromQuery] string query)
        {
            try
            {
                return Ok(new { id, query });
            }
            catch (Exception ex)
            {
                return StatusCode(500, ex.Message);
            }
        }

        [HttpPost("api/upload/specific")]
        public IActionResult Specific(IFormFile myName)
        {
            try
            {
                return Ok(new { Completed = true });
            }
            catch (Exception ex)
            {
                return StatusCode(500, ex.Message);
            }
        }
    }
}

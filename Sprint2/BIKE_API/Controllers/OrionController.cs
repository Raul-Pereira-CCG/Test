using Microsoft.AspNetCore.Mvc;
using System.Net.Http.Headers;
using System.Text.Json;
using Npgsql;
using Microsoft.Extensions.Configuration;

namespace OrionApiDotNet.Controllers;

[ApiController]
[Route("api/[controller]")]
public class OrionController : ControllerBase
{
    private readonly HttpClient _httpClient;
    private readonly string _orionUrl;
    private readonly string _cygnusNotifyUrl;

    public OrionController(IConfiguration configuration)
    {
        _httpClient = new HttpClient();

        _orionUrl = configuration["OrionSettings:OrionUrl"];
        _cygnusNotifyUrl = configuration["OrionSettings:CygnusNotifyUrl"];
    }

    [HttpPost("entity")]
    public async Task<IActionResult> CreateEntity()
    {
        using var reader = new StreamReader(Request.Body);
        var rawBody = await reader.ReadToEndAsync();

        var request = new HttpRequestMessage(HttpMethod.Post, _orionUrl);
        request.Content = new StringContent(rawBody);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/ld+json");

        var response = await _httpClient.SendAsync(request);
        var responseContent = await response.Content.ReadAsStringAsync();

        if (response.IsSuccessStatusCode)
            return Ok("Entity created successfully.");
        return BadRequest($"Orion Error: {responseContent}");
    }

    [HttpGet("entity/{id}")]
    public async Task<IActionResult> GetEntity(string id)
    {
        var response = await _httpClient.GetAsync($"{_orionUrl}/{id}");
        var content = await response.Content.ReadAsStringAsync();
        if (response.IsSuccessStatusCode)
            return Content(content, "application/ld+json");
        return NotFound($"Orion Error: {content}");
    }

    [HttpGet("entity")]
    public async Task<IActionResult> GetAllEntities()
    {
        var queryString = Request.QueryString.HasValue ? Request.QueryString.Value : "";
        var url = _orionUrl + queryString;

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/ld+json"));

        var response = await _httpClient.SendAsync(request);
        var content = await response.Content.ReadAsStringAsync();

        if (response.IsSuccessStatusCode)
            return Content(content, "application/ld+json");

        return StatusCode((int)response.StatusCode, $"Orion Error: {content}");
    }

    [HttpPatch("entity/{id}")]
    public async Task<IActionResult> PatchEntity(string id)
    {
        using var reader = new StreamReader(Request.Body);
        var rawBody = await reader.ReadToEndAsync();

        var request = new HttpRequestMessage(HttpMethod.Patch, $"{_orionUrl}/{id}/attrs");
        request.Content = new StringContent(rawBody);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/ld+json");

        var response = await _httpClient.SendAsync(request);
        var responseContent = await response.Content.ReadAsStringAsync();

        if (response.IsSuccessStatusCode)
            return Ok("Entity updated successfully.");
        return BadRequest($"Orion Error: {responseContent}");
    }

    [HttpDelete("entity/{id}")]
    public async Task<IActionResult> DeleteEntity(string id)
    {
        var getResp = await _httpClient.GetAsync($"{_orionUrl}/{id}");
        if (!getResp.IsSuccessStatusCode)
            return NotFound($"Entity {id} not found in Orion.");

        var entityJson = await getResp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(entityJson);
        if (!doc.RootElement.TryGetProperty("type", out var typeElement))
            return BadRequest("Could not determine entity type.");

        var entityType = typeElement.GetString();

        var patchBody = new
        {
            status = new { type = "Property", value = "deleted" }
        };
        var patchContent = new StringContent(JsonSerializer.Serialize(patchBody));
        patchContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        var patchResp = await _httpClient.PatchAsync($"{_orionUrl}/{id}/attrs", patchContent);
        if (!patchResp.IsSuccessStatusCode)
        {
            var err = await patchResp.Content.ReadAsStringAsync();
            return StatusCode((int)patchResp.StatusCode, $"Failed to patch status: {err}");
        }

        await Task.Delay(500);

        var deleteResp = await _httpClient.DeleteAsync($"{_orionUrl}/{id}");
        if (!deleteResp.IsSuccessStatusCode)
        {
            var msg = await deleteResp.Content.ReadAsStringAsync();
            return StatusCode((int)deleteResp.StatusCode, $"Orion Error: {msg}");
        }

        return Ok("Entity status set to deleted and entity removed from Orion.");
    }
}

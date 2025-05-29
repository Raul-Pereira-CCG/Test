using Microsoft.AspNetCore.Mvc;
using System.Net.Http.Headers;
using OrionApiDotNet.Models;
using OrionApiDotNet.Services;
using System.Text.Json;


namespace OrionApiDotNet.Controllers;

[ApiController]
[Route("api/[controller]")]
public class OrionController : ControllerBase
{
    private readonly HttpClient _httpClient;
    private const string OrionUrl = "http://57.128.119.16:1026/ngsi-ld/v1/entities";
    private readonly MongoService _mongoService = new MongoService();

    public OrionController()
    {
        _httpClient = new HttpClient();
    }

    [HttpPost("entity")]
    public async Task<IActionResult> CreateEntity()
    {
        using var reader = new StreamReader(Request.Body);
        var rawBody = await reader.ReadToEndAsync();

        var request = new HttpRequestMessage(HttpMethod.Post, OrionUrl);
        request.Content = new StringContent(rawBody);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/ld+json");

        var response = await _httpClient.SendAsync(request);
        var responseContent = await response.Content.ReadAsStringAsync();

        if (response.IsSuccessStatusCode)
        {
            try
            {
                var bicycle = JsonSerializer.Deserialize<Bicycle>(rawBody);
                if (bicycle is not null && !string.IsNullOrEmpty(bicycle.id))
                {
                    _mongoService.SaveBicycle(bicycle);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Erro ao guardar no MongoDB: " + ex.Message);
            }

            return Ok("Entity created successfully.");
        }

    return BadRequest($"Orion Error: {responseContent}");
    }


    [HttpGet("entity/{id}")]
    public async Task<IActionResult> GetEntity(string id)
    {
        var response = await _httpClient.GetAsync($"{OrionUrl}/{id}");
        var content = await response.Content.ReadAsStringAsync();
        if (response.IsSuccessStatusCode)
            return Content(content, "application/ld+json");
        return NotFound($"Orion Error: {content}");
    }

    [HttpGet("entity")]
    public async Task<IActionResult> GetAllEntities()
    {
    var queryString = Request.QueryString.HasValue ? Request.QueryString.Value : "";
    var url = OrionUrl + queryString;

    var request = new HttpRequestMessage(HttpMethod.Get, url);
    request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/ld+json"));

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

    var request = new HttpRequestMessage(HttpMethod.Patch, $"{OrionUrl}/{id}/attrs");
    request.Content = new StringContent(rawBody);
    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/ld+json");

    var response = await _httpClient.SendAsync(request);
    var responseContent = await response.Content.ReadAsStringAsync();

    if (response.IsSuccessStatusCode)
    {
        try
        {
            var getResponse = await _httpClient.GetAsync($"{OrionUrl}/{id}");
            var entityJson = await getResponse.Content.ReadAsStringAsync();

            var updatedBicycle = JsonSerializer.Deserialize<BicycleSimples>(entityJson);
            if (updatedBicycle is not null && !string.IsNullOrEmpty(updatedBicycle.id))
            {
                _mongoService.UpdateBicycle(updatedBicycle);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Erro ao atualizar no MongoDB: " + ex.Message);
        }

        return Ok("Entity updated successfully.");
    }

    return BadRequest($"Orion Error: {responseContent}");
}

    [HttpDelete("entity/{id}")]
    public async Task<IActionResult> DeleteEntity(string id)
    {
        var response = await _httpClient.DeleteAsync($"{OrionUrl}/{id}");
        var content = await response.Content.ReadAsStringAsync();

        if (response.IsSuccessStatusCode)
        {
            try
            {
                _mongoService.DeleteBicycle(id);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Erro ao apagar do MongoDB: " + ex.Message);
            }

            return Ok("Entity deleted successfully.");
        }

        return NotFound($"Orion Error: {content}");
    }

}
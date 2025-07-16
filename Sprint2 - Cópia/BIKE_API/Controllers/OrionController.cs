using Microsoft.AspNetCore.Mvc;
using System.Net.Http.Headers;
using System.Text.Json;
using Npgsql;
using Microsoft.Extensions.Configuration;
using Medo;

namespace OrionApiDotNet.Controllers;

[ApiController]
[Route("api/[controller]")]
public class OrionController : ControllerBase
{
    private readonly HttpClient _httpClient;
    private readonly string _orionUrl;
    private readonly string _OrgOrDomain;

    public OrionController(IConfiguration configuration)
    {
        _httpClient = new HttpClient();

        _orionUrl = configuration["OrionSettings:OrionUrl"]
            ?? throw new ArgumentNullException("OrionSettings:OrionUrl not found in configuration.");

        _OrgOrDomain = configuration["OrionSettings:OrgOrDomain"]
            ?? throw new ArgumentNullException("OrionSettings:_OrgOrDomainl not found in configuration.");
    }


    private string GenerateEntityId(string entityType = "Entity")
    {
        var uuid = Uuid7.NewUuid7();
        string urnId = $"urn:ngsi-ld:{_OrgOrDomain}:{entityType}:{uuid}";
        return urnId;
    }


    private string ExtractEntityTypeFromBody(string jsonBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonBody);
            if (doc.RootElement.TryGetProperty("type", out var typeElement))
            {
                var fullType = typeElement.GetString();
                if (fullType != null && fullType.Contains("/"))
                {
                    return fullType.Split('/').Last();
                }
                return fullType ?? "Entity";
            }
        }
        catch (JsonException)
        {
        }
        return "Entity";
    }

    [HttpPost("entity")]
    public async Task<IActionResult> CreateEntity()
    {
        using var reader = new StreamReader(Request.Body);
        var rawBody = await reader.ReadToEndAsync();

        var entityType = ExtractEntityTypeFromBody(rawBody);

        var generatedId = GenerateEntityId(entityType);

        var modifiedBody = AddIdToJsonBody(rawBody, generatedId);

        var request = new HttpRequestMessage(HttpMethod.Post, _orionUrl);
        request.Content = new StringContent(modifiedBody);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/ld+json");

        var response = await _httpClient.SendAsync(request);
        var responseContent = await response.Content.ReadAsStringAsync();

        if (response.IsSuccessStatusCode)
            return Ok(new { message = "Entity created successfully.", id = generatedId });
        return BadRequest($"Orion Error: {responseContent}");
    }

    private string AddIdToJsonBody(string jsonBody, string generatedId)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonBody);
            var rootElement = doc.RootElement;

            var jsonObject = new Dictionary<string, object>();

            jsonObject["id"] = generatedId;

            foreach (var property in rootElement.EnumerateObject())
            {
                if (property.Name != "id")
                {
                    jsonObject[property.Name] = JsonSerializer.Deserialize<object>(property.Value.GetRawText());
                }
            }

            return JsonSerializer.Serialize(jsonObject);
        }
        catch (JsonException)
        {
            return jsonBody;
        }
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

        return Ok($"Entity {entityType} removed from Orion.");

    }

    [HttpGet("vehicles-with-models")]
    public async Task<IActionResult> GetVehiclesWithModels()
    {
        return await GetVehiclesWithModelsInternal(null);
    }

    [HttpGet("vehicles-with-models/{vehicleId}")]
    public async Task<IActionResult> GetVehicleWithModels(string vehicleId)
    {
        return await GetVehiclesWithModelsInternal(vehicleId);
    }

    private async Task<IActionResult> GetVehiclesWithModelsInternal(string? specificVehicleId)
    {
        try
        {
            var vehicleType = "https://smartdatamodels.org/dataModel.Transportation/Vehicle";
            var refVehicleModelProperty = "https://smartdatamodels.org/dataModel.Transportation/refVehicleModel";
            
            List<JsonElement> vehicles;
            
            if (!string.IsNullOrEmpty(specificVehicleId))
            {
                var vehicleUrl = $"{_orionUrl}/{Uri.EscapeDataString(specificVehicleId)}";
                var vehicleRequest = new HttpRequestMessage(HttpMethod.Get, vehicleUrl);
                vehicleRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/ld+json"));
                
                var vehicleResponse = await _httpClient.SendAsync(vehicleRequest);
                if (!vehicleResponse.IsSuccessStatusCode)
                {
                    var errorContent = await vehicleResponse.Content.ReadAsStringAsync();
                    return StatusCode((int)vehicleResponse.StatusCode, $"Orion Error: {errorContent}");
                }
                
                var vehicleContent = await vehicleResponse.Content.ReadAsStringAsync();
                var singleVehicle = JsonSerializer.Deserialize<JsonElement>(vehicleContent);
                
                if (singleVehicle.TryGetProperty("type", out var typeElement))
                {
                    var actualType = typeElement.GetString();
                    if (actualType != vehicleType)
                    {
                        return BadRequest($"Entity {specificVehicleId} is not of type Vehicle");
                    }
                }
                
                vehicles = new List<JsonElement> { singleVehicle };
            }
            else
            {
                var vehicleQueryString = $"?type={Uri.EscapeDataString(vehicleType)}";
                var vehicleUrl = _orionUrl + vehicleQueryString;
                
                var vehicleRequest = new HttpRequestMessage(HttpMethod.Get, vehicleUrl);
                vehicleRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/ld+json"));
                
                var vehicleResponse = await _httpClient.SendAsync(vehicleRequest);
                if (!vehicleResponse.IsSuccessStatusCode)
                {
                    var errorContent = await vehicleResponse.Content.ReadAsStringAsync();
                    return StatusCode((int)vehicleResponse.StatusCode, $"Orion Error: {errorContent}");
                }
                
                var vehicleContent = await vehicleResponse.Content.ReadAsStringAsync();
                var vehicleArray = JsonSerializer.Deserialize<JsonElement[]>(vehicleContent);
                vehicles = vehicleArray.ToList();
            }
            
            var enrichedVehicles = new List<JsonElement>();
            
            foreach (var vehicle in vehicles)
            {
                var vehicleDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(vehicle.GetRawText());
                
                if (vehicleDict.TryGetValue(refVehicleModelProperty, out var refVehicleModel))
                {
                    if (refVehicleModel.TryGetProperty("value", out var modelIdElement))
                    {
                        var modelId = modelIdElement.GetString();
                        
                        var modelResponse = await _httpClient.GetAsync($"{_orionUrl}/{modelId}");
                        if (modelResponse.IsSuccessStatusCode)
                        {
                            var modelContent = await modelResponse.Content.ReadAsStringAsync();
                            var vehicleModel = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(modelContent);
                            
                            foreach (var kvp in vehicleModel)
                            {
                                if (kvp.Key != "id" && kvp.Key != "type" && kvp.Key != "@context")
                                {
                                    vehicleDict[kvp.Key] = kvp.Value;
                                }
                            }
                        }
                    }
                }
                
                var enrichedVehicleJson = JsonSerializer.Serialize(vehicleDict);
                enrichedVehicles.Add(JsonSerializer.Deserialize<JsonElement>(enrichedVehicleJson));
            }
            
            if (!string.IsNullOrEmpty(specificVehicleId))
            {
                var result = enrichedVehicles.FirstOrDefault();
                if (result.ValueKind == JsonValueKind.Undefined)
                {
                    return NotFound($"Vehicle {specificVehicleId} not found");
                }
                var singleResult = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
                return Content(singleResult, "application/ld+json");
            }
            else
            {
                var result = JsonSerializer.Serialize(enrichedVehicles, new JsonSerializerOptions { WriteIndented = true });
                return Content(result, "application/ld+json");
            }
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Internal error: {ex.Message}");
        }
    }

    [HttpGet("entities/by-types")]
    public async Task<IActionResult> GetEntitiesByTypes([FromQuery] string[] types)
    {
        if (types == null || types.Length == 0)
        {
            return BadRequest("At least one entity type must be specified.");
        }

        var encodedTypes = types.Select(Uri.EscapeDataString);
        var typeQuery = string.Join(",", encodedTypes);
        
        var queryString = $"?type={typeQuery}";

        var additionalParams = Request.Query
            .Where(kvp => kvp.Key != "types")
            .Select(kvp => $"{kvp.Key}={Uri.EscapeDataString(kvp.Value)}")
            .ToList();
            
        if (additionalParams.Any())
        {
            queryString += "&" + string.Join("&", additionalParams);
        }

        var url = _orionUrl + queryString;

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/ld+json"));

        var response = await _httpClient.SendAsync(request);
        var content = await response.Content.ReadAsStringAsync();

        if (response.IsSuccessStatusCode)
            return Content(content, "application/ld+json");

        return StatusCode((int)response.StatusCode, $"Orion Error: {content}");
    }
}
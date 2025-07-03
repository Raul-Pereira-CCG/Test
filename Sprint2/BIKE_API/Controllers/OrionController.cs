using Microsoft.AspNetCore.Mvc;
using System.Net.Http.Headers;
using Npgsql;

namespace OrionApiDotNet.Controllers;

[ApiController]
[Route("api/[controller]")]
public class OrionController : ControllerBase
{
    private readonly HttpClient _httpClient;
    private const string OrionUrl = "http://57.128.119.16:1027/ngsi-ld/v1/entities";

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
            return Ok("Entity created successfully.");
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
    // Recolher todos os parâmetros da query string
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
            return Ok("Entity updated successfully.");
        return BadRequest($"Orion Error: {responseContent}");
    }

    
[HttpDelete("entity/{id}")]
public async Task<IActionResult> DeleteEntity(string id)
{
    // 1. Buscar a entidade no Orion para garantir que existe
    var getResponse = await _httpClient.GetAsync($"{OrionUrl}/{id}");
    if (!getResponse.IsSuccessStatusCode)
        return NotFound($"Entity {id} not found in Orion.");

    var entityJson = await getResponse.Content.ReadAsStringAsync();

    // 2. Verificar o tipo da entidade
    string entityType = null;
    using (var doc = System.Text.Json.JsonDocument.Parse(entityJson))
    {
        if (doc.RootElement.TryGetProperty("type", out var typeElement))
        {
            entityType = typeElement.GetString();
        }
    }

    if (string.IsNullOrWhiteSpace(entityType))
        return BadRequest("Could not determine entity type.");

    // Só tentar apagar a tabela se for InventoryItem
    bool isInventoryItem = entityType.EndsWith("InventoryItem"); // para suportar URLs longas também
    string tableName = null;

    if (isInventoryItem)
    {
        // Extrair sufixo da entidade para nome da tabela (ex: "part6")
        string GetTableSuffixFromId(string entityId)
        {
            var parts = entityId.Split(':');
            return parts.Length > 3 ? parts[3].ToLower() : null;
        }

        var suffix = GetTableSuffixFromId(id);
        if (suffix == null)
            return BadRequest("Entity id format invalid to extract table suffix.");

        tableName = $"def_serv_ld.urn_ngsi_ld_inventoryitem_{suffix}";
    }

    // 3. Apagar a entidade no Orion
    var deleteResponse = await _httpClient.DeleteAsync($"{OrionUrl}/{id}");
    var deleteContent = await deleteResponse.Content.ReadAsStringAsync();

    if (!deleteResponse.IsSuccessStatusCode)
        return NotFound($"Orion Error: {deleteContent}");

    // 4. Se for InventoryItem, apagar tabela associada no PostgreSQL
    if (isInventoryItem && tableName != null)
    {
        var connString = "Host=57.128.119.16;Port=5434;Username=postgres;Password=example;Database=postgres";

        try
        {
            await using var conn = new NpgsqlConnection(connString);
            await conn.OpenAsync();

            var dropCmd = new NpgsqlCommand($"DROP TABLE IF EXISTS {tableName}", conn);
            await dropCmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            var errorMsg = $"Error dropping table in PostgreSQL: {ex.Message}";
            Console.Error.WriteLine(errorMsg);
            return StatusCode(500, errorMsg);
        }
    }

    return Ok($"Entity deleted from Orion{(isInventoryItem ? $" and table {tableName} dropped from PostgreSQL" : "")}.");
}

}
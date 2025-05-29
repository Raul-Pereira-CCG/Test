using System.Text.Json.Serialization;

namespace OrionApiDotNet.Models
{
    public class BicycleSimples
    {
        public string id { get; set; } = null!;
        public string? type { get; set; }
        public Location? location { get; set; }

        [JsonPropertyName("https://smartdatamodels.org/dataModel.Transportation/modelName")]
        public Property? modelName { get; set; }

        [JsonPropertyName("https://smartdatamodels.org/dataModel.Transportation/manufacturerName")]
        public Property? manufacturerName { get; set; }

        [JsonPropertyName("https://smartdatamodels.org/dataModel.Transportation/purchaseDate")]
        public Property? purchaseDate { get; set; }

        [JsonPropertyName("https://smartdatamodels.org/dataModel.Transportation/serviceStatus")]
        public Property? serviceStatus { get; set; }
    }
}
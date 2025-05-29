namespace OrionApiDotNet.Models
{
    public class Bicycle
    {
        public string id { get; set; } = null!;
        public string? type { get; set; }
        public Location? location { get; set; }
        public Property? modelName { get; set; }
        public Property? manufacturerName { get; set; }
        public Property? purchaseDate { get; set; }
        public Property? serviceStatus { get; set; }
    }

    public class Location
    {
        public string type { get; set; } = null!;
        public Coordinates value { get; set; }
    }

    public class Coordinates
    {
        public string type { get; set; } = null!;
        public double[] coordinates { get; set; } = null!;
    }

    public class Property
    {
        public string type { get; set; } = null!;
        public string value { get; set; } = null!;
    }
}
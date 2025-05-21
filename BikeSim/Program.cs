using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Generic;
using System.Linq;

class Program
{
    static readonly HttpClient client = new HttpClient();
    static readonly string orionUrl = "http://57.128.119.16:1026/ngsi-ld/v1/entities/";
    static readonly string openRouteServiceUrl = "https://api.openrouteservice.org/v2/directions/cycling-regular/geojson";
    static readonly string openRouteServiceApiKey = "5b3ce3597851110001cf62489b01ed6658bd4e98b6467c80b90934c0";

    static Dictionary<string, List<double[]>> bikeRoutes = new Dictionary<string, List<double[]>>();
    static Dictionary<string, int> bikeRoutePositions = new Dictionary<string, int>();
    static Dictionary<string, bool> bikeRouteCompleted = new Dictionary<string, bool>();
    static double[][] portoPolygon = new double[][]
    {
        new double[] {-8.689051, 41.173113},
        new double[] {-8.675078, 41.147266},
        new double[] {-8.633337, 41.147179},
        new double[] {-8.616012, 41.140305},
        new double[] {-8.590393, 41.139868},
        new double[] {-8.572156, 41.153743},
        new double[] {-8.566115, 41.175561},
        new double[] {-8.608054, 41.184668},
        new double[] {-8.689051, 41.173113}
    };
    static Random random = new Random();
    static bool stopRequested = false;

    static async Task Main()
    {
        Console.Write("Enter start bicycle ID: ");
        int startId = int.Parse(Console.ReadLine());

        Console.Write("Enter end bicycle ID: ");
        int endId = int.Parse(Console.ReadLine());

        Console.Write("Enter update interval (seconds): ");
        int interval = int.Parse(Console.ReadLine()) * 1000;

        List<string> bicycleIds = new List<string>();
        for (int i = startId; i <= endId; i++)
        {
            bicycleIds.Add($"urn:ngsi-ld:Bicycle:{i:D3}");
        }

        // Start a separate thread to handle stopping
        ThreadPool.QueueUserWorkItem(_ => 
        {
            Console.WriteLine("\nPress Enter at any time to stop the simulation...");
            Console.ReadLine();
            stopRequested = true;
            Console.WriteLine("Stop requested. Waiting for current cycle to complete...");
        });

        await RunContinuousSimulation(bicycleIds, interval);
        
        Console.WriteLine("Simulation stopped. Press Enter to exit.");
        Console.ReadLine();
    }

    static async Task RunContinuousSimulation(List<string> bicycleIds, int interval)
    {
        Console.WriteLine("Starting continuous route simulation. Press Enter to stop at any time.");
        
        // Initialize routes for all bicycles
        await InitializeRoutes(bicycleIds);
        
        int cycleCount = 0;
        
        while (!stopRequested)
        {
            cycleCount++;
            Console.WriteLine($"\n======== CYCLE {cycleCount} ========");
            
            // Move bicycles along their routes
            await MoveBicycles(bicycleIds, interval);
            
            // Generate new routes for bicycles that have completed their routes
            await RegenerateCompletedRoutes(bicycleIds);
        }
        
        Console.WriteLine($"Simulation ended after {cycleCount} cycles.");
    }
    
    static async Task InitializeRoutes(List<string> bicycleIds)
    {
        Console.WriteLine("Generating initial routes for all bicycles...");
        
        foreach (var bikeId in bicycleIds)
        {
            await GenerateNewRoute(bikeId);
        }
    }
    
    static async Task GenerateNewRoute(string bikeId)
    {
        // Get current position if the bike already has a route
        double[] startPoint;
        if (bikeRoutes.ContainsKey(bikeId) && bikeRoutes[bikeId].Count > 0 && bikeRoutePositions.ContainsKey(bikeId))
        {
            int currentPos = bikeRoutePositions[bikeId];
            if (currentPos < bikeRoutes[bikeId].Count)
            {
                startPoint = bikeRoutes[bikeId][currentPos];
            }
            else
            {
                startPoint = bikeRoutes[bikeId].Last();
            }
        }
        else
        {
            // Generate start point inside the polygon for new bikes
            startPoint = GenerateRandomPointInsidePolygon(portoPolygon, random);
        }
        
        // Generate end point that's relatively far away for longer routes
        double maxDistance = 0.015; // Increased for longer routes
        
        // Generate a random angle and distance within maxDistance
        double angle = random.NextDouble() * 2 * Math.PI;
        double distance = 0.005 + (random.NextDouble() * maxDistance);
        
        // Calculate the end point based on the angle and distance
        double endLon = startPoint[0] + Math.Sin(angle) * distance;
        double endLat = startPoint[1] + Math.Cos(angle) * distance;
        double[] endPoint = new double[] { endLon, endLat };
        
        // Keep generating until we find an end point inside the polygon
        int attempts = 0;
        while (!IsPointInsidePolygon(portoPolygon, endLat, endLon) && attempts < 10) 
        {
            angle = random.NextDouble() * 2 * Math.PI;
            distance = 0.005 + (random.NextDouble() * maxDistance);
            endLon = startPoint[0] + Math.Sin(angle) * distance;
            endLat = startPoint[1] + Math.Cos(angle) * distance;
            endPoint = new double[] { endLon, endLat };
            attempts++;
        }
        
        // If we couldn't find a suitable end point after 10 attempts,
        // just use another random point in the polygon
        if (!IsPointInsidePolygon(portoPolygon, endLat, endLon))
        {
            Console.WriteLine($"Could not find nearby end point within polygon for {bikeId}, generating random end point");
            endPoint = GenerateRandomPointInsidePolygon(portoPolygon, random);
        }

        try
        {
            List<double[]> route = await GetCyclingRoute(startPoint, endPoint);
            Console.WriteLine($"Generated route for {bikeId} with {route.Count} points");
            
            bikeRoutes[bikeId] = route;
            bikeRoutePositions[bikeId] = 0;
            bikeRouteCompleted[bikeId] = false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Failed to generate route for {bikeId}: {ex.Message}");
            // Create a simple direct route from start to end point
            List<double[]> simpleRoute = new List<double[]>(); 
            
            // Create a few intermediate points on a straight line for more realistic movement
            int numIntermediatePoints = 5 + random.Next(10); // 5-15 intermediate points
            for (int i = 0; i <= numIntermediatePoints; i++)
            {
                double fraction = (double)i / numIntermediatePoints;
                double lon = startPoint[0] + fraction * (endPoint[0] - startPoint[0]);
                double lat = startPoint[1] + fraction * (endPoint[1] - startPoint[1]);
                simpleRoute.Add(new double[] { lon, lat });
            }
            
            bikeRoutes[bikeId] = simpleRoute;
            bikeRoutePositions[bikeId] = 0;
            bikeRouteCompleted[bikeId] = false;
            Console.WriteLine($"✅ Created simple route for {bikeId} with {simpleRoute.Count} points");
        }
    }
    
    static async Task MoveBicycles(List<string> bicycleIds, int interval)
    {
        foreach (var bikeId in bicycleIds)
        {
            if (!bikeRoutes.ContainsKey(bikeId) || !bikeRoutePositions.ContainsKey(bikeId))
            {
                Console.WriteLine($"⚠️ {bikeId} doesn't have a route yet. Generating new route...");
                await GenerateNewRoute(bikeId);
                continue;
            }
            
            var route = bikeRoutes[bikeId];
            int routeLen = route.Count;
            int currentPos = bikeRoutePositions[bikeId];

            if (currentPos >= routeLen)
            {
                bikeRouteCompleted[bikeId] = true;
                Console.WriteLine($"🏁 {bikeId} has completed its route");
                continue;
            }

            double[] newPoint = route[currentPos];
            string serviceStatus = (currentPos == 0 || currentPos == routeLen - 1) ? "parked" : "onRoute";
            
            var updateData = new
            {
                location = new
                {
                    type = "GeoProperty",
                    value = new
                    {
                        type = "Point",
                        coordinates = new double[] { newPoint[0], newPoint[1] }
                    }
                },
                serviceStatus = new
                {
                    type = "Property",
                    value = serviceStatus
                }
            };

            string jsonContent = JsonSerializer.Serialize(updateData);
            HttpContent content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
            content.Headers.Add("Link", "<https://raw.githubusercontent.com/smart-data-models/dataModel.Transportation/master/context.jsonld>; rel=\"http://www.w3.org/ns/json-ld#context\"; type=\"application/ld+json\"");

            try
            {
                HttpResponseMessage response = await client.PatchAsync(orionUrl + bikeId + "/attrs", content);
                if (response.IsSuccessStatusCode)
                    Console.WriteLine($"✅ {bikeId} -> {serviceStatus}, Location: {newPoint[1]}, {newPoint[0]}, Position: {currentPos+1}/{routeLen}");
                else
                    Console.WriteLine($"❌ Failed to update {bikeId}: {response.StatusCode}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error updating {bikeId}: {ex.Message}");
            }
            
            // Move to the next position in the route
            bikeRoutePositions[bikeId] = currentPos + 1;
        }
        
        if (!stopRequested)
        {
            Console.WriteLine($"Waiting {interval / 1000} seconds...\n");
            await Task.Delay(interval);
        }
    }
    
    static async Task RegenerateCompletedRoutes(List<string> bicycleIds)
    {
        foreach (var bikeId in bicycleIds)
        {
            if (bikeRouteCompleted.ContainsKey(bikeId) && bikeRouteCompleted[bikeId])
            {
                Console.WriteLine($"🔄 Generating new route for {bikeId}");
                await GenerateNewRoute(bikeId);
            }
        }
    }

    static async Task<List<double[]>> GetCyclingRoute(double[] startPoint, double[] endPoint)
    {
        try
        {
            Console.WriteLine("Using OpenRouteService API...");
            var requestBody = new
            {
                coordinates = new[]
                {
                    new[] { startPoint[0], startPoint[1] },
                    new[] { endPoint[0], endPoint[1] }
                }
            };

            HttpClient routeClient = new HttpClient();
            routeClient.DefaultRequestHeaders.Add("Authorization", openRouteServiceApiKey);

            string jsonRequest = JsonSerializer.Serialize(requestBody);
            HttpContent content = new StringContent(jsonRequest, Encoding.UTF8, "application/json");

            HttpResponseMessage response = await routeClient.PostAsync(openRouteServiceUrl, content);
            string responseContent = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"API returned {response.StatusCode}: {responseContent}");
            }

            using (JsonDocument doc = JsonDocument.Parse(responseContent))
            {
                JsonElement coordinates = doc.RootElement
                    .GetProperty("features")[0]
                    .GetProperty("geometry")
                    .GetProperty("coordinates");

                List<double[]> route = new List<double[]>();
                foreach (JsonElement coord in coordinates.EnumerateArray())
                {
                    double lon = coord[0].GetDouble();
                    double lat = coord[1].GetDouble();
                    route.Add(new double[] { lon, lat });
                }
                return route;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"OpenRouteService API failed: {ex.Message}");
            Console.WriteLine("Using direct route from start to end point");
            throw;
        }
    }

    static double[] GenerateRandomPointInsidePolygon(double[][] polygon, Random random)
    {
        double minLat = polygon.Min(p => p[1]);
        double maxLat = polygon.Max(p => p[1]);
        double minLon = polygon.Min(p => p[0]);
        double maxLon = polygon.Max(p => p[0]);

        double latitude, longitude;
        do
        {
            latitude = minLat + (random.NextDouble() * (maxLat - minLat));
            longitude = minLon + (random.NextDouble() * (maxLon - minLon));
        } while (!IsPointInsidePolygon(polygon, latitude, longitude));

        return new double[] { longitude, latitude };
    }

    static bool IsPointInsidePolygon(double[][] polygon, double lat, double lon)
    {
        int n = polygon.Length;
        bool inside = false;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            if (((polygon[i][1] > lat) != (polygon[j][1] > lat)) &&
                (lon < (polygon[j][0] - polygon[i][0]) * (lat - polygon[i][1]) / (polygon[j][1] - polygon[i][1]) + polygon[i][0]))
            {
                inside = !inside;
            }
        }
        return inside;
    }
}
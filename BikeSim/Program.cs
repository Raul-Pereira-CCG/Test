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
    static readonly string orionUrl = "http://localhost:1026/ngsi-ld/v1/entities/";
    static readonly string openRouteServiceUrl = "https://api.openrouteservice.org/v2/directions/cycling-regular/geojson";
    static readonly string openRouteServiceApiKey = "5b3ce3597851110001cf62489b01ed6658bd4e98b6467c80b90934c0";

    static Dictionary<string, List<double[]>> bikeRoutes = new Dictionary<string, List<double[]>>();
    static Dictionary<string, int> bikeRoutePositions = new Dictionary<string, int>();
    static Dictionary<string, bool> bikeRouteCompleted = new Dictionary<string, bool>();
    static Dictionary<string, bool> bikeIsActive = new Dictionary<string, bool>();
    static Dictionary<string, double[]> bikeLastPosition = new Dictionary<string, double[]>();
    
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

        Console.Write("Enter percentage of bicycles active at any time (1-100): ");
        int activePercentage = Math.Max(1, Math.Min(100, int.Parse(Console.ReadLine())));

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
            Console.WriteLine("Stop requested. Parking all bicycles and stopping simulation...");
        });

        await RunContinuousSimulation(bicycleIds, interval, activePercentage);
        
        Console.WriteLine("Simulation stopped. Press Enter to exit.");
        Console.ReadLine();
    }

    static async Task RunContinuousSimulation(List<string> bicycleIds, int interval, int activePercentage)
    {
        Console.WriteLine($"Starting continuous route simulation with {activePercentage}% active bicycles. Press Enter to stop at any time.");
        
        // Initialize all bicycles as parked
        await InitializeBicycles(bicycleIds);
        
        // Activate initial set of bicycles
        await ActivateInitialBicycles(bicycleIds, activePercentage);
        
        int cycleCount = 0;
        
        while (!stopRequested)
        {
            cycleCount++;
            Console.WriteLine($"\n======== CYCLE {cycleCount} ========");
            
            // Move active bicycles along their routes
            await MoveBicycles(bicycleIds, interval);
            
            // Handle bicycle rotation and route generation
            if (!stopRequested)
            {
                await HandleBicycleRotation(bicycleIds, activePercentage);
            }
        }
        
        // Park all bicycles before stopping
        await ParkAllBicycles(bicycleIds);
        
        Console.WriteLine($"Simulation ended after {cycleCount} cycles. All bicycles are now parked.");
    }
    
    static async Task InitializeBicycles(List<string> bicycleIds)
    {
        Console.WriteLine("Initializing all bicycles as parked...");
        
        foreach (var bikeId in bicycleIds)
        {
            // Generate random parking position
            double[] parkingPosition = GenerateRandomPointInsidePolygon(portoPolygon, random);
            bikeLastPosition[bikeId] = parkingPosition;
            bikeIsActive[bikeId] = false;
            
            // Set bicycle as parked in the system
            await UpdateBicycleStatus(bikeId, parkingPosition, "parked");
        }
    }
    
    static async Task ActivateInitialBicycles(List<string> bicycleIds, int activePercentage)
    {
        int numToActivate = Math.Max(1, (bicycleIds.Count * activePercentage) / 100);
        var bicyclesToActivate = bicycleIds.OrderBy(x => random.Next()).Take(numToActivate).ToList();
        
        Console.WriteLine($"Activating {numToActivate} bicycles for initial routes...");
        
        foreach (var bikeId in bicyclesToActivate)
        {
            bikeIsActive[bikeId] = true;
            await GenerateNewRoute(bikeId);
        }
    }
    
    static async Task GenerateNewRoute(string bikeId)
    {
        // Get current position 
        double[] startPoint = bikeLastPosition.ContainsKey(bikeId) 
            ? bikeLastPosition[bikeId] 
            : GenerateRandomPointInsidePolygon(portoPolygon, random);
        
        // Generate end point anywhere within the polygon
        double[] endPoint = GenerateRandomPointInsidePolygon(portoPolygon, random);
        
        // Ensure start and end points are different
        int attempts = 0;
        while (Math.Abs(startPoint[0] - endPoint[0]) < 0.001 && Math.Abs(startPoint[1] - endPoint[1]) < 0.001 && attempts < 5)
        {
            endPoint = GenerateRandomPointInsidePolygon(portoPolygon, random);
            attempts++;
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
            
            // Create intermediate points on a straight line for more realistic movement
            int numIntermediatePoints = 10 + random.Next(20);
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
            // Skip parked bicycles
            if (!bikeIsActive.ContainsKey(bikeId) || !bikeIsActive[bikeId])
            {
                continue;
            }
            
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
            bikeLastPosition[bikeId] = newPoint;
            string serviceStatus = (currentPos == 0 || currentPos == routeLen - 1) ? "parked" : "onRoute";
            
            await UpdateBicycleStatus(bikeId, newPoint, serviceStatus);
            Console.WriteLine($"✅ {bikeId} -> {serviceStatus}, Location: {newPoint[1]}, {newPoint[0]}, Position: {currentPos+1}/{routeLen}");
            
            // Move to the next position in the route
            bikeRoutePositions[bikeId] = currentPos + 1;
        }
        
        if (!stopRequested)
        {
            Console.WriteLine($"Waiting {interval / 1000} seconds...\n");
            await Task.Delay(interval);
        }
    }
    
    static async Task HandleBicycleRotation(List<string> bicycleIds, int activePercentage)
    {
        // Park bicycles that have completed their routes
        var completedBikes = bicycleIds.Where(id => 
            bikeRouteCompleted.ContainsKey(id) && 
            bikeRouteCompleted[id] && 
            bikeIsActive.ContainsKey(id) && 
            bikeIsActive[id]).ToList();
        
        foreach (var bikeId in completedBikes)
        {
            Console.WriteLine($"🅿️ Parking {bikeId} after route completion");
            bikeIsActive[bikeId] = false;
            await UpdateBicycleStatus(bikeId, bikeLastPosition[bikeId], "parked");
        }
        
        // Calculate how many bicycles should be active
        int targetActiveBikes = Math.Max(1, (bicycleIds.Count * activePercentage) / 100);
        int currentActiveBikes = bicycleIds.Count(id => bikeIsActive.ContainsKey(id) && bikeIsActive[id]);
        
        // Activate parked bicycles if we're below target
        if (currentActiveBikes < targetActiveBikes)
        {
            var parkedBikes = bicycleIds.Where(id => 
                !bikeIsActive.ContainsKey(id) || 
                !bikeIsActive[id]).ToList();
            
            int bikesToActivate = Math.Min(targetActiveBikes - currentActiveBikes, parkedBikes.Count);
            var bikesToActivateList = parkedBikes.OrderBy(x => random.Next()).Take(bikesToActivate).ToList();
            
            foreach (var bikeId in bikesToActivateList)
            {
                Console.WriteLine($"🚴 Activating {bikeId} for new route");
                bikeIsActive[bikeId] = true;
                await GenerateNewRoute(bikeId);
            }
        }
    }
    
    static async Task ParkAllBicycles(List<string> bicycleIds)
    {
        Console.WriteLine("Parking all bicycles...");
        
        foreach (var bikeId in bicycleIds)
        {
            if (bikeLastPosition.ContainsKey(bikeId))
            {
                await UpdateBicycleStatus(bikeId, bikeLastPosition[bikeId], "parked");
                Console.WriteLine($"🅿️ Parked {bikeId}");
            }
        }
    }
    
    static async Task UpdateBicycleStatus(string bikeId, double[] position, string serviceStatus)
    {
        var updateData = new
        {
            location = new
            {
                type = "GeoProperty",
                value = new
                {
                    type = "Point",
                    coordinates = new double[] { position[0], position[1] }
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
            if (!response.IsSuccessStatusCode)
                Console.WriteLine($"❌ Failed to update {bikeId}: {response.StatusCode}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error updating {bikeId}: {ex.Message}");
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
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
    static Dictionary<string, int> bikeRoutesCompletedToday = new Dictionary<string, int>();
    static Dictionary<string, double[]> bikeLastPosition = new Dictionary<string, double[]>();
    static Dictionary<string, DateTime> bikeNextActivityTime = new Dictionary<string, DateTime>();
    static bool isRunning = true;

    static async Task Main()
    {
        int startId = 1;

        int endId = 25;

        int interval = 10 * 1000;

        int maxActiveBikes = 10;

        List<string> bicycleIds = new List<string>();
        for (int i = startId; i <= endId; i++)
        {
            bicycleIds.Add($"urn:ngsi-ld:Bicycle:{i:D3}");
        }

        // Define porto polygon for generating points
        double[][] portoPolygon = new double[][]
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

        Random random = new Random();

        // Initialize bike data
        foreach (var bikeId in bicycleIds)
        {
            bikeRoutesCompletedToday[bikeId] = 0;
            bikeRouteCompleted[bikeId] = true; // Start with all routes completed
            bikeNextActivityTime[bikeId] = AssignInitialActivityTime(random); // Stagger start times
        }

        // Fetch initial positions for all bicycles
        await InitializeLastPositions(bicycleIds);

        Console.WriteLine("Press Ctrl+C to stop the simulation.");
        
        // Set up cancellation
        Console.CancelKeyPress += (sender, e) => {
            e.Cancel = true;
            isRunning = false;
            Console.WriteLine("Stopping simulation gracefully...");
        };

        DateTime lastDayCheck = DateTime.Now.Date;
        
        while (isRunning)
        {
            // Check if it's a new day to reset counters
            if (DateTime.Now.Date != lastDayCheck)
            {
                Console.WriteLine($"New day detected. Resetting daily route counters.");
                foreach (var bikeId in bicycleIds)
                {
                    bikeRoutesCompletedToday[bikeId] = 0;
                    // Assign new start times for the new day
                    bikeNextActivityTime[bikeId] = AssignInitialActivityTime(random);
                }
                lastDayCheck = DateTime.Now.Date;
            }

            // Check if it's night time (between 00:00 and 08:00)
            DateTime now = DateTime.Now;
            if (now.Hour >= 0 && now.Hour < 8)
            {
                Console.WriteLine($"[{now:HH:mm:ss}] Night time (00:00-08:00). Bicycles are not active.");
                
                // Make sure all bicycles are parked during night time
                await ParkAllBicycles(bicycleIds);
                
                // Sleep for 5 minutes during night time before checking again
                Thread.Sleep(5 * 60 * 1000);
                continue;
            }

            // Count how many bicycles are currently active
            int currentlyActiveBikes = bicycleIds.Count(id => !bikeRouteCompleted[id]);
            Console.WriteLine($"[{now:HH:mm:ss}] Currently active bicycles: {currentlyActiveBikes}/{maxActiveBikes}");

            // Generate new routes for bicycles that should become active
            List<string> eligibleBikes = bicycleIds
                .Where(id => bikeRouteCompleted[id] && // bicycle is currently parked
                           bikeRoutesCompletedToday[id] < 5 && // hasn't reached daily limit
                           bikeNextActivityTime[id] <= now) // scheduled to activate now
                .OrderBy(id => bikeNextActivityTime[id]) // prioritize by scheduled time
                .ToList();

            // Activate only enough bikes to stay under maxActiveBikes
            int bikesToActivate = Math.Min(maxActiveBikes - currentlyActiveBikes, eligibleBikes.Count);
            
            if (bikesToActivate > 0)
            {
                Console.WriteLine($"[{now:HH:mm:ss}] Activating {bikesToActivate} new bicycles");
                
                for (int i = 0; i < bikesToActivate; i++)
                {
                    string bikeId = eligibleBikes[i];
                    
                    // Get the latest position for this bicycle
                    double[] startPoint = bikeLastPosition[bikeId];
                    if (startPoint == null)
                    {
                        Console.WriteLine($"No position available for {bikeId}. Generating random start point.");
                        startPoint = GenerateRandomPointInsidePolygon(portoPolygon, random);
                    }
                    
                    // First, update the bicycle as parked at its current position to refresh the timestamp
                    Console.WriteLine($"Updating {bikeId} as parked at its current position to refresh timestamp");
                    await UpdateBicyclePosition(bikeId, startPoint, "parked");
                    
                    // Wait a moment to ensure the timestamp changes
                    await Task.Delay(1000);
                    
                    // Generate end point inside the polygon
                    double[] endPoint = GenerateRandomPointInsidePolygon(portoPolygon, random);
                    
                    try
                    {
                        Console.WriteLine($"Generating new route for {bikeId} (routes completed today: {bikeRoutesCompletedToday[bikeId]}/5)");
                        List<double[]> route = await GetCyclingRoute(startPoint, endPoint);
                        Console.WriteLine($"Generated route for {bikeId} with {route.Count} points");
                        
                        bikeRoutes[bikeId] = route;
                        bikeRoutePositions[bikeId] = 1; // Start at position 1 (index 0 is the start position)
                        bikeRouteCompleted[bikeId] = false;
                        
                        // Now update to onRoute at the starting position
                        await UpdateBicyclePosition(bikeId, startPoint, "onRoute");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ Failed to generate route for {bikeId}: {ex.Message}");
                        // Create a simple direct route from start to end point
                        List<double[]> simpleRoute = new List<double[]> { startPoint, endPoint };
                        bikeRoutes[bikeId] = simpleRoute;
                        bikeRoutePositions[bikeId] = 0;
                        bikeRouteCompleted[bikeId] = false;
                        Console.WriteLine($"✅ Created simple route for {bikeId} with 2 points");
                        
                        // Update to onRoute for the simple route as well
                        await UpdateBicyclePosition(bikeId, startPoint, "onRoute");
                    }
                }
            }
            else if (eligibleBikes.Count > 0)
            {
                Console.WriteLine($"[{now:HH:mm:ss}] {eligibleBikes.Count} bicycles are eligible to start, but maximum active bikes ({maxActiveBikes}) reached");
            }

            // Update positions for bicycles that are on routes
            bool allRoutesCompleted = true;
            
            foreach (var bikeId in bicycleIds)
            {
                // Skip bicycles that have completed their routes
                if (bikeRouteCompleted[bikeId])
                {
                    continue;
                }

                allRoutesCompleted = false; // At least one bicycle is still on route
                
                var route = bikeRoutes[bikeId];
                int routeLen = route.Count;
                int currentPos = bikeRoutePositions[bikeId];

                double[] newPoint;
                string serviceStatus;

                if (currentPos >= routeLen - 1)
                {
                    // At the end of route
                    newPoint = route[routeLen - 1];
                    serviceStatus = "parked";
                    bikeRouteCompleted[bikeId] = true;
                    bikeRoutesCompletedToday[bikeId]++;
                    
                    // Schedule next activity time for this bicycle
                    bikeNextActivityTime[bikeId] = CalculateNextActivityTime(now, random, bikeId);
                    
                    Console.WriteLine($"🏁 {bikeId} has completed its route ({bikeRoutesCompletedToday[bikeId]}/5 today). Next activity at {bikeNextActivityTime[bikeId]:HH:mm:ss}");
                }
                else
                {
                    // Move along the route
                    newPoint = route[currentPos];
                    serviceStatus = "onRoute";
                    bikeRoutePositions[bikeId] = currentPos + 1;
                }

                // Update the last known position
                bikeLastPosition[bikeId] = newPoint;

                // Update the bicycle's position in the system
                await UpdateBicyclePosition(bikeId, newPoint, serviceStatus);
            }

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Waiting {interval / 1000} seconds...\n");
            Thread.Sleep(interval);
        }

        Console.WriteLine("Simulation stopped.");
    }

    static DateTime AssignInitialActivityTime(Random random)
    {
        DateTime now = DateTime.Now;
        
        // If it's night time (00:00-08:00), start after 8am
        if (now.Hour >= 0 && now.Hour < 8)
        {
            // Start sometime after 8am
            DateTime baseTime = now.Date.AddHours(8);
            int minutesToAdd = random.Next(0, 240); // Up to 4 hours after 8am
            return baseTime.AddMinutes(minutesToAdd);
        }
        else
        {
            // Start sometime between now and 3 hours from now
            int minutesToAdd = random.Next(0, 180);
            return now.AddMinutes(minutesToAdd);
        }
    }

    static DateTime CalculateNextActivityTime(DateTime currentTime, Random random, string bikeId)
    {
        // Get the day of week (0 = Sunday, 1 = Monday, ..., 6 = Saturday)
        int dayOfWeek = (int)currentTime.DayOfWeek;
        
        // Determine if it's a weekday or weekend
        bool isWeekend = dayOfWeek == 0 || dayOfWeek == 6;
        
        int minWaitMinutes, maxWaitMinutes;
        
        // Different wait patterns for weekends vs weekdays
        if (isWeekend)
        {
            // Weekends: longer wait times (more leisure rides, less commuting)
            minWaitMinutes = 30;
            maxWaitMinutes = 240; // 30 min to 4 hours
        }
        else
        {
            // Weekdays: shorter wait times during rush hours, longer at other times
            int hour = currentTime.Hour;
            
            if ((hour >= 7 && hour <= 9) || (hour >= 16 && hour <= 19))
            {
                // Rush hours - shorter wait
                minWaitMinutes = 15;
                maxWaitMinutes = 90; // 15 min to 1.5 hours
            }
            else
            {
                // Non-rush hours - longer wait
                minWaitMinutes = 45;
                maxWaitMinutes = 180; // 45 min to 3 hours
            }
        }
        
        // Randomize the wait time
        int waitMinutes = random.Next(minWaitMinutes, maxWaitMinutes + 1);
        
        // Calculate the next activity time
        DateTime nextTime = currentTime.AddMinutes(waitMinutes);
        
        // If the next activity would be during night hours, reschedule for the next morning
        if (nextTime.Hour >= 0 && nextTime.Hour < 8)
        {
            // Schedule for sometime after 8am the next day
            nextTime = nextTime.Date.AddDays(1).AddHours(8).AddMinutes(random.Next(0, 120));
        }
        
        return nextTime;
    }

    static async Task InitializeLastPositions(List<string> bicycleIds)
    {
        Console.WriteLine("Fetching current positions for all bicycles...");
        
        foreach (var bikeId in bicycleIds)
        {
            try
            {
                HttpResponseMessage response = await client.GetAsync(orionUrl + bikeId);
                
                if (response.IsSuccessStatusCode)
                {
                    string content = await response.Content.ReadAsStringAsync();
                    using (JsonDocument doc = JsonDocument.Parse(content))
                    {
                        if (doc.RootElement.TryGetProperty("location", out JsonElement location) &&
                            location.TryGetProperty("value", out JsonElement locationValue) &&
                            locationValue.TryGetProperty("coordinates", out JsonElement coordinates))
                        {
                            double lon = coordinates[0].GetDouble();
                            double lat = coordinates[1].GetDouble();
                            bikeLastPosition[bikeId] = new double[] { lon, lat };
                            Console.WriteLine($"✅ Fetched position for {bikeId}: {lat}, {lon}");
                        }
                        else
                        {
                            Console.WriteLine($"⚠️ Could not find location data for {bikeId}");
                            bikeLastPosition[bikeId] = null;
                        }
                    }
                }
                else
                {
                    Console.WriteLine($"❌ Failed to fetch position for {bikeId}: {response.StatusCode}");
                    bikeLastPosition[bikeId] = null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error fetching position for {bikeId}: {ex.Message}");
                bikeLastPosition[bikeId] = null;
            }
        }
    }

    static async Task ParkAllBicycles(List<string> bicycleIds)
    {
        foreach (var bikeId in bicycleIds)
        {
            if (!bikeRouteCompleted[bikeId])
            {
                double[] currentPos = bikeLastPosition[bikeId];
                if (currentPos != null)
                {
                    await UpdateBicyclePosition(bikeId, currentPos, "parked");
                    bikeRouteCompleted[bikeId] = true;
                    Console.WriteLine($"🌙 Parked {bikeId} for the night");
                }
            }
        }
    }

    static async Task UpdateBicyclePosition(string bikeId, double[] position, string serviceStatus)
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
            if (response.IsSuccessStatusCode)
                Console.WriteLine($"✅ {bikeId} -> {serviceStatus}, Location: {position[1]}, {position[0]}");
            else
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
            Console.WriteLine($"ORS Request: {jsonRequest}");
            HttpContent content = new StringContent(jsonRequest, Encoding.UTF8, "application/json");

            HttpResponseMessage response = await routeClient.PostAsync(openRouteServiceUrl, content);
            string responseContent = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"ORS Response status: {response.StatusCode}");

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
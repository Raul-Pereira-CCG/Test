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
    static readonly string orionUrl = "http://57.128.119.16:1027/ngsi-ld/v1/entities/";
    static readonly string openRouteServiceUrl = "https://api.openrouteservice.org/v2/directions/cycling-regular/geojson";
    static readonly string openRouteServiceApiKey = "5b3ce3597851110001cf62489b01ed6658bd4e98b6467c80b90934c0";

    static Dictionary<string, List<double[]>> bikeRoutes = new Dictionary<string, List<double[]>>();
    static Dictionary<string, int> bikeRoutePositions = new Dictionary<string, int>();
    static Dictionary<string, bool> bikeRouteCompleted = new Dictionary<string, bool>();
    static Dictionary<string, bool> bikeIsActive = new Dictionary<string, bool>();
    static Dictionary<string, double[]> bikeLastPosition = new Dictionary<string, double[]>();
    static Dictionary<string, string> bikeCurrentStation = new Dictionary<string, string>(); // bikeId -> stationId
    static Dictionary<string, string> bikeDestinationStation = new Dictionary<string, string>(); // bikeId -> stationId

    // Docking station data
    static Dictionary<string, DockingStation> dockingStations = new Dictionary<string, DockingStation>();

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

    public class DockingStation
    {
        public string Id { get; set; }
        public double[] Location { get; set; }
        public int TotalSlots { get; set; }
        public int OutOfServiceSlots { get; set; }
        public List<string> ParkedBicycles { get; set; } = new List<string>();

        public int AvailableBikes => ParkedBicycles.Count(id => !string.IsNullOrEmpty(id));
        public int FreeSlots => TotalSlots - OutOfServiceSlots - AvailableBikes;
        public bool HasAvailableSlots => FreeSlots > 0;
        public bool HasAvailableBikes => ParkedBicycles.Count > 0;
    }

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
            bicycleIds.Add($"urn:ngsi-ld:Vehicle:{i:D3}");
        }

        // Initialize docking stations
        await InitializeDockingStations();

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

    static async Task InitializeDockingStations()
    {
        Console.WriteLine("Fetching existing docking stations from API...");

        try
        {
            string stationsUrl = "http://57.128.119.16:1027/ngsi-ld/v1/entities?type=https://smartdatamodels.org/dataModel.Transportation/BikeHireDockingStation";
            HttpResponseMessage response = await client.GetAsync(stationsUrl);

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"Failed to fetch stations: {response.StatusCode}");
            }

            string responseContent = await response.Content.ReadAsStringAsync();
            using (JsonDocument doc = JsonDocument.Parse(responseContent))
            {
                foreach (JsonElement stationElement in doc.RootElement.EnumerateArray())
                {
                    string stationId = stationElement.GetProperty("id").GetString();

                    // Extract location coordinates
                    double[] location = stationElement
                        .GetProperty("location")
                        .GetProperty("value")
                        .GetProperty("coordinates")
                        .EnumerateArray()
                        .Select(coord => coord.GetDouble())
                        .ToArray();

                    // Extract station properties
                    int totalSlots = GetPropertyValue(stationElement, "https://smartdatamodels.org/dataModel.Transportation/totalSlotNumber");
                    int outOfServiceSlots = GetPropertyValue(stationElement, "https://smartdatamodels.org/dataModel.Transportation/outOfServiceSlotNumber");

                    // Extract current vehicles (bicycles already parked at station)
                    List<string> parkedBicycles = new List<string>();
                    if (stationElement.TryGetProperty("refVehicle", out JsonElement vehiclesElement))
                    {
                        if (vehiclesElement.TryGetProperty("value", out JsonElement valueElement))
                        {
                            foreach (JsonElement vehicle in valueElement.EnumerateArray())
                            {
                                string v = vehicle.GetString();
                                if (!string.IsNullOrWhiteSpace(v))
                                    parkedBicycles.Add(v);
                            }
                        }
                    }

                    // Create station object
                    var station = new DockingStation
                    {
                        Id = stationId,
                        Location = location,
                        TotalSlots = totalSlots,
                        OutOfServiceSlots = outOfServiceSlots
                    };
                    station.ParkedBicycles.AddRange(parkedBicycles);

                    dockingStations[stationId] = station;

                    // Update bicycle tracking for already parked bicycles
                    foreach (string bikeId in parkedBicycles)
                    {
                        bikeCurrentStation[bikeId] = stationId;
                        bikeLastPosition[bikeId] = location;
                    }

                    Console.WriteLine($"Loaded station {stationId}: {station.AvailableBikes} bikes, {station.FreeSlots} free slots");
                }
            }

            Console.WriteLine($"Successfully loaded {dockingStations.Count} docking stations from API");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error fetching docking stations: {ex.Message}");
            Console.WriteLine("Please ensure the Context Broker is running and stations exist.");
            throw;
        }
    }

    static int GetPropertyValue(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out JsonElement property))
        {
            if (property.TryGetProperty("value", out JsonElement value))
            {
                return value.GetInt32();
            }
        }
        return 0;
    }

    static async Task RunContinuousSimulation(List<string> bicycleIds, int interval, int activePercentage)
    {
        Console.WriteLine($"Starting continuous route simulation with {activePercentage}% active bicycles. Press Enter to stop at any time.");

        // Initialize all bicycles as parked in stations
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
        Console.WriteLine("Initializing bicycles...");

        // Separate bicycles that are already parked at stations from new ones
        var bicyclesAlreadyParked = new List<string>();
        var bicyclesToAssign = new List<string>();

        foreach (var bikeId in bicycleIds)
        {
            if (bikeCurrentStation.ContainsKey(bikeId))
            {
                bicyclesAlreadyParked.Add(bikeId);
                bikeIsActive[bikeId] = false;
                Console.WriteLine($"✅ {bikeId} already parked at station {bikeCurrentStation[bikeId]}");
            }
            else
            {
                bicyclesToAssign.Add(bikeId);
            }
        }

        Console.WriteLine($"Found {bicyclesAlreadyParked.Count} bicycles already parked, {bicyclesToAssign.Count} to assign");

        // Assign unparked bicycles to available stations
        foreach (var bikeId in bicyclesToAssign)
        {
            var availableStations = dockingStations.Values
                .Where(s => s.HasAvailableSlots)
                .OrderBy(x => random.Next())
                .ToList();

            if (availableStations.Count == 0)
            {
                // If no stations have slots, find the station with the most capacity
                var bestStation = dockingStations.Values
                    .OrderByDescending(s => s.TotalSlots - s.OutOfServiceSlots - s.ParkedBicycles.Count)
                    .First();
                availableStations.Add(bestStation);
                Console.WriteLine($"⚠️ No free slots available, parking {bikeId} at {bestStation.Id} (overloading station)");
            }

            var selectedStation = availableStations.First();
            await ParkBicycleAtStation(bikeId, selectedStation.Id);
            bikeIsActive[bikeId] = false;
            Console.WriteLine($"🅿️ Assigned {bikeId} to station {selectedStation.Id}");
        }

        Console.WriteLine($"All {bicycleIds.Count} bicycles are now assigned to stations");
    }

    static async Task ActivateInitialBicycles(List<string> bicycleIds, int activePercentage)
    {
        int numToActivate = Math.Max(1, (bicycleIds.Count * activePercentage) / 100);
        var bicyclesToActivate = bicycleIds.OrderBy(x => random.Next()).Take(numToActivate).ToList();

        Console.WriteLine($"Activating {numToActivate} bicycles for initial routes...");

        foreach (var bikeId in bicyclesToActivate)
        {
            // Only activate if the bicycle is at a station
            if (bikeCurrentStation.ContainsKey(bikeId))
            {
                bikeIsActive[bikeId] = true;
                await GenerateNewRoute(bikeId);
            }
        }
    }

    static async Task GenerateNewRoute(string bikeId)
    {
        // Bicycle must start from its current station
        if (!bikeCurrentStation.ContainsKey(bikeId))
        {
            Console.WriteLine($"❌ {bikeId} is not at any station. Cannot generate route.");
            return;
        }

        string startStationId = bikeCurrentStation[bikeId];
        var startStation = dockingStations[startStationId];
        double[] startPoint = startStation.Location;

        // Remove bicycle from current station with retry mechanism
        bool removalSuccess = await RemoveBicycleFromStationWithRetry(bikeId, startStationId);
        if (!removalSuccess)
        {
            Console.WriteLine($"❌ Failed to remove {bikeId} from station {startStationId} after retries. Aborting route generation.");
            bikeIsActive[bikeId] = false;
            return;
        }

        // Choose a destination station with available slots
        var availableDestinations = dockingStations.Values
            .Where(s => s.Id != startStationId && s.HasAvailableSlots)
            .ToList();

        if (availableDestinations.Count == 0)
        {
            Console.WriteLine($"⚠️ No available destination stations for {bikeId}. Using any station.");
            availableDestinations = dockingStations.Values.Where(s => s.Id != startStationId).ToList();
        }

        var destinationStation = availableDestinations.OrderBy(x => random.Next()).First();
        double[] endPoint = destinationStation.Location;

        bikeDestinationStation[bikeId] = destinationStation.Id;

        try
        {
            List<double[]> route = await GetCyclingRoute(startPoint, endPoint);
            Console.WriteLine($"Generated route for {bikeId} from {startStationId} to {destinationStation.Id} with {route.Count} points");

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
            string serviceStatus = (currentPos == 0) ? "parked" : "onRoute";

            await UpdateBicycleStatus(bikeId, newPoint, serviceStatus);
            Console.WriteLine($"✅ {bikeId} -> {serviceStatus}, Location: {newPoint[1]}, {newPoint[0]}, Position: {currentPos + 1}/{routeLen}");

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
            string destinationStationId = bikeDestinationStation[bikeId];
            Console.WriteLine($"🅿️ Parking {bikeId} at station {destinationStationId}");

            bool parkingSuccess = await ParkBicycleAtStationWithRetry(bikeId, destinationStationId);
            if (parkingSuccess)
            {
                bikeIsActive[bikeId] = false;
            }
            else
            {
                Console.WriteLine($"❌ Failed to park {bikeId} at station {destinationStationId} after retries. Bicycle will remain active.");
            }
        }

        // Calculate how many bicycles should be active
        int targetActiveBikes = Math.Max(1, (bicycleIds.Count * activePercentage) / 100);
        int currentActiveBikes = bicycleIds.Count(id => bikeIsActive.ContainsKey(id) && bikeIsActive[id]);

        // Activate parked bicycles if we're below target
        if (currentActiveBikes < targetActiveBikes)
        {
            var parkedBikes = bicycleIds.Where(id =>
                (!bikeIsActive.ContainsKey(id) || !bikeIsActive[id]) &&
                bikeCurrentStation.ContainsKey(id)).ToList();

            int bikesToActivate = Math.Min(targetActiveBikes - currentActiveBikes, parkedBikes.Count);
            var bikesToActivateList = parkedBikes.OrderBy(x => random.Next()).Take(bikesToActivate).ToList();

            foreach (var bikeId in bikesToActivateList)
            {
                Console.WriteLine($"🚴 Activating {bikeId} for new route from station {bikeCurrentStation[bikeId]}");
                bikeIsActive[bikeId] = true;
                await GenerateNewRoute(bikeId);
            }
        }
    }

    static async Task ParkAllBicycles(List<string> bicycleIds)
    {
        Console.WriteLine("Parking all bicycles at their designated stations...");

        foreach (var bikeId in bicycleIds)
        {
            if (bikeIsActive.ContainsKey(bikeId) && bikeIsActive[bikeId])
            {
                // If bicycle is active, park it at its destination station
                if (bikeDestinationStation.ContainsKey(bikeId))
                {
                    await ParkBicycleAtStationWithRetry(bikeId, bikeDestinationStation[bikeId]);
                }
                else if (bikeCurrentStation.ContainsKey(bikeId))
                {
                    await ParkBicycleAtStationWithRetry(bikeId, bikeCurrentStation[bikeId]);
                }
                bikeIsActive[bikeId] = false;
                Console.WriteLine($"🅿️ Parked {bikeId}");
            }
        }
    }

    static async Task<bool> ParkBicycleAtStationWithRetry(string bikeId, string stationId, int maxRetries = 3)
    {
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                bool success = await ParkBicycleAtStation(bikeId, stationId);
                if (success)
                {
                    return true;
                }

                if (attempt < maxRetries)
                {
                    Console.WriteLine($"⚠️ Attempt {attempt} to park {bikeId} at {stationId} failed. Retrying in 1 second...");
                    await Task.Delay(1000);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Exception during attempt {attempt} to park {bikeId}: {ex.Message}");
                if (attempt < maxRetries)
                {
                    await Task.Delay(1000);
                }
            }
        }

        Console.WriteLine($"❌ Failed to park {bikeId} at {stationId} after {maxRetries} attempts");
        return false;
    }

    static async Task<bool> RemoveBicycleFromStationWithRetry(string bikeId, string stationId, int maxRetries = 3)
    {
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                bool success = await RemoveBicycleFromStation(bikeId, stationId);
                if (success)
                {
                    return true;
                }

                if (attempt < maxRetries)
                {
                    Console.WriteLine($"⚠️ Attempt {attempt} to remove {bikeId} from {stationId} failed. Retrying in 1 second...");
                    await Task.Delay(1000);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Exception during attempt {attempt} to remove {bikeId}: {ex.Message}");
                if (attempt < maxRetries)
                {
                    await Task.Delay(1000);
                }
            }
        }

        Console.WriteLine($"❌ Failed to remove {bikeId} from {stationId} after {maxRetries} attempts");
        return false;
    }

    static async Task<bool> ParkBicycleAtStation(string bikeId, string stationId)
    {
        var station = dockingStations[stationId];

        // Create backup of current state in case we need to rollback
        var originalParkedBicycles = new List<string>(station.ParkedBicycles);
        var wasCurrentlyAtStation = bikeCurrentStation.ContainsKey(bikeId);
        var originalCurrentStation = wasCurrentlyAtStation ? bikeCurrentStation[bikeId] : null;
        var originalLastPosition = bikeLastPosition.ContainsKey(bikeId) ? (double[])bikeLastPosition[bikeId].Clone() : null;

        // Update local state first
        if (!station.ParkedBicycles.Contains(bikeId))
        {
            station.ParkedBicycles.Add(bikeId);
        }
        bikeCurrentStation[bikeId] = stationId;
        bikeLastPosition[bikeId] = station.Location;

        // Try to update bicycle status first
        bool bicycleUpdateSuccess = await UpdateBicycleStatusWithRetry(bikeId, station.Location, "parked");
        if (!bicycleUpdateSuccess)
        {
            Console.WriteLine($"❌ Failed to update bicycle {bikeId} status. Rolling back local state.");
            // Rollback local state
            station.ParkedBicycles.Clear();
            station.ParkedBicycles.AddRange(originalParkedBicycles);
            if (wasCurrentlyAtStation)
            {
                bikeCurrentStation[bikeId] = originalCurrentStation;
            }
            else
            {
                bikeCurrentStation.Remove(bikeId);
            }
            if (originalLastPosition != null)
            {
                bikeLastPosition[bikeId] = originalLastPosition;
            }
            else
            {
                bikeLastPosition.Remove(bikeId);
            }
            return false;
        }

        // Try to update station status
        bool stationUpdateSuccess = await UpdateDockingStationStatusWithRetry(stationId);
        if (!stationUpdateSuccess)
        {
            Console.WriteLine($"❌ Failed to update station {stationId} status. Rolling back local state.");
            // Rollback local state
            station.ParkedBicycles.Clear();
            station.ParkedBicycles.AddRange(originalParkedBicycles);
            if (wasCurrentlyAtStation)
            {
                bikeCurrentStation[bikeId] = originalCurrentStation;
            }
            else
            {
                bikeCurrentStation.Remove(bikeId);
            }
            if (originalLastPosition != null)
            {
                bikeLastPosition[bikeId] = originalLastPosition;
            }
            else
            {
                bikeLastPosition.Remove(bikeId);
            }
            return false;
        }

        return true;
    }

    static async Task<bool> RemoveBicycleFromStation(string bikeId, string stationId)
    {
        var station = dockingStations[stationId];

        // Create backup of current state in case we need to rollback
        var originalParkedBicycles = new List<string>(station.ParkedBicycles);
        var wasCurrentlyAtStation = bikeCurrentStation.ContainsKey(bikeId);
        var originalCurrentStation = wasCurrentlyAtStation ? bikeCurrentStation[bikeId] : null;

        // Update local state first
        station.ParkedBicycles.Remove(bikeId);
        bikeCurrentStation.Remove(bikeId);

        // Try to update station status
        bool updateSuccess = await UpdateDockingStationStatusWithRetry(stationId);
        if (!updateSuccess)
        {
            Console.WriteLine($"❌ Failed to update station {stationId} after removing {bikeId}. Rolling back local state.");
            // Rollback local state
            station.ParkedBicycles.Clear();
            station.ParkedBicycles.AddRange(originalParkedBicycles);
            if (wasCurrentlyAtStation)
            {
                bikeCurrentStation[bikeId] = originalCurrentStation;
            }
            return false;
        }

        return true;
    }

    static async Task<bool> UpdateDockingStationStatusWithRetry(string stationId, int maxRetries = 3)
    {
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                bool success = await UpdateDockingStationStatus(stationId);
                if (success)
                {
                    return true;
                }

                if (attempt < maxRetries)
                {
                    Console.WriteLine($"⚠️ Attempt {attempt} to update station {stationId} failed. Retrying in 500ms...");
                    await Task.Delay(500);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Exception during attempt {attempt} to update station {stationId}: {ex.Message}");
                if (attempt < maxRetries)
                {
                    await Task.Delay(500);
                }
            }
        }

        return false;
    }

    static async Task<bool> UpdateBicycleStatusWithRetry(string bikeId, double[] position, string serviceStatus, int maxRetries = 3)
    {
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                bool success = await UpdateBicycleStatus(bikeId, position, serviceStatus);
                if (success)
                {
                    return true;
                }

                if (attempt < maxRetries)
                {
                    Console.WriteLine($"⚠️ Attempt {attempt} to update bicycle {bikeId} failed. Retrying in 500ms...");
                    await Task.Delay(500);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Exception during attempt {attempt} to update bicycle {bikeId}: {ex.Message}");
                if (attempt < maxRetries)
                {
                    await Task.Delay(500);
                }
            }
        }

        return false;
    }

    static async Task<bool> UpdateDockingStationStatus(string stationId)
    {
        var station = dockingStations[stationId];

        var updateData = new
        {
            availableBikeNumber = new
            {
                type = "Property",
                value = station.AvailableBikes
            },
            freeSlotNumber = new
            {
                type = "Property",
                value = station.FreeSlots
            },
            outOfServiceSlotNumber = new
            {
                type = "Property",
                value = station.OutOfServiceSlots
            },
            refVehicle = new
            {
                type = "Property",
                value = BuildRefVehicleArray(station)
            }
        };

        string jsonContent = JsonSerializer.Serialize(updateData);
        HttpContent content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
        content.Headers.Add("Link", "<https://raw.githubusercontent.com/smart-data-models/dataModel.Transportation/master/context.jsonld>; rel=\"http://www.w3.org/ns/json-ld#context\"; type=\"application/ld+json\"");

        try
        {
            HttpResponseMessage response = await client.PatchAsync(orionUrl + stationId + "/attrs", content);
            if (response.IsSuccessStatusCode)
            {
                Console.WriteLine($"📊 Updated station {stationId}: {station.AvailableBikes} bikes, {station.FreeSlots} free slots");
                return true;
            }
            else
            {
                string responseContent = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"❌ Failed to update station {stationId}: {response.StatusCode} - {responseContent}");
                return false;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error updating station {stationId}: {ex.Message}");
            return false;
        }
    }

    static async Task<bool> UpdateBicycleStatus(string bikeId, double[] position, string serviceStatus)
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
            {
                return true;
            }
            else
            {
                string responseContent = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"❌ Failed to update {bikeId}: {response.StatusCode} - {responseContent}");
                return false;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error updating {bikeId}: {ex.Message}");
            return false;
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
    
    static string[] BuildRefVehicleArray(DockingStation st)
    {
        var bikes = st.ParkedBicycles
                    .Where(id => !string.IsNullOrEmpty(id))
                    .ToArray();

        return bikes.Length switch
        {
            0 => Array.Empty<string>(),       
            1 => new[] { "", bikes[0] },          
            _ => bikes                                
        };
    }

}
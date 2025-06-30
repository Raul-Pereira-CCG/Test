using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Concurrent;

class Program
{
    /* ------------------------------------------------------------------ */
    /* Configuration                                                       */
    /* ------------------------------------------------------------------ */
    static readonly HttpClient client  = new HttpClient();
    static readonly string orionUrl    = "http://57.128.119.16:1027/ngsi-ld/v1/entities/";
    static readonly string openRouteServiceUrl =
        "https://api.openrouteservice.org/v2/directions/cycling-regular/geojson";
    static readonly string openRouteServiceApiKey =
        "5b3ce3597851110001cf62489b01ed6658bd4e98b6467c80";

    /* ------------------------------------------------------------------ */
    /* Simulation state                                                    */
    /* ------------------------------------------------------------------ */
    static Dictionary<string, List<double[]>> bikeRoutes            = new();
    static Dictionary<string, int>            bikeRoutePositions    = new();
    static Dictionary<string, bool>           bikeRouteCompleted    = new();
    static Dictionary<string, bool>           bikeIsActive          = new();
    static Dictionary<string, double[]>       bikeLastPosition      = new();
    static Dictionary<string, string>         bikeCurrentStation    = new();
    static Dictionary<string, string>         bikeDestinationStation= new();
    static Dictionary<string, DockingStation> dockingStations       = new();

    /* One semaphore per docking‑station so patches are serialized   */
    static readonly ConcurrentDictionary<string, SemaphoreSlim> stationLocks =
        new ConcurrentDictionary<string, SemaphoreSlim>();

    static Random random        = new Random();
    static bool   stopRequested = false;

    /* ------------------------------------------------------------------ */
    /* Static geometry: Porto polygon for random points                   */
    /* ------------------------------------------------------------------ */
    static readonly double[][] portoPolygon =
    {
        new [] { -8.689051, 41.173113 },
        new [] { -8.675078, 41.147266 },
        new [] { -8.633337, 41.147179 },
        new [] { -8.616012, 41.140305 },
        new [] { -8.590393, 41.139868 },
        new [] { -8.572156, 41.153743 },
        new [] { -8.566115, 41.175561 },
        new [] { -8.608054, 41.184668 },
        new [] { -8.689051, 41.173113 }
    };

    /* ------------------------------------------------------------------ */
    /* Domain models                                                       */
    /* ------------------------------------------------------------------ */
    public class DockingStation
    {
        public string Id              { get; set; }
        public double[] Location      { get; set; }
        public int TotalSlots         { get; set; }
        public int OutOfServiceSlots  { get; set; }
        public List<string> ParkedBicycles { get; set; } = new();

        public int  AvailableBikes  => ParkedBicycles.Count;
        public int  FreeSlots       => TotalSlots - OutOfServiceSlots - ParkedBicycles.Count;
        public bool HasAvailableSlots => FreeSlots > 0;
        public bool HasAvailableBikes => ParkedBicycles.Count > 0;
    }

    /* ================================================================== */
    /* Entry point                                                        */
    /* ================================================================== */
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

        var bicycleIds = Enumerable.Range(startId, endId - startId + 1)
                                   .Select(i => $"urn:ngsi-ld:Vehicle:{i:D3}")
                                   .ToList();

        /* Load stations & bikes already parked */
        await InitializeDockingStations();

        /* Allow user to stop simulation */
        ThreadPool.QueueUserWorkItem(_ =>
        {
            Console.WriteLine("\nPress Enter at any time to stop the simulation…");
            Console.ReadLine();
            stopRequested = true;
            Console.WriteLine("Stop requested – parking all bicycles and terminating.");
        });

        await RunContinuousSimulation(bicycleIds, interval, activePercentage);

        Console.WriteLine("Simulation stopped. Press Enter to exit.");
        Console.ReadLine();
    }

    /* ================================================================== */
    /* Initialisation helpers                                             */
    /* ================================================================== */

    static async Task InitializeDockingStations()
    {
        Console.WriteLine("Fetching existing docking stations from Context Broker…");

        string stationsUrl =
            "http://57.128.119.16:1027/ngsi-ld/v1/entities?type=https://smartdatamodels.org/dataModel.Transportation/BikeHireDockingStation";

        HttpResponseMessage response = await client.GetAsync(stationsUrl);
        if (!response.IsSuccessStatusCode)
            throw new Exception($"Failed to fetch stations: {response.StatusCode}");

        string payload = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(payload);

        foreach (JsonElement element in doc.RootElement.EnumerateArray())
        {
            string id = element.GetProperty("id").GetString();

            double[] loc = element.GetProperty("location")
                                  .GetProperty("value")
                                  .GetProperty("coordinates")
                                  .EnumerateArray()
                                  .Select(c => c.GetDouble())
                                  .ToArray();

            int totalSlots        = GetPropertyValue(element,
                "https://smartdatamodels.org/dataModel.Transportation/totalSlotNumber");
            int outOfServiceSlots = GetPropertyValue(element,
                "https://smartdatamodels.org/dataModel.Transportation/outOfServiceSlotNumber");

            var station = new DockingStation
            {
                Id = id,
                Location = loc,
                TotalSlots = totalSlots,
                OutOfServiceSlots = outOfServiceSlots
            };

            if (element.TryGetProperty("refVehicle", out JsonElement refV) &&
                refV.TryGetProperty("value", out JsonElement vehs))
            {
                foreach (var v in vehs.EnumerateArray())
                {
                    string bikeId = v.GetString();
                    station.ParkedBicycles.Add(bikeId);

                    bikeCurrentStation[bikeId] = id;
                    bikeLastPosition [bikeId] = loc;
                }
            }

            dockingStations[id] = station;
            Console.WriteLine($"Loaded {id}: {station.AvailableBikes} bikes, {station.FreeSlots} free slots");
        }

        Console.WriteLine($"Loaded {dockingStations.Count} docking stations.\n");
    }

    static int GetPropertyValue(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var prop) &&
               prop.TryGetProperty("value", out var value)
             ? value.GetInt32()
             : 0;
    }

    /* ================================================================== */
    /* Main simulation loop                                               */
    /* ================================================================== */

    static async Task RunContinuousSimulation(List<string> bikes, int interval, int activePercentage)
    {
        await InitializeBicycles(bikes);
        await ActivateInitialBicycles(bikes, activePercentage);

        int cycle = 0;
        while (!stopRequested)
        {
            cycle++;
            Console.WriteLine($"\n======= CYCLE {cycle} =======");

            await MoveBicycles(bikes, interval);
            if (!stopRequested)
                await HandleBicycleRotation(bikes, activePercentage);
        }

        await ParkAllBicycles(bikes);
    }

    /* ================================================================== */
    /* Bicycle initialisation                                             */
    /* ================================================================== */

    static async Task InitializeBicycles(List<string> bikes)
    {
        Console.WriteLine("Initialising bicycles…");

        var parked   = bikes.Where(b => bikeCurrentStation.ContainsKey(b)).ToList();
        var unparked = bikes.Except(parked).ToList();

        Console.WriteLine($"Found {parked.Count} already parked, {unparked.Count} to assign.");

        foreach (var bikeId in unparked)
        {
            var station = dockingStations.Values
                                         .Where(s => s.HasAvailableSlots)
                                         .OrderBy(_ => random.Next())
                                         .FirstOrDefault()
                          ?? dockingStations.Values
                             .OrderByDescending(s => s.FreeSlots)
                             .First();

            await ParkBicycleAtStation(bikeId, station.Id);
            bikeIsActive[bikeId] = false;
            Console.WriteLine($"🅿️  Assigned {bikeId} to station {station.Id}");
        }
    }

    static async Task ActivateInitialBicycles(List<string> bikes, int pct)
    {
        int n = Math.Max(1, bikes.Count * pct / 100);
        var toActivate = bikes.OrderBy(_ => random.Next()).Take(n);

        Console.WriteLine($"Activating {n} bicycles…");

        foreach (var bike in toActivate)
        {
            if (!bikeCurrentStation.ContainsKey(bike)) continue;

            bikeIsActive[bike] = true;
            await GenerateNewRoute(bike);
        }
    }

    /* ================================================================== */
    /* Route generation & movement                                        */
    /* ================================================================== */

    static async Task GenerateNewRoute(string bikeId)
    {
        if (!bikeCurrentStation.TryGetValue(bikeId, out string startStationId))
        {
            Console.WriteLine($"❌ {bikeId} not at a station – can’t generate route.");
            return;
        }

        var startStation = dockingStations[startStationId];
        double[] start   = startStation.Location;

        await RemoveBicycleFromStation(bikeId, startStationId);

        var destinations = dockingStations.Values
            .Where(s => s.Id != startStationId && s.HasAvailableSlots)
            .ToList();

        if (destinations.Count == 0)
            destinations = dockingStations.Values.Where(s => s.Id != startStationId).ToList();

        var destStation = destinations.OrderBy(_ => random.Next()).First();
        double[] end    = destStation.Location;

        bikeDestinationStation[bikeId] = destStation.Id;

        try
        {
            var route = await GetCyclingRoute(start, end);
            bikeRoutes      [bikeId] = route;
            bikeRoutePositions[bikeId] = 0;
            bikeRouteCompleted[bikeId] = false;

            Console.WriteLine($"Route for {bikeId}: {route.Count} points, {startStationId} → {destStation.Id}");
        }
        catch
        {
            /* fallback – straight line */
            var direct = new List<double[]>();
            int steps  = 10 + random.Next(20);
            for (int i = 0; i <= steps; i++)
            {
                double f   = (double)i / steps;
                double lon = start[0] + f * (end[0] - start[0]);
                double lat = start[1] + f * (end[1] - start[1]);
                direct.Add(new[] { lon, lat });
            }

            bikeRoutes      [bikeId] = direct;
            bikeRoutePositions[bikeId] = 0;
            bikeRouteCompleted[bikeId] = false;

            Console.WriteLine($"Fallback route for {bikeId}: {direct.Count} points.");
        }
    }

    static async Task MoveBicycles(List<string> bikes, int interval)
    {
        foreach (var bike in bikes)
        {
            if (!bikeIsActive.GetValueOrDefault(bike)) continue;

            if (!bikeRoutes.TryGetValue(bike, out var route))
            {
                Console.WriteLine($"⚠️ {bike} has no route – regenerating.");
                await GenerateNewRoute(bike);
                continue;
            }

            int pos   = bikeRoutePositions[bike];
            if (pos >= route.Count)
            {
                bikeRouteCompleted[bike] = true;
                Console.WriteLine($"🏁 {bike} completed its route.");
                continue;
            }

            double[] nextPoint = route[pos];
            bikeLastPosition[bike] = nextPoint;

            await UpdateBicycleStatus(bike, nextPoint, "onRoute");
            bikeRoutePositions[bike] = pos + 1;
        }

        if (!stopRequested)
        {
            Console.WriteLine($"Sleeping {interval / 1000}s…");
            await Task.Delay(interval);
        }
    }

    static async Task HandleBicycleRotation(List<string> bikes, int pct)
    {
        var completed = bikes
            .Where(b => bikeRouteCompleted.GetValueOrDefault(b) && bikeIsActive.GetValueOrDefault(b))
            .ToList();

        foreach (var bike in completed)
        {
            string dest = bikeDestinationStation[bike];
            await ParkBicycleAtStation(bike, dest);
            bikeIsActive[bike] = false;
        }

        int targetActive = Math.Max(1, bikes.Count * pct / 100);
        int currentActive = bikes.Count(b => bikeIsActive.GetValueOrDefault(b));

        if (currentActive < targetActive)
        {
            var candidates = bikes
                .Where(b => !bikeIsActive.GetValueOrDefault(b) && bikeCurrentStation.ContainsKey(b))
                .OrderBy(_ => random.Next())
                .Take(targetActive - currentActive);

            foreach (var bike in candidates)
            {
                bikeIsActive[bike] = true;
                await GenerateNewRoute(bike);
            }
        }
    }

    /* ================================================================== */
    /* Park / remove helpers – now thread‑safe                             */
    /* ================================================================== */

    static async Task ParkBicycleAtStation(string bikeId, string stationId)
    {
        var station = dockingStations[stationId];
        var sem     = stationLocks.GetOrAdd(stationId, _ => new SemaphoreSlim(1, 1));

        await sem.WaitAsync();
        try
        {
            if (!station.ParkedBicycles.Contains(bikeId))
                station.ParkedBicycles.Add(bikeId);

            bikeCurrentStation[bikeId] = stationId;
            bikeLastPosition [bikeId] = station.Location;
        }
        finally { sem.Release(); }

        await UpdateBicycleStatus(bikeId, station.Location, "parked");
        await UpdateDockingStationStatus(stationId);
    }

    static async Task RemoveBicycleFromStation(string bikeId, string stationId)
    {
        var station = dockingStations[stationId];
        var sem     = stationLocks.GetOrAdd(stationId, _ => new SemaphoreSlim(1, 1));

        await sem.WaitAsync();
        try
        {
            station.ParkedBicycles.Remove(bikeId);
            bikeCurrentStation.Remove(bikeId);
        }
        finally { sem.Release(); }

        await UpdateDockingStationStatus(stationId);
    }

    /* ================================================================== */
    /* PATCH helpers – concurrency‑safe & retry                            */
    /* ================================================================== */

    static async Task<bool> PatchWithRetry(string url, HttpContent content, int max = 3)
    {
        for (int attempt = 1; attempt <= max; attempt++)
        {
            try
            {
                HttpResponseMessage resp = await client.PatchAsync(url, content);
                if (resp.IsSuccessStatusCode) return true;

                Console.WriteLine($"⚠️ PATCH {url} failed ({resp.StatusCode}) " +
                                  $"attempt {attempt}/{max}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ PATCH {url} exception {ex.Message} " +
                                  $"attempt {attempt}/{max}");
            }
            await Task.Delay(200 * attempt);   // back‑off
        }
        return false;
    }

    static async Task UpdateDockingStationStatus(string stationId)
    {
        var station = dockingStations[stationId];
        var sem     = stationLocks.GetOrAdd(stationId, _ => new SemaphoreSlim(1, 1));

        await sem.WaitAsync();
        try
        {
            var data = new
            {
                availableBikeNumber = new { type = "Property", value = station.AvailableBikes },
                freeSlotNumber      = new { type = "Property", value = station.FreeSlots },
                outOfServiceSlotNumber = new { type = "Property", value = station.OutOfServiceSlots },
                refVehicle          = new { type = "Property", value = station.ParkedBicycles.ToArray() }
            };

            string json  = JsonSerializer.Serialize(data);
            var content  = new StringContent(json, Encoding.UTF8, "application/json");
            content.Headers.Add("Link",
                "<https://raw.githubusercontent.com/smart-data-models/dataModel.Transportation/master/context.jsonld>; " +
                "rel=\"http://www.w3.org/ns/json-ld#context\"; type=\"application/ld+json\"");

            bool ok = await PatchWithRetry(orionUrl + stationId + "/attrs", content);
            if (ok)
                Console.WriteLine($"📊 Updated {stationId}: {station.AvailableBikes} bikes, {station.FreeSlots} slots");
            else
                Console.WriteLine($"❌ Gave up updating {stationId} after retries");
        }
        finally { sem.Release(); }
    }

    static async Task UpdateBicycleStatus(string bikeId, double[] pos, string status)
    {
        var data = new
        {
            location = new
            {
                type  = "GeoProperty",
                value = new { type = "Point", coordinates = new[] { pos[0], pos[1] } }
            },
            serviceStatus = new { type = "Property", value = status }
        };

        string json = JsonSerializer.Serialize(data);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        content.Headers.Add("Link",
            "<https://raw.githubusercontent.com/smart-data-models/dataModel.Transportation/master/context.jsonld>; " +
            "rel=\"http://www.w3.org/ns/json-ld#context\"; type=\"application/ld+json\"");

        if (!await PatchWithRetry(orionUrl + bikeId + "/attrs", content, 2))
            Console.WriteLine($"❌ Failed to update bike {bikeId}");
    }

    /* ================================================================== */
    /* External route API helper                                          */
    /* ================================================================== */

    static async Task<List<double[]>> GetCyclingRoute(double[] start, double[] end)
    {
        var body = new { coordinates = new[] { start, end } };
        var json = JsonSerializer.Serialize(body);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var rc = new HttpClient();
        rc.DefaultRequestHeaders.Add("Authorization", openRouteServiceApiKey);

        HttpResponseMessage resp = await rc.PostAsync(openRouteServiceUrl, content);
        string payload = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"ORS {resp.StatusCode}: {payload}");

        using JsonDocument doc = JsonDocument.Parse(payload);
        var coords = doc.RootElement.GetProperty("features")[0]
                                    .GetProperty("geometry")
                                    .GetProperty("coordinates");

        return coords.EnumerateArray()
                     .Select(c => new[] { c[0].GetDouble(), c[1].GetDouble() })
                     .ToList();
    }

    /* ================================================================== */
    /* Geometry helpers                                                   */
    /* ================================================================== */

    static double[] GenerateRandomPointInsidePolygon(double[][] poly, Random rnd)
    {
        double minLat = poly.Min(p => p[1]), maxLat = poly.Max(p => p[1]);
        double minLon = poly.Min(p => p[0]), maxLon = poly.Max(p => p[0]);

        double lat, lon;
        do
        {
            lat = minLat + rnd.NextDouble() * (maxLat - minLat);
            lon = minLon + rnd.NextDouble() * (maxLon - minLon);
        } while (!IsPointInsidePolygon(poly, lat, lon));

        return new[] { lon, lat };
    }

    static bool IsPointInsidePolygon(double[][] poly, double lat, double lon)
    {
        bool inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
        {
            bool intersect = (poly[i][1] > lat) != (poly[j][1] > lat) &&
                             lon < (poly[j][0] - poly[i][0]) *
                                   (lat - poly[i][1]) /
                                   (poly[j][1] - poly[i][1]) + poly[i][0];
            if (intersect) inside = !inside;
        }
        return inside;
    }
}
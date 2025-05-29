using MongoDB.Driver;
using OrionApiDotNet.Models;

namespace OrionApiDotNet.Services
{
    public class MongoService
    {
        private readonly IMongoCollection<Bicycle> _bicycles;
        private readonly IMongoCollection<BicycleSimples> _bicyclesS;

        public MongoService()
        {
            var client = new MongoClient("mongodb://mongo-db:27017");
            var database = client.GetDatabase("Bicicletas");
            _bicycles = database.GetCollection<Bicycle>("bicycles");
            _bicyclesS = database.GetCollection<BicycleSimples>("bicycles");

        }

        public void SaveBicycle(Bicycle bicycle)
        {
            if (!string.IsNullOrEmpty(bicycle.id))
            {
                _bicycles.InsertOne(bicycle);
            }
        }

        public void UpdateBicycle(BicycleSimples bicycle)
        {
            var filter = Builders<BicycleSimples>.Filter.Eq(b => b.id, bicycle.id);
            _bicyclesS.ReplaceOne(filter, bicycle, new ReplaceOptions { IsUpsert = true });
        }

        public void DeleteBicycle(string id)
        {
            var filter = Builders<Bicycle>.Filter.Eq(b => b.id, id);
            _bicycles.DeleteOne(filter);
        }

    }
}
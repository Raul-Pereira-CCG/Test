var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
var app = builder.Build();

app.MapControllers();
app.Run("http://0.0.0.0:5001");
builder.Configuration.AddJsonFile("appsettings.json");

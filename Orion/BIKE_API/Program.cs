var builder = WebApplication.CreateBuilder(args);

// Remover HTTPS redirection (caso exista)
builder.Services.AddControllers();
var app = builder.Build();

app.MapControllers();
app.Run("http://0.0.0.0:5000"); // <- Força apenas HTTP

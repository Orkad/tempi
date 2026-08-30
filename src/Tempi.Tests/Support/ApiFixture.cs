using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Tempi.Configuration;
using Tempi.Hosting;
using Tempi.Sensors;
using Tempi.Storage;

namespace Tempi.Tests.Support;

/// <summary>
/// Un vrai serveur Kestrel sur un port attribué par le noyau.
/// </summary>
/// <remarks>
/// Pas de <c>WebApplicationFactory</c> : elle passe par un serveur en mémoire, sans
/// socket ni sérialisation HTTP réelle, et exigerait de référencer le point d'entrée
/// de l'exécutable. Le test Python démarre délibérément un vrai serveur sur un vrai
/// port et vérifie jusqu'aux en-têtes ; on garde ce choix, que rend possible le fait
/// que la composition de l'hôte vive dans le projet applicatif.
/// </remarks>
public sealed class ApiFixture : IAsyncLifetime
{
    private string _directory = null!;
    private WebApplication _app = null!;

    public HttpClient Client { get; private set; } = null!;

    public TempiStorage Storage { get; private set; } = null!;

    public long Now { get; private set; }

    public async ValueTask InitializeAsync()
    {
        _directory = Directory.CreateTempSubdirectory("tempi-api-").FullName;

        var config = new TempiConfig
        {
            DbPath = Path.Combine(_directory, "tempi.db"),
            W1Dir = "/nonexistent",
            Host = "127.0.0.1",
            Port = 0,   // le noyau choisit, comme le port 0 du test Python
        };

        Storage = new TempiStorage(config.DbPath);
        Now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Seed();

        _app = TempiHost.BuildWeb(config, Storage);
        await _app.StartAsync();

        Client = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        Storage.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private void Seed()
    {
        var readings = new List<Reading>();
        for (var i = 0; i < 60; i++)
        {
            readings.Add(new Reading("28-aaaa", 20.0 + (i * 0.1), Now - (i * 60)));
            readings.Add(new Reading("28-bbbb", 5.0, Now - (i * 60)));
        }

        Storage.Record(readings);
        Storage.SetLabel("28-aaaa", "Salon");
    }
}

[CollectionDefinition(nameof(ApiCollection))]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>;


using System.Net;
using System.Text;
using Kommander;
using Kommander.Communication;
using Kommander.Discovery;
using Kommander.Services;
using Kommander.Time;
using Kommander.WAL;

using Nixie;

try
{
    RaftConfiguration config = new()
    {
        Host = Dns.GetHostAddresses(Environment.GetEnvironmentVariable("PEER_HOST") ?? "localhost")[0].ToString(),
        Port = int.Parse(Environment.GetEnvironmentVariable("PEER_PORT") ?? "8004"),
        MaxPartitions = 3
    };

    List<RaftNode> nodes =
    [
        new(Dns.GetHostAddresses("node1")[0] + ":8081"),
        new(Dns.GetHostAddresses("node2")[0] + ":8082"),
        new(Dns.GetHostAddresses("node3")[0] + ":8083")
    ];

    nodes.RemoveAll(x => x.Endpoint == config.Host + ":" + config.Port);

    if (nodes.Count != 2)
        throw new RaftException("Invalid number of nodes");

    Console.WriteLine("Kommander! {0} {1}", config.Host, config.Port);

    WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

    builder.Services.AddSingleton<ActorSystem>(services =>
        new(services, services.GetRequiredService<ILogger<IRaft>>()));

    builder.Services.AddSingleton<IRaft>(services =>
    {
        RaftManager node = new RaftManager(
            services.GetRequiredService<ActorSystem>(),
            config,
            new StaticDiscovery(nodes),
            new SqliteWAL(path: "/app/data", version: "v3"),
            new HttpCommunication(),
            new HybridLogicalClock(),
            services.GetRequiredService<ILogger<IRaft>>()
        );

        node.OnReplicationError += log =>
        {
            Console.WriteLine("Replication error: {0} #{1}", log.LogType, log.Id);
        };

        node.OnReplicationReceived += (string logType, byte[] logData) =>
        {
            Console.WriteLine("Replication received: {0} {1}", logType, Encoding.UTF8.GetString(logData));
            
            return Task.FromResult(true);
        };

        return node;
    });

    builder.Services.AddHostedService<ReplicationService>();

    WebApplication app = builder.Build();

    app.MapRaftRoutes();

    app.MapGet("/", () => "Kommander Raft Node");

    app.Run();
}
catch (Exception ex)
{
    Console.WriteLine("{0}\n{1}", ex.Message, ex.StackTrace);
}
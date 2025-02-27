# 🔱 Kommander (Raft Consensus)

Kommander is an open-source, distributed consensus library implemented in C# for the .NET platform. It leverages the Raft algorithm to provide a robust and reliable mechanism for leader election, log replication, and data consistency across clusters. Kommander is designed to be flexible and resilient, supporting multiple discovery mechanisms and communication protocols to suit various distributed system architectures.

[![NuGet](https://img.shields.io/nuget/v/Kommander.svg?style=flat-square)](https://www.nuget.org/packages/Kommander)
[![Nuget](https://img.shields.io/nuget/dt/Kommander)](https://www.nuget.org/packages/Kommander)

**This is an alpha project please don't use it in production.**

---

## Features

- **Raft Consensus Algorithm:**
  Implemented using the Raft protocol, which enables a cluster of nodes to maintain a replicated state machine by keeping a synchronized log across all nodes. For an in-depth explanation of Raft, see [In Search of an Understandable Consensus Algorithm](https://raft.github.io/raft.pdf) by Diego Ongaro and John Ousterhout.

- **Distributed Cluster Discovery:**
  Discover other nodes in the cluster using either:
  - **Registries:** Centralized or decentralized service registries.
  - **Multicast Discovery:** Automatic node discovery via multicast messages.
  - **Static Discovery:** Manually configured list of known nodes.

- **Persistent Log Replication:**
  Each node persists its log to disk to ensure data durability. Kommander utilizes a Write-Ahead Log (WAL) internally to safeguard against data loss.

- **Flexible Role Management:**
  Nodes can serve as leaders and followers simultaneously across different partitions, enabling granular control over cluster responsibilities.

- **Multiple Communication Protocols:**
  Achieve consensus and data replication over:
  - **HTTP/2:** For RESTful interactions and easier integration with web services.
  - **gRPC:** For low-latency and high-throughput scenarios.

---

## About Raft and Kommander

Raft is a consensus protocol that helps a cluster of nodes maintain a replicated state machine by synchronizing a replicated log. This log ensures that each node's state remains consistent across the cluster. Kommander implements the core Raft algorithm, providing a minimalistic design that focuses solely on the essential components of the protocol. By separating storage, messaging serialization, and network transport from the consensus logic, Kommander offers flexibility, determinism, and improved performance.

---

## Getting Started

### Prerequisites

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) or higher
- A C# development environment (e.g., Visual Studio, VS Code)

### Installation

To install Kommander into your C#/.NET project, you can use the .NET CLI or the NuGet Package Manager.

#### Using .NET CLI

```shell
dotnet add package Kommander --version 0.1.8
```

### Using NuGet Package Manager

Search for Kommander and install it from the NuGet package manager UI, or use the Package Manager Console:

```shell
Install-Package Kommander -Version 0.1.8
```

Or, using the NuGet Package Manager in Visual Studio, search for **Kommander** and install it.

---

## Usage

Below is a basic example demonstrating how to set up a simple Kommander node, join a cluster, and start the consensus process.

```csharp

// Identify the node configuration, including the host, port, and the maximum number of partitions.
RaftConfiguration config = new()
{
    Host = "localhost",
    Port = 8001,
    MaxPartitions = 1
};

// Create a Raft node with the specified configuration.
// - The node will use a static discovery mechanism to find other nodes in the cluster.
// - The node will use a SQLite Write-Ahead Log (WAL) for log persistence.
// - The node will use HTTP for communication with other nodes.
IRaft node = new RaftManager(
    new ActorSystem(), 
    config, 
    new StaticDiscovery([new("localhost:8002"), new("localhost:8003")]),
    new SqliteWAL(path: "./data", version: "v1"),
    new HTTPCommunication()
    new HybridLogicalClock(),
    logger
);

// Subscribe to the OnReplicationReceived event to receive log entries from other nodes
// if the node is a follower
node.OnReplicationReceived += (string logType, byte[] logData) =>
{
    Console.WriteLine("Replication received: {0} {1}", logType, Encoding.UTF8.GetString(logData));
    
    return Task.FromResult(true);
};

// Start the node and join the cluster.
await node.JoinCluster();

// Check if the node is the leader of partition 0 and replicate a log entry.
if (await node.AmILeader(0))
    await node.ReplicateLogs(0, "Kommander is awesome!");

```

### Advanced Configuration

Kommander supports advanced configurations including:

- **Custom Log Storage:** Implement your own storage engine by extending the log replication modules.
- **Dynamic Partitioning:** Configure nodes to handle multiple partitions with distinct leader election processes.
- **Security:** Integrate with your existing security framework to secure HTTP/TCP communications.

For detailed configuration options, please refer to the [Documentation](docs/CONFIGURATION.md).

---

## Contributing

We welcome contributions to Kommander! If you’d like to contribute, please follow these steps:

1. Fork the repository.
2. Create a new branch for your feature or bug fix.
3. Write tests and ensure all tests pass.
4. Submit a pull request with a clear description of your changes.

For more details, see our [CONTRIBUTING.md](CONTRIBUTING.md).

---

## License

Kommander is licensed under the MIT License. See the [LICENSE](LICENSE) file for details.

---

## Community & Support

- **GitHub Issues:** Report bugs or request features via our [GitHub Issues](https://github.com/your-repo/Kommander/issues) page.

Harness the power of distributed consensus with Kommander and build resilient, high-availability systems on the .NET platform. Happy coding!